using System.Diagnostics;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.TestSupport;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// The Phase 2.1 performance gate (ADR-0003, ADR-0021). The 50,000-line, 10-level structure of <see cref="BomPerformanceTests"/>, with every revision
/// Released, a quarter of the lines carrying serial effectivity (some outside the requested serial) and a tenth of the revisions carrying revision
/// effectivity, explodes for one serial in under 2 s on a warm run. A baseline of it is created, read, compared with the current structure and exported
/// within generous bounds. The explosion is checked against an in-memory walk of the stored rows, so the gate cannot pass by returning the wrong lines.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConfigurationPerformanceTests(PostgresFixture pg)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static void Log(string message)
    {
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        // Optional: KEEL_PERF_LOG=<file> appends the timings to a file, for runs where diagnostic messages are not shown.
        if (Environment.GetEnvironmentVariable("KEEL_PERF_LOG") is { Length: > 0 } path) File.AppendAllText(path, message + Environment.NewLine);
    }

    private static async Task<(T Result, TimeSpan Elapsed)> Time<T>(Func<Task<T>> call)
    {
        var sw = Stopwatch.StartNew();
        var result = await call();
        return (result, sw.Elapsed);
    }

    [Fact]
    public async Task Effectivity_resolution_and_baselines_keep_the_fifty_thousand_line_structure_within_budget()
    {
        var db = await pg.CreateDatabaseAsync();
        try
        {
            var ds = db.AppDataSource();
            var bom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions { Seed = 11, Levels = 10, TargetLines = 50_000, ReleasedFraction = 1.0 });
            // Ranges: a quarter of the lines are effective for serials 1-1000; one line in ten of those only from 2000 (outside serial 500).
            await db.ExecuteAsSuperuserAsync(
                """
                INSERT INTO cfg.effectivity_context (id, name, created_at, created_by) VALUES ('ORCA-POD', 'Synthetic pod', now(), 'perf');
                SET session_replication_role = replica;
                UPDATE plm.bom_usage SET eff_kind = 'serial', eff_context = 'ORCA-POD', eff_from = CASE WHEN abs(hashtext(id::text)) % 10 = 0 THEN 2000 ELSE 1 END,
                       eff_to = CASE WHEN abs(hashtext(id::text)) % 10 = 0 THEN NULL ELSE 1000 END
                WHERE abs(hashtext(id::text)) % 4 = 0;
                INSERT INTO cfg.revision_effectivity (revision_id, master_id, eff_kind, eff_context, eff_from, eff_to, source_change, set_at, set_by)
                SELECT id, master_id, 'serial', 'ORCA-POD', 1, 5000, 'perf', now(), 'perf' FROM plm.part_revision WHERE abs(hashtext(id::text)) % 10 = 0;
                ANALYZE;
                """);
            var log = new PostgresAuditLog(ds, TimeProvider.System);
            var svc = new BomService(ds, log, TimeProvider.System);
            var full = BomTestKit.Alice;
            var unit = EffectivityRequest.ForSerial("ORCA-POD", 500);
            var query = new BomQuery(bom.RootNumber, Spec: BomConfigSpec.Effectivity, Effectivity: unit);

            await svc.ExplodeAsync(full, query, null);   // warm-up: plan cache, buffers, JIT
            var (explosion, time) = await Time(() => svc.ExplodeAsync(full, query, null));
            Assert.True(explosion.IsOk, string.Join(" ", explosion.Errors));
            var lines = explosion.Value!.Lines;
            Log($"Effectivity explosion (serial 500): {lines.Count} of {bom.Lines.Count} lines in {time.TotalMilliseconds:F0} ms (budget {Budget.TotalMilliseconds:F0} ms)");
            Assert.True(time < Budget, $"Effectivity explosion took {time.TotalMilliseconds:F0} ms for {lines.Count} lines; budget is {Budget.TotalMilliseconds:F0} ms.");
            Assert.Equal(0, explosion.Value.UnresolvedCount);
            Assert.True(lines.Count < bom.Lines.Count, "Some lines should have been excluded by their effectivity.");
            Assert.Equal(await CountEffectiveAsync(db, bom.RootNumber, 500), lines.Count);

            // Reverse, and one unit against another.
            var hot = bom.Lines.GroupBy(l => l.ChildNumber).Where(g => bom.SharedPartNumbers.Contains(g.Key)).OrderByDescending(g => g.Count()).First();
            await svc.WhereUsedAsync(full, hot.Key, 1, unit, null);
            var (used, usedTime) = await Time(() => svc.WhereUsedAsync(full, hot.Key, 1, unit, null));
            Log($"Where-used {hot.Key} for serial 500: {used.Value!.Rows.Count} parents in {usedTime.TotalMilliseconds:F0} ms");
            Assert.True(usedTime < Budget);
            var (units, unitsTime) = await Time(() => svc.CompareUnitsAsync(full, bom.RootNumber, unit, EffectivityRequest.ForSerial("ORCA-POD", 3000), null, null));
            Log($"Compare serial 500 with 3000: {units.Value!.Lines.Count} differences in {unitsTime.TotalMilliseconds:F0} ms");
            Assert.True(units.IsOk && unitsTime < TimeSpan.FromSeconds(6));

            // Baseline: create from the released structure, read it back, compare with the current structure, export.
            var baselines = new BaselineService(ds, log, TimeProvider.System);
            var (created, createTime) = await Time(() => baselines.CreateAsync(full,
                new CreateBaselineCommand(bom.RootNumber, "Performance baseline", "Design", BomConfigSpec.LatestReleased, ConfirmRaiseMarkings: true), null));
            Assert.True(created.IsOk, string.Join(" ", created.Errors));
            Log($"Baseline created: {created.Value!.Summary.ItemCount} items in {createTime.TotalMilliseconds:F0} ms");
            Assert.Equal(bom.Lines.Count + 1, created.Value.Summary.ItemCount);
            Assert.True(createTime < TimeSpan.FromSeconds(15), $"Creating the baseline took {createTime.TotalSeconds:F1} s.");
            var number = created.Value.Summary.Number;
            var baselineQuery = new BomQuery("", Spec: BomConfigSpec.Baseline, Baseline: number);
            await svc.ExplodeAsync(full, baselineQuery, null);
            var (frozen, frozenTime) = await Time(() => svc.ExplodeAsync(full, baselineQuery, null));
            Log($"Explosion from the baseline: {frozen.Value!.Lines.Count} lines in {frozenTime.TotalMilliseconds:F0} ms");
            Assert.Equal(bom.Lines.Count, frozen.Value.Lines.Count);
            Assert.True(frozenTime < Budget, $"Baseline explosion took {frozenTime.TotalMilliseconds:F0} ms.");
            // The baseline's markings cover ITAR items: a non-U.S. person cannot read it at all.
            Assert.Equal(OutcomeKind.NotFound, (await svc.ExplodeAsync(BomTestKit.Lena, baselineQuery, null)).Kind);
            var (diff, diffTime) = await Time(() => svc.CompareBaselineToCurrentAsync(full, number, BomConfigSpec.LatestReleased, null));
            Log($"Baseline against current: {diff.Value!.Lines.Count} differences, {diff.Value.Unchanged} unchanged in {diffTime.TotalMilliseconds:F0} ms");
            Assert.Empty(diff.Value.Lines);
            Assert.True(diffTime < TimeSpan.FromSeconds(6), $"Comparing took {diffTime.TotalSeconds:F1} s.");
            var (export, exportTime) = await Time(() => svc.ExportCsvAsync(full, baselineQuery, null));
            Log($"Baseline CSV: {export.Value!.Lines} lines in {exportTime.TotalMilliseconds:F0} ms");
            Assert.True(exportTime < Budget);
            Assert.True((await baselines.VerifyAsync(full, number, null)).Value!.Intact);
        }
        finally
        {
            await db.DropAsync();
        }
    }

    /// <summary>Counts the lines an as-of-serial explosion must return, walking the stored usage rows in memory.</summary>
    private static async Task<int> CountEffectiveAsync(TestDatabase db, string rootNumber, long serial)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        var rows = new Dictionary<Guid, List<(Guid Child, string? Kind, long? From, long? To)>>();
        await using (var cmd = new NpgsqlCommand("SELECT parent_iteration_id, child_master_id, eff_kind, eff_from, eff_to FROM plm.bom_usage", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                if (!rows.TryGetValue(r.GetGuid(0), out var list)) rows[r.GetGuid(0)] = list = [];
                list.Add((r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
            }
        // Every master has one Released revision, so a master resolves to its only iteration.
        var iterationOf = new Dictionary<Guid, Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT r.master_id, i.id FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) iterationOf[r.GetGuid(0)] = r.GetGuid(1);
        Guid rootIteration;
        await using (var cmd = new NpgsqlCommand(
            "SELECT i.id FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration WHERE m.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", rootNumber);
            rootIteration = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        var count = 0;
        var stack = new Stack<Guid>();
        stack.Push(rootIteration);
        while (stack.Count > 0)
            foreach (var (child, kind, from, to) in rows.GetValueOrDefault(stack.Pop(), []))
            {
                if (kind is not null && !(kind == "serial" && (from is null || from <= serial) && (to is null || to >= serial))) continue;
                count++;
                if (iterationOf.TryGetValue(child, out var it)) stack.Push(it);
            }
        return count;
    }
}
