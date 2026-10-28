using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Bom;

public sealed partial class MbomService
{
    internal sealed record RunRow(
        Guid Id, Guid MbomId, Guid RevisionId, Guid IterationId, Guid EbomIteration, string Spec, string SpecKind, string? SpecBaseline, string? SpecContext, long? SpecSerial, long? SpecLot, long? SpecDay,
        string? SpecIteration, Guid? Derived, string Status, int EbomLines, int MbomLines, int Covered, decimal Coverage, int Errors, int Warnings, int Infos, string Sha256, int DurationMs,
        DateTimeOffset RunAt, string RunBy);

    private const string RunColumns =
        "id, mbom_id, revision_id, iteration_id, ebom_part_iteration_id, spec, spec_kind, spec_baseline, spec_context, spec_serial, spec_lot, spec_day, spec_iteration, derived_part_iteration_id, status, ebom_lines, mbom_lines, covered_lines, coverage_pct, errors, warnings, infos, content_sha256, duration_ms, run_at, run_by";

    private static RunRow ReadRun(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetGuid(13), r.GetString(14), r.GetInt32(15), r.GetInt32(16), r.GetInt32(17), r.GetDecimal(18), r.GetInt32(19), r.GetInt32(20), r.GetInt32(21),
        r.GetString(22), r.GetInt32(23), r.GetFieldValue<DateTimeOffset>(24), r.GetString(25));

    private static EbomSpec SpecOf(RunRow run) => Enum.Parse<EbomSpecKind>(run.SpecKind) switch
    {
        EbomSpecKind.Latest => new EbomSpec(EbomSpecKind.Latest),
        EbomSpecKind.Effectivity => new EbomSpec(EbomSpecKind.Effectivity, new EffectivityRequest(run.SpecContext, run.SpecSerial, run.SpecLot, run.SpecDay)),
        EbomSpecKind.Baseline => new EbomSpec(EbomSpecKind.Baseline, Baseline: run.SpecBaseline),
        EbomSpecKind.Iteration => new EbomSpec(EbomSpecKind.Iteration, IterationLabel: run.SpecIteration),
        _ => new EbomSpec(EbomSpecKind.LatestReleased),
    };

    internal static async Task<RunRow?> LatestRunAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid iterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {RunColumns} FROM mbom.reconciliation_run WHERE iteration_id = @i ORDER BY run_at DESC, id DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("i", iterationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRun(r) : null;
    }

    internal static async Task<RunRow?> LoadRunAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {RunColumns} FROM mbom.reconciliation_run WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRun(r) : null;
    }

    // ---------------------------------------------------------------- release gate

    /// <summary>
    /// Why an MBOM cannot be submitted for review, or null. The latest reconciliation of the latest iteration must exist, have no errors (and no warnings when
    /// the plant's policy says so), still be fresh (the EBOM it ran against is still the one its spec selects) and verify against its own digest.
    /// </summary>
    internal static async Task<(RunRow? Run, string? Blocker)> ReleaseCheckAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, PlantInfo plant, CancellationToken ct)
    {
        var run = await LatestRunAsync(conn, tx, head.IterationId, ct);
        if (run is null) return (null, $"Run a reconciliation of {head.Label} against its EBOM first; an MBOM is released only with a clean reconciliation.");
        if (run.Status is ReconcileStatuses.NotReconciled or ReconcileStatuses.Stale)
            return (run, $"The latest reconciliation is {ReconcileStatuses.Label(run.Status)} ({run.Errors} error{(run.Errors == 1 ? "" : "s")}); resolve the findings and reconcile again.");
        if (run.Warnings > 0 && !plant.ReleaseAllowsWarnings)
            return (run, $"Plant {plant.Code} does not release an MBOM whose reconciliation has warnings ({run.Warnings}); resolve them and reconcile again.");
        if (run.Status is not (ReconcileStatuses.Reconciled or ReconcileStatuses.WithExceptions)) return (run, "The latest reconciliation has an unknown status.");
        var spec = SpecOf(run);
        if (spec.Kind is EbomSpecKind.Latest or EbomSpecKind.LatestReleased or EbomSpecKind.Effectivity)
        {
            var part = await MbomStore.LoadPartAsync(conn, tx, head.PartMasterId, ct);
            var (now, _) = await MbomStore.ResolveEbomAsync(conn, tx, null, part!, spec, ct);
            if (now is null || now.IterationId != run.EbomIteration)
                return (run, $"The EBOM of {head.PartNumber} changed since the reconciliation ({spec.Describe()} now selects another version); reconcile again.");
        }
        if (await RunHashAsync(conn, tx, run.Id, ct) != run.Sha256) return (run, "The stored reconciliation does not match its recorded digest.");
        return (run, null);
    }

    internal static async Task<string?> ReleaseBlockerAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, PlantInfo plant, CancellationToken ct) =>
        (await ReleaseCheckAsync(conn, tx, head, plant, ct)).Blocker;

    internal static async Task<ReconciliationBrief?> LastRunBriefAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, PlantInfo plant, CancellationToken ct)
    {
        var (run, blocker) = await ReleaseCheckAsync(conn, tx, head, plant, ct);
        return run is null ? null : new ReconciliationBrief(run.Id, run.Status, run.RunAt, run.RunBy, run.Spec, run.Errors, run.Warnings, run.Infos, run.Coverage, blocker is null, blocker);
    }

    // ---------------------------------------------------------------- the run digest

    internal static string RunDigest(RunRow run, IEnumerable<(int Seq, EngineFinding F)> findings)
    {
        var sb = new StringBuilder(1 << 14);
        sb.Append("keel-mbom-run/1|").Append(run.MbomId.ToString("N")).Append('|').Append(run.IterationId.ToString("N")).Append('|').Append(run.EbomIteration.ToString("N")).Append('|')
          .Append(run.Derived?.ToString("N") ?? "-").Append('|').Append(run.Status).Append('|').Append(Fmt(run.EbomLines)).Append(',').Append(Fmt(run.MbomLines)).Append(',').Append(Fmt(run.Covered)).Append('|')
          .Append(Fmt(run.Errors)).Append(',').Append(Fmt(run.Warnings)).Append(',').Append(Fmt(run.Infos)).Append('\n');
        foreach (var (seq, f) in findings.OrderBy(x => x.Seq))
            sb.Append("f|").Append(Fmt(seq)).Append('|').Append(f.Code).Append('|').Append(f.Severity).Append('|').Append(f.EbomUsageId?.ToString("N")).Append('|').Append(f.MbomUsageId?.ToString("N")).Append('|')
              .Append(f.ChildMasterId?.ToString("N")).Append('|').Append(f.EbomFind?.ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.MbomFind?.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(f.Expected is { } e ? MbomText.Qty(e) : "").Append('|').Append(f.Actual is { } a ? MbomText.Qty(a) : "").Append('|').Append(f.Unit).Append('|').Append(f.Detail).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Recomputes the digest of a stored run from its header and finding rows.</summary>
    internal static async Task<string> RunHashAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid runId, CancellationToken ct)
    {
        var run = await LoadRunAsync(conn, tx, runId, ct) ?? throw new InvalidOperationException("The reconciliation run is missing.");
        var findings = new List<(int, EngineFinding)>();
        await using var cmd = new NpgsqlCommand(
            "SELECT seq, code, severity, ebom_usage_id, mbom_usage_id, child_master_id, ebom_find, mbom_find, qty_expected, qty_actual, unit, detail FROM mbom.reconciliation_finding WHERE run_id = @r ORDER BY seq", conn, tx);
        cmd.Parameters.AddWithValue("r", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            findings.Add((r.GetInt32(0), new EngineFinding(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11))));
        return RunDigest(run, findings);
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>
    /// Reconciles the latest iteration against the EBOM a spec selects (default: the spec of the previous run, else the latest released EBOM) and stores the result as an
    /// immutable run. The comparison covers every line whatever the caller may read; what a reader sees of it is filtered when the run is read.
    /// </summary>
    public Task<Outcome<ReconciliationReport>> ReconcileAsync(Subject subject, string number, EbomSpec? spec, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<ReconciliationReport>(subject, number, "mbom.reconcile", correlationId, async (head, conn, tx) =>
        {
            var watch = Stopwatch.StartNew();
            var part = (await MbomStore.LoadPartAsync(conn, tx, head.PartMasterId, ct))!;
            var chosen = spec;
            if (chosen is null)
            {
                var previous = await LatestRunAsync(conn, tx, head.IterationId, ct);
                chosen = previous is null ? EbomSpec.Released : SpecOf(previous);
            }
            var (resolved, error) = await MbomStore.ResolveEbomAsync(conn, tx, subject, part, chosen, ct);
            if (resolved is null) return Step<ReconciliationReport>.Invalid(error!);

            var ebom = await MbomStore.LoadEbomLinesAsync(conn, tx, resolved.IterationId, chosen.Kind == EbomSpecKind.Effectivity ? chosen.Unit : null, ct);
            var content = await MbomStore.ReadContentAsync(conn, tx, false, head.IterationId, ct);
            Guid? derived;
            await using (var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id FROM mbom.iteration WHERE id = @i", conn, tx))
            {
                cmd.Parameters.AddWithValue("i", head.IterationId);
                derived = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
            var mbom = MbomStore.WithAlternates(content).Select(x => new MbomLineData(x.Line.Id, x.Line.Find, x.Alt, x.Line.Child, x.Line.Quantity, x.Line.Unit, x.Line.Scrap, x.Line.Phantom, x.Line.SupplyType, x.Line.MbomOnly, x.Line.Eff)).ToList();
            var links = content.SelectMany(l => l.Links.Select(k => new LinkData(l.Id, k.EbomUsageId, k.Status, k.Reason))).ToList();
            var known = ebom.Select(e => e.Id).ToHashSet();
            var elsewhere = await LoadEbomLinesByIdAsync(conn, tx, [.. links.Select(l => l.EbomId).Distinct().Where(i => !known.Contains(i))], ct);
            var input = new ReconcileInput(ebom, mbom, links, elsewhere, derived, derived is { } d ? await MbomStore.IterationLabelAsync(conn, tx, d, ct) : null,
                resolved.IterationId, resolved.Label, await MbomReconcileInputs.MakeAssembliesAsync(conn, tx, [.. mbom.Select(m => m.Child).Distinct()], ct),
                await MbomReconcileInputs.ChildMbomsAsync(conn, tx, head.Plant, [.. mbom.Select(m => m.Child).Distinct()], ct));
            var outcome = ReconcileEngine.Run(input);
            watch.Stop();

            var now = clock.GetUtcNow();
            var runId = Guid.CreateVersion7();
            var run = new RunRow(runId, head.Id, head.RevisionId, head.IterationId, resolved.IterationId, resolved.Description, chosen.Kind.ToString(), chosen.Baseline?.Trim(), chosen.Unit?.Context,
                chosen.Unit?.Serial, chosen.Unit?.Lot, chosen.Unit?.Day, chosen.IterationLabel, derived, outcome.Status, outcome.EbomLines, outcome.MbomLines, outcome.CoveredLines, outcome.CoveragePct,
                outcome.Errors, outcome.Warnings, outcome.Infos, "", (int)watch.ElapsedMilliseconds, now, BomService.DisplayId(subject));
            var numbered = outcome.Findings.Select((f, i) => (Seq: i + 1, F: f)).ToList();
            run = run with { Sha256 = RunDigest(run, numbered) };
            await WriteRunAsync(conn, tx, run, subject.Id, numbered, ct);
            var report = await LoadReportAsync(conn, tx, subject, head, runId, ct);
            return Step<ReconciliationReport>.Done(report!, ("run", runId.ToString("D")), ("iteration", head.Label), ("against", resolved.Description), ("status", outcome.Status),
                ("errors", Fmt(outcome.Errors)), ("warnings", Fmt(outcome.Warnings)), ("infos", Fmt(outcome.Infos)), ("coverage", outcome.CoveragePct.ToString("0.##", CultureInfo.InvariantCulture)),
                ("ebomLines", Fmt(outcome.EbomLines)), ("mbomLines", Fmt(outcome.MbomLines)), ("ms", Fmt(run.DurationMs)), ("contentSha256", run.Sha256));
        }, ct);

    private static async Task WriteRunAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunRow run, string byId, List<(int Seq, EngineFinding F)> findings, CancellationToken ct)
    {
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO mbom.reconciliation_run (id, mbom_id, revision_id, iteration_id, ebom_part_iteration_id, spec, spec_kind, spec_baseline, spec_context, spec_serial, spec_lot, spec_day, spec_iteration,
                derived_part_iteration_id, status, ebom_lines, mbom_lines, covered_lines, coverage_pct, errors, warnings, infos, content_sha256, duration_ms, run_at, run_by, run_by_id)
            VALUES (@id, @m, @rev, @it, @ebom, @spec, @kind, @bl, @ctx, @serial, @lot, @day, @iter, @derived, @status, @el, @ml, @cl, @cov, @err, @warn, @info, @sha, @ms, @at, @by, @byid)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", run.Id);
            ins.Parameters.AddWithValue("m", run.MbomId);
            ins.Parameters.AddWithValue("rev", run.RevisionId);
            ins.Parameters.AddWithValue("it", run.IterationId);
            ins.Parameters.AddWithValue("ebom", run.EbomIteration);
            ins.Parameters.AddWithValue("spec", run.Spec);
            ins.Parameters.AddWithValue("kind", run.SpecKind);
            ins.Parameters.Add(new NpgsqlParameter("bl", NpgsqlDbType.Text) { Value = (object?)run.SpecBaseline ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ctx", NpgsqlDbType.Text) { Value = (object?)run.SpecContext ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("serial", NpgsqlDbType.Bigint) { Value = (object?)run.SpecSerial ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("lot", NpgsqlDbType.Bigint) { Value = (object?)run.SpecLot ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("day", NpgsqlDbType.Bigint) { Value = (object?)run.SpecDay ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("iter", NpgsqlDbType.Text) { Value = (object?)run.SpecIteration ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("derived", NpgsqlDbType.Uuid) { Value = (object?)run.Derived ?? DBNull.Value });
            ins.Parameters.AddWithValue("status", run.Status);
            ins.Parameters.AddWithValue("el", run.EbomLines);
            ins.Parameters.AddWithValue("ml", run.MbomLines);
            ins.Parameters.AddWithValue("cl", run.Covered);
            ins.Parameters.AddWithValue("cov", run.Coverage);
            ins.Parameters.AddWithValue("err", run.Errors);
            ins.Parameters.AddWithValue("warn", run.Warnings);
            ins.Parameters.AddWithValue("info", run.Infos);
            ins.Parameters.AddWithValue("sha", run.Sha256);
            ins.Parameters.AddWithValue("ms", run.DurationMs);
            ins.Parameters.AddWithValue("at", run.RunAt);
            ins.Parameters.AddWithValue("by", run.RunBy);
            ins.Parameters.AddWithValue("byid", byId);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (findings.Count == 0) return;
        await using var copy = await conn.BeginBinaryImportAsync(
            "COPY mbom.reconciliation_finding (run_id, seq, code, severity, ebom_usage_id, mbom_usage_id, child_master_id, ebom_find, mbom_find, qty_expected, qty_actual, unit, detail) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (seq, f) in findings)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(run.Id, NpgsqlDbType.Uuid, ct);
            await copy.WriteAsync(seq, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(f.Code, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(f.Severity, NpgsqlDbType.Text, ct);
            if (f.EbomUsageId is { } e) await copy.WriteAsync(e, NpgsqlDbType.Uuid, ct); else await copy.WriteNullAsync(ct);
            if (f.MbomUsageId is { } m) await copy.WriteAsync(m, NpgsqlDbType.Uuid, ct); else await copy.WriteNullAsync(ct);
            if (f.ChildMasterId is { } c) await copy.WriteAsync(c, NpgsqlDbType.Uuid, ct); else await copy.WriteNullAsync(ct);
            if (f.EbomFind is { } ef) await copy.WriteAsync(ef, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
            if (f.MbomFind is { } mf) await copy.WriteAsync(mf, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
            if (f.Expected is { } x) await copy.WriteAsync(x, NpgsqlDbType.Numeric, ct); else await copy.WriteNullAsync(ct);
            if (f.Actual is { } a) await copy.WriteAsync(a, NpgsqlDbType.Numeric, ct); else await copy.WriteNullAsync(ct);
            if (f.Unit is { } u) await copy.WriteAsync(u, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
            if (f.Detail is { } d) await copy.WriteAsync(d, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
        }
        await copy.CompleteAsync(ct);
    }

    // ---------------------------------------------------------------- inputs of the comparison (system view; the result is filtered when read)

    private static async Task<List<EbomLineData>> LoadEbomLinesByIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = new List<EbomLineData>();
        if (ids.Count == 0) return list;
        await using var cmd = new NpgsqlCommand("SELECT id, find_number, alt, child_master_id, quantity, unit, eff_kind, eff_context, eff_from, eff_to FROM plm.bom_usage WHERE id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new EbomLineData(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), r.GetDecimal(4), r.GetString(5), EffectivityStore.ReadRange(r, 6)));
        return list;
    }

    // ---------------------------------------------------------------- reading runs

    public static string FindingTitle(string code) => code switch
    {
        FindingCodes.EbomLineMissing => "EBOM line missing in the MBOM",
        FindingCodes.MbomLineUnexplained => "MBOM line without an EBOM source",
        FindingCodes.MbomOnlyLine => "Manufacturing-only line",
        FindingCodes.MbomOnlyWithLinks => "MBOM-only line that also has EBOM links",
        FindingCodes.QuantityDifference => "Quantity differs from the EBOM",
        FindingCodes.QuantityAdjusted => "Declared quantity adjustment",
        FindingCodes.UnitMismatch => "Unit differs from the EBOM",
        FindingCodes.LinkChildMismatch => "Matched link joins different parts",
        FindingCodes.Substitution => "Substituted part",
        FindingCodes.SubstitutionSamePart => "Substitution of the same part",
        FindingCodes.EffectivityGap => "Effectivity not covered by the MBOM",
        FindingCodes.EffectivityExcess => "MBOM effectivity beyond the EBOM",
        FindingCodes.EffectivityDimension => "Effectivity cannot be compared",
        FindingCodes.RevisionDrift => "EBOM changed since the MBOM was derived",
        FindingCodes.NotDerived => "Not derived from an EBOM",
        FindingCodes.ChildMbomMissing => "Make sub-assembly has no MBOM for this plant",
        FindingCodes.ChildMbomUnreleased => "MBOM of the sub-assembly is not released",
        _ => code,
    };

    private static async Task<ReconciliationReport?> LoadReportAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, MbomHead head, Guid runId, CancellationToken ct)
    {
        var run = await LoadRunAsync(conn, tx, runId, ct);
        if (run is null || run.MbomId != head.Id) return null;
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var cache = new MarkingCache();
        var findings = new List<FindingView>();
        int redacted = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT f.seq, f.code, f.severity, f.ebom_usage_id, f.mbom_usage_id, f.child_master_id, f.ebom_find, f.mbom_find, f.qty_expected, f.qty_actual, f.unit, f.detail,
                   cm.id, cm.number, cm.name, {MarkingColumns.Select("cm")}
            FROM mbom.reconciliation_finding f
            LEFT JOIN plm.part_master cm ON cm.id = f.child_master_id AND {filter.Sql}
            WHERE f.run_id = @r ORDER BY f.seq LIMIT @limit
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("limit", MbomLimits.MaxFindingsShown + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var hasChild = !r.IsDBNull(5);
                var readable = !r.IsDBNull(12);
                var hidden = hasChild && !readable;
                if (readable) cache.Get(r, r.GetGuid(12), 15);
                if (hidden) redacted++;
                var code = r.GetString(1);
                findings.Add(new FindingView(r.GetInt32(0), code, r.GetString(2), FindingTitle(code), hidden, r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                    readable ? r.GetString(13) : null, readable ? r.GetString(14) : null, r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10), hidden || r.IsDBNull(11) ? null : r.GetString(11)));
            }
        }
        var truncated = findings.Count > MbomLimits.MaxFindingsShown;
        if (truncated) findings.RemoveAt(findings.Count - 1);

        IReadOnlyList<BomDiffLine> changes = [];
        var markings = new List<Markings>(cache.Distinct) { head.Markings };
        if (run.Derived is { } derived && derived != run.EbomIteration)
        {
            var from = await BomService.LoadIterationLinesAsync(conn, tx, subject, derived, cache, ct);
            var to = await BomService.LoadIterationLinesAsync(conn, tx, subject, run.EbomIteration, cache, ct);
            changes = BomDiff.Compute(from, to).Lines;
            markings = [.. cache.Distinct, head.Markings];
        }
        string mbomRevision, ebomLabel;
        int mbomIteration;
        await using (var cmd = new NpgsqlCommand("SELECT r.revision, i.iteration FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id WHERE i.id = @i", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", run.IterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (mbomRevision, mbomIteration) = (r.GetString(0), r.GetInt32(1));
        }
        ebomLabel = await MbomStore.IterationLabelAsync(conn, tx, run.EbomIteration, ct) ?? "";
        var derivedLabel = run.Derived is { } dl ? await MbomStore.IterationLabelAsync(conn, tx, dl, ct) : null;
        var integrity = await RunHashAsync(conn, tx, runId, ct) == run.Sha256;
        var info = new ReconciliationRunInfo(run.Id, head.Number, mbomRevision, mbomIteration, run.Status, ReconcileStatuses.Label(run.Status), run.Spec, ebomLabel, derivedLabel,
            run.EbomLines, run.MbomLines, run.Covered, run.Coverage, run.Errors, run.Warnings, run.Infos, redacted, run.RunAt, run.RunBy, run.DurationMs, run.Sha256, integrity);
        return new ReconciliationReport(info, findings, changes, truncated, Markings.AggregateBanner(markings));
    }

    /// <summary>One run of an MBOM (the latest when <paramref name="runId"/> is null), with findings. Findings about unreadable parts keep codes, find numbers and quantities only.</summary>
    public Task<Outcome<ReconciliationReport>> GetReconciliationAsync(Subject subject, string number, Guid? runId, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ReconciliationReport>(subject, number, null, "mbom.reconciliation_read", correlationId, async (conn, tx, head) =>
        {
            var id = runId;
            if (id is null)
            {
                await using var cmd = new NpgsqlCommand("SELECT id FROM mbom.reconciliation_run WHERE mbom_id = @m ORDER BY run_at DESC, id DESC LIMIT 1", conn, tx);
                cmd.Parameters.AddWithValue("m", head.Id);
                id = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
            if (id is null) return (Outcome<ReconciliationReport>.NotFound(), []);
            var report = await LoadReportAsync(conn, tx, subject, head, id.Value, ct);
            return report is null ? (Outcome<ReconciliationReport>.NotFound(), [])
                : (Outcome<ReconciliationReport>.Ok(report), [("run", id.Value.ToString("D")), ("status", report.Run.Status), ("findings", Fmt(report.Findings.Count)), ("redacted", Fmt(report.Run.RedactedFindings)), ("banner", report.Banner)]);
        }, ct);

    /// <summary>The runs of an MBOM, newest first (who ran what against which EBOM; the state at release can be shown from here).</summary>
    public Task<Outcome<ReconciliationHistory>> GetReconciliationHistoryAsync(Subject subject, string number, int limit, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ReconciliationHistory>(subject, number, null, "mbom.reconciliation_history", correlationId, async (conn, tx, head) =>
        {
            var runs = new List<ReconciliationRunInfo>();
            var ids = new List<Guid>();
            await using (var cmd = new NpgsqlCommand("SELECT id FROM mbom.reconciliation_run WHERE mbom_id = @m ORDER BY run_at DESC, id DESC LIMIT @l", conn, tx))
            {
                cmd.Parameters.AddWithValue("m", head.Id);
                cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 200));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
            }
            foreach (var id in ids)
            {
                var run = await LoadRunAsync(conn, tx, id, ct);
                string rev; int it;
                await using (var cmd = new NpgsqlCommand("SELECT r.revision, i.iteration FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id WHERE i.id = @i", conn, tx))
                {
                    cmd.Parameters.AddWithValue("i", run!.IterationId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    (rev, it) = (r.GetString(0), r.GetInt32(1));
                }
                var integrity = await RunHashAsync(conn, tx, id, ct) == run.Sha256;
                runs.Add(new ReconciliationRunInfo(run.Id, head.Number, rev, it, run.Status, ReconcileStatuses.Label(run.Status), run.Spec, await MbomStore.IterationLabelAsync(conn, tx, run.EbomIteration, ct) ?? "",
                    run.Derived is { } d ? await MbomStore.IterationLabelAsync(conn, tx, d, ct) : null, run.EbomLines, run.MbomLines, run.Covered, run.Coverage, run.Errors, run.Warnings, run.Infos, 0,
                    run.RunAt, run.RunBy, run.DurationMs, run.Sha256, integrity));
            }
            return (Outcome<ReconciliationHistory>.Ok(new ReconciliationHistory(runs, head.Markings.BannerText())), [("runs", Fmt(runs.Count))]);
        }, ct);
}
