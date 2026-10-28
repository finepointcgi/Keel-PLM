using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Migration.Canonical;
using Keel.Migration.Load;
using Keel.Migration.Mapping;
using Keel.Migration.Sources;

namespace Keel.Migration.Reports;

public sealed class FileChecks
{
    public long Referenced { get; set; }
    public long Planned { get; set; }
    public long PlannedBytes { get; set; }
    public long Missing { get; set; }
    public long ShaMismatch { get; set; }
    public long SizeMismatch { get; set; }
    public long ShaComputedNotDeclared { get; set; }
}

public sealed class QuarantinedObject
{
    public string EntityType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class EntityResult
{
    public long Created { get; set; }
    public long Skipped { get; set; }
}

/// <summary>
/// The dry-run and load report. It contains counts, source ids, numbers, codes and mapping decisions, never object names or descriptions
/// (property values appear only truncated inside problem messages). Report FILES are nevertheless CUI-handling artifacts: they list which
/// objects exist, which markings were defaulted, and which properties the source carries (ADR-0023).
/// </summary>
public sealed class MigrationReport
{
    public const int SchemaVersion = 1;
    public int Schema { get; set; } = SchemaVersion;
    public string Kind { get; set; } = "dry-run";
    public string Tool { get; set; } = "keel-import";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? BatchId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string Format { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string MappingSha256 { get; set; } = "";
    public string? MappingName { get; set; }
    public bool Quarantine { get; set; }
    public string AuditGranularity { get; set; } = "batch";
    public string ReferenceData { get; set; } = "";
    public bool Succeeded { get; set; }
    public List<string> Blocking { get; set; } = [];
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public SourceInventory Inventory { get; set; } = new();
    public SortedDictionary<string, long> Planned { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, EntityResult> Results { get; set; } = new(StringComparer.Ordinal);
    public List<Problem> Problems { get; set; } = [];
    public List<DefaultedMarking> DefaultedMarkings { get; set; } = [];
    public List<QuarantinedObject> Quarantined { get; set; } = [];
    public SortedDictionary<string, long> UnmappedProperties { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> UserFallbacks { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Decisions { get; set; } = new(StringComparer.Ordinal);
    public List<RevisionMapEntry> RevisionMaps { get; set; } = [];
    public SortedDictionary<string, SortedDictionary<string, long>> StateDistribution { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> MarkingsDistribution { get; set; } = new(StringComparer.Ordinal);
    public FileChecks Files { get; set; } = new();
    /// <summary>Classes the load creates from the staged hierarchy ("code (under parent)"), parents first; empty unless classification.load.</summary>
    public List<string> ClassesToCreate { get; set; } = [];
    /// <summary>Per Keel class: part iterations that carry it after the load.</summary>
    public SortedDictionary<string, long> ClassIterations { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Serial numbers per context sampled for the reconciliation of the as-of-serial explosion.</summary>
    public SortedDictionary<string, List<long>> EffectivitySerials { get; set; } = new(StringComparer.Ordinal);
    public long EstimatedRows { get; set; }
    public long EstimatedUploadBytes { get; set; }
    public double EstimatedLoadSeconds { get; set; }
    public double ElapsedSeconds { get; set; }
    public long AuditEventsWritten { get; set; }
    public string Hash { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Hash over what identifies the outcome of validating THIS package with THIS mapping: kind-independent inputs, planned counts, every problem
    /// and every quarantined or defaulted object. Times, durations and database state are excluded, so the same inputs always give the same hash.
    /// </summary>
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        sb.Append(SourceSystem).Append('|').Append(PackageSha256).Append('|').Append(MappingSha256).Append('|').Append(Quarantine).Append('|').Append(AuditGranularity).Append('\n');
        foreach (var (k, v) in Planned) sb.Append(k).Append('=').Append(v).Append(';');
        sb.Append('\n');
        foreach (var p in Problems.Where(p => !p.Code.StartsWith("LOAD-", StringComparison.Ordinal)).Select(p => $"{p.Severity}|{p.Code}|{p.EntityType}|{p.SourceId}").Order(StringComparer.Ordinal)) sb.Append(p).Append('\n');
        foreach (var q in Quarantined.Where(q => !q.Code.StartsWith("LOAD-", StringComparison.Ordinal)).Select(q => $"{q.EntityType}|{q.SourceId}|{q.Code}").Order(StringComparer.Ordinal)) sb.Append('Q').Append(q).Append('\n');
        foreach (var d in DefaultedMarkings.Select(d => $"{d.EntityType}|{d.SourceId}|{d.Banner}").Order(StringComparer.Ordinal)) sb.Append('D').Append(d).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public bool HashIsValid() => string.Equals(Hash, ComputeHash(), StringComparison.Ordinal);

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static MigrationReport FromJson(string json) => JsonSerializer.Deserialize<MigrationReport>(json, Json) ?? throw new JsonException("Empty report.");

    public string ToCsv()
    {
        var sb = new StringBuilder("severity,code,entityType,sourceId,message,hint\n");
        foreach (var p in Problems) sb.AppendJoin(',', Csv(p.Severity.ToString()), Csv(p.Code), Csv(p.EntityType), Csv(p.SourceId), Csv(p.Message), Csv(p.Hint ?? "")).Append('\n');
        return sb.ToString();
    }

    private static string Csv(string s)
    {
        // Spreadsheet formula injection: values starting with = + - @ are prefixed so they cannot execute when opened.
        var v = s.Length > 0 && s[0] is '=' or '+' or '-' or '@' ? "'" + s : s;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Migration report: {Kind}").AppendLine();
        sb.AppendLine("> This report is a CUI-handling artifact: it lists source ids, part numbers and markings decisions. Store and share it like the package.").AppendLine();
        sb.AppendLine($"- Source system: {SourceSystem} (format {Format})");
        sb.AppendLine($"- Package SHA-256: `{PackageSha256}`").AppendLine($"- Mapping SHA-256: `{MappingSha256}`{(MappingName is null ? "" : $" ({MappingName})")}");
        sb.AppendLine($"- Reference data: {ReferenceData}");
        sb.AppendLine($"- Quarantine mode: {(Quarantine ? "on" : "off")}; audit granularity: {AuditGranularity}");
        sb.AppendLine($"- Result: **{(Succeeded ? "SUCCEEDED" : "BLOCKED")}**, {ErrorCount} errors, {WarningCount} warnings");
        if (BatchId is not null) sb.AppendLine($"- Batch: `{BatchId}`");
        sb.AppendLine($"- Report hash: `{Hash}`").AppendLine();
        if (Blocking.Count > 0) { sb.AppendLine("## Blocking").AppendLine(); foreach (var b in Blocking) sb.AppendLine("- " + b); sb.AppendLine(); }

        void Table(string title, IEnumerable<KeyValuePair<string, long>> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            sb.AppendLine($"## {title}").AppendLine().AppendLine("| Name | Count |").AppendLine("|---|---:|");
            foreach (var (k, v) in list) sb.AppendLine($"| {k.Replace("|", "\\|")} | {v} |");
            sb.AppendLine();
        }
        Table("Source inventory", Inventory.Counts);
        Table("Planned (after mapping and validation)", Planned);
        if (Results.Count > 0)
        {
            sb.AppendLine("## Results").AppendLine().AppendLine("| Entity | Created | Skipped (already imported) |").AppendLine("|---|---:|---:|");
            foreach (var (k, v) in Results) sb.AppendLine($"| {k} | {v.Created} | {v.Skipped} |");
            sb.AppendLine();
        }
        Table("Source states: items", Inventory.ItemStates);
        Table("Source states: documents", Inventory.DocumentStates);
        Table("Source states: changes", Inventory.ChangeStates);
        Table("Source units", Inventory.Units);
        Table("Source item types", Inventory.ItemTypes);
        Table("Source users", Inventory.Users);
        Table("Unmapped properties (kept in legacy_properties)", UnmappedProperties);
        Table("Users replaced by the fallback user", UserFallbacks);
        Table("Mapping decisions", Decisions);
        if (ClassesToCreate.Count > 0)
        {
            sb.AppendLine("## Classes that would be created").AppendLine();
            foreach (var c in ClassesToCreate) sb.AppendLine("- " + c);
            sb.AppendLine();
        }
        Table("Classified part iterations per class (planned)", ClassIterations);
        if (EffectivitySerials.Count > 0)
        {
            sb.AppendLine("## Serial numbers sampled for the as-of-serial reconciliation").AppendLine();
            foreach (var (ctx, serials) in EffectivitySerials) sb.AppendLine($"- {ctx}: {string.Join(", ", serials)}");
            sb.AppendLine();
        }
        Table("Markings distribution (planned)", MarkingsDistribution);
        foreach (var (k, v) in StateDistribution) Table($"Lifecycle states (planned): {k}", v);
        if (Inventory.UnknownElements.Count > 0)
        {
            sb.AppendLine("## Unknown source elements").AppendLine().AppendLine("| Element | Count | First seen | Preserved |").AppendLine("|---|---:|---|---|");
            foreach (var (k, v) in Inventory.UnknownElements) sb.AppendLine($"| {k} | {v.Count} | {v.FirstPath} | {(v.Preserved ? "yes, as properties" : "counted only")} |");
            sb.AppendLine();
        }
        Table("Ignored (recognized, not migrated)", Inventory.IgnoredElements);
        sb.AppendLine("## Files").AppendLine();
        sb.AppendLine($"Referenced {Files.Referenced}; planned {Files.Planned} ({Files.PlannedBytes} bytes); missing {Files.Missing}; SHA-256 mismatch {Files.ShaMismatch}; size mismatch {Files.SizeMismatch}; SHA-256 computed because the source gave none {Files.ShaComputedNotDeclared}.").AppendLine();
        sb.AppendLine("## Estimate").AppendLine();
        sb.AppendLine($"About {EstimatedRows} rows and {EstimatedUploadBytes} bytes of files; roughly {EstimatedLoadSeconds:F0} s (derived from the simulated load; real time depends on the vault).").AppendLine();
        if (DefaultedMarkings.Count > 0)
        {
            sb.AppendLine("## Defaulted markings (review required)").AppendLine().AppendLine("| Type | Source id | Number | Applied markings |").AppendLine("|---|---|---|---|");
            foreach (var d in DefaultedMarkings) sb.AppendLine($"| {d.EntityType} | {d.SourceId} | {d.Number} | {d.Banner} |");
            sb.AppendLine();
        }
        if (Quarantined.Count > 0)
        {
            sb.AppendLine("## Quarantined (not imported)").AppendLine().AppendLine("| Type | Source id | Code | Reason |").AppendLine("|---|---|---|---|");
            foreach (var q in Quarantined) sb.AppendLine($"| {q.EntityType} | {q.SourceId} | {q.Code} | {q.Reason.Replace("|", "\\|")} |");
            sb.AppendLine();
        }
        if (RevisionMaps.Count > 0)
        {
            sb.AppendLine("## Revision label mapping").AppendLine().AppendLine("| Item | Source label | Keel label | Position |").AppendLine("|---|---|---|---:|");
            foreach (var r in RevisionMaps) sb.AppendLine($"| {r.ItemNumber} | {r.SourceLabel} | {r.KeelLabel} | {r.Position} |");
            sb.AppendLine();
        }
        sb.AppendLine("## Problems").AppendLine();
        if (Problems.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Severity | Code | Type | Source id | Message | Fix hint |").AppendLine("|---|---|---|---|---|---|");
            foreach (var p in Problems.OrderByDescending(p => p.Severity).ThenBy(p => p.Code, StringComparer.Ordinal))
                sb.AppendLine($"| {p.Severity} | {p.Code} | {p.EntityType} | {p.SourceId} | {p.Message.Replace("|", "\\|")} | {(p.Hint ?? "").Replace("|", "\\|")} |");
        }
        return sb.ToString();
    }
}

internal static class ReportBuilder
{
    public static MigrationReport Build(PreparedMigration prep, string kind, ImportResult? result, LoadOptions opt, DateTimeOffset now)
    {
        var plan = prep.Plan;
        var r = new MigrationReport
        {
            Kind = kind, CreatedAt = now, BatchId = result?.BatchId, SourceSystem = plan.SourceSystem, Format = prep.Format,
            PackageSha256 = prep.Manifest.Sha256, MappingSha256 = prep.MappingSha256, MappingName = prep.Config.Name, Quarantine = opt.Quarantine,
            AuditGranularity = prep.Config.AuditGranularity, ReferenceData = prep.Reference.FromDatabase ? "target database" : "built-in defaults of migrations 0002/0003/0007 (not checked against a database)",
            Inventory = prep.Sink.Inventory, UnmappedProperties = plan.UnmappedProperties, UserFallbacks = plan.UserFallbacks, Decisions = plan.Decisions,
            RevisionMaps = plan.RevisionMaps, DefaultedMarkings = plan.Defaulted,
        };
        r.Problems = [.. plan.Problems];
        if (result is not null) r.Problems.AddRange(result.Problems);
        r.ErrorCount = r.Problems.Count(p => p.Severity == Severity.Error);
        r.WarningCount = r.Problems.Count(p => p.Severity == Severity.Warning);

        r.Planned["parts"] = plan.Parts.Count;
        r.Planned["partRevisions"] = plan.Parts.Sum(p => p.Revisions.Count);
        r.Planned["partIterations"] = plan.Parts.Sum(p => p.Revisions.Sum(x => x.Iterations.Count));
        r.Planned["bomUsages"] = plan.Parts.Sum(p => p.Revisions.Sum(x => x.Iterations.Sum(i => i.Usages.Count)));
        r.Planned["partDocumentLinks"] = plan.Parts.Sum(p => p.Revisions.Sum(x => x.Iterations.Sum(i => i.Links.Count)));
        r.Planned["documents"] = plan.Documents.Count;
        r.Planned["documentRevisions"] = plan.Documents.Sum(d => d.Revisions.Count);
        r.Planned["documentIterations"] = plan.Documents.Sum(d => d.Revisions.Sum(x => x.Iterations.Count));
        r.Planned["files"] = plan.Documents.Sum(d => d.Revisions.Sum(x => x.Iterations.Sum(i => i.Files.Count)));
        r.Planned["fileBytes"] = plan.Documents.Sum(d => d.Revisions.Sum(x => x.Iterations.Sum(i => i.Files.Sum(f => f.SizeBytes))));
        r.Planned["changes"] = plan.Changes.Count;
        r.Planned["affectedObjects"] = plan.Changes.Sum(c => c.Affected.Count);
        r.Planned["classificationsStaged"] = plan.Classifications.Count;
        if (prep.Config.Classification.Load)
        {
            r.Planned["classesToCreate"] = plan.ClassesToCreate.Count;
            r.Planned["classifiedIterations"] = plan.ClassIterations.Values.Sum();
            r.ClassesToCreate = [.. plan.ClassesToCreate.Select(c => c.ParentCode is null ? c.Code : $"{c.Code} (under {c.ParentCode})")];
            r.ClassIterations = new SortedDictionary<string, long>(plan.ClassIterations, StringComparer.Ordinal);
        }
        if (prep.Config.Effectivity.Load)
        {
            r.Planned["effectivityLines"] = plan.Parts.Sum(p => p.Revisions.Sum(x => x.Iterations.Sum(i => i.Usages.Count(u => u.Effectivity is not null))));
            r.EffectivitySerials = new SortedDictionary<string, List<long>>(plan.EffectivitySerials, StringComparer.Ordinal);
        }

        var codes = r.Problems.Where(p => p.Severity == Severity.Error).GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.Count());
        r.Files = new FileChecks
        {
            Referenced = prep.Sink.Inventory.FilesReferenced, Planned = r.Planned["files"], PlannedBytes = r.Planned["fileBytes"],
            Missing = codes.GetValueOrDefault("FILE-MISSING"), ShaMismatch = codes.GetValueOrDefault("FILE-SHA-MISMATCH"), SizeMismatch = codes.GetValueOrDefault("FILE-SIZE-MISMATCH"),
            ShaComputedNotDeclared = plan.Decisions.GetValueOrDefault("file-sha-computed"),
        };

        foreach (var (key, label) in new[] { ("parts", "part"), ("documents", "document"), ("changes", "change") })
        {
            var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
            IEnumerable<string> states = key switch
            {
                "parts" => plan.Parts.SelectMany(p => p.Revisions.Select(x => x.State)),
                "documents" => plan.Documents.SelectMany(p => p.Revisions.Select(x => x.State)),
                _ => plan.Changes.Select(c => c.ChangeType + ":" + c.State),
            };
            foreach (var s in states) d[s] = d.GetValueOrDefault(s) + 1;
            r.StateDistribution[label] = d;
        }
        foreach (var b in plan.Parts.Select(p => p.Markings.BannerText()).Concat(plan.Documents.Select(p => p.Markings.BannerText())).Concat(plan.Changes.Select(p => p.Markings.BannerText())))
            r.MarkingsDistribution[b] = r.MarkingsDistribution.GetValueOrDefault(b) + 1;

        if (result is not null)
        {
            foreach (var k in result.Created.Keys.Concat(result.Skipped.Keys).Distinct())
                r.Results[k] = new EntityResult { Created = result.Created.GetValueOrDefault(k), Skipped = result.Skipped.GetValueOrDefault(k) };
            r.ElapsedSeconds = result.Elapsed.TotalSeconds;
            r.AuditEventsWritten = result.AuditEventsWritten;
            r.Quarantined = [.. result.Quarantined.Select(q => new QuarantinedObject { EntityType = q.EntityType, SourceId = q.SourceId, Code = q.Code, Reason = q.Message })];
            r.EstimatedUploadBytes = result.BytesUploaded;
        }
        else
        {
            // Never loaded: the objects the mapping rejected are the quarantine list.
            foreach (var key in plan.Failed.Order(StringComparer.Ordinal))
            {
                var parts = key.Split('\u001f');
                if (parts[0] is "package" or "mapping" or "user") continue;
                var first = plan.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.EntityType == parts[0] && p.SourceId == parts[1]);
                r.Quarantined.Add(new QuarantinedObject { EntityType = parts[0], SourceId = parts[1], Code = first?.Code ?? "VALIDATION", Reason = first?.Message ?? "Validation error." });
            }
        }
        r.EstimatedRows = r.Planned.Where(kv => kv.Key is not ("fileBytes" or "classificationsStaged" or "classifiedIterations" or "effectivityLines" or "classesToCreate")).Sum(kv => kv.Value);

        // blocking: package- or mapping-level errors, source (reader) errors, and, unless quarantine is on, any rejected object
        if (plan.IsFailed("mapping", "mapping")) r.Blocking.Add("The mapping file has errors (MAP-* codes).");
        if (plan.IsFailed("package", "package")) r.Blocking.Add("The package has package-level errors (PKG-* codes).");
        if (plan.SourceErrors > 0) r.Blocking.Add($"{plan.SourceErrors} errors were found while reading the source (SRC-* codes); fix the export: quarantine does not apply to unreadable or inconsistent source files.");
        if (!opt.Quarantine && r.Quarantined.Count > 0) r.Blocking.Add($"{r.Quarantined.Count} objects have errors; fix them or use quarantine mode to skip and list them.");
        if (result is not null && !opt.Quarantine && result.Problems.Any(p => p.Severity == Severity.Error)) r.Blocking.Add("Errors occurred while writing.");
        r.Succeeded = r.Blocking.Count == 0;
        r.Hash = r.ComputeHash();
        return r;
    }
}
