using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.Channels;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Siem;

/// <summary>Meters of the forwarder (<c>Keel.Siem</c>); the host maps them to Prometheus.</summary>
internal static class SiemMetrics
{
    internal static readonly Meter Meter = new("Keel.Siem");
    internal static readonly Counter<long> Forwarded = Meter.CreateCounter<long>("keel_siem_events_forwarded", description: "Audit events and checkpoints handed to a SIEM destination.");
    internal static readonly Counter<long> SendFailures = Meter.CreateCounter<long>("keel_siem_send_failures", description: "Failed attempts to deliver a batch to a SIEM destination.");
    internal static readonly Counter<long> ChainBreaks = Meter.CreateCounter<long>("keel_siem_chain_breaks", description: "Breaks found in the audit chain the forwarder reads.");

    internal static void Observe(SiemStatusTracker tracker)
    {
        Meter.CreateObservableGauge("keel_siem_lag_events", () => tracker.Observations(d => (double)d.LagEvents), description: "Audit events not yet forwarded to the destination.");
        Meter.CreateObservableGauge("keel_siem_lag_seconds", () => tracker.Observations(d => d.LagSeconds), description: "Age of the oldest audit event not yet forwarded.");
        Meter.CreateObservableGauge("keel_siem_queue_events", () => tracker.Observations(d => (double)d.QueuedEvents), description: "Events read ahead and held in the bounded in-memory queue.");
        Meter.CreateObservableGauge("keel_siem_connected", () => tracker.Observations(d => d.Connected ? 1d : 0d), description: "1 when the last delivery to the destination succeeded.");
        Meter.CreateObservableGauge("keel_siem_chain_broken", () => tracker.Observations(d => d.ChainBroken ? 1d : 0d), description: "1 when a break in the audit chain was found and not yet cleared.");
    }
}

/// <summary>Exponential backoff with jitter for reconnects and retries.</summary>
public static class Backoff
{
    /// <summary>
    /// The wait before retry number <paramref name="attempt"/> (0-based): min * 2^attempt capped at max, then scaled by <paramref name="jitter"/>
    /// in [0,1) to between 50% and 100% of that, so many forwarders do not reconnect in lockstep.
    /// </summary>
    public static TimeSpan Next(int attempt, TimeSpan min, TimeSpan max, double jitter)
    {
        var exponent = Math.Min(attempt, 20);
        var seconds = Math.Min(max.TotalSeconds, min.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds * (0.5 + 0.5 * Math.Clamp(jitter, 0, 0.999999)));
    }
}

/// <summary>Live state of one destination: read by the status provider, written by its forwarder.</summary>
internal sealed class DestinationState(string name, string type)
{
    private readonly object _gate = new();
    public string Name { get; } = name;
    public string Type { get; } = type;
    public long LastForwardedSeq;
    public byte[] LastForwardedHash = new byte[32];
    public long HeadSeq;
    public double LagSeconds;
    public int QueuedEvents;
    public bool Connected;
    public long ConsecutiveFailures;
    public string? LastError;
    public bool ChainBroken;
    public string? ChainBreakNote;

    public void Update(Action<DestinationState> change) { lock (_gate) change(this); }

    public SiemDestinationStatus Snapshot()
    {
        lock (_gate)
            return new SiemDestinationStatus(Name, Type, LastForwardedSeq, HeadSeq, Math.Max(0, HeadSeq - LastForwardedSeq), LagSeconds, QueuedEvents, Connected,
                ConsecutiveFailures, LastError, ChainBroken, ChainBreakNote);
    }
}

/// <summary>Collects the states of every destination and exposes them as <see cref="ISiemStatus"/>.</summary>
public sealed class SiemStatusTracker : ISiemStatus
{
    private readonly List<DestinationState> _states = [];
    private bool _enabled;

    internal void MarkEnabled() { lock (_states) _enabled = true; }

    internal DestinationState Add(string name, string type)
    {
        var state = new DestinationState(name, type);
        lock (_states) { _states.Add(state); _enabled = true; }
        return state;
    }

    public SiemStatus Current()
    {
        lock (_states) return new SiemStatus(_enabled, _states.Select(s => s.Snapshot()).ToList());
    }

    internal IEnumerable<Measurement<double>> Observations(Func<SiemDestinationStatus, double> value)
    {
        foreach (var s in Current().Destinations) yield return new Measurement<double>(value(s), new KeyValuePair<string, object?>("destination", s.Name));
    }
}

/// <summary>
/// Forwards the audit log (and checkpoint creations) to one destination, in order and at least once (ADR-0026). A reader task pulls events from
/// <c>audit.event</c> by sequence number, verifies that they continue the hash chain, formats them and puts batches into a bounded queue; a sender
/// task delivers each batch, retrying with exponential backoff and jitter until the destination takes it, and only then advances the
/// <c>siem.cursor</c> row; a monitor task measures lag and checks that what was already forwarded is still in the database unchanged. The audit
/// writers never wait for any of this: they only insert rows.
/// </summary>
internal sealed class DestinationForwarder(
    SiemOptions options, SiemDestinationOptions destination, ISiemSink sink, CursorStore cursors, SyslogFormatter formatter, DestinationState state,
    IAuditLog audit, TimeProvider clock, ILogger logger)
{
    private sealed record Batch(IReadOnlyList<SiemMessage> Messages, int Events, long LastSeq, byte[] LastHash, long LastCheckpoint, byte[] LastCheckpointHash, int Checkpoints);

    private readonly Channel<Batch> _batches = Channel.CreateBounded<Batch>(new BoundedChannelOptions(options.QueueBatches)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<SiemMessage> _alerts = Channel.CreateUnbounded<SiemMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TimeSpan _poll = TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds);
    private TimeSpan BackoffMin => TimeSpan.FromSeconds(destination.BackoffMinSeconds);
    private TimeSpan BackoffMax => TimeSpan.FromSeconds(destination.BackoffMaxSeconds);
    private int _breakReported;

    public async Task RunAsync(CancellationToken ct)
    {
        var restart = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or IOException)
            {
                // Typically the database is unreachable. The cursor is durable, so starting over loses nothing.
                logger.LogError(e, "SIEM forwarder for {Destination} stopped and will restart", destination.Name);
                state.Update(s => { s.LastError = e.Message; s.Connected = false; });
                try { await Task.Delay(Backoff.Next(restart++, BackoffMin, BackoffMax, Random.Shared.NextDouble()), clock, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var audit = await cursors.GetOrCreateAsync(destination.Name, CursorStore.AuditStream, ct);
        var checkpoint = await cursors.GetOrCreateAsync(destination.Name, CursorStore.CheckpointStream, ct);
        state.Update(s =>
        {
            s.LastForwardedSeq = audit.LastSeq;
            s.LastForwardedHash = audit.LastHash;
            s.ChainBroken = audit.ChainBreakSeq is not null;
            s.ChainBreakNote = audit.ChainBreakNote;
        });
        if (audit.ChainBreakSeq is not null) Interlocked.Exchange(ref _breakReported, 1);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new[]
        {
            Task.Run(() => ReadLoopAsync(audit, checkpoint, linked.Token), CancellationToken.None),
            Task.Run(() => SendLoopAsync(linked.Token), CancellationToken.None),
            Task.Run(() => MonitorLoopAsync(linked.Token), CancellationToken.None),
        };
        try
        {
            await await Task.WhenAny(tasks);
        }
        finally
        {
            await linked.CancelAsync();
            try { await Task.WhenAll(tasks); } catch (Exception) { /* the first failure is rethrown above */ }
        }
    }

    // ------------------------------------------------------------------------------------------------ reading

    private async Task ReadLoopAsync(SiemCursor audit, SiemCursor checkpoint, CancellationToken ct)
    {
        var tracker = new ChainTracker(audit.LastSeq, audit.LastHash);
        var checkpointId = checkpoint.LastSeq;
        var checkpointHash = checkpoint.LastHash;
        while (!ct.IsCancellationRequested)
        {
            var events = await cursors.ReadEventsAsync(tracker.LastSeq, options.BatchSize, ct);
            var messages = new List<SiemMessage>(events.Count + 2);
            foreach (var r in events)
            {
                var problems = tracker.Accept(r);
                foreach (var p in problems) await ReportBreakAsync(p, messages, ct);
                messages.Add(formatter.Format(r, chainBroken: problems.Count > 0));
            }

            // Checkpoints created for events that were already read (the signature stays in the database; the key id says which key signed).
            var newCheckpoints = await cursors.ReadCheckpointsAsync(checkpointId, 50, ct);
            var includedCheckpoints = 0;
            foreach (var cp in newCheckpoints)
            {
                if (cp.Seq > tracker.LastSeq) break;
                messages.Add(formatter.FormatCheckpoint(cp.Id, cp.Seq, cp.Hash, cp.CreatedAt, cp.KeyId));
                checkpointId = cp.Id;
                checkpointHash = cp.Hash;
                includedCheckpoints++;
            }

            if (messages.Count == 0)
            {
                await Task.Delay(_poll, clock, ct);
                continue;
            }
            state.Update(s => s.QueuedEvents += events.Count);
            await _batches.Writer.WriteAsync(new Batch(messages, events.Count, tracker.LastSeq, tracker.LastHash, checkpointId, checkpointHash, includedCheckpoints), ct);
            if (events.Count < options.BatchSize && includedCheckpoints == 0) await Task.Delay(_poll, clock, ct);
        }
    }

    /// <summary>Counts, logs, audits (once per break), persists and sends the alert for a break in the chain. Messages are appended to <paramref name="inBand"/> when given (the reader's batch).</summary>
    private async Task ReportBreakAsync(ChainBreak b, List<SiemMessage>? inBand, CancellationToken ct)
    {
        SiemMetrics.ChainBreaks.Add(1, new KeyValuePair<string, object?>("destination", destination.Name), new KeyValuePair<string, object?>("kind", b.KindText));
        logger.LogCritical("Audit chain break read for SIEM destination {Destination}: {Kind} at seq {Seq}: {Detail}", destination.Name, b.KindText, b.Seq, b.Detail);
        var message = formatter.FormatAlert(b.KindText, b.Seq, b.Detail, clock.GetUtcNow());
        if (inBand is not null) inBand.Add(message);
        else _alerts.Writer.TryWrite(message);
        state.Update(s => { s.ChainBroken = true; s.ChainBreakNote ??= $"{b.KindText} at seq {b.Seq}: {b.Detail}"; });
        try
        {
            await cursors.RecordBreakAsync(destination.Name, CursorStore.AuditStream, b.Seq, $"{b.KindText} at seq {b.Seq}: {b.Detail}", ct);
            // One audit event per destination until an operator clears the persisted break; it flows through the log like any other.
            if (Interlocked.Exchange(ref _breakReported, 1) == 0)
                await audit.AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.System, Action = "siem.chain_break", Outcome = AuditOutcome.Failure, ObjectType = "siem", ObjectId = destination.Name,
                    Details = AuditDetails.Of(("kind", b.KindText), ("seq", b.Seq.ToString(CultureInfo.InvariantCulture)), ("detail", b.Detail)),
                }, ct);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(e, "Could not persist the SIEM chain break for {Destination}", destination.Name);
        }
    }

    // ------------------------------------------------------------------------------------------------ sending

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_alerts.Reader.TryRead(out var alert)) { await DeliverAsync([alert], ct); continue; }
            if (_batches.Reader.TryRead(out var batch))
            {
                await DeliverAsync(batch.Messages, ct);
                await AdvanceAsync(batch, ct);
                continue;
            }
            await Task.WhenAny(_batches.Reader.WaitToReadAsync(ct).AsTask(), _alerts.Reader.WaitToReadAsync(ct).AsTask());
        }
    }

    private async Task DeliverAsync(IReadOnlyList<SiemMessage> messages, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await sink.SendAsync(messages, ct);
                state.Update(s => { s.Connected = true; s.ConsecutiveFailures = 0; s.LastError = null; });
                return;
            }
            catch (SiemSendException e)
            {
                SiemMetrics.SendFailures.Add(1, new KeyValuePair<string, object?>("destination", destination.Name));
                state.Update(s => { s.Connected = false; s.ConsecutiveFailures++; s.LastError = e.Message; });
                logger.LogWarning("SIEM delivery to {Destination} failed (attempt {Attempt}): {Message}", destination.Name, attempt + 1, e.Message);
                await Task.Delay(Backoff.Next(attempt, BackoffMin, BackoffMax, Random.Shared.NextDouble()), clock, ct);
            }
        }
    }

    private async Task AdvanceAsync(Batch batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (batch.Events > 0)
                    await cursors.AdvanceAsync(destination.Name, CursorStore.AuditStream, batch.LastSeq, batch.LastHash, batch.Events, ct);
                if (batch.Checkpoints > 0)
                    await cursors.AdvanceAsync(destination.Name, CursorStore.CheckpointStream, batch.LastCheckpoint, batch.LastCheckpointHash, batch.Checkpoints, ct);
                break;
            }
            catch (NpgsqlException e)
            {
                logger.LogWarning("Could not advance the SIEM cursor of {Destination}: {Message}", destination.Name, e.Message);
                await Task.Delay(Backoff.Next(attempt, BackoffMin, BackoffMax, Random.Shared.NextDouble()), clock, ct);
            }
        }
        SiemMetrics.Forwarded.Add(batch.Events + batch.Checkpoints, new KeyValuePair<string, object?>("destination", destination.Name));
        state.Update(s =>
        {
            if (batch.Events > 0) { s.LastForwardedSeq = batch.LastSeq; s.LastForwardedHash = batch.LastHash; }
            s.QueuedEvents = Math.Max(0, s.QueuedEvents - batch.Events);
        });
    }

    // ------------------------------------------------------------------------------------------------ monitoring

    private long _tailReportedAt = -1;

    /// <summary>A tail change is reported once per position, not on every check.</summary>
    private async Task ReportTailChangeAsync(ChainBreak b, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _tailReportedAt, b.Seq) == b.Seq) return;
        await ReportBreakAsync(b, null, ct);
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var tailChecks = 0;
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, options.PollIntervalMilliseconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (head, _) = await cursors.HeadAsync(ct);
                long forwarded = 0;
                byte[] forwardedHash = [];
                state.Update(s => { forwarded = s.LastForwardedSeq; forwardedHash = s.LastForwardedHash; });
                double lagSeconds = 0;
                if (head > forwarded && await cursors.ReadEventsAsync(forwarded, 1, ct) is [{ } oldest])
                    lagSeconds = Math.Max(0, (clock.GetUtcNow() - oldest.OccurredAt).TotalSeconds);
                state.Update(s => { s.HeadSeq = Math.Max(head, s.LastForwardedSeq); s.LagSeconds = lagSeconds; });

                // Everything already forwarded must still be in the database, unchanged: this is the check that closes "deletion after the last
                // checkpoint" (the SIEM holds the head the database once had).
                if (forwarded > 0 && (++tailChecks % 5 == 1))
                {
                    var stored = await cursors.HashAtAsync(forwarded, ct);
                    if (stored is null)
                        await ReportTailChangeAsync(new ChainBreak(ChainBreakKind.TailChanged, forwarded, $"event {forwarded} was forwarded but is no longer in the audit log (head is {head})"), ct);
                    else if (!stored.AsSpan().SequenceEqual(forwardedHash))
                        await ReportTailChangeAsync(new ChainBreak(ChainBreakKind.TailChanged, forwarded, $"event {forwarded} was forwarded with a different hash than the database holds now"), ct);
                }
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("SIEM monitor for {Destination}: {Message}", destination.Name, e.Message);
            }
            await Task.Delay(interval, clock, ct);
        }
    }
}
