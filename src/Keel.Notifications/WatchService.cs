using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// Watch lists (ADR-0030). A watch is a pointer. Starting one needs read access to the object (a denial is audited, like any single-object action), and the
/// list is re-authorized on every read: an object the user can no longer read becomes a row without number, name or kind that can still be removed by its opaque id.
/// Every statement filters on the signed-in user's id.
/// </summary>
public sealed class WatchService(NpgsqlDataSource dataSource, ObjectAccessResolver objects, IAuditLog audit, NotificationOptions options, TimeProvider clock) : IWatchService
{
    public async Task<WatchLevel?> GetAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        if (!AwarenessKinds.IsKnown(kind) || number.Length is 0 or > 64) return null;
        await using var cmd = dataSource.CreateCommand("SELECT level FROM ntf.watch WHERE user_sub = @u AND object_type = @t AND object_number = @n");
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("t", kind);
        cmd.Parameters.AddWithValue("n", number);
        return await cmd.ExecuteScalarAsync(ct) is string level ? WatchLevels.FromDb(level) : null;
    }

    public async Task<Outcome<WatchLevel>> SetAsync(Subject subject, string kind, string number, WatchLevel level, string? correlationId, CancellationToken ct = default)
    {
        if (!AwarenessKinds.IsKnown(kind) || number.Length is 0 or > 64) return Outcome<WatchLevel>.NotFound();
        if (await objects.ReadableAsync(subject, kind, number, ct) is null)
        {
            await audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(subject), Action = "watch.set", Outcome = AuditOutcome.Denied, ObjectType = AwarenessKinds.AuditObjectType(kind, number) ?? kind, ObjectId = number,
                CorrelationId = correlationId, Details = AuditDetails.Of(("reasons", "object.not_readable")),
            }, ct);
            return Outcome<WatchLevel>.NotFound();
        }
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", "ntf-watch:" + subject.Id);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM ntf.watch WHERE user_sub = @u AND NOT (object_type = @t AND object_number = @n)", conn, tx))
        {
            count.Parameters.AddWithValue("u", subject.Id);
            count.Parameters.AddWithValue("t", kind);
            count.Parameters.AddWithValue("n", number);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= options.MaxWatchesPerUser)
                return Outcome<WatchLevel>.Conflict($"You can watch up to {options.MaxWatchesPerUser} objects. Stop watching one to watch another.");
        }
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO ntf.watch (id, user_sub, object_type, object_number, level, created_at) VALUES (@id, @u, @t, @n, @l, @now)
            ON CONFLICT (user_sub, object_type, object_number) DO UPDATE SET level = @l
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("t", kind);
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("l", WatchLevels.ToDb(level));
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Outcome<WatchLevel>.Ok(level);
    }

    public async Task RemoveAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM ntf.watch WHERE user_sub = @u AND object_type = @t AND object_number = @n");
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("t", kind);
        cmd.Parameters.AddWithValue("n", number);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RemoveByIdAsync(Subject subject, Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM ntf.watch WHERE user_sub = @u AND id = @id");
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetLevelByIdAsync(Subject subject, Guid id, WatchLevel level, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE ntf.watch SET level = @l WHERE user_sub = @u AND id = @id");
        cmd.Parameters.AddWithValue("l", WatchLevels.ToDb(level));
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<WatchItem>> ListAsync(Subject subject, CancellationToken ct = default)
    {
        var rows = new List<(Guid Id, string Type, string Number, WatchLevel Level, DateTimeOffset Since)>();
        await using (var cmd = dataSource.CreateCommand("SELECT id, object_type, object_number, level, created_at FROM ntf.watch WHERE user_sub = @u ORDER BY created_at DESC, id DESC"))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), WatchLevels.FromDb(r.GetString(3)), r.GetFieldValue<DateTimeOffset>(4)));
        }
        var items = new List<WatchItem>();
        foreach (var row in rows)
        {
            var info = await objects.ReadableAsync(subject, row.Type, row.Number, ct);
            items.Add(info is null
                ? new WatchItem(row.Id, row.Level, row.Since, false, null, null, null, null, null, null, null)
                : new WatchItem(row.Id, row.Level, row.Since, true, info.Kind, info.Number, info.Name, info.StateLabel, info.Markings.BannerText(), info.Markings, AwarenessKinds.Path(info.Kind, info.Number)));
        }
        return items;
    }
}
