using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Compliance;

/// <summary>The kinds of regulatory list: what a substance on the list means.</summary>
public static class ListKinds
{
    /// <summary>Above its threshold the substance is not allowed (RoHS).</summary>
    public const string Restricted = "restricted";
    /// <summary>Above its threshold the substance must be reported (REACH SVHC).</summary>
    public const string Svhc = "svhc";
    /// <summary>Presence is reported with its conflict-free status (3TG).</summary>
    public const string Conflict = "conflict";

    public static readonly ImmutableArray<string> All = [Restricted, Svhc, Conflict];

    public static string Label(string kind) => kind switch
    {
        Restricted => "Restricted substances",
        Svhc => "Substances of very high concern",
        Conflict => "Conflict minerals",
        _ => kind,
    };
}

public static class VersionStates
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static string Label(string s) => s switch { Draft => "Draft", InReview => "In review", Published => "Published", Superseded => "Superseded", _ => s };
}

/// <summary>The status of a declaration (stored). Expiry and non-compliance are derived from the dates and the evaluation, and written back by the services.</summary>
public static class DeclarationStatuses
{
    public const string Declared = "declared";
    public const string Verified = "verified";
    public const string Expired = "expired";
    public const string NonCompliant = "non_compliant";
    public const string Withdrawn = "withdrawn";

    public static readonly ImmutableArray<string> All = [Declared, Verified, Expired, NonCompliant, Withdrawn];

    public static string Label(string s) => s switch
    {
        Declared => "Declared", Verified => "Verified", Expired => "Expired", NonCompliant => "Non-compliant", Withdrawn => "Withdrawn", _ => s,
    };
}

public static class DeclarationSources
{
    public const string Internal = "internal";
    public const string Supplier = "supplier";
    public static string Label(string s) => s == Supplier ? "Supplier" : "Internal";
}

/// <summary>The result of evaluating one declaration against one regulation.</summary>
public static class EvaluationResults
{
    public const string Compliant = "compliant";
    public const string Exempt = "exempt";
    public const string NonCompliant = "non_compliant";
    public const string Unverified = "unverified";

    public static string Label(string r) => r switch
    {
        Compliant => "Compliant", Exempt => "Compliant with exemption", NonCompliant => "Non-compliant", Unverified => "Not yet verified", _ => r,
    };
}

/// <summary>What a part (or a line of an assembly) stands at for one regulation.</summary>
public static class Standings
{
    public const string Compliant = "compliant";
    public const string Exempt = "exempt";
    public const string NonCompliant = "non_compliant";
    public const string Unverified = "unverified";
    /// <summary>No current declaration.</summary>
    public const string Gap = "gap";
    /// <summary>Declarations exist but all have expired.</summary>
    public const string Expired = "expired";
    public const string NotRequired = "not_required";

    public static string Label(string s) => s switch
    {
        Compliant => "Compliant", Exempt => "Compliant with exemption", NonCompliant => "Non-compliant", Unverified => "Conflict status unknown",
        Gap => "No declaration", Expired => "Declaration expired", NotRequired => "Not required (assembly)", _ => s,
    };

    /// <summary>The standing of an assembly as a whole.</summary>
    public const string AssemblyCompliant = "compliant";
    public const string AssemblyExempt = "compliant_exempt";
    public const string AssemblyNonCompliant = "non_compliant";
    public const string AssemblyIncomplete = "incomplete";
    public const string AssemblyNone = "not_applicable";

    public static string AssemblyLabel(string s) => s switch
    {
        AssemblyCompliant => "Compliant", AssemblyExempt => "Compliant, using exemptions", AssemblyNonCompliant => "Non-compliant",
        AssemblyIncomplete => "Incomplete: declarations are missing", AssemblyNone => "No components to assess", _ => s,
    };
}

public static class Minerals
{
    public static readonly ImmutableArray<string> All = ["tin", "tantalum", "tungsten", "gold"];
    public static string Label(string m) => m switch { "tin" => "Tin", "tantalum" => "Tantalum", "tungsten" => "Tungsten", "gold" => "Gold", _ => m };
}

// ---------------------------------------------------------------- substances and materials

public sealed record SubstanceView(Guid Id, string Number, string CasNumber, string Name, string? EcNumber, string? Mineral, int Revision);
public sealed record SubstanceInput(string CasNumber, string Name, string? EcNumber = null, string? Mineral = null);
public sealed record SubstanceSearch(string? Text = null, string? Mineral = null, int Limit = 50, int Offset = 0, SortSpec? Sort = null);
public sealed record SubstancePage(IReadOnlyList<SubstanceView> Items, int Total);
/// <summary>A substance on an active list version: its list, the version and the threshold.</summary>
public sealed record SubstanceListUse(string ListCode, string ListName, int Version, decimal ThresholdPct);
public sealed record SubstanceDetail(SubstanceView Substance, IReadOnlyList<SubstanceListUse> Lists, IReadOnlyList<string> Materials);

public sealed record MaterialComponent(Guid SubstanceId, string SubstanceNumber, string CasNumber, string SubstanceName, decimal FractionPct);
public sealed record MaterialComponentInput(string CasNumber, decimal FractionPct);
public sealed record MaterialView(Guid Id, string Code, string Name, string? Description, bool Active, int Revision, IReadOnlyList<MaterialComponent> Composition);
public sealed record MaterialInput(string Code, string Name, string? Description, IReadOnlyList<MaterialComponentInput> Composition, bool Active = true);

// ---------------------------------------------------------------- regulatory lists

public sealed record RegListSummary(
    string Code, string Name, string Kind, string? Description, bool Active, int? ActiveVersion, DateOnly? ActiveFrom, int? OpenVersion, string? OpenStatus, int EntryCount);

public sealed record RegVersionSummary(
    Guid Id, int Version, string Status, DateOnly EffectiveFrom, string? SourceNote, int Entries, int Exemptions, DateTimeOffset? PublishedAt, string? PublishedBy, int Revision, string OwningOrg = "");

public sealed record RegListDetail(RegListSummary List, IReadOnlyList<RegVersionSummary> Versions);

public sealed record RegEntryView(Guid SubstanceId, string SubstanceNumber, string CasNumber, string SubstanceName, string? Mineral, decimal ThresholdPct, string? Note);
public sealed record RegEntryInput(string CasNumber, decimal ThresholdPct, string? Note = null);
public sealed record RegExemptionView(string Code, string Description, string? SubstanceNumber, string? CasNumber, DateOnly? ExpiresOn);
public sealed record RegExemptionInput(string Code, string Description, string? CasNumber = null, DateOnly? ExpiresOn = null);

/// <summary>What a version changes compared with the one before it: lines of text, never data about parts.</summary>
public sealed record RegVersionDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

public sealed record RegVersionDetail(
    string ListCode, string ListName, string Kind, RegVersionSummary Version, IReadOnlyList<RegEntryView> Entries, IReadOnlyList<RegExemptionView> Exemptions, RegVersionDiff? DiffToPrevious);

public sealed record CreateListCommand(string Code, string Name, string Kind, string? Description);

// ---------------------------------------------------------------- declarations

public sealed record DeclarationLineInput(string? SubstanceCas, string? MaterialCode, decimal? Percent, decimal? MassMg, string? ExemptionCode = null, bool? ConflictFree = null);

public sealed record DeclarationInput(
    string? RevisionScope, string Source, string? SupplierRef, string? MpnRef, decimal? ItemMassMg, DateOnly DeclaredOn, DateOnly ExpiresOn, IReadOnlyList<DeclarationLineInput> Lines, string? Note);

public sealed record CreateDeclarationCommand(string PartNumber, DeclarationInput Input);

public sealed record DeclarationLineView(
    int Ordinal, string? SubstanceNumber, string? CasNumber, string? SubstanceName, string? MaterialCode, string? MaterialName, decimal? Percent, decimal? MassMg, string? ExemptionCode, bool? ConflictFree);

public sealed record EvaluationView(string ListCode, string ListName, int ListVersion, string Result, int Findings);

/// <summary>A linked document: the number only. Whether the viewer may read it is decided each time it is shown; an unreadable one has no number here.</summary>
public sealed record DocumentLinkView(string? DocumentNumber, bool Readable);

public sealed record DeclarationSummary(
    Guid Id, string Number, string PartNumber, string? RevisionScope, string Source, string? SupplierRef, string? MpnRef, DateOnly DeclaredOn, DateOnly ExpiresOn, string Status,
    int DaysToExpiry, IReadOnlyList<EvaluationView> Evaluations)
{
    public string StatusLabel => DeclarationStatuses.Label(Status);
}

public sealed record DeclarationDetail(
    DeclarationSummary Summary, decimal? ItemMassMg, string? Note, DateTimeOffset? VerifiedAt, string? VerifiedBy, string? WithdrawnReason, int Revision,
    IReadOnlyList<DeclarationLineView> Lines, IReadOnlyList<DocumentLinkView> Documents, IReadOnlyList<FindingView> Findings, string PartBanner);

public sealed record DeclarationSearch(string? PartNumber = null, string? Status = null, string? Source = null, string? SupplierRef = null, int Limit = 50, int Offset = 0, SortSpec? Sort = null);
public sealed record DeclarationPage(IReadOnlyList<DeclarationSummary> Items, int Total, string PageBanner);

/// <summary>One thing an evaluation found: a substance line above its threshold, an exemption in use, or a conflict status that is not known.</summary>
public sealed record FindingView(
    string ListCode, int ListVersion, string DeclarationNumber, int LineOrdinal, string SubstanceNumber, string CasNumber, string SubstanceName, decimal Percent, decimal ThresholdPct,
    string Reason, string? ExemptionCode)
{
    public string ReasonLabel => FindingReasons.Label(Reason);
}

public static class FindingReasons
{
    public const string AboveThreshold = "above_threshold";
    public const string Exempt = "exempt";
    public const string ExemptionUnknown = "exemption_unknown";
    public const string ExemptionExpired = "exemption_expired";
    public const string ExemptionNotApplicable = "exemption_not_applicable";
    public const string NotConflictFree = "not_conflict_free";
    public const string ConflictUnknown = "conflict_unknown";
    public const string ConflictFree = "conflict_free";

    public static string Label(string r) => r switch
    {
        AboveThreshold => "Above the threshold",
        Exempt => "Allowed by an exemption",
        ExemptionUnknown => "The exemption is not in this version of the list",
        ExemptionExpired => "The exemption has expired",
        ExemptionNotApplicable => "The exemption does not apply to this substance",
        NotConflictFree => "Not conflict-free",
        ConflictUnknown => "Conflict-free status not declared",
        ConflictFree => "Conflict-free",
        _ => r,
    };

    public static bool IsViolation(string r) => r is AboveThreshold or ExemptionUnknown or ExemptionExpired or ExemptionNotApplicable or NotConflictFree;
}

/// <summary>A part's standing for one regulation, from its current declarations (the worst of them).</summary>
public sealed record PartRegulationStanding(
    string ListCode, string ListName, string Kind, int ListVersion, string Standing, IReadOnlyList<string> DeclarationNumbers, IReadOnlyList<FindingView> Findings)
{
    public string StandingLabel => Standings.Label(Standing);
}

public sealed record PartComplianceView(
    string PartNumber, string? Revision, string PartBanner, IReadOnlyList<DeclarationSummary> Declarations, IReadOnlyList<PartRegulationStanding> Regulations, bool CanModify, bool CanVerify);

/// <summary>Counts for the dashboard card; only over parts the caller may read.</summary>
public sealed record ComplianceCounts(int Declared, int Verified, int Expired, int NonCompliant, int ExpiringSoon, int Parts);

// ---------------------------------------------------------------- the assembly report

/// <summary>A regulation and the list version a report was evaluated against.</summary>
public sealed record ReportRegulation(string Code, string Name, string Kind, int Version, DateOnly EffectiveFrom);

/// <summary>One row of the composition of a part as declared, for the exports.</summary>
public sealed record CompositionRow(string DeclarationNumber, string SubstanceNumber, string CasNumber, string SubstanceName, decimal? Percent, decimal? MassMg, string? ExemptionCode, string? Mineral, bool? ConflictFree);

public sealed record LineStanding(string Standing, IReadOnlyList<FindingView> Findings);

/// <summary>A line of the structure with its standing per regulation code.</summary>
public sealed record ReportLine(
    int Level, string Path, string Number, string Name, string? Revision, decimal Quantity, string Unit, bool Required, IReadOnlyDictionary<string, LineStanding> Standings,
    IReadOnlyList<string> Declarations, IReadOnlyDictionary<string, string> Minerals, IReadOnlyList<CompositionRow> Composition, string? SupplierRef, string? MpnRef, DateOnly? DeclaredOn, DateOnly? ExpiresOn);

/// <summary>Why a line makes the assembly non-compliant or incomplete: the line, its standing and what was found.</summary>
public sealed record ReportCause(string Path, string Number, string Name, string Standing, IReadOnlyList<FindingView> Findings)
{
    public string StandingLabel => Standings.Label(Standing);
}

public sealed record RegulationSummary(
    string Code, string Name, string Kind, int Version, string Standing, int Compliant, int Exempt, int NonCompliant, int Unverified, int Gaps, int Expired, int NotRequired, IReadOnlyList<ReportCause> Causes)
{
    public int Required => Compliant + Exempt + NonCompliant + Unverified + Gaps + Expired;
    public string StandingLabel => Standings.AssemblyLabel(Standing);
}

/// <summary>
/// The compliance of an assembly, built only from the lines the caller may read: a line the caller may not read is left out and not counted, so no total says anything
/// about hidden parts.
/// </summary>
public sealed record ComplianceReport(
    string Mode, string Number, string Name, string Banner, DateOnly AsOf, IReadOnlyList<ReportRegulation> Regulations, IReadOnlyList<ReportLine> Lines, IReadOnlyList<RegulationSummary> Summaries);

/// <summary>A structure line as the host read it through the BOM read path (the host leaves out what the caller may not read).</summary>
public sealed record StructureLine(int Level, int FindNumber, string Path, string Number, string Name, string? Revision, decimal Quantity, string Unit, bool HasChildren);

public sealed record ReportRequest(string Mode, string Number, string Name, string Banner, IReadOnlyList<StructureLine> Lines, IReadOnlyDictionary<string, int>? ListVersions = null, DateOnly? AsOf = null);
