using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>Lets notifications, watch lists, comments and the activity feed find suppliers: a key lookup that makes no access decision (ADR-0030).</summary>
internal sealed class SupplierObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Supplier;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await SourcingStore.LoadSupplierAsync(conn, null, number, false, ct);
        return row is null ? null : new ObjectInfo(AwarenessKinds.Supplier, row.Number, row.Name, SupplierStatuses.Label(row.Status), row.Markings);
    }
}

/// <summary>
/// The sourcing entries of a part on its timeline: when an entry was added and when it became active or was withdrawn. Entries whose supplier the caller may not read are
/// omitted; no supplier name appears (an entry is shown by its number, list and status). It never authorizes the timeline: the part's own sources do.
/// </summary>
internal sealed class SourcingTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 40;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == "part";

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var part = await SourcingStore.LoadPartAsync(conn, null, key, false, ct);
        if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
        var sFilter = AccessPolicy.BuildReadFilter(subject, "s");
        var events = new List<TimelineEvent>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT e.number, e.kind, e.status, e.approval_state, e.created_at, e.updated_at, e.created_by FROM src.source_entry e JOIN src.supplier s ON s.id = e.supplier_id WHERE e.part_master_id = @p AND {sFilter.Sql} ORDER BY e.created_at, e.number", conn);
        foreach (var (k, v) in sFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("p", part.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        long order = 0;
        var href = $"parts/{Uri.EscapeDataString(part.Number)}?tab=sourcing";
        while (await r.ReadAsync(ct))
        {
            var number = r.GetString(0);
            var list = SourceKinds.Label(r.GetString(1)).ToLowerInvariant();
            events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(4), TimelineKinds.Link, $"{number} added to the {list} list", $"Status {EntryStatuses.Label(r.GetString(2)).ToLowerInvariant()}", r.GetString(6), null, href, false, Rank, order++));
            var state = r.GetString(3);
            if (!r.IsDBNull(5) && state is EntryApprovalStates.Active or EntryApprovalStates.Withdrawn)
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(5), TimelineKinds.Lifecycle, state == EntryApprovalStates.Active ? $"{number} approved" : $"{number} withdrawn", null, null, null, href, false, Rank, order++));
        }
        return new TimelineSlice(false, events);
    }
}

public static class SourcingModule
{
    /// <summary>
    /// Registers the sourcing services, the two review gateways (suppliers, list entries), the awareness lookup, the part timeline source and the certification expiry job.
    /// The host registers <see cref="INotificationSink"/>, <see cref="IContentStore"/> and <see cref="IContentReader"/> (all optional here) and the Quality module may register
    /// <see cref="IQualityReferenceResolver"/> implementations.
    /// </summary>
    public static IServiceCollection AddKeelSourcing(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = new SourcingOptions();
        configuration?.GetSection("Keel:Sourcing").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new SupplierService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<INotificationSink>(), sp.GetServices<IQualityReferenceResolver>()));
        services.AddSingleton(sp => new SourcingService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RequestService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IContentReader>()));
        services.AddSingleton(sp => new PortalService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IContentStore>(), sp.GetService<IContentReader>()));
        services.AddSingleton(sp => new CertificationExpiryService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<INotificationSink>()));
        services.AddHostedService<CertificationExpiryHostedService>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new SupplierWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<INotificationSink>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new SourceEntryWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IObjectAccess, SupplierObjectAccess>();
        services.AddSingleton<ITimelineSource, SourcingTimelineSource>();
        return services;
    }
}
