using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Process;

/// <summary>Lets notifications, watch lists, comments and the activity feed find process plans (a key lookup that makes no access decision, ADR-0030).</summary>
internal sealed class PlanObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.ProcessPlan;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await ProcessStore.LoadByNumberAsync(conn, null, number, null, false, ct);
        return head is null ? null : new ObjectInfo(AwarenessKinds.ProcessPlan, head.Number, $"{head.PartName} ({head.Plant})", PlanStates.Label(head.State), head.Markings);
    }
}

internal sealed class ToolObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Tool;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var t = await ProcessStore.LoadToolAsync(conn, null, number, false, ct);
        return t is null ? null : new ObjectInfo(AwarenessKinds.Tool, t.Number, t.Name, t.Status == "retired" ? "Retired" : "Active", t.Markings);
    }
}

/// <summary>
/// The timeline of a process plan (check-ins, revisions, releases) and the plan entries on the timeline of its MBOM. Entries about the MBOM never authorize the MBOM page; for a plan the
/// source answers accessible only when the plan itself is readable (plan markings; the MBOM is checked by its owner's source for the MBOM page).
/// </summary>
internal sealed class ProcessTimelineSource(NpgsqlDataSource dataSource, IMbomProcessSource mboms) : ITimelineSource
{
    public const int RankValue = 45;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType is ProcessStore.PlanObjectType or "mbom";

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        string number;
        if (objectType == "mbom")
        {
            await using var q = new NpgsqlCommand("SELECT p.number FROM proc.plan p WHERE p.mbom_number = @m", conn);
            q.Parameters.AddWithValue("m", key);
            number = (string?)await q.ExecuteScalarAsync(ct) ?? "";
        }
        else number = key;
        var head = number.Length == 0 ? null : await ProcessStore.LoadByNumberAsync(conn, null, number, null, false, ct);
        if (head is null || !AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted || await mboms.FindAsync(subject, head.MbomNumber, correlationId, ct) is null)
            return TimelineSlice.NotAccessible;
        var events = new List<TimelineEvent>();
        var href = AwarenessKinds.Path(AwarenessKinds.ProcessPlan, head.Number);
        long order = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT revision, created_at, created_by, lifecycle_state, state_changed_at, state_changed_by FROM proc.plan_revision WHERE plan_id = @p ORDER BY created_at", conn))
        {
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rev = r.GetString(0);
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(1), TimelineKinds.Revision, $"Process plan {head.Number} revision {rev} started", null, r.GetString(2), rev, href, false, Rank, order++));
                var state = r.GetString(3);
                if (state is PlanStates.Released or PlanStates.Obsolete)
                    events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(4), TimelineKinds.Lifecycle, $"Process plan revision {rev} {(state == PlanStates.Released ? "released" : "superseded")}", null, state == PlanStates.Released ? r.GetString(5) : null, rev, href, false, Rank, order++));
            }
        }
        if (objectType == ProcessStore.PlanObjectType)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT r.revision, i.iteration, i.created_at, i.created_by FROM proc.plan_iteration i JOIN proc.plan_revision r ON r.id = i.revision_id WHERE r.plan_id = @p AND NOT i.is_open ORDER BY i.created_at", conn);
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(2), TimelineKinds.Iteration, $"Version {r.GetString(0)}.{r.GetInt32(1)} checked in", null, r.GetString(3), r.GetString(0), href, false, Rank, order++));
        }
        return new TimelineSlice(false, events);
    }
}

public static class ProcessModule
{
    /// <summary>
    /// Registers process planning (ADR-0042): the plan service, tooling and work centers, the plan review gateway, the awareness lookups, the timeline source and the calibration expiry job.
    /// The host registers <see cref="IMbomProcessSource"/> (through <c>AddKeelBom</c>), <see cref="IViewableFileSource"/> and <see cref="INotificationSink"/> (the last two optional here).
    /// </summary>
    public static IServiceCollection AddKeelProcess(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = new ProcessOptions();
        configuration?.GetSection("Keel:Process").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new ProcessPlanService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IMbomProcessSource>(), sp.GetService<Keel.Core.Viewing.IViewableFileSource>(), sp.GetService<INotificationSink>()));
        services.AddSingleton(sp => new ToolingService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IMbomProcessSource>(), sp.GetService<INotificationSink>()));
        services.AddSingleton(sp => new CalibrationExpiryService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<INotificationSink>()));
        services.AddHostedService<CalibrationExpiryHostedService>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ProcessWorkflowGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<ProcessPlanService>(),
            sp.GetRequiredService<IMbomProcessSource>(), sp.GetService<INotificationSink>()));
        services.AddSingleton<IObjectAccess, PlanObjectAccess>();
        services.AddSingleton<IObjectAccess, ToolObjectAccess>();
        services.AddSingleton<ITimelineSource, ProcessTimelineSource>();
        return services;
    }
}
