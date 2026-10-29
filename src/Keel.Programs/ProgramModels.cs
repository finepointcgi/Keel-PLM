using Keel.Core;

namespace Keel.Programs;

// ---------------------------------------------------------------- commands

public sealed record CreateProgramCommand(string Code, string Name, string? Description, string? CustomerRef, string? ContractRef, string ManagerLogin, DateOnly? Start, DateOnly? End, Markings Markings);
public sealed record UpdateProgramCommand(string Name, string? Description, string? CustomerRef, string? ContractRef, string Status, string ManagerLogin, DateOnly? Start, DateOnly? End, DateOnly? ActualEnd);
public sealed record AddItemCommand(string Kind, string? ParentKey, string Title, string? Description, string? OwnerLogin, DateOnly Start, DateOnly End, string? GateType, Markings? Markings);
public sealed record UpdateItemCommand(string Title, string? Description, string? OwnerLogin, DateOnly Start, DateOnly End, string? Reason);
public sealed record SetProgressCommand(string Status, int Percent, DateOnly? ActualStart, DateOnly? ActualEnd);
public sealed record AddDeliverableCommand(string Title, string ObjectKind, string ObjectKey, DateOnly Due, string? ItemKey, string? OwnerLogin, Markings? Markings);
public sealed record ProgramSearchQuery(string? Text = null, string? Status = null, int Limit = 50, int Offset = 0, Keel.Core.SortSpec? Sort = null);

// ---------------------------------------------------------------- views

public sealed record ProgramSummary(string Code, string Name, string Status, string ManagerName, DateOnly? Start, DateOnly? End, Markings Markings)
{
    public string StatusLabel => ProgramStatuses.Label(Status);
    public string Banner => Markings.BannerText();
}

public sealed record ProgramSearchResult(IReadOnlyList<ProgramSummary> Items, int Total, string PageBanner);

public sealed record ProgramPermissions(bool CanModify, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record ProgramDetail(
    Guid Id, string Code, string Name, string Description, string CustomerRef, string ContractRef, string Status, string ManagerLogin, string ManagerName,
    DateOnly? Start, DateOnly? End, DateOnly? ActualEnd, Markings Markings, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ProgramPermissions Permissions)
{
    public string StatusLabel => ProgramStatuses.Label(Status);
    public string Banner => Markings.BannerText();
}

public sealed record ItemRow(
    Guid Id, string Key, string Kind, string? ParentKey, int SortOrder, string Title, string Description, string? OwnerLogin, string? OwnerName,
    DateOnly PlannedStart, DateOnly PlannedEnd, DateOnly? ActualStart, DateOnly? ActualEnd, string Status, int Percent,
    string? GateType, string? GateNumber, string? GateState,
    DateOnly? ForecastStart, DateOnly? ForecastEnd, int? FloatDays, bool Critical, int SlipDays, DateOnly? BaselineEnd, int? BaselineSlipDays,
    bool Overdue, bool CanModify, Markings Markings, bool HasPredecessors)
{
    public string StatusLabel => ItemStatuses.Label(Status);
    public string Banner => Markings.BannerText();
}

/// <summary>An item the viewer may not read: it keeps its place and time span and nothing else (no key, title, owner, gate or markings).</summary>
public sealed record RestrictedItemRow(int SortOrder, DateOnly PlannedStart, DateOnly PlannedEnd);

public sealed record DependencyRow(string PredecessorKey, string SuccessorKey, int LagDays);

public sealed record BaselineInfo(int Seq, string Name, DateTimeOffset CreatedAt, string CreatedBy, int ItemCount);

public sealed record ScheduleView(
    string Code, DateOnly AsOf, IReadOnlyList<ItemRow> Items, IReadOnlyList<RestrictedItemRow> Restricted, IReadOnlyList<DependencyRow> Dependencies,
    IReadOnlyList<string> CriticalPath, DateOnly? Finish, int? OverallPercent, IReadOnlyList<BaselineInfo> Baselines, BaselineInfo? Compared, bool Cyclic, string Banner);

public sealed record DeliverableRow(
    Guid Id, string Number, string Title, string ObjectKind, string ObjectKey, string? ObjectName, string? StateLabel, string Readiness, DateOnly Due, bool Overdue,
    string? ItemKey, string? OwnerName, string? OwnerLogin, bool CanModify, Markings Markings, Markings? Target)
{
    public string ReadinessLabel => Keel.Programs.Readiness.Label(Readiness);
    public string? TargetBanner => Target?.BannerText();
    public string? Href => ObjectName is null ? null : AwarenessKinds.Path(ObjectKind, ObjectKey);
}

public sealed record DeliverableList(IReadOnlyList<DeliverableRow> Items, string Banner);

public sealed record ChecklistRow(Guid Id, int Seq, string Text, bool Required, string Status, string Note, string? CheckedBy, DateTimeOffset? CheckedAt);

public sealed record GateDetail(
    string Number, string ProgramCode, string ProgramName, string ItemKey, string Title, string Description, string GateType, string State, DateOnly PlannedDate, DateOnly? ActualDate,
    string Status, string? OwnerName, IReadOnlyList<ChecklistRow> Checklist, IReadOnlyList<DeliverableRow> Deliverables, int Submission, bool CanModify, bool Editable,
    bool ChecklistComplete, Markings Markings, string Banner)
{
    public string StateLabel => GateStates.Label(State);
    public string TypeLabel => GateTypes.Label(GateType);
}

public sealed record CountBy(string Key, int Count);

public sealed record ProgramDashboard(
    string Code, DateOnly AsOf, int? Percent, int ItemCount, IReadOnlyList<CountBy> ByStatus, IReadOnlyList<ItemRow> Upcoming, IReadOnlyList<ItemRow> Overdue,
    IReadOnlyList<CountBy> DeliverableReadiness, int DeliverablesOverdue, int DeliverablesTotal, IReadOnlyList<ItemRow> Gates, int? CriticalCount, DateOnly? Finish,
    int MaxSlipDays, int SlippedCount, BaselineInfo? Baseline, string Banner);

public sealed record MyWorkItem(string ProgramCode, string ProgramName, string Kind, string Key, string Title, string Status, DateOnly Due, bool Overdue, string? GateNumber, Markings Markings);
public sealed record MyDeliverable(string ProgramCode, string Number, string Title, string Readiness, DateOnly Due, bool Overdue, Markings Markings);
public sealed record MyProgramWork(IReadOnlyList<MyWorkItem> Items, IReadOnlyList<MyDeliverable> Deliverables, string Banner);

public sealed record ProgramMaintenanceReport(int Due, int Overdue);
