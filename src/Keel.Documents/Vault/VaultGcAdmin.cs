using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents.Vault;

/// <summary>One stored sweep as the admin page shows it.</summary>
public sealed record GcRunSummary(Guid Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string StartedBy, string Mode, int GraceHours, long ObjectsListed,
    long ObjectsReferenced, long Candidates, long Deleted, long BytesCandidates, long BytesDeleted, string ReportHash, string? Continuation, bool Complete, IReadOnlyList<string> SampleKeys);

/// <summary>
/// Administrator-facing garbage collection: read the last reports and run a DRY RUN. Deleting is deliberately not offered here: it is the
/// <c>keel-vault gc --delete</c> command (an operator at a shell with the database role) or the scheduled service with
/// <c>Keel:Vault:Gc:Mode=Delete</c> (ADR-0026). An administrator who reads a dry-run report can compare its hash with the one the delete run records.
/// </summary>
public sealed class VaultGcAdmin(VaultGarbageCollector collector, NpgsqlDataSource dataSource, IAuditLog audit, VaultOptions options)
{
    public async Task<Outcome<IReadOnlyList<GcRunSummary>>> RecentRunsAsync(Subject subject, int limit, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(Denied(subject, "vault.gc_report", correlationId), ct);
            return Outcome<IReadOnlyList<GcRunSummary>>.Forbidden(["role.missing"]);
        }
        var runs = new List<GcRunSummary>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, started_at, finished_at, started_by, mode, grace_hours, objects_listed, objects_referenced, candidates, deleted, bytes_candidates, bytes_deleted,
                   report_hash, continuation, complete, sample_keys
            FROM vault.gc_run ORDER BY started_at DESC LIMIT @n
            """, conn);
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 50));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            runs.Add(new GcRunSummary(r.GetGuid(0), new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetString(3), r.GetString(4),
                r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
                r.GetBoolean(14), (string[])r[15]));
        return Outcome<IReadOnlyList<GcRunSummary>>.Ok(runs);
    }

    /// <summary>Runs a dry-run sweep with the configured grace period and cap. Audited by the run itself (<c>vault.gc</c>).</summary>
    public async Task<Outcome<GcReport>> DryRunAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(Denied(subject, "vault.gc", correlationId), ct);
            return Outcome<GcReport>.Forbidden(["role.missing"]);
        }
        try
        {
            var gc = options.Gc;
            var report = await collector.RunAsync(new GcRequest(GcMode.DryRun, gc.Grace, gc.MaxDeletionsPerRun, gc.PageSize, null, ActorSnapshot.From(subject), correlationId), ct);
            return Outcome<GcReport>.Ok(report);
        }
        catch (GcAlreadyRunningException e)
        {
            return Outcome<GcReport>.Conflict(e.Message);
        }
    }

    private static AuditEntry Denied(Subject subject, string action, string? correlationId) => new()
    {
        Actor = ActorSnapshot.From(subject), Action = action, Outcome = AuditOutcome.Denied, ObjectType = "vault", CorrelationId = correlationId,
        Details = AuditDetails.Of(("reasons", "role.missing")),
    };

    internal static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
}
