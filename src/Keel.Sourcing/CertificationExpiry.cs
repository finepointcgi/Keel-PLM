using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Sourcing;

public sealed class SourcingOptions
{
    /// <summary>The expiry check runs on a timer in the host; turn it off on nodes that should not run it (it is idempotent, so several nodes are harmless).</summary>
    public bool ExpiryJobEnabled { get; set; } = true;
    public int CertificationCheckMinutes { get; set; } = 360;
}

/// <summary>
/// Raises the expiry notices of supplier certifications: once per certification and stage (60 days, 30 days, 7 days, expired), as an audit event of the system and a
/// notification to the watchers of the supplier. A notice row makes the job idempotent, so a restart or a second node never repeats a notice. Notifications carry
/// pointers only (ADR-0030); the audit event carries the certification type and the stage, never a name or a contact.
/// </summary>
public sealed class CertificationExpiryService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var today = SourcingStore.Today(clock);
        var due = new List<(Guid Cert, Guid Supplier, string Type, DateOnly Expires)>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(
            "SELECT c.id, c.supplier_id, c.cert_type, c.expires_on FROM src.supplier_certification c JOIN src.supplier s ON s.id = c.supplier_id WHERE NOT c.revoked AND s.status <> 'obsolete' AND c.expires_on <= @limit ORDER BY c.expires_on", conn))
        {
            cmd.Parameters.AddWithValue("limit", today.AddDays(SourcingRules.CertificationWarnDays));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3)));
        }
        var raised = 0;
        foreach (var (cert, supplierId, type, expires) in due)
        {
            var stage = SourcingRules.NoticeStage(expires, today);
            if (stage is null) continue;
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand("INSERT INTO src.certification_notice (certification_id, stage, noticed_at) VALUES (@c, @s, @now) ON CONFLICT DO NOTHING", conn, tx))
            {
                ins.Parameters.AddWithValue("c", cert);
                ins.Parameters.AddWithValue("s", stage);
                ins.Parameters.AddWithValue("now", clock.GetUtcNow());
                if (await ins.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); continue; }
            }
            var supplier = await SourcingStore.LoadSupplierByIdAsync(conn, tx, supplierId, false, ct);
            if (supplier is null) { await tx.RollbackAsync(ct); continue; }
            await audit.AppendAsync(tx, new AuditEntry
            {
                Actor = ActorSnapshot.System,
                Action = "supplier.cert_expiry_notice",
                Outcome = AuditOutcome.Success,
                ObjectType = SourcingStore.SupplierObjectType,
                ObjectId = supplier.Number,
                Details = AuditDetails.Of(("certType", type), ("stage", stage), ("expires", expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            }, ct);
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(
                    stage == "expired" ? NotificationKindCodes.SupplierCertExpired : NotificationKindCodes.SupplierCertExpiring, AwarenessKinds.Supplier, supplier.Number, supplier.Markings, null), ct);
            await tx.CommitAsync(ct);
            raised++;
        }
        return raised;
    }
}

/// <summary>Runs <see cref="CertificationExpiryService"/> on a timer.</summary>
public sealed class CertificationExpiryHostedService(CertificationExpiryService service, SourcingOptions options, ILogger<CertificationExpiryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ExpiryJobEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.CertificationCheckMinutes)));
            do
            {
                try { await service.RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    logger.LogWarning(e, "The certification expiry check failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
