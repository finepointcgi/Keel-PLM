using Keel.Core;

namespace Keel.Requirements;

/// <summary>Stable codes and labels of the requirement vocabulary (ADR-0038). Codes are stored; labels are shown.</summary>
public static class ReqTypes
{
    public static readonly IReadOnlyList<string> All = ["functional", "performance", "interface", "environmental", "safety"];
    public static string Label(string code) => code switch { "functional" => "Functional", "performance" => "Performance", "interface" => "Interface", "environmental" => "Environmental", "safety" => "Safety", _ => code };
}

public static class ReqPriorities
{
    public static readonly IReadOnlyList<string> All = ["low", "medium", "high", "critical"];
    public static string Label(string code) => code switch { "low" => "Low", "medium" => "Medium", "high" => "High", "critical" => "Critical", _ => code };
}

public static class VerificationMethods
{
    public static readonly IReadOnlyList<string> All = ["test", "analysis", "inspection", "demonstration"];
    public static string Label(string code) => code switch { "test" => "Test", "analysis" => "Analysis", "inspection" => "Inspection", "demonstration" => "Demonstration", _ => code };
}

public static class ReqStatuses
{
    public const string Draft = "draft", Approved = "approved", Deferred = "deferred", Withdrawn = "withdrawn";
    public static readonly IReadOnlyList<string> All = [Draft, Approved, Deferred, Withdrawn];
    public static string Label(string code) => code switch { Draft => "Draft", Approved => "Approved", Deferred => "Deferred", Withdrawn => "Withdrawn", _ => code };
    /// <summary>A requirement that must be allocated and verified: not withdrawn and not deferred.</summary>
    public static bool InScope(string status) => status is Draft or Approved;
}

public static class VerificationResults
{
    public const string Pass = "pass", Fail = "fail", Waived = "waived";
    public static readonly IReadOnlyList<string> All = [Pass, Fail, Waived];
    public static string Label(string code) => code switch { Pass => "Pass", Fail => "Fail", Waived => "Waived", _ => code };
}

/// <summary>The verification state of a requirement's CURRENT content: the newest record for that exact content decides; records for older content are stale.</summary>
public static class VerificationStates
{
    public const string Verified = "verified", Waived = "waived", Failed = "failed", Stale = "stale", None = "none";
    public static string Label(string code) => code switch { Verified => "Verified", Waived => "Waived", Failed => "Failed", Stale => "Needs re-verification", _ => "Not verified" };
}

public static class SpecStates
{
    public const string InWork = "InWork", UnderReview = "UnderReview", Released = "Released", Obsolete = "Obsolete";
    public static string Label(string s) => s switch { InWork => "In Work", UnderReview => "Under Review", Released => "Released", Obsolete => "Obsolete", _ => s };
}

public static class SuspectReasons
{
    public const string SourceChanged = "source_changed", TargetChanged = "target_changed", TargetRevised = "target_revised";
    public static string Label(string? r) => r switch
    {
        SourceChanged => "The requirement changed after the link was reviewed",
        TargetChanged => "The linked requirement changed after the link was reviewed",
        TargetRevised => "The linked object has a newer revision than the one reviewed",
        _ => "",
    };
}

public static class LinkTargetKinds
{
    public const string Requirement = "requirement", Part = "part", Document = "document", Change = "change";
    public static readonly IReadOnlyList<string> All = [Requirement, Part, Document, Change];
}

public static class CoverageRoles
{
    public const string None = "none", Allocation = "allocation", Verification = "verification";
    public static readonly IReadOnlyList<string> All = [None, Allocation, Verification];
}

// ---------------------------------------------------------------- specifications

public sealed record SpecSummary(
    Guid Id, string Number, string Name, string? Product, string Revision, int Iteration, string State, string StateLabel, int RequirementCount, string Banner, Markings Markings);

public sealed record SpecSearch(string? Query = null, string? State = null, string? Program = null, int Limit = 50, int Offset = 0, SortSpec? Sort = null);

public sealed record SpecPage(IReadOnlyList<SpecSummary> Items, int Total, string PageBanner);

public sealed record SpecRevisionInfo(Guid Id, string Revision, string State, string StateLabel, bool IsLatest, int LatestIteration, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record SpecDetail(
    SpecSummary Summary, string Description, IReadOnlyList<SpecRevisionInfo> Revisions, SpecRevisionInfo Displayed, string IterationComment, DateTimeOffset IterationAt, string IterationBy,
    bool CanModify, bool CanAdminister, IReadOnlyList<string> ModifyDeniedReasons, bool Editable);

public sealed record CreateSpecCommand(string Name, string? Product, string? Description, Markings Markings);

// ---------------------------------------------------------------- requirements

/// <summary>
/// One row of a requirement set in outline order. A <see cref="Redacted"/> row stands for a requirement the caller may not read: it has its place in the outline
/// (number and depth) and nothing else (no key, title, text, type, status, marking or link).
/// </summary>
public sealed record RequirementRow(
    bool Redacted, string Outline, int Depth, string? Key, string? ParentKey, string? Title, string? Text, string? Type, string? Priority, string? Method, string? Rationale,
    string? Status, string? ContentSha256, string? Banner, Markings? Markings)
{
    public string? TypeLabel => Type is null ? null : ReqTypes.Label(Type);
}

public sealed record RequirementSet(string SpecNumber, string Revision, int Iteration, string State, bool IsLatest, IReadOnlyList<RequirementRow> Rows, string Banner);

public sealed record RequirementInput(
    string Title, string Text, string Type, string Priority, string Method, string? Rationale = null, string Status = ReqStatuses.Draft,
    string? ParentKey = null, string? BeforeKey = null, Markings? Markings = null, bool ConfirmRaiseMarkings = false);

/// <summary>One edit of a requirement set. <c>Op</c> is add, update or move. Update changes the fields that are not null; move places the requirement under <c>ParentKey</c> before <c>BeforeKey</c> (at the end when null).</summary>
public sealed record EditOp(string Op, string? Key, RequirementInput? Input);

/// <summary>An edit batch. <see cref="BaseIteration"/> is the iteration the caller read; a newer one makes the edit a conflict (nothing is merged silently).</summary>
public sealed record EditCommand(int BaseIteration, string? Comment, IReadOnlyList<EditOp> Ops);

public sealed record EditResult(int Iteration, IReadOnlyList<string> CreatedKeys, int Changed, int SuspectLinks);

// ---------------------------------------------------------------- link types and links

public sealed record LinkTypeInfo(string Code, string Label, string ReverseLabel, string TargetKind, string CoverageRole, bool IsSystem, bool IsActive, int SortOrder);

public sealed record SaveLinkTypeCommand(string Code, string Label, string ReverseLabel, string TargetKind, string CoverageRole, bool IsActive, int SortOrder, string? Reason);

/// <summary>Adds a link from a requirement. For a requirement target name the specification and key; for a part, document or change its number.</summary>
public sealed record AddLinkCommand(string LinkType, string? TargetSpec, string? TargetKey, string? TargetNumber);

/// <summary>
/// A link as one end of it sees it. When the other end is not readable the row is <see cref="Redacted"/> and carries only <see cref="Id"/>: no type, kind, number,
/// name or marking (the existence of the link is the only thing shown).
/// </summary>
public sealed record LinkView(
    Guid Id, bool Redacted, string? LinkType, string? Label, string Direction, string? OtherKind, string? OtherNumber, string? OtherKey, string? OtherName, string? OtherRevision,
    bool Suspect, string? SuspectReason, string? Banner, Markings? Markings, DateTimeOffset CreatedAt, string? CreatedBy, string? ReviewedBy, DateTimeOffset? ReviewedAt);

public sealed record RequirementLinks(string SpecNumber, string Key, IReadOnlyList<LinkView> Links, string Banner);

// ---------------------------------------------------------------- verification

public sealed record RecordVerificationCommand(
    string Result, string? Method, string? Notes, string? WaiverReason, IReadOnlyList<string>? EvidenceDocuments, bool Formal, string? SignatureMeaning);

public sealed record EvidenceView(bool Redacted, string? Number, string? Name, string? Revision, string? Banner, Markings? Markings = null);

public sealed record VerificationView(
    Guid Id, string Result, string Method, bool Formal, string SpecRevision, bool Current, string? Notes, string? WaiverReason, DateTimeOffset PerformedAt, string PerformedBy,
    string? SignedBy, DateTimeOffset? SignedAt, string? SignatureMeaning, IReadOnlyList<EvidenceView> Evidence);

public sealed record RequirementVerifications(string SpecNumber, string Key, string State, IReadOnlyList<VerificationView> Records, string Banner);

// ---------------------------------------------------------------- coverage, matrix, trace

public sealed record CoverageRow(
    string Key, string Outline, string Title, string Status, string Method, string Priority, int Allocations, int PlannedVerifications, string VerificationState, int SuspectLinks,
    bool InScope, bool Unallocated, bool Unverified, string Banner, Markings Markings);

/// <summary>Coverage over what the caller can read: restricted requirements and links to restricted objects are not counted (so a count never reveals one).</summary>
public sealed record CoverageSummary(
    int Total, int InScope, int Allocated, int Unallocated, int Verified, int Waived, int Failed, int Stale, int NotVerified, int Planned, int SuspectLinks, int Gaps);

public sealed record CoverageReport(
    string SpecNumber, string Revision, int Iteration, string? Baseline, CoverageSummary Summary, IReadOnlyList<CoverageRow> Rows, string Banner);

public sealed record MatrixColumn(string Id, string Kind, string Label, string? Href);
public sealed record MatrixCell(string RowKey, string ColumnId, Guid LinkId, bool Suspect);
public sealed record TraceMatrix(string LinkType, string LinkLabel, IReadOnlyList<RequirementRow> Rows, IReadOnlyList<MatrixColumn> Columns, IReadOnlyList<MatrixCell> Cells, bool ColumnsTruncated, string Banner);

/// <summary>Everything the traceability views of one specification revision are built from: the readable requirements, the links with a readable other end, the verification state.</summary>
public sealed record SpecTrace(
    string SpecNumber, string Revision, int Iteration, IReadOnlyList<RequirementRow> Rows, IReadOnlyList<SpecLink> Links, IReadOnlyDictionary<string, string> Verification,
    IReadOnlyDictionary<string, VerificationSummary> LastVerification, string Banner);

/// <summary>A link of a requirement with its other end resolved (or redacted).</summary>
public sealed record SpecLink(
    Guid Id, string SourceKey, string LinkType, string Label, string CoverageRole, string TargetKind, bool Redacted, string? TargetNumber, string? TargetKey, string? TargetName,
    string? TargetRevision, bool Suspect, string? SuspectReason, string? Banner, Markings? Markings);

public sealed record VerificationSummary(string Result, DateTimeOffset At, bool Formal);

/// <summary>The requirements that trace to a part, document or change, with their verification state. Restricted ones appear as placeholders.</summary>
public sealed record ObjectTrace(string ObjectKind, string Key, string Name, IReadOnlyList<TracedRequirement> Requirements, string Banner);

public sealed record SweepResult(int Checked, int MarkedSuspect);
