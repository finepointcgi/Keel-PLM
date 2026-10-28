using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;

namespace Keel.IntegrationTests;

/// <summary>
/// The Phase 1 definition of done on a real database, at service level: an ITAR part with a document and a multi-level BOM goes through
/// review with e-signatures to Released; an ECN revises it (impact analysis shows where it is used); the ECN is released through its
/// workflow; and the audit log holds every step as an intact chain.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase1VerticalSliceTests(PostgresFixture pg)
{
    [Fact]
    public async Task Part_document_bom_review_release_change_and_release_of_the_change_leave_a_complete_verifiable_audit_trail()
    {
        var env = await ChangeEnv.CreateAsync(pg, documents: true);
        var alice = ChangeEnv.Alice;
        var itar = ChangeEnv.Itar();

        // 1. A part with ITAR markings (the one that will be released and changed).
        var created = await env.Parts.CreateAsync(alice, new CreatePartCommand("Gimbal bracket", "Component", "EA", "Make", itar, "Holds the sensor gimbal",
            new JsonObject { ["material"] = "7075-T6", ["mass"] = 0.31, ["critical_item"] = true }), "slice-1");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var part = created.Value!.Number;
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", created.Value.Banner);

        // 2. A document is attached to it (a drawing that describes it, through the part's working copy).
        var drawing = await env.Docs!.CreateAsync(alice, new CreateDocumentCommand("Gimbal bracket drawing", "Drawing", itar), ChangeEnv.File("gimbal.dwg"), "slice-2");
        Assert.True(drawing.IsOk, string.Join("; ", drawing.Errors));
        Assert.True((await env.Parts.CheckOutAsync(alice, part, "slice-2")).IsOk);
        var linked = await env.Docs.AddLinkToWorkingCopyAsync(alice, part, drawing.Value!.Number, "describes", null, "slice-2");
        Assert.True(linked.IsOk, string.Join("; ", linked.Errors));
        Assert.True((await env.Parts.CheckInAsync(alice, part, "Attach drawing", "slice-2")).IsOk);

        // 3. A multi-level BOM: top assembly -> module -> the part.
        var module = await env.NewPartAsync("Gimbal module", itar, type: "Assembly");
        var top = await env.NewPartAsync("Sensor head", itar, type: "Assembly");
        foreach (var (parent, child) in new[] { (module, part), (top, module) })
        {
            Assert.True((await env.Parts.CheckOutAsync(alice, parent, "slice-3")).IsOk);
            var line = await env.Bom.AddUsageAsync(alice, parent, new AddUsageCommand(child, 2, "EA", 10), "slice-3");
            Assert.True(line.IsOk, string.Join("; ", line.Errors));
            Assert.True((await env.Parts.CheckInAsync(alice, parent, "structure", "slice-3")).IsOk);
        }
        var explosion = (await env.Bom.ExplodeAsync(alice, new BomQuery(top), "slice-3")).Value!;
        Assert.Equal([(1, module), (2, part)], explosion.Lines.Select(l => (l.Level, l.Number!)));

        // 4. Review with e-signatures: technical review, then both approvers sign (step-up fresh), and the part is Released.
        var review = await env.Flow.StartAsync(alice, WorkflowEnv.Start(part, "rita", "bob", "carol"), "slice-4");
        Assert.True(review.IsOk, string.Join("; ", review.Errors));
        var signed = await env.ApproveAllAsync(review.Value!);
        Assert.Equal("Approved", signed.Status);
        Assert.Equal(3, signed.Signatures.Count);
        Assert.All(signed.Signatures, s => Assert.StartsWith("Signed by ", s.Manifestation, StringComparison.Ordinal));
        var released = await env.PartAsync(part);
        Assert.Equal(("A", "Released", "A.2"), (released.Revision.Revision, released.Revision.State, released.Iteration.Label));
        Assert.All((await env.Flow.VerifyInstanceAsync(alice, signed.Id, null)).Value!, v => Assert.True(v.Valid));

        // 5. An ECN revises the released part; the impact analysis shows where it is used and which drawing describes it.
        var ecn = await env.NewChangeAsync(alice, ChangeTypes.Ecn, "Increase wall thickness", itar, ChangeClasses.II, priority: "High");
        var withPart = await env.AddAsync(alice, ecn.Number, part);
        Assert.Equal("B", withPart.Affected.Single().ResultingRevision);
        var impact = (await env.Impact.AnalyzeAsync(alice, ecn.Number, 5, "slice-5")).Value!;
        var row = Assert.Single(impact.Objects);
        Assert.Equal([(module, 1), (top, 2)], row.WhereUsed.Select(w => (w.ParentKey!, w.Level)).OrderBy(w => w.Item2));
        Assert.Equal(drawing.Value.Number, Assert.Single(row.Documents).Number);
        Assert.Equal(0, impact.Summary.OpenConflicts);
        await env.CheckedInEditAsync(alice, part, "Wall thickness increased from 2 mm to 3 mm", "slice-5");

        // 6. The ECN is released through its workflow: two approvers sign, the new revision is Released and the old one Obsolete, atomically.
        var ecnReview = await env.SubmitAsync(ecn.Number);
        var ecnDone = await env.ApproveAllAsync(ecnReview);
        Assert.Equal("Approved", ecnDone.Status);
        Assert.Equal("Released", (await env.ChangeAsync(ecn.Number)).State);
        var revisionB = await env.PartAsync(part);
        Assert.Equal(("B", "Released", "B.2"), (revisionB.Revision.Revision, revisionB.Revision.State, revisionB.Iteration.Label));
        Assert.Equal("Obsolete", (await env.PartAsync(part, "A")).Revision.State);
        Assert.All((await env.Flow.VerifyInstanceAsync(alice, ecnDone.Id, null)).Value!, v => Assert.True(v.Valid));
        // The links and the structure came along into the new revision.
        Assert.Equal(drawing.Value.Number, Assert.Single((await env.Docs.GetPartLinksAsync(alice, part, "B", null, null)).Value!.Links).Document!.Number);
        Assert.Contains((await env.Bom.WhereUsedAsync(alice, part, 3, null)).Value!.Rows, r => r.ParentNumber == module && r.ParentRevision == "A");

        // 7. The audit log holds every step, in order, and verifies as an intact chain.
        var audit = await env.AuditAsync();
        var actions = audit.Select(e => e.Action).ToList();
        string[] expectedInOrder =
        [
            "part.create", "document.create", "part.checkout", "part.link_document", "part.checkin", "bom.add_usage", "bom.explode",
            "workflow.start", "workflow.sign", "workflow.complete",
            "change.create", "change.add_affected", "change.impact", "change.submit", "workflow.sign", "change.release", "workflow.complete",
        ];
        var cursor = -1;
        foreach (var expected in expectedInOrder)
        {
            var found = actions.FindIndex(cursor + 1, a => a == expected);
            Assert.True(found > cursor, $"'{expected}' is missing from the audit log after position {cursor}: {string.Join(", ", actions.Distinct())}");
            cursor = found;
        }
        Assert.Equal(6, audit.Count(e => e.Action == "workflow.sign" && e.Outcome == AuditOutcome.Success)); // three per release
        Assert.DoesNotContain(audit, e => e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "change.impact" && e.CorrelationId == "slice-5" && e.Payload.Contains("\"parents\":\"2\"", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == "change.release" && e.ObjectId == ecn.Number && e.Payload.Contains($"{part} B", StringComparison.Ordinal));
        var verdict = await env.VerifyAuditChainAsync();
        Assert.True(verdict.Valid, string.Join("; ", verdict.Failures));
        Assert.Equal(audit.Count, verdict.EventsChecked);
        Assert.Equal(audit.Count, audit[^1].Seq);
    }
}
