using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Compliance;

public sealed record ExportFile(string Content, string ContentType, string FileName);

/// <summary>
/// The compliance roll-up of an assembly (ADR-0040). The host reads the structure through the normal BOM read paths (which redact what the caller may not read) and hands
/// the readable lines here; this service reads the declarations of exactly those parts again under the caller's own read filter, evaluates each current declaration against
/// the regulations in force (or the list versions asked for), and derives a standing per line, per regulation and for the assembly. A part without a current declaration is
/// a gap, never silently compliant. Lines the caller cannot read are not in the structure, are not counted and cannot change any status or total that the caller sees.
/// </summary>
public sealed class ComplianceReportService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private sealed record PartInfo(Guid Id, string Number);

    public async Task<Outcome<ComplianceReport>> BuildAsync(Subject subject, ReportRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) return Outcome<ComplianceReport>.Forbidden([AccessPolicy.RoleMissing]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var asOf = request.AsOf ?? ComplianceStore.Today(clock);
        var ctx = await ComplianceEvaluator.ContextAsync(conn, null, clock, asOf, request.ListVersions, ct);
        var missing = (request.ListVersions?.Keys ?? Enumerable.Empty<string>()).Where(c => ctx.Versions.All(v => v.Code != c)).ToList();
        if (missing.Count > 0) return Outcome<ComplianceReport>.Invalid(missing.Select(c => $"The list {c} has no such published version."));

        // The parts the caller may read, by the same filter as every other list over parts.
        var numbers = request.Lines.Select(l => l.Number).Distinct(StringComparer.Ordinal).ToArray();
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var parts = new Dictionary<string, PartInfo>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, m.number FROM plm.part_master m WHERE {filter.Sql} AND m.number = ANY(@numbers)", conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("numbers", numbers);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) parts[r.GetString(1)] = new PartInfo(r.GetGuid(0), r.GetString(1));
        }

        var rows = new List<DeclarationRow>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ComplianceStore.DeclarationSelect()} FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE {filter.Sql} AND m.number = ANY(@numbers) AND d.status <> 'withdrawn' ORDER BY d.declared_on DESC, d.number DESC", conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("numbers", numbers);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ComplianceStore.ReadDeclaration(r));
        }
        var lineRows = await ComplianceStore.LoadLinesAsync(conn, null, [.. rows.Select(r => r.Id)], ct);
        var compositions = await ComplianceStore.LoadMaterialCompositionsAsync(conn, null, [.. lineRows.Values.SelectMany(l => l).Where(l => l.MaterialId is not null).Select(l => l.MaterialId!.Value).Distinct()], ct);
        var expanded = rows.ToDictionary(r => r.Id, r => ComplianceEvaluator.Expand(lineRows[r.Id], r.ItemMassMg, compositions));
        var evaluated = rows.ToDictionary(r => r.Id, r => ComplianceEvaluator.EvaluateAll(ctx, expanded[r.Id]));
        var substances = await LoadSubstancesAsync(conn, [.. expanded.Values.SelectMany(e => e).Select(e => e.SubstanceId).Distinct()], ct);
        var byPart = rows.GroupBy(r => r.PartNumber, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var report = new List<ReportLine>();
        foreach (var line in request.Lines)
        {
            if (!parts.ContainsKey(line.Number)) continue; // not readable (or not a part any more): left out, not counted
            var required = !line.HasChildren;
            var declarations = byPart.GetValueOrDefault(line.Number) ?? [];
            var covering = declarations.Where(d => ComplianceRules.Covers(d.RevisionScope, line.Revision)).ToList();
            var current = covering.Where(d => d.ExpiresOn >= ctx.Today).ToList();
            var currentView = declarations.Select(d => new CurrentDeclaration(d.Number, d.RevisionScope, d.ExpiresOn, d.Status,
                evaluated[d.Id].ToDictionary(kv => kv.Key, kv => kv.Value.Result, StringComparer.Ordinal))).ToList();
            var standings = new Dictionary<string, LineStanding>(StringComparer.Ordinal);
            foreach (var v in ctx.Versions)
            {
                if (!required) { standings[v.Code] = new LineStanding(Standings.NotRequired, []); continue; }
                var standing = ComplianceRules.PartStanding(currentView, v.Code, line.Revision, ctx.Today);
                var findings = current.SelectMany(d => ComplianceEvaluator.ToViews(ctx, d.Number, evaluated[d.Id]).Where(f => f.ListCode == v.Code))
                    .Where(f => f.Reason != FindingReasons.ConflictFree).ToList();
                standings[v.Code] = new LineStanding(standing, findings);
            }
            var composition = new List<CompositionRow>();
            var minerals = new Dictionary<string, string>(StringComparer.Ordinal);
            if (required)
            {
                foreach (var d in current)
                    foreach (var e in expanded[d.Id])
                        if (substances.TryGetValue(e.SubstanceId, out var s))
                        {
                            var direct = lineRows[d.Id].FirstOrDefault(l => l.Ordinal == e.Ordinal && l.SubstanceId == e.SubstanceId);
                            composition.Add(new CompositionRow(d.Number, s.Number, s.Cas, s.Name, ComplianceRules.RoundPercent(e.Percent), direct?.MassMg, e.ExemptionCode, s.Mineral, e.ConflictFree));
                        }
                foreach (var m in Minerals.All)
                {
                    var hits = composition.Where(c => c.Mineral == m && c.Percent > 0).ToList();
                    minerals[m] = current.Count == 0 ? "no_declaration" : hits.Count == 0 ? "none"
                        : hits.Any(h => h.ConflictFree == false) ? "not_conflict_free" : hits.Any(h => h.ConflictFree is null) ? "unknown" : "conflict_free";
                }
            }
            var latest = current.FirstOrDefault();
            report.Add(new ReportLine(line.Level, line.Path, line.Number, line.Name, line.Revision, line.Quantity, line.Unit, required, standings, [.. current.Select(d => d.Number)], minerals, composition,
                latest?.SupplierRef, latest?.MpnRef, latest?.DeclaredOn, latest?.ExpiresOn));
        }

        var summaries = new List<RegulationSummary>();
        foreach (var v in ctx.Versions)
        {
            var req = report.Where(l => l.Required).ToList();
            int Count(string s) => req.Count(l => l.Standings[v.Code].Standing == s);
            var causes = req.Where(l => l.Standings[v.Code].Standing is Standings.NonCompliant or Standings.Gap or Standings.Expired or Standings.Unverified)
                .OrderBy(l => l.Standings[v.Code].Standing == Standings.NonCompliant ? 0 : 1).ThenBy(l => l.Path, StringComparer.Ordinal)
                .Select(l => new ReportCause(l.Path, l.Number, l.Name, l.Standings[v.Code].Standing, l.Standings[v.Code].Findings.Where(f => FindingReasons.IsViolation(f.Reason) || f.Reason == FindingReasons.ConflictUnknown).ToList()))
                .ToList();
            summaries.Add(new RegulationSummary(v.Code, v.Name, v.Kind, v.Version, ComplianceRules.AssemblyStanding(req.Select(l => l.Standings[v.Code].Standing)),
                Count(Standings.Compliant), Count(Standings.Exempt), Count(Standings.NonCompliant), Count(Standings.Unverified), Count(Standings.Gap), Count(Standings.Expired), report.Count(l => !l.Required), causes));
        }
        var regulations = ctx.Versions.Select(v => new ReportRegulation(v.Code, v.Name, v.Kind, v.Version, v.EffectiveFrom)).ToList();
        var result = new ComplianceReport(request.Mode, request.Number, request.Name, request.Banner, asOf, regulations, report, summaries);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, ComplianceStore.Entry(subject, "compliance.rollup", AuditOutcome.Success, request.Mode == "mbom" ? "mbom" : ComplianceStore.PartObjectType, request.Number, correlationId,
            ("mode", request.Mode), ("lines", report.Count.ToString(CultureInfo.InvariantCulture)), ("regulations", regulations.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ComplianceReport>.Ok(result);
    }

    private sealed record SubstanceInfo(string Number, string Cas, string Name, string? Mineral);

    private static async Task<Dictionary<Guid, SubstanceInfo>> LoadSubstancesAsync(NpgsqlConnection conn, Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SubstanceInfo>();
        if (ids.Length == 0) return result;
        await using var cmd = new NpgsqlCommand("SELECT id, number, cas_number, name, mineral FROM cmp.substance WHERE id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetGuid(0)] = new SubstanceInfo(r.GetString(1), r.GetString(2), r.GetString(3), ComplianceStore.Str(r, 4));
        return result;
    }

    /// <summary>The report as a file: <c>cmrt</c> (conflict mineral reporting, CSV) or <c>ipc1752</c> (material declaration, XML). Audited with counts only.</summary>
    public async Task<Outcome<ExportFile>> ExportAsync(Subject subject, ReportRequest request, string format, string? correlationId, CancellationToken ct = default)
    {
        if (format is not (ExportFormats.Cmrt or ExportFormats.Ipc1752)) return Outcome<ExportFile>.Invalid(["The format is cmrt or ipc1752."]);
        var built = await BuildAsync(subject, request, correlationId, ct);
        if (!built.IsOk) return new Outcome<ExportFile>(built.Kind, default, built.Errors);
        var report = built.Value!;
        var file = format == ExportFormats.Cmrt ? ComplianceExport.Cmrt(report) : ComplianceExport.Ipc1752(report);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, ComplianceStore.Entry(subject, "compliance.export", AuditOutcome.Success, request.Mode == "mbom" ? "mbom" : ComplianceStore.PartObjectType, request.Number, correlationId,
            ("format", format), ("lines", report.Lines.Count(l => l.Required).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ExportFile>.Ok(file);
    }
}
