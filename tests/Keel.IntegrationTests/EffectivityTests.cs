using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Effectivity (A19, ADR-0021): line ranges and alternatives, resolution per unit, revision effectivity driven by change notices.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EffectivityTests(PostgresFixture pg)
{
    private const string Ctx = "ORCA-POD";

    /// <summary>
    /// Pod (released): find 10 is plug X for serials 1-100 and plug Y (an assembly of a nut) from 101; find 20 is a bracket on every unit.
    /// </summary>
    private static async Task<(ConfigEnv Env, string Pod, string X, string Y, string Nut, string Bracket)> Pod(PostgresFixture pg)
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx, ("Block 1", 1), ("Block 2", 2), ("Block 3", 3));
        var x = await env.ReleasedComponentAsync("Plug X");
        var nut = await env.ReleasedComponentAsync("Nut");
        var y = await env.ReleasedAssemblyAsync("Plug Y", (nut, 2m));
        var bracket = await env.ReleasedComponentAsync("Bracket");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, x, 1m, 10, ConfigEnv.Serial(Ctx, 1, 100));
        await env.AddLineAsync(pod, y, 1m, 10, ConfigEnv.Serial(Ctx, 101, null));
        await env.AddLineAsync(pod, bracket, 2m, 20);
        await env.CheckInAsync(pod);
        await env.ReleaseAsync(pod);
        return (env, pod, x, y, nut, bracket);
    }

    private static EffectivityRequest At(long serial) => EffectivityRequest.ForSerial(Ctx, serial);

    // ---------------------------------------------------------------- validation of ranges

    [Fact]
    public async Task Alternatives_of_one_find_number_must_not_overlap_and_the_messages_say_why()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var x = await env.ReleasedComponentAsync("X");
        var y = await env.ReleasedComponentAsync("Y");
        var z = await env.ReleasedComponentAsync("Z");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, x, 1m, 30, ConfigEnv.Serial(Ctx, 1, 100));

        async Task<Outcome<WorkingBomLine>> Try(string child, int find, EffectivityInput? eff) =>
            await env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(child, 1m, null, find, null, null, eff), "corr-eff");

        var overlap = await Try(y, 30, ConfigEnv.Serial(Ctx, 100, 200));
        Assert.Equal(OutcomeKind.Conflict, overlap.Kind);
        Assert.Contains("overlaps", overlap.Errors[0], StringComparison.Ordinal);
        var everyUnit = await Try(y, 30, null);
        Assert.Equal(OutcomeKind.Conflict, everyUnit.Kind);
        Assert.Contains("needs an effectivity", everyUnit.Errors[0], StringComparison.Ordinal);
        var otherKind = await Try(y, 30, new EffectivityInput("date", null, "2030-01-01", null));
        Assert.Equal(OutcomeKind.Conflict, otherKind.Kind);
        Assert.Contains("same type and context", otherKind.Errors[0], StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-eff" && e.Outcome == AuditOutcome.Failure && e.Action == "bom.add_usage");

        // A line that applies to every unit blocks any alternative at its find number.
        await env.AddLineAsync(pod, z, 1m, 40);
        var blocked = await Try(y, 40, ConfigEnv.Serial(Ctx, 1, 5));
        Assert.Equal(OutcomeKind.Conflict, blocked.Kind);
        Assert.Contains("every unit", blocked.Errors[0], StringComparison.Ordinal);

        // Disjoint ranges are alternatives of find number 30, in any order; open ends are fine.
        Assert.True((await Try(y, 30, ConfigEnv.Serial(Ctx, 101, 200))).IsOk);
        Assert.True((await Try(z, 30, ConfigEnv.Serial(Ctx, 201, null))).IsOk);
        var lines = (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, pod, null)).Value!.Lines;
        Assert.Equal(3, lines.Count(l => l.FindNumber == 30));
        Assert.Equal(4, lines.Count);
        Assert.Contains("serial 201 and up (ORCA-POD)", lines.Select(l => l.EffectivityText));

        // Input is validated before anything is stored.
        foreach (var (input, text) in new (EffectivityInput, string)[]
        {
            (ConfigEnv.Serial(Ctx, 10, 5), "must not be after"),
            (new EffectivityInput("serial", Ctx, "abc", null), "not a serial number"),
            (ConfigEnv.Serial("NO-SUCH", 1, 2), "was not found"),
            (new EffectivityInput("serial", null, "1", "2"), "needs an end-item context"),
            (new EffectivityInput("lot", Ctx, "Block 9", null), "block name"),
            (new EffectivityInput("date", null, "tomorrow", null), "not a date"),
        })
        {
            var r = await Try(x, 50, input);
            Assert.Equal(OutcomeKind.Invalid, r.Kind);
            Assert.Contains(text, r.Errors[0], StringComparison.Ordinal);
        }
        Assert.Equal(4, (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, pod, null)).Value!.Lines.Count);   // nothing from the refused inputs was stored
    }

    [Fact]
    public async Task Updates_and_clearing_are_checked_against_the_other_alternatives_and_designators_may_repeat_across_disjoint_ranges()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var x = await env.ReleasedComponentAsync("X");
        var y = await env.ReleasedComponentAsync("Y");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        var first = await env.AddLineAsync(pod, x, 1m, 30, ConfigEnv.Serial(Ctx, 1, 100));
        // R1 on both alternatives: they never apply together.
        var withRef = await env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(y, 1m, "EA", 30, ["R1"], null, ConfigEnv.Serial(Ctx, 101, 200)), null);
        Assert.True(withRef.IsOk, string.Join("; ", withRef.Errors));
        var third = await env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(y, 1m, "EA", 30, ["R1"], null, ConfigEnv.Serial(Ctx, 201, null)), null);
        Assert.True(third.IsOk, string.Join("; ", third.Errors));   // R1 again: still never together with the others
        var again = await env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(x, 1m, "EA", 40, ["R1"], null, null), null);
        Assert.Equal(OutcomeKind.Invalid, again.Kind);   // find 40 applies to every unit, so R1 clashes with Y's R1 (no disjointness)
        Assert.Contains("R1", again.Errors[0], StringComparison.Ordinal);

        // Widening the first alternative into the second is refused; keeping it as it was (effectivity unchanged) is fine.
        var widen = await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, pod, first.Id, new UpdateUsageCommand(1m, "EA", 30, null, null, ConfigEnv.Serial(Ctx, 1, 150)), null);
        Assert.Equal(OutcomeKind.Conflict, widen.Kind);
        var keep = await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, pod, first.Id, new UpdateUsageCommand(3m, "EA", 30), null);
        Assert.True(keep.IsOk);
        Assert.Equal("serial 1-100 (ORCA-POD)", keep.Value!.EffectivityText);

        // Clearing the effectivity of a line that has alternatives conflicts; on a line of its own it works.
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, pod, first.Id, new UpdateUsageCommand(1m, "EA", 30, null, null, null, ClearEffectivity: true), null)).Kind);
        var solo = await env.AddLineAsync(pod, x, 1m, 50, ConfigEnv.Serial(Ctx, 5, 9));
        var cleared = await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, pod, solo.Id, new UpdateUsageCommand(1m, "EA", 50, null, null, null, ClearEffectivity: true), null);
        Assert.Null(cleared.Value!.Effectivity);

        // Renumbering keeps the alternatives together and never lets them collide.
        Assert.True((await env.Bom.RenumberAsync(ConfigEnv.Alice, pod, 100, 100, null)).IsOk);
        var lines = (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, pod, null)).Value!.Lines;
        Assert.Equal([100, 100, 100, 200], lines.Select(l => l.FindNumber));   // alternatives keep sharing one find number
    }

    [Fact]
    public async Task The_database_refuses_overlapping_alternatives_even_without_the_service()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var x = await env.ReleasedComponentAsync("X");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, x, 1m, 30, ConfigEnv.Serial(Ctx, 1, 100));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, created_at, created_by, eff_kind, eff_context, eff_from, eff_to) " +
            "SELECT gen_random_uuid(), checkout_revision_id, child_master_id, 1, 'EA', find_number, now(), 'x', 'serial', 'ORCA-POD', 50, 60 FROM plm.bom_working_usage"));
        Assert.Equal("23505", ex.SqlState);
        // Shape: a serial range needs a context and from <= to.
        var shape = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("UPDATE plm.bom_working_usage SET eff_from = 9, eff_to = 3"));
        Assert.Equal("23514", shape.SqlState);
    }

    // ---------------------------------------------------------------- resolution per unit

    [Fact]
    public async Task A_serial_number_resolves_the_alternative_of_each_find_number()
    {
        var (env, pod, x, y, nut, bracket) = await Pod(pg);

        var s50 = await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(50));
        Assert.Equal([x, bracket], s50.Lines.Select(l => l.Number));
        Assert.Equal(["10", "20"], s50.Lines.Select(l => l.Path));
        Assert.Equal("serial 50 (ORCA-POD)", s50.Configuration);
        Assert.Equal("serial 1-100 (ORCA-POD)", s50.Lines[0].EffectivityText);
        Assert.Null(s50.Lines[1].Effectivity);           // a line without effectivity always applies

        var s150 = await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(150));
        Assert.Equal([y, nut, bracket], s150.Lines.Select(l => l.Number));
        Assert.Equal(["10~1", "10~1.10", "20"], s150.Lines.Select(l => l.Path));   // the second alternative of find number 10
        Assert.Equal(2m, s150.Lines[1].ExtendedQuantity);

        // Boundaries: 100 is X's last unit, 101 is Y's first.
        Assert.Equal(x, (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(100))).Lines[0].Number);
        Assert.Equal(y, (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(101))).Lines[0].Number);
        // A unit in another context matches no ranged line: only the line without effectivity remains.
        var other = await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: EffectivityRequest.ForSerial("OTHER-POD", 5));
        Assert.Equal([bracket], other.Lines.Select(l => l.Number));

        // Without a unit the as-designed view shows every alternative, told apart by their effectivity.
        var all = await env.ExplodeAsync(pod, BomConfigSpec.LatestReleased);
        Assert.Equal([x, y, nut, bracket], all.Lines.Select(l => l.Number));
        Assert.Equal(["10", "10~1", "10~1.10", "20"], all.Lines.Select(l => l.Path));

        // Lazy expansion addresses the alternative by its path.
        var expanded = (await env.Bom.ExplodeAsync(ConfigEnv.Alice, new BomQuery(pod, null, null, BomConfigSpec.LatestReleased, 1, "10~1"), null)).Value!;
        Assert.Equal([nut], expanded.Lines.Select(l => l.Number));
        Assert.Equal("10~1.10", expanded.Lines[0].Path);
        Assert.Equal(2m, expanded.Lines[0].ExtendedQuantity);
        var inUnit = (await env.Bom.ExplodeAsync(ConfigEnv.Alice, new BomQuery(pod, null, null, BomConfigSpec.Effectivity, 1, "10~1", Effectivity: At(150)), null)).Value!;
        Assert.Equal([nut], inUnit.Lines.Select(l => l.Number));
        // Under the unit that does not use the alternative the path does not exist.
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(ConfigEnv.Alice, new BomQuery(pod, null, null, BomConfigSpec.Effectivity, 1, "10~1", Effectivity: At(5)), null)).Kind);

        // Export and summary follow the same resolution, and the CSV carries the effectivity.
        var csv = (await env.Bom.ExportCsvAsync(ConfigEnv.Alice, new BomQuery(pod, null, null, BomConfigSpec.Effectivity, Effectivity: At(50)), null)).Value!;
        Assert.Contains("serial 1-100 (ORCA-POD)", csv.Csv, StringComparison.Ordinal);
        Assert.DoesNotContain(y, csv.Csv, StringComparison.Ordinal);
        Assert.Contains("serial_50", csv.FileName, StringComparison.Ordinal);
        var summary = (await env.Bom.SummaryAsync(ConfigEnv.Alice, new BomQuery(pod, null, null, BomConfigSpec.Effectivity, Effectivity: At(150)), true, null)).Value!;
        Assert.Equal(new[] { bracket, nut }.Order(StringComparer.Ordinal), summary.Lines.Select(l => l.Number!));

        // A request without a unit or without a context is invalid.
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.ExplodeAsync(ConfigEnv.Alice, new BomQuery(pod, Spec: BomConfigSpec.Effectivity), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.ExplodeAsync(ConfigEnv.Alice, new BomQuery(pod, Spec: BomConfigSpec.Effectivity, Effectivity: new EffectivityRequest(null, Serial: 5)), null)).Kind);
    }

    [Fact]
    public async Task Lots_blocks_and_dates_resolve_in_their_own_dimension()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx, ("Block 1", 1), ("Block 2", 2), ("Block 3", 3));
        var a = await env.ReleasedComponentAsync("Early");
        var b = await env.ReleasedComponentAsync("Middle");
        var c = await env.ReleasedComponentAsync("Late");
        var d = await env.ReleasedComponentAsync("Dated");
        var always = await env.ReleasedComponentAsync("Always");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, a, 1m, 10, new EffectivityInput("lot", Ctx, "Block 1", "Block 1"));
        await env.AddLineAsync(pod, b, 1m, 10, new EffectivityInput("lot", Ctx, "Block 2", "Block 3"));
        await env.AddLineAsync(pod, c, 1m, 10, new EffectivityInput("lot", Ctx, "4", null));
        await env.AddLineAsync(pod, d, 1m, 20, new EffectivityInput("date", null, "2027-01-01", "2027-12-31"));
        await env.AddLineAsync(pod, always, 1m, 30);
        await env.CheckInAsync(pod);
        await env.ReleaseAsync(pod);

        async Task<string[]> Numbers(EffectivityRequest unit) => [.. (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: unit)).Lines.Select(l => l.Number!)];
        Assert.Equal([a, always], await Numbers(EffectivityRequest.ForLot(Ctx, 1)));
        Assert.Equal([b, always], await Numbers(EffectivityRequest.ForLot(Ctx, 3)));
        Assert.Equal([c, always], await Numbers(EffectivityRequest.ForLot(Ctx, 9)));
        // Dates are UTC days; the boundaries are inclusive.
        Assert.Equal([d, always], await Numbers(EffectivityRequest.ForDate(new DateOnly(2027, 1, 1))));
        Assert.Equal([d, always], await Numbers(EffectivityRequest.ForDate(new DateOnly(2027, 12, 31))));
        Assert.Equal([always], await Numbers(EffectivityRequest.ForDate(new DateOnly(2028, 1, 1))));
        Assert.Equal([always], await Numbers(EffectivityRequest.ForDate(new DateOnly(2026, 12, 31))));
        // Several dimensions at once (A19: serial first, then lot, then date): each line is judged by its own dimension.
        var both = new EffectivityRequest(Ctx, Lot: 2, Day: EffectivityRange.DayNumber(new DateOnly(2027, 6, 1)));
        Assert.Equal([b, d, always], await Numbers(both));
        // A dimension that was not asked about leaves its lines out (CFG-5).
        Assert.Equal([always], await Numbers(EffectivityRequest.ForSerial(Ctx, 1)));
    }

    [Fact]
    public async Task Effectivity_is_copied_forward_through_check_out_and_check_in_and_rows_stay_immutable()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var x = await env.ReleasedComponentAsync("X");
        var y = await env.ReleasedComponentAsync("Y");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, x, 1m, 10, ConfigEnv.Serial(Ctx, 1, 100));
        await env.AddLineAsync(pod, y, 1m, 10, ConfigEnv.Serial(Ctx, 101, null));
        await env.CheckInAsync(pod);

        // Second iteration: the working copy starts with the ranges; adding a line keeps them.
        await env.CheckOutAsync(pod);
        var working = (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, pod, null)).Value!.Lines;
        Assert.Equal(["serial 1-100 (ORCA-POD)", "serial 101 and up (ORCA-POD)"], working.Select(l => l.EffectivityText));
        await env.AddLineAsync(pod, await env.ReleasedComponentAsync("Z"), 1m, 20);
        await env.CheckInAsync(pod);
        Assert.Equal(new string?[] { "serial 1-100 (ORCA-POD)", "serial 101 and up (ORCA-POD)", null }, (await env.ExplodeAsync(pod, BomConfigSpec.Latest)).Lines.Select(l => l.EffectivityText));
        Assert.Equal(4, await env.ScalarAsync("SELECT count(*) FROM plm.bom_usage WHERE eff_kind IS NOT NULL"));   // two iterations carry the two ranged lines each

        // Rows of a checked-in iteration cannot be edited, not even their effectivity.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("UPDATE plm.bom_usage SET eff_from = 1"));
        Assert.Contains("immutable", ex.MessageText, StringComparison.Ordinal);
        // Undoing a check-out discards the edited ranges with the working copy.
        await env.CheckOutAsync(pod);
        var line = (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, pod, null)).Value!.Lines[0];
        Assert.True((await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, pod, line.Id, new UpdateUsageCommand(1m, "EA", line.FindNumber, null, null, ConfigEnv.Serial(Ctx, 1, 50)), null)).IsOk);
        Assert.True((await env.Parts.UndoCheckOutAsync(ConfigEnv.Alice, pod, null)).IsOk);
        Assert.Equal("serial 1-100 (ORCA-POD)", (await env.ExplodeAsync(pod, BomConfigSpec.Latest)).Lines[0].EffectivityText);
    }

    [Fact]
    public async Task Where_used_can_be_asked_for_one_unit()
    {
        var (env, pod, x, y, _, bracket) = await Pod(pg);
        var all = (await env.Bom.WhereUsedAsync(ConfigEnv.Alice, x, 1, null)).Value!;
        Assert.Equal(pod, Assert.Single(all.Rows).ParentNumber);
        Assert.Equal("serial 1-100 (ORCA-POD)", all.Rows[0].EffectivityText);

        Assert.Single((await env.Bom.WhereUsedAsync(ConfigEnv.Alice, x, 1, At(50), null)).Value!.Rows);
        Assert.Empty((await env.Bom.WhereUsedAsync(ConfigEnv.Alice, x, 1, At(150), null)).Value!.Rows);
        Assert.Single((await env.Bom.WhereUsedAsync(ConfigEnv.Alice, y, 1, At(150), null)).Value!.Rows);
        Assert.Empty((await env.Bom.WhereUsedAsync(ConfigEnv.Alice, y, 1, At(50), null)).Value!.Rows);
        Assert.Single((await env.Bom.WhereUsedAsync(ConfigEnv.Alice, bracket, 1, At(1), null)).Value!.Rows);   // no effectivity: every unit
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.WhereUsedAsync(ConfigEnv.Alice, x, 1, new EffectivityRequest(null, Serial: 3), null)).Kind);
    }

    [Fact]
    public async Task Two_units_can_be_compared_to_see_what_differs()
    {
        var (env, pod, x, y, nut, bracket) = await Pod(pg);
        var diff = (await env.Bom.CompareUnitsAsync(ConfigEnv.Alice, pod, At(100), At(101), null, "corr-units")).Value!;
        Assert.Equal((2, 1, 0), (diff.Added, diff.Removed, diff.Changed));
        // X goes away, Y and its nut come in; the bracket is the same on both.
        Assert.Equal([(BomChangeKind.Removed, x), (BomChangeKind.Added, y), (BomChangeKind.Added, nut)], diff.Lines.Select(l => (l.Kind, l.Number!)));
        Assert.Equal(1, diff.Unchanged);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "bom.compare_units" && e.CorrelationId == "corr-units" && e.Outcome == AuditOutcome.Success);

        // Two units that build the same product differ in nothing.
        var same = (await env.Bom.CompareUnitsAsync(ConfigEnv.Alice, pod, At(10), At(20), null, null)).Value!;
        Assert.Empty(same.Lines);
        Assert.Equal(2, same.Unchanged);
        Assert.Contains("Removed", DeepDiffCsv.Render(diff), StringComparison.Ordinal);
        Assert.NotEmpty(bracket);
    }

    // ---------------------------------------------------------------- revision effectivity and change notices

    [Fact]
    public async Task A_revision_effective_from_a_serial_leaves_the_earlier_revision_released_for_the_serials_below()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var part = await env.ReleasedComponentAsync("Plug");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        await env.AddLineAsync(pod, part, 1m, 10);
        await env.CheckInAsync(pod);
        await env.ReleaseAsync(pod);

        var ecn = await env.ReviseWithEffectivityAsync(part, ConfigEnv.Serial(Ctx, 101, null));
        // The effectivity is shown on the notice's affected object.
        var shown = (await env.Change.GetAsync(ConfigEnv.Alice, ecn.Number, null)).Value!.Affected.Single();
        Assert.Equal("serial 101 and up (ORCA-POD)", shown.Effectivity);
        var done = await env.ReleaseEcnAsync(ecn.Number);
        Assert.Equal("Approved", done.Status);

        // A stays Released (for the units below 101); B is Released too. Nothing was obsoleted.
        Assert.Equal("Released", (await env.PartAsync(part, "A")).Revision.State);
        Assert.Equal("Released", (await env.PartAsync(part, "B")).Revision.State);
        var effectivity = (await env.Eff.GetPartAsync(ConfigEnv.Alice, part, null)).Value!;
        Assert.Equal(["serial up to 100 (ORCA-POD)", "serial 101 and up (ORCA-POD)"], effectivity.Revisions.Select(r => r.Range?.Describe()));
        Assert.Equal([ecn.Number, ecn.Number], effectivity.Revisions.Select(r => r.SourceChange));
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM cfg.revision_effectivity"));
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM cfg.revision_effectivity_history"));

        // The as-built structure follows: serial 50 gets revision A of the plug, serial 150 gets B; the default (no match) is the latest released.
        Assert.Equal("A", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(50))).Lines.Single().Revision);
        Assert.Equal("B", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(150))).Lines.Single().Revision);
        Assert.Equal("B", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(101))).Lines.Single().Revision);
        Assert.Equal("A", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(100))).Lines.Single().Revision);
        Assert.Equal("B", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: EffectivityRequest.ForSerial("OTHER-POD", 5))).Lines.Single().Revision);
        Assert.Equal("B", (await env.ExplodeAsync(pod, BomConfigSpec.LatestReleased)).Lines.Single().Revision);
        // Comparing the units either side of the change shows the revision change.
        var diff = (await env.Bom.CompareUnitsAsync(ConfigEnv.Alice, pod, At(100), At(101), null, null)).Value!;
        Assert.Equal(["Revision A.1 to B.2"], diff.Lines.Single().Changes);

        // The release history and the audit trail say what the rule did.
        var release = (await env.AuditAsync()).Single(e => e.Action == "change.release");
        Assert.Contains("retainedByEffectivity", release.Payload, StringComparison.Ordinal);
        Assert.Contains("still Released for their earlier units", (await env.Change.GetAsync(ConfigEnv.Alice, ecn.Number, null)).Value!.History[^1].Detail, StringComparison.Ordinal);

        // A third revision from serial 201 cuts B back to 101-200 and leaves A alone.
        var ecn2 = await env.ReviseWithEffectivityAsync(part, ConfigEnv.Serial(Ctx, 201, null), "Third");
        await env.ReleaseEcnAsync(ecn2.Number);
        var rows = (await env.Eff.GetPartAsync(ConfigEnv.Alice, part, null)).Value!.Revisions;
        Assert.Equal(["serial up to 100 (ORCA-POD)", "serial 101-200 (ORCA-POD)", "serial 201 and up (ORCA-POD)"], rows.Select(r => r.Range?.Describe()));
        Assert.All(rows, r => Assert.Equal("Released", r.State));
        Assert.Equal("C", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(500))).Lines.Single().Revision);
        Assert.Equal("B", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(150))).Lines.Single().Revision);

        // A fourth, effective from 50, makes B and C serve nothing (Obsolete) and cuts A back to 1-49.
        var ecn3 = await env.ReviseWithEffectivityAsync(part, ConfigEnv.Serial(Ctx, 50, null), "Fourth");
        await env.ReleaseEcnAsync(ecn3.Number);
        var states = (await env.Eff.GetPartAsync(ConfigEnv.Alice, part, null)).Value!.Revisions;
        Assert.Equal(["Released", "Obsolete", "Obsolete", "Released"], states.Select(r => r.State));
        Assert.Equal("serial up to 49 (ORCA-POD)", states[0].Range!.Describe());
        Assert.Equal("D", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(60))).Lines.Single().Revision);
        Assert.Equal("A", (await env.ExplodeAsync(pod, BomConfigSpec.Effectivity, unit: At(10))).Lines.Single().Revision);
    }

    [Fact]
    public async Task Without_an_effectivity_the_notice_supersedes_the_earlier_revision_completely_as_before()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var part = await env.ReleasedComponentAsync("Plug");
        var ecn = await env.ReviseWithEffectivityAsync(part, null);
        await env.ReleaseEcnAsync(ecn.Number);
        Assert.Equal("Obsolete", (await env.PartAsync(part, "A")).Revision.State);
        Assert.Equal("Released", (await env.PartAsync(part, "B")).Revision.State);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.revision_effectivity"));
    }

    [Fact]
    public async Task Effectivity_of_a_revision_is_checked_when_it_is_set_and_frozen_with_the_submission()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        await env.NewContextAsync("KRAKEN-POD");
        var part = await env.ReleasedComponentAsync("Plug");
        var other = await env.ReleasedComponentAsync("Other plug");

        var ecn = (await env.Change.CreateAsync(ConfigEnv.Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Effectivity", "d", "Medium", "r", ConfigEnv.Open, ChangeClasses.II), null)).Value!;
        async Task<Outcome<ChangeDetail>> Add(string key, EffectivityInput? e, string action = ChangeActions.Revise) =>
            await env.Change.AddAffectedAsync(ConfigEnv.Alice, ecn.Number, new AddAffectedCommand("part", key, action, null, false, e), "corr-add");

        // Closed at the top: serials above would have no revision.
        var closed = await Add(part, ConfigEnv.Serial(Ctx, 101, 200));
        Assert.Equal(OutcomeKind.Invalid, closed.Kind);
        Assert.Contains("open at the top", closed.Errors[0], StringComparison.Ordinal);
        // Only a revise row can carry one.
        var obsolete = await Add(part, ConfigEnv.Serial(Ctx, 101, null), ChangeActions.Obsolete);
        Assert.Contains("revises", obsolete.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await Add(part, ConfigEnv.Serial("NO-SUCH", 101, null))).Kind);
        // Nothing was created by the refusals: the part still has one revision and the notice no rows.
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM plm.part_revision WHERE master_id = (SELECT id FROM plm.part_master WHERE number = '{part}')"));
        Assert.Empty((await env.Change.GetAsync(ConfigEnv.Alice, ecn.Number, null)).Value!.Affected);

        var ok = await Add(part, ConfigEnv.Serial(Ctx, 101, null));
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        var affected = ok.Value!.Affected.Single();
        // Set, change and clear while the notice is a draft; the history records each.
        var changed = await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Alice, ecn.Number, affected.Id, ConfigEnv.Serial(Ctx, 150, null), "corr-set");
        Assert.Equal("serial 150 and up (ORCA-POD)", changed.Value!.Affected.Single().Effectivity);
        Assert.Contains(changed.Value.History, h => h.Action == "Effectivity set");
        Assert.Null((await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Alice, ecn.Number, affected.Id, null, null)).Value!.Affected.Single().Effectivity);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Alice, ecn.Number, affected.Id, ConfigEnv.Serial(Ctx, 1, 5), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Mallory, ecn.Number, affected.Id, ConfigEnv.Serial(Ctx, 150, null), "corr-v")).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-v" && e.Outcome == AuditOutcome.Denied);
        Assert.True((await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Alice, ecn.Number, affected.Id, ConfigEnv.Serial(Ctx, 101, null), null)).IsOk);

        // Submitting freezes it: the notice's signed content names the effectivity, and the draft-only trigger holds it.
        await env.CheckOutAsync(part);
        await env.CheckInAsync(part);
        var started = await env.Flow.StartAsync(ConfigEnv.Alice, new Keel.Workflow.StartReviewCommand("ecn", ecn.Number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), null);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var task = (await env.Flow.GetTaskAsync(WorkflowEnv.Fresh(ConfigEnv.Rita), started.Value!.Tasks.Single(t => t.Status == "Open").Id, null)).Value!;
        Assert.Contains("effective serial 101 and up (ORCA-POD)", task.Description, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.SetAffectedEffectivityAsync(ConfigEnv.Alice, ecn.Number, affected.Id, ConfigEnv.Serial(Ctx, 10, null), null)).Kind);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("UPDATE cfg.change_effectivity SET eff_from = 5"));
        Assert.Contains("draft", ex.MessageText, StringComparison.Ordinal);
        Assert.NotEmpty(other);
    }

    [Fact]
    public async Task A_notice_cannot_mix_dimensions_with_what_the_part_already_has()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var part = await env.ReleasedComponentAsync("Plug");
        await env.ReleaseEcnAsync((await env.ReviseWithEffectivityAsync(part, ConfigEnv.Serial(Ctx, 101, null))).Number);

        var ecn = (await env.Change.CreateAsync(ConfigEnv.Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Mixed", "d", "Medium", "r", ConfigEnv.Open, ChangeClasses.II), null)).Value!;
        var byDate = await env.Change.AddAffectedAsync(ConfigEnv.Alice, ecn.Number, new AddAffectedCommand("part", part, ChangeActions.Revise, null, false, new EffectivityInput("date", null, "2030-01-01", null)), null);
        Assert.Equal(OutcomeKind.Invalid, byDate.Kind);
        Assert.Contains("same type and context", byDate.Errors[0], StringComparison.Ordinal);
        var byLot = await env.Change.AddAffectedAsync(ConfigEnv.Alice, ecn.Number, new AddAffectedCommand("part", part, ChangeActions.Revise, null, false, new EffectivityInput("lot", Ctx, "3", null)), null);
        Assert.Equal(OutcomeKind.Invalid, byLot.Kind);
    }

    // ---------------------------------------------------------------- contexts

    [Fact]
    public async Task Contexts_are_created_by_authors_and_program_contexts_stay_inside_their_program()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var made = await env.Eff.CreateContextAsync(ConfigEnv.Alice, new CreateContextCommand("ORCA-POD", "Orca pod", "The pod", "ORCA", [new EffectivityBlock("Block 1", 1)]), "corr-ctx");
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        Assert.Equal(OutcomeKind.Conflict, (await env.Eff.CreateContextAsync(ConfigEnv.Alice, new CreateContextCommand("ORCA-POD", "Again"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Eff.CreateContextAsync(ConfigEnv.Alice, new CreateContextCommand("bad id!", "x"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Eff.CreateContextAsync(ConfigEnv.Alice, new CreateContextCommand("DUP-BLOCKS", "x", "", null, [new EffectivityBlock("A", 1), new EffectivityBlock("B", 1)]), null)).Kind);
        // A viewer cannot create one; nobody can create one for a program they do not hold; both are audited as denials.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Eff.CreateContextAsync(ConfigEnv.Mallory, new CreateContextCommand("MAL-POD", "x"), "corr-m")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Eff.CreateContextAsync(ConfigEnv.Eve, new CreateContextCommand("EVE-POD", "x", "", "ORCA"), "corr-e")).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.CorrelationId == "corr-m" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.CorrelationId == "corr-e" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.CorrelationId == "corr-ctx" && e.Outcome == AuditOutcome.Success && e.Action == "effectivity.context_create");

        // Eve (not on ORCA) neither lists nor reads it, and cannot use it in a line.
        Assert.Single(await env.Eff.ListContextsAsync(ConfigEnv.Alice, null));
        Assert.Empty(await env.Eff.ListContextsAsync(ConfigEnv.Eve, null));
        Assert.Equal(OutcomeKind.NotFound, (await env.Eff.GetContextAsync(ConfigEnv.Eve, "ORCA-POD", null)).Kind);
        Assert.Equal("Block 1", (await env.Eff.GetContextAsync(ConfigEnv.Alice, "orca-pod", null)).Value!.Blocks.Single().Name);
        var part = await env.NewPartAsync("Plain");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod, ConfigEnv.Eve);
        var refused = await env.Bom.AddUsageAsync(ConfigEnv.Eve, pod, new AddUsageCommand(part, 1m, null, 10, null, null, ConfigEnv.Serial("ORCA-POD", 1, 5)), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("was not found", refused.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_users_adding_overlapping_ranges_at_once_serialize_and_one_wins()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        await env.NewContextAsync(Ctx);
        var x = await env.ReleasedComponentAsync("X");
        var y = await env.ReleasedComponentAsync("Y");
        var pod = await env.NewPartAsync("Pod", "Assembly");
        await env.CheckOutAsync(pod);
        // The same owner from two sessions (the second request waits for the first on the part's row lock).
        var results = await Task.WhenAll(
            env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(x, 1m, null, 10, null, null, ConfigEnv.Serial(Ctx, 1, 100)), null),
            env.Bom.AddUsageAsync(ConfigEnv.Alice, pod, new AddUsageCommand(y, 1m, null, 10, null, null, ConfigEnv.Serial(Ctx, 50, 150)), null));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(1, results.Count(r => r.Kind == OutcomeKind.Conflict));
        // A different user cannot edit a working copy they do not own.
        var other = await env.Bom.AddUsageAsync(ConfigEnv.Rita, pod, new AddUsageCommand(y, 1m, null, 20, null, null, null), null);
        Assert.Equal(OutcomeKind.Conflict, other.Kind);
    }
}
