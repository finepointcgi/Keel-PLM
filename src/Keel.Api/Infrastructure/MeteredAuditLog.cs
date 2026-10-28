using Keel.Audit;
using Npgsql;

namespace Keel.Api.Infrastructure;

public sealed class MeteredAuditLog(PostgresAuditLog inner) : IAuditLog
{
    public async Task<AuditRecord> AppendAsync(NpgsqlTransaction transaction, AuditEntry entry, CancellationToken ct = default) =>
        Count(await inner.AppendAsync(transaction, entry, ct));

    public async Task<AuditRecord> AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
        Count(await inner.AppendAsync(entry, ct));

    private static AuditRecord Count(AuditRecord r)
    {
        KeelMetrics.AuditEvents.WithLabels(r.Outcome.ToString()).Inc();
        if (r.Outcome == AuditOutcome.Denied) KeelMetrics.AccessDenials.WithLabels(r.Action).Inc();
        return r;
    }
}
