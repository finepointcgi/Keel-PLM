using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keel.Migration.Canonical;

/// <summary>
/// The canonical staging model (ADR-0023). Every reader emits it, mapping and validation consume it, and it serializes to JSON
/// (the "Keel canonical package" format, version <see cref="CurrentVersion"/>). It is deliberately close to the incumbents' model:
/// master (item) -> revision -> iteration, with usages and file lists on iterations.
/// </summary>
public sealed class CanonicalPackage
{
    public const int CurrentVersion = 1;
    public int FormatVersion { get; set; } = CurrentVersion;
    /// <summary>Free label of the origin: "teamcenter", "windchill", or any name for scripted exports. Part of every id-map key.</summary>
    public string SourceSystem { get; set; } = "";
    public string? SourceInstance { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public List<CObject> Items { get; set; } = [];
    public List<CObject> Documents { get; set; } = [];
    public List<CChange> Changes { get; set; } = [];
    public List<CClassAssignment> Classifications { get; set; } = [];
    /// <summary>The source's classification hierarchy (optional; from package.json "classNodes" or the sidecar file classification.json).</summary>
    public List<CClassNode> ClassNodes { get; set; } = [];
    public List<CUser> Users { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>An item (part) or a document master with its revisions.</summary>
public sealed class CObject
{
    public string SourceId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Source item/document type name, mapped by the mapping file.</summary>
    public string? Type { get; set; }
    /// <summary>Items only: unit of measure as the source spells it.</summary>
    public string? Unit { get; set; }
    /// <summary>Items only: make/buy as the source spells it.</summary>
    public string? Source { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public Dictionary<string, string?> Properties { get; set; } = [];
    public List<CRevision> Revisions { get; set; } = [];
}

public sealed class CRevision
{
    /// <summary>Unique within the package and system. Readers build it from the item id and the label when the source has none.</summary>
    public string SourceId { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    /// <summary>Lifecycle history in source order; the last entry is the current state.</summary>
    public List<CState> States { get; set; } = [];
    public Dictionary<string, string?> Properties { get; set; } = [];
    public List<CIteration> Iterations { get; set; } = [];
}

public sealed class CState
{
    public string Name { get; set; } = "";
    public DateTimeOffset? At { get; set; }
    public string? By { get; set; }
}

public sealed class CIteration
{
    public int Sequence { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Comment { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string?> Properties { get; set; } = [];
    public List<CUsage> Usages { get; set; } = [];
    public List<CDocLink> DocLinks { get; set; } = [];
    public List<CFile> Files { get; set; } = [];
}

public sealed class CUsage
{
    public string ChildSourceId { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public int? FindNumber { get; set; }
    public List<string> ReferenceDesignators { get; set; } = [];
    public string? Notes { get; set; }
    /// <summary>Raw source effectivity text. Effectivity loads with Phase 2a; until then it is preserved in the usage notes and reported.</summary>
    public string? Effectivity { get; set; }
    /// <summary>
    /// Structured source effectivity (serial, lot or date ranges inside a source end item). One line may carry several ranges; each becomes one Keel line (an alternate of the same
    /// find number). Loaded only when the mapping enables effectivity; otherwise it is rendered into the usage notes.
    /// </summary>
    public List<CEffectivity> EffectivityRanges { get; set; } = [];
}

/// <summary>A source effectivity range as the source spells it; the mapping decides the Keel context, block names and date handling.</summary>
public sealed class CEffectivity
{
    /// <summary>"serial", "lot" or "date" (case-insensitive).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The source end item or effectivity unit (for example a Teamcenter end item or Windchill product/serial scope); null for dates.</summary>
    public string? Unit { get; set; }
    /// <summary>First unit: a whole number, a lot/block name, or an ISO 8601 date or date-time (read as UTC).</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>"openEnded" (no upper end whatever <see cref="To"/> says) or "stockOut"/empty (<see cref="To"/> is the last unit), as in PLM XML.</summary>
    public string? EndCondition { get; set; }
}

/// <summary>A class of the staged classification hierarchy (format keel-classification/1, docs/migration/classification-format.md).</summary>
public sealed class CClassNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Parent { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; } = "active";
    public List<CClassAttribute> Attributes { get; set; } = [];
}

public sealed class CClassAttribute
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string DataType { get; set; } = "string";
    public string? Unit { get; set; }
    public List<string>? EnumValues { get; set; }
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public string? HelpText { get; set; }
    public string? Group { get; set; }
    public int SortOrder { get; set; }
    public bool Retired { get; set; }
}

public sealed class CDocLink
{
    /// <summary>"describes" or "references".</summary>
    public string Type { get; set; } = "describes";
    public string DocumentSourceId { get; set; } = "";
    /// <summary>Source revision label of the document a "describes" link pins; null means the document's last revision.</summary>
    public string? DocumentRevisionLabel { get; set; }
}

public sealed class CFile
{
    public string Name { get; set; } = "";
    public string? MediaType { get; set; }
    /// <summary>Path inside the package (relative, forward slashes).</summary>
    public string? PackagePath { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool Primary { get; set; }
}

public sealed class CChange
{
    public string SourceId { get; set; } = "";
    public string Number { get; set; } = "";
    /// <summary>Source type name (ECN, ECR, PR, "Change Notice" ...), mapped by the mapping file.</summary>
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string State { get; set; } = "";
    public string? ChangeClass { get; set; }
    public string? Priority { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? StateChangedAt { get; set; }
    public string? StateChangedBy { get; set; }
    /// <summary>Source id of the change this one descends from (PR -> ECR -> ECN).</summary>
    public string? ParentSourceId { get; set; }
    public Dictionary<string, string?> Properties { get; set; } = [];
    public List<CAffected> Affected { get; set; } = [];
}

public sealed class CAffected
{
    public string ItemSourceId { get; set; } = "";
    public string? RevisionLabel { get; set; }
    public string? ResultingRevisionLabel { get; set; }
    /// <summary>"revise", "obsolete" or "none" (listed only).</summary>
    public string Action { get; set; } = "none";
}

public sealed class CClassAssignment
{
    public string ItemSourceId { get; set; } = "";
    public string ClassCode { get; set; } = "";
    public Dictionary<string, string?> Attributes { get; set; } = [];
}

public sealed class CUser
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> Groups { get; set; } = [];
}

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>A finding of a reader, the mapper or the loader: stable code, the source object, and a hint how to fix it.</summary>
public sealed record Problem(Severity Severity, string Code, string EntityType, string SourceId, string Message, string? Hint = null);
