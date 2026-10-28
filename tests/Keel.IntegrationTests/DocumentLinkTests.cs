using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Npgsql;
using static Keel.IntegrationTests.DocumentServiceTests;

namespace Keel.IntegrationTests;

/// <summary>Part ↔ document links owned by part iterations (ADR-0016).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentLinkTests(PostgresFixture pg)
{
    // Kim is on both programs, so she can link a KRAKEN document that Alice cannot see.
    private static readonly Subject Kim = User("kim", programs: ["ORCA", "KRAKEN"]);

    private sealed record Env(DocumentService Docs, PartService Parts, TestDatabase Db, NpgsqlDataSource Ds);

    private async Task<Env> Setup()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var auditLog = new PostgresAuditLog(ds, TimeProvider.System);
        return new Env(new DocumentService(ds, auditLog, TimeProvider.System, new ObjectVault(await VaultContainer.OptionsAsync())),
            new PartService(ds, auditLog, TimeProvider.System), db, ds);
    }

    private static FileUpload File(string name) => new(name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"{name} {Guid.NewGuid()}")));

    /// <summary>P-000001 (ITAR/ORCA part); D-000001 drawing, D-000002 specification (both ITAR/ORCA); D-000003 KRAKEN report.</summary>
    private static async Task Seed(Env env)
    {
        Assert.True((await env.Parts.CreateAsync(Alice, new CreatePartCommand("Bracket", "Component", "EA", "Make", Itar(), "Synthetic", new JsonObject()), null)).IsOk);
        Assert.True((await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Bracket drawing", "Drawing", Itar()), File("bracket.dwg"), null)).IsOk);
        Assert.True((await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Anodizing specification", "Specification", Itar()), File("anodize.txt"), null)).IsOk);
        Assert.True((await env.Docs.CreateAsync(Kim, new CreateDocumentCommand("Kraken load report", "Report", Itar("KRAKEN")), File("loads.txt"), null)).IsOk);
    }

    private static async Task<List<AuditRecord>> Audit(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    [Fact]
    public async Task Links_change_only_through_the_part_working_copy_and_carry_forward()
    {
        var env = await Setup();
        await Seed(env);

        Assert.Contains("Check P-000001 out", (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", null, null)).Errors[0]);
        await env.Parts.CheckOutAsync(Alice, "P-000001", null);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.AddLinkToWorkingCopyAsync(Bob, "P-000001", "D-000001", "describes", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "depicts", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000002", "references", "A", null)).Kind);
        Assert.Contains("no revision C", (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", "C", null)).Errors[0]);

        var working = (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", null, null)).Value!;
        Assert.Empty(working.Links); // A.1 has no links; the change lives in the working copy until check-in
        var w = Assert.Single(working.WorkingLinks!);
        Assert.Equal(("describes", true, "A", "D-000001", "A"), (w.LinkType, w.IsWorking, w.PinnedRevision, w.Document!.Number, w.Document.Revision));
        await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000002", "references", null, null);
        Assert.Contains("already", (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000002", "references", null, null)).Errors[0]);

        // Alice cannot read the KRAKEN document: the answer is the same as for a document that does not exist.
        var hidden = await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000003", "references", null, null);
        var missing = await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-999999", "references", null, null);
        Assert.Equal(("Document D-000003 was not found.", "Document D-999999 was not found."), (hidden.Errors[0], missing.Errors[0]));

        await env.Parts.CheckInAsync(Alice, "P-000001", "Linked drawing and spec", null); // -> A.2

        // Kim adds the KRAKEN report in A.3.
        await env.Parts.CheckOutAsync(Kim, "P-000001", null);
        var kimWorking = (await env.Docs.GetPartLinksAsync(Kim, "P-000001", null, null, null)).Value!;
        Assert.Equal(2, kimWorking.WorkingLinks!.Count); // check-out copied A.2's links into the working copy
        await env.Docs.AddLinkToWorkingCopyAsync(Kim, "P-000001", "D-000003", "references", null, null);
        await env.Parts.CheckInAsync(Kim, "P-000001", "Added load report", null); // -> A.3

        // Alice sees the KRAKEN link as a redacted placeholder: link id and type only.
        var latest = (await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!;
        Assert.Equal("A.3", latest.IterationLabel);
        Assert.Null(latest.WorkingLinks);
        Assert.Equal(3, latest.Links.Count);
        var redacted = Assert.Single(latest.Links, l => l.Redacted);
        Assert.Equal(("references", null, null, false, null, null), (redacted.LinkType, redacted.Document, redacted.PinnedRevision, redacted.NewerRevisionAvailable, redacted.CreatedBy, redacted.CreatedAt));
        Assert.Equal(["D-000001", "D-000002"], latest.Links.Where(l => !l.Redacted).Select(l => l.Document!.Number).Order());
        Assert.DoesNotContain("KRAKEN", latest.PageBanner);
        Assert.Equal(3, (await env.Docs.GetPartLinksAsync(Kim, "P-000001", null, null, null)).Value!.Links.Count(l => !l.Redacted));

        // History is preserved: A.2 still shows exactly what it was checked in with, A.1 had none.
        Assert.Equal(["D-000001", "D-000002"], (await env.Docs.GetPartLinksAsync(Kim, "P-000001", "A", 2, null)).Value!.Links.Select(l => l.Document!.Number).Order());
        Assert.Empty((await env.Docs.GetPartLinksAsync(Alice, "P-000001", "A", 1, null)).Value!.Links);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.GetPartLinksAsync(Alice, "P-000001", "A", 9, null)).Kind);

        // Removing in the working copy does not touch the checked-in iteration; undo discards the working links.
        var co = (await env.Parts.CheckOutAsync(Alice, "P-000001", null));
        Assert.True(co.IsOk);
        var view = (await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!;
        var drawing = view.WorkingLinks!.Single(l => l.Document?.Number == "D-000001");
        Assert.Equal(["document.read_denied"], (await env.Docs.RemoveLinkFromWorkingCopyAsync(Alice, "P-000001", view.WorkingLinks!.Single(l => l.Redacted).LinkId, null)).Errors);
        var removed = (await env.Docs.RemoveLinkFromWorkingCopyAsync(Alice, "P-000001", drawing.LinkId, null)).Value!;
        Assert.Equal(2, removed.WorkingLinks!.Count);
        Assert.Equal(3, removed.Links.Count);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.RemoveLinkFromWorkingCopyAsync(Alice, "P-000001", drawing.LinkId, null)).Kind);
        await env.Parts.UndoCheckOutAsync(Alice, "P-000001", null);
        Assert.Null((await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!.WorkingLinks);
        Assert.Equal(3, (await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!.Links.Count);

        var audit = await Audit(env.Ds);
        Assert.Equal(3, audit.Count(a => a.Action == "part.link_document" && a.Outcome == AuditOutcome.Success));
        Assert.Equal(1, audit.Count(a => a.Action == "part.unlink_document" && a.Outcome == AuditOutcome.Success));
        Assert.Contains(audit, a => a.Action == "part.unlink_document" && a.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, a => a.Action == "document.read" && a.Outcome == AuditOutcome.Denied && a.ObjectId == "D-000003");
    }

    [Fact]
    public async Task Describes_pins_a_revision_and_references_resolves_to_latest_released()
    {
        var env = await Setup();
        await Seed(env);
        await env.Parts.CheckOutAsync(Alice, "P-000001", null);
        await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", null, null);
        await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000002", "references", null, null);
        await env.Parts.CheckInAsync(Alice, "P-000001", null, null);

        // Revising documents (A -> B) arrives with change notices in 1.6; simulate a released A superseded by an in-work B.
        await env.Docs.SetStateAsync(Ada, "D-000001", "Released", "Released on paper", true, null);
        await env.Docs.SetStateAsync(Ada, "D-000002", "Released", "Released on paper", true, null);
        await env.Db.ExecuteAsSuperuserAsync(
            """
            WITH old AS (UPDATE plm.document_revision SET is_latest = false WHERE is_latest RETURNING id, master_id),
                 rev AS (INSERT INTO plm.document_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
                         SELECT gen_random_uuid(), master_id, 'B', 'InWork', true, 1, now(), 'test', now(), 'test' FROM old RETURNING id)
            INSERT INTO plm.document_iteration (id, revision_id, iteration, description, created_at, created_by) SELECT gen_random_uuid(), id, 1, 'rev B', now(), 'test' FROM rev;
            """);

        var links = (await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!.Links;
        var describes = links.Single(l => l.LinkType == "describes");
        Assert.Equal(("A", "A", "Released", true), (describes.PinnedRevision, describes.Document!.Revision, describes.Document.State, describes.NewerRevisionAvailable));
        var references = links.Single(l => l.LinkType == "references");
        // ASSUMPTION(A23): latest Released wins over the newer in-work revision.
        Assert.Equal((null, "A", "Released", false), (references.PinnedRevision, references.Document!.Revision, references.Document.State, references.NewerRevisionAvailable));

        // Explicit pin to a revision on a new link.
        await env.Parts.CheckOutAsync(Alice, "P-000001", null);
        var w = (await env.Docs.GetPartLinksAsync(Alice, "P-000001", null, null, null)).Value!.WorkingLinks!;
        await env.Docs.RemoveLinkFromWorkingCopyAsync(Alice, "P-000001", w.Single(l => l.LinkType == "describes").LinkId, null);
        var latestB = (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", null, null)).Value!.WorkingLinks!.Single(l => l.LinkType == "describes");
        Assert.Equal(("B", false), (latestB.PinnedRevision, latestB.NewerRevisionAvailable));
    }

    [Fact]
    public async Task Reverse_listing_redacts_parts_the_caller_cannot_read()
    {
        var env = await Setup();
        await Seed(env);
        Assert.True((await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Open standard", "Specification", Open), File("std.txt"), null)).IsOk); // D-000004
        Assert.True((await env.Parts.CreateAsync(Alice, new CreatePartCommand("Washer", "Standard", "EA", "Buy", Open, "Synthetic", new JsonObject()), null)).IsOk); // P-000002
        foreach (var part in new[] { "P-000001", "P-000002" })
        {
            await env.Parts.CheckOutAsync(Alice, part, null);
            Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(Alice, part, "D-000004", "references", null, null)).IsOk);
            await env.Parts.CheckInAsync(Alice, part, null, null);
        }

        var alice = (await env.Docs.GetDocumentPartsAsync(Alice, "D-000004", null)).Value!;
        Assert.Equal(["P-000001", "P-000002"], alice.Parts.Select(p => p.PartNumber));
        Assert.Equal("A.2", alice.Parts[0].IterationLabel);

        var lena = (await env.Docs.GetDocumentPartsAsync(Lena, "D-000004", null)).Value!;
        Assert.Equal(["P-000002", null], lena.Parts.Select(p => p.PartNumber));
        var placeholder = lena.Parts[1];
        Assert.Equal((true, "references", null, null, null), (placeholder.Redacted, placeholder.LinkType, placeholder.PartName, placeholder.IterationLabel, placeholder.Markings));
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", lena.PageBanner);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.GetDocumentPartsAsync(Lena, "D-000001", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.GetPartLinksAsync(Lena, "P-000001", null, null, null)).Kind);
    }

    [Fact]
    public async Task Link_rows_are_immutable_and_locked_parts_reject_links()
    {
        var env = await Setup();
        await Seed(env);
        await using var app = env.Db.AppDataSource();
        async Task Rejects(string sql, string state)
        {
            await using var cmd = app.CreateCommand(sql);
            Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
        const string d1 = "(SELECT id FROM plm.document_master WHERE number = 'D-000001')";
        const string d2 = "(SELECT id FROM plm.document_master WHERE number = 'D-000002')";
        const string d1Rev = "(SELECT r.id FROM plm.document_revision r JOIN plm.document_master m ON m.id = r.master_id WHERE m.number = 'D-000001')";
        // Structural rules, checked while the part revision is still unlocked: 'describes' pins a revision of the same
        // document, 'references' pins none.
        await Rejects($"INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, created_at, created_by) " +
            $"SELECT gen_random_uuid(), id, 'describes', {d2}, now(), 'x' FROM plm.part_iteration", PostgresErrorCodes.CheckViolation);
        await Rejects($"INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, document_revision_id, created_at, created_by) " +
            $"SELECT gen_random_uuid(), id, 'references', {d1}, {d1Rev}, now(), 'x' FROM plm.part_iteration", PostgresErrorCodes.CheckViolation);
        await Rejects($"INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, document_revision_id, created_at, created_by) " +
            $"SELECT gen_random_uuid(), id, 'describes', {d2}, {d1Rev}, now(), 'x' FROM plm.part_iteration", PostgresErrorCodes.ForeignKeyViolation);

        await env.Parts.CheckOutAsync(Alice, "P-000001", null);
        await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000001", "describes", null, null);
        await env.Parts.CheckInAsync(Alice, "P-000001", null, null);
        await Rejects("UPDATE plm.part_document_link SET created_by = 'mallory'", PostgresErrorCodes.InsufficientPrivilege);
        await Rejects("DELETE FROM plm.part_document_link", PostgresErrorCodes.InsufficientPrivilege);

        await env.Parts.SetStateAsync(Alice, "P-000001", "UnderReview", null, false, null);
        Assert.Contains("locked", (await env.Docs.AddLinkToWorkingCopyAsync(Alice, "P-000001", "D-000002", "references", null, null)).Errors[0]);
        await Rejects($"INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, created_at, created_by) " +
            $"SELECT gen_random_uuid(), id, 'references', {d2}, now(), 'x' FROM plm.part_iteration WHERE iteration = 2", PostgresErrorCodes.CheckViolation);
    }
}
