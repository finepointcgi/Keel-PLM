using Keel.Audit;
using Keel.Bom;
using Keel.Core;

namespace Keel.IntegrationTests;

/// <summary>Generating an MBOM from an EBOM (1:1, multi-level, by configuration spec) and regenerating it after the EBOM changed without overwriting manufacturing edits.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomGenerateTests(PostgresFixture pg)
{
    [Fact]
    public async Task Generate_copies_every_ebom_line_with_a_matched_one_to_one_link_and_records_the_derivation()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var d = await env.GenerateAsync(p.Top);
        Assert.Equal(("A", 1, "InWork", "A.2"), (d.Summary.Revision, d.Summary.Iteration, d.Summary.State, d.Summary.DerivedFrom));
        Assert.Equal("Latest released (" + p.Top + " A.2)", d.Iteration.DerivedSpec);

        var lines = (await env.LinesAsync(d.Summary.Number)).Lines;
        Assert.Equal([(10, p.Screw, 4m, "Buy"), (20, p.Plate, 1m, "Make"), (30, p.Sub, 2m, "Make")], lines.Select(l => (l.FindNumber, l.ChildNumber!, l.Quantity, l.SupplyType!)).ToArray());
        Assert.All(lines, l =>
        {
            var link = Assert.Single(l.Links);
            Assert.Equal((LinkStatuses.Matched, null), (link.Status, link.Reason));
            Assert.Equal(l.FindNumber, link.EbomFindNumber);
            Assert.False(l.MbomOnly);
        });
        Assert.Equal((3, 3), (d.Iteration.LineCount, d.Iteration.LinkCount));
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.generate" && e.Outcome == AuditOutcome.Success && e.ObjectId == d.Summary.Number);

        var again = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, MbomEnv.Plant), "c");
        Assert.Equal(OutcomeKind.Conflict, again.Kind);
        Assert.Contains("regenerate", again.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_follows_the_configuration_spec_and_refuses_an_unreleased_ebom_under_LatestReleased()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var plate = await env.C.NewPartAsync("Plate");
        var top = await env.C.AssemblyAsync("Draft assembly", (plate, 2));
        var refused = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, MbomEnv.Plant), "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("no released revision", refused.Errors[0], StringComparison.Ordinal);
        var latest = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, MbomEnv.Plant, new EbomSpec(EbomSpecKind.Latest)), "c");
        Assert.True(latest.IsOk, string.Join("; ", latest.Errors));
        Assert.Equal("Latest (" + top + " A.2)", latest.Value!.Items[0].DerivedFrom);
        Assert.True((await env.Mbom.SavePlantAsync(MbomEnv.Ada, new SavePlantCommand("PLANT-C", "Third plant (synthetic)"), "c")).IsOk);
        var explicitIteration = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, "PLANT-B", new EbomSpec(EbomSpecKind.Iteration, IterationLabel: "A.1")), "c");
        Assert.True(explicitIteration.IsOk);
        Assert.Equal(0, explicitIteration.Value!.Items[0].Lines); // A.1 had no structure yet
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, "PLANT-C", new EbomSpec(EbomSpecKind.Baseline, Baseline: "BL-000001")), "c")).Kind);
    }

    [Fact]
    public async Task Generate_can_take_the_ebom_from_a_baseline_and_from_an_effectivity_unit()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var baseline = await env.C.BaselineAsync(p.Top, "Design", BomConfigSpec.Latest);
        // The EBOM moves on after the baseline.
        await env.ReviseAsync(p.Top, () => env.SetEbomQuantityAsync(p.Top, 10, 9));
        var fromBaseline = await env.GenerateAsync(p.Top, spec: new EbomSpec(EbomSpecKind.Baseline, Baseline: baseline.Summary.Number));
        Assert.Equal("A.2", fromBaseline.Summary.DerivedFrom);
        Assert.Equal(4m, (await env.LinesAsync(fromBaseline.Summary.Number)).Lines.Single(l => l.FindNumber == 10).Quantity);
        var latestReleased = await env.GenerateAsync(p.Top, "PLANT-B");
        Assert.StartsWith("B.", latestReleased.Summary.DerivedFrom, StringComparison.Ordinal);
        Assert.Equal(9m, (await env.LinesAsync(latestReleased.Summary.Number)).Lines.Single(l => l.FindNumber == 10).Quantity);
    }

    [Fact]
    public async Task Multi_level_generate_creates_an_mbom_for_each_make_subassembly_and_keeps_buy_items_as_leaves()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var result = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, MbomEnv.Plant, null, MultiLevel: true), "c");
        Assert.True(result.IsOk, string.Join("; ", result.Errors));
        Assert.Equal([p.Top, p.Sub], result.Value!.Items.Select(i => i.PartNumber).ToArray());
        Assert.All(result.Value.Items, i => Assert.True(i.Created));
        Assert.Equal(1, result.Value.Items[1].Lines);
        // No MBOM exists for the bought screw or the plain plate.
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM mbom.mbom"));

        // Regenerating one level later keeps the existing MBOM of the plant and says so.
        var again = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, "PLANT-B", null, MultiLevel: true), "c");
        Assert.True(again.IsOk);
        Assert.Equal(4, await env.ScalarAsync("SELECT count(*) FROM mbom.mbom"));
        var onlyChild = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, MbomEnv.Plant, null, MultiLevel: true), "c");
        Assert.Equal(OutcomeKind.Conflict, onlyChild.Kind); // the root already has one in this plant

        // The top MBOM now reconciles against the sub-assembly's MBOM: unreleased is a warning, released is clean.
        var top = result.Value.Items[0].MbomNumber;
        var r = await env.ReconcileAsync(top);
        Assert.Equal(ReconcileStatuses.WithExceptions, r.Run.Status);
        Assert.Equal([FindingCodes.ChildMbomUnreleased], MbomEnv.Codes(r));
    }

    [Fact]
    public async Task Generate_refuses_when_the_ebom_holds_parts_the_caller_cannot_read()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var secret = await env.C.NewPartAsync("Stealth rotor", "Component", ConfigEnv.Itar());
        var plain = await env.BuyPartAsync("Washer");
        var top = await env.C.NewPartAsync("Mixed assembly", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, plain, 2);
        await env.C.AddLineAsync(top, secret, 1);
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);

        var asEve = await env.Mbom.GenerateAsync(MbomEnv.Eve, new GenerateMbomCommand(top, MbomEnv.Plant), "c");
        Assert.Equal(OutcomeKind.Invalid, asEve.Kind);
        Assert.Contains("not authorized to see", asEve.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(secret, asEve.Errors[0], StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.generate" && e.Outcome == AuditOutcome.Denied);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM mbom.mbom"));
    }

    // ---------------------------------------------------------------- regeneration

    [Fact]
    public async Task Regeneration_proposes_changes_and_never_overwrites_manufacturing_edits()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var d = await env.GenerateAsync(p.Top);
        var number = d.Summary.Number;

        // Manufacturing edits: a declared quantity adjustment on find 10, and an MBOM-only consumable.
        var glue = await env.C.Parts.CreateAsync(MbomEnv.Alice, new Keel.Parts.CreatePartCommand("Adhesive", "Consumable", "mL", "Buy", ConfigEnv.Open), null);
        await env.CheckOutAsync(number);
        await env.UpdateLineAsync(number, 10, c => c with { Quantity = 6 });
        var screwLine = await env.WorkingLineAsync(number, 10);
        Assert.True((await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, screwLine.Id, [new LinkInput(screwLine.Links[0].EbomUsageId, LinkStatuses.QuantityAdjusted, "Two spares per kit")], "c")).IsOk);
        var added = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(glue.Value!.Number, 5, "mL", 900, MbomOnly: true, SupplyType: "Consume", OperationReference: "OP-90"), "c");
        Assert.True(added.IsOk, string.Join("; ", added.Errors));
        await env.CheckInAsync(number);

        // The EBOM changes in revision B: screw 4 to 5, plate 1 to 2, a new bracket line.
        await env.ReviseAsync(p.Top, async () =>
        {
            await env.SetEbomQuantityAsync(p.Top, 10, 5);
            await env.SetEbomQuantityAsync(p.Top, 20, 2);
            await env.C.AddLineAsync(p.Top, p.Bracket, 3, 40);
        });

        var proposal = (await env.Mbom.ProposeRegenerationAsync(MbomEnv.Alice, number, null, "c")).Value!;
        Assert.True(proposal.EbomChanged);
        Assert.Equal((1, 3), (proposal.PreservedManualLines, proposal.RelinkedLines));
        Assert.Equal(
            new (string, int?, bool)[] { (ProposalKinds.ReviewAdjusted, 10, false), (ProposalKinds.ChangeQuantity, 20, true), (ProposalKinds.AddLine, null, true) },
            proposal.Items.Select(i => (i.Kind, i.MbomFind, i.Actionable)).ToArray());
        Assert.Contains(proposal.Items, i => i.Kind == ProposalKinds.ReviewAdjusted && i.Summary.Contains("EBOM quantity 4 to 5", StringComparison.Ordinal) && i.Summary.Contains("left as it is", StringComparison.Ordinal));

        // Nothing changed yet: the proposal is read-only.
        Assert.Equal(2, (await env.GetAsync(number)).Summary.Iteration);

        var applied = await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, number, null, null, "c");
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        Assert.Equal((true, "A.3", 2, 0), (applied.Value!.CreatedIteration, applied.Value.Label, applied.Value.Applied, applied.Value.Skipped));
        Assert.Single(applied.Value.Notices);

        var d3 = await env.GetAsync(number);
        Assert.Equal(("A.3", true), (d3.Summary.Revision + "." + d3.Summary.Iteration, d3.Summary.DerivedFrom!.StartsWith("B.", StringComparison.Ordinal)));
        var lines = (await env.LinesAsync(number)).Lines;
        Assert.Equal(6m, lines.Single(l => l.FindNumber == 10).Quantity);                       // manufacturing adjustment kept
        Assert.Equal(LinkStatuses.QuantityAdjusted, lines.Single(l => l.FindNumber == 10).Links[0].Status);
        Assert.Equal(2m, lines.Single(l => l.FindNumber == 20).Quantity);                       // accepted change
        Assert.Equal((p.Bracket, 3m), (lines.Single(l => l.FindNumber == 40).ChildNumber!, lines.Single(l => l.FindNumber == 40).Quantity)); // accepted addition
        var manual = lines.Single(l => l.FindNumber == 900);                                    // manual line preserved
        Assert.Equal((true, "OP-90", "Consume"), (manual.MbomOnly, manual.OperationReference, manual.SupplyType));
        Assert.Empty(manual.Links);

        // Reconciled against the new EBOM, only declared exceptions remain (the sub-assembly MBOM is missing, a real error).
        var report = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.NotReconciled, report.Run.Status);
        Assert.Equal([FindingCodes.ChildMbomMissing, FindingCodes.MbomOnlyLine, FindingCodes.QuantityAdjusted], MbomEnv.Codes(report).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Idempotent: the MBOM is already derived from this EBOM and nothing is left to accept.
        var twice = await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, number, null, null, "c");
        Assert.False(twice.Value!.CreatedIteration);
        Assert.Equal(3, (await env.GetAsync(number)).Summary.Iteration);
        Assert.DoesNotContain((await env.Mbom.ProposeRegenerationAsync(MbomEnv.Alice, number, null, "c")).Value!.Items, i => i.Actionable);
    }

    [Fact]
    public async Task Regeneration_can_be_accepted_line_by_line_and_unaccepted_changes_show_up_as_findings()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var d = await env.GenerateAsync(p.Top);
        await env.ReviseAsync(p.Top, async () =>
        {
            await env.SetEbomQuantityAsync(p.Top, 20, 2);
            await env.C.AddLineAsync(p.Top, p.Bracket, 3, 40);
        });
        var proposal = (await env.Mbom.ProposeRegenerationAsync(MbomEnv.Alice, d.Summary.Number, null, "c")).Value!;
        var add = proposal.Items.Single(i => i.Kind == ProposalKinds.AddLine);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, d.Summary.Number, null, new HashSet<string> { "add:nonexistent" }, "c")).Kind);
        var applied = await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, d.Summary.Number, null, new HashSet<string> { add.Id }, "c");
        Assert.Equal((1, 1), (applied.Value!.Applied, applied.Value.Skipped));
        var lines = (await env.LinesAsync(d.Summary.Number)).Lines;
        Assert.Equal(1m, lines.Single(l => l.FindNumber == 20).Quantity);  // the plate change was not accepted
        Assert.Equal(3m, lines.Single(l => l.FindNumber == 40).Quantity);
        var report = await env.ReconcileAsync(d.Summary.Number);
        var diff = report.Findings.Single(f => f.Code == FindingCodes.QuantityDifference);
        Assert.Equal((20, 2m, 1m), (diff.MbomFind, diff.Expected, diff.Actual));
        // The EBOM changes since the derivation are listed from the BOM comparison.
        Assert.Empty(report.EbomChangesSinceDerivation);
    }

    [Fact]
    public async Task Regeneration_needs_an_unlocked_mbom_and_does_not_reveal_restricted_parts()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        await env.ReleaseAsync(number);
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, number, null, null, "c")).Kind);

        var secret = await env.C.NewPartAsync("Stealth rotor", "Component", ConfigEnv.Itar());
        var top = await env.C.NewPartAsync("Mixed assembly", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, p.Screw, 2);
        await env.C.AddLineAsync(top, secret, 1);
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);
        var mbom = await env.GenerateAsync(top, "PLANT-B");
        var proposal = await env.Mbom.ProposeRegenerationAsync(MbomEnv.Eve, mbom.Summary.Number, null, "c");
        Assert.Equal(OutcomeKind.Invalid, proposal.Kind);
        Assert.DoesNotContain(secret, proposal.Errors[0], StringComparison.Ordinal);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "mbom.regenerate_propose" && e.Outcome == AuditOutcome.Failure);
    }
}
