using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Programs;

/// <summary>Lets notifications, comments, watch lists and the activity feed find a program by code: a key lookup that decides nothing about access (ADR-0030).</summary>
internal sealed class ProgramObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Program;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await ProgramStore.LoadProgramAsync(conn, null, number, false, ct);
        return head is null ? null : new ObjectInfo(Kind, head.Code, head.Name, ProgramStatuses.Label(head.Status), head.Markings);
    }
}

/// <summary>The same for a gate review, found by its number.</summary>
internal sealed class GateObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Gate;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var item = await ProgramStore.LoadItemByGateAsync(conn, null, number, false, ct);
        return item?.GateNumber is null ? null : new ObjectInfo(Kind, item.GateNumber, item.Title, GateStates.Label(item.GateState!), item.Markings);
    }
}

/// <summary>
/// The events of a program on its timeline: baselines taken, milestones reached, and schedule moves (key and days only, no titles). Only rows the caller may read are named
/// (the SQL read filter); it authorizes the timeline of a program (its owner is this module).
/// </summary>
internal sealed class ProgramTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 65;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == ProgramStore.ObjectType;

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await ProgramStore.LoadProgramAsync(conn, null, key, false, ct);
        if (head is null || !AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
        var events = new List<TimelineEvent>();
        long order = 0;
        var href = AwarenessKinds.Path(AwarenessKinds.Program, head.Code);
        await using (var cmd = new NpgsqlCommand("SELECT seq, name, created_at, created_by FROM prg.baseline WHERE program_id = @p ORDER BY seq", conn))
        {
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(2), TimelineKinds.Baseline, $"Schedule baseline {r.GetInt32(0).ToString(CultureInfo.InvariantCulture)} taken", null, r.GetString(3), null, href + "?tab=schedule", false, Rank, order++));
        }
        var filter = AccessPolicy.BuildReadFilter(subject, "i");
        await using (var cmd = new NpgsqlCommand(
            $"SELECT i.key, i.gate_number, i.updated_at, i.updated_by, {MarkingColumns.Select("i")} FROM prg.item i WHERE i.program_id = @p AND {filter.Sql} AND i.kind = 'Milestone' AND i.status = 'Complete' ORDER BY i.updated_at, i.key", conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(2), TimelineKinds.Lifecycle, $"Milestone {r.GetString(0)} reached", null, r.GetString(3), null,
                    r.IsDBNull(1) ? href + "?tab=schedule" : AwarenessKinds.Path(AwarenessKinds.Gate, r.GetString(1)), false, Rank, order++, MarkingColumns.Read(r, 4)));
        }
        var cFilter = AccessPolicy.BuildReadFilter(subject, "i");
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT i.key, c.at, c.actor_name, c.slip_days, {MarkingColumns.Select("i")}
            FROM prg.schedule_change c JOIN prg.item i ON i.id = c.item_id
            WHERE c.program_id = @p AND {cFilter.Sql} AND c.slip_days <> 0 ORDER BY c.at, c.id
            """, conn))
        {
            foreach (var (k, v) in cFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var days = r.GetInt32(3);
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(1), TimelineKinds.Lifecycle, $"{r.GetString(0)} moved {Math.Abs(days).ToString(CultureInfo.InvariantCulture)} day(s) {(days > 0 ? "later" : "earlier")}",
                    null, r.GetString(2), null, href + "?tab=schedule", false, Rank, order++, MarkingColumns.Read(r, 4)));
            }
        }
        return TimelineSlice.Of(events);
    }
}

/// <summary>Settings under <c>Keel:Programs</c>.</summary>
public sealed class ProgramOptions
{
    public const string Section = "Keel:Programs";
    public bool MaintenanceEnabled { get; set; } = true;
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>How many days before its date an open milestone is announced as due.</summary>
    public int DueWarningDays { get; set; } = 7;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaintenanceInterval < TimeSpan.FromSeconds(30)) errors.Add("Keel:Programs:MaintenanceInterval must be at least 30 seconds.");
        if (DueWarningDays is < 1 or > 90) errors.Add("Keel:Programs:DueWarningDays must be between 1 and 90.");
        return errors;
    }
}

/// <summary>
/// The system's own work on schedules (ADR-0043): an open milestone that is due within a few days, and one past its planned date, is reported once to its owner as a pointer-only
/// system notice. Each milestone is handled in its own transaction (one failure never blocks the rest) and a second pass finds nothing left to do.
/// </summary>
public sealed class ProgramMaintenance(NpgsqlDataSource dataSource, Keel.Audit.IAuditLog audit, TimeProvider clock, ProgramOptions options, INotificationSink? notifications = null)
{
    public async Task<ProgramMaintenanceReport> RunOnceAsync(CancellationToken ct = default)
    {
        var today = ProgramStore.Today(clock);
        return new ProgramMaintenanceReport(await NoticeAsync(today, overdue: false, ct), await NoticeAsync(today, overdue: true, ct));
    }

    private async Task<int> NoticeAsync(DateOnly today, bool overdue, CancellationToken ct)
    {
        var ids = new List<Guid>();
        var column = overdue ? "overdue_notified_at" : "due_notified_at";
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                SELECT i.id FROM prg.item i JOIN prg.program p ON p.id = i.program_id
                WHERE i.kind = 'Milestone' AND i.status IN ('NotStarted', 'InProgress', 'Blocked') AND i.owner_sub IS NOT NULL AND i.{column} IS NULL AND p.status NOT IN ('Complete', 'Cancelled')
                  AND {(overdue ? "i.planned_end < @day" : "i.planned_end >= @day AND i.planned_end <= @soon")}
                ORDER BY i.planned_end, i.key
                """, conn);
            cmd.Parameters.AddWithValue("day", today.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("soon", today.AddDays(options.DueWarningDays).ToDateTime(TimeOnly.MinValue));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        }
        var count = 0;
        foreach (var id in ids)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var item = await ProgramStore.LoadItemByIdAsync(conn, tx, id, true, ct);
            if (item?.OwnerSub is null) { await tx.RollbackAsync(ct); continue; }
            await using (var up = ProgramStore.Cmd(conn, tx, $"UPDATE prg.item SET {column} = @at WHERE id = @id AND {column} IS NULL AND status IN ('NotStarted', 'InProgress', 'Blocked')"))
            {
                up.Parameters.AddWithValue("at", clock.GetUtcNow());
                up.Parameters.AddWithValue("id", id);
                if (await up.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); continue; }
            }
            var program = (await ProgramStore.LoadProgramByIdAsync(conn, tx, item.ProgramId, ct))!;
            await audit.AppendAsync(tx, ProgramStore.SystemEntry(overdue ? "program.milestone_overdue" : "program.milestone_due", ProgramStore.ObjectType, program.Code, ("key", item.Key)), ct);
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(overdue ? NotificationKindCodes.MilestoneOverdue : NotificationKindCodes.MilestoneDue, ProgramStore.ObjectType, program.Code, item.Markings, null, item.OwnerSub, id), ct);
            await tx.CommitAsync(ct);
            count++;
        }
        return count;
    }
}

internal sealed class ProgramMaintenanceService(ProgramMaintenance maintenance, ProgramOptions options, ILogger<ProgramMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.MaintenanceEnabled) return;
        using var timer = new PeriodicTimer(options.MaintenanceInterval);
        do
        {
            try
            {
                var report = await maintenance.RunOnceAsync(stoppingToken);
                if (report.Due + report.Overdue > 0) logger.LogInformation("Program maintenance: {Due} due notices, {Overdue} overdue notices.", report.Due, report.Overdue);
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning(e, "Program maintenance failed; it will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class ProgramModule
{
    /// <summary>The service wired by hand (tests and tools); the host uses <see cref="AddKeelPrograms"/>.</summary>
    public static ProgramService Create(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IEnumerable<IObjectAccess> objects, INotificationSink? notifications = null, IDomainEventSink? events = null) =>
        new(dataSource, audit, clock, objects.Append(new ProgramObjectAccess(dataSource)), notifications, events);

    public static IWorkflowObjectGateway CreateGateway(IAuditLog audit, ProgramService service) => new ProgramGateway(audit, service);

    public static ProgramMaintenance CreateMaintenance(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null, ProgramOptions? options = null) =>
        new(dataSource, audit, clock, options ?? new ProgramOptions(), notifications);

    /// <summary>
    /// Registers the program service, the gate review gateway (workflow object type <c>gate</c>), the object lookups of the awareness features (program, gate), the program timeline
    /// source and the maintenance job. Deliverable targets are read through the other modules' <see cref="IObjectAccess"/> lookups, which the host registers.
    /// </summary>
    public static IServiceCollection AddKeelPrograms(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ProgramOptions.Section).Get<ProgramOptions>() ?? new ProgramOptions();
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid programs configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton<IObjectAccess>(sp => new ProgramObjectAccess(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IObjectAccess>(sp => new GateObjectAccess(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new ProgramService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IObjectAccess>(), sp.GetService<INotificationSink>(), sp.GetService<IDomainEventSink>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ProgramGateway(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<ProgramService>()));
        services.AddSingleton<ITimelineSource>(sp => new ProgramTimelineSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new ProgramMaintenance(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ProgramOptions>(), sp.GetService<INotificationSink>()));
        services.AddHostedService<ProgramMaintenanceService>();
        return services;
    }
}
