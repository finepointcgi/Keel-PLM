using System.Globalization;
using Keel.Audit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Integration;

/// <summary>
/// Configuration section <c>Keel:Integration:Retention</c> (docs/ops/integration.md, ADR-0032). Off by default: nothing is deleted until an operator chooses a period.
/// </summary>
public sealed class OutboxRetentionOptions
{
    /// <summary>Delivered outbox events older than this many days are removed with their delivery rows. 0 (the default) turns the job off.</summary>
    public int RetentionDays { get; set; }
    /// <summary>How often the hosted job runs.</summary>
    public int IntervalHours { get; set; } = 6;
    /// <summary>Events removed per transaction; a run repeats until nothing eligible is left or <see cref="MaxBatchesPerRun"/> is reached.</summary>
    public int BatchSize { get; set; } = 1000;
    public int MaxBatchesPerRun { get; set; } = 100;
    /// <summary>Also remove old events that never had a delivery row (written while no sink was enabled). Off: those stay available to the pull API.</summary>
    public bool IncludeUnroutedEvents { get; set; }
    /// <summary>Count what would be removed and record that, without deleting anything.</summary>
    public bool DryRun { get; set; }

    public bool Enabled => RetentionDays > 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RetentionDays != 0 && RetentionDays is < 7 or > 36500) errors.Add("Keel:Integration:Retention:RetentionDays must be 0 (off) or between 7 and 36500: receivers that backfill with the pull API need that window.");
        if (IntervalHours is < 1 or > 168) errors.Add("Keel:Integration:Retention:IntervalHours must be between 1 and 168.");
        if (BatchSize is < 1 or > 100_000) errors.Add("Keel:Integration:Retention:BatchSize must be between 1 and 100000.");
        if (MaxBatchesPerRun is < 1 or > 10_000) errors.Add("Keel:Integration:Retention:MaxBatchesPerRun must be between 1 and 10000.");
        return errors;
    }
}

public sealed record RetentionResult(int EventsDeleted, int DeliveriesDeleted, DateTimeOffset? OldestEventAt, DateTimeOffset? NewestEventAt, bool DryRun, DateTimeOffset Cutoff);

/// <summary>
/// Removes delivered outbox events after the retention period, only through the database function <c>integration.purge_delivered_outbox</c>, which
/// never deletes an event that has a pending, failed, withheld or dead delivery, nor one the notification fan-out has not passed (migration 0016).
/// Each batch runs in one transaction together with its audit event (<c>integration.outbox_retention</c>, counts and dates only) and its
/// <c>integration.retention_run</c> record, so a purge cannot happen unrecorded. Several nodes may run it: the function takes an advisory lock.
/// </summary>
public sealed class OutboxRetentionService(NpgsqlDataSource dataSource, IAuditLog audit, IntegrationOptions options, TimeProvider clock, ILogger<OutboxRetentionService> logger) : BackgroundService
{
    private readonly string _node = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = options.Retention;
        if (!retention.Enabled)
        {
            logger.LogInformation("Outbox retention is off (Keel:Integration:Retention:RetentionDays = 0): delivered events are kept.");
            return;
        }
        logger.LogInformation("Outbox retention keeps delivered events for {Days} days{DryRun}.", retention.RetentionDays.ToString(CultureInfo.InvariantCulture), retention.DryRun ? " (dry run)" : "");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(e, "Outbox retention run failed; it retries at the next interval.");
            }
            try { await Task.Delay(TimeSpan.FromHours(retention.IntervalHours), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One run: batches until nothing eligible remains. Returns the totals (all zero when retention is off). Public so operators' tests can drive it.</summary>
    public async Task<RetentionResult> RunOnceAsync(CancellationToken ct)
    {
        var retention = options.Retention;
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(retention.RetentionDays);
        if (!retention.Enabled) return new RetentionResult(0, 0, null, null, retention.DryRun, cutoff);
        int events = 0, deliveries = 0;
        DateTimeOffset? oldest = null, newest = null;
        for (var batch = 0; batch < retention.MaxBatchesPerRun; batch++)
        {
            var r = await PurgeBatchAsync(cutoff, ct);
            if (r.EventsDeleted == 0) break;
            events += r.EventsDeleted;
            deliveries += r.DeliveriesDeleted;
            oldest = oldest is null || r.OldestEventAt < oldest ? r.OldestEventAt : oldest;
            newest = newest is null || r.NewestEventAt > newest ? r.NewestEventAt : newest;
            if (retention.DryRun) break; // a dry run deletes nothing, so the next batch would count the same events again
        }
        if (events > 0)
            logger.LogInformation("Outbox retention {Verb} {Events} events and {Deliveries} delivery rows older than {Cutoff:O}.", retention.DryRun ? "would remove" : "removed",
                events.ToString(CultureInfo.InvariantCulture), deliveries.ToString(CultureInfo.InvariantCulture), cutoff);
        return new RetentionResult(events, deliveries, oldest, newest, retention.DryRun, cutoff);
    }

    private async Task<RetentionResult> PurgeBatchAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var retention = options.Retention;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int events, deliveries;
        DateTimeOffset? oldest, newest;
        await using (var cmd = new NpgsqlCommand("SELECT events_deleted, deliveries_deleted, oldest_event_at, newest_event_at FROM integration.purge_delivered_outbox(@cutoff, @batch, @unrouted, @dry)", conn, tx))
        {
            cmd.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
            cmd.Parameters.AddWithValue("batch", retention.BatchSize);
            cmd.Parameters.AddWithValue("unrouted", retention.IncludeUnroutedEvents);
            cmd.Parameters.AddWithValue("dry", retention.DryRun);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            events = r.GetInt32(0);
            deliveries = r.GetInt32(1);
            oldest = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2);
            newest = r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3);
        }
        if (events == 0) { await tx.RollbackAsync(ct); return new RetentionResult(0, 0, null, null, retention.DryRun, cutoff); }

        await using (var run = new NpgsqlCommand(
            """
            INSERT INTO integration.retention_run (ran_at, cutoff, dry_run, include_unrouted, events_deleted, deliveries_deleted, oldest_event_at, newest_event_at, node)
            VALUES (@now, @cutoff, @dry, @unrouted, @events, @deliveries, @oldest, @newest, @node)
            """, conn, tx))
        {
            run.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());
            run.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
            run.Parameters.AddWithValue("dry", retention.DryRun);
            run.Parameters.AddWithValue("unrouted", retention.IncludeUnroutedEvents);
            run.Parameters.AddWithValue("events", events);
            run.Parameters.AddWithValue("deliveries", deliveries);
            run.Parameters.Add(new NpgsqlParameter("oldest", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)oldest?.ToUniversalTime() ?? DBNull.Value });
            run.Parameters.Add(new NpgsqlParameter("newest", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)newest?.ToUniversalTime() ?? DBNull.Value });
            run.Parameters.AddWithValue("node", _node);
            await run.ExecuteNonQueryAsync(ct);
        }
        // Counts and dates only: what an event was about (object numbers, markings) is never written here.
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.System, Action = "integration.outbox_retention", Outcome = AuditOutcome.Success, ObjectType = "integration",
            Details = AuditDetails.Of(("events", events.ToString(CultureInfo.InvariantCulture)), ("deliveries", deliveries.ToString(CultureInfo.InvariantCulture)),
                ("cutoff", cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("dryRun", retention.DryRun.ToString()), ("includeUnrouted", retention.IncludeUnroutedEvents.ToString()),
                ("oldest", oldest?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("newest", newest?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))),
        }, ct);
        await tx.CommitAsync(ct);
        return new RetentionResult(events, deliveries, oldest, newest, retention.DryRun, cutoff);
    }
}
