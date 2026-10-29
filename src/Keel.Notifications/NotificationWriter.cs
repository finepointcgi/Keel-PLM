using System.Data.Common;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// Turns a <see cref="NotificationRequest"/> into rows, inside the caller's transaction. Every recipient is checked against the markings of the event with the
/// attributes the directory holds for them; a recipient who cannot read the object gets nothing, a recipient the directory does not know gets nothing (fail
/// closed), a person is not told about their own action unless they asked to be, and a kind a user switched off is skipped (unless it cannot be switched off).
/// </summary>
public sealed class NotificationWriter(NotificationOptions options, TimeProvider clock, INotificationHub hub)
{
    public async Task<IReadOnlyList<string>> WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, NotificationRequest request, CancellationToken ct, IReadOnlySet<string>? exclude = null)
    {
        var kind = NotificationKinds.Find(request.Kind) ?? throw new ArgumentException($"Unknown notification kind '{request.Kind}'.", nameof(request));
        if (!AwarenessKinds.IsKnown(request.ObjectType)) throw new ArgumentException($"Unknown object kind '{request.ObjectType}'.", nameof(request));

        var candidates = new List<(string Sub, string Level)>();
        if (request.RecipientSub is { } direct) candidates.Add((direct, "all"));
        else
        {
            await using var q = new NpgsqlCommand("SELECT user_sub, level FROM ntf.watch WHERE object_type = @t AND object_number = @n", conn, tx);
            q.Parameters.AddWithValue("t", request.ObjectType);
            q.Parameters.AddWithValue("n", request.ObjectNumber);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) candidates.Add((r.GetString(0), r.GetString(1)));
        }
        if (candidates.Count == 0) return [];

        var directory = await DirectoryReader.LoadAsync(conn, tx, [.. candidates.Select(c => c.Sub)], ct);
        var prefs = await LoadKindPreferencesAsync(conn, tx, [.. candidates.Select(c => c.Sub)], kind.Code, ct);
        var written = new List<string>();
        foreach (var (sub, level) in candidates)
        {
            if (exclude?.Contains(sub) == true) continue; // already told by a higher-priority kind (a mention beats a comment)
            if (request.RecipientSub is null && level == "decisions" && !kind.IsDecision) continue;
            if (!directory.TryGetValue(sub, out var person)) continue; // unknown to the directory: nothing is sent
            if (!ObjectAccessResolver.CanRead(person.Subject, request.Markings)) continue; // the recipient could not read the object
            var pref = prefs.GetValueOrDefault(sub);
            if (pref is { InApp: false } && kind.CanDisable) continue;
            if (request.ActorSub == sub && pref?.NotifyOwn != true) continue; // never tell people about what they just did
            await InsertAsync(conn, tx, sub, kind, request, ct);
            written.Add(sub);
        }
        foreach (var sub in written) hub.Publish(sub, TimeSpan.FromMilliseconds(options.PublishDelayMilliseconds));
        return written;
    }

    private async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string recipient, NotificationKind kind, NotificationRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Writes for one recipient are serialized, so the collapse and the cap are exact under concurrency.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", "ntf:" + recipient);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        if (kind.Collapsible && options.DedupeWindowMinutes > 0)
        {
            await using var bump = new NpgsqlCommand(
                """
                UPDATE ntf.notification SET occurrences = occurrences + 1, created_at = @now, actor_sub = @actor
                WHERE id = (SELECT id FROM ntf.notification
                            WHERE recipient_sub = @r AND kind = @k AND object_type = @t AND object_number = @n AND read_at IS NULL AND created_at >= @since
                            ORDER BY created_at DESC, id DESC LIMIT 1)
                """, conn, tx);
            bump.Parameters.AddWithValue("now", now);
            bump.Parameters.Add(new NpgsqlParameter("actor", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)request.ActorSub ?? DBNull.Value });
            bump.Parameters.AddWithValue("r", recipient);
            bump.Parameters.AddWithValue("k", kind.Code);
            bump.Parameters.AddWithValue("t", request.ObjectType);
            bump.Parameters.AddWithValue("n", request.ObjectNumber);
            bump.Parameters.AddWithValue("since", now - options.DedupeWindow);
            if (await bump.ExecuteNonQueryAsync(ct) > 0) return;
        }
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO ntf.notification (id, recipient_sub, kind, object_type, object_number, ref_id, event_id, actor_sub, occurrences, created_at)
            VALUES (@id, @r, @k, @t, @n, @ref, @ev, @actor, 1, @now)
            ON CONFLICT (recipient_sub, kind, event_id) WHERE event_id IS NOT NULL DO NOTHING
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("r", recipient);
            ins.Parameters.AddWithValue("k", kind.Code);
            ins.Parameters.AddWithValue("t", request.ObjectType);
            ins.Parameters.AddWithValue("n", request.ObjectNumber);
            ins.Parameters.Add(new NpgsqlParameter("ref", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)request.RefId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ev", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)request.EventId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("actor", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)request.ActorSub ?? DBNull.Value });
            ins.Parameters.AddWithValue("now", now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using var trim = new NpgsqlCommand(
            """
            DELETE FROM ntf.notification WHERE recipient_sub = @r AND id IN
                (SELECT id FROM ntf.notification WHERE recipient_sub = @r ORDER BY created_at DESC, id DESC OFFSET @cap)
            """, conn, tx);
        trim.Parameters.AddWithValue("r", recipient);
        trim.Parameters.AddWithValue("cap", options.MaxPerUser);
        await trim.ExecuteNonQueryAsync(ct);
    }

    internal sealed record KindPreferenceRow(bool InApp, bool NotifyOwn);

    internal static async Task<Dictionary<string, KindPreferenceRow>> LoadKindPreferencesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<string> subs, string kind, CancellationToken ct)
    {
        var result = new Dictionary<string, KindPreferenceRow>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT user_sub, in_app, notify_own FROM ntf.kind_preference WHERE kind = @k AND user_sub = ANY(@subs)", conn, tx);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("subs", subs.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = new KindPreferenceRow(r.GetBoolean(1), r.GetBoolean(2));
        return result;
    }

    /// <summary>Starts a watch (level "all") unless the user's auto-watch preference is off, they already watch the object, or they are at the cap.</summary>
    public async Task AutoWatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userSub, string objectType, string objectNumber, CancellationToken ct)
    {
        if (!AwarenessKinds.IsKnown(objectType) || string.IsNullOrEmpty(userSub) || objectNumber.Length is 0 or > 64) return;
        await using (var pref = new NpgsqlCommand("SELECT auto_watch FROM ntf.preference WHERE user_sub = @u", conn, tx))
        {
            pref.Parameters.AddWithValue("u", userSub);
            if (await pref.ExecuteScalarAsync(ct) is false) return;
        }
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO ntf.watch (id, user_sub, object_type, object_number, level, created_at)
            SELECT @id, @u, @t, @n, 'all', @now
            WHERE (SELECT count(*) FROM ntf.watch WHERE user_sub = @u) < @cap
            ON CONFLICT (user_sub, object_type, object_number) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("u", userSub);
        cmd.Parameters.AddWithValue("t", objectType);
        cmd.Parameters.AddWithValue("n", objectNumber);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("cap", options.MaxWatchesPerUser);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// The sink the owning modules call inside their transactions (<see cref="INotificationSink"/>). Each call runs under a savepoint: a database error while
/// writing a notification is logged and rolled back to the savepoint, so it can never fail the release, signature or check-in that caused it.
/// </summary>
public sealed class NotificationSink : INotificationSink
{
    private readonly NotificationWriter _writer;
    private readonly ILogger<NotificationSink> _logger;

    public NotificationSink(NotificationWriter writer, ILogger<NotificationSink> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public async Task AppendAsync(DbConnection conn, DbTransaction tx, NotificationRequest request, CancellationToken ct)
    {
        var t = (NpgsqlTransaction)tx;
        await t.SaveAsync("ntf_append", ct);
        try
        {
            await _writer.WriteAsync((NpgsqlConnection)conn, t, request, ct);
            await t.ReleaseAsync("ntf_append", ct);
        }
        catch (NpgsqlException e)
        {
            _logger.LogError(e, "A {Kind} notification could not be written; the change it reports is unaffected.", request.Kind);
            await t.RollbackAsync("ntf_append", ct);
        }
    }

    public async Task AutoWatchAsync(DbConnection conn, DbTransaction tx, string userSub, string objectType, string objectNumber, CancellationToken ct)
    {
        var t = (NpgsqlTransaction)tx;
        await t.SaveAsync("ntf_watch", ct);
        try
        {
            await _writer.AutoWatchAsync((NpgsqlConnection)conn, t, userSub, objectType, objectNumber, ct);
            await t.ReleaseAsync("ntf_watch", ct);
        }
        catch (NpgsqlException e)
        {
            _logger.LogError(e, "Automatic watching of an object could not be recorded; the action that caused it is unaffected.");
            await t.RollbackAsync("ntf_watch", ct);
        }
    }
}
