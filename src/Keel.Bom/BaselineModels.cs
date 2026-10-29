using Keel.Core;

namespace Keel.Bom;

/// <summary>Lifecycle states of a baseline (data in <c>plm.lifecycle_state</c>, lifecycle <c>baseline-default</c>).</summary>
public static class BaselineStates
{
    public const string Draft = "Draft";
    public const string UnderReview = "UnderReview";
    public const string Established = "Established";
    public const string Superseded = "Superseded";
    public const string Archived = "Archived";
    public const string Lifecycle = "baseline-default";
}

public static class BaselineTypes
{
    public const string Design = "Design";
    public const string Release = "Release";
    public const string Product = "Product";
}

public static class BaselineLimits
{
    public const int MaxReasonLength = 2000;
    public const int MaxPurposeLength = 4000;
}

/// <summary>
/// Create a baseline: the structure under <see cref="RootNumber"/> exploded under <see cref="Spec"/> (Latest, LatestReleased or Effectivity for one unit)
/// and frozen. <see cref="Markings"/> is the baseline's own request (default: the root part's markings); when the items need more, the minimal
/// cover is used only if <see cref="ConfirmRaiseMarkings"/> is set (ADR-0021, as for change objects). <see cref="RequireSignoff"/> null follows the
/// baseline type; true forces review with e-signatures; false is refused for types that require it.
/// </summary>
public sealed record CreateBaselineCommand(
    string RootNumber,
    string Name,
    string Type,
    BomConfigSpec Spec = BomConfigSpec.LatestReleased,
    bool ReleasedOnly = false,
    bool IncludeDocuments = false,
    string Purpose = "",
    string? Milestone = null,
    string? RootRevision = null,
    EffectivityRequest? Effectivity = null,
    Markings? Markings = null,
    bool ConfirmRaiseMarkings = false,
    bool? RequireSignoff = null);

public sealed record BaselineTypeInfo(string Type, string Label, bool RequiresSignoff);

public sealed record BaselineSummary(
    Guid Id,
    string Number,
    string Name,
    string Type,
    string? Milestone,
    string State,
    string StateLabel,
    bool Signoff,
    /// <summary>Root part number, null when the reader may no longer read the root part.</summary>
    string? RootNumber,
    string? RootName,
    string? RootVersion,
    int ItemCount,
    string SpecText,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? EstablishedAt,
    string Banner,
    Markings Markings);

public sealed record BaselineSearch(
    string? Text = null,
    string? State = null,
    string? Type = null,
    string? RootNumber = null,
    string? ContainsPart = null,
    int Limit = 50,
    int Offset = 0,
    SortSpec? Sort = null);

public sealed record BaselinePage(IReadOnlyList<BaselineSummary> Items, int Total, string Banner);

public sealed record BaselinePermissions(bool CanModify, bool CanEstablishDirectly, bool RequiresSignoff, bool CanArchive, string? Reason);

public sealed record BaselineDetail(
    BaselineSummary Summary,
    string Purpose,
    bool ReleasedOnly,
    bool IncludeDocuments,
    int DocumentCount,
    string ContentSha256,
    string? EstablishReason,
    string? EstablishedBy,
    string? StateReason,
    /// <summary>Number of the baseline that replaced this one, when the reader may read it.</summary>
    string? SupersededBy,
    IReadOnlyList<string> Supersedes,
    BaselinePermissions Permissions,
    IReadOnlyList<string> Notices);

/// <summary>A document revision pinned by a baseline. Redacted when the reader may not read the document (A4).</summary>
public sealed record BaselineDocumentRow(
    bool Redacted, string ItemPath, string LinkType, string? Number, string? Name, string? DocumentType, string? Revision, string? State, string? Banner, Markings? Markings);

public sealed record BaselineDocuments(string Number, IReadOnlyList<BaselineDocumentRow> Rows, string Banner);

/// <summary>One line of a deep structure comparison (baseline against baseline or current structure, or one unit against another).</summary>
public sealed record DeepDiffLine(
    BomChangeKind Kind,
    bool Redacted,
    int Level,
    string Path,
    string? Number,
    string? Name,
    string? FromVersion,
    string? ToVersion,
    int? FromFind,
    int? ToFind,
    decimal? FromQuantity,
    decimal? ToQuantity,
    string? Unit,
    IReadOnlyList<string> Changes,
    string? Banner,
    Markings? Markings);

public sealed record DeepComparison(
    string Title,
    string FromLabel,
    string ToLabel,
    IReadOnlyList<DeepDiffLine> Lines,
    int Unchanged,
    int Added,
    int Removed,
    int Changed,
    string Banner);

public sealed record DeepComparisonExport(string FileName, string Csv, string Banner, int Lines);
