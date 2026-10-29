using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// The user's notifications (ADR-0030). The stored rows are pointers; this class renders them: it reads each object again through the owning module's
/// <see cref="IObjectAccess"/> and applies the access policy to the VIEWER's live attributes, so a user who lost access (markings raised, programme left, role
/// removed) sees a neutral row and nothing about the object. Every statement filters on the signed-in user's id: no method can return another user's rows.
/// </summary>
public sealed class NotificationCenter(NpgsqlDataSource dataSource, ObjectAccessResolver objects, IAuditLog audit, TimeProvider clock) : INotificationCenter
{
    public async Task<int> CountUnreadAsync(Subject subject, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM ntf.notification WHERE recipient_sub = @u AND read_at IS NULL");
        cmd.Parameters.AddWithValue("u", subject.Id);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed record Row(Guid Id, string Kind, string ObjectType, string ObjectNumber, Guid? RefId, string? ActorSub, int Occurrences, DateTimeOffset At, bool IsRead);

    public async Task<NotificationPage> ListAsync(Subject subject, NotificationFilter filter, int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var group = filter switch
        {
            NotificationFilter.Mentions => NotificationGroups.Mentions,
            NotificationFilter.Tasks => NotificationGroups.Tasks,
            NotificationFilter.Watching => NotificationGroups.Watching,
            _ => null,
        };
        var unreadOnly = filter == NotificationFilter.Unread;
        var kinds = group is null ? null : NotificationKinds.CodesOf(group).ToArray();

        // All and Unread page in SQL. A group tab renders the (at most MaxPerUser) matching rows first and drops the restricted ones: a restricted row must not
        // reveal which kind it was, so it never appears under a kind tab, and the page is cut after that.
        var rows = new List<Row>();
        int total;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await using (var count = new NpgsqlCommand(
                "SELECT count(*) FROM ntf.notification WHERE recipient_sub = @u AND (@unread = false OR read_at IS NULL) AND (@kinds::text[] IS NULL OR kind = ANY(@kinds))", conn))
            {
                AddFilter(count, subject, unreadOnly, kinds);
                total = (int)(long)(await count.ExecuteScalarAsync(ct))!;
            }
            await using var cmd = new NpgsqlCommand(
                """
                SELECT id, kind, object_type, object_number, ref_id, actor_sub, occurrences, created_at, read_at
                FROM ntf.notification WHERE recipient_sub = @u AND (@unread = false OR read_at IS NULL) AND (@kinds::text[] IS NULL OR kind = ANY(@kinds))
                ORDER BY created_at DESC, id DESC OFFSET @skip LIMIT @take
                """, conn);
            AddFilter(cmd, subject, unreadOnly, kinds);
            cmd.Parameters.AddWithValue("skip", kinds is null ? skip : 0);
            cmd.Parameters.AddWithValue("take", kinds is null ? take : 5000);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7), !r.IsDBNull(8)));
        }

        var readable = await ResolveAsync(subject, rows.Select(r => (r.ObjectType, r.ObjectNumber)), ct);
        var actors = await ActorNamesAsync(rows.Select(r => r.ActorSub).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), ct);
        var items = new List<NotificationItem>();
        foreach (var row in rows)
        {
            readable.TryGetValue((row.ObjectType, row.ObjectNumber), out var info);
            var kind = NotificationKinds.Find(row.Kind);
            if (info is null || kind is null)
            {
                if (kinds is null) items.Add(new NotificationItem(row.Id, true, row.IsRead, 1, row.At, NotificationItem.RestrictedText, null, null, null, null, null, null, null, null));
                continue;
            }
            var actor = row.ActorSub is { } a && actors.TryGetValue(a, out var name) ? name : null;
            items.Add(new NotificationItem(row.Id, false, row.IsRead, row.Occurrences, row.At, NotificationKinds.Sentence(kind, actor), kind.Group, info.Number, info.Name,
                AwarenessKinds.Label(info.Kind), info.Markings.BannerText(), info.Markings, actor, NotificationKinds.Href(kind, row.ObjectType, row.ObjectNumber, row.RefId)));
        }
        if (kinds is not null)
        {
            total = items.Count;
            items = [.. items.Skip(skip).Take(take)];
        }

        var shown = items.Where(i => !i.Restricted).Select(i => i.Number!).ToList();
        if (shown.Count > 0) await AuditReadAsync(subject, shown, ct);
        return new NotificationPage(items, total, await CountUnreadAsync(subject, ct));
    }

    private static void AddFilter(NpgsqlCommand cmd, Subject subject, bool unreadOnly, string[]? kinds)
    {
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("unread", unreadOnly);
        cmd.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)kinds ?? DBNull.Value });
    }

    private async Task<Dictionary<(string Type, string Number), ObjectInfo>> ResolveAsync(Subject subject, IEnumerable<(string Type, string Number)> pointers, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), ObjectInfo>();
        foreach (var p in pointers.Distinct())
            if (await objects.ReadableAsync(subject, p.Type, p.Number, ct) is { } info) result[p] = info;
        return result;
    }

    private async Task<Dictionary<string, string>> ActorNamesAsync(string[] subs, CancellationToken ct)
    {
        if (subs.Length == 0) return [];
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var directory = await DirectoryReader.LoadAsync(conn, null, subs, ct);
        return directory.ToDictionary(d => d.Key, d => d.Value.DisplayName, StringComparer.Ordinal);
    }

    private async Task AuditReadAsync(Subject s, List<string> numbers, CancellationToken ct) => await audit.AppendAsync(new AuditEntry
    {
        Actor = ActorSnapshot.From(s),
        Action = "workspace.read",
        Outcome = AuditOutcome.Success,
        ObjectType = "workspace",
        ObjectId = "notifications",
        Details = AuditDetails.Of(("returned", numbers.Count.ToString(CultureInfo.InvariantCulture)), ("items", string.Join(",", numbers.Distinct(StringComparer.Ordinal)))),
    }, ct);

    public async Task<bool> MarkReadAsync(Subject subject, Guid id, CancellationToken ct = default) => await MarkReadAsync(subject, [id], ct) > 0;

    public async Task<int> MarkReadAsync(Subject subject, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var cmd = dataSource.CreateCommand(
            "UPDATE ntf.notification SET read_at = @now, seen_at = COALESCE(seen_at, @now) WHERE recipient_sub = @u AND id = ANY(@ids) AND read_at IS NULL");
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(Subject subject, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE ntf.notification SET read_at = @now, seen_at = COALESCE(seen_at, @now) WHERE recipient_sub = @u AND read_at IS NULL");
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("u", subject.Id);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<NotificationPreferences> GetPreferencesAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var autoWatch = true;
        await using (var cmd = new NpgsqlCommand("SELECT auto_watch FROM ntf.preference WHERE user_sub = @u", conn))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            if (await cmd.ExecuteScalarAsync(ct) is bool b) autoWatch = b;
        }
        var chosen = new Dictionary<string, (bool InApp, bool Own)>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT kind, in_app, notify_own FROM ntf.kind_preference WHERE user_sub = @u", conn))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) chosen[r.GetString(0)] = (r.GetBoolean(1), r.GetBoolean(2));
        }
        var kinds = NotificationKinds.All.Select(k => chosen.TryGetValue(k.Code, out var c)
            ? new KindPreference(k, c.InApp || !k.CanDisable, c.Own)
            : new KindPreference(k, true, false)).ToList();
        return new NotificationPreferences(autoWatch, kinds);
    }

    public async Task<bool> SetKindPreferenceAsync(Subject subject, string kind, bool inApp, bool notifyOwn, CancellationToken ct = default)
    {
        var def = NotificationKinds.Find(kind);
        if (def is null || (!inApp && !def.CanDisable)) return false;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        if (inApp && !notifyOwn)
        {
            await using var del = new NpgsqlCommand("DELETE FROM ntf.kind_preference WHERE user_sub = @u AND kind = @k", conn);
            del.Parameters.AddWithValue("u", subject.Id);
            del.Parameters.AddWithValue("k", kind);
            await del.ExecuteNonQueryAsync(ct);
            return true;
        }
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO ntf.kind_preference (user_sub, kind, in_app, notify_own) VALUES (@u, @k, @a, @o)
            ON CONFLICT (user_sub, kind) DO UPDATE SET in_app = @a, notify_own = @o
            """, conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("a", inApp);
        cmd.Parameters.AddWithValue("o", notifyOwn);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task SetAutoWatchAsync(Subject subject, bool autoWatch, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO ntf.preference (user_sub, auto_watch, updated_at) VALUES (@u, @a, @now)
            ON CONFLICT (user_sub) DO UPDATE SET auto_watch = @a, updated_at = @now
            """);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("a", autoWatch);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
