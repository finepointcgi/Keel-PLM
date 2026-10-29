using System.Text.Json;
using Keel.Core;
using Keel.Programs;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Access control of programs (ADR-0004, 8.8): every count, percentage, list and total is limited by the SQL read filter and agrees with the in-memory policy for every probe user
/// (SQL oracle); what a user may not read never shows in a number, a banner or a notification; the system's maintenance notices are pointer-only and happen once.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramAccessTests(PostgresFixture pg)
{
    private static readonly (string Name, Subject Who)[] Probes =
    [
        ("alice (author, ORCA)", ProgramEnv.Alice), ("eve (no program)", ProgramEnv.Eve), ("kim (ORCA and KRAKEN)", ProgramEnv.Kim), ("mallory (viewer)", ProgramEnv.Mallory),
        ("lena (foreign, no authorization)", WorkflowEnv.Lena), ("farid (foreign, TAA-0042)", WorkflowEnv.Farid), ("audrey (auditor, no program)", ProgramEnv.Audrey),
        ("ada (administrator)", ProgramEnv.Ada), ("a US user without CUI clearance", WorkflowEnv.User("nocui", [Roles.Viewer], cui: [])),
    ];

    private static Markings Ear99() => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.B, "ENG", eccn: Markings.Ear99, controllingOffice: "ACME", distributionReason: "Administrative", program: "ORCA").Value;

    private sealed record Row(string Key, string Status, int Percent, Markings M);

    private static async Task<(ProgramEnv Env, List<Row> Rows)> SeedAsync(PostgresFixture pg)
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var rows = new List<Row>();
        var variants = new[] { ProgramEnv.Prg(), ProgramEnv.Itar(), Ear99(), ProgramEnv.Prg(), ProgramEnv.Itar(), ProgramEnv.Prg() };
        for (var i = 0; i < variants.Length; i++)
        {
            var item = await env.AddAsync(ProgramEnv.Alice, "ORCA", i % 3 == 0 ? "Milestone" : "Task", $"Item number {i} secret-title-{i}", i * 3, i * 3 + (i % 3 == 0 ? 0 : 5), "alice", markings: variants[i]);
            var status = i % 2 == 0 ? "Complete" : "InProgress";
            var p = await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", item.Key, new SetProgressCommand(status, 40, ProgramEnv.D(i * 3), status == "Complete" ? ProgramEnv.D(i * 3 + 1) : null), null);
            Assert.True(p.IsOk, string.Join("; ", p.Errors));
            rows.Add(new Row(item.Key, status, status == "Complete" ? 100 : 40, variants[i]));
        }
        return (env, rows);
    }

    [Fact]
    public async Task Counts_lists_and_percentages_agree_with_the_policy_for_every_probe_user()
    {
        var (env, rows) = await SeedAsync(pg);
        foreach (var (name, who) in Probes)
        {
            var readable = rows.Where(r => AccessPolicy.Evaluate(who, r.M, PlmAction.Read).Permitted).ToList();
            var programReadable = AccessPolicy.Evaluate(who, ProgramEnv.Prg(), PlmAction.Read).Permitted;
            var dash = await env.P.GetDashboardAsync(who, "ORCA", ProgramEnv.D(100), null);
            if (!programReadable) { Assert.Equal(OutcomeKind.NotFound, dash.Kind); continue; }
            Assert.True(dash.IsOk, name);
            Assert.Equal(readable.Count, dash.Value!.ItemCount);
            foreach (var s in ItemStatuses.All) Assert.Equal(readable.Count(r => r.Status == s), dash.Value.ByStatus.Single(c => c.Key == s).Count);

            var sched = await env.ScheduleAsync(who, "ORCA", ProgramEnv.D(100));
            Assert.Equal(readable.Select(r => r.Key).Order(StringComparer.Ordinal), sched.Items.Select(i => i.Key).Order(StringComparer.Ordinal));
            Assert.Equal(rows.Count - readable.Count, sched.Restricted.Count);
            // the SQL oracle: the same filter the services use, run by hand
            var filter = AccessPolicy.BuildReadFilter(who, "i");
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM prg.item i WHERE i.program_id = (SELECT id FROM prg.program WHERE code = 'ORCA') AND {filter.Sql}", await OpenSuperAsync(env));
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            Assert.Equal(readable.Count, (int)(long)(await cmd.ExecuteScalarAsync())!);
            // the overall percentage is computed over the readable items only
            if (readable.Count > 0)
            {
                var weights = sched.Items.Where(i => i.Kind != "Phase").Select(i => (Weight: Math.Max(1, i.PlannedEnd.DayNumber - i.PlannedStart.DayNumber), i.Percent)).ToList();
                var expected = (int)((weights.Sum(w => (long)w.Weight * w.Percent) + weights.Sum(w => w.Weight) / 2) / weights.Sum(w => w.Weight));
                Assert.Equal(expected, sched.OverallPercent);
            }
            // no title of an item the user cannot read appears anywhere in what they are given
            var json = JsonSerializer.Serialize(new object[] { sched, dash.Value });
            foreach (var r in rows.Where(r => !readable.Contains(r))) Assert.DoesNotContain($"secret-title-{rows.IndexOf(r)}", json, StringComparison.Ordinal);
            var mine = await env.P.MyWorkAsync(who, 50, null);
            Assert.Equal(who.Id == ProgramEnv.Alice.Id ? readable.Count(r => r.Status != "Complete") : 0, mine.Items.Count);
        }
    }

    private static async Task<NpgsqlConnection> OpenSuperAsync(ProgramEnv env)
    {
        var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task The_banner_of_the_schedule_covers_only_what_the_reader_is_shown()
    {
        var (env, _) = await SeedAsync(pg);
        var alice = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", ProgramEnv.D(100));
        var lena = await env.ScheduleAsync(WorkflowEnv.Lena, "ORCA", ProgramEnv.D(100));
        Assert.Contains("ITAR", alice.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", lena.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", (await env.P.GetDashboardAsync(WorkflowEnv.Lena, "ORCA", null, null)).Value!.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Due_and_overdue_milestones_are_announced_once_to_the_owner_as_pointer_only_system_notices()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var today = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);
        Task<ItemRow> Ms(string title, int offset, string? owner = "bob") =>
            env.P.AddItemAsync(ProgramEnv.Alice, "ORCA", new AddItemCommand("Milestone", null, title, null, owner, today.AddDays(offset), today.AddDays(offset), null, null), null).ContinueWith(t => t.Result.Value!);
        var soon = await Ms("Due milestone", 3);
        var late = await Ms("Late milestone", -2, "carol");
        var far = await Ms("Far milestone", 40);
        var nobody = await Ms("Nobody owns it", 2, null);
        var itar = await env.P.AddItemAsync(ProgramEnv.Alice, "ORCA", new AddItemCommand("Milestone", null, "Restricted title text", null, "bob", today.AddDays(1), today.AddDays(1), null, ProgramEnv.Itar()), null);
        Assert.True(itar.IsOk);

        var report = await env.Maintenance.RunOnceAsync();
        Assert.Equal(2, report.Due);     // the due milestone and the ITAR one (bob may read it)
        Assert.Equal(1, report.Overdue); // the late one
        var again = await env.Maintenance.RunOnceAsync();
        Assert.Equal((0, 0), (again.Due, again.Overdue));

        Assert.Equal("2", await env.TextAsync("SELECT count(*) FROM ntf.notification WHERE kind = 'milestone_due' AND recipient_sub = 'sub-bob'"));
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM ntf.notification WHERE kind = 'milestone_overdue' AND recipient_sub = 'sub-carol'"));
        Assert.Equal("0", await env.TextAsync("SELECT count(*) FROM ntf.notification WHERE recipient_sub = 'sub-alice' AND kind IN ('milestone_due', 'milestone_overdue')"));
        // pointer only: the row carries the kind, the program code and an opaque id, never a title
        Assert.Equal("program", await env.TextAsync("SELECT DISTINCT object_type FROM ntf.notification WHERE kind = 'milestone_due'"));
        Assert.Equal("ORCA", await env.TextAsync("SELECT DISTINCT object_number FROM ntf.notification WHERE kind = 'milestone_due'"));
        var dump = await env.TextAsync("SELECT string_agg(row(n.*)::text, ' ') FROM ntf.notification n");
        Assert.DoesNotContain("milestone", dump.Replace("milestone_due", "", StringComparison.Ordinal).Replace("milestone_overdue", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restricted title text", dump, StringComparison.Ordinal);
        Assert.NotEqual(far.Key, soon.Key);
        Assert.NotEqual(nobody.Key, late.Key);

        // a completed milestone is not announced; the audit log records the system's notices without any title
        var audit = await env.AuditAsync();
        Assert.Equal(2, audit.Count(a => a.Action == "program.milestone_due"));
        Assert.Equal(1, audit.Count(a => a.Action == "program.milestone_overdue"));
        var details = string.Join(" ", audit.Where(a => a.ObjectType == "program").Select(a => a.Payload));
        foreach (var title in new[] { "Due milestone", "Late milestone", "Restricted title text", "Nobody owns it" }) Assert.DoesNotContain(title, details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task My_work_lists_what_the_user_owns_across_programs_and_nothing_hidden()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice, "ORCA");
        await env.NewProgramAsync(ProgramEnv.Kim, "KRAKEN", "kim");
        var a = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Orca task", 0, 4, "kim");
        var b = await env.AddAsync(ProgramEnv.Kim, "KRAKEN", "Task", "Kraken task", 0, 4, "kim");
        var hidden = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Orca restricted task", 0, 4, "kim", markings: ProgramEnv.Itar());
        var mine = await env.P.MyWorkAsync(ProgramEnv.Kim, 20, null);
        Assert.Equal(3, mine.Items.Count);
        Assert.Contains(mine.Items, i => i.ProgramCode == "KRAKEN" && i.Key == b.Key);
        // a user who is not in KRAKEN sees nothing of it, even if she owned an item there (the program is unreadable to her)
        Assert.Empty((await env.P.MyWorkAsync(ProgramEnv.Alice, 20, null)).Items);
        Assert.NotEqual(a.Key, hidden.Key);
        // the foreign user owns nothing, and an owner who loses access would see nothing: the filter is the policy, not the owner column
        Assert.Empty((await env.P.MyWorkAsync(WorkflowEnv.Lena, 20, null)).Items);
    }
}
