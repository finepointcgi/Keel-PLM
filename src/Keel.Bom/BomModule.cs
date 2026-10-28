using Keel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Bom;

public static class BomModule
{
    /// <summary>Registers <see cref="BomService"/> and the check-out participant that keeps BOM working copies in step with part check-out.</summary>
    public static IServiceCollection AddKeelBom(this IServiceCollection services)
    {
        services.AddSingleton<BomService>();
        services.AddSingleton<IPartIterationParticipant, BomParticipant>();
        services.AddSingleton<IStructureImpactProvider, BomStructureImpactProvider>(); // 1.6 impact analysis
        return services;
    }
}
