using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Compliance;

/// <summary>
/// The compliance declarations of a part on its timeline: when a declaration was added, verified or withdrawn. No supplier or substance text appears (a declaration is shown
/// by its number and status). It never authorizes the timeline: the part's own sources do.
/// </summary>
internal sealed class ComplianceTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 45;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == "part";

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var part = await ComplianceStore.LoadPartAsync(conn, null, key, false, ct);
        if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
        var events = new List<TimelineEvent>();
        await using var cmd = new NpgsqlCommand(
            "SELECT d.number, d.source, d.status, d.created_at, d.created_by, d.verified_at, d.verified_by, d.updated_at FROM cmp.declaration d WHERE d.part_master_id = @p ORDER BY d.created_at, d.number", conn);
        cmd.Parameters.AddWithValue("p", part.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        long order = 0;
        var href = $"parts/{Uri.EscapeDataString(part.Number)}?tab=compliance";
        while (await r.ReadAsync(ct))
        {
            var number = r.GetString(0);
            var source = DeclarationSources.Label(r.GetString(1)).ToLowerInvariant();
            events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(3), TimelineKinds.Link, $"Compliance declaration {number} added", $"{char.ToUpperInvariant(source[0])}{source[1..]} declaration", r.GetString(4), null, href, false, Rank, order++));
            if (!r.IsDBNull(5))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(5), TimelineKinds.Lifecycle, $"{number} verified", null, ComplianceStore.Str(r, 6), null, href, false, Rank, order++));
            if (r.GetString(2) == DeclarationStatuses.Withdrawn && !r.IsDBNull(7))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(7), TimelineKinds.Lifecycle, $"{number} withdrawn", null, null, null, href, false, Rank, order++));
        }
        return new TimelineSlice(false, events);
    }
}

public static class ComplianceModule
{
    /// <summary>
    /// Registers the compliance services, the review gateway for regulatory list versions, the part timeline source and the expiry job. The host registers
    /// <see cref="INotificationSink"/> and the <see cref="IObjectAccess"/> lookups (the document lookup lets a declaration point at a document); both are optional here.
    /// </summary>
    public static IServiceCollection AddKeelCompliance(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = new ComplianceOptions();
        configuration?.GetSection("Keel:Compliance").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new RegulatoryCatalogService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DeclarationService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<INotificationSink>(), sp.GetServices<IObjectAccess>()));
        services.AddSingleton(sp => new ComplianceReportService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DeclarationExpiryService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<INotificationSink>()));
        services.AddHostedService<DeclarationExpiryHostedService>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ComplianceListWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<INotificationSink>()));
        services.AddSingleton<ITimelineSource, ComplianceTimelineSource>();
        return services;
    }
}
