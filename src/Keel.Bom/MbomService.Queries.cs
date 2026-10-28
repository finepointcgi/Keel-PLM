using System.Globalization;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>CSV rendering of an exploded MBOM. The aggregate marking banner is the first and the last line (ADR-0004); cells are neutralized against formula injection.</summary>
public static class MbomCsv
{
    public static string Render(string banner, IEnumerable<MbomLine> lines)
    {
        var sb = new StringBuilder(1 << 20);
        sb.Append(BomCsv.Cell(banner)).Append("\r\n");
        sb.Append("Level,Path,Find number,Part number,Name,Source,Supply type,Phantom,Quantity,Unit,Scrap factor,Extended net quantity,Extended gross quantity,Operation reference,MBOM only,Reference designators,Child MBOM,Markings,Effectivity,Flags\r\n");
        foreach (var l in lines)
        {
            sb.Append(l.Level.ToString(CultureInfo.InvariantCulture)).Append(',').Append(BomCsv.Cell(l.Path)).Append(',').Append(l.FindNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(BomCsv.Cell(l.Redacted ? BomCsv.RestrictedLabel : l.Number)).Append(',').Append(BomCsv.Cell(l.Name)).Append(',').Append(BomCsv.Cell(l.Source)).Append(',')
              .Append(BomCsv.Cell(l.SupplyType)).Append(',').Append(l.Redacted ? "" : l.Phantom ? "yes" : "no").Append(',').Append(MbomText.Qty(l.Quantity)).Append(',').Append(BomCsv.Cell(l.Unit)).Append(',')
              .Append(l.ScrapFactor is { } s ? MbomText.Qty(s) : "").Append(',').Append(MbomText.Qty(l.ExtendedQuantity)).Append(',').Append(MbomText.Qty(l.GrossQuantity)).Append(',')
              .Append(BomCsv.Cell(l.OperationReference)).Append(',').Append(l.Redacted ? "" : l.MbomOnly ? "yes" : "no").Append(',').Append(BomCsv.Cell(string.Join(' ', l.ReferenceDesignators))).Append(',')
              .Append(BomCsv.Cell(l.ChildMbomRevision is null ? null : $"{l.ChildMbomRevision}.{l.ChildMbomIteration} {l.ChildMbomStateLabel}")).Append(',').Append(BomCsv.Cell(l.Banner)).Append(',')
              .Append(BomCsv.Cell(l.EffectivityText)).Append(',').Append(BomCsv.Cell(l.Redacted ? "restricted" : l.Circular ? "circular reference" : "")).Append("\r\n");
        }
        sb.Append(BomCsv.Cell(banner)).Append("\r\n");
        return sb.ToString();
    }
}

public sealed partial class MbomService
{
    internal sealed record MbomSeed(Guid IterationId, int Level, int[] Path, int[] Alts, string PathText, decimal Extended, decimal Gross, IReadOnlyList<Guid> Ancestors);

    internal sealed record MbomExplodeData(IReadOnlyList<MbomLine> Lines, string Banner, int Redacted, int Circular);

    private static List<string> ValidateQuery(MbomQuery q, out int[]? path, out int[]? alts)
    {
        var errors = new List<string>();
        if (q.Depth is { } d && (d < 1 || d > BomLimits.MaxDepth)) errors.Add($"Depth must be between 1 and {BomLimits.MaxDepth}.");
        if (q.Iteration is { } it && it < 1) errors.Add("Iteration must be 1 or greater.");
        if (BomRules.TryParsePath(q.ParentLine, out var finds, out var altList)) (path, alts) = (finds, altList);
        else
        {
            (path, alts) = (null, null);
            errors.Add("Parent line must be a find-number path such as 10.20 (an alternate is written 30~1).");
        }
        if (q.Unit is { } u) errors.AddRange(u.Validate());
        return errors;
    }

    private static async Task<(Guid IterationId, int Iteration)?> ResolveIterationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, int? iteration, CancellationToken ct)
    {
        if (iteration is not { } it || it == head.LatestIteration) return (head.IterationId, head.LatestIteration);
        await using var cmd = new NpgsqlCommand("SELECT i.id FROM mbom.iteration i WHERE i.revision_id = @r AND i.iteration = @it", conn, tx);
        cmd.Parameters.AddWithValue("r", head.RevisionId);
        cmd.Parameters.AddWithValue("it", it);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? (id, it) : null;
    }

    private static async Task<MbomSeed?> ResolveSeedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, MbomHead head, Guid iteration, MbomQuery q, int[] path, int[] alts, CancellationToken ct)
    {
        var current = iteration;
        decimal ext = 1m, gross = 1m;
        var ancestors = new List<Guid> { head.PartMasterId };
        var partFilter = AccessPolicy.BuildReadFilter(subject, "cm");
        var mbomFilter = AccessPolicy.BuildReadFilter(subject, "mm");
        for (var hop = 0; hop < path.Length; hop++)
        {
            await using var cmd = new NpgsqlCommand(MbomSql.Hop(q.LatestChildren, q.Unit is not null, partFilter, mbomFilter), conn, tx);
            foreach (var (k, v) in partFilter.Parameters.Concat(mbomFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("parent", current);
            cmd.Parameters.AddWithValue("find", path[hop]);
            cmd.Parameters.AddWithValue("alt", alts[hop]);
            cmd.Parameters.AddWithValue("plant", head.Plant);
            BomService.BindEffectivity(cmd, q.Unit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.IsDBNull(3)) return null;
            var child = r.GetGuid(0);
            if (ancestors.Contains(child)) return null;
            ancestors.Add(child);
            var qty = r.GetDecimal(1);
            ext = decimal.Round(Math.Min(ext * qty, 1e18m), 6);
            gross = decimal.Round(Math.Min(gross * qty * (1m + (r.IsDBNull(2) ? 0m : r.GetDecimal(2))), 1e18m), 6);
            current = r.GetGuid(3);
        }
        return new MbomSeed(current, path.Length, path, alts, q.ParentLine ?? "", ext, gross, ancestors);
    }

    internal static async Task<MbomExplodeData?> ExplodeCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, MbomHead head, MbomQuery q, MbomSeed seed, CancellationToken ct)
    {
        var partFilter = AccessPolicy.BuildReadFilter(subject, "cm");
        var mbomFilter = AccessPolicy.BuildReadFilter(subject, "mm");
        var lines = new List<MbomLine>(1024);
        var cache = new MarkingCache();
        int redacted = 0, circular = 0;
        await using var cmd = new NpgsqlCommand(MbomSql.Explode(q.LatestChildren, q.Unit is not null, partFilter, mbomFilter), conn, tx);
        foreach (var (k, v) in partFilter.Parameters.Concat(mbomFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("base_level", seed.Level);
        cmd.Parameters.AddWithValue("base_path", seed.Path);
        cmd.Parameters.AddWithValue("base_alts", seed.Alts);
        cmd.Parameters.AddWithValue("base_tpath", seed.PathText);
        BomService.BindEffectivity(cmd, q.Unit);
        cmd.Parameters.AddWithValue("base_ext", seed.Extended);
        cmd.Parameters.AddWithValue("base_gross", seed.Gross);
        cmd.Parameters.AddWithValue("seed_iter", seed.IterationId);
        cmd.Parameters.AddWithValue("base_anc", seed.Ancestors.ToArray());
        cmd.Parameters.AddWithValue("plant", head.Plant);
        cmd.Parameters.AddWithValue("max_level", q.Depth is { } d ? seed.Level + d : 10_000);
        cmd.Parameters.AddWithValue("limit", MbomLimits.MaxExplosionLines + 1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var readable = r.GetBoolean(9);
            var cyc = r.GetBoolean(10);
            Guid? master = r.IsDBNull(13) ? null : r.GetGuid(13);
            var isRedacted = !readable || master is null;
            string? banner = null;
            Markings? markings = null;
            if (!isRedacted) (markings, banner) = cache.Get(r, master!.Value, 28);
            if (isRedacted) redacted++;
            else if (cyc) circular++;
            string? S(int i) => isRedacted || r.IsDBNull(i) ? null : r.GetString(i);
            lines.Add(new MbomLine(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt32(8), r.GetDecimal(6), r.GetString(7), r.GetDecimal(4), r.GetDecimal(5),
                isRedacted, !isRedacted && cyc, !isRedacted && r.GetBoolean(11), !isRedacted && !r.IsDBNull(12) && r.GetBoolean(12), master, S(14), S(15), S(16), S(17),
                isRedacted || r.IsDBNull(18) ? null : r.GetDecimal(18), S(19), S(20), !isRedacted && !r.IsDBNull(21) && r.GetBoolean(21),
                isRedacted || r.IsDBNull(22) ? [] : r.GetFieldValue<string[]>(22), S(23), S(24), isRedacted || r.IsDBNull(25) ? null : r.GetInt32(25), S(26), S(27), banner, markings,
                EffectivityStore.ReadRange(r, 28 + MarkingColumns.Count)));
            if (lines.Count > MbomLimits.MaxExplosionLines) return null;
        }
        return new MbomExplodeData(lines, Markings.AggregateBanner(cache.Distinct.Append(head.Markings)), redacted, circular);
    }

    private static Outcome<T> TooLarge<T>() =>
        Outcome<T>.Invalid([$"The structure has more than {MbomLimits.MaxExplosionLines:N0} lines; request a depth limit or expand one line at a time."]);

    private static string? Configuration(MbomQuery q) => q.Unit is { } u ? u.Describe() : null;

    /// <summary>Multi-level explosion per plant (or one lazily expanded line): same filter and redaction as the EBOM, children resolved to the MBOMs of the same plant.</summary>
    public Task<Outcome<MbomExplosion>> ExplodeAsync(Subject subject, MbomQuery query, string? correlationId, CancellationToken ct = default)
    {
        var errors = ValidateQuery(query, out var path, out var alts);
        if (errors.Count > 0) return Task.FromResult(Outcome<MbomExplosion>.Invalid(errors));
        return ReadAsync<MbomExplosion>(subject, query.Number, query.Revision, "mbom.explode", correlationId, async (conn, tx, head) =>
        {
            var resolved = await ResolveIterationAsync(conn, tx, head, query.Iteration, ct);
            if (resolved is null) return (Outcome<MbomExplosion>.NotFound(), []);
            var seed = await ResolveSeedAsync(conn, tx, subject, head, resolved.Value.IterationId, query, path!, alts!, ct);
            if (seed is null) return (Outcome<MbomExplosion>.NotFound(), []);
            var data = await ExplodeCoreAsync(conn, tx, subject, head, query, seed, ct);
            if (data is null) return (TooLarge<MbomExplosion>(), []);
            var result = new MbomExplosion(head.Number, head.PartNumber, head.PartName, head.Plant, head.Revision, resolved.Value.Iteration, query.Depth, query.ParentLine, data.Lines, data.Banner, data.Redacted, Configuration(query));
            return (Outcome<MbomExplosion>.Ok(result), Details(head, query, data));
        }, ct);
    }

    private static (string, string?)[] Details(MbomHead head, MbomQuery q, MbomExplodeData data, params (string, string?)[] extra) =>
    [
        ("mbom", head.Number), ("plant", head.Plant), ("revision", head.Label), ("depth", q.Depth?.ToString(CultureInfo.InvariantCulture) ?? "full"), ("parentLine", q.ParentLine),
        ("lines", Fmt(data.Lines.Count)), ("redacted", Fmt(data.Redacted)), ("unit", Configuration(q)), ("banner", data.Banner), .. extra,
    ];

    /// <summary>CSV export of the full explosion (audited as a download). Same filter and redaction as the screen.</summary>
    public Task<Outcome<MbomExport>> ExportCsvAsync(Subject subject, MbomQuery query, string? correlationId, CancellationToken ct = default)
    {
        var errors = ValidateQuery(query with { ParentLine = null }, out _, out _);
        if (errors.Count > 0) return Task.FromResult(Outcome<MbomExport>.Invalid(errors));
        return ReadAsync<MbomExport>(subject, query.Number, query.Revision, "mbom.export", correlationId, async (conn, tx, head) =>
        {
            var resolved = await ResolveIterationAsync(conn, tx, head, query.Iteration, ct);
            if (resolved is null) return (Outcome<MbomExport>.NotFound(), []);
            var data = await ExplodeCoreAsync(conn, tx, subject, head, query, new MbomSeed(resolved.Value.IterationId, 0, [], [], "", 1m, 1m, [head.PartMasterId]), ct);
            if (data is null) return (TooLarge<MbomExport>(), []);
            var csv = MbomCsv.Render(data.Banner, data.Lines);
            var file = $"{head.Number}_{head.Plant}_{head.Revision}.{resolved.Value.Iteration}_mbom.csv";
            return (Outcome<MbomExport>.Ok(new MbomExport(file, csv, data.Banner, data.Lines.Count, data.Redacted)), Details(head, query, data, ("format", "csv")));
        }, ct, PlmAction.Download);
    }

    /// <summary>Purchased and consumed quantities per distinct part, unit and supply type, net and with planned scrap (compounded through the levels). Restricted lines are never merged.</summary>
    public Task<Outcome<MbomSummaryResult>> SummaryAsync(Subject subject, MbomQuery query, bool leavesOnly, string? correlationId, CancellationToken ct = default)
    {
        var errors = ValidateQuery(query with { ParentLine = null }, out _, out _);
        if (errors.Count > 0) return Task.FromResult(Outcome<MbomSummaryResult>.Invalid(errors));
        return ReadAsync<MbomSummaryResult>(subject, query.Number, query.Revision, "mbom.summary", correlationId, async (conn, tx, head) =>
        {
            var resolved = await ResolveIterationAsync(conn, tx, head, query.Iteration, ct);
            if (resolved is null) return (Outcome<MbomSummaryResult>.NotFound(), []);
            var data = await ExplodeCoreAsync(conn, tx, subject, head, query, new MbomSeed(resolved.Value.IterationId, 0, [], [], "", 1m, 1m, [head.PartMasterId]), ct);
            if (data is null) return (TooLarge<MbomSummaryResult>(), []);
            var summary = SummarizeCore(head, resolved.Value.Iteration, leavesOnly, data);
            return (Outcome<MbomSummaryResult>.Ok(summary), Details(head, query, data, ("leavesOnly", leavesOnly.ToString()), ("distinctParts", Fmt(summary.Lines.Count))));
        }, ct);
    }

    internal static MbomSummaryResult SummarizeCore(MbomHead head, int iteration, bool leavesOnly, MbomExplodeData data)
    {
        var candidates = data.Lines.Where(l => !leavesOnly || !l.HasChildren).ToList();
        var lines = new List<MbomSummaryLine>();
        foreach (var g in candidates.Where(l => !l.Redacted).GroupBy(l => (l.MasterId, l.Unit, l.SupplyType)))
        {
            var first = g.First();
            lines.Add(new MbomSummaryLine(false, first.Number, first.Name, g.Key.SupplyType, g.Key.Unit, g.Sum(l => l.ExtendedQuantity), g.Sum(l => l.GrossQuantity), g.Count(), null, first.Banner, first.Markings));
        }
        lines = [.. lines.OrderBy(l => l.Number, StringComparer.Ordinal).ThenBy(l => l.Unit, StringComparer.Ordinal)];
        lines.AddRange(candidates.Where(l => l.Redacted).Select(l => new MbomSummaryLine(true, null, null, null, l.Unit, l.ExtendedQuantity, l.GrossQuantity, 1, l.Path, null, null)));
        return new MbomSummaryResult(head.Number, head.Plant, $"{head.Revision}.{iteration}", leavesOnly, lines, data.Banner);
    }

    // ---------------------------------------------------------------- where-used by plant

    /// <summary>The MBOMs whose latest iteration uses a part, optionally for one plant. An MBOM the caller may not read is a placeholder (find number, quantity, unit).</summary>
    public async Task<Outcome<MbomWhereUsedResult>> WhereUsedAsync(Subject subject, string partNumber, string? plant, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await MbomStore.LoadPartByNumberAsync(conn, tx, partNumber, ct);
        if (part is null) return Outcome<MbomWhereUsedResult>.NotFound();
        if (!AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "mbom.where_used", AuditOutcome.Denied, partNumber, correlationId, ("reasons", "read")), ct);
            await tx.CommitAsync(ct);
            return Outcome<MbomWhereUsedResult>.NotFound();
        }
        var mbomFilter = AccessPolicy.BuildReadFilter(subject, "mm");
        var partFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        var rows = new List<MbomWhereUsedRow>();
        var cache = new MarkingCache();
        await using (var cmd = new NpgsqlCommand(MbomSql.WhereUsed(mbomFilter, partFilter), conn, tx))
        {
            foreach (var (k, v) in mbomFilter.Parameters.Concat(partFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("target", part.Id);
            cmd.Parameters.Add(new NpgsqlParameter("plant", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)plant?.Trim().ToUpperInvariant() ?? DBNull.Value });
            cmd.Parameters.AddWithValue("limit", BomLimits.MaxWhereUsedRows + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var readable = !r.IsDBNull(3) && !r.IsDBNull(12);
                Markings? m = null;
                string? banner = null;
                if (readable) (m, banner) = cache.Get(r, r.GetGuid(12), 13);
                rows.Add(new MbomWhereUsedRow(!readable, r.GetInt32(0), r.GetDecimal(1), r.GetString(2), readable ? r.GetString(3) : null, readable ? r.GetString(4) : null, readable ? r.GetString(5) : null,
                    readable ? r.GetString(6) : null, readable ? r.GetString(7) : null, readable ? r.GetInt32(8) : null, readable ? r.GetString(9) : null, readable && !r.IsDBNull(10) ? r.GetString(10) : null,
                    readable ? r.GetString(11) : null, banner, m));
                if (rows.Count > BomLimits.MaxWhereUsedRows) return Outcome<MbomWhereUsedResult>.Invalid([$"More than {BomLimits.MaxWhereUsedRows:N0} usages; narrow the plant."]);
            }
        }
        var pageBanner = Markings.AggregateBanner(cache.Distinct.Append(part.Markings));
        await audit.AppendAsync(tx, Entry(subject, "mbom.where_used", AuditOutcome.Success, partNumber, correlationId, ("plant", plant), ("rows", Fmt(rows.Count)), ("redacted", Fmt(rows.Count(x => x.Redacted))), ("banner", pageBanner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<MbomWhereUsedResult>.Ok(new MbomWhereUsedResult(part.Number, plant, rows, pageBanner));
    }

    // ---------------------------------------------------------------- compare two iterations of one MBOM

    /// <summary>Compares the lines of two iterations of an MBOM (labels like "A.1" and "A.2"). Lines are matched on (part, find number), then on part alone; restricted lines on find number.</summary>
    public Task<Outcome<MbomComparison>> CompareAsync(Subject subject, string number, string from, string to, string? correlationId, CancellationToken ct = default)
    {
        var fm = MbomRules.IterationLabel.Match(from ?? "");
        var tm = MbomRules.IterationLabel.Match(to ?? "");
        if (!fm.Success || !tm.Success) return Task.FromResult(Outcome<MbomComparison>.Invalid(["Versions must be written like A.1 (revision, dot, iteration)."]));
        return ReadAsync<MbomComparison>(subject, number, null, "mbom.compare", correlationId, async (conn, tx, head) =>
        {
            var a = await IterationIdAsync(conn, tx, head.Id, fm.Groups[1].Value, int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture), ct);
            var b = await IterationIdAsync(conn, tx, head.Id, tm.Groups[1].Value, int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture), ct);
            if (a is null || b is null) return (Outcome<MbomComparison>.NotFound(), []);
            var cache = new MarkingCache();
            var left = await LoadLineViewsAsync(conn, tx, subject, false, a.Value, null, cache, ct);
            var right = await LoadLineViewsAsync(conn, tx, subject, false, b.Value, null, cache, ct);
            var (diff, unchanged) = MbomDiffer.Compute(left, right);
            var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
            return (Outcome<MbomComparison>.Ok(new MbomComparison(head.Number, from!, to!, diff, unchanged, banner)), [("from", from), ("to", to), ("changes", Fmt(diff.Count)), ("banner", banner)]);
        }, ct);
    }

    private static async Task<Guid?> IterationIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid mbom, string revision, int iteration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT i.id FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id WHERE r.mbom_id = @m AND r.revision = @rev AND i.iteration = @it", conn, tx);
        cmd.Parameters.AddWithValue("m", mbom);
        cmd.Parameters.AddWithValue("rev", revision);
        cmd.Parameters.AddWithValue("it", iteration);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    // ---------------------------------------------------------------- EBOM and MBOM side by side

    /// <summary>
    /// The lines of the EBOM a spec selects next to the MBOM lines that implement them: one row per group of lines joined by links, then EBOM lines nobody implements and MBOM lines
    /// without a source. The same link carry-over as reconciliation applies when the EBOM moved on.
    /// </summary>
    public Task<Outcome<SideBySide>> SideBySideAsync(Subject subject, string number, EbomSpec? spec, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<SideBySide>(subject, number, null, "mbom.side_by_side", correlationId, async (conn, tx, head) =>
        {
            var part = (await MbomStore.LoadPartAsync(conn, tx, head.PartMasterId, ct))!;
            var previous = spec is null ? await LatestRunAsync(conn, tx, head.IterationId, ct) : null;
            var chosen = spec ?? (previous is null ? EbomSpec.Released : SpecOf(previous));
            var (resolved, error) = await MbomStore.ResolveEbomAsync(conn, tx, subject, part, chosen, ct);
            if (resolved is null) return (Outcome<SideBySide>.Invalid([error!]), []);
            var ebom = await LoadEbomCopyLinesAsync(conn, tx, resolved.IterationId, chosen.Kind == EbomSpecKind.Effectivity ? chosen.Unit : null, ct);
            var content = await MbomStore.ReadContentAsync(conn, tx, false, head.IterationId, ct);
            var alt = MbomStore.WithAlternates(content).ToDictionary(x => x.Line.Id, x => x.Alt);
            var ids = ebom.Select(e => e.Child).Concat(content.Select(c => c.Child)).Distinct().ToList();
            var parts = await ReadableAsync(conn, tx, subject, ids, ct);
            var known = ebom.Select(e => e.Id).ToHashSet();
            var links = content.SelectMany(l => l.Links.Select(k => new LinkData(l.Id, k.EbomUsageId, k.Status, k.Reason))).ToList();
            var elsewhere = await LoadEbomLinesByIdAsync(conn, tx, [.. links.Select(l => l.EbomId).Where(i => !known.Contains(i)).Distinct()], ct);
            var ebomData = ebom.Select(e => new EbomLineData(e.Id, e.Find, e.Alt, e.Child, e.Quantity, e.Unit, e.Eff)).ToList();
            var mbomData = content.Select(c => new MbomLineData(c.Id, c.Find, alt[c.Id], c.Child, c.Quantity, c.Unit, c.Scrap, c.Phantom, c.SupplyType, c.MbomOnly, c.Eff)).ToDictionary(m => m.Id);
            var effective = ReconcileEngine.EffectiveLinks(
                new ReconcileInput(ebomData, [], links, elsewhere, null, null, resolved.IterationId, resolved.Label, new HashSet<Guid>(), new Dictionary<Guid, ChildMbomInfo>()),
                ebomData, ebomData.ToDictionary(e => e.Id), mbomData);

            SideLine E(EbomCopyLine e) => parts.TryGetValue(e.Child, out var p)
                ? new SideLine(e.Id, e.Find, false, p.Number, p.Name, e.Quantity, e.Unit, null, e.Eff)
                : new SideLine(e.Id, e.Find, true, null, null, e.Quantity, e.Unit, null, e.Eff);
            SideLine M(NewMbomLine m)
            {
                var detail = string.Join(", ", new[] { m.Scrap is { } s ? $"scrap {MbomText.Qty(s)}" : null, m.SupplyType, m.Phantom ? "phantom" : null, m.MbomOnly ? "MBOM only" : null, m.OperationRef is null ? null : $"op {m.OperationRef}" }.Where(x => x is not null));
                return parts.TryGetValue(m.Child, out var p)
                    ? new SideLine(m.Id, m.Find, false, p.Number, p.Name, m.Quantity, m.Unit, detail, m.Eff)
                    : new SideLine(m.Id, m.Find, true, null, null, m.Quantity, m.Unit, null, m.Eff);
            }
            var ebomById = ebom.ToDictionary(e => e.Id);
            var mbomById = content.ToDictionary(c => c.Id);
            var parent = new Dictionary<Guid, Guid>();
            Guid Root(Guid x) { while (parent.TryGetValue(x, out var p) && p != x) x = p; return x; }
            foreach (var l in effective)
            {
                parent.TryAdd(l.MbomId, l.MbomId);
                parent.TryAdd(l.EbomId, l.EbomId);
                var a = Root(l.MbomId); var b = Root(l.EbomId);
                if (a != b) parent[a] = b;
            }
            var rows = new List<SideBySideRow>();
            foreach (var g in effective.GroupBy(l => Root(l.MbomId)).OrderBy(g => g.Min(l => mbomById[l.MbomId].Find)))
            {
                var es = g.Select(l => l.EbomId).Distinct().Select(i => ebomById[i]).OrderBy(e => e.Find).ThenBy(e => e.Alt).Select(E).ToList();
                var ms = g.Select(l => l.MbomId).Distinct().Select(i => mbomById[i]).OrderBy(m => m.Find).Select(M).ToList();
                var worst = g.OrderByDescending(l => l.Status == LinkStatuses.Substituted ? 2 : l.Status == LinkStatuses.QuantityAdjusted ? 1 : 0).First();
                rows.Add(new SideBySideRow(es, ms, "linked", worst.Status, g.All(l => parts.ContainsKey(mbomById[l.MbomId].Child)) ? g.Select(l => l.Reason).FirstOrDefault(r => r is not null) : null));
            }
            var linkedE = effective.Select(l => l.EbomId).ToHashSet();
            var linkedM = effective.Select(l => l.MbomId).ToHashSet();
            foreach (var e in ebom.Where(e => !linkedE.Contains(e.Id))) rows.Add(new SideBySideRow([E(e)], [], "ebom_only", null, null));
            foreach (var m in content.Where(m => !linkedM.Contains(m.Id)).OrderBy(m => m.Find)) rows.Add(new SideBySideRow([], [M(m)], m.MbomOnly ? "mbom_only" : "unexplained", null, null));
            var banner = Markings.AggregateBanner(parts.Values.Select(p => p.Markings).Append(head.Markings));
            return (Outcome<SideBySide>.Ok(new SideBySide(head.Number, resolved.Description, head.Label, rows, banner)), [("against", resolved.Description), ("rows", Fmt(rows.Count)), ("banner", banner)]);
        }, ct);
}

/// <summary>Compares the lines of two MBOM iterations. Restricted lines match on find number only and reveal nothing but the changes of find, quantity and unit.</summary>
internal static class MbomDiffer
{
    public static (IReadOnlyList<MbomDiffEntry> Lines, int Unchanged) Compute(IReadOnlyList<MbomLineView> from, IReadOnlyList<MbomLineView> to)
    {
        var fromMatch = new int[from.Count];
        var toMatch = new int[to.Count];
        Array.Fill(fromMatch, -1);
        Array.Fill(toMatch, -1);
        var toByKey = new Dictionary<(Guid?, int, bool), Queue<int>>();
        for (var j = 0; j < to.Count; j++)
        {
            var key = (to[j].Redacted ? null : to[j].ChildMasterId, to[j].FindNumber, to[j].Redacted);
            if (!toByKey.TryGetValue(key, out var q)) toByKey[key] = q = new Queue<int>();
            q.Enqueue(j);
        }
        for (var i = 0; i < from.Count; i++)
            if (toByKey.TryGetValue((from[i].Redacted ? null : from[i].ChildMasterId, from[i].FindNumber, from[i].Redacted), out var q) && q.Count > 0)
            {
                var j = q.Dequeue();
                (fromMatch[i], toMatch[j]) = (j, i);
            }
        for (var i = 0; i < from.Count; i++)
        {
            if (fromMatch[i] >= 0 || from[i].Redacted) continue;
            for (var j = 0; j < to.Count; j++)
                if (toMatch[j] < 0 && !to[j].Redacted && to[j].ChildMasterId == from[i].ChildMasterId) { (fromMatch[i], toMatch[j]) = (j, i); break; }
        }
        var lines = new List<MbomDiffEntry>();
        var unchanged = 0;
        for (var i = 0; i < from.Count; i++)
        {
            var a = from[i];
            if (fromMatch[i] < 0) { lines.Add(new MbomDiffEntry(BomChangeKind.Removed, a.Redacted, a.ChildNumber, a.ChildName, a.FindNumber, null, [], a.Banner, a.Markings)); continue; }
            var b = to[fromMatch[i]];
            var changes = new List<string>();
            if (a.FindNumber != b.FindNumber) changes.Add($"Find number {a.FindNumber} to {b.FindNumber}");
            if (a.Quantity != b.Quantity) changes.Add($"Quantity {MbomText.Qty(a.Quantity)} to {MbomText.Qty(b.Quantity)}");
            if (a.Unit != b.Unit) changes.Add($"Unit {a.Unit} to {b.Unit}");
            if (a.ScrapFactor != b.ScrapFactor) changes.Add($"Scrap factor {(a.ScrapFactor is { } x ? MbomText.Qty(x) : "none")} to {(b.ScrapFactor is { } y ? MbomText.Qty(y) : "none")}");
            if (a.Phantom != b.Phantom) changes.Add(b.Phantom ? "Made phantom" : "No longer phantom");
            if (a.MbomOnly != b.MbomOnly) changes.Add(b.MbomOnly ? "Flagged MBOM-only" : "No longer MBOM-only");
            if (!a.Redacted)
            {
                if (a.SupplyType != b.SupplyType) changes.Add($"Supply type {a.SupplyType} to {b.SupplyType}");
                if (a.OperationReference != b.OperationReference) changes.Add($"Operation reference {a.OperationReference ?? "none"} to {b.OperationReference ?? "none"}");
                if (!a.ReferenceDesignators.SequenceEqual(b.ReferenceDesignators)) changes.Add("Reference designators changed");
                if ((a.Notes ?? "") != (b.Notes ?? "")) changes.Add("Notes changed");
                if (!Equals(a.Effectivity, b.Effectivity)) changes.Add($"Effectivity {a.EffectivityText ?? "none"} to {b.EffectivityText ?? "none"}");
                var la = string.Join(';', a.Links.Select(l => $"{l.EbomFindNumber}:{l.Status}:{l.Reason}"));
                var lb = string.Join(';', b.Links.Select(l => $"{l.EbomFindNumber}:{l.Status}:{l.Reason}"));
                if (la != lb) changes.Add("EBOM links changed");
            }
            if (changes.Count == 0) { unchanged++; continue; }
            lines.Add(new MbomDiffEntry(BomChangeKind.Changed, a.Redacted, a.ChildNumber, a.ChildName, a.FindNumber, b.FindNumber, changes, b.Banner, b.Markings));
        }
        for (var j = 0; j < to.Count; j++)
            if (toMatch[j] < 0) lines.Add(new MbomDiffEntry(BomChangeKind.Added, to[j].Redacted, to[j].ChildNumber, to[j].ChildName, null, to[j].FindNumber, [], to[j].Banner, to[j].Markings));
        return ([.. lines.OrderBy(l => l.ToFind ?? l.FromFind).ThenBy(l => l.Kind)], unchanged);
    }
}
