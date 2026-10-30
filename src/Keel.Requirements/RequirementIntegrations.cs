using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Requirements;

/// <summary>Lets notifications, watch lists, comments and the activity feed find specifications: a key lookup that makes no access decision (ADR-0030).</summary>
internal sealed class SpecObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.ReqSpec;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await RequirementStore.LoadSpecAsync(conn, null, number, null, false, ct);
        return head is null ? null : new ObjectInfo(Kind, head.Number, head.Name, SpecStates.Label(head.State), head.Markings);
    }
}

/// <summary>
/// The life of a specification on its timeline: each revision and each iteration (who and when, no requirement text). It authorizes the timeline of a specification (its owner is this module).
/// Released and review events come from the workflow source.
/// </summary>
internal sealed class SpecTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 60;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == RequirementStore.ObjectType;

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await RequirementStore.LoadSpecAsync(conn, null, key, null, false, ct);
        if (head is null || !AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
        var events = new List<TimelineEvent>();
        long order = 0;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.created_at, r.created_by, i.iteration, i.created_at, i.created_by, i.requirement_count
            FROM req.spec_revision r JOIN req.spec_iteration i ON i.revision_id = r.id
            WHERE r.spec_id = @s ORDER BY i.created_at, i.iteration
            """, conn);
        cmd.Parameters.AddWithValue("s", head.Id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var href = $"requirements/{Uri.EscapeDataString(head.Number)}";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await rd.ReadAsync(ct))
        {
            var rev = rd.GetString(0);
            if (seen.Add(rev)) events.Add(new TimelineEvent(rd.GetFieldValue<DateTimeOffset>(1), TimelineKinds.Revision, $"Revision {rev} started", null, rd.GetString(2), rev, href, false, Rank, order++));
            events.Add(new TimelineEvent(rd.GetFieldValue<DateTimeOffset>(4), TimelineKinds.Iteration, $"Version {rev}.{rd.GetInt32(3).ToString(CultureInfo.InvariantCulture)} saved", null, rd.GetString(5), rev, href, false, Rank, order++));
        }
        return TimelineSlice.Of(events);
    }
}

/// <summary>Change impact: the requirements that trace to an affected part (additive; Keel.Change only sees <see cref="IRequirementImpactProvider"/>).</summary>
internal sealed class RequirementImpactProvider(RequirementService service) : IRequirementImpactProvider
{
    public async Task<Outcome<RequirementImpact>> AffectedRequirementsAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct = default)
    {
        var trace = await service.GetObjectTraceAsync(subject, objectType, key, correlationId, ct);
        return trace.IsOk ? Outcome<RequirementImpact>.Ok(new RequirementImpact(trace.Value!.Requirements)) : trace.Kind == OutcomeKind.NotFound ? Outcome<RequirementImpact>.NotFound() : Outcome<RequirementImpact>.Invalid(trace.Errors);
    }
}

/// <summary>Runs the suspect-link check on an interval (<c>Keel:Requirements:SweepSeconds</c>, 0 = off). Failures are logged without data and retried at the next tick.</summary>
internal sealed class SuspectSweepHostedService(RequirementService service, RequirementOptions options, ILogger<SuspectSweepHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.SweepSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.SweepSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await service.SweepSuspectLinksAsync(null, null, stoppingToken); }
                catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { log.LogWarning("The suspect-link check failed: {Error}", e.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }
}

public static class RequirementsModule
{
    /// <summary>
    /// Registers the requirements service, the review gateway, the awareness lookup, the specification timeline source, the change-impact provider and the suspect-link job.
    /// The host registers <see cref="INotificationSink"/> and <see cref="IBaselineMembership"/> (both optional here).
    /// </summary>
    public static IServiceCollection AddKeelRequirements(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = new RequirementOptions();
        configuration?.GetSection("Keel:Requirements").Bind(options);
        if (string.IsNullOrWhiteSpace(options.StepUpRequiredAcr)) options.StepUpRequiredAcr = configuration?["Keel:Auth:StepUpAcr"]; // ADR-0058
        services.AddSingleton(options);
        services.AddSingleton(sp => new RequirementService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<INotificationSink>(), sp.GetRequiredService<RequirementOptions>(), sp.GetService<IBaselineMembership>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new RequirementWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetService<INotificationSink>()));
        services.AddSingleton<IObjectAccess, SpecObjectAccess>();
        services.AddSingleton<ITimelineSource, SpecTimelineSource>();
        services.AddSingleton<IRequirementImpactProvider, RequirementImpactProvider>();
        services.AddHostedService<SuspectSweepHostedService>();
        return services;
    }
}
