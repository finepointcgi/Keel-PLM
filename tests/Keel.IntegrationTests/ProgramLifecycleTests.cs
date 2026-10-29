using Keel.Core;
using Keel.Programs;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Programs (8.8, ADR-0043): the master record, the schedule with its critical path, baselines and slip, progress rules and the database guards, on real services and a real database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramLifecycleTests(PostgresFixture pg)
{
    private static readonly DateOnly Early = new(2027, 1, 1);

    [Fact]
    public async Task A_program_is_created_by_a_member_marked_with_its_own_code_and_membership_stays_the_identity_providers()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        var before = await env.TextAsync("SELECT string_agg(username || ':' || array_to_string(programs, ','), ';' ORDER BY username) FROM identity.user_directory");

        var created = await env.NewProgramAsync(ProgramEnv.Alice, "ORCA");
        Assert.Equal("ORCA", created.Code);
        Assert.Equal("Planning", created.Status);
        Assert.Equal("ORCA", created.Markings.Program);
        Assert.Equal("alice", created.ManagerLogin);
        Assert.True(created.Permissions.CanModify);

        // creating a program never changes who belongs to a program
        Assert.Equal(before, await env.TextAsync("SELECT string_agg(username || ':' || array_to_string(programs, ','), ';' ORDER BY username) FROM identity.user_directory"));

        // a person who is not a member cannot create a program under that code, and cannot read the page
        var eveCreate = await env.P.CreateAsync(ProgramEnv.Eve, new CreateProgramCommand("KRAKEN2", "Other", null, null, null, "eve", null, null, ProgramEnv.Prg("KRAKEN2")), null);
        Assert.Equal(OutcomeKind.Forbidden, eveCreate.Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.GetAsync(ProgramEnv.Eve, "ORCA", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.GetAsync(ProgramEnv.Audrey, "ORCA", null)).Kind);
        Assert.Equal(0, (await env.P.SearchAsync(ProgramEnv.Eve, new ProgramSearchQuery(), null)).Total);
        Assert.Equal(1, (await env.P.SearchAsync(ProgramEnv.Alice, new ProgramSearchQuery("orc"), null)).Total);
        // a viewer (no author role) cannot create one either
        Assert.Equal(OutcomeKind.Forbidden, (await env.P.CreateAsync(ProgramEnv.Mallory, new CreateProgramCommand("ORCA3", "x", null, null, null, "alice", null, null, ProgramEnv.Prg("ORCA3")), null)).Kind);

        // the markings must name the program's own code
        var wrong = await env.P.CreateAsync(ProgramEnv.Alice, new CreateProgramCommand("ZED", "Zed", null, null, null, "alice", null, null, ProgramEnv.Prg("ORCA")), null);
        Assert.Equal(OutcomeKind.Invalid, wrong.Kind);
        // a manager who could not read the page is refused: no one is made a member by being named
        var kraken = await env.P.CreateAsync(ProgramEnv.Kim, new CreateProgramCommand("KRAKEN", "Kraken", null, null, null, "alice", null, null, ProgramEnv.Prg("KRAKEN")), null);
        Assert.Equal(OutcomeKind.Invalid, kraken.Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.P.CreateAsync(ProgramEnv.Alice, new CreateProgramCommand("ORCA", "Again", null, null, null, "alice", null, null, ProgramEnv.Prg("ORCA")), null)).Kind);
        // the database enforces the same rule
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.program SET program = 'OTHER' WHERE code = 'ORCA'"));
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("DELETE FROM prg.program WHERE code = 'ORCA'"));

        var updated = await env.P.UpdateAsync(ProgramEnv.Alice, "ORCA", new UpdateProgramCommand("Orca program", "New text", "Customer 9", "C-2", "Active", "kim", ProgramEnv.D(0), ProgramEnv.D(400), null), "c");
        Assert.True(updated.IsOk, string.Join("; ", updated.Errors));
        Assert.Equal("Active", updated.Value!.Status);
        Assert.Equal("kim", updated.Value.ManagerLogin);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.UpdateAsync(ProgramEnv.Alice, "ORCA", new UpdateProgramCommand("n", null, null, null, "Active", "eve", null, null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.P.UpdateAsync(ProgramEnv.Mallory, "ORCA", new UpdateProgramCommand("n", null, null, null, "Active", "alice", null, null, null), null)).Kind);
    }

    private static async Task<(ProgramEnv Env, string Phase, string A, string B, string C, string M)> DiamondAsync(PostgresFixture pg)
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var phase = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Phase", "Design", 0, 20);
        var a = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Concept", 0, 10, "bob", phase.Key);
        var b = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Detail design", 10, 20, "carol", phase.Key);
        var c = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Analysis", 10, 15, null, phase.Key);
        var m = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Milestone", "Design complete", 20, 20);
        await env.DependAsync(ProgramEnv.Alice, "ORCA", a.Key, b.Key);
        await env.DependAsync(ProgramEnv.Alice, "ORCA", a.Key, c.Key);
        await env.DependAsync(ProgramEnv.Alice, "ORCA", b.Key, m.Key);
        await env.DependAsync(ProgramEnv.Alice, "ORCA", c.Key, m.Key);
        return (env, phase.Key, a.Key, b.Key, c.Key, m.Key);
    }

    [Fact]
    public async Task The_schedule_computes_the_critical_path_float_and_the_roll_up_and_refuses_loops_in_the_service_and_the_database()
    {
        var (env, phase, a, b, c, m) = await DiamondAsync(pg);
        var view = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", Early);
        Assert.Equal([a, b, m], view.CriticalPath);
        Assert.Equal(0, view.Items.Single(i => i.Key == b).FloatDays);
        Assert.Equal(5, view.Items.Single(i => i.Key == c).FloatDays);
        Assert.True(view.Items.Single(i => i.Key == b).Critical);
        Assert.Equal(ProgramEnv.D(20), view.Finish);
        Assert.Equal(4, view.Dependencies.Count);
        Assert.Equal(phase, view.Items.Single(i => i.Key == a).ParentKey);
        Assert.Equal(0, view.OverallPercent);

        // a loop and a dependency on a phase are refused
        Assert.Equal(OutcomeKind.Invalid, (await env.P.AddDependencyAsync(ProgramEnv.Alice, "ORCA", m, a, 0, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.AddDependencyAsync(ProgramEnv.Alice, "ORCA", phase, a, 0, null)).Kind);
        // the database refuses a loop even when the service is bypassed
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync(
            "INSERT INTO prg.dependency (id, program_id, predecessor_id, successor_id, lag_days, created_at, created_by) SELECT gen_random_uuid(), p.id, s.id, f.id, 0, now(), 'x' FROM prg.program p, prg.item s, prg.item f WHERE s.key = @m AND f.key = @a",
            ("m", m), ("a", a)));
        Assert.Contains("circular", ex.MessageText, StringComparison.Ordinal);

        // progress rolls up into the phase and the program
        Assert.True((await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", a, new SetProgressCommand("Complete", 100, ProgramEnv.D(0), ProgramEnv.D(10)), null)).IsOk);
        Assert.True((await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", b, new SetProgressCommand("InProgress", 50, ProgramEnv.D(10), null), null)).IsOk);
        view = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", ProgramEnv.D(12));
        Assert.Equal(100, view.Items.Single(i => i.Key == a).Percent);
        // weights by duration: a 10, b 10, c 5 => (1000 + 500 + 0) / 25 = 60 for the phase; the milestone outside it weighs 1 => 1500 / 26 = 57.7 for the program
        Assert.Equal(60, view.Items.Single(i => i.Key == phase).Percent);
        Assert.Equal(58, view.OverallPercent);
    }

    [Fact]
    public async Task A_baseline_is_immutable_and_slip_needs_a_reason_and_is_measured_against_it()
    {
        var (env, _, a, b, _, m) = await DiamondAsync(pg);
        var baseline = await env.P.CreateBaselineAsync(ProgramEnv.Alice, "ORCA", "Contract baseline", null);
        Assert.True(baseline.IsOk);
        Assert.Equal(1, baseline.Value!.Seq);
        Assert.Equal(5, baseline.Value.ItemCount);

        // moving the end later without a reason is refused; with one it is logged
        var noReason = await env.P.UpdateItemAsync(ProgramEnv.Alice, "ORCA", b, new UpdateItemCommand("Detail design", null, "carol", ProgramEnv.D(10), ProgramEnv.D(25), null), null);
        Assert.Equal(OutcomeKind.Invalid, noReason.Kind);
        var slipped = await env.P.UpdateItemAsync(ProgramEnv.Alice, "ORCA", b, new UpdateItemCommand("Detail design", null, "carol", ProgramEnv.D(10), ProgramEnv.D(25), "Supplier input arrived late"), null);
        Assert.True(slipped.IsOk, string.Join("; ", slipped.Errors));
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM prg.schedule_change WHERE slip_days = 5"));
        // pulling it back in needs no reason
        Assert.True((await env.P.UpdateItemAsync(ProgramEnv.Alice, "ORCA", b, new UpdateItemCommand("Detail design", null, "carol", ProgramEnv.D(10), ProgramEnv.D(23), null), null)).IsOk);

        var view = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", Early);
        Assert.Equal(3, view.Items.Single(i => i.Key == b).BaselineSlipDays);
        Assert.Equal(0, view.Items.Single(i => i.Key == a).BaselineSlipDays);
        Assert.Equal(1, view.Compared!.Seq);
        // the forecast of the milestone follows its predecessor: 3 days late
        Assert.Equal(3, view.Items.Single(i => i.Key == m).SlipDays);

        // baselines and the log are immutable for the application role
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.baseline SET name = 'x'"));
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("DELETE FROM prg.baseline_item"));
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.schedule_change SET reason = 'x'"));
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("TRUNCATE prg.baseline_item"));
        // a second baseline does not change the first
        Assert.Equal(2, (await env.P.CreateBaselineAsync(ProgramEnv.Alice, "ORCA", "After slip", null)).Value!.Seq);
        var first = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", Early, 1);
        Assert.Equal(3, first.Items.Single(i => i.Key == b).BaselineSlipDays);
        var second = await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", Early, 2);
        Assert.Equal(0, second.Items.Single(i => i.Key == b).BaselineSlipDays);
    }

    [Fact]
    public async Task Progress_follows_its_rules_and_completing_a_milestone_writes_the_outbox_event_once()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var task = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Build", 0, 9, "bob");
        var milestone = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Milestone", "Prototype ready", 12, 40);
        Assert.Equal(milestone.PlannedStart, milestone.PlannedEnd); // a milestone is one day
        var gate = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Milestone", "PDR", 20, 20, gate: "PDR");
        Assert.StartsWith("GR-", gate.GateNumber, StringComparison.Ordinal);
        Assert.Equal("Draft", gate.GateState);

        Assert.Equal(OutcomeKind.Invalid, (await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task.Key, new SetProgressCommand("Sleeping", 0, null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task.Key, new SetProgressCommand("InProgress", 101, null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task.Key, new SetProgressCommand("Complete", 100, ProgramEnv.D(9), ProgramEnv.D(2)), null)).Kind);
        var started = await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task.Key, new SetProgressCommand("InProgress", 130 - 31, null, null), null);
        Assert.Equal(99, started.Value!.Percent); // an unfinished task never shows 100
        var done = await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task.Key, new SetProgressCommand("Complete", 10, ProgramEnv.D(0), ProgramEnv.D(9)), null);
        Assert.Equal(100, done.Value!.Percent);
        Assert.Equal(ProgramEnv.D(9), done.Value.ActualEnd);
        Assert.Equal("0", await env.TextAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'program.milestone_reached'")); // a task is not a milestone

        // a gate is completed by its signed approval, not by hand
        Assert.Equal(OutcomeKind.Conflict, (await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", gate.Key, new SetProgressCommand("Complete", 100, null, null), null)).Kind);
        // the database agrees
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.item SET status = 'Complete', percent_complete = 100, actual_end = now()::date WHERE key = @k", ("k", gate.Key)));

        var reached = await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", milestone.Key, new SetProgressCommand("Complete", 100, null, ProgramEnv.D(12)), null);
        Assert.True(reached.IsOk, string.Join("; ", reached.Errors));
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));
        Assert.Equal("ORCA", await env.TextAsync("SELECT payload->>'program' FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));
        // completing it again changes nothing and writes no second event
        Assert.True((await env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", milestone.Key, new SetProgressCommand("Complete", 100, null, ProgramEnv.D(12)), null)).IsOk);
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));

        // closing the program freezes the schedule
        Assert.True((await env.P.UpdateAsync(ProgramEnv.Alice, "ORCA", new UpdateProgramCommand("Program ORCA", null, null, null, "Complete", "alice", null, null, null), null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.P.AddItemAsync(ProgramEnv.Alice, "ORCA", new AddItemCommand("Task", null, "Late", null, null, ProgramEnv.D(1), ProgramEnv.D(2), null, null), null)).Kind);
    }

    [Fact]
    public async Task Owners_and_markings_of_items_follow_the_access_rules()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        // an owner who could not read the item is refused (no membership is granted by naming someone)
        var refused = await env.P.AddItemAsync(ProgramEnv.Alice, "ORCA", new AddItemCommand("Task", null, "Secret work", null, "eve", ProgramEnv.D(0), ProgramEnv.D(3), null, null), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        // an item must cover the program's markings: one for another program does not
        var other = await env.P.AddItemAsync(ProgramEnv.Alice, "ORCA", new AddItemCommand("Task", null, "Other program work", null, null, ProgramEnv.D(0), ProgramEnv.D(3), null, ProgramEnv.Prg("KRAKEN")), null);
        Assert.Equal(OutcomeKind.Invalid, other.Kind);
        // a stricter item is allowed, and a reader without the clearance sees a placeholder with dates only
        var secret = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Classified-looking title", 5, 9, markings: ProgramEnv.Itar());
        var seenByLena = await env.ScheduleAsync(WorkflowEnv.Lena, "ORCA", Early);
        Assert.Empty(seenByLena.Items);
        var hidden = Assert.Single(seenByLena.Restricted);
        Assert.Equal((ProgramEnv.D(5), ProgramEnv.D(9)), (hidden.PlannedStart, hidden.PlannedEnd));
        Assert.DoesNotContain(secret.Title, System.Text.Json.JsonSerializer.Serialize(seenByLena), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.UpdateItemAsync(WorkflowEnv.Lena, "ORCA", secret.Key, new UpdateItemCommand("x", null, null, ProgramEnv.D(5), ProgramEnv.D(9), null), null)).Kind);
    }
}
