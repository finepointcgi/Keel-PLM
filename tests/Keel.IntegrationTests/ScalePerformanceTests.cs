using System.Diagnostics;
using System.Globalization;
using System.Text;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Notifications;
using Keel.Parts;
using Keel.TestSupport;
using Keel.Web.Services;
using Keel.Workspace;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Gap wave D (ADR-0034): the module services and the dashboard reads at the volumes the product is sized for, in an isolated throwaway database
/// (the Testcontainers PostgreSQL of this run; the dev and demo databases are never touched): 120,000 part masters (including a 50,000-line ten-level
/// structure), 50,000 documents, 20,000 changes, 150,000 audit events and 20,000 notifications of one user. Every call goes through the real service and its
/// access filter, as a fully authorized and as a restricted caller. The budgets are generous multiples of the measured times (docs/ops/performance.md) so a
/// loaded machine does not make them flaky; they exist to catch an order-of-magnitude regression (a lost index, a query that scans per row).
/// Timings and the query plans of the statements the migration 0018 indexes serve are written to TestResults/scale-performance.txt.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScalePerformanceTests(PostgresFixture pg)
{
    // KEEL_SCALE_FACTOR=3 triples the bulk data (and the budgets) for a one-off larger measurement; the default is the size the budgets are written for.
    private static readonly int Factor = int.TryParse(Environment.GetEnvironmentVariable("KEEL_SCALE_FACTOR"), CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 10 ? f : 1;
    private static readonly int BulkParts = 70_000 * Factor;
    private static readonly int Documents = 50_000 * Factor;
    private static readonly int Changes = 20_000 * Factor;
    private static readonly int AuditEvents = 150_000 * Factor;
    private const int Notifications = 20_000;

    private static readonly StringBuilder Report = new();

    private static void Log(string message)
    {
        lock (Report) Report.AppendLine(message);
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        if (Environment.GetEnvironmentVariable("KEEL_PERF_LOG") is { Length: > 0 } path) File.AppendAllText(path, message + Environment.NewLine);
    }

    /// <summary>One warm-up call, then five timed calls; returns the median and the slowest in milliseconds.</summary>
    private static async Task<(double Median, double Max)> MeasureAsync(string name, double budgetMs, Func<Task> call, int runs = 5)
    {
        await call();
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await call();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        budgetMs *= Factor;
        times.Sort();
        var median = times[times.Count / 2];
        Log($"{name}: median {median.ToString("F0", CultureInfo.InvariantCulture)} ms, max {times[^1].ToString("F0", CultureInfo.InvariantCulture)} ms (budget {budgetMs.ToString("F0", CultureInfo.InvariantCulture)} ms)");
        Assert.True(median < budgetMs, $"{name}: median {median:F0} ms exceeds the budget of {budgetMs:F0} ms.");
        return (median, times[^1]);
    }

    [Fact]
    public async Task Search_sorting_where_used_dashboard_feed_and_notifications_meet_their_budgets_at_scale()
    {
        var env = await ConfigEnv.CreateAsync(pg, documents: true);
        try
        {
            var load = Stopwatch.StartNew();
            var bom = await SyntheticBomGenerator.InsertAsync(env.Ds, new SyntheticBomOptions { Seed = 11, Levels = 10, TargetLines = 50_000, ReleasedFraction = 0.3 });
            await LoadBulkAsync(env);
            await env.Db.ExecuteAsSuperuserAsync("ANALYZE");
            var parts = Convert.ToInt64(await ScalarAsync(env, "SELECT count(*) FROM plm.part_master"), CultureInfo.InvariantCulture);
            Log($"Dataset: {parts} parts, {bom.Lines.Count} usage lines, {Documents} documents, {Changes} changes, {AuditEvents} audit events, {Notifications} notifications; loaded in {load.Elapsed.TotalSeconds:F0} s.");
            Assert.True(parts >= 100_000, $"The dataset has only {parts} parts.");

            var full = BomTestKit.Alice;
            var restricted = BomTestKit.Lena;

            // ---------------------------------------------------------------- part search
            await MeasureAsync("Part search, first page, no query (full access)", 3000, () => env.Parts.SearchAsync(full, "", null, 50, 0, null, null));
            await MeasureAsync("Part search, first page, no query (restricted caller)", 3000, () => env.Parts.SearchAsync(restricted, "", null, 50, 0, null, null));
            await MeasureAsync("Part search, deep page (offset 60,000)", 4000, () => env.Parts.SearchAsync(full, "", null, 50, 60_000, null, null));
            await MeasureAsync("Part search, words (\"gimbal bracket\")", 3000, () => env.Parts.SearchAsync(full, "gimbal bracket", null, 50, 0, null, null));
            await MeasureAsync("Part search, number fragment (\"B-0123\")", 3000, () => env.Parts.SearchAsync(full, "B-0123", null, 50, 0, null, null));
            await MeasureAsync("Part search, state filter (Released)", 3000, () => env.Parts.SearchAsync(full, "", "Released", 50, 0, null, null));
            foreach (var key in Keel.Parts.PartService.SortKeys.Keys)
                foreach (var descending in new[] { false, true })
                    await MeasureAsync($"Part list sorted by {key} {(descending ? "desc" : "asc")}", 3500, () => env.Parts.SearchAsync(full, "", null, 50, 0, new SortSpec(key, descending), null), runs: 3);

            // ---------------------------------------------------------------- other lists
            await MeasureAsync("Document list, first page", 3000, () => env.Docs!.SearchAsync(full, "", null, null, 50, 0, null, null));
            await MeasureAsync("Document list sorted by name", 3500, () => env.Docs!.SearchAsync(full, "", null, null, 50, 0, new SortSpec("name", false), null), runs: 3);
            await MeasureAsync("Change list, first page", 3000, () => env.Change.SearchAsync(full, new ChangeSearchQuery(), null));
            foreach (var key in Keel.Change.ChangeService.SortKeys.Keys)
                await MeasureAsync($"Change list sorted by {key}", 3500, () => env.Change.SearchAsync(full, new ChangeSearchQuery(Sort: new SortSpec(key, true)), null), runs: 3);

            // ---------------------------------------------------------------- BOM explode and where-used
            var svc = env.Bom;
            await MeasureAsync("BOM explode, one level (what the structure tab loads first)", 1500, () => svc.ExplodeAsync(full, new BomQuery(bom.RootNumber, Depth: 1), null));
            await MeasureAsync("BOM explode, full ten levels, 50,000 lines", 4000, () => svc.ExplodeAsync(full, new BomQuery(bom.RootNumber), null), runs: 3);
            var shared = bom.SharedPartNumbers[0];
            await MeasureAsync("Where-used, one level, a standard part used all over the structure", 2000, () => svc.WhereUsedAsync(full, shared, 1, null));
            await MeasureAsync("Where-used, all levels, same part", 4000, () => svc.WhereUsedAsync(full, shared, BomLimits.MaxDepth, null), runs: 3);
            await MeasureAsync("Where-used, all levels (restricted caller)", 4000, () => svc.WhereUsedAsync(restricted, shared, BomLimits.MaxDepth, null), runs: 3);

            // ---------------------------------------------------------------- home dashboard
            var workspace = new WorkspaceService(env.Ds, env.Audit, env.Clock);
            var favorites = new[] { "B-000010", "B-000020", "B-000030", "B-000040", "B-000050", "B-000060", "B-000070", "B-000080" };
            foreach (var n in favorites) { await workspace.AddFavoriteAsync(full, ObjectKinds.Part, n); await workspace.RecordViewAsync(full, ObjectKinds.Part, n); }
            var reads = new WorkspaceReads(WorkspaceTests.UserOf(full), workspace, env.Ds, env.Audit, env.Flow, env.Change, null!, env.Clock);
            await MeasureAsync("Home: recently viewed", 1500, () => reads.RecentAsync());
            await MeasureAsync("Home: favorites", 1500, () => reads.FavoritesAsync());
            await MeasureAsync("Home: recently released", 2500, () => reads.RecentlyReleasedAsync());
            await MeasureAsync("Home: changes in my programs", 2500, () => reads.ProgramChangesAsync());
            await MeasureAsync("Home: needs my attention", 2500, () => reads.AttentionAsync());
            await MeasureAsync("Home: all cards in sequence", 6000, async () =>
            {
                await reads.RecentAsync(); await reads.FavoritesAsync(); await reads.ProgramChangesAsync(); await reads.RecentlyReleasedAsync(); await reads.AttentionAsync();
            });

            // ---------------------------------------------------------------- activity feed and notifications
            var objects = new ObjectAccessResolver([new PartObjectAccess(env.Ds)]);
            var feed = new ActivityService(env.Ds, objects, env.Audit);
            await MeasureAsync("Activity feed, an object with 5,000 events, first 30", 1500, async () => Assert.True((await feed.GetAsync(full, "part", "B-000001", null, 30, false, null)).IsOk));
            var center = new NotificationCenter(env.Ds, objects, env.Audit, env.Clock, new NotificationHub());
            await MeasureAsync("Notifications: unread count (20,000 rows)", 1000, () => center.CountUnreadAsync(full));
            await MeasureAsync("Notifications: list, all, first page of 50", 2500, () => center.ListAsync(full, NotificationFilter.All, 0, 50));
            await MeasureAsync("Notifications: list, all, deep page (skip 15,000)", 3000, () => center.ListAsync(full, NotificationFilter.All, 15_000, 50));
            await MeasureAsync("Notifications: list, unread only", 2500, () => center.ListAsync(full, NotificationFilter.Unread, 0, 50));

            // ---------------------------------------------------------------- plans of the statements the new indexes serve
            await ExplainAsync(env, "Recently released parts (home card)",
                "SELECT m.number FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest WHERE r.lifecycle_state = 'Released' AND r.state_changed_at >= now() - interval '30 days' ORDER BY r.state_changed_at DESC, m.number LIMIT 8");
            await ExplainAsync(env, "Recently released documents (home card)",
                "SELECT m.number FROM plm.document_master m JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest WHERE r.lifecycle_state = 'Released' AND r.state_changed_at >= now() - interval '30 days' ORDER BY r.state_changed_at DESC, m.number LIMIT 8");
            await ExplainAsync(env, "Part list sorted by number (default path)",
                "SELECT m.number, count(*) OVER () FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest ORDER BY m.number LIMIT 50");
            await ExplainAsync(env, "Part list sorted by lower(name)",
                "SELECT m.number, count(*) OVER () FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest ORDER BY lower(m.name), m.number LIMIT 50");
        }
        finally
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TestResults");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "scale-performance.txt"), Report.ToString());
            await env.Db.DropAsync();
        }
    }

    [Fact]
    public async Task The_home_card_for_my_programs_finds_my_changes_even_when_hundreds_of_newer_open_changes_have_no_program()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        try
        {
            // Three older ECRs of program ORCA (ITAR) and 300 newer open ECRs without a program: the card used to read the newest 200 open changes the user may read and keep
            // those of her programs afterwards, so on a busy system her own changes were missing. The program is now part of the query.
            await ExecAsync(env,
                $"""
                INSERT INTO chg.change (id, change_type, number, title, description, priority, change_class, reason, requester_sub, requester_username, requester_name, lifecycle_id,
                    lifecycle_state, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org,
                    export_authorizations, created_at, created_by, state_changed_at, state_changed_by)
                SELECT gen_random_uuid(), 'ECR', 'ECR-' || lpad((g + 900000)::text, 6, '0'), 'Change ' || g, '', 'Low', 'II', '', 'sub-x', 'x', 'X', 'ecr-default', 'Draft',
                       CASE WHEN g <= 3 THEN 'Itar' ELSE 'None' END, NULL, CASE WHEN g <= 3 THEN 'XI(d)' END, CASE WHEN g <= 3 THEN ARRAY['EXPT'] ELSE ARRAY[]::text[] END,
                       CASE WHEN g <= 3 THEN 'D' ELSE 'A' END, CASE WHEN g <= 3 THEN 'ACME' END, CASE WHEN g <= 3 THEN 'Critical technology' END, CASE WHEN g <= 3 THEN 'ORCA' END,
                       'ENG', ARRAY[]::text[], now() - ((CASE WHEN g <= 3 THEN 10000 + g ELSE g END) || ' minutes')::interval, 'x', now(), 'x'
                FROM generate_series(1, 303) g
                """);
            var workspace = new WorkspaceService(env.Ds, env.Audit, env.Clock);
            var reads = new WorkspaceReads(WorkspaceTests.UserOf(BomTestKit.Alice), workspace, env.Ds, env.Audit, env.Flow, env.Change, null!, env.Clock);
            var card = await reads.ProgramChangesAsync();
            Assert.Equal(3, card.Items.Count);
            Assert.All(card.Items, i => Assert.Equal("Draft", i.StateLabel));
            Assert.Contains("ITAR", card.Banner, StringComparison.Ordinal);
            // A user without that program sees none of them (and none of the open ones either: only her own programs' changes are listed).
            var carmen = new WorkspaceReads(WorkspaceTests.UserOf(BomTestKit.Carmen), workspace, env.Ds, env.Audit, env.Flow, env.Change, null!, env.Clock);
            Assert.Empty((await carmen.ProgramChangesAsync()).Items);
        }
        finally { await env.Db.DropAsync(); }
    }

    // ---------------------------------------------------------------- data

    private static async Task<object?> ScalarAsync(ConfigEnv env, string sql)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(ConfigEnv env, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExplainAsync(ConfigEnv env, string title, string sql)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, COSTS OFF, BUFFERS OFF) " + sql, conn);
        var sb = new StringBuilder();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) sb.AppendLine("    " + r.GetString(0));
        Log($"PLAN {title}:{Environment.NewLine}{sb}");
    }

    /// <summary>Bulk rows with plain SQL (the tables are append-only for the application role, but a throwaway database may be filled by its owner).</summary>
    private static async Task LoadBulkAsync(ConfigEnv env)
    {
        // Markings by g % 10: 0-5 open, 6-7 ITAR program ORCA, 8 EAR (ECCN 9A610.x), 9 ITAR without a program: the mix of the synthetic structure.
        const string Marks =
            """
            CASE WHEN g % 10 < 6 THEN 'None' WHEN g % 10 < 8 THEN 'Itar' WHEN g % 10 = 8 THEN 'Ear' ELSE 'Itar' END AS jur,
            CASE WHEN g % 10 = 8 THEN '9A610.x' END AS eccn,
            CASE WHEN g % 10 IN (6, 7) THEN 'XI(d)' WHEN g % 10 = 9 THEN 'VIII(h)' END AS usml,
            CASE WHEN g % 10 < 6 THEN '{}'::text[] ELSE '{EXPT}'::text[] END AS cui,
            CASE WHEN g % 10 < 6 THEN 'A' ELSE 'D' END AS dist,
            CASE WHEN g % 10 < 6 THEN NULL ELSE 'ACME' END AS office,
            CASE WHEN g % 10 < 6 THEN NULL WHEN g % 10 = 8 THEN 'Export controlled' ELSE 'Critical technology' END AS reason,
            CASE WHEN g % 10 IN (6, 7) THEN 'ORCA' END AS program
            """;
        const string Words = "(ARRAY['Bracket','Housing','Plate','Gasket','Bolt','Sensor','Gimbal','Lens','Cap','Yoke','Spacer','Clamp','Bearing','Shaft','Washer'])";
        const string Adj = "(ARRAY['Titanium','Aluminum','Steel','Composite','Anodized','Sealed','Machined','Cast'])";

        await ExecAsync(env,
            $"""
            INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, export_jurisdiction, eccn, usml_category, cui_categories,
                distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
            SELECT gen_random_uuid(), 'B-' || lpad(g::text, 6, '0'), {Adj}[1 + g % 8] || ' ' || {Words}[1 + (g / 8) % 15] || ' ' || g, 'Component', 'EA', 'Make', 'part-default',
                   jur, eccn, usml, cui, dist, office, reason, program, 'ENG', ARRAY[]::text[], now() - (g || ' minutes')::interval, 'bulk'
            FROM (SELECT g, {Marks} FROM generate_series(1, {BulkParts}) g) s;
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            SELECT gen_random_uuid(), m.id, 'A', 'InWork', true, 1, m.created_at, 'bulk', m.created_at, 'bulk' FROM plm.part_master m WHERE m.created_by = 'bulk';
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
            SELECT gen_random_uuid(), r.id, 1, 'Bulk part', jsonb_build_object(), 'Generated', r.created_at, 'bulk' FROM plm.part_revision r WHERE r.created_by = 'bulk';
            -- a third of the bulk parts are released, spread over the last 400 days (a locked revision accepts only a state change)
            UPDATE plm.part_revision r SET lifecycle_state = 'Released', state_changed_at = now() - ((substr(m.number, 3)::int % 400) || ' days')::interval - ((substr(m.number, 3)::int % 1440) || ' minutes')::interval
              FROM plm.part_master m WHERE m.id = r.master_id AND m.created_by = 'bulk' AND substr(m.number, 3)::int % 3 = 0;
            """);

        await ExecAsync(env,
            $"""
            INSERT INTO plm.document_master (id, number, name, document_type, lifecycle_id, export_jurisdiction, eccn, usml_category, cui_categories,
                distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
            SELECT gen_random_uuid(), 'D-' || lpad((g + 100000)::text, 6, '0'), {Adj}[1 + g % 8] || ' ' || {Words}[1 + (g / 8) % 15] || ' drawing ' || g,
                   (ARRAY['Specification','Drawing','CAD','Report','Procedure','Other'])[1 + g % 6], 'document-default',
                   jur, eccn, usml, cui, dist, office, reason, program, 'ENG', ARRAY[]::text[], now() - (g || ' minutes')::interval, 'bulk'
            FROM (SELECT g, {Marks} FROM generate_series(1, {Documents}) g) s;
            INSERT INTO plm.document_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            SELECT gen_random_uuid(), m.id, 'A', 'InWork', true, 1, m.created_at, 'bulk', m.created_at, 'bulk' FROM plm.document_master m WHERE m.created_by = 'bulk';
            INSERT INTO plm.document_iteration (id, revision_id, iteration, description, comment, created_at, created_by)
            SELECT gen_random_uuid(), r.id, 1, 'Bulk document', 'Generated', r.created_at, 'bulk' FROM plm.document_revision r WHERE r.created_by = 'bulk';
            UPDATE plm.document_revision r SET lifecycle_state = 'Released', state_changed_at = now() - ((substr(m.number, 3)::int % 400) || ' days')::interval - ((substr(m.number, 3)::int % 1440) || ' minutes')::interval
              FROM plm.document_master m WHERE m.id = r.master_id AND m.created_by = 'bulk' AND substr(m.number, 3)::int % 3 = 0;
            """);

        // Changes: three quarters ECRs and ECNs (open and decided), the rest problem reports; the user of the dashboard requested a few.
        await ExecAsync(env,
            $"""
            INSERT INTO chg.change (id, change_type, number, title, description, priority, change_class, reason, requester_sub, requester_username, requester_name, lifecycle_id,
                lifecycle_state, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org,
                export_authorizations, created_at, created_by, state_changed_at, state_changed_by)
            SELECT gen_random_uuid(), t, t || '-' || lpad((g + 100000)::text, 6, '0'), {Adj}[1 + g % 8] || ' ' || {Words}[1 + (g / 8) % 15] || ' change ' || g, '', (ARRAY['Low','Medium','High','Critical'])[1 + g % 4],
                   CASE WHEN t = 'PR' THEN NULL WHEN t = 'ECN' THEN 'II' ELSE 'II' END, '', CASE WHEN g % 50 = 0 THEN '{BomTestKit.Alice.Id}' ELSE 'sub-bulk-' || (g % 40) END, 'bulk' || (g % 40), 'Bulk requester ' || (g % 40),
                   lower(t) || '-default',
                   CASE WHEN t = 'PR' THEN (ARRAY['Open','Evaluating','Resolved'])[1 + g % 3] WHEN t = 'ECR' THEN (ARRAY['Draft','Draft','Approved','Closed'])[1 + g % 4] ELSE (ARRAY['Draft','Draft','Released','Implemented'])[1 + g % 4] END,
                   jur, eccn, usml, cui, dist, office, reason, program, 'ENG', ARRAY[]::text[], now() - (g || ' minutes')::interval, 'bulk', now() - (g || ' minutes')::interval, 'bulk'
            FROM (SELECT g, (ARRAY['PR','ECR','ECN'])[1 + g % 3] AS t, {Marks} FROM generate_series(1, {Changes}) g) s;
            """);

        // Audit events: the hot object B-000001 has 5,000 of its own among the 150,000 (the table is append-only and hash-chained; plain rows are enough for read paths).
        var first = Convert.ToInt64(await ScalarAsync(env, "SELECT coalesce(max(seq), 0) FROM audit.event"), CultureInfo.InvariantCulture);
        await ExecAsync(env,
            """
            INSERT INTO audit.event (seq, id, occurred_at, actor, action, object_type, object_id, outcome, payload, prev_hash, hash)
            SELECT @first + g, gen_random_uuid(), now() - (g || ' seconds')::interval, 'sub-x',
                   CASE g % 4 WHEN 0 THEN 'part.read' WHEN 1 THEN 'part.edit' WHEN 2 THEN 'part.checkin' ELSE 'part.search' END,
                   'part', CASE WHEN g % 30 = 0 THEN 'B-000001' ELSE 'B-' || lpad((g % 70000 + 1)::text, 6, '0') END, 'Success', '{"actor":{"name":"x"},"details":{}}',
                   decode(repeat('00', 32), 'hex'), decode(repeat('11', 32), 'hex')
            FROM generate_series(1, @n) g
            """, ("first", first), ("n", AuditEvents));

        var kind = NotificationKinds.All[0].Code;
        await ExecAsync(env,
            """
            INSERT INTO ntf.notification (id, recipient_sub, kind, object_type, object_number, occurrences, created_at, read_at)
            SELECT gen_random_uuid(), @sub, @kind, 'part', 'B-' || lpad((g % 70000 + 1)::text, 6, '0'), 1, now() - (g || ' minutes')::interval, CASE WHEN g % 5 = 0 THEN NULL ELSE now() END
            FROM generate_series(1, @n) g
            """, ("sub", BomTestKit.Alice.Id), ("kind", kind), ("n", Notifications));
    }
}
