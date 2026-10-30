using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Quality;

/// <summary>Settings under <c>Keel:Quality</c>.</summary>
public sealed class QualityOptions
{
    public const string Section = "Keel:Quality";
    /// <summary>Whether the background job that expires deviations and reports overdue actions runs (it can always be run once by hand).</summary>
    public bool MaintenanceEnabled { get; set; } = true;
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>How many days before its end an approved deviation or waiver is announced as expiring.</summary>
    public int ExpiryWarningDays { get; set; } = 14;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaintenanceInterval < TimeSpan.FromSeconds(30)) errors.Add("Keel:Quality:MaintenanceInterval must be at least 30 seconds.");
        if (ExpiryWarningDays is < 1 or > 90) errors.Add("Keel:Quality:ExpiryWarningDays must be between 1 and 90.");
        return errors;
    }
}

/// <summary>What one maintenance pass did.</summary>
public sealed record MaintenanceReport(int Expired, int ExpiryWarnings, int OverdueActions);

/// <summary>
/// The system's own work on quality records (ADR-0036): an approved deviation or waiver whose window ended becomes Expired (a <c>system</c> lifecycle step, audited as the
/// system actor, with a notification to the requester and the watchers); one that ends soon is announced once; an action item past its due date is reported once to its
/// owner. Each record is handled in its own transaction, so one failure never blocks the rest, and a second pass finds nothing left to do.
/// </summary>
public sealed class QualityMaintenance(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, QualityOptions options, INotificationSink? notifications = null)
{
    public async Task<MaintenanceReport> RunOnceAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var expired = await ExpireAsync(today, ct);
        var warned = await WarnAsync(today, ct);
        var overdue = await OverdueAsync(today, ct);
        return new MaintenanceReport(expired, warned, overdue);
    }

    private async Task<List<Guid>> IdsAsync(string sql, DateOnly day, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("day", day.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("soon", day.AddDays(options.ExpiryWarningDays).ToDateTime(TimeOnly.MinValue));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }

    private async Task<int> ExpireAsync(DateOnly today, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in await IdsAsync("SELECT id FROM qual.deviation WHERE lifecycle_state = 'Approved' AND valid_to < @day ORDER BY valid_to, number", today, ct))
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var head = await QualityStore.LoadByIdAsync(conn, tx, QualityKinds.Deviation, id, forUpdate: true, ct);
            if (head is null || head.State != "Approved") { await tx.RollbackAsync(ct); continue; }
            var now = clock.GetUtcNow();
            var moved = await QualityStore.TransitionAsync(conn, tx, head, "Expired", "system", ActorSnapshot.System.Id, now, "close_reason = 'The approved window ended.'");
            if (!moved.IsOk) { await tx.RollbackAsync(ct); continue; }
            await QualityStore.AppendHistoryAsync(conn, tx, QualityKinds.Deviation, id, ActorSnapshot.System.Id, "Keel system", "Expired", "The approved window ended.", now, ct);
            await audit.AppendAsync(tx, QualityStore.SystemEntry("deviation.expire", QualityKinds.Deviation, head.Number, ("from", "Approved"), ("to", "Expired")), ct);
            if (notifications is not null)
            {
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.DeviationExpired, QualityKinds.Deviation, head.Number, head.Markings, null, head.RaiserSub, null, id), ct);
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.DeviationExpired, QualityKinds.Deviation, head.Number, head.Markings, null, null, null, id), ct);
            }
            await tx.CommitAsync(ct);
            count++;
        }
        return count;
    }

    private async Task<int> WarnAsync(DateOnly today, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in await IdsAsync("SELECT id FROM qual.deviation WHERE lifecycle_state = 'Approved' AND expiry_warned_at IS NULL AND valid_to >= @day AND valid_to <= @soon ORDER BY valid_to, number", today, ct))
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var head = await QualityStore.LoadByIdAsync(conn, tx, QualityKinds.Deviation, id, forUpdate: true, ct);
            if (head is null || head.State != "Approved") { await tx.RollbackAsync(ct); continue; }
            await using (var up = QualityStore.Cmd(conn, tx, "UPDATE qual.deviation SET expiry_warned_at = @at WHERE id = @id AND expiry_warned_at IS NULL"))
            {
                up.Parameters.AddWithValue("at", clock.GetUtcNow());
                up.Parameters.AddWithValue("id", id);
                if (await up.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); continue; }
            }
            await audit.AppendAsync(tx, QualityStore.SystemEntry("deviation.expiry_warning", QualityKinds.Deviation, head.Number, ("days", options.ExpiryWarningDays.ToString(CultureInfo.InvariantCulture))), ct);
            if (notifications is not null)
            {
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.DeviationExpiring, QualityKinds.Deviation, head.Number, head.Markings, null, head.RaiserSub, null, id), ct);
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.DeviationExpiring, QualityKinds.Deviation, head.Number, head.Markings, null, null, null, id), ct);
            }
            await tx.CommitAsync(ct);
            count++;
        }
        return count;
    }

    private async Task<int> OverdueAsync(DateOnly today, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in await IdsAsync("SELECT id FROM qual.action WHERE status IN ('Open', 'InProgress') AND overdue_notified_at IS NULL AND due_date < @day ORDER BY due_date, number", today, ct))
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string kind, ownerSub, number;
            Guid parent;
            await using (var q = QualityStore.Cmd(conn, tx, "SELECT parent_kind, parent_id, owner_sub, number FROM qual.action WHERE id = @id AND status IN ('Open', 'InProgress') AND overdue_notified_at IS NULL FOR UPDATE"))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) { await tx.RollbackAsync(ct); continue; }
                (kind, parent, ownerSub, number) = (r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3));
            }
            var head = await QualityStore.LoadByIdAsync(conn, tx, kind, parent, forUpdate: false, ct);
            if (head is null) { await tx.RollbackAsync(ct); continue; }
            await using (var up = QualityStore.Cmd(conn, tx, "UPDATE qual.action SET overdue_notified_at = @at WHERE id = @id"))
            {
                up.Parameters.AddWithValue("at", clock.GetUtcNow());
                up.Parameters.AddWithValue("id", id);
                await up.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, QualityStore.SystemEntry(kind + ".action_overdue", kind, head.Number, ("action", number)), ct);
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.QualityActionOverdue, kind, head.Number, head.Markings, null, ownerSub, id), ct);
            await tx.CommitAsync(ct);
            count++;
        }
        return count;
    }
}

internal sealed class QualityMaintenanceService(QualityMaintenance maintenance, QualityOptions options, ILogger<QualityMaintenanceService> logger) : BackgroundService
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
                if (report.Expired + report.ExpiryWarnings + report.OverdueActions > 0)
                    logger.LogInformation("Quality maintenance: {Expired} expired, {Warnings} expiry warnings, {Overdue} overdue actions.", report.Expired, report.ExpiryWarnings, report.OverdueActions);
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning(e, "Quality maintenance failed; it will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>The services of the module wired by hand (tests and tools); the host uses <see cref="QualityModule"/>.</summary>
public sealed record QualityServices(NcrService Ncrs, CapaService Capas, DeviationService Deviations, QualityActions Actions, QualityQueries Queries, QualityMaintenance Maintenance);

public static class QualityModule
{
    public static QualityServices Create(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IEnumerable<IChangeObjectGateway> partGateways, IEnumerable<IObjectAccess> objects,
        INotificationSink? notifications = null, QualityOptions? options = null)
    {
        var run = new QualityRunner(dataSource, audit, clock);
        var lookups = new QualityLookups(partGateways, objects);
        var deviations = new DeviationService(run, lookups, notifications);
        return new QualityServices(new NcrService(run, lookups, notifications), new CapaService(run, lookups, notifications), deviations,
            new QualityActions(run, lookups, notifications), new QualityQueries(run, deviations), new QualityMaintenance(dataSource, audit, clock, options ?? new QualityOptions(), notifications));
    }

    /// <summary>
    /// Registers the quality services, the three review gateways (NCR material review board, CAPA verification, deviation approval), the object lookups of the awareness
    /// features, the supplier page's reference resolver, the part timeline source and the maintenance job. The host must also register an
    /// <see cref="IChangeObjectGateway"/> for parts (it does, for change management) and the other modules' <see cref="IObjectAccess"/> lookups.
    /// </summary>
    public static IServiceCollection AddKeelQuality(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(QualityOptions.Section).Get<QualityOptions>() ?? new QualityOptions();
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid quality configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton(sp => Create(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IChangeObjectGateway>(), sp.GetServices<IObjectAccess>(), sp.GetService<INotificationSink>(), sp.GetRequiredService<QualityOptions>()));
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Ncrs);
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Capas);
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Deviations);
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Actions);
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Queries);
        services.AddSingleton(sp => sp.GetRequiredService<QualityServices>().Maintenance);
        foreach (var kind in QualityKinds.All)
        {
            var k = kind;
            services.AddSingleton<IObjectAccess>(sp => new QualityObjectAccess(sp.GetRequiredService<NpgsqlDataSource>(), k));
            services.AddSingleton<IWorkflowObjectGateway>(sp => new QualityWorkflowGateway(k, sp.GetRequiredService<IAuditLog>()));
        }
        services.AddSingleton<IQualityReferenceResolver>(sp => new QualityReferenceResolver(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ISupplierQualityFacts>(sp => new SupplierQualityFacts(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ITimelineSource>(sp => new QualityTimelineSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddHostedService<QualityMaintenanceService>();
        return services;
    }
}
