using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Impact analysis of a change request or change notice (ADR-0019): where each affected part is used (every level, parents the caller may
/// not read stay redacted placeholders), which other open changes touch the same parts or any readable parent (conflict warnings), which
/// documents describe or are referenced by the parts, and, for revised assemblies, how the structure differs between the revision being
/// replaced and the new one. It uses only <c>Keel.Core</c> interfaces for everything outside change management; every query goes through the
/// read filter or the providers' own access checks, and the whole analysis is audited with counts.
/// </summary>
public sealed class ImpactAnalysisService(
    NpgsqlDataSource dataSource, IAuditLog audit, IEnumerable<IChangeObjectGateway> gateways,
    IEnumerable<IStructureImpactProvider> structure, IEnumerable<ILinkedDocumentProvider> documents)
{
    public const int DefaultLevels = 10;

    public async Task<Outcome<ImpactReport>> AnalyzeAsync(Subject subject, string number, int levels, string? correlationId, CancellationToken ct = default)
    {
        if (levels is < 1 or > 10) return Outcome<ImpactReport>.Invalid(["Levels must be between 1 and 10."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ChangeStore.LoadByNumberAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<ImpactReport>.NotFound();
        var objectType = ChangeTypes.ObjectTypeOf(head.Type);
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.impact", AuditOutcome.Denied, objectType, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ImpactReport>.NotFound();
        }

        var rows = await ChangeStore.LoadAffectedAsync(conn, tx, head.Id, ct);
        var live = new Dictionary<Guid, ChangeObject>();
        foreach (var group in rows.GroupBy(r => r.ObjectType))
        {
            var gateway = gateways.FirstOrDefault(g => g.ObjectType == group.Key);
            if (gateway is null) continue;
            var ids = group.SelectMany(r => r.ResultingRevisionId is { } res ? new[] { r.RevisionId, res } : [r.RevisionId]).Distinct().ToArray();
            foreach (var (rev, obj) in await gateway.FindByRevisionsAsync(conn, tx, ids, ct)) live[rev] = obj;
        }

        var shown = new List<Markings> { head.Markings };
        var objects = new List<ImpactObject>();
        var affectedMasters = new HashSet<Guid>();
        var parentMasters = new HashSet<Guid>();
        int parents = 0, redactedParents = 0, docs = 0, redactedDocs = 0, hidden = 0, structureChanges = 0;

        foreach (var row in rows)
        {
            if (!live.TryGetValue(row.RevisionId, out var obj) || !AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read).Permitted)
            {
                hidden++;
                objects.Add(new ImpactObject(row.Id, true, row.Action, null, null, null, null, [], 0, 0, [], null, null));
                continue;
            }
            shown.Add(obj.Markings);
            affectedMasters.Add(obj.MasterId);

            var whereUsed = new List<WhereUsedEntry>();
            if (structure.FirstOrDefault(p => p.ObjectType == row.ObjectType) is { } sp)
            {
                var wu = await sp.WhereUsedAsync(subject, obj.Key, levels, correlationId, ct);
                if (wu.IsOk) whereUsed.AddRange(wu.Value!);
            }
            foreach (var w in whereUsed)
            {
                if (w.Redacted) { redactedParents++; continue; }
                parents++;
                if (w.ParentMasterId is { } pm) parentMasters.Add(pm);
                if (w.Markings is { } wm) shown.Add(wm);
            }

            var linked = new List<LinkedDocument>();
            if (documents.FirstOrDefault(p => p.ObjectType == row.ObjectType) is { } dp)
            {
                var d = await dp.GetLinkedAsync(subject, obj.Key, obj.Revision, null, correlationId, ct);
                if (d.IsOk) linked.AddRange(d.Value!);
            }
            foreach (var d in linked)
            {
                if (d.Redacted) redactedDocs++;
                else { docs++; if (d.Markings is { } dm) shown.Add(dm); }
            }

            StructureComparison? compare = null;
            string? note = null;
            if (row.Action == ChangeActions.Revise && row.ResultingRevisionId is { } resultingId && live.TryGetValue(resultingId, out var resulting))
            {
                if (structure.FirstOrDefault(p => p.ObjectType == row.ObjectType) is { } cp)
                {
                    var cmp = await cp.CompareAsync(subject, obj.Key, obj.LatestIterationLabel, resulting.LatestIterationLabel, correlationId, ct);
                    if (cmp.IsOk)
                    {
                        compare = cmp.Value;
                        structureChanges += compare!.Changes.Count;
                        if (compare.Changes.Count == 0) note = $"No structure change between {obj.LatestIterationLabel} and {resulting.LatestIterationLabel}.";
                    }
                    else note = "The structure comparison is not available to you.";
                }
            }
            else if (row.Action == ChangeActions.Revise) note = "The new revision is created when this request becomes a change notice.";

            objects.Add(new ImpactObject(row.Id, false, row.Action, obj.Key, obj.Name, obj.Revision, obj.Markings.BannerText(), whereUsed,
                whereUsed.Count(w => w.Redacted), whereUsed.Count == 0 ? 0 : whereUsed.Max(w => w.Level), linked, compare, note));
        }

        var conflicts = await FindConflictsAsync(conn, tx, subject, head, affectedMasters, parentMasters, ct);
        foreach (var c in conflicts) shown.Add(c.Markings);

        var banner = Markings.AggregateBanner(shown);
        var summary = new ImpactSummary(rows.Count, hidden, parents, redactedParents, docs, redactedDocs, conflicts.Count, structureChanges);
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.impact", AuditOutcome.Success, objectType, number, correlationId,
            ("levels", levels.ToString(CultureInfo.InvariantCulture)), ("objects", rows.Count.ToString(CultureInfo.InvariantCulture)), ("hiddenObjects", hidden.ToString(CultureInfo.InvariantCulture)),
            ("parents", parents.ToString(CultureInfo.InvariantCulture)), ("redactedParents", redactedParents.ToString(CultureInfo.InvariantCulture)),
            ("documents", docs.ToString(CultureInfo.InvariantCulture)), ("redactedDocuments", redactedDocs.ToString(CultureInfo.InvariantCulture)),
            ("conflicts", conflicts.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<ImpactReport>.Ok(new ImpactReport(number, objects, [.. conflicts.Select(c => c.Conflict)], summary, banner));
    }

    private sealed record Found(ImpactConflict Conflict, Markings Markings);

    /// <summary>
    /// Other open changes (not final, readable by the caller) that list the same objects or any readable parent of them. The change's own
    /// ancestors and descendants are excluded: a notice is not in conflict with the request it implements.
    /// </summary>
    private static async Task<List<Found>> FindConflictsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ChangeRow head,
        HashSet<Guid> affectedMasters, HashSet<Guid> parentMasters, CancellationToken ct)
    {
        var result = new List<Found>();
        var masters = affectedMasters.Union(parentMasters).ToArray();
        if (masters.Length == 0) return result;

        var ancestors = new List<Guid>();
        var cursor = head.SourceId;
        while (cursor is { } id && ancestors.Count < 4)
        {
            ancestors.Add(id);
            cursor = (await ChangeStore.LoadByIdAsync(conn, tx, id, forUpdate: false, ct))?.SourceId;
        }
        var filter = AccessPolicy.BuildReadFilter(subject, "c");
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT c.change_type, c.number, c.title, COALESCE(ls.label, c.lifecycle_state), ao.object_key, ao.master_id, {MarkingColumns.Select("c")}
            FROM chg.affected_object ao
            JOIN chg.change c ON c.id = ao.change_id
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql} AND ao.removed_at IS NULL AND ao.master_id = ANY(@masters) AND c.id <> @self
              AND c.id <> ALL(@ancestors) AND c.source_id IS DISTINCT FROM @self AND NOT COALESCE(ls.is_final, false)
            ORDER BY c.number, ao.object_key
            """, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("masters", masters);
        cmd.Parameters.AddWithValue("self", head.Id);
        cmd.Parameters.AddWithValue("ancestors", ancestors.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var m = MarkingColumns.Read(r, 6);
            var same = affectedMasters.Contains(r.GetGuid(5));
            result.Add(new Found(new ImpactConflict(same ? "same object" : "uses an affected part", r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), m.BannerText()), m));
        }
        return result;
    }
}
