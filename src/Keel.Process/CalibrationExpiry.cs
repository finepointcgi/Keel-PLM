using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Process;

/// <summary>
/// Raises calibration expiry notices of tools, fixtures and gauges: once per tool, stage (30, 14, 7 days, overdue) and due date, as an audit event of the system and a pointer-only
/// notification to the watchers of the tool (ADR-0030). A notice row makes the job idempotent. Recording a calibration moves the due date, so the next cycle gets fresh notices.
/// </summary>
public sealed class CalibrationExpiryService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var today = ProcessStore.Today(clock);
        var due = new List<(Guid Id, DateOnly Due)>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT t.id, t.due_on FROM proc.tool t WHERE t.requires_calibration AND t.status = 'active' AND t.due_on IS NOT NULL AND t.due_on <= @limit ORDER BY t.due_on", conn))
        {
            cmd.Parameters.AddWithValue("limit", today.AddDays(30));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add((r.GetGuid(0), r.GetFieldValue<DateOnly>(1)));
        }
        var raised = 0;
        foreach (var (id, dueOn) in due)
        {
            var stage = ProcessRules.NoticeStage(dueOn, today);
            if (stage is null) continue;
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand("INSERT INTO proc.tool_notice (tool_id, stage, due_on, noticed_at) VALUES (@t, @s, @d, @now) ON CONFLICT DO NOTHING", conn, tx))
            {
                ins.Parameters.AddWithValue("t", id);
                ins.Parameters.AddWithValue("s", stage);
                ins.Parameters.AddWithValue("d", dueOn);
                ins.Parameters.AddWithValue("now", clock.GetUtcNow());
                if (await ins.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); continue; }
            }
            var tool = await ProcessStore.LoadToolByIdAsync(conn, tx, id, false, ct);
            if (tool is null) { await tx.RollbackAsync(ct); continue; }
            await audit.AppendAsync(tx, new AuditEntry
            {
                Actor = ActorSnapshot.System,
                Action = "tool.calibration_notice",
                Outcome = AuditOutcome.Success,
                ObjectType = ProcessStore.ToolObjectType,
                ObjectId = tool.Number,
                Details = AuditDetails.Of(("stage", stage), ("due", dueOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            }, ct);
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(
                    stage == "overdue" ? NotificationKindCodes.ToolCalibrationOverdue : NotificationKindCodes.ToolCalibrationDue, AwarenessKinds.Tool, tool.Number, tool.Markings, null), ct);
            await tx.CommitAsync(ct);
            raised++;
        }
        return raised;
    }
}

/// <summary>Runs <see cref="CalibrationExpiryService"/> on a timer.</summary>
public sealed class CalibrationExpiryHostedService(CalibrationExpiryService service, ProcessOptions options, ILogger<CalibrationExpiryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ExpiryJobEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.CalibrationCheckMinutes)));
            do
            {
                try { await service.RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    logger.LogWarning(e, "The calibration expiry check failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
