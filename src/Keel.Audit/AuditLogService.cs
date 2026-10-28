using System.Globalization;
using System.Text.Json;
using Keel.Core;
using Npgsql;

namespace Keel.Audit;

/// <summary>
/// Auditor-facing operations on the audit log, shared by the REST API and the UI. Reading or verifying the log is
/// itself audited, and the auditor role is checked here as well as by endpoint policy (defence in depth).
/// </summary>
public sealed class AuditLogService(NpgsqlDataSource dataSource, AuditQueries queries, IAuditLog log, TrustedCheckpointKeys keys)
{
    public async Task<Outcome<IReadOnlyList<AuditRecord>>> ListAsync(Subject subject, AuditQuery query, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyUnlessAuditor(subject, "audit.read", correlationId, ct)) return Outcome<IReadOnlyList<AuditRecord>>.Forbidden(["role.missing"]);
        var events = await queries.ListAsync(query, ct);
        await log.AppendAsync(Entry(subject, "audit.read", AuditOutcome.Success, correlationId,
            ("after", query.AfterSeq?.ToString(CultureInfo.InvariantCulture)), ("objectType", query.ObjectType), ("objectId", query.ObjectId),
            ("actor", query.Actor), ("returned", events.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<IReadOnlyList<AuditRecord>>.Ok(events);
    }

    public async Task<Outcome<VerificationResult>> VerifyAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyUnlessAuditor(subject, "audit.verify", correlationId, ct)) return Outcome<VerificationResult>.Forbidden(["role.missing"]);
        VerificationResult result;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
            result = await AuditVerifier.VerifyAsync(conn, keys.Keys, ct);
        await log.AppendAsync(Entry(subject, "audit.verify", result.Valid ? AuditOutcome.Success : AuditOutcome.Failure, correlationId,
            ("eventsChecked", result.EventsChecked.ToString(CultureInfo.InvariantCulture)),
            ("checkpointsChecked", result.CheckpointsChecked.ToString(CultureInfo.InvariantCulture)),
            ("failures", result.Failures.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<VerificationResult>.Ok(result);
    }

    /// <summary>The actor's login name recorded in the payload (null for events written before it was captured).</summary>
    public static string? ActorName(AuditRecord record)
    {
        using var doc = JsonDocument.Parse(record.Payload);
        return doc.RootElement.GetProperty("actor").TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
    }

    private async Task<bool> DenyUnlessAuditor(Subject subject, string action, string? correlationId, CancellationToken ct)
    {
        if (subject.Roles.Contains(Roles.Auditor)) return false;
        await log.AppendAsync(Entry(subject, action, AuditOutcome.Denied, correlationId, ("reason", "role.missing")), ct);
        return true;
    }

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = "audit_log",
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };
}
