using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

public sealed partial class ChangeService
{
    private const string TaskLifecycle = "ct-default";

    private static readonly string[] TaskRoles = [Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin];

    // ---------------------------------------------------------------- change tasks

    /// <summary>
    /// Creates a change task on a change notice and assigns it to a user of the directory who can read the notice (and is not an
    /// auditor). Tasks may be created while the notice is a draft, under review or released; not once it is implemented or cancelled.
    /// </summary>
    public Task<Outcome<ChangeDetail>> CreateTaskAsync(Subject subject, string ecnNumber, CreateTaskCommand cmd, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, ecnNumber, "change.task_create", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (c.Head.Type != ChangeTypes.Ecn) return Invalid("Change tasks belong to change notices.");
            if (c.Lifecycle.State(c.State) is { IsLocked: true } && c.State is not ("UnderReview" or "Released"))
                return Fail($"{c.Head.Number} is {c.StateLabel}; it takes no new change tasks.");
            var errors = new List<string>();
            var title = cmd.Title?.Trim() ?? "";
            if (title.Length is 0 or > MaxTitleLength) errors.Add($"Title is required (at most {MaxTitleLength} characters).");
            if ((cmd.Description?.Length ?? 0) > 4000) errors.Add("Description must be at most 4000 characters.");
            if (cmd.DueDate is { } due && due < DateOnly.FromDateTime(c.Now.UtcDateTime)) errors.Add("The due date cannot be in the past.");
            if (string.IsNullOrWhiteSpace(cmd.Assignee)) errors.Add("Choose who the task is assigned to.");
            if (errors.Count > 0) return Invalid([.. errors]);

            var user = await ChangeStore.FindUserAsync(conn, tx, cmd.Assignee.Trim(), ct);
            if (user is null) return Invalid($"'{cmd.Assignee}' is not in the user directory (users appear there after they sign in).");
            if (!TaskRoles.Any(user.Subject.Roles.Contains)) return Invalid($"{user.Username} holds no role that can work on change tasks.");
            var eligible = AccessPolicy.Evaluate(user.Subject, c.Head.Markings, PlmAction.Read);
            if (!eligible.Permitted)
                return Invalid($"{user.Username} is not allowed to read {c.Head.Number} ({string.Join(", ", eligible.Reasons)}) and cannot be assigned a task on it.");

            long seq;
            await using (var nv = new NpgsqlCommand("SELECT nextval('chg.ct_seq')", conn, tx)) seq = (long)(await nv.ExecuteScalarAsync(ct))!;
            var number = $"CT-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO chg.change_task (id, number, ecn_id, title, description, assignee_sub, assignee_username, assignee_name, due_date, created_at, created_by, state_changed_at)
                VALUES (@id, @n, @ecn, @t, @d, @sub, @user, @name, @due, @at, @by, @at)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("n", number);
                ins.Parameters.AddWithValue("ecn", c.Head.Id);
                ins.Parameters.AddWithValue("t", title);
                ins.Parameters.AddWithValue("d", cmd.Description?.Trim() ?? "");
                ins.Parameters.AddWithValue("sub", user.Sub);
                ins.Parameters.AddWithValue("user", user.Username);
                ins.Parameters.AddWithValue("name", user.DisplayName);
                ins.Parameters.AddWithValue("due", cmd.DueDate is { } d ? d : DBNull.Value);
                ins.Parameters.AddWithValue("at", c.Now);
                ins.Parameters.AddWithValue("by", ChangeStore.Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, id, subject, "Task created", $"{number}: {title}, assigned to {user.Username}.", c.Now, ct);
            return Done(("task", number), ("title", title), ("assignee", user.Username), ("due", cmd.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }, ct);

    /// <summary>The assignee starts a task (Open → In progress).</summary>
    public Task<Outcome<ChangeDetail>> StartTaskAsync(Subject subject, string taskNumber, string? correlationId, CancellationToken ct = default) =>
        TaskTransitionAsync(subject, taskNumber, "InProgress", null, "change.task_start", correlationId, ct);

    /// <summary>The assignee (or an administrator) completes a task; the completion note is required.</summary>
    public Task<Outcome<ChangeDetail>> CompleteTaskAsync(Subject subject, string taskNumber, string note, string? correlationId, CancellationToken ct = default) =>
        TaskTransitionAsync(subject, taskNumber, "Done", note, "change.task_complete", correlationId, ct);

    /// <summary>Someone who may modify the change notice cancels a task; a reason is required.</summary>
    public Task<Outcome<ChangeDetail>> CancelTaskAsync(Subject subject, string taskNumber, string reason, string? correlationId, CancellationToken ct = default) =>
        TaskTransitionAsync(subject, taskNumber, "Cancelled", reason, "change.task_cancel", correlationId, ct);

    private async Task<Outcome<ChangeDetail>> TaskTransitionAsync(Subject subject, string taskNumber, string to, string? text, string action, string? correlationId, CancellationToken ct)
    {
        string? ecnNumber;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT e.number FROM chg.change_task t JOIN chg.change e ON e.id = t.ecn_id WHERE t.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", taskNumber);
            ecnNumber = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        if (ecnNumber is null) return Outcome<ChangeDetail>.NotFound();

        // Read access to the notice is enough to reach the task; who may take which transition is decided below.
        return await MutateAsync(subject, ecnNumber, action, correlationId, PlmAction.Read, async (c, conn, tx) =>
        {
            (Guid Id, string State, string AssigneeSub, string Title)? task = null;
            await using (var sel = new NpgsqlCommand(
                "SELECT t.id, t.lifecycle_state, t.assignee_sub, t.title FROM chg.change_task t WHERE t.number = @n AND t.ecn_id = @e FOR UPDATE", conn, tx))
            {
                sel.Parameters.AddWithValue("n", taskNumber);
                sel.Parameters.AddWithValue("e", c.Head.Id);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) task = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3));
            }
            if (task is null) return Fail($"{c.Head.Number} has no task {taskNumber}.");
            var (taskId, state, assignee, title) = task.Value;

            var lifecycle = await Lifecycle.LoadAsync(conn, tx, TaskLifecycle, ct) ?? throw new InvalidOperationException("Task lifecycle missing.");
            var transition = lifecycle.Transitions.FirstOrDefault(t => t.From == state && t.To == to && t.Via == "manual");
            if (transition is null) return Fail($"Task {taskNumber} is {lifecycle.State(state)?.Label ?? state}; it cannot move to {lifecycle.State(to)?.Label ?? to}.");

            var isAssignee = assignee == subject.Id;
            switch (to)
            {
                case "InProgress" when !isAssignee:
                    return Forbid("task.not_assignee");
                case "Done" when !isAssignee && !AccessPolicy.Evaluate(subject, c.Head.Markings, PlmAction.Administer).Permitted:
                    return Forbid("task.not_assignee");
                case "Cancelled":
                {
                    var modify = AccessPolicy.Evaluate(subject, c.Head.Markings, PlmAction.Modify);
                    if (!modify.Permitted) return Forbid([.. modify.Reasons]);
                    break;
                }
            }
            if (to is "Done" or "Cancelled")
            {
                if (string.IsNullOrWhiteSpace(text)) return Invalid(to == "Done" ? "A completion note is required." : "A reason is required to cancel a task.");
                if (text.Length > 2000) return Invalid("The text must be at most 2000 characters.");
            }
            if (c.State is "Implemented" or "Cancelled")
                return Fail($"{c.Head.Number} is {c.StateLabel}; its tasks are frozen.");

            await using var up = new NpgsqlCommand(
                """
                UPDATE chg.change_task SET lifecycle_state = @to, state_changed_at = @at,
                       started_at = CASE WHEN @to = 'InProgress' THEN @at ELSE started_at END,
                       completed_at = CASE WHEN @to IN ('Done', 'Cancelled') THEN @at ELSE completed_at END,
                       completed_by = CASE WHEN @to IN ('Done', 'Cancelled') THEN @by ELSE completed_by END,
                       completion_note = CASE WHEN @to = 'Done' THEN @text ELSE completion_note END,
                       cancel_reason = CASE WHEN @to = 'Cancelled' THEN @text ELSE cancel_reason END
                WHERE id = @id
                """, conn, tx);
            up.Parameters.AddWithValue("to", to);
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("by", ChangeStore.Who(subject));
            up.Parameters.AddWithValue("text", (object?)text?.Trim() ?? DBNull.Value);
            up.Parameters.AddWithValue("id", taskId);
            await up.ExecuteNonQueryAsync(ct);
            var verb = to switch { "InProgress" => "Task started", "Done" => "Task completed", _ => "Task cancelled" };
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, taskId, subject, verb, $"{taskNumber}: {title}{(string.IsNullOrWhiteSpace(text) ? "" : $". {text.Trim()}")}", c.Now, ct);
            return Done(("task", taskNumber), ("from", state), ("to", to), ("note", text?.Trim()));
        }, ct);
    }

    /// <summary>The caller's open change tasks, on change notices they may still read.</summary>
    public async Task<MyChangeTasks> MyTasksAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "e");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var items = new List<MyChangeTask>();
        var markings = new List<Markings>();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT t.id, t.number, t.title, t.lifecycle_state, COALESCE(ls.label, t.lifecycle_state), t.due_date, e.number, e.title, {MarkingColumns.Select("e")}
            FROM chg.change_task t
            JOIN chg.change e ON e.id = t.ecn_id
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = t.lifecycle_id AND ls.state = t.lifecycle_state
            WHERE {filter.Sql} AND t.assignee_sub = @sub AND t.lifecycle_state IN ('Open', 'InProgress')
            ORDER BY t.due_date NULLS LAST, t.number
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("sub", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 8);
                DateOnly? due = r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5));
                markings.Add(m);
                items.Add(new MyChangeTask(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), due, due is { } d && d < today, r.GetString(6), r.GetString(7), m.BannerText()));
            }
        }
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.task_list", AuditOutcome.Success, "change_task", null, correlationId,
            ("returned", items.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return new MyChangeTasks(items, Markings.AggregateBanner(markings));
    }
}
