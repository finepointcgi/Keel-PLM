using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Siem;

/// <summary>Composition of the SIEM forwarder (ADR-0026). With <c>Keel:Siem:Enabled</c> false (the default) nothing but a status that says so is registered.</summary>
public static class SiemModule
{
    /// <param name="requireFips">The host's <c>Keel:Security:RequireFips</c>: production forwards over TLS only.</param>
    /// <param name="secretsDirectory">KEEL_SECRETS_DIR; relative certificate and key paths resolve against it.</param>
    public static IServiceCollection AddKeelSiem(this IServiceCollection services, IConfiguration configuration, bool requireFips, string? secretsDirectory)
    {
        var options = configuration.GetSection(SiemOptions.Section).Get<SiemOptions>() ?? new SiemOptions();
        var errors = options.Validate(requireFips); // throws NotSupportedException for UDP
        if (errors.Count > 0) throw new InvalidOperationException("Invalid SIEM configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        var tracker = new SiemStatusTracker();
        services.AddSingleton(tracker);
        services.AddSingleton<ISiemStatus>(tracker);
        if (!options.Enabled) return services;
        tracker.MarkEnabled();

        SiemMetrics.Observe(tracker);
        services.AddSingleton(sp => new CursorStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new SiemForwardingService(options, sp.GetRequiredService<CursorStore>(), tracker, sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>(), secretsDirectory));
        return services;
    }
}

/// <summary>Runs one <see cref="DestinationForwarder"/> per configured destination until the host stops.</summary>
public sealed class SiemForwardingService : BackgroundService
{
    private readonly SiemOptions _options;
    private readonly CursorStore _cursors;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly string? _secretsDirectory;
    private readonly List<(SiemDestinationOptions Options, DestinationState State)> _destinations = [];

    public SiemForwardingService(SiemOptions options, CursorStore cursors, SiemStatusTracker tracker, IAuditLog audit, TimeProvider clock,
        ILoggerFactory loggers, string? secretsDirectory)
    {
        _options = options;
        _cursors = cursors;
        _audit = audit;
        _clock = clock;
        _loggers = loggers;
        _secretsDirectory = secretsDirectory;
        // Destinations are visible to the health check and the metrics from construction on, not only once the first batch moved.
        foreach (var d in options.Destinations)
            _destinations.Add((d, tracker.Add(d.Name, d.Type == SiemDestinationType.File ? "file" : "syslog-tls")));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostname = string.IsNullOrWhiteSpace(_options.Hostname) ? SyslogFormatter.DefaultHostname() : _options.Hostname;
        var formatter = new SyslogFormatter(_options, hostname, Environment.ProcessId);
        var sinks = new List<ISiemSink>();
        try
        {
            var runs = new List<Task>();
            foreach (var (d, state) in _destinations)
            {
                ISiemSink sink = d.Type == SiemDestinationType.File ? new FileSink(d, _secretsDirectory) : new SyslogTlsSink(d, _secretsDirectory);
                sinks.Add(sink);
                var forwarder = new DestinationForwarder(_options, d, sink, _cursors, formatter, state, _audit, _clock, _loggers.CreateLogger($"Keel.Siem.{d.Name}"));
                runs.Add(forwarder.RunAsync(stoppingToken));
            }
            await Task.WhenAll(runs);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown: cursors are durable, an unsent batch is resent on the next start
        }
        finally
        {
            foreach (var s in sinks) await s.DisposeAsync();
        }
    }
}
