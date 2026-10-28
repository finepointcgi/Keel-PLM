using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Structures from baselines and comparisons (ADR-0021). A baseline's items are read like an explosion: through the caller's read filter,
/// with restricted items shown as placeholders (quantity, unit, find number) and never expanded. Comparisons reduce to flat line lists and share
/// <see cref="BomDeepDiff"/>.
/// </summary>
public sealed partial class BomService
{
    private sealed record BaselineView(BaselineHead Head, ExplodeData Data, string RootNumber, string RootName, string RootRevision, int RootIteration);

    // ---------------------------------------------------------------- baseline structure

    /// <summary>
    /// Items of a baseline through the read filter. The root (seq 0) comes back as the header. Items below a restricted item are dropped (the
    /// subtree of a line the caller cannot read is never shown), exactly like the live explosion.
    /// </summary>
    private static async Task<BaselineView> ReadBaselineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, BaselineHead head, int? depth, string? parentPath, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var cache = new MarkingCache();
        var lines = new List<BomLine>(1024);
        var identities = new List<LineIdentity?>(1024);
        var hidden = new HashSet<int>();
        var parents = new HashSet<int>();
        int redacted = 0;
        string rootNumber = "Restricted item", rootName = "Restricted item", rootRevision = "";
        var rootIteration = 0;
        var prefix = string.IsNullOrEmpty(parentPath) ? null : parentPath + ".";
        var parentLevel = string.IsNullOrEmpty(parentPath) ? 0 : parentPath.Count(c => c == '.') + 1;

        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT i.seq, i.parent_seq, i.level, i.path, i.find_number, i.quantity, i.unit, i.ext_quantity,
                   cm.id, cm.number, cm.name, cm.part_type, r.revision, it.iteration, r.lifecycle_state, ls.label,
                   i.reference_designators, i.notes, {MarkingColumns.Select("cm")}, i.eff_kind, i.eff_context, i.eff_from, i.eff_to,
                   i.master_id, i.revision_id, i.iteration_id
            FROM cfg.baseline_item i
            LEFT JOIN plm.part_master cm ON cm.id = i.master_id AND {filter.Sql}
            LEFT JOIN plm.part_revision r ON r.id = i.revision_id AND cm.id IS NOT NULL
            LEFT JOIN plm.part_iteration it ON it.id = i.iteration_id AND cm.id IS NOT NULL
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = cm.lifecycle_id AND ls.state = r.lifecycle_state
            WHERE i.baseline_id = @b
            ORDER BY i.seq
            """, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("b", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var seq = r.GetInt32(0);
            var readable = !r.IsDBNull(8);
            if (seq == 0)
            {
                if (readable)
                {
                    (rootNumber, rootName, rootRevision, rootIteration) = (r.GetString(9), r.GetString(10), r.GetString(12), r.GetInt32(13));
                    cache.Get(r, r.GetGuid(8), 18);
                }
                else hidden.Add(0);
                continue;
            }
            var parentSeq = r.GetInt32(1);
            if (hidden.Contains(parentSeq)) { hidden.Add(seq); continue; } // below a restricted item: never shown
            if (!readable) hidden.Add(seq); // shown as a placeholder, but its own children are not
            parents.Add(parentSeq);
            var level = r.GetInt32(2);
            var path = r.GetString(3);
            if (prefix is not null && !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (depth is { } d && level > parentLevel + d) continue;

            Markings? markings = null;
            string? banner = null;
            if (readable) (markings, banner) = cache.Get(r, r.GetGuid(8), 18);
            else redacted++;
            var eff = EffectivityStore.ReadRange(r, 28);
            identities.Add(new LineIdentity(r.GetGuid(32), r.GetGuid(33), r.GetGuid(34)));
            lines.Add(new BomLine(
                LineGuid(head.Id, seq), r.IsDBNull(1) ? null : LineGuid(head.Id, parentSeq), level, path, r.GetInt32(4), r.GetDecimal(5), r.GetString(6), r.GetDecimal(7),
                !readable, false, false, false, readable ? r.GetGuid(8) : null, readable ? r.GetString(9) : null, readable ? r.GetString(10) : null,
                readable ? r.GetString(11) : null, readable ? r.GetString(12) : null, readable ? r.GetInt32(13) : null, readable ? r.GetString(14) : null,
                readable && !r.IsDBNull(15) ? r.GetString(15) : null, readable ? r.GetFieldValue<string[]>(16) : [], readable && !r.IsDBNull(17) ? r.GetString(17) : null,
                banner, markings, eff));
        }
        // HasChildren: a readable line with stored children (their own readability is decided when they are shown).
        var withChildren = new List<BomLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var seq = SeqOf(l.LineId);
            withChildren.Add(!l.Redacted && parents.Contains(seq) ? l with { HasChildren = true } : l);
        }
        var banner2 = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
        return new BaselineView(head, new ExplodeData(withChildren, banner2, redacted, 0, identities), rootNumber, rootName, rootRevision, rootIteration);
    }

    /// <summary>A stable id for an item: its sequence number in the first four bytes, the baseline's id in the rest.</summary>
    private static Guid LineGuid(Guid baseline, int seq)
    {
        var bytes = baseline.ToByteArray();
        BitConverter.GetBytes(seq).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static int SeqOf(Guid lineId) => BitConverter.ToInt32(lineId.ToByteArray(), 0);

    private async Task<Outcome<T>> WithBaselineAsync<T>(
        Subject subject, string? number, PlmAction permission, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, BaselineHead, Task<(Outcome<T> Result, (string, string?)[] Details)>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = string.IsNullOrWhiteSpace(number) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, number.Trim(), false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!read.Permitted || !decision.Permitted)
        {
            await audit.AppendAsync(tx, BaselineService.Entry(subject, action, AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var (result, details) = await body(conn, tx, head);
        await audit.AppendAsync(tx, BaselineService.Entry(subject, action, result.IsOk ? AuditOutcome.Success : AuditOutcome.Failure, head.Number, correlationId,
            result.IsOk ? details : [("error", string.Join(" ", result.Errors)), ("kind", result.Kind.ToString())]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static (string, string?)[] BaselineDetails(BaselineHead head, ExplodeData data, params (string, string?)[] extra) =>
    [
        ("baseline", head.Number), ("lines", data.Lines.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", data.Redacted.ToString(CultureInfo.InvariantCulture)),
        ("banner", data.Banner), .. extra,
    ];

    private Task<Outcome<BomExplosion>> ExplodeBaselineAsync(Subject subject, BomQuery query, string? correlationId, CancellationToken ct)
    {
        var errors = ValidateQuery(query, out _, out _);
        if (errors.Count > 0) return Task.FromResult(Outcome<BomExplosion>.Invalid(errors));
        return WithBaselineAsync<BomExplosion>(subject, query.Baseline, PlmAction.Read, "baseline.explode", correlationId, async (conn, tx, head) =>
        {
            var view = await ReadBaselineAsync(conn, tx, subject, head, query.Depth, query.ParentLine, ct);
            var result = new BomExplosion(view.RootNumber, view.RootName, view.RootRevision, view.RootIteration, BomConfigSpec.Baseline, query.Depth, query.ParentLine,
                view.Data.Lines, view.Data.Banner, view.Data.Redacted, 0, ConfigurationLabel(query));
            return (Outcome<BomExplosion>.Ok(result), BaselineDetails(head, view.Data, ("depth", query.Depth?.ToString(CultureInfo.InvariantCulture) ?? "full"), ("parentLine", query.ParentLine)));
        }, ct);
    }

    private Task<Outcome<BomExport>> ExportBaselineCsvAsync(Subject subject, BomQuery query, string? correlationId, CancellationToken ct)
    {
        var errors = ValidateQuery(query with { ParentLine = null }, out _, out _);
        if (errors.Count > 0) return Task.FromResult(Outcome<BomExport>.Invalid(errors));
        return WithBaselineAsync<BomExport>(subject, query.Baseline, PlmAction.Download, "baseline.export", correlationId, async (conn, tx, head) =>
        {
            var view = await ReadBaselineAsync(conn, tx, subject, head, query.Depth, null, ct);
            var csv = BomCsv.Render(view.Data.Banner, view.Data.Lines);
            return (Outcome<BomExport>.Ok(new BomExport($"{head.Number}_bom.csv", csv, view.Data.Banner, view.Data.Lines.Count, view.Data.Redacted)),
                BaselineDetails(head, view.Data, ("format", "csv")));
        }, ct);
    }

    private Task<Outcome<BomSummary>> SummaryBaselineAsync(Subject subject, BomQuery query, bool leavesOnly, string? correlationId, CancellationToken ct)
    {
        var errors = ValidateQuery(query with { ParentLine = null }, out _, out _);
        if (errors.Count > 0) return Task.FromResult(Outcome<BomSummary>.Invalid(errors));
        return WithBaselineAsync<BomSummary>(subject, query.Baseline, PlmAction.Read, "baseline.summary", correlationId, async (conn, tx, head) =>
        {
            var view = await ReadBaselineAsync(conn, tx, subject, head, query.Depth, null, ct);
            var summary = SummarizeCore(view.RootNumber, $"{view.RootRevision}.{view.RootIteration}", BomConfigSpec.Baseline, leavesOnly, view.Data);
            return (Outcome<BomSummary>.Ok(summary), BaselineDetails(head, view.Data, ("leavesOnly", leavesOnly.ToString())));
        }, ct);
    }

    // ---------------------------------------------------------------- comparisons

    private static Outcome<DeepComparison> Compared(string title, string fromLabel, string toLabel, ExplodeData a, ExplodeData b, IEnumerable<Markings> extraMarkings, out (string, string?)[] details)
    {
        var (lines, unchanged) = BomDeepDiff.Compute(a.Lines, a.Identities, b.Lines, b.Identities);
        var markings = a.Lines.Concat(b.Lines).Where(l => l.Markings is not null).Select(l => l.Markings!).Distinct().Concat(extraMarkings);
        var banner = Markings.AggregateBanner(markings);
        var result = BomDeepDiff.Build(title, fromLabel, toLabel, lines, unchanged, banner);
        details = [("from", fromLabel), ("to", toLabel), ("added", Fmt(result.Added)), ("removed", Fmt(result.Removed)), ("changed", Fmt(result.Changed)), ("unchanged", Fmt(unchanged)), ("banner", banner)];
        return Outcome<DeepComparison>.Ok(result);
    }

    /// <summary>Baseline against baseline. The caller needs Read on both; restricted items are placeholders on either side.</summary>
    public async Task<Outcome<DeepComparison>> CompareBaselinesAsync(Subject subject, string from, string to, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var a = string.IsNullOrWhiteSpace(from) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, from.Trim(), false, ct);
        var b = string.IsNullOrWhiteSpace(to) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, to.Trim(), false, ct);
        if (a is null || b is null) return Outcome<DeepComparison>.NotFound();
        foreach (var h in new[] { a, b })
        {
            var read = AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Read);
            if (read.Permitted) continue;
            await audit.AppendAsync(tx, BaselineService.Entry(subject, "baseline.compare", AuditOutcome.Denied, h.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<DeepComparison>.NotFound();
        }
        var va = await ReadBaselineAsync(conn, tx, subject, a, null, null, ct);
        var vb = await ReadBaselineAsync(conn, tx, subject, b, null, null, ct);
        var result = Compared("Baseline comparison", a.Number, b.Number, va.Data, vb.Data, [a.Markings, b.Markings], out var details);
        await audit.AppendAsync(tx, BaselineService.Entry(subject, "baseline.compare", AuditOutcome.Success, a.Number, correlationId, [("other", b.Number), .. details]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>A baseline against the current structure of its root part under Latest or LatestReleased (what changed since the baseline).</summary>
    public async Task<Outcome<DeepComparison>> CompareBaselineToCurrentAsync(Subject subject, string baseline, BomConfigSpec spec, string? correlationId, CancellationToken ct = default)
    {
        if (spec is not (BomConfigSpec.Latest or BomConfigSpec.LatestReleased))
            return Outcome<DeepComparison>.Invalid(["A baseline is compared with the current structure under Latest or LatestReleased."]);
        return await WithBaselineAsync<DeepComparison>(subject, baseline, PlmAction.Read, "baseline.compare_current", correlationId, async (conn, tx, head) =>
        {
            var view = await ReadBaselineAsync(conn, tx, subject, head, null, null, ct);
            var rootRevision = await CurrentRootRevisionAsync(conn, tx, head.RootMasterId, spec, ct);
            var rootNumber = await RootNumberAsync(conn, tx, subject, head.RootMasterId, ct);
            if (rootNumber is null) return (Outcome<DeepComparison>.Invalid(["The root part of this baseline is no longer visible to you; it cannot be compared with the current structure."]), []);
            if (rootRevision is null) return (Outcome<DeepComparison>.Invalid([$"{rootNumber} has no released revision to compare with."]), []);
            var root = await LoadRootAsync(conn, tx, rootNumber, rootRevision, null, ct);
            if (root is null) return (Outcome<DeepComparison>.NotFound(), []);
            if (!AccessPolicy.Evaluate(subject, root.Markings, PlmAction.Read).Permitted)
                return (Outcome<DeepComparison>.Invalid(["The root part of this baseline is no longer visible to you; it cannot be compared with the current structure."]), []);
            var current = await ExplodeCoreAsync(conn, tx, subject, root, spec, null, Seed.ForRoot(root), null, ct);
            if (current is null) return (TooLarge<DeepComparison>(), []);
            var title = spec == BomConfigSpec.Latest ? "Baseline against the latest structure" : "Baseline against the latest released structure";
            var result = Compared(title, head.Number, $"{root.Number} {root.Revision}.{root.Iteration} ({(spec == BomConfigSpec.Latest ? "latest" : "latest released")})",
                view.Data, current, [head.Markings, root.Markings], out var details);
            return (result, [("baseline", head.Number), ("against", spec.ToString()), .. details]);
        }, ct);
    }

    /// <summary>One unit against another (what differs between serial 100 and 101). Both structures use the caller's read filter.</summary>
    public async Task<Outcome<DeepComparison>> CompareUnitsAsync(Subject subject, string number, EffectivityRequest a, EffectivityRequest b, int? depth, string? correlationId, CancellationToken ct = default)
    {
        var errors = a.Validate().Concat(b.Validate()).ToList();
        if (depth is { } d && (d < 1 || d > BomLimits.MaxDepth)) errors.Add($"Depth must be between 1 and {BomLimits.MaxDepth}.");
        if (errors.Count > 0) return Outcome<DeepComparison>.Invalid(errors);
        return await WithRootAsync<DeepComparison>(subject, new BomQuery(number), PlmAction.Read, "bom.compare_units", correlationId, async (conn, tx, root0) =>
        {
            var sides = new List<(Root Root, ExplodeData Data)>();
            foreach (var unit in new[] { a, b })
            {
                var revision = await ResolveRootRevisionAsync(conn, tx, root0.MasterId, unit, ct);
                var root = revision is null ? root0 : await LoadRootAsync(conn, tx, root0.Number, revision, null, ct) ?? root0;
                if (!AccessPolicy.Evaluate(subject, root.Markings, PlmAction.Read).Permitted) return (Outcome<DeepComparison>.NotFound(), []);
                var data = await ExplodeCoreAsync(conn, tx, subject, root, BomConfigSpec.Effectivity, depth, Seed.ForRoot(root), unit, ct);
                if (data is null) return (TooLarge<DeepComparison>(), []);
                sides.Add((root, data));
            }
            var result = Compared($"{root0.Number}: one unit against another",
                $"{a.Describe()} ({sides[0].Root.Revision}.{sides[0].Root.Iteration})", $"{b.Describe()} ({sides[1].Root.Revision}.{sides[1].Root.Iteration})",
                sides[0].Data, sides[1].Data, [sides[0].Root.Markings, sides[1].Root.Markings], out var details);
            return (result, [("part", root0.Number), .. details]);
        }, ct);
    }

    private static async Task<string?> CurrentRootRevisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, BomConfigSpec spec, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(spec == BomConfigSpec.Latest
            ? "SELECT revision FROM plm.part_revision WHERE master_id = @m AND is_latest"
            : "SELECT revision FROM plm.part_revision WHERE master_id = @m AND lifecycle_state = 'Released' ORDER BY length(revision) DESC, revision DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<string?> RootNumberAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid master, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        await using var cmd = new NpgsqlCommand($"SELECT cm.number FROM plm.part_master cm WHERE cm.id = @m AND {filter.Sql}", conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("m", master);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>CSV of a comparison; same banner rules as every export.</summary>
    public static DeepComparisonExport ExportComparison(DeepComparison comparison, string fileStem) =>
        new($"{FileSafe(fileStem)}_comparison.csv", DeepDiffCsv.Render(comparison), comparison.Banner, comparison.Lines.Count);
}
