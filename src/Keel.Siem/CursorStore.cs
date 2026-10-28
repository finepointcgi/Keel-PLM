using Keel.Audit;
using Npgsql;

namespace Keel.Siem;

/// <summary>Delivery position of one destination and stream (row of <c>siem.cursor</c>).</summary>
public sealed record SiemCursor(string Destination, string Stream, long LastSeq, byte[] LastHash, long ForwardedTotal, long? ChainBreakSeq, string? ChainBreakNote);

/// <summary>Reads and advances <c>siem.cursor</c>. The cursor moves only after the destination accepted a batch, so a crash resends at most that batch (at-least-once).</summary>
public sealed class CursorStore(NpgsqlDataSource dataSource, TimeProvider clock)
{
    public const string AuditStream = "audit";
    public const string CheckpointStream = "checkpoint";

    public async Task<SiemCursor> GetOrCreateAsync(string destination, string stream, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO siem.cursor (destination, stream, updated_at) VALUES (@d, @s, @at) ON CONFLICT DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue("d", destination);
            insert.Parameters.AddWithValue("s", stream);
            insert.Parameters.AddWithValue("at", clock.GetUtcNow());
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(
            "SELECT last_seq, last_hash, forwarded_total, chain_break_seq, chain_break_note FROM siem.cursor WHERE destination = @d AND stream = @s", conn);
        cmd.Parameters.AddWithValue("d", destination);
        cmd.Parameters.AddWithValue("s", stream);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new SiemCursor(destination, stream, r.GetInt64(0), (byte[])r[1], r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    public async Task AdvanceAsync(string destination, string stream, long lastSeq, byte[] lastHash, int forwarded, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE siem.cursor SET last_seq = @seq, last_hash = @hash, forwarded_total = forwarded_total + @n, updated_at = @at WHERE destination = @d AND stream = @s");
        cmd.Parameters.AddWithValue("seq", lastSeq);
        cmd.Parameters.AddWithValue("hash", lastHash);
        cmd.Parameters.AddWithValue("n", (long)forwarded);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("d", destination);
        cmd.Parameters.AddWithValue("s", stream);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordBreakAsync(string destination, string stream, long seq, string note, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE siem.cursor SET chain_break_seq = COALESCE(chain_break_seq, @seq), chain_break_at = COALESCE(chain_break_at, @at), chain_break_note = COALESCE(chain_break_note, @note) WHERE destination = @d AND stream = @s");
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("note", note.Length > 500 ? note[..500] : note);
        cmd.Parameters.AddWithValue("d", destination);
        cmd.Parameters.AddWithValue("s", stream);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The audit events after <paramref name="afterSeq"/>, oldest first.</summary>
    public async Task<IReadOnlyList<AuditRecord>> ReadEventsAsync(long afterSeq, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT seq, id, occurred_at, actor, action, object_type, object_id, outcome, correlation_id, payload, prev_hash, hash FROM audit.event WHERE seq > @after ORDER BY seq LIMIT @n");
        cmd.Parameters.AddWithValue("after", afterSeq);
        cmd.Parameters.AddWithValue("n", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRecord>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditRecord(r.GetInt64(0), r.GetGuid(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), Enum.Parse<AuditOutcome>(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), (byte[])r[10], (byte[])r[11]));
        return list;
    }

    public async Task<IReadOnlyList<(long Id, long Seq, byte[] Hash, DateTimeOffset CreatedAt, string KeyId)>> ReadCheckpointsAsync(long afterId, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, seq, hash, created_at, key_id FROM audit.checkpoint WHERE id > @after ORDER BY id LIMIT @n");
        cmd.Parameters.AddWithValue("after", afterId);
        cmd.Parameters.AddWithValue("n", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(long, long, byte[], DateTimeOffset, string)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), r.GetInt64(1), (byte[])r[2], new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), r.GetString(4)));
        return list;
    }

    /// <summary>Highest audit sequence number and its timestamp (for lag), or (0, null) when the log is empty.</summary>
    public async Task<(long Seq, DateTimeOffset? At)> HeadAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT seq, occurred_at FROM audit.event ORDER BY seq DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero)) : (0, null);
    }

    /// <summary>The hash currently stored for <paramref name="seq"/>, or null when no such event exists (it was deleted).</summary>
    public async Task<byte[]?> HashAtAsync(long seq, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT hash FROM audit.event WHERE seq = @seq");
        cmd.Parameters.AddWithValue("seq", seq);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
}
