using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;

namespace Keel.IntegrationTests;

/// <summary>Impact analysis: where-used (redacted parents), conflicting open changes, linked documents, structure comparison, banner and audit.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeImpactTests(PostgresFixture pg)
{
    private static Subject Alice => ChangeEnv.Alice;

    private static async Task<string> AssembleAsync(ChangeEnv env, string name, Markings markings, params string[] children)
    {
        var number = await env.NewPartAsync(name, markings, type: "Assembly");
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        var find = 10;
        foreach (var child in children)
        {
            var added = await env.Bom.AddUsageAsync(Alice, number, new AddUsageCommand(child, 1, "EA", find), null);
            Assert.True(added.IsOk, string.Join("; ", added.Errors));
            find += 10;
        }
        Assert.True((await env.Parts.CheckInAsync(Alice, number, "structure", null)).IsOk);
        return number;
    }

    [Fact]
    public async Task The_analysis_covers_where_used_documents_conflicts_and_structure_changes_and_hides_what_the_reader_may_not_see()
    {
        var env = await ChangeEnv.CreateAsync(pg, documents: true);
        var open = ChangeEnv.Open;

        // C1 is used by M (open), M by T (open), and by H, a restricted assembly (ITAR, program ORCA) that Eve cannot see.
        var c1 = await env.NewPartAsync("Sensor bracket", open);
        var c2 = await env.NewPartAsync("Spacer", open);
        var drawing = (await env.Docs!.CreateAsync(Alice, new CreateDocumentCommand("Bracket drawing", "Drawing", open), ChangeEnv.File("bracket.txt"), null)).Value!.Number;
        var spec = (await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Restricted specification", "Specification", ChangeEnv.Itar()), ChangeEnv.File("spec.txt"), null)).Value!.Number;
        Assert.True((await env.Parts.CheckOutAsync(Alice, c1, null)).IsOk);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(Alice, c1, drawing, "describes", null, null)).IsOk);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(Alice, c1, spec, "references", null, null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(Alice, c1, "links", null)).IsOk);
        await env.ReleasePartAsync(c1);
        var m = await AssembleAsync(env, "Sensor module", open, c1);
        await env.ReleasePartAsync(m);
        var t = await AssembleAsync(env, "Top assembly", open, m);
        await env.ReleasePartAsync(t);
        var h = await AssembleAsync(env, "Restricted assembly", ChangeEnv.Itar(), c1);

        // The notice revises the bracket and the module; engineers add a spacer to the new module revision.
        var ecn = (await env.NewChangeAsync(Alice, ChangeTypes.Ecn, "Stiffen bracket and module", open, ChangeClasses.II)).Number;
        await env.AddAsync(Alice, ecn, c1);
        await env.AddAsync(Alice, ecn, m);
        Assert.True((await env.Parts.CheckOutAsync(Alice, m, null)).IsOk);
        Assert.True((await env.Bom.AddUsageAsync(Alice, m, new AddUsageCommand(c2, 2, "EA", 20), null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(Alice, m, "add spacer", null)).IsOk);

        // Other open changes: one lists the same part, one lists a parent (the top assembly), one is restricted, one is finished.
        var sameObject = (await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Also touches the bracket", open)).Number;
        await env.AddAsync(Alice, sameObject, c1, ChangeActions.None);
        var parentChange = (await env.NewChangeAsync(Alice, ChangeTypes.Ecn, "Touches the top assembly", open, ChangeClasses.II)).Number;
        await env.AddAsync(Alice, parentChange, t, ChangeActions.None);
        var restricted = (await env.NewChangeAsync(Alice, ChangeTypes.Ecn, "Restricted change", ChangeEnv.Itar(), ChangeClasses.II)).Number;
        await env.AddAsync(Alice, restricted, c1, ChangeActions.None);
        var finished = (await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Closed report", open)).Number;
        await env.AddAsync(Alice, finished, c1, ChangeActions.None);
        Assert.True((await env.Change.TransitionAsync(Alice, finished, "Closed", "NoActionRequired", null, null)).IsOk);

        // ---- the full picture for someone who can read everything
        var full = (await env.Impact.AnalyzeAsync(Alice, ecn, 5, "corr-impact")).Value!;
        var bracket = full.Objects.Single(o => o.Key == c1);
        // A parent is a revision whose latest iteration contains the part: the module's revisions A and B both do.
        Assert.Equal(new[] { "Sensor module:1:A", "Sensor module:1:B", "Top assembly:2:A", "Restricted assembly:1:A" }.Order(),
            bracket.WhereUsed.Select(w => $"{w.ParentName}:{w.Level}:{w.ParentRevision}").Order());
        Assert.Equal(2, bracket.MaxLevel);
        Assert.Equal(0, bracket.RedactedParents);
        Assert.Equal(["describes:" + drawing, "references:" + spec], bracket.Documents.Select(d => $"{d.LinkType}:{d.Number}").Order());
        var module = full.Objects.Single(o => o.Key == m);
        Assert.Equal(["Top assembly"], module.WhereUsed.Select(w => w.ParentName));
        // The revised assembly shows the structure change between A.n and B.2: the spacer was added.
        var delta = Assert.Single(module.Compare!.Changes);
        Assert.Equal(("Added", c2), (delta.Kind, delta.Key));
        Assert.Equal(("A.2", "B.2"), (module.Compare.From, module.Compare.To));
        Assert.Empty(bracket.Compare!.Changes);
        Assert.Contains("No structure change", bracket.CompareNote, StringComparison.Ordinal);
        // Conflicts: same object, a where-used parent, and the restricted change (visible to Alice); the closed report is not open.
        Assert.Equal(new[] { parentChange, restricted, sameObject }.Order(), full.Conflicts.Select(c => c.ChangeNumber).Order());
        Assert.Equal("same object", full.Conflicts.Single(c => c.ChangeNumber == sameObject).Kind);
        Assert.Equal("uses an affected part", full.Conflicts.Single(c => c.ChangeNumber == parentChange).Kind);
        Assert.Equal(new ImpactSummary(2, 0, 5, 0, 2, 0, 3, 1), full.Summary);
        Assert.Contains("ITAR", full.Banner, StringComparison.Ordinal);

        // ---- Eve cannot see ORCA data: the restricted parent, document and change are placeholders or absent
        var eve = ChangeEnv.Eve;
        var limited = (await env.Impact.AnalyzeAsync(eve, ecn, 5, null)).Value!;
        var eveBracket = limited.Objects.Single(o => o.Key == c1);
        Assert.Equal(["Sensor module", "Sensor module", "Top assembly"], eveBracket.WhereUsed.Where(w => !w.Redacted).Select(w => w.ParentName!).Order());
        var placeholder = Assert.Single(eveBracket.WhereUsed, w => w.Redacted);
        Assert.Equal((null, null, null, 1), (placeholder.ParentKey, placeholder.ParentName, placeholder.Markings, placeholder.Level));
        Assert.Equal(1, eveBracket.RedactedParents);
        Assert.Equal(["describes"], eveBracket.Documents.Where(d => !d.Redacted).Select(d => d.LinkType));
        var redactedDoc = Assert.Single(eveBracket.Documents, d => d.Redacted);
        Assert.Equal(("references", null, null), (redactedDoc.LinkType, redactedDoc.Number, redactedDoc.Name));
        Assert.DoesNotContain(limited.Conflicts, c => c.ChangeNumber == restricted);
        Assert.Equal(2, limited.Conflicts.Count);
        Assert.Equal((1, 1, 1), (limited.Summary.RedactedParents, limited.Summary.RedactedDocuments, limited.Summary.OpenConflicts - 1));
        Assert.DoesNotContain("ITAR", limited.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain(h, System.Text.Json.JsonSerializer.Serialize(limited), StringComparison.Ordinal);
        Assert.DoesNotContain(restricted, System.Text.Json.JsonSerializer.Serialize(limited), StringComparison.Ordinal);

        // ---- audited with counts, and level limits are validated
        var audit = Assert.Single(await env.AuditAsync(), e => e.Action == "change.impact" && e.CorrelationId == "corr-impact");
        Assert.Equal((ecn, AuditOutcome.Success), (audit.ObjectId, audit.Outcome));
        Assert.Contains("\"parents\":\"5\"", audit.Payload, StringComparison.Ordinal);
        Assert.Contains("\"conflicts\":\"3\"", audit.Payload, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Impact.AnalyzeAsync(Alice, ecn, 0, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Impact.AnalyzeAsync(Alice, ecn, 11, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Impact.AnalyzeAsync(Alice, "ECN-999999", 3, null)).Kind);
        // Level 1 shows only direct parents.
        var shallow = (await env.Impact.AnalyzeAsync(Alice, ecn, 1, null)).Value!;
        Assert.DoesNotContain(shallow.Objects.Single(o => o.Key == c1).WhereUsed, w => w.Level > 1);
    }

    [Fact]
    public async Task A_change_request_is_analysed_before_any_revision_exists_and_its_own_notice_is_not_a_conflict()
    {
        var env = await ChangeEnv.CreateAsync(pg, documents: true);
        var part = await env.ReleasedPartAsync();
        var ecr = (await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Plan", cls: ChangeClasses.II)).Number;
        await env.AddAsync(Alice, ecr, part, ChangeActions.Revise);
        var report = (await env.Impact.AnalyzeAsync(Alice, ecr, 3, null)).Value!;
        var row = Assert.Single(report.Objects);
        Assert.Equal((part, "revise", null), (row.Key, row.Action, row.Compare));
        Assert.Contains("created when this request becomes a change notice", row.CompareNote, StringComparison.Ordinal);
        Assert.Empty(report.Conflicts);

        // The notice made from the request lists the same part, and the request lists it too: neither is a conflict of the other.
        var ecn = (await env.Change.CreateFromAsync(Alice, ecr, ChangeTypes.Ecn, new CreateFromCommand(), null)).Value!.Number;
        Assert.Empty((await env.Impact.AnalyzeAsync(Alice, ecn, 3, null)).Value!.Conflicts);
        Assert.Empty((await env.Impact.AnalyzeAsync(Alice, ecr, 3, null)).Value!.Conflicts);

        // The "changes affecting this part" query shows both open changes, and the notice the new revision is bound to.
        var changes = (await env.Change.ChangesForObjectAsync(Alice, "part", part, null, null)).Value!;
        Assert.Equal(new[] { ecr, ecn }.Order(), changes.Open.Select(c => c.Number).Order());
        Assert.Equal(ecn, changes.BoundTo!.Number);
        var released = (await env.Change.ChangesForObjectAsync(Alice, "part", part, "A", null)).Value!;
        Assert.Null(released.BoundTo);
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.ChangesForObjectAsync(ChangeEnv.Eve, "part", part, null, null)).Kind);
    }
}
