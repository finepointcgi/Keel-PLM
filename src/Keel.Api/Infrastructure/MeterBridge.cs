using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Prometheus;

namespace Keel.Api.Infrastructure;

/// <summary>
/// Exposes the <see cref="System.Diagnostics.Metrics"/> instruments of Keel modules (meters named <c>Keel.*</c>) on <c>/metrics</c>. Modules
/// reference neither Prometheus nor the host: they publish counters and gauges through a <see cref="Meter"/> and this bridge maps them to
/// Prometheus. Counters get the <c>_total</c> suffix, observable gauges are read when Prometheus scrapes.
/// </summary>
public sealed class MeterBridge : IHostedService, IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private readonly ConcurrentDictionary<string, Gauge> _gauges = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("Keel.", StringComparison.Ordinal)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => OnLong(i, v, t, s));
        _listener.Start();
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => _listener.RecordObservableInstruments());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, value, tags);
    private void OnDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, value, tags);

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var names = new string[tags.Length];
        var values = new string[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            names[i] = tags[i].Key.Replace('.', '_').Replace('-', '_');
            values[i] = Convert.ToString(tags[i].Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        // Prometheus fixes label names per metric; a metric keeps the label names of its first measurement.
        if (instrument.GetType().Name.StartsWith("ObservableGauge", StringComparison.Ordinal) || instrument.GetType().Name.StartsWith("Gauge", StringComparison.Ordinal))
        {
            var gauge = _gauges.GetOrAdd(instrument.Name, n => Metrics.CreateGauge(n, instrument.Description ?? n, names));
            if (names.Length == gauge.LabelNames.Length) gauge.WithLabels(values).Set(value);
        }
        else if (instrument.GetType().Name.StartsWith("Counter", StringComparison.Ordinal))
        {
            var name = instrument.Name.EndsWith("_total", StringComparison.Ordinal) ? instrument.Name : instrument.Name + "_total";
            var counter = _counters.GetOrAdd(name, n => Metrics.CreateCounter(n, instrument.Description ?? n, names));
            if (names.Length == counter.LabelNames.Length && value > 0) counter.WithLabels(values).Inc(value);
        }
    }

    public void Dispose() => _listener.Dispose();
}
