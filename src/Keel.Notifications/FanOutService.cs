using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Notifications;

/// <summary>Which watcher notifications an outbox event produces (pure, unit-tested).</summary>
public static class FanOutRules
{
    /// <summary>One target: the notification kind and the object whose watchers are told.</summary>
    public sealed record Target(string Kind, string ObjectType, string ObjectNumber);

    /// <summary>
    /// The targets of an event. A baseline or manufacturing BOM that is established or released for a part also tells the watchers of that part. Events with no
    /// meaning for a watcher (the BOM publication events of the ERP contract) produce none.
    /// </summary>
    public static IReadOnlyList<Target> Map(string eventType, string aggregateType, string aggregateNumber, JsonObject? payload)
    {
        switch (eventType)
        {
            case EventTypes.PartReleased: return [new(NotificationKinds.WatchedReleased, AwarenessKinds.Part, aggregateNumber)];
            case EventTypes.PartObsoleted: return [new(NotificationKinds.WatchedObsoleted, AwarenessKinds.Part, aggregateNumber)];
            case EventTypes.DocumentReleased: return [new(NotificationKinds.WatchedReleased, AwarenessKinds.Document, aggregateNumber)];
            case EventTypes.DocumentObsoleted: return [new(NotificationKinds.WatchedObsoleted, AwarenessKinds.Document, aggregateNumber)];
            case EventTypes.ChangeApproved: return [new(NotificationKinds.WatchedApproved, AwarenessKinds.Change, aggregateNumber)];
            case EventTypes.ChangeReleased: return [new(NotificationKinds.WatchedReleased, AwarenessKinds.Change, aggregateNumber)];
            case EventTypes.BaselineEstablished:
            {
                var targets = new List<Target> { new(NotificationKinds.WatchedEstablished, AwarenessKinds.Baseline, aggregateNumber) };
                if (payload?["root"]?["number"]?.GetValue<string>() is { Length: > 0 } root) targets.Add(new(NotificationKinds.WatchedEstablished, AwarenessKinds.Part, root));
                return targets;
            }
            case EventTypes.MbomReleased:
            {
                var targets = new List<Target> { new(NotificationKinds.WatchedEstablished, AwarenessKinds.Mbom, aggregateNumber) };
                if (payload?["part"]?["number"]?.GetValue<string>() is { Length: > 0 } part) targets.Add(new(NotificationKinds.WatchedEstablished, AwarenessKinds.Part, part));
                return targets;
            }
            default: return [];
        }
    }
}

/// <summary>
/// Turns the integration outbox into notifications for the people who watch the reported object (ADR-0030, ADR-0011). It reads committed events in commit order
/// from its own cursor (<c>ntf.fanout_cursor</c>), so an event of a rolled-back change never exists and one that commits late is not skipped. For every event the
/// watchers are found, each is checked AT FAN-OUT TIME with the event's stored markings against the attributes the user directory holds for them (a user who
/// has since lost the programme gets nothing), and the rows plus the cursor advance commit together. The row is checked again, with the live subject, when it is shown.
/// </summary>
public sealed class FanOutService(NpgsqlDataSource dataSource, NotificationWriter writer, NotificationOptions options, ILogger<FanOutService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.FanOutEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try { processed = await ProcessBatchAsync(stoppingToken); }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(e, "Notification fan-out failed; it retries from its cursor.");
            }
            catch (OperationCanceledException) { return; }
            if (processed >= options.FanOutBatchSize) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(options.FanOutPollSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Processes up to one batch of committed outbox events. Returns the number of events handled (also those that produced no notification).</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string lastXid;
        long lastSeq;
        await using (var cur = new NpgsqlCommand("SELECT last_xid, last_seq FROM ntf.fanout_cursor WHERE id = 1 FOR UPDATE", conn, tx))
        await using (var r = await cur.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The fan-out cursor row is missing; migration 0015 creates it.");
            (lastXid, lastSeq) = (r.GetString(0), r.GetInt64(1));
        }

        var events = new List<OutboxEvent>();
        // A system read of committed events: each event carries the markings that cover its payload and every recipient is evaluated against them below.
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT o.id, o.seq, o.created_in_tx::text, o.event_type, o.aggregate_type, o.aggregate_number, o.actor, o.payload::text, {MarkingColumns.Select("o")}
            FROM integration.outbox o
            WHERE o.created_in_tx < pg_snapshot_xmin(pg_current_snapshot()) AND (o.created_in_tx, o.seq) > (@cx::xid8, @cs)
            ORDER BY o.created_in_tx, o.seq LIMIT @n
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("cx", lastXid);
            cmd.Parameters.AddWithValue("cs", lastSeq);
            cmd.Parameters.AddWithValue("n", options.FanOutBatchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new OutboxEvent(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    JsonNode.Parse(r.GetString(7)) as JsonObject, MarkingColumns.Read(r, 8)));
        }
        if (events.Count == 0) { await tx.CommitAsync(ct); return 0; }

        foreach (var e in events)
        {
            var targets = FanOutRules.Map(e.EventType, e.AggregateType, e.AggregateNumber, e.Payload);
            if (targets.Count == 0) continue;
            // The outbox records the actor's login; the directory turns it into the subject so the actor is not told about their own action.
            var actor = await DirectoryReader.FindByUsernameAsync(conn, tx, e.Actor, ct);
            foreach (var t in targets)
                await writer.WriteAsync(conn, tx, new NotificationRequest(t.Kind, t.ObjectType, t.ObjectNumber, e.Markings, actor?.Sub, null, null, e.Id), ct);
        }
        var last = events[^1];
        await using (var advance = new NpgsqlCommand("UPDATE ntf.fanout_cursor SET last_xid = @x, last_seq = @s, updated_at = now() WHERE id = 1", conn, tx))
        {
            advance.Parameters.AddWithValue("x", last.Xid);
            advance.Parameters.AddWithValue("s", last.Seq);
            await advance.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        logger.LogDebug("Notification fan-out handled {Count} events up to sequence {Seq}.", events.Count.ToString(CultureInfo.InvariantCulture), last.Seq);
        return events.Count;
    }

    private sealed record OutboxEvent(Guid Id, long Seq, string Xid, string EventType, string AggregateType, string AggregateNumber, string Actor, JsonObject? Payload, Markings Markings);
}

/// <summary>
/// Purges notifications the user has read once they are older than <c>Keel:Notifications:RetentionDays</c>. Off by default (0 days): unread notifications
/// are never purged by age, and the per-user cap (newest 500) bounds the table either way.
/// </summary>
public sealed class RetentionService(NpgsqlDataSource dataSource, NotificationOptions options, TimeProvider clock, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RetentionDays <= 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PurgeAsync(stoppingToken); }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(e, "Notification retention purge failed; it retries at the next interval.");
            }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(TimeSpan.FromHours(options.PurgeIntervalHours), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Deletes read notifications older than the retention period and returns how many went. Does nothing when retention is off.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        if (options.RetentionDays <= 0) return 0;
        await using var cmd = dataSource.CreateCommand("DELETE FROM ntf.notification WHERE read_at IS NOT NULL AND read_at < @cutoff");
        cmd.Parameters.AddWithValue("cutoff", clock.GetUtcNow() - TimeSpan.FromDays(options.RetentionDays));
        var purged = await cmd.ExecuteNonQueryAsync(ct);
        if (purged > 0) logger.LogInformation("Purged {Count} read notifications older than {Days} days.", purged.ToString(CultureInfo.InvariantCulture), options.RetentionDays.ToString(CultureInfo.InvariantCulture));
        return purged;
    }
}
