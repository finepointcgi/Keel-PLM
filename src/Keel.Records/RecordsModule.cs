using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// Optional background maintenance (off unless <c>Keel:Records:BackgroundMinutes</c> is set): re-evaluates program and query holds so they cover objects created after
/// they were placed, and refreshes the classification cache. Neither is needed for correctness: execution evaluates everything again.
/// </summary>
internal sealed class RecordsMaintenanceService(HoldService holds, RetentionClassificationService classification, RecordsOptions options, ILogger<RecordsMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.BackgroundMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.BackgroundMinutes));
        do
        {
            try
            {
                await holds.ReevaluateAllAsSystemAsync(stoppingToken);
                await classification.ClassifyAsSystemAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Records maintenance failed; it will run again at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class RecordsModule
{
    /// <summary>
    /// Registers records management: schedules, classification, legal holds, disposition, archives, the three review gateways (schedule publication, hold release,
    /// disposition approval) and the <c>plm.records</c> policy. The host registers <see cref="IContentStore"/>, <see cref="IContentReader"/> and <see cref="IWormStorage"/> (all
    /// optional here: without them archives and destruction confirmation answer "unavailable") and the audit checkpoint signer, whose key signs certificates and manifests.
    /// </summary>
    public static IServiceCollection AddKeelRecords(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = new RecordsOptions();
        configuration?.GetSection(RecordsOptions.Section).Bind(options);
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton(sp => new RecordsSigner(sp.GetService<CheckpointSigner>(), sp.GetRequiredService<TrustedCheckpointKeys>()));
        services.AddSingleton(sp => new ScheduleService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RetentionClassificationService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new HoldService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ArchiveService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<RecordsSigner>(),
            sp.GetRequiredService<TrustedCheckpointKeys>(), sp.GetRequiredService<RecordsOptions>(), sp.GetService<IContentStore>(), sp.GetService<IContentReader>(), sp.GetService<IWormStorage>()));
        services.AddSingleton(sp => new DispositionService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<RecordsSigner>(),
            sp.GetRequiredService<ArchiveService>(), sp.GetRequiredService<RecordsOptions>(), sp.GetService<IContentStore>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ScheduleWorkflowGateway(sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new HoldWorkflowGateway(sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new DispositionWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<RecordsMaintenanceService>();
        // Added to the host's own policies (additive): records managers, administrators and auditors reach the records pages and API; the services decide who may do what.
        services.Configure<AuthorizationOptions>(o => o.AddPolicy(Policies.Records, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("roles", Roles.RecordsManager, Roles.Admin, Roles.Auditor)));
        return services;
    }
}
