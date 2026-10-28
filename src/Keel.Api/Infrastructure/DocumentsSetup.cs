using Amazon.S3;
using Keel.Documents;
using Keel.Documents.Vault;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Keel.Api.Infrastructure;

/// <summary>Composition of the documents module and its vault (increment 1.3), kept out of Program.cs.</summary>
public static class DocumentsSetup
{
    public static IServiceCollection AddKeelDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions();
        services.AddSingleton(options);
        // The vault validates its configuration when first resolved; readiness reports it before traffic arrives.
        services.AddSingleton(sp => new ObjectVault(sp.GetRequiredService<VaultOptions>()));
        services.AddSingleton<DocumentService>();
        services.AddHealthChecks().AddCheck<VaultHealthCheck>("vault", tags: ["ready"]);
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
        catch (Exception ex) when (ex is AmazonS3Exception or HttpRequestException or Amazon.Runtime.AmazonClientException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Vault unreachable.", ex);
        }
    }
}
