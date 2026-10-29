using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

public sealed partial class RequirementService
{
    /// <summary>The verification records of the given requirements, newest first, grouped by requirement (system view over requirements the caller already may read).</summary>
    private static async Task<Dictionary<Guid, List<(string Sha, string Result, DateTimeOffset At, bool Formal)>>> LoadVerificationRecordsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<Guid> requirementIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, List<(string, string, DateTimeOffset, bool)>>();
        if (requirementIds.Count == 0) return map;
        await using var cmd = new NpgsqlCommand(
            "SELECT v.requirement_id, v.requirement_sha256, v.result, v.performed_at, v.formal FROM req.verification v WHERE v.requirement_id = ANY(@ids) ORDER BY v.performed_at DESC, v.id DESC", conn, tx);
        cmd.Parameters.AddWithValue("ids", requirementIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            if (!map.TryGetValue(id, out var list)) map[id] = list = [];
            list.Add((r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4)));
        }
        return map;
    }

    private static async Task<SpecTrace> BuildTraceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SpecHead head, CancellationToken ct)
    {
        var raw = await ReadRowsAsync(conn, tx, subject, head.IterationId, ct);
        var rows = BuildRows(raw);
        var visible = raw.Where(r => r.Visible is not null).Select(r => r.Visible!).ToList();
        var byId = visible.ToDictionary(w => w.Id);
        var links = new List<SpecLink>();
        foreach (var l in await LoadLinksAsync(conn, tx, subject, [.. byId.Keys], false, ct))
        {
            var src = byId[l.SourceId];
            if (!l.Visible) { links.Add(new SpecLink(l.Id, src.Key, "", "", "", "", true, null, null, null, null, false, null, null, null)); continue; }
            var reason = l.EffectiveSuspect(src.Sha);
            links.Add(new SpecLink(l.Id, src.Key, l.TypeCode, l.Label, l.Role, l.TargetKind, false, l.OtherNumber, l.OtherKey, l.OtherName, l.OtherRevision, reason is not null, reason, l.OtherMarkings?.BannerText(), l.OtherMarkings));
        }
        var records = await LoadVerificationRecordsAsync(conn, tx, [.. byId.Keys], ct);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var last = new Dictionary<string, VerificationSummary>(StringComparer.Ordinal);
        foreach (var w in visible)
        {
            var recs = records.GetValueOrDefault(w.Id) ?? [];
            states[w.Key] = RequirementRules.VerificationState(w.Sha, [.. recs.Select(r => (r.Sha, r.Result))]);
            if (recs.Count > 0) last[w.Key] = new VerificationSummary(recs[0].Result, recs[0].At, recs[0].Formal);
        }
        var shown = rows.Where(r => r.Markings is not null).Select(r => r.Markings!).Concat(links.Where(l => l.Markings is not null).Select(l => l.Markings!)).Append(head.Markings);
        return new SpecTrace(head.Number, head.Revision, head.Iteration, rows, links, states, last, Markings.AggregateBanner(shown));
    }

    /// <summary>The readable requirements of a revision with the links whose other end is readable, restricted ends as placeholders, and the verification state of each requirement.</summary>
    public Task<Outcome<SpecTrace>> GetTraceAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<SpecTrace>(subject, number, revision, "requirement.trace", correlationId, async (conn, tx, head) =>
        {
            var trace = await BuildTraceAsync(conn, tx, subject, head, ct);
            return (Outcome<SpecTrace>.Ok(trace), [("revision", head.Label), ("requirements", Fmt(trace.Rows.Count(r => !r.Redacted))), ("links", Fmt(trace.Links.Count(l => !l.Redacted))), ("restrictedLinks", Fmt(trace.Links.Count(l => l.Redacted)))]);
        }, ct);

    /// <summary>
    /// Coverage of a revision (or, with <paramref name="baseline"/>, coverage within a baseline: an allocation counts when its part is in the baseline). Everything is counted over what the
    /// caller can read: a restricted requirement, or a link to a restricted part or document, is not counted, so no number reveals one.
    /// </summary>
    public Task<Outcome<SpecCoverage>> GetCoverageAsync(Subject subject, string number, string? revision, string? baseline, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<SpecCoverage>(subject, number, revision, "requirement.coverage", correlationId, async (conn, tx, head) =>
        {
            HashSet<string>? inBaseline = null;
            if (!string.IsNullOrWhiteSpace(baseline))
            {
                if (baselines is null) return (Outcome<SpecCoverage>.Invalid(["Coverage by baseline is not available."]), []);
                var parts = await baselines.ReadablePartsAsync(subject, baseline.Trim(), correlationId, ct);
                if (!parts.IsOk) return (Outcome<SpecCoverage>.Invalid([$"Baseline '{baseline}' was not found, or you are not authorized to see it."]), []);
                inBaseline = parts.Value!.Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
            }
            var trace = await BuildTraceAsync(conn, tx, subject, head, ct);
            var rows = new List<CoverageRow>();
            foreach (var r in trace.Rows.Where(r => !r.Redacted))
            {
                var mine = trace.Links.Where(l => l.SourceKey == r.Key && !l.Redacted).ToList();
                var allocations = mine.Count(l => l.CoverageRole == CoverageRoles.Allocation && (inBaseline is null || (l.TargetKind == LinkTargetKinds.Part && l.TargetNumber is { } n && inBaseline.Contains(n))));
                var planned = mine.Count(l => l.CoverageRole == CoverageRoles.Verification);
                var state = trace.Verification.GetValueOrDefault(r.Key!, VerificationStates.None);
                rows.Add(new CoverageRow(r.Key!, r.Outline, r.Title!, r.Status!, r.Method!, r.Priority!, allocations, planned, state, mine.Count(l => l.Suspect), ReqStatuses.InScope(r.Status!),
                    allocations == 0, state is not (VerificationStates.Verified or VerificationStates.Waived), r.Banner!, r.Markings!));
            }
            var summary = RequirementRules.Summarize(rows);
            var banner = Markings.AggregateBanner(rows.Select(r => r.Markings).Append(head.Markings));
            return (Outcome<SpecCoverage>.Ok(new SpecCoverage(head.Number, head.Revision, head.Iteration, inBaseline is null ? null : baseline!.Trim(), summary, rows, banner)),
                [("revision", head.Label), ("requirements", Fmt(rows.Count)), ("gaps", Fmt(summary.Gaps)), ("baseline", inBaseline is null ? null : baseline!.Trim())]);
        }, ct);

    /// <summary>The traceability matrix of one link type: requirements down, linked objects across (readable ones only; at most <see cref="MatrixColumnCap"/> columns).</summary>
    public const int MatrixColumnCap = 40;

    public async Task<Outcome<TraceMatrix>> GetMatrixAsync(Subject subject, string number, string linkType, string? revision, string? correlationId, CancellationToken ct = default)
    {
        var trace = await GetTraceAsync(subject, number, revision, correlationId, ct);
        if (!trace.IsOk) return Outcome<TraceMatrix>.NotFound();
        var types = await ListLinkTypesAsync(true, ct);
        var type = types.FirstOrDefault(t => t.Code == linkType);
        if (type is null) return Outcome<TraceMatrix>.Invalid([$"'{linkType}' is not a link type."]);
        return Outcome<TraceMatrix>.Ok(TraceMatrixBuilder.Build(trace.Value!, type));
    }

    // ---------------------------------------------------------------- the digital thread from the other side

    private static AuditEntry ObjectEntry(Subject s, string kind, string key, AuditOutcome outcome, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s), Action = "requirement.object_trace", Outcome = outcome, ObjectType = AwarenessKinds.AuditObjectType(kind, key) ?? kind, ObjectId = key, CorrelationId = correlationId, Details = AuditDetails.Of(details),
    };

    /// <summary>
    /// The requirements that trace to a part, document or change the caller can read, with link and verification state; a document also lists the requirements it is verification evidence for.
    /// A requirement the caller cannot read appears as a placeholder with no detail.
    /// </summary>
    public async Task<Outcome<ObjectTrace>> GetObjectTraceAsync(Subject subject, string kind, string key, string? correlationId, CancellationToken ct = default)
    {
        if (kind is not (LinkTargetKinds.Part or LinkTargetKinds.Document or LinkTargetKinds.Change)) return Outcome<ObjectTrace>.Invalid(["The object kind must be part, document or change."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var target = await LoadTargetAsync(conn, tx, kind, key, ct);
        var read = target is null ? null : AccessPolicy.Evaluate(subject, target.Markings, PlmAction.Read);
        if (target is null || !read!.Permitted)
        {
            if (target is not null) await audit.AppendAsync(tx, ObjectEntry(subject, kind, key, AuditOutcome.Denied, correlationId, ("reasons", string.Join(",", read!.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ObjectTrace>.NotFound();
        }
        var crFilter = AccessPolicy.BuildReadFilter(subject, "cr");
        var rows = new List<TracedRequirement>();
        var shown = new List<Markings> { target.Markings };
        var visibleIds = new List<(Guid Id, int Index)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT lt.code, lt.label, l.status, l.suspect_reason, l.reviewed_target_revision, l.reviewed_source_sha, sq.key, sp.number, cr.requirement_id, cr.title, cr.content_sha256, {MarkingColumns.Select("cr")}
            FROM req.link l
            JOIN req.link_type lt ON lt.code = l.link_type
            JOIN req.requirement sq ON sq.id = l.source_requirement_id
            JOIN req.spec sp ON sp.id = sq.spec_id
            LEFT JOIN req.current_requirement cr ON cr.requirement_id = l.source_requirement_id AND {crFilter.Sql}
            WHERE l.target_kind = @kind AND l.target_number = @key AND l.removed_at IS NULL
            ORDER BY sp.number, sq.key, lt.code
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("key", key);
            foreach (var p in crFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(8)) { rows.Add(new TracedRequirement(true, null, null, null, null, null, false, null, null, null)); continue; }
                var m = MarkingColumns.Read(r, 11);
                var reviewedRevision = r.IsDBNull(4) ? null : r.GetString(4);
                var suspect = r.GetString(2) == "suspect" || r.GetString(5) != r.GetString(10) || (kind != LinkTargetKinds.Change && target.Revision is not null && reviewedRevision != target.Revision);
                rows.Add(new TracedRequirement(false, r.GetString(7), r.GetString(6), r.GetString(9), r.GetString(0), r.GetString(1), suspect, null, m.BannerText(), m));
                visibleIds.Add((r.GetGuid(8), rows.Count - 1));
                shown.Add(m);
            }
        }
        if (kind == LinkTargetKinds.Document)
        {
            // Requirements this document is verification evidence for (a record, not a link): each requirement once, with the record's result.
            var linked = rows.Where(x => !x.Redacted).Select(x => (x.SpecNumber, x.Key)).ToHashSet();
            await using var cmd = new NpgsqlCommand(
                $"""
                SELECT DISTINCT ON (cr.requirement_id) cr.requirement_id, sp.number, sq.key, cr.title, v.result, {MarkingColumns.Select("cr")}
                FROM req.verification_evidence e
                JOIN req.verification v ON v.id = e.verification_id
                JOIN req.requirement sq ON sq.id = v.requirement_id
                JOIN req.spec sp ON sp.id = sq.spec_id
                LEFT JOIN req.current_requirement cr ON cr.requirement_id = v.requirement_id AND {crFilter.Sql}
                WHERE e.document_number = @key
                ORDER BY cr.requirement_id, v.performed_at DESC
                """, conn, tx);
            cmd.Parameters.AddWithValue("key", key);
            foreach (var p in crFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(0)) { rows.Add(new TracedRequirement(true, null, null, null, null, null, false, null, null, null)); continue; }
                var m = MarkingColumns.Read(r, 5);
                if (linked.Contains((r.GetString(1), r.GetString(2)))) continue;
                rows.Add(new TracedRequirement(false, r.GetString(1), r.GetString(2), r.GetString(3), "evidence", "Verification evidence", false, null, m.BannerText(), m));
                visibleIds.Add((r.GetGuid(0), rows.Count - 1));
                shown.Add(m);
            }
        }
        // Verification state of every readable requirement shown (system view over requirements already read through the filter).
        var recs = await LoadVerificationRecordsAsync(conn, tx, [.. visibleIds.Select(v => v.Id).Distinct()], ct);
        if (recs.Count > 0 || visibleIds.Count > 0)
        {
            var shas = new Dictionary<Guid, string>();
            await using (var cmd = new NpgsqlCommand("SELECT cr.requirement_id, cr.content_sha256 FROM req.current_requirement cr WHERE cr.requirement_id = ANY(@ids)", conn, tx))
            {
                cmd.Parameters.AddWithValue("ids", visibleIds.Select(v => v.Id).Distinct().ToArray());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) shas[r.GetGuid(0)] = r.GetString(1);
            }
            foreach (var (id, index) in visibleIds)
            {
                var state = RequirementRules.VerificationState(shas.GetValueOrDefault(id, ""), [.. (recs.GetValueOrDefault(id) ?? []).Select(x => (x.Sha, x.Result))]);
                rows[index] = rows[index] with { VerificationStatus = state };
            }
        }
        await audit.AppendAsync(tx, ObjectEntry(subject, kind, key, AuditOutcome.Success, correlationId, ("requirements", Fmt(rows.Count(r => !r.Redacted))), ("restricted", Fmt(rows.Count(r => r.Redacted)))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ObjectTrace>.Ok(new ObjectTrace(kind, key, target.Name, rows, Markings.AggregateBanner(shown)));
    }
}

/// <summary>Pure construction of the traceability matrix from a <see cref="SpecTrace"/>.</summary>
public static class TraceMatrixBuilder
{
    public static TraceMatrix Build(SpecTrace trace, LinkTypeInfo type)
    {
        var rows = trace.Rows.Where(r => !r.Redacted).ToList();
        var links = trace.Links.Where(l => !l.Redacted && l.LinkType == type.Code).ToList();
        string Id(SpecLink l) => l.TargetKind == LinkTargetKinds.Requirement ? $"{l.TargetNumber}/{l.TargetKey}" : $"{l.TargetKind}:{l.TargetNumber}";
        var columns = links.GroupBy(Id).Select(g => (Id: g.Key, Count: g.Count(), First: g.First()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
        var truncated = columns.Count > RequirementService.MatrixColumnCap;
        var kept = columns.Take(RequirementService.MatrixColumnCap).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var keptIds = kept.Select(c => c.Id).ToHashSet();
        string? Href(SpecLink l) => l.TargetKind switch
        {
            LinkTargetKinds.Part => "parts/" + Uri.EscapeDataString(l.TargetNumber!),
            LinkTargetKinds.Document => "documents/" + Uri.EscapeDataString(l.TargetNumber!),
            LinkTargetKinds.Change => "changes/" + Uri.EscapeDataString(l.TargetNumber!),
            _ => "requirements/" + Uri.EscapeDataString(l.TargetNumber!),
        };
        var cols = kept.Select(c => new MatrixColumn(c.Id, c.First.TargetKind, c.First.TargetKind == LinkTargetKinds.Requirement ? $"{c.First.TargetNumber} {c.First.TargetKey}" : c.First.TargetNumber!, Href(c.First))).ToList();
        var cells = links.Where(l => keptIds.Contains(Id(l))).Select(l => new MatrixCell(l.SourceKey, Id(l), l.Id, l.Suspect)).ToList();
        var banner = Markings.AggregateBanner(rows.Select(r => r.Markings!).Concat(links.Where(l => l.Markings is not null && keptIds.Contains(Id(l))).Select(l => l.Markings!)));
        return new TraceMatrix(type.Code, type.Label, rows, cols, cells, truncated, banner);
    }
}
