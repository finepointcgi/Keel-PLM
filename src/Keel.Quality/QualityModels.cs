using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Quality;

/// <summary>The three kinds of quality record. A kind is also the audit object type, the workflow object type and the awareness kind.</summary>
public static class QualityKinds
{
    public const string Ncr = "ncr";
    public const string Capa = "capa";
    public const string Deviation = "deviation";
    public static readonly ImmutableArray<string> All = [Ncr, Capa, Deviation];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    public static string Table(string kind) => kind switch { Ncr => "qual.ncr", Capa => "qual.capa", Deviation => "qual.deviation", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public static string Family(string kind) => kind + "-default";
    public static string Sequence(string kind) => kind switch { Ncr => "qual.ncr_seq", Capa => "qual.capa_seq", Deviation => "qual.deviation_seq", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public static string Prefix(string kind) => kind switch { Ncr => "NCR", Capa => "CAPA", Deviation => "DEV", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    /// <summary>The column family naming the person who raised the record.</summary>
    public static string Raiser(string kind) => kind == Ncr ? "reporter" : "requester";

    public static string Label(string kind) => kind switch { Ncr => "Nonconformance report", Capa => "Corrective action", Deviation => "Deviation or waiver", _ => kind };
    public static string ShortLabel(string kind) => kind switch { Ncr => "NCR", Capa => "CAPA", Deviation => "Deviation", _ => kind };
    public static string ListHref(string kind) => kind switch { Ncr => "quality/ncrs", Capa => "quality/capas", Deviation => "quality/deviations", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
}

public static class NcrDispositions
{
    public const string UseAsIs = "UseAsIs";
    public const string Rework = "Rework";
    public const string Repair = "Repair";
    public const string Scrap = "Scrap";
    public const string ReturnToSupplier = "ReturnToSupplier";
    public static readonly IReadOnlyList<string> All = [UseAsIs, Rework, Repair, Scrap, ReturnToSupplier];

    public static string Label(string? d) => d switch
    {
        UseAsIs => "Use as is", Rework => "Rework", Repair => "Repair", Scrap => "Scrap", ReturnToSupplier => "Return to supplier", null => "Not proposed", _ => d,
    };
}

public static class NcrSeverities
{
    public static readonly IReadOnlyList<string> All = ["Minor", "Major", "Critical"];
    public static readonly IReadOnlyList<string> Stages = ["Receiving", "InProcess", "Final", "Field", "Audit", "Other"];
    public static string StageLabel(string s) => s == "InProcess" ? "In process" : s;
}

public static class CapaOptions
{
    public static readonly IReadOnlyList<string> Types = ["Corrective", "Preventive"];
    public static readonly IReadOnlyList<string> Sources = ["Ncr", "Audit", "Customer", "Process", "Other"];
    public static readonly IReadOnlyList<string> Priorities = ["Low", "Medium", "High", "Critical"];
    public static readonly IReadOnlyList<string> Categories = ["Man", "Machine", "Method", "Material", "Measurement", "Environment"];
    public const int MaxWhys = 8;
    public static string SourceLabel(string s) => s == "Ncr" ? "Nonconformance report" : s;
}

public static class DeviationKinds
{
    public const string Deviation = "Deviation";
    public const string Waiver = "Waiver";
    public static readonly IReadOnlyList<string> All = [Deviation, Waiver];
}

public static class ActionTypes
{
    public static readonly IReadOnlyList<string> Ncr = ["Containment", "Correction"];
    public static readonly IReadOnlyList<string> Capa = ["Corrective", "Preventive"];
}

public static class LinkKinds
{
    public static readonly IReadOnlyList<string> Targets = ["ncr", "capa", "deviation", "change", "part"];
    public static string Label(string kind) => kind switch { "change" => "Change", "part" => "Part", _ => QualityKinds.ShortLabel(kind) };
}

// ---------------------------------------------------------------- commands

public sealed record CreateNcrCommand(
    string Title, string? Description, string Severity, string Stage, string PartNumber, string? Revision, string? LotNumber, string? SerialNumber,
    string? SupplierRef, decimal Quantity, string? Unit, Markings Markings);

public sealed record UpdateNcrCommand(
    string Title, string? Description, string Severity, string Stage, string? LotNumber, string? SerialNumber, string? SupplierRef, decimal Quantity, string? Unit);

public sealed record ProposeDispositionCommand(string Disposition, string Rationale);

public sealed record CreateCapaCommand(
    string Title, string? Description, string CapaType, string Source, string Priority, string? Owner, DateOnly? TargetDate, Markings Markings);

public sealed record UpdateCapaCommand(string Title, string? Description, string CapaType, string Source, string Priority, string Owner, DateOnly? TargetDate);

public sealed record WhyInput(string Why, string? Evidence);
public sealed record CauseInput(string Category, string Description, bool IsRoot);
public sealed record SaveAnalysisCommand(string RootCause, IReadOnlyList<WhyInput> Whys, IReadOnlyList<CauseInput> Causes);
public sealed record SaveVerificationPlanCommand(string Method, string Criteria, DateOnly? DueDate);
public sealed record RecordVerificationCommand(string Result, bool Effective);

public sealed record CreateActionCommand(string ActionType, string Description, string Owner, DateOnly? DueDate);

public sealed record CreateDeviationCommand(
    string Kind, string Title, string? Description, string? RequirementRef, bool CustomerApprovalRequired, DateOnly ValidFrom, DateOnly ValidTo, Markings Markings);

public sealed record UpdateDeviationCommand(string Kind, string Title, string? Description, string? RequirementRef, bool CustomerApprovalRequired, DateOnly ValidFrom, DateOnly ValidTo);

public sealed record ScopeInput(string PartNumber, string? Revision, long? SerialFrom, long? SerialTo, IReadOnlyList<string>? Serials, IReadOnlyList<string>? Lots, decimal? MaxQuantity);

public sealed record CustomerApprovalCommand(string Reference, DateOnly ApprovedOn);

public sealed record QualitySearchQuery(string? Text = null, string? State = null, bool OpenOnly = false, int Limit = 50, int Offset = 0, SortSpec? Sort = null, string? PartNumber = null);

// ---------------------------------------------------------------- views

/// <summary>One row of a list. <see cref="Detail1"/> is the severity (NCR), priority (CAPA) or kind (deviation); <see cref="Detail2"/> the part, owner or window.</summary>
public sealed record QualitySummary(
    string Kind, Guid Id, string Number, string Title, string State, string StateLabel, string Detail1, string Detail2, string RaisedBy, DateTimeOffset CreatedAt,
    DateOnly? Due, bool Overdue, string Banner, Markings Markings);

public sealed record QualitySearchResult(IReadOnlyList<QualitySummary> Items, long Total, string PageBanner);

public sealed record ActionView(
    Guid Id, string Number, string ActionType, string Description, string OwnerName, string OwnerUsername, DateOnly? DueDate, bool Overdue, string Status,
    string? CompletionNote, string? CancelReason, bool CanStart, bool CanComplete, bool CanCancel);

/// <summary>A link to another record. Redacted when the viewer may not read the target now: nothing but the fact that a link exists is shown.</summary>
public sealed record LinkView(Guid Id, bool Redacted, string Direction, string Kind, string? Key, string? Title, string? StateLabel, string? Href, string? Banner, bool CanRemove);

public sealed record HistoryView(DateTimeOffset At, string Actor, string Action, string? Detail);

public sealed record QualityStepView(string To, string Label, string Via, bool Allowed, string? Reason, bool NeedsReason);

public sealed record QualityPermissions(bool CanModify, bool Editable, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record NcrDetail(
    Guid Id, string Number, string Title, string Description, string Severity, string Stage, string PartNumber, string PartRevision, string? LotNumber, string? SerialNumber,
    string? SupplierRef, decimal Quantity, string Unit, string? ProposedDisposition, string DispositionRationale, string? Disposition, string? DispositionNote, string? CancelReason,
    string State, string StateLabel, bool IsLocked, bool IsFinal, int Submission, Markings Markings, string Banner, string ReporterName, DateTimeOffset CreatedAt, DateTimeOffset StateChangedAt,
    IReadOnlyList<ActionView> Containment, IReadOnlyList<LinkView> Links, IReadOnlyList<HistoryView> History, IReadOnlyList<QualityStepView> Transitions,
    QualityPermissions Permissions, bool PartReadable, IReadOnlyList<string> Notices, IReadOnlyList<Markings> Shown);

public sealed record WhyView(int Seq, string Why, string Evidence);
public sealed record CauseView(Guid Id, string Category, string Description, bool IsRoot);

public sealed record CapaDetail(
    Guid Id, string Number, string Title, string Description, string CapaType, string Source, string Priority, string OwnerName, string OwnerUsername, DateOnly? TargetDate, bool Overdue,
    string RootCause, IReadOnlyList<WhyView> Whys, IReadOnlyList<CauseView> Causes,
    string VerificationMethod, string VerificationCriteria, DateOnly? VerificationDue, string VerificationResult, bool? VerificationEffective, string? VerificationBy, DateTimeOffset? VerificationAt,
    string? CancelReason, string State, string StateLabel, bool IsLocked, bool IsFinal, int Submission, Markings Markings, string Banner, string RequesterName,
    DateTimeOffset CreatedAt, DateTimeOffset StateChangedAt,
    IReadOnlyList<ActionView> Actions, IReadOnlyList<LinkView> Links, IReadOnlyList<HistoryView> History, IReadOnlyList<QualityStepView> Transitions,
    QualityPermissions Permissions, IReadOnlyList<string> Notices, IReadOnlyList<Markings> Shown);

public sealed record ScopeView(
    Guid Id, string PartNumber, string? Revision, long? SerialFrom, long? SerialTo, IReadOnlyList<string> Serials, IReadOnlyList<string> Lots, decimal? MaxQuantity, bool PartReadable, string? Banner);

public sealed record DeviationDetail(
    Guid Id, string Number, string Kind, string Title, string Description, string RequirementRef, bool CustomerApprovalRequired, string? CustomerApprovalRef, DateOnly? CustomerApprovalDate,
    string? CustomerApprovalBy, DateOnly ValidFrom, DateOnly ValidTo, bool IsActive, int? DaysLeft, string? CloseReason, string State, string StateLabel, bool IsLocked, bool IsFinal, int Submission,
    Markings Markings, string Banner, string RequesterName, DateTimeOffset CreatedAt, DateTimeOffset StateChangedAt,
    IReadOnlyList<ScopeView> Scope, IReadOnlyList<LinkView> Links, IReadOnlyList<HistoryView> History, IReadOnlyList<QualityStepView> Transitions,
    QualityPermissions Permissions, IReadOnlyList<string> Notices, IReadOnlyList<Markings> Shown);

/// <summary>A deviation or waiver that applies to a part (the part page, an applicability check). <see cref="Active"/> means approved with today inside its window.</summary>
public sealed record ApplicableDeviation(
    string Number, string Kind, string Title, string State, string StateLabel, DateOnly ValidFrom, DateOnly ValidTo, bool Active, string Applicability, bool CustomerApproved,
    string Banner, Markings Markings);

public sealed record PartQuality(
    IReadOnlyList<ApplicableDeviation> Deviations, IReadOnlyList<QualitySummary> OpenNcrs, IReadOnlyList<QualitySummary> OpenCapas, string PageBanner, IReadOnlyList<Markings> Shown);

/// <summary>Counts for the Home card. Every number counts only rows the caller may read; a zero never says whether restricted rows exist.</summary>
public sealed record QualityDashboardData(
    int OpenNcrs, int OpenCapas, int OverdueCapas, int OverdueActions, int MyOpenActions, int ActiveDeviations, int ExpiringDeviations, int OldestOpenNcrDays, string Banner);

public sealed record UserChoice(string Username, string DisplayName);
