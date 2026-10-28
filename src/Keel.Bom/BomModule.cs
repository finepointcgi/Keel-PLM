using Keel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Bom;

public static class BomModule
{
    /// <summary>Registers <see cref="BomService"/>, the check-out participant that keeps BOM working copies in step with part check-out, and the configuration services (baselines, effectivity).</summary>
    public static IServiceCollection AddKeelBom(this IServiceCollection services)
    {
        services.AddSingleton<BomService>();
        services.AddSingleton<IPartIterationParticipant, BomParticipant>();
        services.AddSingleton<IStructureImpactProvider, BomStructureImpactProvider>(); // 1.6 impact analysis
        // Phase 2.1 configuration management (ADR-0021): baselines, effectivity contexts, revision effectivity for change notices.
        services.AddSingleton<BaselineService>();
        services.AddSingleton<EffectivityService>();
        services.AddSingleton<IRevisionEffectivity, RevisionEffectivityService>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new BaselineWorkflowGateway(sp.GetRequiredService<Keel.Audit.IAuditLog>()));
        // ---- Increment 2.3: EBOM / MBOM and reconciliation (ADR-0024)
        services.AddSingleton<MbomService>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new MbomWorkflowGateway(sp.GetRequiredService<Keel.Audit.IAuditLog>()));
        services.AddSingleton<IManufacturingBomSource, MbomBomSource>();
        services.AddSingleton<IManufacturingImpactProvider, MbomImpactProvider>();
        // ---- end 2.3
        return services;
    }
}
