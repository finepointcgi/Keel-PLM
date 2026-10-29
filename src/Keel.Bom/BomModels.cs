using Keel.Core;

namespace Keel.Bom;

/// <summary>
/// How a usage link's child version is chosen at read time (ADR-0003, ADR-0021).
/// </summary>
public enum BomConfigSpec
{
    /// <summary>The child master's latest revision, at its latest iteration.</summary>
    Latest,
    /// <summary>The child master's highest revision in the Released state (latest iteration); unresolved when there is none.</summary>
    LatestReleased,
    /// <summary>As designed or built for one unit: lines whose effectivity covers it, each child at the revision whose effectivity covers it.</summary>
    Effectivity,
    /// <summary>Exactly the items of an established or draft baseline, as frozen at its creation.</summary>
    Baseline,
}

public static class BomLimits
{
    /// <summary>ASSUMPTION: a single parent iteration owns at most this many direct children.</summary>
    public const int MaxChildrenPerParent = 5000;
    public const int MaxDepth = 10;
    public const decimal MaxQuantity = 1_000_000m;
    public const int MaxFindNumber = 999_999;
    /// <summary>Safety valve: an explosion larger than this must be taken in parts (depth limit or lazy expansion).</summary>
    public const int MaxExplosionLines = 250_000;
    public const int MaxWhereUsedRows = 100_000;
}

/// <summary>Selects what to explode. <see cref="ParentLine"/> is the find-number path (e.g. "10.20") of the line whose children to list.</summary>
public sealed record BomQuery(
    string Number,
    string? Revision = null,
    int? Iteration = null,
    BomConfigSpec Spec = BomConfigSpec.Latest,
    int? Depth = null,
    string? ParentLine = null,
    /// <summary>Baseline number for <see cref="BomConfigSpec.Baseline"/>.</summary>
    string? Baseline = null,
    /// <summary>Unit for <see cref="BomConfigSpec.Effectivity"/>.</summary>
    EffectivityRequest? Effectivity = null,
    /// <summary>Resolve the structure for this variant (ADR-0039): lines whose option condition does not hold for its options are left out. Null: the full 150% structure.</summary>
    Keel.Core.Variants.VariantSelection? Variant = null);

/// <summary>
/// One line of an exploded structure. Redacted lines (A4) carry only quantity, unit, find number, level and path;
/// every identifying field is null and the line is never expanded.
/// </summary>
public sealed record BomLine(
    Guid LineId,
    Guid? ParentLineId,
    int Level,
    string Path,
    int FindNumber,
    decimal Quantity,
    string Unit,
    decimal ExtendedQuantity,
    bool Redacted,
    bool Unresolved,
    bool Circular,
    bool HasChildren,
    Guid? MasterId,
    string? Number,
    string? Name,
    string? PartType,
    string? Revision,
    int? Iteration,
    string? State,
    string? StateLabel,
    IReadOnlyList<string> ReferenceDesignators,
    string? Notes,
    string? Banner,
    Markings? Markings,
    /// <summary>Applicability of this line (null: applies to every unit). Shown for redacted lines too: it is not identifying (CFG-6).</summary>
    EffectivityRange? Effectivity = null,
    /// <summary>Canonical option usage condition of the line (null: used in every variant). Never shown for restricted lines.</summary>
    string? Condition = null)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record BomExplosion(
    string RootNumber,
    string RootName,
    string RootRevision,
    int RootIteration,
    BomConfigSpec Spec,
    int? Depth,
    string? ParentLine,
    IReadOnlyList<BomLine> Lines,
    /// <summary>Aggregate banner of the root and every readable line returned.</summary>
    string Banner,
    int RedactedCount,
    int UnresolvedCount,
    /// <summary>What the structure was resolved for, e.g. "serial 101 (ORCA-POD)" or "baseline BL-000001"; null for Latest and LatestReleased.</summary>
    string? Configuration = null);

public sealed record BomExport(string FileName, string Csv, string Banner, int Lines, int RedactedCount);

public sealed record BomSummaryLine(
    bool Redacted,
    string? Number,
    string? Name,
    string? Revision,
    string? State,
    string Unit,
    decimal TotalQuantity,
    int Occurrences,
    bool IsAssembly,
    string? Path,
    string? Banner,
    Markings? Markings);

public sealed record BomSummary(string RootNumber, string RootRevision, BomConfigSpec Spec, bool LeavesOnly, IReadOnlyList<BomSummaryLine> Lines, string Banner);

public sealed record WhereUsedRow(
    int Level,
    bool Redacted,
    int FindNumber,
    decimal Quantity,
    string Unit,
    string? UsedNumber,
    string? ParentNumber,
    string? ParentName,
    string? ParentRevision,
    int? ParentIteration,
    string? ParentState,
    string? ParentStateLabel,
    string? Banner,
    Markings? Markings,
    EffectivityRange? Effectivity = null)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record WhereUsedResult(string Number, string Name, int Levels, IReadOnlyList<WhereUsedRow> Rows, string Banner);

public enum BomChangeKind { Added, Removed, Changed }

public sealed record BomDiffLine(
    BomChangeKind Kind,
    bool Redacted,
    string? Number,
    string? Name,
    int? FromFindNumber,
    int? ToFindNumber,
    decimal? FromQuantity,
    decimal? ToQuantity,
    string? FromUnit,
    string? ToUnit,
    IReadOnlyList<string> Changes,
    string? Banner,
    Markings? Markings);

public sealed record BomComparison(string Number, string From, string To, IReadOnlyList<BomDiffLine> Lines, int Unchanged, string Banner);

/// <summary>A line of the caller's private working copy. Children the caller can no longer read are shown redacted.</summary>
public sealed record WorkingBomLine(
    Guid Id,
    int FindNumber,
    decimal Quantity,
    string Unit,
    bool Redacted,
    Guid? ChildMasterId,
    string? ChildNumber,
    string? ChildName,
    string? ChildPartType,
    IReadOnlyList<string> ReferenceDesignators,
    string? Notes,
    string? Banner,
    Markings? Markings,
    EffectivityRange? Effectivity = null,
    /// <summary>Canonical option usage condition (null: used in every variant).</summary>
    string? Condition = null)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record WorkingBom(string Number, string Revision, IReadOnlyList<WorkingBomLine> Lines, string Banner, int MaxChildren);

public sealed record AddUsageCommand(
    string ChildNumber,
    decimal Quantity,
    string? Unit = null,
    int? FindNumber = null,
    IReadOnlyList<string>? ReferenceDesignators = null,
    string? Notes = null,
    EffectivityInput? Effectivity = null);

/// <summary><see cref="Effectivity"/> null leaves the line's effectivity as it is; <see cref="ClearEffectivity"/> removes it (the line then applies to every unit).</summary>
public sealed record UpdateUsageCommand(
    decimal Quantity,
    string Unit,
    int FindNumber,
    IReadOnlyList<string>? ReferenceDesignators = null,
    string? Notes = null,
    EffectivityInput? Effectivity = null,
    bool ClearEffectivity = false);
