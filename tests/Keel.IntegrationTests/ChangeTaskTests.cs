using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Change tasks (CT): assignment to readers of the notice, the assignee's work, the Implemented rule, the inbox.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeTaskTests(PostgresFixture pg)
{
    private static Subject Alice => ChangeEnv.Alice;
    private static Subject Bob => ChangeEnv.Bob;
    private static readonly DateOnly Tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

    [Fact]
    public async Task Tasks_go_to_directory_users_who_can_read_the_notice()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var ecn = (await env.NewEcnAsync()).Number;

        var created = await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand("Update the work instruction", "Torque values change", "bob", Tomorrow), "corr-task");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var task = Assert.Single(created.Value!.Tasks);
        Assert.Equal(("CT-000001", "Open", "bob", Tomorrow, false), (task.Number, task.State, task.AssigneeUsername, task.DueDate, task.Overdue));
        Assert.Equal((false, false, true), (task.CanStart, task.CanComplete, task.CanCancel)); // Alice is not the assignee
        Assert.Contains(created.Value.History, h => h.Action == "Task created" && h.Detail!.Contains("CT-000001", StringComparison.Ordinal));

        // Not in the directory, no work role (auditor), cannot read the notice (no program, non-U.S.), past due date, empty title.
        async Task<Outcome<ChangeDetail>> Try(string title, string assignee, DateOnly? due = null) =>
            await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand(title, null, assignee, due), null);
        Assert.Contains("not in the user directory", (await Try("t", "nobody")).Errors[0], StringComparison.Ordinal);
        Assert.Contains("no role", (await Try("t", "audrey")).Errors[0], StringComparison.Ordinal);
        Assert.Contains("not allowed to read", (await Try("t", "eve")).Errors[0], StringComparison.Ordinal);
        Assert.Contains("not allowed to read", (await Try("t", "lena")).Errors[0], StringComparison.Ordinal);
        Assert.Contains("past", (await Try("t", "bob", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)))).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await Try("", "bob")).Kind);
        // Viewers cannot create tasks (Modify), and only notices take tasks.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.CreateTaskAsync(ChangeEnv.Mallory, ecn, new CreateTaskCommand("t", null, "bob", null), null)).Kind);
        var pr = (await env.NewChangeAsync(Alice, ChangeTypes.Pr, "A problem")).Number;
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateTaskAsync(Alice, pr, new CreateTaskCommand("t", null, "bob", null), null)).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.task_create" && e.CorrelationId == "corr-task" && e.Payload.Contains("\"assignee\":\"bob\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_assignee_starts_and_completes_and_the_notice_is_implemented_only_when_every_task_is_done()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, _) = await env.ReadyEcnAsync();
        await env.ApproveAllAsync(await env.SubmitAsync(ecn));
        Assert.Equal("Released", (await env.ChangeAsync(ecn)).State);
        foreach (var (title, who) in new[] { ("Update work instruction", "bob"), ("Scrap old stock", "carol"), ("Inform supplier", "dave") })
            Assert.True((await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand(title, null, who, null), null)).IsOk);

        // Only the assignee starts; completing needs a note.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.StartTaskAsync(Alice, "CT-000001", null)).Kind);
        var started = await env.Change.StartTaskAsync(Bob, "CT-000001", "corr-start");
        Assert.Equal("In progress", started.Value!.Tasks.Single(t => t.Number == "CT-000001").StateLabel);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.StartTaskAsync(Bob, "CT-000001", null)).Kind); // already started
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.CompleteTaskAsync(ChangeEnv.Carol, "CT-000001", "not mine", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CompleteTaskAsync(Bob, "CT-000001", "  ", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.StartTaskAsync(Bob, "CT-999999", null)).Kind);

        // Not implemented while tasks are open; the transition view says why.
        var detail = await env.ChangeAsync(ecn);
        var implement = detail.Transitions.Single(t => t.To == "Implemented");
        Assert.False(implement.Allowed);
        Assert.Contains("3 change task(s) are still open", implement.Reason, StringComparison.Ordinal);
        Assert.Contains("still open", (await env.Change.TransitionAsync(Alice, ecn, "Implemented", null, null, null)).Errors[0], StringComparison.Ordinal);
        // The database repeats the rule.
        await using var direct = env.Ds.CreateCommand("UPDATE chg.change SET lifecycle_state = 'Implemented' WHERE number = $1");
        direct.Parameters.AddWithValue(ecn);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync())).SqlState);

        var done = await env.Change.CompleteTaskAsync(Bob, "CT-000001", "Instruction WI-12 rev C issued", "corr-done");
        var completed = done.Value!.Tasks.Single(t => t.Number == "CT-000001");
        Assert.Equal(("Done", "Instruction WI-12 rev C issued"), (completed.State, completed.CompletionNote));
        Assert.NotNull(completed.CompletedAt);
        // Cancelling needs Modify and a reason; the finished task is immutable.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.CancelTaskAsync(ChangeEnv.Carol, "CT-000002", "x", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CancelTaskAsync(Alice, "CT-000002", "", null)).Kind);
        Assert.True((await env.Change.CancelTaskAsync(Alice, "CT-000002", "Stock already scrapped", null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.CompleteTaskAsync(ChangeEnv.Carol, "CT-000002", "late", null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.StartTaskAsync(Bob, "CT-000001", null)).Kind);
        // An administrator may complete a task on behalf of its assignee.
        var admin = await env.Change.CompleteTaskAsync(ChangeEnv.Ada, "CT-000003", "Supplier informed by phone", null);
        Assert.True(admin.IsOk, string.Join("; ", admin.Errors));

        // All non-cancelled tasks are done: the notice can be implemented, and then takes no more tasks.
        var implemented = await env.Change.TransitionAsync(Alice, ecn, "Implemented", null, "All follow-ups done", null);
        Assert.True(implemented.IsOk, string.Join("; ", implemented.Errors));
        Assert.True(implemented.Value!.IsFinal);
        Assert.All(implemented.Value.Tasks, t => Assert.False(t.CanStart || t.CanComplete || t.CanCancel));
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand("late", null, "bob", null), null)).Kind);

        var audit = await env.AuditAsync();
        foreach (var action in new[] { "change.task_start", "change.task_complete", "change.task_cancel" })
            Assert.Contains(audit, e => e.Action == action && e.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, e => e.Action == "change.task_complete" && e.Outcome == AuditOutcome.Denied && e.Actor == ChangeEnv.Carol.Id);
        Assert.Contains(implemented.Value.History, h => h.Action == "Task completed" && h.Detail!.Contains("CT-000001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_inbox_lists_open_tasks_on_readable_notices_and_leaves_the_workflow_inbox_alone()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var ecn = (await env.NewEcnAsync()).Number;
        Assert.True((await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand("Prepare the build", null, "bob", Tomorrow), null)).IsOk);
        Assert.True((await env.Change.CreateTaskAsync(Alice, ecn, new CreateTaskCommand("Review the drawing", null, "carol", null), null)).IsOk);

        var mine = await env.Change.MyTasksAsync(Bob, "corr-inbox");
        var item = Assert.Single(mine.Items);
        Assert.Equal(("CT-000001", ecn, "Open", Tomorrow), (item.Number, item.EcnNumber, item.State, item.DueDate));
        Assert.Equal(Markings.AggregateBanner([ChangeEnv.Itar()]), mine.PageBanner);
        Assert.Empty((await env.Change.MyTasksAsync(Alice, null)).Items);

        // The workflow task inbox is a different list and is not affected.
        Assert.Empty((await env.Flow.MyTasksAsync(Bob, null)).Items);

        // Finished tasks leave the inbox; an assignee who can no longer read the notice sees nothing of it.
        Assert.True((await env.Change.CompleteTaskAsync(Bob, "CT-000001", "Done", null)).IsOk);
        Assert.Empty((await env.Change.MyTasksAsync(Bob, null)).Items);
        await env.Db.ExecuteAsSuperuserAsync("UPDATE identity.user_directory SET programs = '{}' WHERE username = 'carol'");
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.task_list" && e.CorrelationId == "corr-inbox");
        var carol = ChangeEnv.Carol with { Programs = [] };
        Assert.Empty((await env.Change.MyTasksAsync(carol, null)).Items);
    }
}
