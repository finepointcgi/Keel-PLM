using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// Comments on objects (ADR-0030). A comment has no markings of its own: it inherits those of its object dynamically, so whoever may read the object may read
/// its comments, and when the markings change the comments follow. Reading and posting both start with a read check of the object under the caller's live
/// attributes (a denial is audited and looks like "not found"). Posting also needs a role of viewer or higher (not auditor or integration: auditors stay read-only).
/// A comment is immutable. Removing one keeps its text in the table (the UI shows a tombstone; an auditor can still read it). The audit events
/// <c>comment.create</c> and <c>comment.delete</c> carry the comment id and the object pointer, never the text: the body is marked data and lives here.
/// </summary>
public sealed class CommentService(
    NpgsqlDataSource dataSource, ObjectAccessResolver objects, IAuditLog audit, NotificationOptions options, NotificationWriter writer, TimeProvider clock) : ICommentService
{
    public const int MaxRepliesShown = 200;

    /// <summary>Roles that may write comments: a viewer or higher. Auditors and service accounts read only.</summary>
    public static bool CanComment(Subject s) => s.Roles.Overlaps([Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin]);

    private static string AuditType(string kind, string number) => AwarenessKinds.AuditObjectType(kind, number) ?? kind;

    private async Task DenyAsync(Subject s, string action, string kind, string number, string reason, string? correlationId, CancellationToken ct) => await audit.AppendAsync(new AuditEntry
    {
        Actor = ActorSnapshot.From(s), Action = action, Outcome = AuditOutcome.Denied, ObjectType = AuditType(kind, number), ObjectId = number, CorrelationId = correlationId,
        Details = AuditDetails.Of(("reasons", reason)),
    }, ct);

    /// <summary>The object when the subject may read it. A missing object is NotFound without audit; an unreadable one is NotFound with an audited denial.</summary>
    private async Task<(ObjectInfo? Info, bool Denied)> ReadableAsync(Subject s, string kind, string number, string action, string? correlationId, CancellationToken ct)
    {
        var info = await objects.FindAsync(kind, number, ct);
        if (info is null) return (null, false);
        if (ObjectAccessResolver.CanRead(s, info.Markings)) return (info, false);
        await DenyAsync(s, action, kind, number, "object.not_readable", correlationId, ct);
        return (null, true);
    }

    // ---------------------------------------------------------------- read

    private sealed record Row(Guid Id, Guid? ParentId, string AuthorSub, string AuthorName, string Body, DateTimeOffset At, DateTimeOffset? DeletedAt, string? DeletedByName, string? DeletedReason);

    public async Task<Outcome<CommentPage>> ListAsync(Subject subject, string kind, string number, bool newestFirst, int skip, int take, string? correlationId, CancellationToken ct = default)
    {
        var (info, _) = await ReadableAsync(subject, kind, number, "comment.read", correlationId, ct);
        if (info is null) return Outcome<CommentPage>.NotFound();
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);
        var isAuditor = subject.Roles.Contains(Roles.Auditor);
        var canAdmin = AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Administer).Permitted;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        int totalThreads, totalComments;
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE parent_id IS NULL), count(*) FILTER (WHERE deleted_at IS NULL) FROM ntf.comment WHERE object_type = @t AND object_number = @n", conn))
        {
            count.Parameters.AddWithValue("t", kind);
            count.Parameters.AddWithValue("n", number);
            await using var r = await count.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (totalThreads, totalComments) = ((int)r.GetInt64(0), (int)r.GetInt64(1));
        }
        var roots = new List<Row>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT id, parent_id, author_sub, author_name, body, created_at, deleted_at, deleted_by_name, deleted_reason FROM ntf.comment
            WHERE object_type = @t AND object_number = @n AND parent_id IS NULL
            ORDER BY created_at {(newestFirst ? "DESC" : "ASC")}, id {(newestFirst ? "DESC" : "ASC")} OFFSET @skip LIMIT @take
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", kind);
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("skip", skip);
            cmd.Parameters.AddWithValue("take", take);
            roots = await ReadRowsAsync(cmd, ct);
        }
        var replies = new List<Row>();
        if (roots.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT id, parent_id, author_sub, author_name, body, created_at, deleted_at, deleted_by_name, deleted_reason FROM ntf.comment
                WHERE object_type = @t AND object_number = @n AND parent_id = ANY(@parents) ORDER BY created_at, id LIMIT @cap
                """, conn);
            cmd.Parameters.AddWithValue("t", kind);
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("parents", roots.Select(r => r.Id).ToArray());
            cmd.Parameters.AddWithValue("cap", MaxRepliesShown * take);
            replies = await ReadRowsAsync(cmd, ct);
        }

        CommentView View(Row r, IReadOnlyList<CommentView> children)
        {
            var removed = r.DeletedAt is not null;
            return new CommentView(r.Id, r.ParentId, r.AuthorName, r.AuthorSub, r.At, removed, removed && !isAuditor ? null : r.Body, r.DeletedAt,
                removed ? r.DeletedByName : null, removed && (isAuditor || r.AuthorSub == subject.Id || canAdmin) ? r.DeletedReason : null,
                !removed && (r.AuthorSub == subject.Id || canAdmin), children);
        }
        var threads = roots.Select(root => View(root, [.. replies.Where(x => x.ParentId == root.Id).Select(x => View(x, []))])).ToList();
        return Outcome<CommentPage>.Ok(new CommentPage(threads, totalComments, totalThreads, CanComment(subject), isAuditor));
    }

    private static async Task<List<Row>> ReadRowsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<Row>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new Row(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return rows;
    }

    public async Task<int?> CountAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        var info = await objects.ReadableAsync(subject, kind, number, ct);
        if (info is null) return null;
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM ntf.comment WHERE object_type = @t AND object_number = @n AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("t", kind);
        cmd.Parameters.AddWithValue("n", number);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---------------------------------------------------------------- post

    public async Task<Outcome<PostedComment>> PostAsync(Subject subject, string kind, string number, string body, Guid? parentId, string? correlationId, CancellationToken ct = default)
    {
        if (CommentText.Validate(body) is { } problem) return Outcome<PostedComment>.Invalid([problem]);
        var text = CommentText.Normalize(body);
        var (info, _) = await ReadableAsync(subject, kind, number, "comment.create", correlationId, ct);
        if (info is null) return Outcome<PostedComment>.NotFound();
        if (!CanComment(subject))
        {
            await DenyAsync(subject, "comment.create", kind, number, AccessPolicy.RoleMissing, correlationId, ct);
            return Outcome<PostedComment>.Forbidden([AccessPolicy.RoleMissing]);
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", $"ntf-comment:{kind}:{number}");
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        var now = clock.GetUtcNow();
        await using (var limit = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM ntf.comment WHERE author_sub = @u AND created_at > @since), (SELECT count(*) FROM ntf.comment WHERE object_type = @t AND object_number = @n)", conn, tx))
        {
            limit.Parameters.AddWithValue("u", subject.Id);
            limit.Parameters.AddWithValue("since", now - TimeSpan.FromMinutes(1));
            limit.Parameters.AddWithValue("t", kind);
            limit.Parameters.AddWithValue("n", number);
            await using var r = await limit.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var (recent, onObject) = (r.GetInt64(0), r.GetInt64(1));
            if (recent >= options.CommentsPerMinutePerUser) return Outcome<PostedComment>.Conflict("You are commenting too quickly. Wait a moment and try again.");
            if (onObject >= options.MaxCommentsPerObject) return Outcome<PostedComment>.Conflict($"This object has reached its limit of {options.MaxCommentsPerObject} comments.");
        }

        string? parentAuthor = null;
        if (parentId is { } pid)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT author_sub, deleted_at IS NOT NULL, parent_id IS NOT NULL FROM ntf.comment WHERE id = @id AND object_type = @t AND object_number = @n", conn, tx);
            cmd.Parameters.AddWithValue("id", pid);
            cmd.Parameters.AddWithValue("t", kind);
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<PostedComment>.Invalid(["The comment you are replying to does not exist."]);
            if (r.GetBoolean(2)) return Outcome<PostedComment>.Invalid(["Replies go one level deep: reply to the comment at the top of the thread."]);
            if (r.GetBoolean(1)) return Outcome<PostedComment>.Invalid(["That comment was removed; it cannot be replied to."]);
            parentAuthor = r.GetString(0);
        }

        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO ntf.comment (id, object_type, object_number, author_sub, author_name, body, created_at, parent_id) VALUES (@id, @t, @n, @a, @an, @b, @now, @p)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("t", kind);
            ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("a", subject.Id);
            ins.Parameters.AddWithValue("an", string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.Username ?? subject.Id : subject.DisplayName);
            ins.Parameters.AddWithValue("b", text);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        // The audit event names the comment and the object. The text is marked data and stays in ntf.comment.
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = "comment.create", Outcome = AuditOutcome.Success, ObjectType = AuditType(kind, number), ObjectId = number, CorrelationId = correlationId,
            Details = AuditDetails.Of(("comment", id.ToString("D")), ("parent", parentId?.ToString("D")), ("length", text.Length.ToString(CultureInfo.InvariantCulture))),
        }, ct);

        // Notifications: mentions first (highest priority, only people who could read the object), then the replied-to author and the watchers.
        var told = new HashSet<string>(StringComparer.Ordinal);
        foreach (var login in CommentText.Mentions(text))
        {
            var person = await DirectoryReader.FindByUsernameAsync(conn, tx, login, ct);
            if (person is null || person.Sub == subject.Id) continue;
            var request = new NotificationRequest(NotificationKinds.CommentMention, kind, number, info.Markings, subject.Id, person.Sub, id);
            if ((await writer.WriteAsync(conn, tx, request, ct)).Count > 0) told.Add(person.Sub);
        }
        if (parentAuthor is not null && parentAuthor != subject.Id && !told.Contains(parentAuthor))
            foreach (var sub in await writer.WriteAsync(conn, tx, new NotificationRequest(NotificationKinds.CommentAdded, kind, number, info.Markings, subject.Id, parentAuthor, id), ct))
                told.Add(sub);
        await writer.WriteAsync(conn, tx, new NotificationRequest(NotificationKinds.CommentAdded, kind, number, info.Markings, subject.Id, null, id), ct, told);
        await writer.AutoWatchAsync(conn, tx, subject.Id, kind, number, ct);
        await tx.CommitAsync(ct);
        return Outcome<PostedComment>.Ok(new PostedComment(id, told.Count));
    }

    // ---------------------------------------------------------------- delete (tombstone)

    public async Task<Outcome<bool>> DeleteAsync(Subject subject, Guid commentId, string reason, string? correlationId, CancellationToken ct = default)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length is 0 or > AwarenessLimits.DeleteReasonLength) return Outcome<bool>.Invalid([$"Give a reason (at most {AwarenessLimits.DeleteReasonLength} characters)."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string kind, number, author;
        bool removed;
        await using (var cmd = new NpgsqlCommand("SELECT object_type, object_number, author_sub, deleted_at IS NOT NULL FROM ntf.comment WHERE id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", commentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<bool>.NotFound();
            (kind, number, author, removed) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
        }
        var info = await objects.FindAsync(kind, number, ct);
        if (info is null) return Outcome<bool>.NotFound();
        if (!ObjectAccessResolver.CanRead(subject, info.Markings))
        {
            await audit.AppendAsync(tx, DenialEntry(subject, kind, number, "object.not_readable", correlationId, commentId), ct);
            await tx.CommitAsync(ct);
            return Outcome<bool>.NotFound();
        }
        var asAdmin = author != subject.Id && AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Administer).Permitted;
        if (author != subject.Id && !asAdmin)
        {
            await audit.AppendAsync(tx, DenialEntry(subject, kind, number, "comment.not_author_or_admin", correlationId, commentId), ct);
            await tx.CommitAsync(ct);
            return Outcome<bool>.Forbidden(["comment.not_author_or_admin"]);
        }
        if (removed) return Outcome<bool>.Conflict("This comment was already removed.");
        await using (var up = new NpgsqlCommand(
            "UPDATE ntf.comment SET deleted_at = @now, deleted_by = @by, deleted_by_name = @name, deleted_reason = @reason WHERE id = @id", conn, tx))
        {
            up.Parameters.AddWithValue("now", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", subject.Id);
            up.Parameters.AddWithValue("name", string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.Username ?? subject.Id : subject.DisplayName);
            up.Parameters.AddWithValue("reason", reason);
            up.Parameters.AddWithValue("id", commentId);
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = "comment.delete", Outcome = AuditOutcome.Success, ObjectType = AuditType(kind, number), ObjectId = number, CorrelationId = correlationId,
            Details = AuditDetails.Of(("comment", commentId.ToString("D")), ("by", asAdmin ? "administrator" : "author")),
        }, ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    private static AuditEntry DenialEntry(Subject s, string kind, string number, string reason, string? correlationId, Guid commentId) => new()
    {
        Actor = ActorSnapshot.From(s), Action = "comment.delete", Outcome = AuditOutcome.Denied, ObjectType = AuditType(kind, number), ObjectId = number, CorrelationId = correlationId,
        Details = AuditDetails.Of(("reasons", reason), ("comment", commentId.ToString("D"))),
    };

    // ---------------------------------------------------------------- mentions and references

    public async Task<Outcome<IReadOnlyList<MentionCandidate>>> MentionCandidatesAsync(Subject subject, string kind, string number, string prefix, CancellationToken ct = default)
    {
        var info = await objects.ReadableAsync(subject, kind, number, ct);
        if (info is null) return Outcome<IReadOnlyList<MentionCandidate>>.NotFound();
        prefix = (prefix ?? "").Trim();
        if (prefix.Length > 64) return Outcome<IReadOnlyList<MentionCandidate>>.Ok([]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Only people whose stored attributes let them read THIS object are offered: suggesting anyone else would tell the author who is outside the object's audience.
        var found = (await DirectoryReader.SearchAsync(conn, prefix, 200, ct))
            .Where(e => ObjectAccessResolver.CanRead(e.Subject, info.Markings))
            .Take(8)
            .Select(e => new MentionCandidate(e.Username, e.DisplayName))
            .ToList();
        return Outcome<IReadOnlyList<MentionCandidate>>.Ok(found);
    }

    public async Task<IReadOnlyDictionary<string, ObjectChip>> ResolveReferencesAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default)
    {
        var chips = new Dictionary<string, ObjectChip>(StringComparer.Ordinal);
        foreach (var n in numbers.Distinct(StringComparer.Ordinal).Take(20))
            foreach (var kind in AwarenessKinds.All)
                if (await objects.ReadableAsync(subject, kind, n, ct) is { } info)
                {
                    chips[n] = new ObjectChip(info.Kind, info.Number, info.Name, info.Markings.BannerText(), AwarenessKinds.Path(info.Kind, info.Number));
                    break;
                }
        return chips;
    }
}
