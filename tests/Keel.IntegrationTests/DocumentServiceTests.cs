using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Marking;
using Keel.Documents.Vault;
using Npgsql;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DocumentServiceTests(PostgresFixture pg)
{
    internal static Subject User(string name, bool usPerson = true, string[]? roles = null, string[]? programs = null, string org = "ENG") => new()
    {
        Id = "sub-" + name,
        Username = name,
        DisplayName = name,
        UsPerson = usPerson,
        Org = org,
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = ImmutableSortedSet.Create(StringComparer.Ordinal, "EXPT", "CTI"),
        Roles = (roles ?? [Roles.Viewer, Roles.Author]).ToImmutableSortedSet(StringComparer.Ordinal),
    };

    internal static readonly Subject Alice = User("alice");
    internal static readonly Subject Bob = User("bob");
    internal static readonly Subject Lena = User("lena", usPerson: false);
    internal static readonly Subject Ada = User("ada", roles: [Roles.Admin, Roles.Viewer]);

    internal static Markings Itar(string program = "ORCA") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;

    internal static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static CreateDocumentCommand Cmd(string name, Markings? m = null, string type = "Specification") => new(name, type, m ?? Itar(), "Synthetic test document");

    private static FileUpload File(string name, byte[] bytes, string? mediaType = null) => new(name, mediaType, new MemoryStream(bytes));

    private static byte[] Unique(string text) => Encoding.UTF8.GetBytes($"{text}\n{Guid.NewGuid()}\n");

    internal static byte[] SamplePdf(int pages)
    {
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = new PdfDocument();
        var font = new XFont(EmbeddedFontResolver.FamilyName, 12);
        for (var i = 1; i <= pages; i++)
        {
            using var gfx = XGraphics.FromPdfPage(doc.AddPage());
            gfx.DrawString($"Synthetic drawing sheet {i} of {pages} ({Guid.NewGuid()})", font, XBrushes.Black, new XPoint(72, 72));
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private sealed record Env(DocumentService Docs, Keel.Parts.PartService Parts, TestDatabase Db, NpgsqlDataSource Ds, VaultOptions Vault);

    private async Task<Env> Setup()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var vaultOptions = await VaultContainer.OptionsAsync();
        var auditLog = new PostgresAuditLog(ds, TimeProvider.System);
        return new Env(new DocumentService(ds, auditLog, TimeProvider.System, new ObjectVault(vaultOptions)),
            new Keel.Parts.PartService(ds, auditLog, TimeProvider.System), db, ds, vaultOptions);
    }

    private static async Task<List<AuditRecord>> Audit(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    private static async Task<byte[]> Bytes(MarkedDownload d)
    {
        using var ms = new MemoryStream();
        await d.WriteToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- create / files / check-in

    [Fact]
    public async Task Create_with_and_without_a_primary_file()
    {
        var env = await Setup();
        var bare = (await env.Docs.CreateAsync(Alice, Cmd("Bracket specification"), null, "corr-1")).Value!;
        Assert.Equal(("D-000001", "A.1", "InWork"), (bare.Number, bare.Iteration.Label, bare.Revision.State));
        Assert.Empty(bare.Iteration.Files);
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", bare.Banner);
        // Submitting needs content.
        Assert.False(bare.Transitions.Single().Allowed);
        Assert.Contains("primary file", (await env.Docs.SetStateAsync(Alice, "D-000001", "UnderReview", null, false, null)).Errors[0]);

        var bytes = Unique("specification body");
        var withFile = (await env.Docs.CreateAsync(Alice, Cmd("Bracket drawing", type: "Drawing"), File("bracket.txt", bytes, "Text/Plain; charset=utf-8"), null)).Value!;
        var f = Assert.Single(withFile.Iteration.Files);
        Assert.Equal(("primary", "bracket.txt", "text/plain", (long)bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
            (f.Role, f.FileName, f.MediaType, f.SizeBytes, f.Sha256));

        var created = (await Audit(env.Ds)).Where(e => e.Action == "document.create").ToList();
        Assert.Equal(2, created.Count);
        Assert.Equal(("D-000001", "corr-1"), (created[0].ObjectId, created[0].CorrelationId));
        Assert.Contains(f.Sha256, created[1].Payload);
    }

    [Fact]
    public async Task Create_validates_and_denies()
    {
        var env = await Setup();
        var invalid = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("", "Memo", Itar()), File("../etc/passwd", [1]), null);
        Assert.Equal(OutcomeKind.Invalid, invalid.Kind);
        Assert.Equal(3, invalid.Errors.Count);

        var denied = await env.Docs.CreateAsync(Lena with { Roles = [Roles.Author] }, Cmd("Restricted"), File("x.txt", Unique("x")), null);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains("export.us_person_or_authorization_required", denied.Errors);
        Assert.Equal(["number.explicit_requires_admin"], (await env.Docs.CreateAsync(Alice, Cmd("Migrated") with { Number = "DWG-1" }, null, null)).Errors);
        Assert.All(await Audit(env.Ds), e => Assert.Equal(AuditOutcome.Denied, e.Outcome));
    }

    [Fact]
    public async Task Checkout_upload_checkin_builds_iteration_history()
    {
        var env = await Setup();
        var v1 = Unique("drawing v1");
        await env.Docs.CreateAsync(Alice, Cmd("Housing drawing", type: "Drawing"), File("housing.dwg", v1), null);

        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.UploadFileAsync(Alice, "D-000001", "attachment", File("notes.txt", Unique("n")), null)).Kind);
        var co = (await env.Docs.CheckOutAsync(Alice, "D-000001", null)).Value!;
        Assert.Equal(["housing.dwg"], co.Checkout!.Files!.Select(f => f.FileName));
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.CheckOutAsync(Bob, "D-000001", null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.UploadFileAsync(Bob, "D-000001", "attachment", File("bob.txt", Unique("b")), null)).Kind);

        var v2 = Unique("drawing v2");
        await env.Docs.UploadFileAsync(Alice, "D-000001", "attachment", File("Stress analysis.xlsx", Unique("calc")), null);
        Assert.Contains("already has a file named", (await env.Docs.UploadFileAsync(Alice, "D-000001", "attachment", File("STRESS ANALYSIS.XLSX", Unique("dup")), null)).Errors[0]);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.UploadFileAsync(Alice, "D-000001", "attachment", File("bad\u0007name", Unique("c")), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.UploadFileAsync(Alice, "D-000001", "cover", File("c.txt", Unique("c")), null)).Kind);
        var replaced = (await env.Docs.UploadFileAsync(Alice, "D-000001", "primary", File("housing-r2.dwg", v2), null)).Value!;
        Assert.Equal(["housing-r2.dwg", "Stress analysis.xlsx"], replaced.Checkout!.Files!.Select(f => f.FileName));
        Assert.Equal(["housing.dwg"], replaced.Iteration.Files.Select(f => f.FileName)); // the vaulted iteration is untouched until check-in
        await env.Docs.UpdateWorkingCopyAsync(Alice, "D-000001", "Housing drawing, wall thickened", null);

        // Bob sees that it is checked out, but not the private working copy.
        var bobView = (await env.Docs.GetAsync(Bob, "D-000001", null, null)).Value!;
        Assert.Equal(("alice", false, null, null), (bobView.Checkout!.UserName, bobView.Checkout.IsMine, bobView.Checkout.Description, bobView.Checkout.Files));

        var ci = (await env.Docs.CheckInAsync(Alice, "D-000001", "Wall thickened", null)).Value!;
        Assert.Null(ci.Checkout);
        Assert.Equal(("A.2", "Housing drawing, wall thickened", "Wall thickened"), (ci.Iteration.Label, ci.Iteration.Description, ci.Iteration.Comment));
        Assert.Equal(["housing-r2.dwg", "Stress analysis.xlsx"], ci.Iteration.Files.Select(f => f.FileName));
        Assert.Equal(["A.2", "A.1"], ci.History.Select(h => h.Label));
        Assert.Equal(["housing.dwg"], ci.History[1].Files.Select(f => f.FileName));

        // Once there is a primary file, a check-in cannot drop it.
        var co2 = (await env.Docs.CheckOutAsync(Alice, "D-000001", null)).Value!;
        var primary = co2.Checkout!.Files!.Single(f => f.Role == "primary");
        Assert.Equal(["Stress analysis.xlsx"], (await env.Docs.RemoveFileAsync(Alice, "D-000001", primary.Id, null)).Value!.Checkout!.Files!.Select(f => f.FileName));
        Assert.Contains("primary file", (await env.Docs.CheckInAsync(Alice, "D-000001", null, null)).Errors[0]);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.RemoveFileAsync(Alice, "D-000001", Guid.NewGuid(), null)).Kind);
        Assert.Equal(["checkout.not_owner"], (await env.Docs.UndoCheckOutAsync(Bob, "D-000001", null)).Errors);
        Assert.Equal("A.2", (await env.Docs.UndoCheckOutAsync(Ada, "D-000001", null)).Value!.Iteration.Label);

        var actions = (await Audit(env.Ds)).Where(e => e.Outcome == AuditOutcome.Success).Select(e => e.Action).ToList();
        Assert.Contains("document.file_add", actions);
        Assert.Contains("document.file_remove", actions);
        Assert.Contains("document.checkin", actions);
    }

    [Fact]
    public async Task Identical_bytes_share_one_content_addressed_object()
    {
        var env = await Setup();
        var bytes = Unique("shared specification");
        var a = (await env.Docs.CreateAsync(Alice, Cmd("Spec copy one"), File("spec.txt", bytes), null)).Value!.Iteration.Files.Single();
        var b = (await env.Docs.CreateAsync(Alice, Cmd("Spec copy two"), File("other-name.txt", bytes), null)).Value!.Iteration.Files.Single();
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Id, b.Id);

        using var s3 = ObjectVault.CreateClient(env.Vault);
        var listing = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = env.Vault.Bucket, Prefix = a.Sha256 });
        var obj = Assert.Single(listing.S3Objects!);
        Assert.Equal((a.Sha256, (long?)bytes.Length), (obj.Key, obj.Size));
    }

    // ---------------------------------------------------------------- download

    [Fact]
    public async Task Pdf_download_is_stamped_and_audited()
    {
        var env = await Setup();
        var pdf = SamplePdf(2);
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Housing drawing", type: "Drawing"), File("housing.pdf", pdf, "application/pdf"), "corr-dl")).Value!;
        var file = doc.Iteration.Files.Single();

        var d = (await env.Docs.DownloadAsync(Alice, doc.Number, file.Id, null, null, "corr-dl")).Value!;
        Assert.Equal(("housing.pdf", "application/pdf", "pdf", doc.Banner), (d.FileName, d.ContentType, d.Format, d.Banner));
        using var stamped = PdfReader.Open(new MemoryStream(await Bytes(d)), PdfDocumentOpenMode.Import);
        Assert.Equal(2, stamped.PageCount);
        Assert.Equal(doc.Banner, stamped.Info.Subject);

        var e = (await Audit(env.Ds)).Single(a => a.Action == "document.download");
        Assert.Equal((AuditOutcome.Success, "D-000001", "corr-dl"), (e.Outcome, e.ObjectId, e.CorrelationId));
        Assert.Contains("\"file\":\"housing.pdf\"", e.Payload);
        Assert.Contains($"\"sha256\":\"{file.Sha256}\"", e.Payload);
        Assert.Contains("\"iteration\":\"A.1\"", e.Payload);
        using (var payload = System.Text.Json.JsonDocument.Parse(e.Payload))
            Assert.Equal(doc.Banner, payload.RootElement.GetProperty("details").GetProperty("markings").GetString());

        // Selecting by SHA-256 gives the same file.
        Assert.True((await env.Docs.DownloadAsync(Alice, doc.Number, null, file.Sha256, "A", null)).IsOk);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.DownloadAsync(Alice, doc.Number, Guid.NewGuid(), null, null, null)).Kind);
    }

    [Fact]
    public async Task Other_formats_and_unstampable_pdfs_are_delivered_in_a_marked_zip()
    {
        var env = await Setup();
        var stl = Unique("solid bracket");
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Bracket model", type: "CAD"), File("bracket.stl", stl, "model/stl"), null)).Value!;
        var d = (await env.Docs.DownloadAsync(Alice, doc.Number, doc.Iteration.Files.Single().Id, null, null, null)).Value!;
        Assert.Equal(("bracket.stl.zip", "application/zip", "zip"), (d.FileName, d.ContentType, d.Format));
        using (var zip = new ZipArchive(new MemoryStream(await Bytes(d))))
        {
            Assert.Equal(["MARKINGS.pdf", "MARKINGS.txt", "bracket.stl"], zip.Entries.Select(x => x.FullName));
            using var ms = new MemoryStream();
            await zip.GetEntry("bracket.stl")!.Open().CopyToAsync(ms);
            Assert.Equal(stl, ms.ToArray());
        }

        // Claims to be a PDF but cannot be parsed: still delivered marked, as a ZIP, and the reason is audited.
        var broken = Encoding.ASCII.GetBytes($"%PDF-1.7\nnot really a pdf {Guid.NewGuid()}");
        var bad = (await env.Docs.CreateAsync(Alice, Cmd("Broken drawing"), File("broken.pdf", broken, "application/pdf"), null)).Value!;
        var z = (await env.Docs.DownloadAsync(Alice, bad.Number, bad.Iteration.Files.Single().Id, null, null, null)).Value!;
        Assert.Equal(("broken.pdf.zip", "zip"), (z.FileName, z.Format));
        Assert.Contains("stampError", (await Audit(env.Ds)).Last(a => a.Action == "document.download").Payload);
    }

    [Fact]
    public async Task Large_non_pdf_streams_through_two_verified_passes()
    {
        var env = await Setup();
        var big = RandomNumberGenerator.GetBytes(env.Vault.PartSizeBytes * 2 + 17);
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Mesh"), File("mesh.bin", big), null)).Value!;
        var d = (await env.Docs.DownloadAsync(Alice, doc.Number, doc.Iteration.Files.Single().Id, null, null, null)).Value!;
        using var zip = new ZipArchive(new MemoryStream(await Bytes(d)));
        using var ms = new MemoryStream();
        await zip.GetEntry("mesh.bin")!.Open().CopyToAsync(ms);
        Assert.Equal(big, ms.ToArray());
    }

    [Fact]
    public async Task Tampered_vault_object_fails_download_with_a_clear_error()
    {
        var env = await Setup();
        var original = Unique("procedure step 1");
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Torque procedure", type: "Procedure"), File("torque.txt", original), null)).Value!;
        var file = doc.Iteration.Files.Single();

        // Someone with store access swaps the bytes behind the application's back.
        using var s3 = ObjectVault.CreateClient(env.Vault);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = env.Vault.Bucket, Key = file.Sha256, InputStream = new MemoryStream(Unique("procedure step 1 (altered)")) });

        var e = await Assert.ThrowsAsync<VaultIntegrityException>(() => env.Docs.DownloadAsync(Alice, doc.Number, file.Id, null, null, null));
        Assert.Contains($"Vault object {file.Sha256} failed integrity verification", e.Message);
        var failure = (await Audit(env.Ds)).Single(a => a.Action == "document.download");
        Assert.Equal(AuditOutcome.Failure, failure.Outcome);
        Assert.Contains("integrity", failure.Payload);
    }

    [Fact]
    public async Task Unauthorized_download_is_audited_and_not_found()
    {
        var env = await Setup();
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Seeker drawing"), File("seeker.txt", Unique("seeker")), null)).Value!;
        var fileId = doc.Iteration.Files.Single().Id;

        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.DownloadAsync(Lena, doc.Number, fileId, null, null, "corr-x")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.GetAsync(Lena, doc.Number, null, null)).Kind);
        var denial = (await Audit(env.Ds)).Single(a => a.Action == "document.download");
        Assert.Equal((AuditOutcome.Denied, "corr-x"), (denial.Outcome, denial.CorrelationId));
        Assert.Contains("export.us_person_or_authorization_required", denial.Payload);
        Assert.Contains(fileId.ToString(), denial.Payload);
    }

    // ---------------------------------------------------------------- search, lifecycle, concurrency, DB guards

    [Fact]
    public async Task Search_filters_by_access_type_and_state()
    {
        var env = await Setup();
        await env.Docs.CreateAsync(Alice, Cmd("Radome specification"), null, null);
        await env.Docs.CreateAsync(Alice, Cmd("Radome test report", Open, "Report"), null, null);
        await env.Docs.CreateAsync(Alice, Cmd("Gasket procedure", Open, "Procedure"), null, null);

        var alice = await env.Docs.SearchAsync(Alice, "radome", null, null, 50, 0, null);
        Assert.Equal(["D-000001", "D-000002"], alice.Items.Select(i => i.Number));
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", alice.PageBanner);
        var lena = await env.Docs.SearchAsync(Lena, "radome", null, null, 50, 0, null);
        Assert.Equal(["D-000002"], lena.Items.Select(i => i.Number));
        Assert.Equal(["D-000003"], (await env.Docs.SearchAsync(Alice, "", "Procedure", null, 50, 0, null)).Items.Select(i => i.Number));
        Assert.Equal(3, (await env.Docs.SearchAsync(Alice, "", null, "InWork", 50, 0, null)).Total);
        Assert.Contains("D-000001,D-000002", (await Audit(env.Ds)).First(a => a.Action == "document.search").Payload);
    }

    [Fact]
    public async Task Concurrent_checkouts_admit_exactly_one_user()
    {
        var env = await Setup();
        await env.Docs.CreateAsync(Alice, Cmd("Contended"), File("c.txt", Unique("c")), null);
        var users = Enumerable.Range(0, 12).Select(i => User($"user{i}")).ToList();
        var results = await Task.WhenAll(users.Select(u => Task.Run(() => env.Docs.CheckOutAsync(u, "D-000001", null))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(11, results.Count(r => r.Kind == OutcomeKind.Conflict));
    }

    [Fact]
    public async Task Locked_revisions_are_enforced_by_the_database()
    {
        var env = await Setup();
        await env.Docs.CreateAsync(Alice, Cmd("Spacer drawing"), File("spacer.txt", Unique("spacer")), null);

        var review = (await env.Docs.SetStateAsync(Alice, "D-000001", "UnderReview", null, false, null)).Value!;
        Assert.True(review.Revision.IsLocked);
        Assert.Contains("locked", (await env.Docs.CheckOutAsync(Alice, "D-000001", null)).Errors[0]);
        Assert.Contains("approved review", (await env.Docs.SetStateAsync(Alice, "D-000001", "Released", null, false, null)).Errors[0]);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.SetStateAsync(Alice, "D-000001", "Released", "x", true, null)).Kind);
        Assert.Equal("Released", (await env.Docs.SetStateAsync(Ada, "D-000001", "Released", "Released on paper before cutover", true, null)).Value!.Revision.State);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.ChangeMarkingsAsync(Alice, "D-000001", Open, "x", null)).Kind);

        await using var app = env.Db.AppDataSource();
        async Task Rejects(string sql, string state)
        {
            await using var cmd = app.CreateCommand(sql);
            Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
        await Rejects("INSERT INTO plm.document_iteration (id, revision_id, iteration, created_at, created_by) SELECT gen_random_uuid(), id, 9, now(), 'x' FROM plm.document_revision",
            PostgresErrorCodes.CheckViolation);
        await Rejects("INSERT INTO plm.document_checkout (revision_id, user_id, checked_out_at, description) SELECT id, 'x', now(), '' FROM plm.document_revision",
            PostgresErrorCodes.CheckViolation);
        await Rejects("INSERT INTO plm.document_file (id, iteration_id, role, file_name, media_type, size_bytes, sha256, added_at, added_by) " +
            $"SELECT gen_random_uuid(), id, 'attachment', 'late.txt', 'text/plain', 1, '{new string('a', 64)}', now(), 'x' FROM plm.document_iteration", PostgresErrorCodes.CheckViolation);
        await Rejects("UPDATE plm.document_revision SET latest_iteration = 2", PostgresErrorCodes.CheckViolation);
        await Rejects("UPDATE plm.document_file SET sha256 = repeat('b', 64)", PostgresErrorCodes.InsufficientPrivilege);
        await Rejects("DELETE FROM plm.document_file", PostgresErrorCodes.InsufficientPrivilege);
        await Rejects("UPDATE plm.document_iteration SET description = 'tampered'", PostgresErrorCodes.InsufficientPrivilege);
        await Rejects("DELETE FROM plm.document_master", PostgresErrorCodes.InsufficientPrivilege);
        await Rejects("UPDATE plm.document_master SET number = 'D-999999'", PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Markings_change_requires_reason_and_keeps_actor_access()
    {
        var env = await Setup();
        await env.Docs.CreateAsync(Alice, Cmd("Lens specification"), null, null);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.ChangeMarkingsAsync(Alice, "D-000001", Open, "", null)).Kind);
        Assert.Equal(["new_markings:program.not_member"], (await env.Docs.ChangeMarkingsAsync(Alice, "D-000001", Itar("KRAKEN"), "Transfer", null)).Errors);
        var changed = (await env.Docs.ChangeMarkingsAsync(Alice, "D-000001", Open, "Export office determination EO-12", null)).Value!;
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", changed.Banner);
        Assert.True((await env.Docs.GetAsync(Lena, "D-000001", null, null)).IsOk);
    }
}
