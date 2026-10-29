using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Process;

public static class PlanStates
{
    public const string InWork = "InWork";
    public const string UnderReview = "UnderReview";
    public const string Released = "Released";
    public const string Obsolete = "Obsolete";

    public static string Label(string state) => state switch { InWork => "In work", UnderReview => "Under review", Released => "Released", Obsolete => "Obsolete", _ => state };
}

public static class StepKinds
{
    public const string Step = "step";
    public const string Note = "note";
    public const string Caution = "caution";
    public const string Warning = "warning";
    public const string Quality = "quality";

    public static readonly ImmutableArray<string> All = [Step, Note, Caution, Warning, Quality];

    public static string Label(string kind) => kind switch
    {
        Step => "Step", Note => "Note", Caution => "Caution (safety)", Warning => "Warning (safety)", Quality => "Quality check", _ => kind,
    };

    /// <summary>A symbol that accompanies the word (never colour alone).</summary>
    public static string Glyph(string kind) => kind switch { Warning => "!!", Caution => "!", Quality => "Q", Note => "i", _ => "" };

    public static bool IsCallout(string kind) => kind is Caution or Warning or Quality;
}

public static class ToolKinds
{
    public const string Tool = "tool";
    public const string Fixture = "fixture";
    public const string Gauge = "gauge";

    public static readonly ImmutableArray<string> All = [Tool, Fixture, Gauge];

    public static string Label(string kind) => kind switch { Tool => "Tool", Fixture => "Fixture", Gauge => "Gauge", _ => kind };
}

public static class ResourceKinds
{
    public static readonly ImmutableArray<string> All = ["work_center", "machine", "station", "labor"];

    public static string Label(string kind) => kind switch { "work_center" => "Work center", "machine" => "Machine", "station" => "Station", "labor" => "Labor", _ => kind };
}

// ---------------------------------------------------------------- the content of one plan iteration (stored as one JSON document, immutable once checked in)

/// <summary>An assignment of an MBOM line to an operation: the line is identified by find number and alternate; <see cref="Quantity"/> null means the whole line quantity.</summary>
public sealed record ComponentAssignment(Guid PartMasterId, int Find, int Alt, decimal? Quantity);

public sealed record AttachmentDef(string Document, Guid FileId, string Caption);

public sealed record StepDef(Guid Id, string Kind, string Text, IReadOnlyList<Guid> Tools, IReadOnlyList<AttachmentDef> Attachments);

public sealed record OperationDef(
    Guid Id, int Number, string Name, string? WorkCenter, decimal? SetupMinutes, decimal? RunMinutes, string? Condition, bool Inspection, string? InspectionText,
    IReadOnlyList<ComponentAssignment> Components, IReadOnlyList<Guid> Tools, IReadOnlyList<StepDef> Steps);

public sealed record RoutingDef(string Code, string Name, bool IsPrimary, string? Condition, string? Note, IReadOnlyList<OperationDef> Operations);

public sealed record PlanContent(IReadOnlyList<RoutingDef> Routings)
{
    public static readonly PlanContent Empty = new([new RoutingDef("PRIMARY", "Primary routing", true, null, null, [])]);
}

// ---------------------------------------------------------------- findings and staleness

public static class Severities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

public sealed record PlanFinding(string Severity, string Code, string Message, string? Routing = null, int? Operation = null);

public enum Staleness { Current, Stale, NoReleasedMbom }

// ---------------------------------------------------------------- read models

public sealed record RevisionRow(string Revision, string State, string StateLabel, int LatestIteration, bool IsLatest, string BoundMbomRevision, DateTimeOffset StateChangedAt, string StateChangedBy);

public sealed record PlanSummary(
    string Number, string MbomNumber, string PartNumber, string PartName, string Plant, string Revision, string State, string StateLabel, string IterationLabel,
    bool IsOpen, string? OpenBy, string BoundMbomRevision, Staleness Stale, string? MbomReleasedRevision, string Banner, Markings Markings)
{
    public string StaleText => Stale switch
    {
        Staleness.Stale => $"Stale: written against MBOM revision {BoundMbomRevision}, which is no longer the released revision ({MbomReleasedRevision})",
        Staleness.NoReleasedMbom => "The MBOM has no released revision any more",
        _ => "Current with the released MBOM",
    };
}

public sealed record PlanResults(IReadOnlyList<PlanSummary> Items, int Total, string Banner);

public sealed record ComponentView(int Find, int Alt, bool Redacted, string? PartNumber, string? PartName, decimal LineQuantity, string Unit, decimal AssignedQuantity, bool Whole, string? Banner, bool PartChanged);

public sealed record ToolView(Guid Id, bool Restricted, string? Number, string? Name, string? Kind, string? Status, DateOnly? DueOn, string? Banner, bool CalibrationOverdue);

public sealed record AttachmentView(string Document, Guid FileId, string Caption, string Availability, string? FileName, bool IsImage, string? Banner, string? ContentUrl, string? ViewUrl);

public sealed record StepView(Guid Id, string Kind, string KindLabel, string Text, IReadOnlyList<ToolView> Tools, IReadOnlyList<AttachmentView> Attachments);

public sealed record OperationView(
    Guid Id, int Number, string Name, string? WorkCenter, string? WorkCenterName, decimal? SetupMinutes, decimal? RunMinutes, string? Condition, bool Inspection, string? InspectionText,
    IReadOnlyList<ComponentView> Components, IReadOnlyList<ToolView> Tools, IReadOnlyList<StepView> Steps);

public sealed record RoutingView(string Code, string Name, bool IsPrimary, string? Condition, string? Note, IReadOnlyList<OperationView> Operations);

/// <summary>A line of the bound MBOM revision, as the reader may see it.</summary>
public sealed record BoundLineView(int Find, int Alt, bool Redacted, string? PartNumber, string? PartName, decimal Quantity, string Unit, bool Phantom, string? SupplyType, Guid MasterId = default);

public sealed record PlanPermissions(bool CanModify, bool CanAdminister, bool IsCheckoutOwner, bool CanCheckOut, bool CanRevise);

public sealed record PlanDetail(
    PlanSummary Summary, string Description, IReadOnlyList<RevisionRow> Revisions, IReadOnlyList<RoutingView> Routings, IReadOnlyList<BoundLineView> Lines,
    IReadOnlyList<PlanFinding> Findings, bool IsWorkingCopy, DateTimeOffset CreatedAt, string CreatedBy, PlanPermissions Permissions);

public sealed record ShopFloorView(
    string PlanNumber, string PartNumber, string PartName, string Plant, string Revision, string BoundMbomRevision, Staleness Stale, string? MbomReleasedRevision,
    string Banner, RoutingView Routing, IReadOnlyList<string> OtherRoutings, DateTimeOffset GeneratedAt);

public sealed record ToolSummary(
    Guid Id, string Number, string Name, string Kind, string Plant, string Status, bool RequiresCalibration, int? IntervalDays, DateOnly? LastCalibratedOn, DateOnly? DueOn, string CalibrationState, string Banner, Markings Markings);

public sealed record CalibrationRecord(DateOnly CalibratedOn, DateOnly NextDueOn, string? Reference, string? Note, DateTimeOffset RecordedAt, string RecordedBy);

public sealed record ToolPlanUse(string PlanNumber, string PartNumber, string Plant, string Revision, string State, string Banner);

public sealed record ToolDetail(
    ToolSummary Summary, string? Description, int Revision, IReadOnlyList<CalibrationRecord> History, IReadOnlyList<ToolPlanUse> UsedBy, bool CanModify);

public sealed record ToolResults(IReadOnlyList<ToolSummary> Items, int Total, string Banner);

public sealed record WorkCenterInfo(Guid Id, string Plant, string Code, string Name, string ResourceKind, bool IsActive);

// ---------------------------------------------------------------- commands

public sealed record CreatePlanCommand(string MbomNumber, string? Description, Markings? Markings = null, bool ConfirmRaiseMarkings = false);

public sealed record PlanSearch(string? Text = null, string? Plant = null, string? State = null, bool StaleOnly = false, int Limit = 50, int Offset = 0, SortSpec? Sort = null);

public sealed record CreateToolCommand(
    string Name, string Kind, string Plant, string? Description, bool RequiresCalibration, int? IntervalDays, DateOnly? LastCalibratedOn, Markings Markings);

public sealed record UpdateToolCommand(string Name, string? Description, bool RequiresCalibration, int? IntervalDays);

public sealed record ToolSearch(string? Text = null, string? Plant = null, string? Kind = null, bool DueOnly = false, bool IncludeRetired = false, int Limit = 50, int Offset = 0, SortSpec? Sort = null);

public sealed record SaveWorkCenterCommand(string Plant, string Code, string Name, string ResourceKind, bool IsActive);

public sealed class ProcessOptions
{
    /// <summary>The calibration expiry check runs on a timer in the host; it is idempotent, so several nodes are harmless.</summary>
    public bool ExpiryJobEnabled { get; set; } = true;
    public int CalibrationCheckMinutes { get; set; } = 360;
}
