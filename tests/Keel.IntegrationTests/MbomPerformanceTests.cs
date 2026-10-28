using System.Diagnostics;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.TestSupport;

namespace Keel.IntegrationTests;

/// <summary>
/// Performance gates of ADR-0024: 5,000 MBOM lines reconcile against 5,000 EBOM lines in under one second, and a 10-level, 50,000-line MBOM explodes in
/// under two seconds through the real SQL filter. Both are warm runs; the existing EBOM gates are unchanged (BomPerformanceTests, ConfigurationPerformanceTests).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomPerformanceTests(PostgresFixture pg)
{
    private static void Log(string message)
    {
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        if (Environment.GetEnvironmentVariable("KEEL_PERF_LOG") is { Length: > 0 } path) File.AppendAllText(path, message + Environment.NewLine);
    }

    private const string Replica = "SET session_replication_role = replica; ";

    [Fact]
    public async Task Five_thousand_mbom_lines_reconcile_against_five_thousand_ebom_lines_in_under_one_second()
    {
        var env = await MbomEnv.CreateAsync(pg);
        try
        {
            var root = await env.C.NewPartAsync("Perf assembly", "Assembly");
            // 5,000 synthetic component parts and EBOM lines by bulk SQL (the guards that would reject them one by one are what the service already tested).
            await env.Db.ExecuteAsSuperuserAsync(Replica +
                """
                INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement,
                                             controlling_office, distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
                SELECT gen_random_uuid(), 'PF-' || lpad(g::text, 6, '0'), 'Perf part ' || g, 'Component', 'EA', 'Buy', 'part-default', 'None', NULL, NULL, '{}', 'A', NULL, NULL, NULL, 'ENG', '{}', now(), 'perf'
                FROM generate_series(1, 5000) g;
                INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by, alt)
                SELECT gen_random_uuid(), i.id, m.id, 1 + (n.g % 7), 'EA', n.g * 10, '{}', NULL, 'EBOM', now(), 'perf', 0
                FROM (SELECT id, row_number() OVER (ORDER BY number) AS g FROM plm.part_master WHERE number LIKE 'PF-%') n
                JOIN plm.part_master m ON m.id = n.id
                CROSS JOIN (SELECT i.id FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master pm ON pm.id = r.master_id WHERE pm.number = '
                """ + root + "') i;");
            await env.Db.ExecuteAsSuperuserAsync("ANALYZE");
            Assert.Equal(5000, await env.ScalarAsync("SELECT count(*) FROM plm.bom_usage"));

            var generate = Stopwatch.StartNew();
            var made = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(root, MbomEnv.Plant, new EbomSpec(EbomSpecKind.Latest)), "c");
            Assert.True(made.IsOk, string.Join("; ", made.Errors));
            Log($"Generated an MBOM of {made.Value!.Items[0].Lines} lines from the EBOM in {generate.ElapsedMilliseconds} ms");
            var number = made.Value.Items[0].MbomNumber;
            Assert.Equal(5000, await env.ScalarAsync("SELECT count(*) FROM mbom.usage"));

            // 100 manufacturing deviations so the run has findings to store.
            await env.Db.ExecuteAsSuperuserAsync(Replica + "UPDATE mbom.usage SET quantity = quantity + 1 WHERE find_number % 500 = 0 OR find_number % 700 = 0");
            var deviations = await env.ScalarAsync("SELECT count(*) FROM mbom.usage WHERE find_number % 500 = 0 OR find_number % 700 = 0");

            var warm = await env.Mbom.ReconcileAsync(MbomEnv.Alice, number, new EbomSpec(EbomSpecKind.Latest), "c"); // plan cache, JIT
            Assert.True(warm.IsOk, string.Join("; ", warm.Errors));
            var timer = Stopwatch.StartNew();
            var run = await env.Mbom.ReconcileAsync(MbomEnv.Alice, number, new EbomSpec(EbomSpecKind.Latest), "c");
            timer.Stop();
            Assert.True(run.IsOk, string.Join("; ", run.Errors));
            var report = run.Value!;
            Log($"Reconciled {report.Run.MbomLines} MBOM lines against {report.Run.EbomLines} EBOM lines ({report.Findings.Count} findings) in {timer.ElapsedMilliseconds} ms end to end (engine and SQL {report.Run.DurationMs} ms; budget 1000 ms)");
            Assert.Equal((5000, 5000, 5000, 100m), (report.Run.EbomLines, report.Run.MbomLines, report.Run.CoveredLines, report.Run.CoveragePct));
            Assert.Equal(deviations, report.Findings.Count);
            Assert.All(report.Findings, f => Assert.Equal(FindingCodes.QuantityDifference, f.Code));
            Assert.True(report.Run.IntegrityOk);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Reconciliation took {timer.ElapsedMilliseconds} ms");
        }
        finally
        {
            await env.Db.DropAsync();
        }
    }

    [Fact]
    public async Task A_ten_level_fifty_thousand_line_mbom_explodes_in_under_two_seconds()
    {
        var db = await pg.CreateDatabaseAsync();
        try
        {
            var ds = db.AppDataSource();
            var bom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions { Seed = 7, Levels = 10, TargetLines = 50_000 });
            // One released MBOM per assembly, copied from the latest EBOM by set-based SQL (each carries its part's markings, which trivially dominate them).
            await db.ExecuteAsSuperuserAsync(Replica +
                """
                INSERT INTO mbom.mbom (id, number, part_master_id, plant, lifecycle_id, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office,
                                       distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
                SELECT gen_random_uuid(), 'MB-' || lpad(nextval('mbom.mbom_number_seq')::text, 6, '0'), m.id, 'PLANT-A', 'mbom-default', m.export_jurisdiction, m.eccn, m.usml_category, m.cui_categories,
                       m.distribution_statement, m.controlling_office, m.distribution_reason, m.program, m.owning_org, m.export_authorizations, now(), 'perf'
                FROM plm.part_master m WHERE m.part_type = 'Assembly';
                INSERT INTO mbom.revision (id, mbom_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
                SELECT gen_random_uuid(), id, 'A', 'Released', true, 1, now(), 'perf', now(), 'perf' FROM mbom.mbom;
                INSERT INTO mbom.iteration (id, revision_id, iteration, description, content_sha256, line_count, link_count, created_at, created_by)
                SELECT gen_random_uuid(), id, 1, '', repeat('a', 64), 0, 0, now(), 'perf' FROM mbom.revision;
                INSERT INTO mbom.usage (id, iteration_id, child_master_id, quantity, unit, find_number, alt, reference_designators, supply_type, mbom_only, created_at, created_by)
                SELECT gen_random_uuid(), mi.id, u.child_master_id, u.quantity, u.unit, u.find_number, u.alt, u.reference_designators,
                       CASE WHEN cm.part_type = 'Assembly' THEN 'Make' ELSE 'Buy' END, false, now(), 'perf'
                FROM mbom.mbom mm
                JOIN mbom.revision r ON r.mbom_id = mm.id
                JOIN mbom.iteration mi ON mi.revision_id = r.id
                JOIN plm.part_revision pr ON pr.master_id = mm.part_master_id AND pr.is_latest
                JOIN plm.part_iteration pi ON pi.revision_id = pr.id AND pi.iteration = pr.latest_iteration
                JOIN plm.bom_usage u ON u.parent_iteration_id = pi.id
                JOIN plm.part_master cm ON cm.id = u.child_master_id;
                """);
            await db.ExecuteAsSuperuserAsync("ANALYZE");
            var svc = new MbomService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System);
            var rootNumber = (await svc.SearchAsync(BomTestKit.Alice, new MbomSearch(PartNumber: bom.RootNumber), null)).Value!.Items.Single().Number;
            var query = new MbomQuery(rootNumber);
            await svc.ExplodeAsync(BomTestKit.Alice, query, null); // warm-up
            var timer = Stopwatch.StartNew();
            var result = await svc.ExplodeAsync(BomTestKit.Alice, query, null);
            timer.Stop();
            Assert.True(result.IsOk, string.Join(" ", result.Errors));
            Log($"Full MBOM explosion, full access: {result.Value!.Lines.Count} lines in {timer.ElapsedMilliseconds} ms (budget 2000 ms)");
            Assert.Equal(bom.Lines.Count, result.Value.Lines.Count);
            Assert.Equal(0, result.Value.RedactedCount);
            Assert.Equal(10, result.Value.Lines.Max(l => l.Level));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"The MBOM explosion took {timer.ElapsedMilliseconds} ms");

            // The same explosion for a restricted reader goes through the redaction path and returns the placeholders only.
            var restricted = await svc.ExplodeAsync(BomTestKit.Lena, query, null);
            Assert.True(restricted.IsOk);
            Assert.True(restricted.Value!.RedactedCount > 0);
            Assert.All(restricted.Value.Lines.Where(l => l.Redacted), l => Assert.Null(l.Number));
            // And the EBOM explosion of the same root still returns the same number of lines: the MBOM added nothing to its path.
            var ebom = await new BomService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System).ExplodeAsync(BomTestKit.Alice, new BomQuery(bom.RootNumber), null);
            Assert.Equal(bom.Lines.Count, ebom.Value!.Lines.Count);
        }
        finally
        {
            await db.DropAsync();
        }
    }
}
