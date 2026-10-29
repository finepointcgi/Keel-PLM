using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>The part every quality record shares: identity, state, owner and markings. Callers evaluate the access policy themselves.</summary>
internal sealed record QHead(
    Guid Id, string Kind, string Number, string Title, string LifecycleId, string State, int Submission, string RaiserSub, string RaiserUsername, string RaiserName,
    DateTimeOffset CreatedAt, Markings Markings);

internal sealed record DirectoryUser(string Sub, string Username, string DisplayName, Subject Subject);

/// <summary>Plain SQL shared by the quality services and the review gateways.</summary>
internal static class QualityStore
{
    public static NpgsqlCommand Cmd(DbConnection conn, DbTransaction? tx, string sql) => new(sql, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);

    public static DateTimeOffset Utc(DbDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);

    public static string Who(Subject s) => s.Username ?? s.Id;

    private static string HeadColumns(string kind)
    {
        var who = QualityKinds.Raiser(kind);
        return $"c.id, c.number, c.title, c.lifecycle_id, c.lifecycle_state, c.submission_no, c.{who}_sub, c.{who}_username, c.{who}_name, c.created_at, {MarkingColumns.Select("c")}";
    }

    private static QHead ReadHead(DbDataReader r, string kind) => new(
        r.GetGuid(0), kind, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetString(7), r.GetString(8), Utc(r, 9), MarkingColumns.Read(r, 10));

    public static async Task<QHead?> LoadByNumberAsync(DbConnection conn, DbTransaction? tx, string kind, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {HeadColumns(kind)} FROM {QualityKinds.Table(kind)} c WHERE c.number = @n" + (forUpdate ? " FOR UPDATE OF c" : ""));
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r, kind) : null;
    }

    public static async Task<QHead?> LoadByIdAsync(DbConnection conn, DbTransaction? tx, string kind, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {HeadColumns(kind)} FROM {QualityKinds.Table(kind)} c WHERE c.id = @id" + (forUpdate ? " FOR UPDATE OF c" : ""));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r, kind) : null;
    }

    public static async Task<string> NextNumberAsync(DbConnection conn, DbTransaction tx, string kind, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT nextval('{QualityKinds.Sequence(kind)}')");
        var seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return $"{QualityKinds.Prefix(kind)}-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
    }

    public static async Task AppendHistoryAsync(DbConnection conn, DbTransaction tx, string kind, Guid id, string actorSub, string actorName, string action, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "INSERT INTO qual.history (object_kind, object_id, at, actor_sub, actor_name, action, detail) VALUES (@k, @id, @at, @sub, @name, @action, @detail)");
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("sub", actorSub);
        cmd.Parameters.AddWithValue("name", actorName);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static Task AppendHistoryAsync(DbConnection conn, DbTransaction tx, string kind, Guid id, Subject actor, string action, string? detail, DateTimeOffset now, CancellationToken ct) =>
        AppendHistoryAsync(conn, tx, kind, id, actor.Id, actor.DisplayName, action, detail, now, ct);

    /// <summary>
    /// Moves a record along its lifecycle. The transition must exist in the lifecycle data and carry the tag the caller's path requires
    /// ("manual", "workflow" or "system"), so a state reached only through the review cannot be reached any other way.
    /// <paramref name="extraSet"/> is constant SQL assigning more columns in the same statement (parameters named in <paramref name="extra"/>).
    /// </summary>
    public static async Task<GatewayResult> TransitionAsync(DbConnection conn, DbTransaction tx, QHead head, string to, string via, string actorLogin, DateTimeOffset now,
        string? extraSet = null, params (string Name, object? Value)[] extra)
    {
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, default) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        var transition = lifecycle.Transitions.FirstOrDefault(t => t.From == head.State && t.To == to);
        if (transition is null || transition.Via != via)
            return GatewayResult.Fail($"The lifecycle has no {via} step from {lifecycle.State(head.State)?.Label ?? head.State} to {lifecycle.State(to)?.Label ?? to}.");
        await using var cmd = Cmd(conn, tx,
            $"UPDATE {QualityKinds.Table(head.Kind)} SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by{(extraSet is null ? "" : ", " + extraSet)} WHERE id = @id AND lifecycle_state = @from");
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", head.State);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actorLogin);
        cmd.Parameters.AddWithValue("id", head.Id);
        foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() == 1 ? GatewayResult.Ok : GatewayResult.Fail($"{head.Number} changed state concurrently.");
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

    public static AuditEntry SystemEntry(string action, string objectType, string objectId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.System, Action = action, Outcome = AuditOutcome.Success, ObjectType = objectType, ObjectId = objectId, Details = AuditDetails.Of(details),
    };

    /// <summary>Finds a user of the directory (written by the workflow module at sign-in) by login name or subject.</summary>
    public static async Task<DirectoryUser?> FindUserAsync(DbConnection conn, DbTransaction? tx, string usernameOrSub, CancellationToken ct)
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
            Id = r.GetString(0), Username = r.GetString(1), DisplayName = r.GetString(2), Org = r.IsDBNull(3) ? null : r.GetString(3), Roles = Set(4), UsPerson = r.GetBoolean(5),
            Citizenship = r.IsDBNull(6) ? null : r.GetString(6), Programs = Set(7), ExportAuthorizations = Set(8), CuiCategories = Set(9),
        };
        return new DirectoryUser(subject.Id, subject.Username!, subject.DisplayName, subject);
    }

    public static async Task SetMarkingsAsync(DbConnection conn, DbTransaction tx, string kind, Guid id, Markings markings)
    {
        await using var cmd = Cmd(conn, tx, $"UPDATE {QualityKinds.Table(kind)} SET {MarkingColumns.UpdateSet} WHERE id = @id");
        foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The id the workflow engine binds an instance to: derived from the record and the submission number.</summary>
    public static Guid SubmissionId(string kind, Guid id, int submission) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"keel-quality-submission:{kind}:{id:D}:{submission}")).AsSpan(0, 16));

    public static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public static async Task<string?> LoadSnapshotAsync(DbConnection conn, DbTransaction tx, Guid submissionId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "SELECT s.snapshot FROM qual.submission s WHERE s.id = @id");
        cmd.Parameters.AddWithValue("id", submissionId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public static async Task InsertSnapshotAsync(DbConnection conn, DbTransaction tx, QHead head, int submission, string text, string actorLogin, DateTimeOffset now, CancellationToken ct)
    {
        await using var ins = Cmd(conn, tx,
            "INSERT INTO qual.submission (id, object_kind, object_id, submission_no, snapshot, snapshot_sha256, created_at, created_by) VALUES (@id, @k, @o, @n, @s, @h, @at, @by)");
        ins.Parameters.AddWithValue("id", SubmissionId(head.Kind, head.Id, submission));
        ins.Parameters.AddWithValue("k", head.Kind);
        ins.Parameters.AddWithValue("o", head.Id);
        ins.Parameters.AddWithValue("n", submission);
        ins.Parameters.AddWithValue("s", text);
        ins.Parameters.AddWithValue("h", Sha256(text));
        ins.Parameters.AddWithValue("at", now);
        ins.Parameters.AddWithValue("by", actorLogin);
        await ins.ExecuteNonQueryAsync(ct);
        await using var up = Cmd(conn, tx, $"UPDATE {QualityKinds.Table(head.Kind)} SET submission_no = @n WHERE id = @id");
        up.Parameters.AddWithValue("n", submission);
        up.Parameters.AddWithValue("id", head.Id);
        await up.ExecuteNonQueryAsync(ct);
    }

    public static string DateText(DateOnly? d) => d is null ? "none" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The action items of a record in a readable fixed form (used by the review snapshots, so the signatures cover them).</summary>
    public static async Task<List<string>> ActionLinesAsync(DbConnection conn, DbTransaction tx, string parentKind, Guid parentId, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var cmd = Cmd(conn, tx,
            "SELECT a.number, a.action_type, a.description, a.owner_username, a.due_date, a.status, a.completion_note FROM qual.action a WHERE a.parent_kind = @k AND a.parent_id = @id ORDER BY a.number");
        cmd.Parameters.AddWithValue("k", parentKind);
        cmd.Parameters.AddWithValue("id", parentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add($"  {r.GetString(0)} {r.GetString(1)} [{r.GetString(5)}] owner {r.GetString(3)}, due {DateText(r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)))}: {r.GetString(2)}"
                + (r.IsDBNull(6) ? "" : $" Result: {r.GetString(6)}"));
        return lines;
    }
}
