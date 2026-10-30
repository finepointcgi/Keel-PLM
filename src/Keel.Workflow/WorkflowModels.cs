using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Workflow;

public static class InstanceStatus
{
    public const string Active = "Active";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public sealed record SlotView(string Name, string Label, string Role, int Min, int Max);

public sealed record TemplateView(
    string Id, int Version, string Name, string? Description, string ObjectType, bool IsActive, IReadOnlyList<SlotView> Slots,
    DateTimeOffset CreatedAt, string CreatedBy, JsonNode Definition);

/// <summary>Start a review. <c>Participants</c> maps slot name to users, each given as IdP subject or login name.</summary>
public sealed record StartReviewCommand(
    string ObjectType, string ObjectKey, IReadOnlyDictionary<string, string[]> Participants, string? TemplateId = null, string? Comment = null);

/// <summary>What a reader who may see the object learns about it.</summary>
public sealed record ObjectSummary(
    string ObjectType, string Key, string Name, string Revision, string IterationLabel, string StateLabel, string Banner, Markings Markings);

public sealed record TaskSummary(
    Guid Id, Guid InstanceId, string Name, string Kind, bool Redacted, ObjectSummary? Target, string? StartedByName,
    DateTimeOffset? StartedAt, DateTimeOffset? DueAt, bool Overdue, bool IsPool);

/// <summary>
/// The caller's open tasks. Tasks whose object the caller may no longer read are present but redacted: no object
/// name, number or markings, and they cannot be signed.
/// </summary>
public sealed record MyTasks(IReadOnlyList<TaskSummary> Items, string PageBanner);

public sealed record ReasonCodeView(string Code, string Label, string Decision, bool RequiresComment);

public sealed record SignatureView(
    Guid Id, Guid TaskId, Guid InstanceId, string SignerSub, string SignerUsername, string SignerName, string? SignerOrg,
    string MeaningCode, string Meaning, string Decision, string? Comment, DateTimeOffset SignedAt, string ObjectKey, string Revision,
    Guid IterationId, string IterationLabel, string MarkingBanner, string ContentHash, string SignatureHash, string Manifestation);

public sealed record TaskItem(
    Guid Id, string Name, string Kind, string Status, string? Decision, string? AssigneeSub, string? AssigneeUsername, string? AssigneeName,
    string RequiredRole, DateTimeOffset? DueAt, bool Overdue, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid? SignatureId);

public sealed record EventView(DateTimeOffset At, string ActorName, string Action, string? Detail);

public sealed record RejectionView(string By, string Username, string Meaning, string? Comment, DateTimeOffset At);

public sealed record InstanceView(
    Guid Id, string TemplateId, int TemplateVersion, string TemplateName, string Status, ObjectSummary Target,
    string StartedBy, string StartedByUsername, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? CompletionNote, string? Comment,
    IReadOnlyList<TaskItem> Tasks, IReadOnlyList<SignatureView> Signatures, IReadOnlyList<RejectionView> Rejections, IReadOnlyList<EventView> Events,
    bool CanCancel, bool CanReassign, string Banner);

public sealed record InstanceSummary(
    Guid Id, string Status, ObjectSummary Target, string TemplateName, string StartedBy, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int OpenTasks);

public sealed record InstancePage(IReadOnlyList<InstanceSummary> Items, long Total, string PageBanner);

/// <summary>
/// What a signature on this task will bind to, beyond what <see cref="TaskDetail.Summary"/> already names (object, revision, iteration, markings banner):
/// the SHA-256 content hash the signature will record (the same function as the signing step, so what is shown is what is signed), the submitter's note and
/// the template version the review runs. Present only for a reader who may read the object.
/// </summary>
// ASSUMPTION(A84): the change summary is the submitter's note plus the description and attributes; there is no per-type diff against the previous revision.
public sealed record ReviewSummary(string ContentHash, string? SubmitterNote, string TemplateName, int TemplateVersion);

public sealed record TaskDetail(
    TaskSummary Summary, string Status, string? Decision, string? Description, JsonObject? Attributes, InstanceView? Instance,
    IReadOnlyList<ReasonCodeView> ReasonCodes, bool CanSign, IReadOnlyList<string> CannotSignReasons,
    bool StepUpRequired, int StepUpMaxAgeSeconds, string Banner, ReviewSummary? Review = null);

/// <summary>Workflow state of one object, for its page: the running review, the latest one, and the release history.</summary>
public sealed record ObjectWorkflowStatus(InstanceView? Active, InstanceView? Last, IReadOnlyList<ReleaseRecord> Releases);

public sealed record ReleaseRecord(Guid InstanceId, string Revision, string IterationLabel, DateTimeOffset ReleasedAt, IReadOnlyList<SignatureView> Signatures);

public sealed record SignatureVerification(Guid SignatureId, bool Valid, IReadOnlyList<string> Problems, bool MarkingsChangedSince, string Manifestation);
