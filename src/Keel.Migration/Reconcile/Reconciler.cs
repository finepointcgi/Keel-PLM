using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Audit;
using Keel.Core;
using Keel.Migration.Mapping;
using Npgsql;

namespace Keel.Migration.Reconcile;

public sealed class ReconcileOptions
{
    /// <summary>Restrict the check to one batch's id-map rows; otherwise every object of the source system that the package maps.</summary>
    public Guid? BatchId { get; set; }
    /// <summary>Read every vault object back and verify its hash (slow); otherwise presence and size are checked.</summary>
    public bool DeepVault { get; set; }
}

public sealed class ReconcileCheck
{
    public string Name { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public bool Ok { get; set; }
    public List<string> Details { get; set; } = [];
}

public sealed class ReconcileReport
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? BatchId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public List<ReconcileCheck> Checks { get; set; } = [];
    public int Differences => Checks.Count(c => !c.Ok);
    public bool Ok => Differences == 0;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static ReconcileReport FromJson(string json) => JsonSerializer.Deserialize<ReconcileReport>(json, Options) ?? throw new JsonException("Empty report.");

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Migration reconciliation").AppendLine();
        sb.AppendLine("> CUI-handling artifact: lists source ids and part numbers.").AppendLine();
        sb.AppendLine($"- Source system: {SourceSystem}").AppendLine($"- Package SHA-256: `{PackageSha256}`").AppendLine($"- Batch: {(BatchId is null ? "(none found)" : BatchId.ToString())}");
        sb.AppendLine($"- Result: **{(Ok ? "RECONCILED" : $"{Differences} DIFFERENCES")}**").AppendLine();
        sb.AppendLine("| Check | Source | Keel | Result |").AppendLine("|---|---:|---:|---|");
        foreach (var c in Checks) sb.AppendLine($"| {c.Name} | {c.Expected} | {c.Actual} | {(c.Ok ? "ok" : "DIFFERENT")} |");
        foreach (var c in Checks.Where(c => c.Details.Count > 0))
        {
            sb.AppendLine().AppendLine($"### {c.Name}").AppendLine();
            foreach (var d in c.Details.Take(50)) sb.AppendLine("- " + d);
            if (c.Details.Count > 50) sb.AppendLine($"- ... and {c.Details.Count - 50} more");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Reconciliation after a load (ADR-0023): the package is read and mapped again and compared with what is in Keel. Because the source side is
/// recomputed from the package as it is NOW, a package or a file that was altered after the load shows up as a difference, as does any row
/// that was changed in Keel. Compared: counts per entity, BOM structure (direct lines per iteration and an exploded Merkle checksum per
/// assembly), file hashes (and vault presence), lifecycle and markings distributions, id-map completeness, and the audit chain.
/// </summary>
internal sealed class Reconciler(NpgsqlDataSource ds, IContentStore? store, TimeProvider clock, IClassificationLoader? classification = null)
{
    private readonly List<ReconcileCheck> _checks = [];
    private bool _effectivityLoad;
    private bool _classificationLoad;

    private ReconcileCheck Add(string name, object expected, object actual, params string[] details)
    {
        var c = new ReconcileCheck { Name = name, Expected = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "", Actual = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "", Details = [.. details] };
        c.Ok = c.Expected == c.Actual && details.Length == 0;
        _checks.Add(c);
        return c;
    }

    public async Task<ReconcileReport> RunAsync(PreparedMigration p, ReconcileOptions opt, CancellationToken ct)
    {
        var plan = p.Plan;
        _effectivityLoad = p.Config.Effectivity.Load;
        _classificationLoad = p.Config.Classification.Load;
        var system = plan.SourceSystem;
        await using var conn = await ds.OpenConnectionAsync(ct);

        Guid? batch = opt.BatchId;
        if (batch is null)
        {
            await using var c = new NpgsqlCommand("SELECT id FROM mig.batch WHERE source_system = @s ORDER BY started_at DESC LIMIT 1", conn);
            c.Parameters.AddWithValue("s", system);
            batch = await c.ExecuteScalarAsync(ct) as Guid?;
        }

        var ids = new Dictionary<(string, string), Guid>();
        await using (var c = new NpgsqlCommand("SELECT entity_type, source_id, keel_id FROM mig.id_map WHERE source_system = @s", conn))
        {
            c.Parameters.AddWithValue("s", system);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids[(r.GetString(0), r.GetString(1))] = r.GetGuid(2);
        }

        // ---- id-map completeness
        var missing = new List<string>();
        foreach (var part in plan.Parts)
        {
            if (!ids.ContainsKey(("part", part.SourceId))) missing.Add($"part {part.Number}");
            missing.AddRange(part.Revisions.Where(r => !ids.ContainsKey(("part_revision", r.SourceId))).Select(r => $"part revision {part.Number} {r.LegacyLabel}"));
        }
        foreach (var d in plan.Documents)
        {
            if (!ids.ContainsKey(("document", d.SourceId))) missing.Add($"document {d.Number}");
            missing.AddRange(d.Revisions.Where(r => !ids.ContainsKey(("document_revision", r.SourceId))).Select(r => $"document revision {d.Number} {r.LegacyLabel}"));
        }
        missing.AddRange(plan.Changes.Where(c => !ids.ContainsKey(("change", c.SourceId))).Select(c => $"change {c.LegacyNumber}"));
        var expectedMapped = plan.Parts.Count + plan.Parts.Sum(x => x.Revisions.Count) + plan.Documents.Count + plan.Documents.Sum(x => x.Revisions.Count) + plan.Changes.Count;
        Add("id map completeness (planned objects with an id-map row)", expectedMapped, expectedMapped - missing.Count, [.. missing.Take(100)]);

        // objects this batch imported that the package, as it is now, no longer plans (removed, changed to invalid, or altered after the load)
        if (batch is { } batchId)
        {
            var planned = new HashSet<(string, string)>();
            foreach (var part in plan.Parts) { planned.Add(("part", part.SourceId)); foreach (var r in part.Revisions) planned.Add(("part_revision", r.SourceId)); }
            foreach (var d in plan.Documents) { planned.Add(("document", d.SourceId)); foreach (var r in d.Revisions) planned.Add(("document_revision", r.SourceId)); }
            foreach (var c in plan.Changes) planned.Add(("change", c.SourceId));
            var stray = new List<string>();
            await using var c0 = new NpgsqlCommand("SELECT entity_type, source_id FROM mig.id_map WHERE batch_id = @b AND source_system = @s", conn);
            c0.Parameters.AddWithValue("b", batchId);
            c0.Parameters.AddWithValue("s", system);
            await using (var r0 = await c0.ExecuteReaderAsync(ct))
                while (await r0.ReadAsync(ct))
                    if (!planned.Contains((r0.GetString(0), r0.GetString(1)))) stray.Add($"{r0.GetString(0)} {r0.GetString(1)} was imported but the package no longer plans it (changed to invalid, altered or removed)");
            Add("imported objects that the package still plans", "all", stray.Count == 0 ? "all" : $"{stray.Count} missing", [.. stray.Take(100)]);
        }

        var partIds = plan.Parts.Where(x => ids.ContainsKey(("part", x.SourceId))).Select(x => ids[("part", x.SourceId)]).ToArray();
        var docIds = plan.Documents.Where(x => ids.ContainsKey(("document", x.SourceId))).Select(x => ids[("document", x.SourceId)]).ToArray();
        var changeIds = plan.Changes.Where(x => ids.ContainsKey(("change", x.SourceId))).Select(x => ids[("change", x.SourceId)]).ToArray();

        async Task<long> Scalar(string sql, params (string, object)[] ps)
        {
            await using var c = new NpgsqlCommand(sql, conn);
            foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
            return Convert.ToInt64(await c.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture);
        }

        // ---- counts per entity
        long ExpectedParts() => plan.Parts.Count;
        Add("masters (parts)", ExpectedParts(), await Scalar("SELECT count(*) FROM plm.part_master WHERE id = ANY(@ids) AND imported_batch_id IS NOT NULL", ("ids", partIds)));
        Add("part revisions", plan.Parts.Sum(x => x.Revisions.Count), await Scalar("SELECT count(*) FROM plm.part_revision WHERE master_id = ANY(@ids) AND imported_batch_id IS NOT NULL", ("ids", partIds)));
        Add("part iterations", plan.Parts.Sum(x => x.Revisions.Sum(r => r.Iterations.Count)),
            await Scalar("SELECT count(*) FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", partIds)));
        Add("BOM lines", plan.Parts.Sum(x => x.Revisions.Sum(r => r.Iterations.Sum(i => i.Usages.Count))),
            await Scalar("SELECT count(*) FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", partIds)));
        Add("part-document links", plan.Parts.Sum(x => x.Revisions.Sum(r => r.Iterations.Sum(i => i.Links.Count))),
            await Scalar("SELECT count(*) FROM plm.part_document_link l JOIN plm.part_iteration i ON i.id = l.part_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", partIds)));
        Add("documents", plan.Documents.Count, await Scalar("SELECT count(*) FROM plm.document_master WHERE id = ANY(@ids) AND imported_batch_id IS NOT NULL", ("ids", docIds)));
        Add("document revisions", plan.Documents.Sum(x => x.Revisions.Count), await Scalar("SELECT count(*) FROM plm.document_revision WHERE master_id = ANY(@ids) AND imported_batch_id IS NOT NULL", ("ids", docIds)));
        Add("document iterations", plan.Documents.Sum(x => x.Revisions.Sum(r => r.Iterations.Count)),
            await Scalar("SELECT count(*) FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", docIds)));
        Add("files", plan.Documents.Sum(x => x.Revisions.Sum(r => r.Iterations.Sum(i => i.Files.Count))),
            await Scalar("SELECT count(*) FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", docIds)));
        Add("file bytes", plan.Documents.Sum(x => x.Revisions.Sum(r => r.Iterations.Sum(i => i.Files.Sum(f => f.SizeBytes)))),
            await Scalar("SELECT COALESCE(sum(f.size_bytes), 0) FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", ("ids", docIds)));
        Add("change objects", plan.Changes.Count, await Scalar("SELECT count(*) FROM chg.change WHERE id = ANY(@ids) AND imported_batch_id IS NOT NULL", ("ids", changeIds)));
        Add("change affected objects", plan.Changes.Sum(x => x.Affected.Count), await Scalar("SELECT count(*) FROM chg.affected_object WHERE change_id = ANY(@ids)", ("ids", changeIds)));
        Add("workflow instances for imported objects (must be 0)", 0, await Scalar("SELECT count(*) FROM wf.instance w WHERE w.object_id = ANY(@ids)", ("ids", partIds.Concat(docIds).Concat(changeIds).ToArray())));

        await CheckBomAsync(conn, plan, ids, ct);
        if (_classificationLoad) await CheckClassificationAsync(conn, plan, partIds, ct);
        await CheckFilesAsync(conn, plan, docIds, opt.DeepVault, ct);
        await CheckDistributionsAsync(conn, plan, partIds, docIds, changeIds, ct);

        // ---- audit chain
        var verification = await AuditVerifier.VerifyAsync(conn, [], ct);
        Add("audit chain valid", true, verification.Valid, [.. verification.Failures.Take(10)]);
        Add("audit events of the batch (start and end)", batch is null ? "n/a" : "2+", batch is null ? "n/a" : (await Scalar("SELECT count(*) FROM audit.event WHERE correlation_id = @b AND action IN ('migration.batch.start', 'migration.batch.end')", ("b", batch.Value.ToString())) >= 2 ? "2+" : "missing"));

        return new ReconcileReport { CreatedAt = clock.GetUtcNow(), BatchId = batch, SourceSystem = system, PackageSha256 = p.Manifest.Sha256, Checks = _checks };
    }

    // ------------------------------------------------------------------------------------------------ BOM
    private static string Qty(decimal q) => q.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Line(int find, string child, decimal qty, string unit, IEnumerable<string> designators, int alt, EffectivityRange? eff) =>
        $"{find}|{child}|{Qty(qty)}|{unit}|{string.Join(',', designators.Order(StringComparer.Ordinal))}|{alt}|{(eff is null ? "" : $"{eff.Kind}:{eff.Context}:{eff.From}:{eff.To}")}";

    /// <summary>True when a line (as written by <see cref="Line"/>) applies to a unit: no effectivity, or a serial range of the context that covers it.</summary>
    private static bool AppliesTo(string line, string context, long serial)
    {
        var eff = line[(line.LastIndexOf('|') + 1)..];
        if (eff.Length == 0) return true;
        var f = eff.Split(':');
        if (f[0] != EffectivityKinds.Serial || !string.Equals(f[1], context, StringComparison.Ordinal)) return false;
        return (f[2].Length == 0 || long.Parse(f[2], CultureInfo.InvariantCulture) <= serial) && (f[3].Length == 0 || long.Parse(f[3], CultureInfo.InvariantCulture) >= serial);
    }

    /// <summary>The alternate ordinal the importer's insert order gives each line: lines of one find number by range start (open start first).</summary>
    private static List<(PUsage Usage, int Alt)> WithAlternates(IEnumerable<PUsage> usages) =>
        [.. usages.GroupBy(u => u.FindNumber).SelectMany(g => g.OrderBy(u => u.Effectivity?.From is null ? 0 : 1).ThenBy(u => u.Effectivity?.From ?? 0).Select((u, i) => (u, i)))];
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task CheckBomAsync(NpgsqlConnection conn, Plan plan, Dictionary<(string, string), Guid> ids, CancellationToken ct)
    {
        var numberOf = plan.Parts.ToDictionary(p => p.SourceId, p => p.Number, StringComparer.Ordinal);
        // source side: direct lines per (number, keel label, iteration)
        var src = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var srcLatest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in plan.Parts)
        {
            foreach (var r in part.Revisions)
                foreach (var i in r.Iterations)
                    if (i.Usages.Count > 0)
                        src[$"{part.Number}|{r.Label}|{i.Sequence}"] = WithAlternates(i.Usages).Select(x => Line(x.Usage.FindNumber, numberOf[x.Usage.ChildSourceId], x.Usage.Quantity, x.Usage.Unit, x.Usage.Designators, x.Alt, x.Usage.Effectivity)).Order(StringComparer.Ordinal).ToList();
            var latest = part.Revisions[^1];
            srcLatest[part.Number] = $"{part.Number}|{latest.Label}|{latest.Iterations.Max(i => i.Sequence)}";
        }
        // Keel side
        var partIds = plan.Parts.Where(x => ids.ContainsKey(("part", x.SourceId))).Select(x => ids[("part", x.SourceId)]).ToArray();
        var dst = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var c = new NpgsqlCommand("""
            SELECT m.number, r.revision, i.iteration, u.find_number, c.number, u.quantity, u.unit, u.reference_designators, u.alt, u.eff_kind, u.eff_context, u.eff_from, u.eff_to
            FROM plm.bom_usage u
            JOIN plm.part_iteration i ON i.id = u.parent_iteration_id
            JOIN plm.part_revision r ON r.id = i.revision_id
            JOIN plm.part_master m ON m.id = r.master_id
            JOIN plm.part_master c ON c.id = u.child_master_id
            WHERE m.id = ANY(@ids) AND i.imported_batch_id IS NOT NULL
            """, conn))
        {
            c.Parameters.AddWithValue("ids", partIds);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var key = $"{rd.GetString(0)}|{rd.GetString(1)}|{rd.GetInt32(2)}";
                if (!dst.TryGetValue(key, out var list)) dst[key] = list = [];
                list.Add(Line(rd.GetInt32(3), rd.GetString(4), rd.GetDecimal(5), rd.GetString(6), rd.GetFieldValue<string[]>(7), rd.GetInt32(8),
                    rd.IsDBNull(9) ? null : new EffectivityRange(rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetInt64(11), rd.IsDBNull(12) ? null : rd.GetInt64(12))));
            }
        }
        var differences = new List<string>();
        foreach (var key in src.Keys.Union(dst.Keys).Order(StringComparer.Ordinal))
        {
            var a = src.GetValueOrDefault(key); var b = dst.GetValueOrDefault(key)?.Order(StringComparer.Ordinal).ToList();
            if (a is null || b is null || !a.SequenceEqual(b)) differences.Add($"{key.Replace('|', ' ')}: source {Hash(string.Join('\n', a ?? []))[..12]} ({a?.Count ?? 0} lines), Keel {Hash(string.Join('\n', b ?? []))[..12]} ({b?.Count ?? 0} lines)");
        }
        Add("BOM direct structure per assembly iteration (checksum compared for each)", src.Count, Math.Max(0, src.Count - differences.Count), [.. differences]);

        // exploded structure per assembly, "as imported" = every master resolved to its latest revision's latest iteration (Merkle hash)
        string? Exploded(Dictionary<string, List<string>> direct, Dictionary<string, string> latestKey, string number, Dictionary<string, string> memo, HashSet<string> path, Func<string, bool>? keep = null)
        {
            if (memo.TryGetValue(number, out var h)) return h;
            if (!path.Add(number)) return "cycle";
            var lines = latestKey.TryGetValue(number, out var key) && direct.TryGetValue(key, out var l) ? l : [];
            var parts = new List<string>();
            foreach (var line in lines.Where(l => keep is null || keep(l)))
            {
                var child = line.Split('|')[1];
                parts.Add(line + "|" + Exploded(direct, latestKey, child, memo, path, keep));
            }
            path.Remove(number);
            return memo[number] = Hash(string.Join('\n', parts.Order(StringComparer.Ordinal)));
        }
        var dstLatest = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var c = new NpgsqlCommand("""
            SELECT m.number, r.revision, r.latest_iteration FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id
            WHERE m.id = ANY(@ids) AND r.is_latest AND r.imported_batch_id IS NOT NULL
            """, conn))
        {
            c.Parameters.AddWithValue("ids", partIds);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) dstLatest[rd.GetString(0)] = $"{rd.GetString(0)}|{rd.GetString(1)}|{rd.GetInt32(2)}";
        }
        var srcMemo = new Dictionary<string, string>(StringComparer.Ordinal);
        var dstMemo = new Dictionary<string, string>(StringComparer.Ordinal);
        var assemblies = plan.Parts.Where(p => p.PartType == "Assembly").Select(p => p.Number).Order(StringComparer.Ordinal).ToList();
        var bad = new List<string>();
        foreach (var number in assemblies)
        {
            var a = Exploded(src, srcLatest, number, srcMemo, []);
            var b = Exploded(dst, dstLatest, number, dstMemo, []);
            if (a != b) bad.Add($"{number}: exploded structure checksum source {a?[..12]} differs from Keel {b?[..12]}");
        }
        Add("BOM exploded structure checksum per assembly (as imported)", assemblies.Count, assemblies.Count - bad.Count, [.. bad]);

        // effectivity: line counts and the as-of-serial explosion at serials sampled from the data (every range boundary and its neighbour)
        var effExpected = plan.Parts.Sum(x => x.Revisions.Sum(r => r.Iterations.Sum(i => i.Usages.Count(u => u.Effectivity is not null))));
        if (effExpected > 0 || _effectivityLoad)
        {
            long effActual;
            await using (var c = new NpgsqlCommand("SELECT count(*) FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = ANY(@ids) AND i.imported_batch_id IS NOT NULL AND u.eff_kind IS NOT NULL", conn))
            {
                c.Parameters.AddWithValue("ids", partIds);
                effActual = Convert.ToInt64(await c.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            Add("BOM lines with effectivity (serial, lot, date ranges)", effExpected, effActual);
            // As-of-serial structures resolve the Released revisions (as the native effectivity spec does); an item without one falls back to its latest revision.
            var srcReleased = new Dictionary<string, string>(srcLatest, StringComparer.Ordinal);
            foreach (var part in plan.Parts)
                if (part.Revisions.LastOrDefault(r => r.State == "Released") is { } rel) srcReleased[part.Number] = $"{part.Number}|{rel.Label}|{rel.Iterations.Max(i => i.Sequence)}";
            var dstReleased = new Dictionary<string, string>(dstLatest, StringComparer.Ordinal);
            await using (var c = new NpgsqlCommand("""
                SELECT DISTINCT ON (m.number) m.number, r.revision, r.latest_iteration FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id
                WHERE m.id = ANY(@ids) AND r.lifecycle_state = 'Released' AND r.imported_batch_id IS NOT NULL ORDER BY m.number, length(r.revision) DESC, r.revision DESC
                """, conn))
            {
                c.Parameters.AddWithValue("ids", partIds);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) dstReleased[rd.GetString(0)] = $"{rd.GetString(0)}|{rd.GetString(1)}|{rd.GetInt32(2)}";
            }
            foreach (var (context, serials) in plan.EffectivitySerials)
                foreach (var serial in serials)
                {
                    var a = new Dictionary<string, string>(StringComparer.Ordinal);
                    var b = new Dictionary<string, string>(StringComparer.Ordinal);
                    bool Keep(string line) => AppliesTo(line, context, serial);
                    var aHash = Hash(string.Join('\n', assemblies.Select(n => n + "=" + Exploded(src, srcReleased, n, a, [], Keep))));
                    var bHash = Hash(string.Join('\n', assemblies.Select(n => n + "=" + Exploded(dst, dstReleased, n, b, [], Keep))));
                    Add($"as-of-serial explosion checksum (context {context}, serial {serial}) over {assemblies.Count} assemblies", aHash[..12], bHash[..12]);
                }
        }
    }

    // ------------------------------------------------------------------------------------------------ classification
    /// <summary>Canonical text of class values (sorted keys, numbers without trailing zeros) so the package's JSON and the stored jsonb compare equal.</summary>
    private static string Canon(string json)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        return string.Join(';', o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value switch
        {
            System.Text.Json.Nodes.JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString("0.############", CultureInfo.InvariantCulture),
            System.Text.Json.Nodes.JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
            var other => other?.GetValue<string>() ?? "",
        }));
    }

    private async Task CheckClassificationAsync(NpgsqlConnection conn, Plan plan, Guid[] partIds, CancellationToken ct)
    {
        var loader = classification ?? throw new InvalidOperationException("classification.load needs a classification loader.");
        // source side: (part number, Keel revision label, iteration) -> class and values
        var src = new Dictionary<string, (string Class, string Values, int Count)>(StringComparer.Ordinal);
        foreach (var part in plan.Parts)
            foreach (var r in part.Revisions)
                foreach (var i in r.Iterations.Where(i => i.ClassCode is not null))
                    src[$"{part.Number}|{r.Label}|{i.Sequence}"] = (i.ClassCode!, Canon(i.ClassAttributesJson!), System.Text.Json.Nodes.JsonNode.Parse(i.ClassAttributesJson!)!.AsObject().Count);
        // Keel side
        var keys = new Dictionary<Guid, string>();
        await using (var c = new NpgsqlCommand("SELECT i.id, m.number, r.revision, i.iteration FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.id = ANY(@ids) AND i.imported_batch_id IS NOT NULL", conn))
        {
            c.Parameters.AddWithValue("ids", partIds);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) keys[rd.GetGuid(0)] = $"{rd.GetString(1)}|{rd.GetString(2)}|{rd.GetInt32(3)}";
        }
        var stored = await loader.ReadAsync(conn, keys.Keys, ct);
        var dst = stored.ToDictionary(x => keys[x.IterationId], x => (Class: x.ClassCode, Values: Canon(x.AttributesJson), Count: x.ValueRows), StringComparer.Ordinal);

        Add("classified part iterations", src.Count, dst.Count);
        var perClassSrc = src.Values.GroupBy(v => v.Class).ToDictionary(g => g.Key, g => (long)g.Count());
        var perClassDst = dst.Values.GroupBy(v => v.Class).ToDictionary(g => g.Key, g => (long)g.Count());
        var classDiffs = perClassSrc.Keys.Union(perClassDst.Keys).Where(k => perClassSrc.GetValueOrDefault(k) != perClassDst.GetValueOrDefault(k))
            .Select(k => $"class {k}: source {perClassSrc.GetValueOrDefault(k)}, Keel {perClassDst.GetValueOrDefault(k)}").Order(StringComparer.Ordinal).ToArray();
        Add("class assignments per class (iterations)", src.Count, dst.Count, classDiffs);
        Add("class attribute values (typed rows)", src.Values.Sum(v => v.Count), dst.Values.Sum(v => v.Count));
        var diffs = new List<string>();
        foreach (var k in src.Keys.Union(dst.Keys).Order(StringComparer.Ordinal))
        {
            var a = src.TryGetValue(k, out var x); var b = dst.TryGetValue(k, out var y);
            if (!a || !b || x.Class != y.Class || x.Values != y.Values)
                diffs.Add($"{k.Replace('|', ' ')}: source {(a ? Hash(x.Class + "|" + x.Values)[..12] : "-")} ({(a ? x.Class : "none")}), Keel {(b ? Hash(y.Class + "|" + y.Values)[..12] : "-")} ({(b ? y.Class : "none")})");
        }
        Add("class and values per iteration (checksum compared for each; values hash)", src.Count, Math.Max(0, src.Count - diffs.Count), [.. diffs]);
        var wanted = plan.Classifications.Where(c => c.KeelClassCode is not null).Select(c => c.KeelClassCode!).Concat(plan.ClassesToCreate.Select(c => c.Code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = (await loader.LoadDefinitionsAsync(conn, null, ct)).Select(d => d.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = wanted.Where(c => !existing.Contains(c)).ToArray();
        Add("classes used or created exist in Keel", wanted.Count, wanted.Count - missing.Length, [.. missing.Select(m => $"class {m} does not exist")]);
    }

    // ------------------------------------------------------------------------------------------------ files
    private async Task CheckFilesAsync(NpgsqlConnection conn, Plan plan, Guid[] docIds, bool deep, CancellationToken ct)
    {
        var src = new Dictionary<string, (string Sha, long Size)>(StringComparer.Ordinal);
        foreach (var d in plan.Documents)
            foreach (var r in d.Revisions)
                foreach (var i in r.Iterations)
                    foreach (var f in i.Files) src[$"{d.Number}|{r.Label}|{i.Sequence}|{f.Name.ToLowerInvariant()}"] = (f.Sha256, f.SizeBytes);
        var dst = new Dictionary<string, (string Sha, long Size)>(StringComparer.Ordinal);
        await using (var c = new NpgsqlCommand("""
            SELECT m.number, r.revision, i.iteration, f.file_name, f.sha256, f.size_bytes
            FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id JOIN plm.document_master m ON m.id = r.master_id
            WHERE m.id = ANY(@ids) AND i.imported_batch_id IS NOT NULL
            """, conn))
        {
            c.Parameters.AddWithValue("ids", docIds);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) dst[$"{rd.GetString(0)}|{rd.GetString(1)}|{rd.GetInt32(2)}|{rd.GetString(3).ToLowerInvariant()}"] = (rd.GetString(4), rd.GetInt64(5));
        }
        var diffs = new List<string>();
        foreach (var k in src.Keys.Union(dst.Keys).Order(StringComparer.Ordinal))
            if (!src.TryGetValue(k, out var a) || !dst.TryGetValue(k, out var b) || a != b)
                diffs.Add($"{k.Replace('|', ' ')}: source sha256 {(src.TryGetValue(k, out var x) ? x.Sha[..12] : "-")}, Keel {(dst.TryGetValue(k, out var y) ? y.Sha[..12] : "-")}");
        Add("file SHA-256 equality (package as it is now vs Keel)", src.Count, src.Count - diffs.Count, [.. diffs]);

        if (store is not null)
        {
            var vaultDiffs = new List<string>();
            foreach (var (sha, size) in dst.Values.Distinct())
            {
                try
                {
                    if (deep) await store.VerifyAsync(sha, size, ct);
                    else if (!await store.ExistsAsync(sha, size, ct)) vaultDiffs.Add($"vault object {sha[..12]} ({size} bytes) is missing");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    vaultDiffs.Add($"vault object {sha[..12]}: {e.Message}");
                }
            }
            var distinct = dst.Values.Distinct().Count();
            Add(deep ? "vault objects present and hash-verified" : "vault objects present with the recorded size", distinct, distinct - vaultDiffs.Count, [.. vaultDiffs]);
        }
    }

    // ------------------------------------------------------------------------------------------------ distributions
    private async Task CheckDistributionsAsync(NpgsqlConnection conn, Plan plan, Guid[] partIds, Guid[] docIds, Guid[] changeIds, CancellationToken ct)
    {
        async Task<SortedDictionary<string, long>> States(string sql, Guid[] ids)
        {
            var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
            await using var c = new NpgsqlCommand(sql, conn);
            c.Parameters.AddWithValue("ids", ids);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetInt64(1);
            return d;
        }
        static SortedDictionary<string, long> Group(IEnumerable<string> keys)
        {
            var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var k in keys) d[k] = d.GetValueOrDefault(k) + 1;
            return d;
        }
        void Compare(string name, SortedDictionary<string, long> a, SortedDictionary<string, long> b)
        {
            var diffs = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).Select(k => $"{k}: source {a.GetValueOrDefault(k)}, Keel {b.GetValueOrDefault(k)}").ToArray();
            Add(name, a.Values.Sum(), b.Values.Sum(), diffs);
        }
        Compare("lifecycle state distribution: part revisions", Group(plan.Parts.SelectMany(p => p.Revisions.Select(r => r.State))),
            await States("SELECT lifecycle_state, count(*) FROM plm.part_revision WHERE master_id = ANY(@ids) AND imported_batch_id IS NOT NULL GROUP BY 1", partIds));
        Compare("lifecycle state distribution: document revisions", Group(plan.Documents.SelectMany(p => p.Revisions.Select(r => r.State))),
            await States("SELECT lifecycle_state, count(*) FROM plm.document_revision WHERE master_id = ANY(@ids) AND imported_batch_id IS NOT NULL GROUP BY 1", docIds));
        Compare("lifecycle state distribution: change objects", Group(plan.Changes.Select(c => c.State)),
            await States("SELECT lifecycle_state, count(*) FROM chg.change WHERE id = ANY(@ids) AND imported_batch_id IS NOT NULL GROUP BY 1", changeIds));

        static string Key(Markings m) => $"{m.ExportJurisdiction}|{m.Eccn}|{m.UsmlCategory}|{string.Join(',', m.CuiCategories)}|{m.DistributionStatement}|{m.ControllingOffice}|{m.DistributionReason}|{m.Program}|{m.OwningOrg}|{string.Join(',', m.ExportAuthorizations)}";
        var expected = Group(plan.Parts.Select(p => Key(p.Markings)).Concat(plan.Documents.Select(d => Key(d.Markings))).Concat(plan.Changes.Select(c => Key(c.Markings))));
        var actual = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, ids) in new[] { ("plm.part_master", partIds), ("plm.document_master", docIds), ("chg.change", changeIds) })
        {
#pragma warning disable CA2100 // table names come from the fixed list above
            await using var c = new NpgsqlCommand($"SELECT export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations FROM {table} WHERE id = ANY(@ids) AND imported_batch_id IS NOT NULL", conn);
#pragma warning restore CA2100
            c.Parameters.AddWithValue("ids", ids);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var k = $"{S(0)}|{S(1)}|{S(2)}|{string.Join(',', r.GetFieldValue<string[]>(3).Order(StringComparer.Ordinal))}|{S(4)}|{S(5)}|{S(6)}|{S(7)}|{S(8)}|{string.Join(',', r.GetFieldValue<string[]>(9).Order(StringComparer.Ordinal))}";
                actual[k] = actual.GetValueOrDefault(k) + 1;
            }
        }
        Compare("markings distribution (every object, every marking field)", expected, actual);
    }
}
