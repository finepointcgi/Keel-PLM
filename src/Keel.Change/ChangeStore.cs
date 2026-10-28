using System.Collections.Immutable;
using System.Data.Common;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

internal sealed record ChangeRow(
    Guid Id, string Type, string Number, string Title, string Description, string Priority, string? ChangeClass, string Reason,
    Guid? SourceId, string LifecycleId, string State, int Submission, string RequesterSub, string RequesterUsername, string RequesterName,
    string? CloseReasonCode, string? CloseReason, DateTimeOffset CreatedAt, DateTimeOffset StateChangedAt, Markings Markings);

internal sealed record AffectedRow(
    Guid Id, Guid ChangeId, string ObjectType, Guid MasterId, string Key, string Name, Guid RevisionId, string Revision, string Action,
    Guid? ResultingRevisionId, string? ResultingRevision, bool Binding, DateTimeOffset AddedAt, string AddedBy);

internal sealed record DirectoryUser(string Sub, string Username, string DisplayName, Subject Subject);

/// <summary>Plain SQL shared by the change service and the change workflow gateways. Callers evaluate the access policy themselves.</summary>
internal static class ChangeStore
{
    private static NpgsqlCommand Cmd(DbConnection conn, DbTransaction? tx, string sql) => new(sql, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);

    private static string Columns =>
        $"""
        c.id, c.change_type, c.number, c.title, c.description, c.priority, c.change_class, c.reason, c.source_id, c.lifecycle_id, c.lifecycle_state,
        c.submission_no, c.requester_sub, c.requester_username, c.requester_name, c.close_reason_code, c.close_reason, c.created_at, c.state_changed_at,
        {MarkingColumns.Select("c")}
        """;

    private static ChangeRow ReadRow(DbDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9), r.GetString(10), r.GetInt32(11), r.GetString(12), r.GetString(13), r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), Utc(r, 17), Utc(r, 18), MarkingColumns.Read(r, 19));

    public static DateTimeOffset Utc(DbDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);

    public static async Task<ChangeRow?> LoadByNumberAsync(DbConnection conn, DbTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {Columns} FROM chg.change c WHERE c.number = @n" + (forUpdate ? " FOR UPDATE OF c" : ""));
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public static async Task<ChangeRow?> LoadByIdAsync(DbConnection conn, DbTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {Columns} FROM chg.change c WHERE c.id = @id" + (forUpdate ? " FOR UPDATE OF c" : ""));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public static async Task<List<AffectedRow>> LoadAffectedAsync(DbConnection conn, DbTransaction? tx, Guid changeId, CancellationToken ct)
    {
        var rows = new List<AffectedRow>();
        await using var cmd = Cmd(conn, tx,
            """
            SELECT ao.id, ao.change_id, ao.object_type, ao.master_id, ao.object_key, ao.object_name, ao.revision_id, ao.revision, ao.change_action,
                   ao.resulting_revision_id, ao.resulting_revision, ao.binding, ao.added_at, ao.added_by
            FROM chg.affected_object ao WHERE ao.change_id = @id AND ao.removed_at IS NULL ORDER BY ao.master_id, ao.id
            """);
        cmd.Parameters.AddWithValue("id", changeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AffectedRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetGuid(6), r.GetString(7), r.GetString(8),
                r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetBoolean(11), Utc(r, 12), r.GetString(13)));
        return rows;
    }

    public static async Task AppendHistoryAsync(DbConnection conn, DbTransaction tx, Guid changeId, Guid? taskId, Subject actor, string action, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "INSERT INTO chg.history (change_id, task_id, at, actor_sub, actor_name, action, detail) VALUES (@c, @t, @at, @sub, @name, @action, @detail)");
        cmd.Parameters.AddWithValue("c", changeId);
        cmd.Parameters.AddWithValue("t", (object?)taskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("sub", actor.Id);
        cmd.Parameters.AddWithValue("name", actor.DisplayName);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Moves a change object along its lifecycle. The transition must exist in the lifecycle data and carry the tag the caller's path
    /// requires ("manual", "workflow" or "change"), so a state reached only through the workflow cannot be reached any other way.
    /// </summary>
    public static async Task<GatewayResult> TransitionAsync(DbConnection conn, DbTransaction tx, ChangeRow row, string to, string via, Subject actor, DateTimeOffset now,
        string? closeReasonCode, string? closeReason, CancellationToken ct)
    {
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, row.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {row.LifecycleId} missing.");
        var transition = lifecycle.Transitions.FirstOrDefault(t => t.From == row.State && t.To == to);
        if (transition is null || transition.Via != via)
            return GatewayResult.Fail($"Lifecycle {row.LifecycleId} has no {via} transition from {row.State} to {to}.");
        await using var cmd = Cmd(conn, tx,
            """
            UPDATE chg.change SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by,
                   close_reason_code = COALESCE(@code, close_reason_code), close_reason = COALESCE(@reason, close_reason)
            WHERE id = @id AND lifecycle_state = @from
            """);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", row.State);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        cmd.Parameters.AddWithValue("code", (object?)closeReasonCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)closeReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", row.Id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail($"{row.Number} changed state concurrently.");
    }

    public static async Task ReleaseBindingsAsync(DbConnection conn, DbTransaction tx, Guid changeId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "UPDATE chg.affected_object SET binding = false WHERE change_id = @id AND binding");
        cmd.Parameters.AddWithValue("id", changeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    /// <summary>Finds a user of the directory (written by the workflow module at sign-in) by login name or subject.</summary>
    public static async Task<DirectoryUser?> FindUserAsync(DbConnection conn, DbTransaction tx, string usernameOrSub, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            """
            SELECT sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories
            FROM identity.user_directory WHERE sub = @k OR lower(username) = lower(@k) ORDER BY (sub = @k) DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("k", usernameOrSub);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        ImmutableSortedSet<string> Set(int i) => r.GetFieldValue<string[]>(i).ToImmutableSortedSet(StringComparer.Ordinal);
        var subject = new Subject
        {
            Id = r.GetString(0),
            Username = r.GetString(1),
            DisplayName = r.GetString(2),
            Org = r.IsDBNull(3) ? null : r.GetString(3),
            Roles = Set(4),
            UsPerson = r.GetBoolean(5),
            Citizenship = r.IsDBNull(6) ? null : r.GetString(6),
            Programs = Set(7),
            ExportAuthorizations = Set(8),
            CuiCategories = Set(9),
        };
        return new DirectoryUser(subject.Id, subject.Username!, subject.DisplayName, subject);
    }

    /// <summary>Open (not final) changes the user is the requester or assignee of, by number: used to warn before markings are raised.</summary>
    public static async Task<List<DirectoryUser>> TaskAssigneesAsync(DbConnection conn, DbTransaction tx, Guid ecnId, CancellationToken ct)
    {
        var subs = new List<string>();
        await using (var cmd = Cmd(conn, tx, "SELECT DISTINCT t.assignee_sub FROM chg.change_task t WHERE t.ecn_id = @id AND t.lifecycle_state IN ('Open', 'InProgress')"))
        {
            cmd.Parameters.AddWithValue("id", ecnId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) subs.Add(r.GetString(0));
        }
        var users = new List<DirectoryUser>();
        foreach (var sub in subs)
            if (await FindUserAsync(conn, tx, sub, ct) is { } u) users.Add(u);
        return users;
    }

    public static string Who(Subject s) => s.Username ?? s.Id;
}
