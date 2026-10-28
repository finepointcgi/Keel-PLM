using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Keel.Integration;

public static class IntegrationModule
{
    /// <summary>
    /// Registers the outbox sink (the <see cref="IDomainEventSink"/> the other modules write to), the dispatcher (runs only when
    /// <c>Keel:Integration:DispatcherEnabled</c>), the webhook and file-drop sinks, administration, the pull queries and the TDP service (ADR-0025).
    /// The host must also register <see cref="IBomPublicationSource"/> and the Tdp source interfaces from the owning modules.
    /// </summary>
    public static IServiceCollection AddKeelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(IntegrationOptions.Section).Get<IntegrationOptions>() ?? new IntegrationOptions();
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid integration configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton<SecretResolver>();
        services.AddSingleton<SsrfGuard>();
        services.AddSingleton<IEventSink, WebhookSink>(sp => new WebhookSink(sp.GetRequiredService<IntegrationOptions>(), sp.GetRequiredService<SecretResolver>(), sp.GetRequiredService<SsrfGuard>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IEventSink, FileDropSink>();
        services.AddSingleton<IEventSink, BrokerSink>();
        services.AddSingleton<SinkRegistry>();
        services.AddSingleton<DeliveryStore>();
        services.AddSingleton<DeliveryDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<DeliveryDispatcher>());
        services.AddSingleton<IDomainEventSink>(sp => new OutboxEventSink(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<IntegrationOptions>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetService<IBomPublicationSource>()));
        services.AddSingleton<IntegrationAdminService>();
        services.AddSingleton<OutboxQueries>();
        services.AddSingleton<TdpSigner>();
        services.AddSingleton<TdpService>();
        services.AddHealthChecks().AddCheck<IntegrationHealthCheck>("integration", failureStatus: HealthStatus.Degraded, tags: ["ready"]);
        return services;
    }
}
