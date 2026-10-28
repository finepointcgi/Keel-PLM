using System.Collections.Immutable;
using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.TestSupport;
using Keel.Viewing;
using Npgsql;
using UglyToad.PdfPig;

namespace Keel.IntegrationTests;

/// <summary>
/// The in-browser viewer's server side against real PostgreSQL, a real vault and the real audit log (increment 6.0, ADR-0027): viewing is authorized
/// like downloading, audited (denials and refusals too), verified against the stored hash, marked like downloads for PDFs, and limited by size and content.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ViewerTests(PostgresFixture pg)
{
    private static readonly Subject Alice = DocumentServiceTests.Alice;
    private static readonly Subject Lena = DocumentServiceTests.Lena;

    private sealed record Env(DocumentService Docs, ViewerService Viewer, NpgsqlDataSource Ds, VaultOptions Vault, TestDatabase Db);

    private async Task<Env> Setup(ViewerOptions? options = null, Action<VaultOptions>? tweakVault = null, params IFileViewGate[] gates)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var vaultOptions = await VaultContainer.OptionsAsync();
        tweakVault?.Invoke(vaultOptions);
        var docs = new DocumentService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System, new ObjectVault(vaultOptions));
        return new Env(docs, new ViewerService(docs, options ?? new ViewerOptions(), gates), ds, vaultOptions, db);
    }

    private static CreateDocumentCommand Cmd(string name, Markings? m = null) => new(name, "Drawing", m ?? DocumentServiceTests.Itar(), "Synthetic test document");

    private static async Task<(string Number, DocumentFileView File)> Create(Env env, string fileName, byte[] bytes, Markings? m = null, string mediaType = "application/octet-stream")
    {
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Viewer test " + fileName, m), new FileUpload(fileName, mediaType, new MemoryStream(bytes)), null)).Value!;
        return (doc.Number, doc.Iteration.Files.Single());
    }

    private static async Task<List<AuditRecord>> Views(NpgsqlDataSource ds) =>
        [.. (await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))).Where(e => e.Action == "document.view")];

    // ---------------------------------------------------------------- authorization: viewing is downloading

    private static Subject With(string name, bool usPerson = true, string[]? roles = null, string[]? programs = null, string[]? cui = null) => new()
    {
        Id = "sub-" + name, Username = name, DisplayName = name, UsPerson = usPerson, Org = "ENG",
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = (cui ?? ["EXPT", "CTI"]).ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = (roles ?? [Roles.Viewer]).ToImmutableSortedSet(StringComparer.Ordinal),
    };

    [Fact]
    public async Task Authorization_matrix_is_identical_to_downloads_and_every_denial_is_audited_as_not_found()
    {
        var env = await Setup();
        var (number, file) = await Create(env, "bracket.stl", ViewerSamples.AsciiStlCube());

        // (subject, expected permitted). The viewer reads the same Read + Download rule as DownloadAsync: today both are granted by the same roles, so
        // "may read but not download" cannot occur (see ADR-0027, VWR-2); every other clause of the read rule denies BOTH with the same not-found.
        var cases = new (Subject Who, bool Permitted, string Reason)[]
        {
            (With("viewer"), true, ""),
            (With("author", roles: [Roles.Author]), true, ""),
            (With("auditor", roles: [Roles.Auditor]), true, ""),
            (With("foreign", usPerson: false), false, "export.us_person_or_authorization_required"),
            (With("otherprogram", programs: ["KRAKEN"]), false, "program.not_member"),
            (With("noprogram", programs: []), false, "program.not_member"),
            (With("nocui", cui: []), false, "cui.category_missing"),
            (With("noroles", roles: []), false, "role.missing"),
        };
        foreach (var (who, permitted, reason) in cases)
        {
            var viewed = await env.Viewer.DescribeAsync(who, number, file.Id, "corr-" + who.Username);
            var downloaded = await env.Docs.DownloadAsync(who, number, file.Id, null, null, "corr-d-" + who.Username);
            Assert.Equal(permitted, viewed.IsOk);
            Assert.Equal(downloaded.IsOk, viewed.IsOk); // viewing is never easier than downloading
            if (permitted) continue;
            Assert.Equal(OutcomeKind.NotFound, viewed.Kind);
            Assert.Equal(OutcomeKind.NotFound, downloaded.Kind);
            var open = await env.Viewer.OpenAsync(who, number, file.Id, "corr-open-" + who.Username);
            Assert.Equal(ViewerContentStatus.NotFound, open.Status);
            Assert.Null(open.Content);
            var denial = (await Views(env.Ds)).First(e => e.CorrelationId == "corr-" + who.Username);
            Assert.Equal(AuditOutcome.Denied, denial.Outcome);
            Assert.Equal(number, denial.ObjectId);
            Assert.Contains(reason, denial.Payload, StringComparison.Ordinal);
            Assert.Contains(file.Id.ToString(), denial.Payload, StringComparison.Ordinal);
        }
        // A document that does not exist answers exactly like one the caller may not read.
        var missing = await env.Viewer.DescribeAsync(With("foreign", usPerson: false), "D-999999", Guid.NewGuid(), null);
        Assert.Equal(OutcomeKind.NotFound, missing.Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Viewer.DescribeAsync(Alice, "D-999999", Guid.NewGuid(), null)).Kind);
        // A file id of another document is not found either.
        var (other, _) = await Create(env, "other.stl", ViewerSamples.AsciiStlCube(8));
        Assert.Equal(OutcomeKind.NotFound, (await env.Viewer.DescribeAsync(Alice, other, file.Id, null)).Kind);
    }

    [Fact]
    public async Task A_licensed_foreign_person_can_view_exactly_what_lists_their_authorization()
    {
        var env = await Setup();
        var listed = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME",
            distributionReason: "Critical technology", program: "ORCA", exportAuthorizations: ["TAA-0042"]).Value;
        var (number, file) = await Create(env, "partner.png", ViewerSamples.Png(), listed);
        var farid = new Subject
        {
            Id = "sub-farid", Username = "farid", DisplayName = "farid", UsPerson = false, Org = "PARTNER",
            Programs = ImmutableSortedSet.Create(StringComparer.Ordinal, "ORCA"), CuiCategories = ImmutableSortedSet.Create(StringComparer.Ordinal, "EXPT"),
            ExportAuthorizations = ImmutableSortedSet.Create(StringComparer.Ordinal, "TAA-0042"), Roles = ImmutableSortedSet.Create(StringComparer.Ordinal, Roles.Viewer),
        };
        Assert.True((await env.Viewer.DescribeAsync(farid, number, file.Id, null)).IsOk);
        Assert.True((await env.Viewer.OpenAsync(farid, number, file.Id, null)).IsOk);
        Assert.False((await env.Viewer.DescribeAsync(Lena, number, file.Id, null)).IsOk);
    }

    // ---------------------------------------------------------------- delivery, audit, detection

    [Fact]
    public async Task A_delivered_view_is_audited_with_file_hash_iteration_banner_and_viewer_type()
    {
        var env = await Setup();
        var stl = ViewerSamples.AsciiStlCube();
        var (number, file) = await Create(env, "bracket.stl", stl);

        var d = (await env.Viewer.DescribeAsync(Alice, number, file.Id, "corr-describe")).Value!;
        Assert.Equal((ViewerStatus.Ready, ViewerTypes.Model3D, "stl"), (d.Status, d.ViewerType, d.Detection.Format.Id));
        Assert.Empty(await Views(env.Ds)); // describing is not a view

        var result = await env.Viewer.OpenAsync(Alice, number, file.Id, "corr-view");
        Assert.True(result.IsOk);
        Assert.Equal(stl, result.Content!.Bytes); // non-PDF files are returned as stored; the viewer draws its overlay
        Assert.False(result.Content.Marked);
        Assert.Equal("model/stl", result.Content.Format.MediaType); // the server's media type, not the stored one

        var e = Assert.Single(await Views(env.Ds));
        Assert.Equal((AuditOutcome.Success, number, "corr-view"), (e.Outcome, e.ObjectId, e.CorrelationId));
        foreach (var expected in new[]
                 {
                     "\"file\":\"bracket.stl\"", $"\"sha256\":\"{file.Sha256}\"", "\"iteration\":\"A.1\"", $"\"banner\":\"{d.File.Banner.Replace("·", "\\u00B7", StringComparison.Ordinal)}\"",
                     "\"viewer\":\"model3d\"", "\"format\":\"stl\"", "\"handling\":\"overlay\"", $"\"fileId\":\"{file.Id}\"",
                 })
            Assert.Contains(expected, e.Payload.Replace("\\u00B7", "\\u00B7", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdfs_are_viewed_through_the_same_marked_pipeline_as_downloads()
    {
        var env = await Setup();
        var pdf = ViewerSamples.Pdf(3, Guid.NewGuid().ToString("N"));
        var (number, file) = await Create(env, "drawing.pdf", pdf, mediaType: "application/pdf");
        var banner = DocumentServiceTests.Itar().BannerText();

        var viewed = (await env.Viewer.OpenAsync(Alice, number, file.Id, "corr-pdf")).Content!;
        Assert.True(viewed.Marked);
        Assert.NotEqual(pdf, viewed.Bytes);
        Assert.Equal("application/pdf", viewed.Format.MediaType);

        var download = (await env.Docs.DownloadAsync(Alice, number, file.Id, null, null, null)).Value!;
        using var ms = new MemoryStream();
        await download.WriteToAsync(ms, CancellationToken.None);

        using var a = PdfDocument.Open(viewed.Bytes);
        using var b = PdfDocument.Open(ms.ToArray());
        Assert.Equal(3, a.NumberOfPages);
        Assert.Equal(b.NumberOfPages, a.NumberOfPages);
        for (var i = 1; i <= 3; i++)
        {
            // Same stamped geometry and the banner on every page, extractable as text: what is on screen is what a download contains.
            Assert.Equal((b.GetPage(i).Width, b.GetPage(i).Height), (a.GetPage(i).Width, a.GetPage(i).Height));
            Assert.Contains(banner, a.GetPage(i).Text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("DISTRIBUTION STATEMENT D", a.GetPage(i).Text, StringComparison.Ordinal);
            Assert.Contains("retrieved", a.GetPage(i).Text, StringComparison.Ordinal);
        }
        Assert.Equal(banner, a.Information.Subject);
        Assert.Contains("\"handling\":\"pdf-stamped\"", (await Views(env.Ds)).Single().Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_that_cannot_be_marked_is_refused_and_never_delivered_unmarked()
    {
        var env = await Setup();
        var broken = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not a pdf body " + Guid.NewGuid() + "\n");
        var (number, file) = await Create(env, "broken.pdf", broken, mediaType: "application/pdf");

        var result = await env.Viewer.OpenAsync(Alice, number, file.Id, "corr-broken");
        Assert.Equal(ViewerContentStatus.Unprocessable, result.Status);
        Assert.Null(result.Content);
        Assert.Contains("cannot be marked", result.Message, StringComparison.Ordinal);
        Assert.Equal(AuditOutcome.Denied, (await Views(env.Ds)).Single().Outcome);

        // Too large for the stamper: refused as well (a download would deliver a marked ZIP instead).
        var big = await Setup(tweakVault: v => v.MaxPdfStampBytes = 100);
        var (n2, f2) = await Create(big, "big.pdf", ViewerSamples.Pdf(1, Guid.NewGuid().ToString("N")), mediaType: "application/pdf");
        var refused = await big.Viewer.OpenAsync(Alice, n2, f2.Id, null);
        Assert.Equal(ViewerContentStatus.Unprocessable, refused.Status);
        Assert.Contains("too large to be marked", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detection_never_trusts_the_name_or_the_stored_media_type()
    {
        var env = await Setup();
        // A PNG named .stl with a lying media type is shown as the PNG it is, and the audit records the mismatch.
        var (n1, png) = await Create(env, "model.stl", ViewerSamples.Png(), mediaType: "model/stl");
        var d = (await env.Viewer.DescribeAsync(Alice, n1, png.Id, null)).Value!;
        Assert.Equal(("png", ViewerTypes.Image, ViewerStatus.Ready), (d.Detection.Format.Id, d.ViewerType, d.Status));
        Assert.Contains("is named like", d.Detection.Warning, StringComparison.Ordinal);
        var shown = (await env.Viewer.OpenAsync(Alice, n1, png.Id, null)).Content!;
        Assert.Equal("image/png", shown.Format.MediaType);

        // Text pretending to be a model, and random bytes pretending to be a GLB: no preview, nothing sent, refusal audited.
        var (n2, text) = await Create(env, "model.stl", System.Text.Encoding.UTF8.GetBytes("just a note " + Guid.NewGuid()));
        var noPreview = await env.Viewer.OpenAsync(Alice, n2, text.Id, "corr-text");
        Assert.Equal(ViewerContentStatus.NoPreview, noPreview.Status);
        Assert.Null(noPreview.Content);
        var (n3, random) = await Create(env, "scan.glb", System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
        Assert.Equal(ViewerContentStatus.NoPreview, (await env.Viewer.OpenAsync(Alice, n3, random.Id, null)).Status);
        Assert.Equal(ViewerStatus.NoPreview, (await env.Viewer.DescribeAsync(Alice, n3, random.Id, null)).Value!.Status);

        // An unknown type has no preview but still describes its facts.
        var (n4, docx) = await Create(env, "report.docx", System.Text.Encoding.ASCII.GetBytes("PK\u0003\u0004 not really " + Guid.NewGuid()));
        var facts = (await env.Viewer.DescribeAsync(Alice, n4, docx.Id, null)).Value!;
        Assert.Equal((ViewerStatus.NoPreview, ViewerTypes.None, docx.Sha256), (facts.Status, facts.ViewerType, facts.File.Sha256));

        var refusals = (await Views(env.Ds)).Where(e => e.Outcome == AuditOutcome.Denied).ToList();
        Assert.Contains(refusals, e => e.CorrelationId == "corr-text" && e.Payload.Contains("no_preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Size_limits_show_too_large_to_preview_per_type_and_are_audited_without_reading_the_file()
    {
        var env = await Setup(new ViewerOptions { MaxModelBytes = 500, MaxPdfBytes = 50, MaxImageBytes = 50 });
        var (n1, model) = await Create(env, "big.stl", ViewerSamples.AsciiStlCube()); // ~3 KB
        var (n2, pdf) = await Create(env, "big.pdf", ViewerSamples.Pdf(1, Guid.NewGuid().ToString("N")), mediaType: "application/pdf");
        var (n3, png) = await Create(env, "big.png", ViewerSamples.Png(64, 64));
        foreach (var (n, f) in new[] { (n1, model), (n2, pdf), (n3, png) })
        {
            var d = (await env.Viewer.DescribeAsync(Alice, n, f.Id, null)).Value!;
            Assert.Equal(ViewerStatus.TooLarge, d.Status);
            Assert.Contains("too large to preview", d.Message, StringComparison.Ordinal);
            Assert.Contains("Download it instead", d.Message, StringComparison.Ordinal);
            var open = await env.Viewer.OpenAsync(Alice, n, f.Id, "corr-big-" + n);
            Assert.Equal(ViewerContentStatus.TooLarge, open.Status);
            Assert.Null(open.Content);
        }
        Assert.Equal(3, (await Views(env.Ds)).Count(e => e.Outcome == AuditOutcome.Denied && e.Payload.Contains("too_large", StringComparison.Ordinal)));
        Assert.DoesNotContain(await Views(env.Ds), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_registered_gate_refuses_before_any_byte_is_read_the_single_place_for_a_quarantine_check()
    {
        var gate = new RefuseNamed("quarantined.stl");
        var env = await Setup(gates: gate);
        var (n1, bad) = await Create(env, "quarantined.stl", ViewerSamples.AsciiStlCube());
        var (n2, good) = await Create(env, "fine.stl", ViewerSamples.AsciiStlCube(7));

        var d = (await env.Viewer.DescribeAsync(Alice, n1, bad.Id, null)).Value!;
        Assert.Equal((ViewerStatus.Blocked, "Quarantined: scan pending."), (d.Status, d.Message));
        var open = await env.Viewer.OpenAsync(Alice, n1, bad.Id, "corr-gate");
        Assert.Equal((ViewerContentStatus.Blocked, "Quarantined: scan pending."), (open.Status, open.Message));
        Assert.Null(open.Content);
        Assert.True((await env.Viewer.OpenAsync(Alice, n2, good.Id, null)).IsOk);
        Assert.Contains("blocked", (await Views(env.Ds)).Single(e => e.CorrelationId == "corr-gate").Payload, StringComparison.Ordinal);
    }

    private sealed class RefuseNamed(string name) : IFileViewGate
    {
        public ValueTask<ViewGateDecision> CheckAsync(ViewableFile file, CancellationToken ct = default) =>
            ValueTask.FromResult(file.FileName == name ? ViewGateDecision.Refuse("Quarantined: scan pending.") : ViewGateDecision.Allow);
    }

    // ---------------------------------------------------------------- integrity

    [Fact]
    public async Task A_tampered_vault_object_is_never_delivered_and_the_failure_is_audited()
    {
        var env = await Setup();
        var original = ViewerSamples.AsciiStlCube(11);
        var (number, file) = await Create(env, "tampered.stl", original);

        using var s3 = ObjectVault.CreateClient(env.Vault);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = env.Vault.Bucket, Key = file.Sha256, InputStream = new MemoryStream(ViewerSamples.AsciiStlCube(12)) });

        var e = await Assert.ThrowsAsync<StoredFileIntegrityException>(() => env.Viewer.OpenAsync(Alice, number, file.Id, "corr-tamper"));
        Assert.Contains("failed integrity verification", e.Message, StringComparison.Ordinal);
        var failure = Assert.Single(await Views(env.Ds));
        Assert.Equal((AuditOutcome.Failure, "corr-tamper"), (failure.Outcome, failure.CorrelationId));
        Assert.Contains("integrity", failure.Payload, StringComparison.Ordinal);
        // The same object also fails the download, which is the behaviour viewing must not be weaker than.
        await Assert.ThrowsAsync<VaultIntegrityException>(() => env.Docs.DownloadAsync(Alice, number, file.Id, null, null, null));
    }

    [Fact]
    public async Task The_primary_file_of_a_document_can_be_opened_by_revision()
    {
        var env = await Setup();
        var (number, file) = await Create(env, "primary.pdf", ViewerSamples.Pdf(1, Guid.NewGuid().ToString("N")), mediaType: "application/pdf");
        var primary = (await env.Viewer.DescribePrimaryAsync(Alice, number, null, null)).Value!;
        Assert.Equal(file.Id, primary.File.FileId);
        Assert.Equal(file.Id, (await env.Viewer.DescribePrimaryAsync(Alice, number, "A", null)).Value!.File.FileId);
        Assert.Equal(OutcomeKind.NotFound, (await env.Viewer.DescribePrimaryAsync(Alice, number, "B", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Viewer.DescribePrimaryAsync(Lena, number, null, null)).Kind);

        var bare = (await env.Docs.CreateAsync(Alice, Cmd("No files"), null, null)).Value!;
        Assert.Equal(OutcomeKind.NotFound, (await env.Viewer.DescribePrimaryAsync(Alice, bare.Number, null, null)).Kind);
    }
}
