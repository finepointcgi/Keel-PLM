using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Link semantics and every reconciliation finding type, end to end through the service and the database (ADR-0024).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomReconcileTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_generated_and_reconciled_mbom_is_clean_with_full_coverage()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var report = (await env.Mbom.GetReconciliationAsync(MbomEnv.Alice, number, null, "c")).Value!;
        Assert.Equal((ReconcileStatuses.Reconciled, 3, 3, 3, 100m), (report.Run.Status, report.Run.EbomLines, report.Run.MbomLines, report.Run.CoveredLines, report.Run.CoveragePct));
        Assert.Empty(report.Findings);
        Assert.True(report.Run.IntegrityOk);
        Assert.Equal(("A.1", "A.2"), (report.Run.MbomRevision + "." + report.Run.MbomIteration, report.Run.EbomLabel));
        Assert.Equal("Latest released (" + p.Top + " A.2)", report.Run.Spec);
        var brief = (await env.GetAsync(number)).LastRun!;
        Assert.True(brief.FreshForRelease);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.reconcile" && e.Outcome == AuditOutcome.Success && e.ObjectId == number);
    }

    [Fact]
    public async Task Splits_merges_and_substitutions_are_declared_through_links_and_reconciled_as_groups()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var altScrew = await env.BuyPartAsync("Screw, alternate vendor");
        await env.CheckOutAsync(number);

        // Split: the four screws are installed at two stations.
        var screw = await env.WorkingLineAsync(number, 10);
        await env.UpdateLineAsync(number, 10, c => c with { Quantity = 2 });
        var second = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 2, FindNumber: 15, OperationReference: "OP-20"), "c");
        Assert.True((await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, second.Value!.Id, [new LinkInput(screw.Links[0].EbomUsageId)], "c")).IsOk);
        await env.CheckInAsync(number);
        var split = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.Reconciled, split.Run.Status);
        Assert.Equal(4, split.Run.MbomLines);
        Assert.Equal(3, split.Run.EbomLines);

        // Substitution: the plate is built from another vendor's plate; a matched link between different parts is refused when it is set.
        await env.CheckOutAsync(number);
        var plate = await env.WorkingLineAsync(number, 20);
        var ebomPlate = plate.Links[0].EbomUsageId;
        Assert.True((await env.Mbom.RemoveLineAsync(MbomEnv.Alice, number, plate.Id, "c")).IsOk);
        var altPlate = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(altScrew, 1, FindNumber: 20), "c");
        var wrong = await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, altPlate.Value!.Id, [new LinkInput(ebomPlate)], "c");
        Assert.Equal(OutcomeKind.Invalid, wrong.Kind);
        Assert.Contains("declare a substitution", wrong.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, altPlate.Value.Id, [new LinkInput(ebomPlate, LinkStatuses.Substituted)], "c")).Kind); // reason needed
        var ok = await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, altPlate.Value.Id, [new LinkInput(ebomPlate, LinkStatuses.Substituted, "Alternate vendor, approved deviation 17")], "c");
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        await env.CheckInAsync(number);
        var substituted = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.WithExceptions, substituted.Run.Status);
        var f = Assert.Single(substituted.Findings);
        Assert.Equal((FindingCodes.Substitution, FindingSeverities.Warning, altScrew, "Alternate vendor, approved deviation 17"), (f.Code, f.Severity, f.ChildNumber, f.Detail));
        Assert.Equal((20, 20), (f.EbomFind, f.MbomFind));
    }

    [Fact]
    public async Task Link_validation_refuses_foreign_lines_mbom_only_lines_and_missing_reasons()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var other = await env.GenerateAsync(p.Sub, "PLANT-B");
        await env.CheckOutAsync(number);
        var line = await env.WorkingLineAsync(number, 10);
        var foreign = (await env.Mbom.GetLinesAsync(MbomEnv.Alice, other.Summary.Number, null, null, "c")).Value!.Lines[0].Links[0].EbomUsageId;
        var r = await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, line.Id, [new LinkInput(foreign)], "c");
        Assert.Equal(OutcomeKind.Invalid, r.Kind);
        Assert.Contains("not part of the EBOM it is derived from", r.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, line.Id, [new LinkInput(line.Links[0].EbomUsageId, "approximately")], "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, line.Id, [new LinkInput(line.Links[0].EbomUsageId), new LinkInput(line.Links[0].EbomUsageId)], "c")).Kind);
        // A line flagged MBOM-only drops its links, and cannot take new ones.
        var flagged = await env.UpdateLineAsync(number, 10, c => c with { MbomOnly = true });
        Assert.Empty(flagged.Links);
        var again = await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, line.Id, [new LinkInput(line.Links[0].EbomUsageId)], "c");
        Assert.Equal(OutcomeKind.Invalid, again.Kind);
        Assert.Contains("MBOM-only", again.Errors[0], StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.set_links" && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task Missing_unexplained_and_mbom_only_lines_quantities_and_scrap_have_exact_findings()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var glue = (await env.C.Parts.CreateAsync(MbomEnv.Alice, new Keel.Parts.CreatePartCommand("Adhesive", "Consumable", "mL", "Buy", ConfigEnv.Open), null)).Value!.Number;
        await env.CheckOutAsync(number);
        await env.UpdateLineAsync(number, 10, c => c with { ScrapFactor = 0.05m });           // scrap alone is never a difference
        await env.UpdateLineAsync(number, 20, c => c with { Quantity = 3 });                  // 1 expected, 3 present
        Assert.True((await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(glue, 5, "mL", 900, MbomOnly: true, SupplyType: "Consume"), "c")).IsOk);
        Assert.True((await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Bracket, 1, FindNumber: 910), "c")).IsOk); // no source, not flagged
        Assert.True((await env.Mbom.RemoveLineAsync(MbomEnv.Alice, number, (await env.WorkingLineAsync(number, 30)).Id, "c")).IsOk);   // the sub-assembly is gone
        await env.CheckInAsync(number);
        var r = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.NotReconciled, r.Run.Status);
        Assert.Equal((3, 4, 2, 66.67m), (r.Run.EbomLines, r.Run.MbomLines, r.Run.CoveredLines, r.Run.CoveragePct));
        Assert.Equal(
            new (string, string, int?, int?)[]
            {
                (FindingCodes.EbomLineMissing, FindingSeverities.Error, 30, null), (FindingCodes.MbomLineUnexplained, FindingSeverities.Error, null, 910),
                (FindingCodes.QuantityDifference, FindingSeverities.Error, 20, 20), (FindingCodes.MbomOnlyLine, FindingSeverities.Info, null, 900),
            },
            r.Findings.Select(f => (f.Code, f.Severity, f.EbomFind, f.MbomFind)).ToArray());
        var qty = r.Findings.Single(f => f.Code == FindingCodes.QuantityDifference);
        Assert.Equal((1m, 3m, "EA", "Expected 1 EA (EBOM), the MBOM has 3."), (qty.Expected, qty.Actual, qty.Unit, qty.Detail));
        Assert.Equal((3, 0, 1), (r.Run.Errors, r.Run.Warnings, r.Run.Infos));
    }

    [Fact]
    public async Task Effectivity_gaps_and_excess_are_reported_with_the_units_involved()
    {
        var env = await MbomEnv.CreateAsync(pg);
        await env.C.NewContextAsync("ORCA-POD");
        var screw = await env.BuyPartAsync("Screw");
        var top = await env.C.NewPartAsync("Pod assembly", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, screw, 4, 10, ConfigEnv.Serial("ORCA-POD", 1, 100));
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);
        var number = (await env.GenerateAsync(top)).Summary.Number;
        Assert.Equal(ReconcileStatuses.Reconciled, (await env.ReconcileAsync(number)).Run.Status);

        await env.CheckOutAsync(number);
        await env.UpdateLineAsync(number, 10, c => c with { Effectivity = ConfigEnv.Serial("ORCA-POD", 1, 80) });
        await env.CheckInAsync(number);
        var gap = await env.ReconcileAsync(number);
        var f = Assert.Single(gap.Findings);
        Assert.Equal((FindingCodes.EffectivityGap, FindingSeverities.Error), (f.Code, f.Severity));
        Assert.Equal("EBOM applies to serial 1-100 (ORCA-POD); the MBOM to serial 1-80 (ORCA-POD). Not covered by the MBOM: serial 81-100 (ORCA-POD).", f.Detail);

        await env.CheckOutAsync(number);
        await env.UpdateLineAsync(number, 10, c => c with { Effectivity = ConfigEnv.Serial("ORCA-POD", 1, 120) });
        await env.CheckInAsync(number);
        var excess = Assert.Single((await env.ReconcileAsync(number)).Findings);
        Assert.Equal((FindingCodes.EffectivityExcess, FindingSeverities.Warning), (excess.Code, excess.Severity));
        Assert.Equal("EBOM applies to serial 1-100 (ORCA-POD); the MBOM to serial 1-120 (ORCA-POD). Covered by the MBOM beyond the EBOM: serial 101-120 (ORCA-POD).", excess.Detail);
    }

    [Fact]
    public async Task An_ebom_revised_in_an_ecn_makes_the_mbom_stale_and_the_report_lists_the_changes_since_derivation()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        Assert.Equal(ReconcileStatuses.Reconciled, (await env.Mbom.GetReconciliationAsync(MbomEnv.Alice, number, null, "c")).Value!.Run.Status);
        Assert.True((await env.GetAsync(number)).LastRun!.FreshForRelease);

        await env.ReviseAsync(p.Top, () => env.SetEbomQuantityAsync(p.Top, 10, 6));
        // The EBOM the earlier run used is no longer what its spec selects: the run is not fresh any more.
        var brief = (await env.GetAsync(number)).LastRun!;
        Assert.False(brief.FreshForRelease);
        Assert.Contains("changed since the reconciliation", brief.NotFreshReason, StringComparison.Ordinal);
        Assert.Contains("changed since the reconciliation", (await env.GetAsync(number)).ReleaseBlocker, StringComparison.Ordinal);

        var report = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.NotReconciled, report.Run.Status); // drift plus the quantity change
        Assert.Contains(FindingCodes.RevisionDrift, MbomEnv.Codes(report));
        var drift = report.Findings.Single(f => f.Code == FindingCodes.RevisionDrift);
        Assert.Contains("derived from EBOM A.2", drift.Detail, StringComparison.Ordinal);
        Assert.StartsWith("B.", report.Run.EbomLabel, StringComparison.Ordinal);
        var change = Assert.Single(report.EbomChangesSinceDerivation);
        Assert.Equal((BomChangeKind.Changed, p.Screw, "Quantity 4 to 6"), (change.Kind, change.Number, change.Changes.Single()));

        // Drift alone is Stale: regenerate nothing structural, reconcile against the derived iteration to prove the lines agree.
        var explicitA = await env.ReconcileAsync(number, new EbomSpec(EbomSpecKind.Iteration, IterationLabel: "A.2"));
        Assert.Equal(ReconcileStatuses.Reconciled, explicitA.Run.Status);
        var history = (await env.Mbom.GetReconciliationHistoryAsync(MbomEnv.Alice, number, 10, "c")).Value!;
        Assert.Equal(3, history.Runs.Count);
        Assert.True(history.Runs[0].RunAt >= history.Runs[1].RunAt);
        Assert.All(history.Runs, r => Assert.True(r.IntegrityOk));
    }

    [Fact]
    public async Task Drift_without_a_structural_change_is_stale_and_regenerating_clears_it()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        await env.ReviseAsync(p.Top, () => Task.CompletedTask);
        var stale = await env.ReconcileAsync(number);
        Assert.Equal(ReconcileStatuses.Stale, stale.Run.Status);
        Assert.Equal([FindingCodes.RevisionDrift], MbomEnv.Codes(stale));
        Assert.Empty(stale.EbomChangesSinceDerivation);
        // Regeneration re-derives from the new EBOM with nothing to review.
        var applied = await env.Mbom.ApplyRegenerationAsync(MbomEnv.Alice, number, null, null, "c");
        Assert.True(applied.Value!.CreatedIteration);
        Assert.Equal(ReconcileStatuses.Reconciled, (await env.ReconcileAsync(number)).Run.Status);
    }

    [Fact]
    public async Task Make_subassemblies_need_a_released_mbom_in_the_same_plant()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var top = (await env.GenerateAsync(p.Top)).Summary.Number;
        var missing = await env.ReconcileAsync(top);
        Assert.Equal([FindingCodes.ChildMbomMissing], MbomEnv.Codes(missing));
        Assert.Equal((30, p.Sub), (missing.Findings[0].MbomFind, missing.Findings[0].ChildNumber));

        // An MBOM of the sub-assembly in another plant does not help.
        await env.GenerateAsync(p.Sub, "PLANT-B");
        Assert.Equal([FindingCodes.ChildMbomMissing], MbomEnv.Codes(await env.ReconcileAsync(top)));
        var sub = await env.GenerateAsync(p.Sub);
        Assert.Equal([FindingCodes.ChildMbomUnreleased], MbomEnv.Codes(await env.ReconcileAsync(top)));
        await env.ReconcileAsync(sub.Summary.Number);
        await env.ReleaseAsync(sub.Summary.Number);
        Assert.Equal(ReconcileStatuses.Reconciled, (await env.ReconcileAsync(top)).Run.Status);
        // A part bought instead of made (supply type Buy) stays a leaf and needs no MBOM.
        await env.CheckOutAsync(top);
        await env.UpdateLineAsync(top, 30, c => c with { SupplyType = "Buy" });
        await env.CheckInAsync(top);
        Assert.Equal(ReconcileStatuses.Reconciled, (await env.ReconcileAsync(top)).Run.Status);
    }

    [Fact]
    public async Task Findings_about_parts_the_reader_cannot_see_keep_only_codes_find_numbers_and_quantities()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var secret = await env.C.NewPartAsync("Stealth rotor", "Component", ConfigEnv.Itar());
        var plain = await env.BuyPartAsync("Washer");
        var top = await env.C.NewPartAsync("Mixed assembly", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, plain, 2);
        await env.C.AddLineAsync(top, secret, 3);
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);
        var number = (await env.GenerateAsync(top)).Summary.Number;
        await env.CheckOutAsync(number);
        await env.UpdateLineAsync(number, 20, c => c with { Quantity = 1, Notes = "rotor change" });
        await env.UpdateLineAsync(number, 10, c => c with { Quantity = 5 });
        await env.CheckInAsync(number);
        var asAlice = await env.ReconcileAsync(number);
        Assert.Equal(2, asAlice.Findings.Count);
        Assert.Contains(asAlice.Findings, f => f.ChildNumber == secret && f.Detail == "Expected 3 EA (EBOM), the MBOM has 1.");

        var asEve = (await env.Mbom.GetReconciliationAsync(MbomEnv.Eve, number, asAlice.Run.Id, "c")).Value!;
        Assert.Equal(1, asEve.Run.RedactedFindings);
        var hidden = asEve.Findings.Single(f => f.Redacted);
        Assert.Equal((FindingCodes.QuantityDifference, 20, 3m, 1m), (hidden.Code, hidden.MbomFind, hidden.Expected, hidden.Actual));
        Assert.All([hidden.ChildNumber, hidden.ChildName, hidden.Detail], Assert.Null);
        var visible = asEve.Findings.Single(f => !f.Redacted);
        Assert.Equal(plain, visible.ChildNumber);
        Assert.DoesNotContain("ITAR", asEve.Banner, StringComparison.Ordinal);
        Assert.Contains("ITAR", asAlice.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_run_fails_its_integrity_check_and_blocks_the_review()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, number) = await env.ReconciledProductAsync();
        var run = (await env.Mbom.GetReconciliationAsync(MbomEnv.Alice, number, null, "c")).Value!.Run;
        Assert.True(run.IntegrityOk);
        await env.Db.ExecuteAsSuperuserAsync($"SET session_replication_role = replica; UPDATE mbom.reconciliation_run SET infos = 7 WHERE id = '{run.Id}'");
        Assert.False((await env.Mbom.GetReconciliationAsync(MbomEnv.Alice, number, run.Id, "c")).Value!.Run.IntegrityOk);
        var brief = (await env.GetAsync(number)).LastRun!;
        Assert.False(brief.FreshForRelease);
        Assert.Contains("recorded digest", brief.NotFreshReason, StringComparison.Ordinal);
        var submit = await env.SubmitAsync(number);
        Assert.Equal(OutcomeKind.Conflict, submit.Kind);
        Assert.Contains("recorded digest", submit.Errors[0], StringComparison.Ordinal);
        // A new run is a new, valid record, and the history keeps both.
        Assert.True((await env.Mbom.ReconcileAsync(MbomEnv.Alice, number, null, "c")).IsOk);
        Assert.Equal(2, (await env.Mbom.GetReconciliationHistoryAsync(MbomEnv.Alice, number, 10, "c")).Value!.Runs.Count);
        Assert.True((await env.SubmitAsync(number)).IsOk);
    }

    [Fact]
    public async Task Two_users_cannot_edit_one_mbom_and_concurrent_reconciles_serialize_into_distinct_runs()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, number) = await env.ReconciledProductAsync();
        await env.CheckOutAsync(number);
        var rita = ConfigEnv.Rita;
        var racing = await env.Mbom.CheckOutAsync(rita, number, "c");
        Assert.Equal(OutcomeKind.Conflict, racing.Kind);
        Assert.Contains("Already checked out", racing.Errors[0], StringComparison.Ordinal);
        var edit = await env.Mbom.AddLineAsync(rita, number, new AddMbomLineCommand("P-000001", 1), "c");
        Assert.Equal(OutcomeKind.Conflict, edit.Kind);
        Assert.Contains("only the user who checked it out", edit.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.GetWorkingAsync(rita, number, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.UndoCheckOutAsync(rita, number, "c")).Kind); // not the owner
        await env.Mbom.UndoCheckOutAsync(MbomEnv.Alice, number, "c");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => env.Mbom.ReconcileAsync(i % 2 == 0 ? MbomEnv.Alice : rita, number, null, "c")));
        Assert.All(results, r => Assert.True(r.IsOk, string.Join("; ", r.Errors)));
        Assert.Equal(8, results.Select(r => r.Value!.Run.Id).Distinct().Count());
        Assert.All(results, r => Assert.Equal(ReconcileStatuses.Reconciled, r.Value!.Run.Status));
        Assert.Equal(10, await env.ScalarAsync("SELECT count(*) FROM mbom.reconciliation_run"));
    }

    [Fact]
    public async Task Denials_are_audited_and_hidden_mboms_answer_not_found()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync(ConfigEnv.Itar());
        var made = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, MbomEnv.Plant, null, false, ConfigEnv.Itar()), "c");
        var number = made.Value!.Items[0].MbomNumber;
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ReconcileAsync(MbomEnv.Eve, number, null, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.CheckOutAsync(MbomEnv.Eve, number, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ExplodeAsync(MbomEnv.Eve, new MbomQuery(number), "c")).Kind);
        var viewerOnly = BomTestKit.Viewer;
        Assert.Equal(OutcomeKind.Forbidden, (await env.Mbom.ReconcileAsync(viewerOnly, number, null, "c")).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "mbom.reconcile" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "mbom.checkout" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "mbom.explode" && e.Outcome == AuditOutcome.Denied);
    }
}
