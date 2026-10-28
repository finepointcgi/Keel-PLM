using Amazon.S3;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Scanning;
using Keel.Documents.Vault;
using Npgsql;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Keel.Api.Infrastructure;

/// <summary>Composition of the documents module and its vault (increment 1.3), kept out of Program.cs.</summary>
public static class DocumentsSetup
{
    public static IServiceCollection AddKeelDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions();
        services.AddSingleton(options);

        // Malware scanning (NIST 3.14.2, ADR-0026). A production host (RequireFips) cannot silently run without it: Disabled needs an explicit acknowledgement.
        var scanErrors = options.Scanning.Validate(configuration.GetValue<bool>("Keel:Security:RequireFips"));
        if (scanErrors.Count > 0) throw new InvalidOperationException("Invalid malware scanning configuration: " + string.Join(" ", scanErrors));
        services.AddSingleton(options.Scanning);
        services.AddSingleton<IUploadScanner>(sp => options.Scanning.Mode == ScanMode.Disabled
            ? new NoScanner()
            : new ClamdScanner(options.Scanning, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new UploadScanning(sp.GetRequiredService<IUploadScanner>(), options.Scanning, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IVaultPendingGuard>(sp => new PostgresVaultPendingGuard(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));

        // The vault validates its configuration when first resolved; readiness reports it before traffic arrives.
        services.AddSingleton(sp => new ObjectVault(sp.GetRequiredService<VaultOptions>(), sp.GetRequiredService<UploadScanning>(), sp.GetRequiredService<IVaultPendingGuard>()));
        services.AddSingleton<IContentStore, VaultContentStore>();
        services.AddSingleton<DocumentService>();
        services.AddSingleton<MeterBridge>();
        services.AddHostedService(sp => sp.GetRequiredService<MeterBridge>()); // module meters (Keel.*) -> /metrics
        services.AddSingleton<VaultGarbageCollector>();
        services.AddSingleton<VaultGcAdmin>();
        if (options.Gc.Enabled) services.AddHostedService<VaultGcService>();
        services.AddHealthChecks().AddCheck<VaultHealthCheck>("vault", tags: ["ready"]).AddCheck<ScannerHealthCheck>("scanner", tags: ["ready"]);
        return services;
    }
}

/// <summary>Ready when the vault configuration is valid and the bucket answers with the configured credentials.</summary>
public sealed class VaultHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var vault = services.GetRequiredService<ObjectVault>();
            await vault.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy($"Vault bucket {vault.Options.Bucket} reachable.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy("Vault is not configured: " + ex.Message);
        }
        catch (Exception ex) when (ex is AmazonS3Exception or VaultUnavailableException or HttpRequestException or Amazon.Runtime.AmazonClientException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Vault unreachable.", ex);
        }
    }
}

/// <summary>
/// A vault that times out or cannot be reached is a temporary condition of the service, not a server fault of this
/// request: answer 503 with Retry-After so clients and proxies retry instead of hanging.
/// </summary>
public sealed class VaultUnavailableExceptionHandler(IProblemDetailsService problems, ILogger<VaultUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not VaultUnavailableException) return false;
        logger.LogError(exception, "Vault unavailable for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Title = "Document vault unavailable", Detail = "The document store did not answer. Nothing was changed; try again shortly." },
        });
    }
}

/// <summary>
/// Scanner readiness. Required mode: Unhealthy when clamd does not answer (uploads would be refused). BestEffort: Degraded (uploads are accepted
/// unscanned). Disabled: Healthy with a note, because the operator chose it (production needs the acknowledgement at startup).
/// </summary>
public sealed class ScannerHealthCheck(UploadScanning scanning) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var mode = scanning.Options.Mode;
        if (mode == ScanMode.Disabled) return HealthCheckResult.Healthy("Malware scanning is disabled by configuration.");
        try
        {
            var info = await scanning.Scanner.ProbeAsync(cancellationToken);
            return HealthCheckResult.Healthy($"{info.Name} {info.Version}");
        }
        catch (ScannerUnavailableException ex)
        {
            return mode == ScanMode.Required
                ? HealthCheckResult.Unhealthy("The malware scanner is unreachable; uploads are refused.", ex)
                : HealthCheckResult.Degraded("The malware scanner is unreachable; uploads are accepted unscanned.", ex);
        }
    }
}

/// <summary>Scheduled sweeper (off by default, <c>Keel:Vault:Gc:Enabled</c>). Dry-run unless <c>Keel:Vault:Gc:Mode</c> is Delete.</summary>
public sealed class VaultGcService(VaultGarbageCollector collector, VaultOptions options, ILogger<VaultGcService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gc = options.Gc;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(gc.IntervalHours));
        do
        {
            try
            {
                var report = await collector.RunAsync(new GcRequest(gc.Mode, gc.Grace, gc.MaxDeletionsPerRun, gc.PageSize, null, ActorSnapshot.System), stoppingToken);
                logger.LogInformation("Vault GC {Mode}: {Candidates} candidates, {Deleted} deleted, report {Hash}", report.Mode, report.Candidates, report.Deleted, report.ReportHash);
            }
            catch (GcAlreadyRunningException)
            {
                logger.LogInformation("Vault GC skipped: another run is in progress.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Vault GC failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
