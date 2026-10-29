using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Compliance;

public sealed class ComplianceOptions
{
    /// <summary>The expiry and re-evaluation check runs on a timer in the host; turn it off on nodes that should not run it (it is idempotent, so several nodes are harmless).</summary>
    public bool ExpiryJobEnabled { get; set; } = true;
    public int CheckMinutes { get; set; } = 360;
}

/// <summary>
/// The daily work of the module: raises the expiry notices of declarations (60, 30, 7 days before and on expiry, once each), marks declarations that ran out as expired, and
/// re-evaluates every declaration when the regulations in force changed by the passing of an effective date (a version published for a later date becomes current). A notice
/// row makes the job idempotent. Audit events and notifications carry pointers only: the declaration number, the stage and the part the watchers follow (ADR-0030).
/// </summary>
public sealed class DeclarationExpiryService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var raised = await ReevaluateIfRegulationsChangedAsync(ct);
        var today = ComplianceStore.Today(clock);
        var due = new List<Guid>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT id FROM cmp.declaration WHERE status <> 'withdrawn' AND expires_on <= @limit ORDER BY expires_on, number", conn))
        {
            cmd.Parameters.AddWithValue("limit", today.AddDays(ComplianceRules.ExpiryWarnDays));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add(r.GetGuid(0));
        }
        foreach (var id in due)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var row = await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, id, true, ct);
            if (row is null || row.Status == DeclarationStatuses.Withdrawn) { await tx.RollbackAsync(ct); continue; }
            var stage = ComplianceRules.NoticeStage(row.ExpiresOn, today);
            if (stage is null) { await tx.RollbackAsync(ct); continue; }
            if (stage == "expired" && row.Status != DeclarationStatuses.Expired)
            {
                var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
                var (status, changed, previous) = await ComplianceEvaluator.RefreshAsync(conn, tx, row, ctx, ct);
                if (changed)
                    await audit.AppendAsync(tx, ComplianceStore.SystemEntry("compliance.status_change", ComplianceStore.PartObjectType, row.PartNumber,
                        ("declaration", row.Number), ("from", previous), ("to", status), ("cause", "expired")), ct);
            }
            await using (var ins = new NpgsqlCommand("INSERT INTO cmp.expiry_notice (declaration_id, stage, noticed_at) VALUES (@d, @s, @now) ON CONFLICT DO NOTHING", conn, tx))
            {
                ins.Parameters.AddWithValue("d", id);
                ins.Parameters.AddWithValue("s", stage);
                ins.Parameters.AddWithValue("now", clock.GetUtcNow());
                if (await ins.ExecuteNonQueryAsync(ct) == 0) { await tx.CommitAsync(ct); continue; }
            }
            await audit.AppendAsync(tx, ComplianceStore.SystemEntry("compliance.expiry_notice", ComplianceStore.PartObjectType, row.PartNumber,
                ("declaration", row.Number), ("stage", stage), ("expires", row.ExpiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), ct);
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(
                    stage == "expired" ? NotificationKindCodes.ComplianceDeclarationExpired : NotificationKindCodes.ComplianceDeclarationExpiring, AwarenessKinds.Part, row.PartNumber, row.PartMarkings, null), ct);
            await tx.CommitAsync(ct);
            raised++;
        }
        return raised;
    }

    /// <summary>When the list versions in force are not the ones the stored evaluations were made against (an effective date came), every declaration is evaluated again.</summary>
    private async Task<int> ReevaluateIfRegulationsChangedAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var stored = new HashSet<(string, int)>();
        await using (var cmd = new NpgsqlCommand("SELECT DISTINCT list_code, list_version FROM cmp.evaluation", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) stored.Add((r.GetString(0), r.GetInt32(1)));
        var inForce = ctx.Versions.Select(v => (v.Code, v.Version)).ToHashSet();
        if (stored.Count == 0 || stored.IsSubsetOf(inForce) && inForce.IsSubsetOf(stored)) { await tx.RollbackAsync(ct); return 0; }
        var changes = await ComplianceEvaluator.RefreshAllAsync(conn, tx, ctx, ct);
        foreach (var (row, from, to) in changes)
        {
            await audit.AppendAsync(tx, ComplianceStore.SystemEntry("compliance.status_change", ComplianceStore.PartObjectType, row.PartNumber,
                ("declaration", row.Number), ("from", from), ("to", to), ("cause", "effective_date")), ct);
            if (to == DeclarationStatuses.NonCompliant && notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ComplianceNonCompliant, AwarenessKinds.Part, row.PartNumber, row.PartMarkings, null), ct);
        }
        await tx.CommitAsync(ct);
        return changes.Count;
    }
}

/// <summary>Runs <see cref="DeclarationExpiryService"/> on a timer.</summary>
public sealed class DeclarationExpiryHostedService(DeclarationExpiryService service, ComplianceOptions options, ILogger<DeclarationExpiryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ExpiryJobEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.CheckMinutes)));
            do
            {
                try { await service.RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    logger.LogWarning(e, "The declaration expiry check failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
