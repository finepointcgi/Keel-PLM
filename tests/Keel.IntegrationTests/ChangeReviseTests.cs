using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Revising released parts inside an ECN (ADR-0019): the next revision is created in work, bound to the notice.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeReviseTests(PostgresFixture pg)
{
    [Fact]
    public async Task Adding_a_released_part_creates_the_next_revision_from_the_latest_iteration_carrying_links_and_structure()
    {
        var env = await ChangeEnv.CreateAsync(pg, documents: true);
        var alice = ChangeEnv.Alice;
        var child = await env.NewPartAsync("Fastener");
        var drawing = (await env.Docs!.CreateAsync(alice, new Keel.Documents.CreateDocumentCommand("Assembly drawing", "Drawing", ChangeEnv.Itar()), ChangeEnv.File("asm.txt"), null)).Value!.Number;
        var spec = (await env.Docs.CreateAsync(alice, new Keel.Documents.CreateDocumentCommand("Process specification", "Specification", ChangeEnv.Itar()), ChangeEnv.File("spec.txt"), null)).Value!.Number;
        var asm = await env.NewPartAsync("Sensor assembly", type: "Assembly");

        // Work on A: structure, links, a second iteration; then release.
        Assert.True((await env.Parts.CheckOutAsync(alice, asm, null)).IsOk);
        Assert.True((await env.Bom.AddUsageAsync(alice, asm, new AddUsageCommand(child, 4, "EA", 10, ["R1", "R2", "R3", "R4"]), null)).IsOk);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(alice, asm, drawing, "describes", null, null)).IsOk);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(alice, asm, spec, "references", null, null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(alice, asm, "structure and links", null)).IsOk);
        await env.ReleasePartAsync(asm);
        var a = await env.PartAsync(asm);
        Assert.Equal(("A", 2, "Released"), (a.Revision.Revision, a.Revision.LatestIteration, a.Revision.State));

        var ecn = (await env.NewEcnAsync()).Number;
        var detail = await env.AddAsync(alice, ecn, asm);

        // The affected row names the resulting revision, in work.
        var row = Assert.Single(detail.Affected);
        Assert.Equal((asm, "A", "revise", "B", "In Work"), (row.Key, row.Revision, row.Action, row.ResultingRevision, row.ResultingState));

        var b = await env.PartAsync(asm);
        Assert.Equal(("B", "InWork", true, 1, "B.1"), (b.Revision.Revision, b.Revision.State, b.Revision.IsLatest, b.Revision.LatestIteration, b.Iteration.Label));
        Assert.Equal(a.Iteration.Description, b.Iteration.Description);
        Assert.Equal(a.Iteration.Attributes.ToJsonString(), b.Iteration.Attributes.ToJsonString());
        Assert.Contains("Revised from A.2", b.Iteration.Comment);
        Assert.Contains(ecn, b.Iteration.Comment);

        // Revision A is untouched: still Released, same iterations, no longer the latest.
        var oldA = await env.PartAsync(asm, "A");
        Assert.Equal(("Released", false, 2), (oldA.Revision.State, oldA.Revision.IsLatest, oldA.Revision.LatestIteration));
        Assert.Equal(a.History.Where(h => h.Revision == "A").Select(h => h.Id), oldA.History.Where(h => h.Revision == "A").Select(h => h.Id));

        // Document links and BOM lines were carried into B.1 (ADR-0016 note, ADR-0003).
        var links = (await env.Docs.GetPartLinksAsync(alice, asm, "B", 1, null)).Value!;
        Assert.Equal(["describes:" + drawing, "references:" + spec], links.Links.Select(l => $"{l.LinkType}:{l.Document!.Number}").Order().ToArray());
        var bomB = (await env.Bom.ExplodeAsync(alice, new BomQuery(asm, "B", 1), null)).Value!;
        var line = Assert.Single(bomB.Lines);
        Assert.Equal((child, 4m, 10), (line.Number, line.Quantity, line.FindNumber));
        Assert.Equal(["R1", "R2", "R3", "R4"], line.ReferenceDesignators);
        var bomA = (await env.Bom.ExplodeAsync(alice, new BomQuery(asm, "A", 2), null)).Value!;
        Assert.Equal(bomA.Lines.Select(l => (l.Number, l.Quantity)), bomB.Lines.Select(l => (l.Number, l.Quantity)));

        // Engineers work on B normally: check out, edit, check in.
        await env.CheckedInEditAsync(alice, asm, "Revised bracket geometry");
        Assert.Equal("B.2", (await env.PartAsync(asm)).Iteration.Label);

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "change.add_affected" && e.ObjectId == ecn && e.Outcome == Keel.Audit.AuditOutcome.Success && e.Payload.Contains("\"resulting\":\"B\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_one_open_ecn_may_hold_a_revision_and_unreleased_or_old_revisions_are_refused()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var alice = ChangeEnv.Alice;
        var part = await env.ReleasedPartAsync();
        // The first notice carries an extra CUI category (CTI) that the part does not: its readers need CTI, the part's readers do not.
        var cti = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "CTI"],
            controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var first = (await env.NewEcnAsync(title: "First", markings: cti)).Number;
        var ivan = WorkflowEnv.User("ivan", [Roles.Viewer, Roles.Author], cui: ["EXPT"]);
        var second = (await env.NewEcnAsync(ivan, title: "Second")).Number;
        await env.AddAsync(alice, first, part);

        // The part's latest revision is now B, the working revision of the first notice; revision A is held by it as well.
        var clash = await env.Change.AddAffectedAsync(alice, second, new AddAffectedCommand("part", part, "revise"), null);
        Assert.Equal(OutcomeKind.Conflict, clash.Kind);
        Assert.Contains($"working revision of {first}", clash.Errors[0], StringComparison.Ordinal);
        var secondOfAlice = (await env.NewEcnAsync(title: "Alice's second")).Number;
        var heldA = await env.Change.AddAffectedAsync(alice, secondOfAlice, new AddAffectedCommand("part", part, "revise", "A"), null);
        Assert.Equal(OutcomeKind.Conflict, heldA.Kind);
        Assert.Contains($"already being changed by {first}", heldA.Errors[0], StringComparison.Ordinal);
        // Ivan can read the part but not the first notice (CTI): he learns that another notice holds it, not which one.
        var hidden = await env.Change.AddAffectedAsync(ivan, second, new AddAffectedCommand("part", part, "revise", "A"), null);
        Assert.Equal(OutcomeKind.Conflict, hidden.Kind);
        Assert.Contains("another open change notice", hidden.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(first, hidden.Errors[0], StringComparison.Ordinal);

        // The database enforces it as well: a second binding row for the same revision violates the unique index.
        await using var dup = env.Ds.CreateCommand(
            "INSERT INTO chg.affected_object (id, change_id, object_type, master_id, object_key, object_name, revision_id, revision, change_action, binding, added_at, added_by) " +
            "SELECT gen_random_uuid(), (SELECT id FROM chg.change WHERE number = $1), object_type, master_id, object_key, object_name, revision_id, revision, 'obsolete', true, now(), 'x' FROM chg.affected_object");
        dup.Parameters.AddWithValue(second);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync())).SqlState);

        // Informational listing ('none') does not hold the revision.
        await env.AddAsync(alice, second, part, ChangeActions.None);

        // An unreleased part cannot be revised in a change, nor can it be obsoleted this way.
        var draft = await env.NewPartAsync("Draft part");
        var third = (await env.NewEcnAsync(title: "Third")).Number;
        var unreleased = await env.Change.AddAffectedAsync(alice, third, new AddAffectedCommand("part", draft, "revise"), null);
        Assert.Equal(OutcomeKind.Conflict, unreleased.Kind);
        Assert.Contains("In Work", unreleased.Errors[0], StringComparison.Ordinal);
        Assert.Contains("edited directly", unreleased.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.AddAffectedAsync(alice, third, new AddAffectedCommand("part", draft, "obsolete"), null)).Kind);

        // Revision A is no longer the latest once B exists: naming it explicitly is refused.
        var stale = await env.Change.AddAffectedAsync(alice, third, new AddAffectedCommand("part", part, "none", "A"), null);
        Assert.Equal(OutcomeKind.Conflict, stale.Kind);
        Assert.Contains("not the latest", stale.Errors[0], StringComparison.Ordinal);
        Assert.Contains("working revision", (await env.Change.AddAffectedAsync(alice, third, new AddAffectedCommand("part", part, "revise", "B"), null)).Errors[0], StringComparison.Ordinal);

        // Same object twice in one change is refused; unknown types are refused naming the phase.
        Assert.Contains("already listed", (await env.Change.AddAffectedAsync(alice, first, new AddAffectedCommand("part", part, "none"), null)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("Only parts can be", (await env.Change.AddAffectedAsync(alice, third, new AddAffectedCommand("document", "D-000001", "revise"), null)).Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_created_by_a_change_cannot_be_released_by_the_part_workflow()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ecn = (await env.NewEcnAsync()).Number;
        await env.AddAsync(ChangeEnv.Alice, ecn, part);

        var attempt = await env.Flow.StartAsync(ChangeEnv.Alice, WorkflowEnv.Start(part, "rita", "bob", "carol"), null);
        Assert.Equal(OutcomeKind.Conflict, attempt.Kind);
        Assert.Contains($"belongs to change notice {ecn}", attempt.Errors[0], StringComparison.Ordinal);
        Assert.Contains("released only with it", attempt.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", (await env.PartAsync(part)).Revision.State);

        // The manual lifecycle path cannot release it either (workflow-only transition from 1.5).
        Assert.False((await env.Parts.SetStateAsync(ChangeEnv.Alice, part, "Released", null, false, null)).IsOk);
    }

    [Fact]
    public async Task Removing_an_object_abandons_an_untouched_resulting_revision_and_restores_the_old_latest()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var alice = ChangeEnv.Alice;
        var part = await env.ReleasedPartAsync();
        var ecn = (await env.NewEcnAsync()).Number;
        var added = await env.AddAsync(alice, ecn, part);

        var removed = await env.Change.RemoveAffectedAsync(alice, ecn, added.Affected.Single().Id, null);
        Assert.True(removed.IsOk, string.Join("; ", removed.Errors));
        Assert.Empty(removed.Value!.Affected);
        Assert.Equal(("A", "Released", true), ((await env.PartAsync(part)).Revision.Revision, (await env.PartAsync(part)).Revision.State, (await env.PartAsync(part)).Revision.IsLatest));
        var b = await env.PartAsync(part, "B");
        Assert.Equal(("Obsolete", false), (b.Revision.State, b.Revision.IsLatest));
        // Nothing was deleted: the abandoned revision and the affected row are still in the database, and the revision is free again.
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM chg.affected_object WHERE removed_at IS NOT NULL"));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM chg.affected_object WHERE binding"));

        // Adding it again creates revision C: an abandoned revision label is never reused.
        var again = await env.AddAsync(alice, ecn, part);
        Assert.Equal("C", again.Affected.Single().ResultingRevision);
        Assert.Contains(removed.Value.History, h => h.Action == "Object removed");

        // Once someone worked on the resulting revision it cannot be silently dropped from the change.
        await env.CheckedInEditAsync(alice, part, "Real work");
        var refused = await env.Change.RemoveAffectedAsync(alice, ecn, again.Affected.Single().Id, null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("already worked on", refused.Errors[0], StringComparison.Ordinal);
    }
}
