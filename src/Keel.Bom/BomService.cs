using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// BOM structure queries and working-copy edits. Every call evaluates the access policy (ADR-0004) and writes an audit event
/// (ADR-0005) in the same transaction as its change, including denials. Structural queries run through the SQL read filter and
/// return redacted placeholders for lines the caller cannot read (A4). Edits are serialized on the part master row.
/// </summary>
public sealed partial class BomService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const string ObjectType = "part";

    private sealed record Root(
        Guid MasterId, string Number, string Name, string PartType, Markings Markings,
        Guid RevisionId, string Revision, string State, int LatestIteration, Guid IterationId, int Iteration);

    private sealed record Seed(Guid IterationId, int Level, int[] Path, decimal Extended, IReadOnlyList<Guid> Ancestors);

    private sealed record ExplodeData(IReadOnlyList<BomLine> Lines, string Banner, int Redacted, int Unresolved);

    // ---------------------------------------------------------------- explosion

    public async Task<Outcome<BomExplosion>> ExplodeAsync(Subject subject, BomQuery query, string? correlationId, CancellationToken ct = default)
    {
        if (query.Spec is BomConfigSpec.Effectivity or BomConfigSpec.Baseline) throw BomSql.Phase2(query.Spec);
        var errors = ValidateQuery(query, out var path);
        if (errors.Count > 0) return Outcome<BomExplosion>.Invalid(errors);

        return await WithRootAsync<BomExplosion>(subject, query, PlmAction.Read, "bom.explode", correlationId, async (conn, tx, root) =>
        {
            var seed = await ResolveSeedAsync(conn, tx, subject, root, query.Spec, path!, ct);
            if (seed is null) return (Outcome<BomExplosion>.NotFound(), []);
            var data = await ExplodeCoreAsync(conn, tx, subject, root, query.Spec, query.Depth, seed, ct);
            if (data is null) return (TooLarge<BomExplosion>(), []);
            var result = new BomExplosion(root.Number, root.Name, root.Revision, root.Iteration, query.Spec, query.Depth, query.ParentLine,
                data.Lines, data.Banner, data.Redacted, data.Unresolved);
            return (Outcome<BomExplosion>.Ok(result), Details(root, query, data));
        }, ct);
    }

    /// <summary>CSV export of the full explosion. Same filter and redaction as the screen; the banner is the first and last line (ADR-0004).</summary>
    public async Task<Outcome<BomExport>> ExportCsvAsync(Subject subject, BomQuery query, string? correlationId, CancellationToken ct = default)
    {
        if (query.Spec is BomConfigSpec.Effectivity or BomConfigSpec.Baseline) throw BomSql.Phase2(query.Spec);
        var errors = ValidateQuery(query with { ParentLine = null }, out _);
        if (errors.Count > 0) return Outcome<BomExport>.Invalid(errors);

        return await WithRootAsync<BomExport>(subject, query, PlmAction.Download, "bom.export", correlationId, async (conn, tx, root) =>
        {
            var data = await ExplodeCoreAsync(conn, tx, subject, root, query.Spec, query.Depth, new Seed(root.IterationId, 0, [], 1m, [root.MasterId]), ct);
            if (data is null) return (TooLarge<BomExport>(), []);
            var csv = BomCsv.Render(data.Banner, data.Lines);
            var file = $"{root.Number}_{root.Revision}.{root.Iteration}_{query.Spec}_bom.csv";
            return (Outcome<BomExport>.Ok(new BomExport(file, csv, data.Banner, data.Lines.Count, data.Redacted)),
                Details(root, query, data, ("format", "csv")));
        }, ct);
    }

    /// <summary>
    /// Indented-to-single-level summary: extended quantities summed per distinct part and unit. Restricted lines are never merged
    /// (that would reveal that two hidden lines are the same part); each stays its own placeholder.
    /// </summary>
    public async Task<Outcome<BomSummary>> SummaryAsync(Subject subject, BomQuery query, bool leavesOnly, string? correlationId, CancellationToken ct = default)
    {
        if (query.Spec is BomConfigSpec.Effectivity or BomConfigSpec.Baseline) throw BomSql.Phase2(query.Spec);
        var errors = ValidateQuery(query with { ParentLine = null }, out _);
        if (errors.Count > 0) return Outcome<BomSummary>.Invalid(errors);

        return await WithRootAsync<BomSummary>(subject, query, PlmAction.Read, "bom.summary", correlationId, async (conn, tx, root) =>
        {
            var data = await ExplodeCoreAsync(conn, tx, subject, root, query.Spec, query.Depth, new Seed(root.IterationId, 0, [], 1m, [root.MasterId]), ct);
            if (data is null) return (TooLarge<BomSummary>(), []);
            var lines = new List<BomSummaryLine>();
            var candidates = data.Lines.Where(l => !leavesOnly || !l.HasChildren).ToList();
            foreach (var g in candidates.Where(l => !l.Redacted).GroupBy(l => (l.MasterId, l.Unit)))
            {
                var first = g.First();
                lines.Add(new BomSummaryLine(false, first.Number, first.Name, first.Revision is null ? null : $"{first.Revision}.{first.Iteration}", first.StateLabel,
                    g.Key.Unit, g.Sum(l => l.ExtendedQuantity), g.Count(), first.PartType == "Assembly", null, first.Banner, first.Markings));
            }
            lines = [.. lines.OrderBy(l => l.Number, StringComparer.Ordinal).ThenBy(l => l.Unit, StringComparer.Ordinal)];
            lines.AddRange(candidates.Where(l => l.Redacted).Select(l =>
                new BomSummaryLine(true, null, null, null, null, l.Unit, l.ExtendedQuantity, 1, false, l.Path, null, null)));
            var result = new BomSummary(root.Number, $"{root.Revision}.{root.Iteration}", query.Spec, leavesOnly, lines, data.Banner);
            return (Outcome<BomSummary>.Ok(result), Details(root, query, data, ("leavesOnly", leavesOnly.ToString()), ("distinctParts", lines.Count.ToString(CultureInfo.InvariantCulture))));
        }, ct);
    }

    private static List<string> ValidateQuery(BomQuery q, out int[]? path)
    {
        var errors = new List<string>();
        if (q.Depth is { } d && (d < 1 || d > BomLimits.MaxDepth)) errors.Add($"Depth must be between 1 and {BomLimits.MaxDepth}.");
        if (q.Iteration is { } it && it < 1) errors.Add("Iteration must be 1 or greater.");
        path = BomRules.ParsePath(q.ParentLine);
        if (path is null) errors.Add("Parent line must be a find-number path such as 10.20.");
        return errors;
    }

    private static Outcome<T> TooLarge<T>() =>
        Outcome<T>.Invalid([$"The structure has more than {BomLimits.MaxExplosionLines:N0} lines; request a depth limit or expand one line at a time."]);

    private static (string, string?)[] Details(Root root, BomQuery q, ExplodeData data, params (string, string?)[] extra) =>
    [
        ("root", $"{root.Revision}.{root.Iteration}"), ("spec", q.Spec.ToString()),
        ("depth", q.Depth?.ToString(CultureInfo.InvariantCulture) ?? "full"), ("parentLine", q.ParentLine),
        ("lines", data.Lines.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", data.Redacted.ToString(CultureInfo.InvariantCulture)),
        ("unresolved", data.Unresolved.ToString(CultureInfo.InvariantCulture)), ("banner", data.Banner), .. extra,
    ];

    private static async Task<ExplodeData?> ExplodeCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Root root, BomConfigSpec spec, int? depth, Seed seed, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var lines = new List<BomLine>(1024);
        var cache = new MarkingCache();
        int redacted = 0, unresolved = 0;

        await using var cmd = new NpgsqlCommand(BomSql.Explode(spec, filter), conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("base_level", seed.Level);
        cmd.Parameters.AddWithValue("base_path", seed.Path);
        cmd.Parameters.AddWithValue("base_ext", seed.Extended);
        cmd.Parameters.AddWithValue("seed_iter", seed.IterationId);
        cmd.Parameters.AddWithValue("base_anc", seed.Ancestors.ToArray());
        cmd.Parameters.AddWithValue("max_level", depth is { } d ? seed.Level + d : 10_000);
        cmd.Parameters.AddWithValue("limit", BomLimits.MaxExplosionLines + 1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var readable = r.GetBoolean(8);
            var circular = r.GetBoolean(9);
            Guid? master = r.IsDBNull(11) ? null : r.GetGuid(11);
            var isRedacted = !readable || master is null;
            string? banner = null;
            Markings? markings = null;
            if (!isRedacted)
            {
                (markings, banner) = cache.Get(r, master!.Value, 21);
            }
            var rev = isRedacted || r.IsDBNull(15) ? null : r.GetString(15);
            var isUnresolved = !isRedacted && rev is null;
            if (isRedacted) redacted++;
            if (isUnresolved) unresolved++;
            lines.Add(new BomLine(
                r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt32(7), r.GetDecimal(5), r.GetString(6), r.GetDecimal(4),
                isRedacted, isUnresolved, !isRedacted && circular, !isRedacted && r.GetBoolean(10),
                isRedacted ? null : master, isRedacted ? null : r.GetString(12), isRedacted ? null : r.GetString(13), isRedacted ? null : r.GetString(14),
                rev, isRedacted || r.IsDBNull(16) ? null : r.GetInt32(16), isRedacted || r.IsDBNull(17) ? null : r.GetString(17),
                isRedacted || r.IsDBNull(18) ? null : r.GetString(18),
                isRedacted || r.IsDBNull(19) ? [] : r.GetFieldValue<string[]>(19), isRedacted || r.IsDBNull(20) ? null : r.GetString(20),
                banner, markings));
            if (lines.Count > BomLimits.MaxExplosionLines) return null;
        }
        var pageBanner = Markings.AggregateBanner(cache.Distinct.Append(root.Markings));
        return new ExplodeData(lines, pageBanner, redacted, unresolved);
    }

    /// <summary>Walks the find-number path from the root, one readable hop at a time, to the iteration whose children are wanted.</summary>
    private static async Task<Seed?> ResolveSeedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Root root, BomConfigSpec spec, int[] path, CancellationToken ct)
    {
        var iteration = root.IterationId;
        var extended = 1m;
        var ancestors = new List<Guid> { root.MasterId };
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        foreach (var find in path)
        {
            await using var cmd = new NpgsqlCommand(BomSql.Hop(spec, filter), conn, tx);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("parent", iteration);
            cmd.Parameters.AddWithValue("find", find);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.IsDBNull(2)) return null;
            var child = r.GetGuid(0);
            if (ancestors.Contains(child)) return null;
            ancestors.Add(child);
            extended = decimal.Round(Math.Min(extended * r.GetDecimal(1), 1e18m), 6);
            iteration = r.GetGuid(2);
        }
        return new Seed(iteration, path.Length, path, extended, ancestors);
    }

    // ---------------------------------------------------------------- where-used

    public async Task<Outcome<WhereUsedResult>> WhereUsedAsync(Subject subject, string number, int levels, string? correlationId, CancellationToken ct = default)
    {
        if (levels < 1 || levels > BomLimits.MaxDepth) return Outcome<WhereUsedResult>.Invalid([$"Levels must be between 1 and {BomLimits.MaxDepth}."]);
        return await WithRootAsync<WhereUsedResult>(subject, new BomQuery(number), PlmAction.Read, "bom.where_used", correlationId, async (conn, tx, root) =>
        {
            var parentFilter = AccessPolicy.BuildReadFilter(subject, "pm");
            var usedFilter = AccessPolicy.BuildReadFilter(subject, "um");
            var rows = new List<WhereUsedRow>();
            var cache = new MarkingCache();
            int redacted = 0;
            await using (var cmd = new NpgsqlCommand(BomSql.WhereUsed(parentFilter, usedFilter), conn, tx))
            {
                foreach (var (k, v) in parentFilter.Parameters.Concat(usedFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("target", root.MasterId);
                cmd.Parameters.AddWithValue("levels", levels);
                cmd.Parameters.AddWithValue("limit", BomLimits.MaxWhereUsedRows + 1);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var readable = !r.IsDBNull(11);
                    string? banner = null;
                    Markings? markings = null;
                    if (readable) (markings, banner) = cache.Get(r, r.GetGuid(11), 12);
                    else redacted++;
                    rows.Add(new WhereUsedRow(r.GetInt32(0), !readable, r.GetInt32(1), r.GetDecimal(2), r.GetString(3),
                        readable && !r.IsDBNull(4) ? r.GetString(4) : null,
                        readable ? r.GetString(5) : null, readable ? r.GetString(6) : null, readable ? r.GetString(7) : null,
                        readable ? r.GetInt32(8) : null, readable ? r.GetString(9) : null, readable && !r.IsDBNull(10) ? r.GetString(10) : null,
                        banner, markings));
                    if (rows.Count > BomLimits.MaxWhereUsedRows)
                        return (Outcome<WhereUsedResult>.Invalid([$"More than {BomLimits.MaxWhereUsedRows:N0} usages; reduce the number of levels."]), []);
                }
            }
            var pageBanner = Markings.AggregateBanner(cache.Distinct.Append(root.Markings));
            return (Outcome<WhereUsedResult>.Ok(new WhereUsedResult(root.Number, root.Name, levels, rows, pageBanner)),
                [("levels", levels.ToString(CultureInfo.InvariantCulture)), ("rows", rows.Count.ToString(CultureInfo.InvariantCulture)),
                 ("redacted", redacted.ToString(CultureInfo.InvariantCulture)), ("banner", pageBanner)]);
        }, ct);
    }

    // ---------------------------------------------------------------- compare

    private static readonly Regex IterationLabel = IterationLabelPattern();

    /// <summary>Compares the direct structure of two iterations of a part, given labels such as "A.1" and "A.2".</summary>
    public async Task<Outcome<BomComparison>> CompareAsync(Subject subject, string number, string from, string to, string? correlationId, CancellationToken ct = default)
    {
        var fm = IterationLabel.Match(from ?? "");
        var tm = IterationLabel.Match(to ?? "");
        if (!fm.Success || !tm.Success) return Outcome<BomComparison>.Invalid(["Versions must be written like A.1 (revision, dot, iteration)."]);

        return await WithRootAsync<BomComparison>(subject, new BomQuery(number), PlmAction.Read, "bom.compare", correlationId, async (conn, tx, root) =>
        {
            var a = await LoadIterationIdAsync(conn, tx, root.MasterId, fm.Groups[1].Value, int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture), ct);
            var b = await LoadIterationIdAsync(conn, tx, root.MasterId, tm.Groups[1].Value, int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture), ct);
            if (a is null || b is null) return (Outcome<BomComparison>.NotFound(), []);

            var cache = new MarkingCache();
            var left = await LoadIterationLinesAsync(conn, tx, subject, a.Value, cache, ct);
            var right = await LoadIterationLinesAsync(conn, tx, subject, b.Value, cache, ct);
            var (diff, unchanged) = BomDiff.Compute(left, right);
            var banner = Markings.AggregateBanner(cache.Distinct.Append(root.Markings));
            return (Outcome<BomComparison>.Ok(new BomComparison(root.Number, from!, to!, diff, unchanged, banner)),
                [("from", from), ("to", to), ("changes", diff.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)]);
        }, ct);
    }

    private static async Task<Guid?> LoadIterationIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, string revision, int iteration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT i.id FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = @m AND r.revision = @rev AND i.iteration = @it", conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        cmd.Parameters.AddWithValue("rev", revision);
        cmd.Parameters.AddWithValue("it", iteration);
        return (Guid?)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<List<BomDiff.Side>> LoadIterationLinesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid iteration, MarkingCache cache, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var result = new List<BomDiff.Side>();
        await using var cmd = new NpgsqlCommand(BomSql.IterationLines(filter), conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("iter", iteration);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var readable = !r.IsDBNull(6);
            Markings? m = null;
            string? banner = null;
            if (readable) (m, banner) = cache.Get(r, r.GetGuid(6), 9);
            result.Add(new BomDiff.Side(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetString(3),
                string.Join(' ', r.GetFieldValue<string[]>(4)), r.IsDBNull(5) ? null : r.GetString(5),
                readable, readable ? r.GetString(7) : null, readable ? r.GetString(8) : null, banner, m));
        }
        return result;
    }

    // ---------------------------------------------------------------- shared scaffolding

    /// <summary>Loads the root part, enforces read (or download) access with auditing, runs the body and audits its outcome.</summary>
    private async Task<Outcome<T>> WithRootAsync<T>(
        Subject subject, BomQuery query, PlmAction permission, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Root, Task<(Outcome<T> Result, (string, string?)[] Details)>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var root = await LoadRootAsync(conn, tx, query.Number, query.Revision, query.Iteration, ct);
        if (root is null) return Outcome<T>.NotFound();

        var read = AccessPolicy.Evaluate(subject, root.Markings, PlmAction.Read);
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, root.Markings, permission);
        if (!read.Permitted || !decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, root.Number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }

        var (result, details) = await body(conn, tx, root);
        await audit.AppendAsync(tx, Entry(subject, action, result.IsOk ? AuditOutcome.Success : AuditOutcome.Failure, root.Number, correlationId,
            result.IsOk ? details : [("error", string.Join(" ", result.Errors)), ("kind", result.Kind.ToString())]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<Root?> LoadRootAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, string? revision, int? iteration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.part_type, {MarkingColumns.Select("m")},
                   r.id, r.revision, r.lifecycle_state, r.latest_iteration, i.id, i.iteration
            FROM plm.part_master m
            JOIN plm.part_revision r ON r.master_id = m.id AND ((@rev::text IS NULL AND r.is_latest) OR r.revision = @rev)
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = COALESCE(@it, r.latest_iteration)
            WHERE m.number = @n
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.AddWithValue("rev", (object?)revision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("it", (object?)iteration ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var o = 4 + MarkingColumns.Count;
        return new Root(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 4),
            r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetInt32(o + 3), r.GetGuid(o + 4), r.GetInt32(o + 5));
    }

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    // ASSUMPTION: created_by stores the login name for display; the audit log keeps the stable IdP subject (as in Keel.Parts).
    private static string DisplayId(Subject s) => s.Username ?? s.Id;

    [GeneratedRegex(@"^([A-Z]{1,3})\.([0-9]{1,6})$")]
    private static partial Regex IterationLabelPattern();
}
