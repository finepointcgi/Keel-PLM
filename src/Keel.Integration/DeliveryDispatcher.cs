using Keel.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Keel.Integration;

public static class IntegrationMetrics
{
    public static readonly Counter Deliveries = Metrics.CreateCounter("keel_integration_deliveries_total", "Delivery attempts by sink and result (delivered, retry, dead, withheld).", "sink", "result");
    public static readonly Gauge Backlog = Metrics.CreateGauge("keel_integration_deliveries", "Deliveries by status.", "status");
    public static readonly Gauge SpoolFiles = Metrics.CreateGauge("keel_integration_spool_files", "Committed files waiting in a file-drop sink's spool directory for the receiver to consume them.", "sink");
    public static readonly Gauge LagSeconds = Metrics.CreateGauge("keel_integration_lag_seconds", "Seconds the oldest due delivery has been waiting.");
}

/// <summary>
/// Delivers pending outbox events to their sinks (ADR-0025): at-least-once, per-aggregate ordering within a sink, exponential backoff with
/// jitter, dead letters after the configured attempts. Several instances may run against one database; the claim uses SKIP LOCKED.
/// Events a sink is not cleared for are withheld by the outbound release check before anything is sent.
/// </summary>
public sealed class DeliveryDispatcher(DeliveryStore store, SinkRegistry sinks, IntegrationOptions options, ILogger<DeliveryDispatcher> logger) : BackgroundService
{
    private readonly string _instance = InstanceId();

    /// <summary>
    /// Names this dispatcher in the lease table: machine, process and a random part, at most 48 characters. The machine name is cut to 10 characters and the random part
    /// to 24, never assuming a length: a managed container platform reports a short machine name such as "localhost", a Kubernetes pod a long one.
    /// </summary>
    internal static string InstanceId()
    {
        var machine = Environment.MachineName;
        if (machine.Length > 10) machine = machine[..10];
        return $"{machine}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..24]}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.DispatcherEnabled)
        {
            logger.LogInformation("Integration dispatcher is disabled (Keel:Integration:DispatcherEnabled).");
            return;
        }
        logger.LogInformation("Integration dispatcher {Instance} started.", _instance);
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await RunOnceAsync(stoppingToken);
                await RefreshMetricsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
            {
                logger.LogError(e, "Integration dispatcher cycle failed; retrying.");
            }
            if (handled == 0)
            {
                try { await Task.Delay(options.PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        logger.LogInformation("Integration dispatcher {Instance} stopped.", _instance);
    }

    /// <summary>One claim-and-deliver cycle; returns the number of deliveries handled. Public so tests and operators can drive it step by step.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await store.ClaimAsync(_instance, options.BatchSize, options.Lease, ct);
        if (claimed.Count == 0) return 0;
        // Sinks run in parallel; within one sink the order of the claim (event order) is kept.
        await Task.WhenAll(claimed.GroupBy(c => c.Sink.Id).Select(g => ProcessSinkAsync(g.OrderBy(c => c.Event.Seq).ToList(), ct)));
        return claimed.Count;
    }

    private async Task ProcessSinkAsync(List<ClaimedDelivery> deliveries, CancellationToken ct)
    {
        var done = new HashSet<Guid>();
        try
        {
            var cleared = new List<ClaimedDelivery>();
            foreach (var d in deliveries)
            {
                var reasons = Clearance(d);
                if (reasons.Count > 0)
                {
                    await store.WithholdAsync(d, reasons, ct);
                    IntegrationMetrics.Deliveries.WithLabels(d.Sink.Name, "withheld").Inc();
                    done.Add(d.DeliveryId);
                }
                else cleared.Add(d);
            }
            if (cleared.Count == 0) return;

            var sink = sinks.For(cleared[0].Sink.Kind);
            if (sink is IBatchEventSink batch)
            {
                var outcomes = await SafeAsync(() => batch.DeliverBatchAsync(cleared[0].Sink, [.. cleared.Select(Message)], ct), cleared.Count);
                for (var i = 0; i < cleared.Count; i++)
                {
                    await RecordAsync(cleared[i], outcomes[i], ct);
                    done.Add(cleared[i].DeliveryId);
                }
                return;
            }
            foreach (var d in cleared)
            {
                var outcome = (await SafeAsync(async () => [await sink.DeliverAsync(d.Sink, Message(d), ct)], 1))[0];
                await RecordAsync(d, outcome, ct);
                done.Add(d.DeliveryId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: give back what was not finished; the attempt does not count and the delivery is retried by the next dispatcher.
        }
        finally
        {
            var open = deliveries.Select(d => d.DeliveryId).Where(id => !done.Contains(id)).ToList();
            if (open.Count > 0) await store.ReleaseAsync(open, CancellationToken.None);
        }
    }

    /// <summary>Reason codes when the sink's profile does not clear the event; empty when it may be delivered.</summary>
    internal static IReadOnlyList<string> Clearance(ClaimedDelivery d)
    {
        if (!d.Profile.Active) return ["outbound.profile_inactive"];
        var decision = OutboundRelease.Evaluate(d.Profile.ToProfile(), d.Event.Markings);
        return decision.Permitted ? [] : [.. decision.Reasons];
    }

    private static EnvelopeMessage Message(ClaimedDelivery d) =>
        new(d.Event.Id, d.Event.EventType, d.Event.Seq, d.Event.OccurredAt, Envelope.Body(d.Event), d.Attempts + 1);

    /// <summary>A sink that throws (poison event, bad configuration) must not stop the loop: the event becomes a dead letter with the error.</summary>
    private static async Task<IReadOnlyList<DeliveryOutcome>> SafeAsync(Func<Task<IReadOnlyList<DeliveryOutcome>>> deliver, int count)
    {
        try
        {
            return await deliver();
        }
        catch (NotSupportedException e)
        {
            return [.. Enumerable.Repeat(DeliveryOutcome.Permanent(e.Message), count)];
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return [.. Enumerable.Repeat(DeliveryOutcome.Permanent("The event could not be rendered or sent: " + e.Message), count)];
        }
    }

    private async Task RecordAsync(ClaimedDelivery d, DeliveryOutcome outcome, CancellationToken ct)
    {
        var status = await store.CompleteAsync(d, outcome, options.MaxAttempts, options.BackoffBase, options.BackoffMax, ct);
        IntegrationMetrics.Deliveries.WithLabels(d.Sink.Name, status switch
        {
            DeliveryStatus.Delivered => "delivered",
            DeliveryStatus.Dead => "dead",
            _ => "retry",
        }).Inc();
        if (status != DeliveryStatus.Delivered)
            logger.LogWarning("Delivery of event {EventId} ({Type}) to sink {Sink} is {Status} (attempt {Attempt}): {Error}", d.Event.Id, d.Event.EventType, d.Sink.Name, status, d.Attempts + 1, outcome.Error);
    }

    public async Task RefreshMetricsAsync(CancellationToken ct)
    {
        var stats = await store.StatsAsync(ct);
        foreach (var (status, count) in stats.ByStatus) IntegrationMetrics.Backlog.WithLabels(status).Set(count);
        IntegrationMetrics.LagSeconds.Set(stats.LagSeconds);
    }
}

/// <summary>Readiness: Degraded (still serving) when deliveries are falling behind or dead letters wait for an administrator.</summary>
public sealed class IntegrationHealthCheck(DeliveryStore store, IntegrationOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stats = await store.StatsAsync(cancellationToken);
        var dead = stats.ByStatus.GetValueOrDefault(DeliveryStatus.Dead);
        var data = new Dictionary<string, object>
        {
            ["lagSeconds"] = Math.Round(stats.LagSeconds, 1),
            ["pending"] = stats.ByStatus.GetValueOrDefault(DeliveryStatus.Pending),
            ["failed"] = stats.ByStatus.GetValueOrDefault(DeliveryStatus.Failed),
            ["dead"] = dead,
            ["dispatcherEnabled"] = options.DispatcherEnabled,
        };
        if (stats.LagSeconds > options.LagDegradedSeconds)
            return HealthCheckResult.Degraded($"The oldest due delivery has waited {stats.LagSeconds:0} s (threshold {options.LagDegradedSeconds} s)." +
                (options.DispatcherEnabled ? "" : " The dispatcher is not enabled in this process."), data: data);
        return HealthCheckResult.Healthy(dead > 0 ? $"{dead} dead letter(s) wait for an administrator." : "Deliveries are current.", data);
    }
}
