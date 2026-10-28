using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;

namespace Keel.Api.Infrastructure;

/// <summary>Ready when the database answers and every shipped migration has been applied.</summary>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = dataSource.CreateCommand("SELECT version FROM public.schema_migrations");
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) applied.Add(r.GetString(0));
            var missing = Migrator.Discover(Migrator.DefaultDirectory).Select(m => m.Version).Where(v => !applied.Contains(v)).ToList();
            return missing.Count == 0
                ? HealthCheckResult.Healthy("Database reachable; schema current.")
                : HealthCheckResult.Unhealthy($"Pending migrations: {string.Join(", ", missing)}");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}

public sealed class FipsHealthCheck(FipsStatus status, KeelOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(status.Enabled
            ? HealthCheckResult.Healthy(status.Detail)
            : options.Security.RequireFips
                ? HealthCheckResult.Unhealthy(status.Detail)
                : HealthCheckResult.Degraded(status.Detail));
}

/// <summary>
/// SIEM forwarding. Degraded (never Unhealthy: an unreachable SIEM must not take the application out of service, and forwarding never blocks audit writes)
/// when a destination fails, falls far behind, or the forwarder found a break in the audit chain it reads.
/// </summary>
public sealed class SiemHealthCheck(Keel.Core.ISiemStatus status) : IHealthCheck
{
    public static readonly TimeSpan MaxLag = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var s = status.Current();
        if (!s.Enabled) return Task.FromResult(HealthCheckResult.Healthy("SIEM forwarding is not configured."));
        var broken = s.Destinations.Where(d => d.ChainBroken).ToList();
        if (broken.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded($"The audit chain read for SIEM forwarding is broken: {string.Join("; ", broken.Select(d => $"{d.Name}: {d.ChainBreakNote}"))}. Run keel-audit verify."));
        var failing = s.Destinations.Where(d => d.ConsecutiveFailures > 0).ToList();
        if (failing.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded($"SIEM delivery failing: {string.Join("; ", failing.Select(d => $"{d.Name}: {d.LastError}"))}"));
        var behind = s.Destinations.Where(d => d.LagSeconds > MaxLag.TotalSeconds).ToList();
        if (behind.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded($"SIEM destination behind by more than {MaxLag.TotalMinutes:0} minutes: {string.Join(", ", behind.Select(d => d.Name))}"));
        return Task.FromResult(HealthCheckResult.Healthy($"{s.Destinations.Count} destination(s) up to date."));
    }
}

public static class KeelMetrics
{
    public static readonly Counter AuditEvents = Metrics.CreateCounter("keel_audit_events_total", "Audit events appended.", "outcome");
    public static readonly Counter AccessDenials = Metrics.CreateCounter("keel_access_denials_total", "Access decisions that denied.", "action");
}
