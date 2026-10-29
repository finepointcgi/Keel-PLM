using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Integration;

/// <summary>A delivery claimed by a dispatcher together with the event, the sink and the sink's clearance profile.</summary>
public sealed record ClaimedDelivery(Guid DeliveryId, int Attempts, OutboxRow Event, SinkRecord Sink, ProfileRecord Profile);

public sealed record DeliveryStats(IReadOnlyDictionary<string, long> ByStatus, double LagSeconds);

/// <summary>
/// The dispatcher's persistence (ADR-0025). A claim is a short transaction using FOR UPDATE SKIP LOCKED that writes a lease; the HTTP call or
/// file write happens outside any transaction, and the result is a second short transaction. A crashed dispatcher's lease expires and the
/// delivery is retried (at-least-once; consumers deduplicate on the event id). This is a system component without an end user, so it reads
/// the outbox without the user read filter; everything shown to a person goes through <see cref="OutboxQueries"/>, which applies it.
/// </summary>
public sealed class DeliveryStore(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private const string Visible = "o.created_in_tx < pg_snapshot_xmin(pg_current_snapshot())";

    /// <summary>
    /// Claims due deliveries, oldest event first. A delivery is not claimed while an earlier event of the same aggregate is still pending or
    /// failed for the same sink, which gives per-aggregate ordering within a sink (no global ordering is promised).
    /// </summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(string instance, int limit, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var cmd = dataSource.CreateCommand(
            $"""
            WITH due AS (
                SELECT d.id
                FROM integration.delivery d
                JOIN integration.outbox o ON o.id = d.event_id
                JOIN integration.sink s ON s.id = d.sink_id
                WHERE s.enabled AND d.status IN ('pending', 'failed') AND d.next_attempt_at <= @now
                  AND (d.claimed_until IS NULL OR d.claimed_until < @now)
                  AND {Visible}
                  AND NOT EXISTS (
                      SELECT 1 FROM integration.delivery d2 JOIN integration.outbox o2 ON o2.id = d2.event_id
                      WHERE d2.sink_id = d.sink_id AND d2.status IN ('pending', 'failed')
                        AND o2.aggregate_type = o.aggregate_type AND o2.aggregate_number = o.aggregate_number AND o2.seq < o.seq)
                ORDER BY o.seq
                LIMIT @limit
                FOR UPDATE OF d SKIP LOCKED
            ), claimed AS (
                UPDATE integration.delivery d SET claimed_by = @by, claimed_until = @until, updated_at = @now
                FROM due WHERE d.id = due.id
                RETURNING d.id, d.attempts, d.event_id, d.sink_id
            )
            SELECT c.id, c.attempts, {OutboxRow.Columns}, {MarkingColumns.Select("o")}, {SinkRecord.Columns}, {ProfileRecord.Columns}
            FROM claimed c
            JOIN integration.outbox o ON o.id = c.event_id
            JOIN integration.sink s ON s.id = c.sink_id
            JOIN integration.clearance_profile p ON p.id = s.profile_id
            ORDER BY o.seq
            """);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("by", instance);
        cmd.Parameters.AddWithValue("until", now + lease);
        var claimed = new List<ClaimedDelivery>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            claimed.Add(new ClaimedDelivery(r.GetGuid(0), r.GetInt32(1), OutboxRow.Read(r, 2), SinkRecord.Read(r, 24), ProfileRecord.Read(r, 35)));
        return claimed;
    }

    /// <summary>Records the result of an attempt. Returns the new status.</summary>
    public async Task<string> CompleteAsync(ClaimedDelivery d, DeliveryOutcome outcome, int maxAttempts, TimeSpan backoffBase, TimeSpan backoffMax, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (outcome.Result == DeliveryResult.Defer)
        {
            // Not an attempt: the row keeps its status and attempt count and is looked at again after a pause (never a dead letter because a receiver is slow).
            await using var defer = dataSource.CreateCommand(
                """
                UPDATE integration.delivery SET last_error = @error, updated_at = @now, next_attempt_at = @next, claimed_by = NULL, claimed_until = NULL
                WHERE id = @id AND status IN ('pending', 'failed') RETURNING status
                """);
            defer.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)Truncate(outcome.Error) ?? DBNull.Value });
            defer.Parameters.AddWithValue("now", now);
            defer.Parameters.AddWithValue("next", now + (backoffBase * 6 < backoffMax ? backoffBase * 6 : backoffMax));
            defer.Parameters.AddWithValue("id", d.DeliveryId);
            return await defer.ExecuteScalarAsync(ct) as string ?? DeliveryStatus.Pending;
        }
        var attempts = d.Attempts + 1;
        string status;
        DateTimeOffset next = now;
        switch (outcome.Result)
        {
            case DeliveryResult.Delivered:
                status = DeliveryStatus.Delivered;
                break;
            case DeliveryResult.Permanent:
                status = DeliveryStatus.Dead;
                break;
            default:
                status = attempts >= maxAttempts ? DeliveryStatus.Dead : DeliveryStatus.Failed;
                next = now + Backoff.Delay(attempts, backoffBase, backoffMax, Random.Shared.NextDouble());
                break;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE integration.delivery SET status = @status, attempts = @attempts, last_attempt_at = @now, updated_at = @now, next_attempt_at = @next,
                   last_error = @error, delivered_at = CASE WHEN @status = 'delivered' THEN @now ELSE delivered_at END, claimed_by = NULL, claimed_until = NULL
            WHERE id = @id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("attempts", attempts);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("next", next);
            cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)Truncate(outcome.Error) ?? DBNull.Value });
            cmd.Parameters.AddWithValue("id", d.DeliveryId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (status == DeliveryStatus.Dead)
            await audit.AppendAsync(tx, IntegrationAudit.Entry(ActorSnapshot.System, "integration.delivery.dead", AuditOutcome.Failure, d.Event.Id.ToString("D"), d.Event.CorrelationId,
                ("sink", d.Sink.Name), ("eventType", d.Event.EventType), ("attempts", attempts.ToString(CultureInfo.InvariantCulture)), ("error", Truncate(outcome.Error))), ct);
        await tx.CommitAsync(ct);
        return status;
    }

    /// <summary>An event the sink is not cleared for: nothing is delivered, the reasons are recorded and audited.</summary>
    public async Task WithholdAsync(ClaimedDelivery d, IReadOnlyList<string> reasons, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE integration.delivery SET status = 'withheld', withheld_reason = @reasons, last_error = NULL, updated_at = @now, claimed_by = NULL, claimed_until = NULL
            WHERE id = @id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("reasons", reasons.ToArray());
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("id", d.DeliveryId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(ActorSnapshot.System, "integration.delivery.withheld", AuditOutcome.Denied, d.Event.Id.ToString("D"), d.Event.CorrelationId,
            ("sink", d.Sink.Name), ("profile", d.Profile.Name), ("eventType", d.Event.EventType), ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Gives claimed deliveries back without counting an attempt (graceful shutdown).</summary>
    public async Task ReleaseAsync(IEnumerable<Guid> deliveryIds, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE integration.delivery SET claimed_by = NULL, claimed_until = NULL, updated_at = @now WHERE id = ANY(@ids) AND status IN ('pending', 'failed')");
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("ids", deliveryIds.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DeliveryStats> StatsAsync(CancellationToken ct = default)
    {
        var byStatus = DeliveryStatus.All.ToDictionary(s => s, _ => 0L, StringComparer.Ordinal);
        await using (var cmd = dataSource.CreateCommand("SELECT status, count(*) FROM integration.delivery GROUP BY status"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) byStatus[r.GetString(0)] = r.GetInt64(1);
        // Lag: how long the oldest delivery that is due (and visible) has been waiting.
        await using var lag = dataSource.CreateCommand(
            $"""
            SELECT COALESCE(EXTRACT(EPOCH FROM (@now - min(GREATEST(d.next_attempt_at, o.occurred_at)))), 0)::float8
            FROM integration.delivery d JOIN integration.outbox o ON o.id = d.event_id JOIN integration.sink s ON s.id = d.sink_id
            WHERE s.enabled AND d.status IN ('pending', 'failed') AND d.next_attempt_at <= @now AND {Visible}
            """);
        lag.Parameters.AddWithValue("now", clock.GetUtcNow());
        return new DeliveryStats(byStatus, Math.Max(0, (double)(await lag.ExecuteScalarAsync(ct))!));
    }

    private static string? Truncate(string? s) => s is { Length: > 2000 } ? s[..2000] : s;
}

public static class Backoff
{
    /// <summary>
    /// Exponential delay for the <paramref name="attempts"/>th failed attempt (1-based): base * 2^(attempts-1) capped at <paramref name="max"/>,
    /// then jittered into [50%, 100%] of that value by <paramref name="random01"/> in [0, 1), so retries of many deliveries do not align.
    /// </summary>
    public static TimeSpan Delay(int attempts, TimeSpan baseDelay, TimeSpan max, double random01)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 30);
        var capped = Math.Min(baseDelay.TotalSeconds * Math.Pow(2, exponent), max.TotalSeconds);
        return TimeSpan.FromSeconds(capped * (0.5 + 0.5 * Math.Clamp(random01, 0, 0.999999)));
    }
}
