using Npgsql;

namespace Keel.Audit;

public sealed record AuditQuery(long? AfterSeq = null, string? ObjectType = null, string? ObjectId = null, string? Actor = null, int Limit = 100);

public sealed class AuditQueries(NpgsqlDataSource dataSource)
{
    public const int MaxLimit = 1000;

    public async Task<IReadOnlyList<AuditRecord>> ListAsync(AuditQuery q, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT {Columns} FROM audit.event
            WHERE seq > @after
              AND (@otype::text IS NULL OR object_type = @otype)
              AND (@oid::text IS NULL OR object_id = @oid)
              AND (@actor::text IS NULL OR actor = @actor)
            ORDER BY seq
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("after", q.AfterSeq ?? 0);
        cmd.Parameters.AddWithValue("otype", (object?)q.ObjectType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("oid", (object?)q.ObjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actor", (object?)q.Actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRecord>();
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    internal const string Columns = "seq, id, occurred_at, actor, action, object_type, object_id, outcome, correlation_id, payload, prev_hash, hash";

    internal static AuditRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetGuid(1),
        new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero),
        r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        Enum.Parse<AuditOutcome>(r.GetString(7)),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetString(9),
        (byte[])r[10],
        (byte[])r[11]);
}
