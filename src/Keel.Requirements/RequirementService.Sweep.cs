using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

public sealed partial class RequirementService
{
    /// <summary>
    /// Finds links whose part or document now has a newer revision than the one the link was reviewed against and marks them suspect (until someone reviews them), with one audit event per
    /// link and one pointer-only notification per specification for its watchers. A system job: it reads part and document revision labels and the link ends by number, shows nothing to a user
    /// and writes only audit events (numbers, keys, link type, reason). Reads of links already show this drift without waiting for the job.
    /// </summary>
    public async Task<SweepResult> SweepSuspectLinksAsync(Subject? actor, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var drifted = new List<(Guid Id, string Type, string Kind, string Target, string Revision, string Spec, string Key, Markings Markings)>();
        int checkedLinks;
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM req.link WHERE removed_at IS NULL AND status = 'ok' AND target_kind IN ('part', 'document')", conn, tx))
            checkedLinks = Convert.ToInt32(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT l.id, l.link_type, l.target_kind, l.target_number, cur.revision, sp.number, q.key, {MarkingColumns.Select("sp")}
            FROM req.link l
            JOIN req.requirement q ON q.id = l.source_requirement_id
            JOIN req.spec sp ON sp.id = q.spec_id
            CROSS JOIN LATERAL (
                SELECT r.revision FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE l.target_kind = 'part' AND m.number = l.target_number AND r.is_latest
                UNION ALL
                SELECT r.revision FROM plm.document_revision r JOIN plm.document_master m ON m.id = r.master_id WHERE l.target_kind = 'document' AND m.number = l.target_number AND r.is_latest
            ) cur
            WHERE l.removed_at IS NULL AND l.status = 'ok' AND l.target_kind IN ('part', 'document') AND l.reviewed_target_revision IS DISTINCT FROM cur.revision
            ORDER BY sp.number, q.key, l.id
            """, conn, tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) drifted.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), MarkingColumns.Read(r, 7)));
        }
        var now = clock.GetUtcNow();
        var marked = 0;
        foreach (var d in drifted)
        {
            await using var up = new NpgsqlCommand("UPDATE req.link SET status = 'suspect', suspect_reason = 'target_revised', suspect_at = @at WHERE id = @id AND status = 'ok' AND removed_at IS NULL", conn, tx);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("id", d.Id);
            if (await up.ExecuteNonQueryAsync(ct) != 1) continue;
            marked++;
            var details = new (string, string?)[] { ("key", d.Key), ("link", d.Id.ToString("D")), ("linkType", d.Type), ("targetKind", d.Kind), ("target", d.Target), ("revision", d.Revision), ("reason", SuspectReasons.TargetRevised) };
            await audit.AppendAsync(tx, actor is null ? SystemEntry("requirement.link_suspect", d.Spec, details) : Entry(actor, "requirement.link_suspect", AuditOutcome.Success, d.Spec, correlationId, details), ct);
        }
        if (notifications is not null)
            foreach (var spec in drifted.GroupBy(d => d.Spec))
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedChanged, AwarenessKinds.ReqSpec, spec.Key, spec.First().Markings, actor?.Id), ct);
        if (marked > 0 || actor is not null)
            await audit.AppendAsync(tx, actor is null ? SystemEntry("requirement.sweep", null, ("checked", Fmt(checkedLinks)), ("suspect", Fmt(marked))) : Entry(actor, "requirement.sweep", AuditOutcome.Success, null, correlationId, ("checked", Fmt(checkedLinks)), ("suspect", Fmt(marked))), ct);
        await tx.CommitAsync(ct);
        return new SweepResult(checkedLinks, marked);
    }

    /// <summary>The administrator's button for <see cref="SweepSuspectLinksAsync"/>.</summary>
    public async Task<Outcome<SweepResult>> SweepAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await audit.AppendAsync(tx, Entry(subject, "requirement.sweep", AuditOutcome.Denied, null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<SweepResult>.Forbidden([AccessPolicy.RoleMissing]);
        }
        return Outcome<SweepResult>.Ok(await SweepSuspectLinksAsync(subject, correlationId, ct));
    }
}
