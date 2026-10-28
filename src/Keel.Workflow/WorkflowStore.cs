using System.Text.Json;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

internal sealed record InstanceRow(
    Guid Id, string TemplateId, int TemplateVersion, string Definition, string Participants, string ObjectType, string ObjectKey, Guid ObjectId,
    Guid RevisionId, string ObjectRevision, Guid IterationId, string IterationLabel, string Status, string? Comment,
    string StartedBy, string StartedByUsername, string StartedByName, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? CompletedBy,
    string? CompletionNote, Markings Markings);

internal sealed record SignatureRow(
    Guid Id, Guid InstanceId, Guid TaskId, string SignerSub, string SignerUsername, string SignerName, string? SignerOrg, string MeaningCode,
    string MeaningLabel, string Decision, string? Comment, DateTimeOffset SignedAt, DateTimeOffset? AuthTime, string ObjectType, string ObjectKey,
    string Revision, Guid IterationId, string IterationLabel, string MarkingBanner, string MarkingsJson, byte[] ContentHash, byte[] SignatureHash);

internal sealed record ReasonCodeRow(string Code, string Label, string Decision, bool RequiresComment, string[] AppliesTo);

internal sealed record TemplateRow(string Id, int Version, string Name, string? Description, string ObjectType, string Definition, bool IsActive, DateTimeOffset CreatedAt, string CreatedBy);

internal sealed record EventRow(DateTimeOffset At, string ActorName, string Action, string? Detail);

/// <summary>All SQL of the workflow module. Callers supply the transaction; row locks are taken by the service.</summary>
internal static class WorkflowStore
{
    private static DateTimeOffset T(NpgsqlDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);
    private static DateTimeOffset? TN(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : T(r, i);
    private static string? SN(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---------------------------------------------------------------- instances

    private static string InstanceColumns =>
        "i.id, i.template_id, i.template_version, i.definition::text, i.participants::text, i.object_type, i.object_key, i.object_id, i.revision_id, " +
        "i.object_revision, i.iteration_id, i.iteration_label, i.status, i.comment, i.started_by, i.started_by_username, i.started_by_name, i.started_at, " +
        "i.completed_at, i.completed_by, i.completion_note, " + MarkingColumns.Select("i");

    private static InstanceRow ReadInstance(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetGuid(7), r.GetGuid(8),
        r.GetString(9), r.GetGuid(10), r.GetString(11), r.GetString(12), SN(r, 13), r.GetString(14), r.GetString(15), r.GetString(16), T(r, 17),
        TN(r, 18), SN(r, 19), SN(r, 20), MarkingColumns.Read(r, 21));

    public static async Task<InstanceRow?> LoadInstanceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {InstanceColumns} FROM wf.instance i WHERE i.id = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadInstance(r) : null;
    }

    public static async Task<Guid?> InstanceIdOfTaskAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid taskId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT instance_id FROM wf.task WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", taskId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public static async Task<bool> HasActiveInstanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string objectType, Guid objectId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM wf.instance WHERE object_id = @id AND object_type = @t AND status = 'Active'", conn, tx);
        cmd.Parameters.AddWithValue("t", objectType);
        cmd.Parameters.AddWithValue("id", objectId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public static async Task InsertInstanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, InstanceRow row, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO wf.instance (id, template_id, template_version, definition, participants, object_type, object_key, object_id, revision_id, object_revision,
                iteration_id, iteration_label, status, comment, started_by, started_by_username, started_by_name, started_at, {MarkingColumns.InsertColumns})
            VALUES (@id, @tid, @tv, @def::jsonb, @parts::jsonb, @ot, @ok, @oid, @rid, @orev, @iid, @ilabel, 'Active', @comment, @by, @byu, @byn, @at, {MarkingColumns.InsertValues})
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("tid", row.TemplateId);
        cmd.Parameters.AddWithValue("tv", row.TemplateVersion);
        cmd.Parameters.AddWithValue("def", row.Definition);
        cmd.Parameters.AddWithValue("parts", row.Participants);
        cmd.Parameters.AddWithValue("ot", row.ObjectType);
        cmd.Parameters.AddWithValue("ok", row.ObjectKey);
        cmd.Parameters.AddWithValue("oid", row.ObjectId);
        cmd.Parameters.AddWithValue("rid", row.RevisionId);
        cmd.Parameters.AddWithValue("orev", row.ObjectRevision);
        cmd.Parameters.AddWithValue("iid", row.IterationId);
        cmd.Parameters.AddWithValue("ilabel", row.IterationLabel);
        cmd.Parameters.AddWithValue("comment", (object?)row.Comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("by", row.StartedBy);
        cmd.Parameters.AddWithValue("byu", row.StartedByUsername);
        cmd.Parameters.AddWithValue("byn", row.StartedByName);
        cmd.Parameters.AddWithValue("at", row.StartedAt);
        foreach (var (k, v) in MarkingColumns.Parameters(row.Markings)) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task CompleteInstanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string status, DateTimeOffset at, string by, string? note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE wf.instance SET status = @s, completed_at = @at, completed_by = @by, completion_note = @note WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("by", by);
        cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- tokens and tasks

    public static async Task<ExecutionState> LoadExecutionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid instanceId, CancellationToken ct)
    {
        var state = new ExecutionState();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, node_id, parent_id, branch, step_index, branch_count, status, created_at, completed_at FROM wf.token WHERE instance_id = @i ORDER BY created_at, id", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", instanceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                state.Tokens.Add(new TokenState
                {
                    Id = r.GetGuid(0), NodeId = r.GetString(1), ParentId = r.IsDBNull(2) ? null : r.GetGuid(2), Branch = r.IsDBNull(3) ? null : r.GetInt32(3),
                    StepIndex = r.GetInt32(4), BranchCount = r.GetInt32(5), Status = Enum.Parse<TokenStatus>(r.GetString(6)), CreatedAt = T(r, 7), CompletedAt = TN(r, 8),
                });
        }
        state.Tasks.AddRange(await LoadTasksAsync(conn, tx, instanceId, ct));
        // Tokens are written parent first, but timestamps can tie: order them so parents always precede children.
        var ordered = new List<TokenState>();
        var pending = state.Tokens.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(t => t.ParentId is null || ordered.Any(o => o.Id == t.ParentId)).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Workflow token tree is inconsistent.");
            ordered.AddRange(ready);
            pending.RemoveAll(ready.Contains);
        }
        state.Tokens.Clear();
        state.Tokens.AddRange(ordered);
        return state;
    }

    public static async Task<List<TaskState>> LoadTasksAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid instanceId, CancellationToken ct)
    {
        var tasks = new List<TaskState>();
        await using var cmd = new NpgsqlCommand($"SELECT {TaskColumns} FROM wf.task WHERE instance_id = @i ORDER BY created_at, name, assignee_username, id", conn, tx);
        cmd.Parameters.AddWithValue("i", instanceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) tasks.Add(ReadTask(r));
        return tasks;
    }

    private const string TaskColumns =
        "id, token_id, node_id, name, kind, assignee_sub, assignee_username, assignee_name, required_role, status, decision, signature_id, due_at, created_at, completed_at, instance_id";

    private static TaskState ReadTask(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0), TokenId = r.GetGuid(1), NodeId = r.GetString(2), Name = r.GetString(3), Kind = r.GetString(4), AssigneeSub = SN(r, 5),
        AssigneeUsername = SN(r, 6), AssigneeName = SN(r, 7), RequiredRole = r.GetString(8), Status = Enum.Parse<WorkTaskStatus>(r.GetString(9)),
        Decision = SN(r, 10), SignatureId = r.IsDBNull(11) ? null : r.GetGuid(11), DueAt = TN(r, 12), CreatedAt = T(r, 13), CompletedAt = TN(r, 14),
        InstanceId = r.GetGuid(15),
    };

    public static async Task SaveExecutionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid instanceId, ExecutionState state, CancellationToken ct)
    {
        foreach (var t in state.Tokens.Where(t => t.IsNew || t.IsDirty))
        {
            await using var cmd = new NpgsqlCommand(
                t.IsNew
                    ? "INSERT INTO wf.token (id, instance_id, node_id, parent_id, branch, step_index, branch_count, status, created_at, completed_at) VALUES (@id, @i, @node, @parent, @branch, @step, @count, @status, @created, @completed)"
                    : "UPDATE wf.token SET step_index = @step, branch_count = @count, status = @status, completed_at = @completed WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("id", t.Id);
            if (t.IsNew)
            {
                cmd.Parameters.AddWithValue("i", instanceId);
                cmd.Parameters.AddWithValue("node", t.NodeId);
                cmd.Parameters.AddWithValue("parent", (object?)t.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("branch", (object?)t.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue("created", t.CreatedAt);
            }
            cmd.Parameters.AddWithValue("step", t.StepIndex);
            cmd.Parameters.AddWithValue("count", t.BranchCount);
            cmd.Parameters.AddWithValue("status", t.Status.ToString());
            cmd.Parameters.AddWithValue("completed", (object?)t.CompletedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            t.IsNew = false;
            t.IsDirty = false;
        }
        foreach (var t in state.Tasks.Where(t => t.IsNew || t.IsDirty))
        {
            await using var cmd = new NpgsqlCommand(
                t.IsNew
                    ? """
                      INSERT INTO wf.task (id, instance_id, token_id, node_id, name, kind, assignee_sub, assignee_username, assignee_name, required_role, status, decision, signature_id, due_at, created_at, completed_at)
                      VALUES (@id, @i, @token, @node, @name, @kind, @asub, @auser, @aname, @role, @status, @decision, @sig, @due, @created, @completed)
                      """
                    : "UPDATE wf.task SET assignee_sub = @asub, assignee_username = @auser, assignee_name = @aname, status = @status, decision = @decision, signature_id = @sig, completed_at = @completed WHERE id = @id",
                conn, tx);
            cmd.Parameters.AddWithValue("id", t.Id);
            if (t.IsNew)
            {
                cmd.Parameters.AddWithValue("i", instanceId);
                cmd.Parameters.AddWithValue("token", t.TokenId);
                cmd.Parameters.AddWithValue("node", t.NodeId);
                cmd.Parameters.AddWithValue("name", t.Name);
                cmd.Parameters.AddWithValue("kind", t.Kind);
                cmd.Parameters.AddWithValue("role", t.RequiredRole);
                cmd.Parameters.AddWithValue("due", (object?)t.DueAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("created", t.CreatedAt);
            }
            cmd.Parameters.AddWithValue("asub", (object?)t.AssigneeSub ?? DBNull.Value);
            cmd.Parameters.AddWithValue("auser", (object?)t.AssigneeUsername ?? DBNull.Value);
            cmd.Parameters.AddWithValue("aname", (object?)t.AssigneeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", t.Status.ToString());
            cmd.Parameters.AddWithValue("decision", (object?)t.Decision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sig", (object?)t.SignatureId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("completed", (object?)t.CompletedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            t.InstanceId = instanceId;
            t.IsNew = false;
            t.IsDirty = false;
        }
    }

    /// <summary>Open tasks the user may act on: assigned to them, or pool tasks for a role they hold. Object access is checked by the caller.</summary>
    public static async Task<List<TaskState>> LoadOpenTasksForAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, CancellationToken ct)
    {
        var tasks = new List<TaskState>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {TaskColumns} FROM wf.task
            WHERE status = 'Open' AND (assignee_sub = @sub OR (assignee_sub IS NULL AND required_role = ANY(@roles)))
            ORDER BY due_at NULLS LAST, created_at, id
            """, conn, tx);
        cmd.Parameters.AddWithValue("sub", subject.Id);
        cmd.Parameters.AddWithValue("roles", CandidateRoles(subject));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) tasks.Add(ReadTask(r));
        return tasks;
    }

    /// <summary>Roles a pool task may require for this user: those held, plus plm-viewer when any viewer-or-higher role is held.</summary>
    private static string[] CandidateRoles(Subject s) =>
        [.. s.Roles, .. WorkflowRoles.Satisfies(s.Roles, Roles.Viewer) ? [Roles.Viewer] : Array.Empty<string>()];

    // ---------------------------------------------------------------- signatures and events

    private const string SignatureColumns =
        "id, instance_id, task_id, signer_sub, signer_username, signer_display_name, signer_org, meaning_code, meaning_label, decision, comment, signed_at, auth_time, " +
        "object_type, object_key, object_revision, iteration_id, iteration_label, marking_banner, markings::text, content_hash, signature_hash";

    private static SignatureRow ReadSignature(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), SN(r, 6), r.GetString(7), r.GetString(8), r.GetString(9),
        SN(r, 10), T(r, 11), TN(r, 12), r.GetString(13), r.GetString(14), r.GetString(15), r.GetGuid(16), r.GetString(17), r.GetString(18), r.GetString(19),
        (byte[])r[20], (byte[])r[21]);

    public static async Task InsertSignatureAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignatureRow s, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO wf.signature (id, instance_id, task_id, signer_sub, signer_username, signer_display_name, signer_org, meaning_code, meaning_label, decision, comment,
                signed_at, auth_time, object_type, object_key, object_revision, iteration_id, iteration_label, marking_banner, markings, content_hash, signature_hash)
            VALUES (@id, @inst, @task, @sub, @user, @name, @org, @code, @label, @decision, @comment, @at, @auth, @ot, @ok, @rev, @iid, @ilabel, @banner, @markings::jsonb, @chash, @shash)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("inst", s.InstanceId);
        cmd.Parameters.AddWithValue("task", s.TaskId);
        cmd.Parameters.AddWithValue("sub", s.SignerSub);
        cmd.Parameters.AddWithValue("user", s.SignerUsername);
        cmd.Parameters.AddWithValue("name", s.SignerName);
        cmd.Parameters.AddWithValue("org", (object?)s.SignerOrg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("code", s.MeaningCode);
        cmd.Parameters.AddWithValue("label", s.MeaningLabel);
        cmd.Parameters.AddWithValue("decision", s.Decision);
        cmd.Parameters.AddWithValue("comment", (object?)s.Comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", s.SignedAt);
        cmd.Parameters.AddWithValue("auth", (object?)s.AuthTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ot", s.ObjectType);
        cmd.Parameters.AddWithValue("ok", s.ObjectKey);
        cmd.Parameters.AddWithValue("rev", s.Revision);
        cmd.Parameters.AddWithValue("iid", s.IterationId);
        cmd.Parameters.AddWithValue("ilabel", s.IterationLabel);
        cmd.Parameters.AddWithValue("banner", s.MarkingBanner);
        cmd.Parameters.AddWithValue("markings", s.MarkingsJson);
        cmd.Parameters.AddWithValue("chash", s.ContentHash);
        cmd.Parameters.AddWithValue("shash", s.SignatureHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<SignatureRow>> LoadSignaturesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid instanceId, CancellationToken ct)
    {
        var list = new List<SignatureRow>();
        await using var cmd = new NpgsqlCommand($"SELECT {SignatureColumns} FROM wf.signature WHERE instance_id = @i ORDER BY signed_at, id", conn, tx);
        cmd.Parameters.AddWithValue("i", instanceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadSignature(r));
        return list;
    }

    public static async Task AppendEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid instanceId, DateTimeOffset at, string actorSub, string actorName, string action, string? detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO wf.event (instance_id, at, actor_sub, actor_name, action, detail) VALUES (@i, @at, @sub, @name, @action, @detail)", conn, tx);
        cmd.Parameters.AddWithValue("i", instanceId);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("sub", actorSub);
        cmd.Parameters.AddWithValue("name", actorName);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<EventRow>> LoadEventsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid instanceId, CancellationToken ct)
    {
        var list = new List<EventRow>();
        await using var cmd = new NpgsqlCommand("SELECT at, actor_name, action, detail FROM wf.event WHERE instance_id = @i ORDER BY id", conn, tx);
        cmd.Parameters.AddWithValue("i", instanceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new EventRow(T(r, 0), r.GetString(1), r.GetString(2), SN(r, 3)));
        return list;
    }

    // ---------------------------------------------------------------- reference data

    public static async Task<List<ReasonCodeRow>> LoadReasonCodesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<ReasonCodeRow>();
        await using var cmd = new NpgsqlCommand("SELECT code, label, decision, requires_comment, applies_to FROM wf.reason_code WHERE is_active ORDER BY sort_order", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ReasonCodeRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), (string[])r.GetValue(4)));
        return list;
    }

    private const string TemplateColumns = "id, version, name, description, object_type, definition::text, is_active, created_at, created_by";

    private static TemplateRow ReadTemplate(NpgsqlDataReader r) => new(r.GetString(0), r.GetInt32(1), r.GetString(2), SN(r, 3), r.GetString(4), r.GetString(5), r.GetBoolean(6), T(r, 7), r.GetString(8));

    public static async Task<List<TemplateRow>> LoadTemplatesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool activeOnly, CancellationToken ct)
    {
        var list = new List<TemplateRow>();
        await using var cmd = new NpgsqlCommand($"SELECT {TemplateColumns} FROM wf.template {(activeOnly ? "WHERE is_active" : "")} ORDER BY id, version DESC", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadTemplate(r));
        return list;
    }

    public static async Task<TemplateRow?> LoadTemplateVersionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, int version, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {TemplateColumns} FROM wf.template WHERE id = @id AND version = @v", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("v", version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadTemplate(r) : null;
    }

    public static string ParticipantsJson(IReadOnlyDictionary<string, IReadOnlyList<Participant>> participants) => JsonSerializer.Serialize(participants);

    public static Dictionary<string, IReadOnlyList<Participant>> ParseParticipants(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, List<Participant>>>(json)!.ToDictionary(p => p.Key, p => (IReadOnlyList<Participant>)p.Value);
}
