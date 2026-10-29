using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Variants;

/// <summary>
/// The variants of a product on its part timeline: when each was defined and when it was released. Only for a product the caller may read and that has a model;
/// it never authorizes the timeline (the part's own source does) and shows nothing about products the caller cannot read.
/// </summary>
internal sealed class VariantTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 60;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == "part";

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var product = await VariantStore.LoadProductAsync(conn, null, key, ct);
        if (product is null || !AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
        var events = new List<TimelineEvent>();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, state, created_at, created_by, released_at, released_by FROM var.variant WHERE product_master_id = @p ORDER BY created_at, name", conn);
        cmd.Parameters.AddWithValue("p", product.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        long order = 0;
        var href = $"options/{Uri.EscapeDataString(product.Number)}";
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(2), TimelineKinds.Link, $"Variant {name} defined", null, r.GetString(3), null, href, false, Rank, order++));
            if (!r.IsDBNull(4) && r.GetString(1) is VariantStates.Released or VariantStates.Retired)
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(4), TimelineKinds.Lifecycle, $"Variant {name} released", null, r.IsDBNull(5) ? null : r.GetString(5), null, href, false, Rank, order++));
        }
        return new TimelineSlice(false, events);
    }
}

public static class VariantModule
{
    /// <summary>Registers <see cref="VariantService"/> and the part timeline source. The host registers <see cref="IDomainEventSink"/> (optional here).</summary>
    public static IServiceCollection AddKeelVariants(this IServiceCollection services)
    {
        services.AddSingleton(sp => new VariantService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IDomainEventSink>()));
        services.AddSingleton<ITimelineSource, VariantTimelineSource>();
        return services;
    }
}
