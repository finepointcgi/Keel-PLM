using Npgsql;
using NpgsqlTypes;

namespace Keel.Audit;

public interface IAuditLog
{
    /// <summary>Appends inside the caller's transaction, so the business change and its audit event commit together.</summary>
    Task<AuditRecord> AppendAsync(NpgsqlTransaction transaction, AuditEntry entry, CancellationToken ct = default);

    /// <summary>Appends in a dedicated transaction. Used for reads and access denials, which have no business transaction.</summary>
    Task<AuditRecord> AppendAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed class PostgresAuditLog(NpgsqlDataSource dataSource, TimeProvider clock) : IAuditLog
{
    // Advisory lock key serializing appends to the single chain (ADR-0005). ASCII "KEELAUDT".
    internal const long ChainLockKey = 0x4B45454C41554454;

    public async Task<AuditRecord> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var record = await AppendAsync(tx, entry, ct);
        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<AuditRecord> AppendAsync(NpgsqlTransaction transaction, AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction has no connection.");

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, transaction))
        {
            lockCmd.Parameters.AddWithValue("k", ChainLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        long prevSeq = 0;
        var prevHash = AuditChain.GenesisHash;
        await using (var headCmd = new NpgsqlCommand("SELECT seq, hash FROM audit.event ORDER BY seq DESC LIMIT 1", conn, transaction))
        await using (var reader = await headCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                prevSeq = reader.GetInt64(0);
                prevHash = (byte[])reader[1];
            }
        }

        var seq = prevSeq + 1;
        var id = Guid.CreateVersion7();
        var occurredAt = AuditChain.Truncate(clock.GetUtcNow());
        var payload = AuditChain.CanonicalPayload(id, occurredAt, entry);
        var hash = AuditChain.ComputeHash(prevHash, seq, payload);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO audit.event (seq, id, occurred_at, actor, action, object_type, object_id, outcome, correlation_id, payload, prev_hash, hash)
            VALUES (@seq, @id, @at, @actor, @action, @otype, @oid, @outcome, @corr, @payload, @prev, @hash)
            """, conn, transaction))
        {
            insert.Parameters.AddWithValue("seq", seq);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("at", occurredAt);
            insert.Parameters.AddWithValue("actor", entry.Actor.Id);
            insert.Parameters.AddWithValue("action", entry.Action);
            insert.Parameters.Add(new NpgsqlParameter("otype", NpgsqlDbType.Text) { Value = (object?)entry.ObjectType ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Text) { Value = (object?)entry.ObjectId ?? DBNull.Value });
            insert.Parameters.AddWithValue("outcome", entry.Outcome.ToString());
            insert.Parameters.Add(new NpgsqlParameter("corr", NpgsqlDbType.Text) { Value = (object?)entry.CorrelationId ?? DBNull.Value });
            insert.Parameters.AddWithValue("payload", payload);
            insert.Parameters.AddWithValue("prev", prevHash);
            insert.Parameters.AddWithValue("hash", hash);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return new AuditRecord(seq, id, occurredAt, entry.Actor.Id, entry.Action, entry.ObjectType, entry.ObjectId,
            entry.Outcome, entry.CorrelationId, payload, prevHash, hash);
    }
}
