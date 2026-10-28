using Keel.Core;

namespace Keel.Change;

public static class ChangeTypes
{
    public const string Pr = "PR";
    public const string Ecr = "ECR";
    public const string Ecn = "ECN";
    public static readonly IReadOnlyList<string> All = [Pr, Ecr, Ecn];

    public static string LifecycleOf(string type) => type.ToLowerInvariant() + "-default";

    /// <summary>The object type used by the audit log and the workflow engine: "pr", "ecr", "ecn".</summary>
    public static string ObjectTypeOf(string type) => type.ToLowerInvariant();

    public static string TypeOfObjectType(string objectType) => objectType.ToUpperInvariant();

    public static string Label(string type) => type switch
    {
        Pr => "Problem report",
        Ecr => "Change request",
        Ecn => "Change notice",
        _ => type,
    };
}

public static class ChangeActions
{
    public const string Revise = "revise";
    public const string Obsolete = "obsolete";
    public const string None = "none";
    public static readonly IReadOnlyList<string> All = [Revise, Obsolete, None];
}

public static class ChangeClasses
{
    public const string I = "I";
    public const string II = "II";
    public static readonly IReadOnlyList<string> All = [I, II];
}

public static class ChangePriorities
{
    public static readonly IReadOnlyList<string> All = ["Low", "Medium", "High", "Critical"];
}

/// <summary>
/// Create a change object. <see cref="SourceNumber"/> names the object it descends from (a PR for an ECR, an ECR for an ECN).
/// Class I needs a source ECR that the CCB approved (A8); Class II may omit it.
/// </summary>
public sealed record CreateChangeCommand(
    string Type,
    string Title,
    string? Description,
    string Priority,
    string? Reason,
    Markings Markings,
    string? ChangeClass = null,
    string? SourceNumber = null);

/// <summary>Create from an existing change object. Anything left null is copied from the source; markings must cover the source's.</summary>
public sealed record CreateFromCommand(
    string? Title = null,
    string? Description = null,
    string? Priority = null,
    string? Reason = null,
    string? ChangeClass = null,
    Markings? Markings = null);

public sealed record UpdateChangeCommand(string Title, string? Description, string Priority, string? Reason);

/// <summary>
/// Add an object to a change. <see cref="Revision"/> names a revision explicitly (refused unless it is the latest). When the change's
/// markings do not cover the object's, <see cref="RaiseMarkings"/> says whether to raise them (CHG-3); otherwise the request is refused.
/// </summary>
public sealed record AddAffectedCommand(string ObjectType, string Key, string Action, string? Revision = null, bool RaiseMarkings = false);

public sealed record CreateTaskCommand(string Title, string? Description, string Assignee, DateOnly? DueDate);

public sealed record ChangeSearchQuery(string? Type = null, string? State = null, string? Text = null, bool OpenOnly = false, int Limit = 50, int Offset = 0);

public sealed record ChangeSummary(
    Guid Id, string Type, string Number, string Title, string State, string StateLabel, string? ChangeClass, string Priority,
    string RequesterName, DateTimeOffset CreatedAt, string Banner, Markings Markings);

public sealed record ChangeSearchResult(IReadOnlyList<ChangeSummary> Items, long Total, string PageBanner);

/// <summary>An affected object. Redacted when the viewer may not read it now: only the action is disclosed (A4).</summary>
public sealed record AffectedObjectView(
    Guid Id, bool Redacted, string ObjectType, string? Key, string? Name, string? Revision, string? State, string Action,
    string? ResultingRevision, string? ResultingState, string? Banner, DateTimeOffset AddedAt, string AddedBy);

public sealed record ChangeRef(string Type, string Number, string Title, string StateLabel, string Banner);

public sealed record TaskView(
    Guid Id, string Number, string Title, string Description, string State, string StateLabel, string AssigneeUsername, string AssigneeName,
    DateOnly? DueDate, bool Overdue, string? CompletionNote, string? CancelReason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    bool CanStart, bool CanComplete, bool CanCancel);

public sealed record HistoryView(DateTimeOffset At, string Actor, string Action, string? Detail);

/// <summary><see cref="ReasonScope"/> is set when the transition needs a reason code (see <c>chg.reason_code</c>).</summary>
public sealed record ChangeTransitionView(string To, string Label, string Via, bool Allowed, string? Reason, string? ReasonScope);

public sealed record ChangePermissions(bool CanModify, bool CanAdminister, bool Editable, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record ChangeDetail(
    Guid Id,
    string Type,
    string Number,
    string Title,
    string Description,
    string Priority,
    string? ChangeClass,
    string Reason,
    string RequesterName,
    string RequesterUsername,
    string State,
    string StateLabel,
    bool IsLocked,
    bool IsFinal,
    Markings Markings,
    string Banner,
    ChangeRef? Source,
    IReadOnlyList<ChangeRef> Derived,
    IReadOnlyList<AffectedObjectView> Affected,
    IReadOnlyList<TaskView> Tasks,
    IReadOnlyList<HistoryView> History,
    IReadOnlyList<ChangeTransitionView> Transitions,
    ChangePermissions Permissions,
    string? CloseReasonCode,
    string? CloseReason,
    int Submission,
    DateTimeOffset CreatedAt,
    DateTimeOffset StateChangedAt,
    /// <summary>One-off messages about what this call did beyond the request, e.g. that the change's markings were raised.</summary>
    IReadOnlyList<string> Notices);

public sealed record ReasonCodeView(string Code, string Label);

/// <summary>A change task the caller is assigned, with the change notice it belongs to.</summary>
public sealed record MyChangeTask(Guid Id, string Number, string Title, string State, string StateLabel, DateOnly? DueDate, bool Overdue, string EcnNumber, string EcnTitle, string Banner);

public sealed record MyChangeTasks(IReadOnlyList<MyChangeTask> Items, string PageBanner);

/// <summary>What changes touch one part: open requests and notices, and the notice a revision is bound to.</summary>
public sealed record ObjectChanges(IReadOnlyList<ChangeSummary> Open, ChangeSummary? BoundTo, bool BoundToHidden, string PageBanner);

// ---------------------------------------------------------------- impact analysis

public sealed record ImpactObject(
    Guid AffectedId, bool Redacted, string Action, string? Key, string? Name, string? Revision, string? Banner,
    IReadOnlyList<WhereUsedEntry> WhereUsed, int RedactedParents, int MaxLevel,
    IReadOnlyList<LinkedDocument> Documents, StructureComparison? Compare, string? CompareNote);

public sealed record ImpactConflict(string Kind, string ChangeType, string ChangeNumber, string Title, string StateLabel, string? ObjectKey, string Banner);

public sealed record ImpactSummary(
    int AffectedObjects, int HiddenObjects, int WhereUsedParents, int RedactedParents, int Documents, int RedactedDocuments, int OpenConflicts, int StructureChanges);

public sealed record ImpactReport(string Number, IReadOnlyList<ImpactObject> Objects, IReadOnlyList<ImpactConflict> Conflicts, ImpactSummary Summary, string Banner);
