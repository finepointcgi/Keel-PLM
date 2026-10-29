using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Reports;

public static class ReportsModule
{
    /// <summary>
    /// Registers the output services (ADR-0044). The host registers <see cref="IReleasePackageSource"/>, <see cref="IBomExchangeSource"/>, <see cref="ICadPartPort"/>, <see cref="IContentStore"/>,
    /// <see cref="IContentReader"/>, the <see cref="IMassChangeTarget"/>s and the <see cref="IReportDataset"/>s. <paramref name="notices"/> words the distribution notices of a marking (the host
    /// passes the one that stamps downloads) and <paramref name="ensureFonts"/> installs the PDF font resolver.
    /// </summary>
    public static IServiceCollection AddKeelReports(this IServiceCollection services, Func<Markings, IReadOnlyList<string>> notices, Action ensureFonts)
    {
        services.AddSingleton(sp => new ReleasePackageService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IReleasePackageSource>(), sp.GetRequiredService<IContentStore>(), sp.GetRequiredService<IContentReader>(), notices, ensureFonts));
        services.AddSingleton(sp => new BomExchangeService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IBomExchangeSource>(), sp.GetRequiredService<ICadPartPort>()));
        services.AddSingleton(sp => new MassChangeService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IMassChangeTarget>()));
        services.AddSingleton(sp => new ReportBuilderService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IReportDataset>(), notices, ensureFonts));
        return services;
    }
}
