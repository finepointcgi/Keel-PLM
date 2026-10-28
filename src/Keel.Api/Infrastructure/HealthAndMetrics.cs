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

public static class KeelMetrics
{
    public static readonly Counter AuditEvents = Metrics.CreateCounter("keel_audit_events_total", "Audit events appended.", "outcome");
    public static readonly Counter AccessDenials = Metrics.CreateCounter("keel_access_denials_total", "Access decisions that denied.", "action");
}
