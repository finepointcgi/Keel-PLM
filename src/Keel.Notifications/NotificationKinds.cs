using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Notifications;

/// <summary>The groups the notification page filters by (the tabs Mentions, Tasks, Watching).</summary>
public static class NotificationGroups
{
    public const string Mentions = "mentions";
    public const string Tasks = "tasks";
    public const string Watching = "watching";
}

/// <summary>
/// One kind of notification. The list below is data: adding a kind is adding a row (plus its generation point), and the preferences page, the filter
/// tabs, the sentences and the links all follow from it. A notification row stores only <see cref="Code"/>, never the sentence.
/// </summary>
/// <param name="Code">Stable identifier stored in <c>ntf.notification.kind</c>.</param>
/// <param name="Group">Which filter tab it belongs to.</param>
/// <param name="Label">Name on the preferences page.</param>
/// <param name="Description">One sentence for the preferences page.</param>
/// <param name="Sentence">What the notification says, with <c>{actor}</c> for the person who did it. Shown only after a read check.</param>
/// <param name="CanDisable">False for kinds that need the user's action: they cannot be turned off.</param>
/// <param name="LockedReason">Why it cannot be turned off, shown on the preferences page.</param>
/// <param name="Collapsible">Repeats within the dedupe window collapse into one notification with a counter.</param>
/// <param name="IsDecision">A release, approval or similar outcome: also delivered to watchers who chose "decisions only".</param>
/// <param name="Priority">Higher sorts first within the same minute and wins when one event makes several kinds for a person (a mention beats a comment).</param>
public sealed record NotificationKind(
    string Code, string Group, string Label, string Description, string Sentence, bool CanDisable, string? LockedReason, bool Collapsible, bool IsDecision, int Priority);

public static class NotificationKinds
{
    public const string TaskAssigned = NotificationKindCodes.TaskAssigned;
    public const string ReviewRequested = NotificationKindCodes.ReviewRequested;
    public const string ChangeTaskAssigned = NotificationKindCodes.ChangeTaskAssigned;
    public const string ReviewApproved = NotificationKindCodes.ReviewApproved;
    public const string ReviewRejected = NotificationKindCodes.ReviewRejected;
    public const string ChangeDecisionApproved = NotificationKindCodes.ChangeDecisionApproved;
    public const string ChangeDecisionRejected = NotificationKindCodes.ChangeDecisionRejected;
    public const string WatchedChanged = NotificationKindCodes.WatchedChanged;
    public const string WatchedState = NotificationKindCodes.WatchedState;
    public const string WatchedReleased = NotificationKindCodes.WatchedReleased;
    public const string WatchedObsoleted = NotificationKindCodes.WatchedObsoleted;
    public const string WatchedApproved = NotificationKindCodes.WatchedApproved;
    public const string WatchedEstablished = NotificationKindCodes.WatchedEstablished;
    public const string CommentAdded = NotificationKindCodes.CommentAdded;
    public const string CommentMention = NotificationKindCodes.CommentMention;
    public const string SupplierCertExpiring = NotificationKindCodes.SupplierCertExpiring;
    public const string SupplierCertExpired = NotificationKindCodes.SupplierCertExpired;
    public const string SupplierStatusChanged = NotificationKindCodes.SupplierStatusChanged;
    public const string ComplianceNonCompliant = NotificationKindCodes.ComplianceNonCompliant;
    public const string ComplianceDeclarationExpiring = NotificationKindCodes.ComplianceDeclarationExpiring;
    public const string ComplianceDeclarationExpired = NotificationKindCodes.ComplianceDeclarationExpired;
    public const string ComplianceSupplierDeclaration = NotificationKindCodes.ComplianceSupplierDeclaration;

    private const string TasksLocked = "A task or review that waits for you cannot be turned off: it would be missed.";
    private const string MentionLocked = "Someone asked for you by name: this cannot be turned off.";

    public static readonly ImmutableArray<NotificationKind> All =
    [
        new(CommentMention, NotificationGroups.Mentions, "Mentioned in a comment", "Someone wrote @your-name in a comment on an object you can read.",
            "{actor} mentioned you in a comment", false, MentionLocked, false, false, 100),
        new(TaskAssigned, NotificationGroups.Tasks, "Approval task assigned to you", "You were asked to approve something.",
            "{actor} asked for your approval", false, TasksLocked, false, false, 90),
        new(ReviewRequested, NotificationGroups.Tasks, "Review requested of you", "You were asked to review something.",
            "{actor} asked you to review", false, TasksLocked, false, false, 90),
        new(ChangeTaskAssigned, NotificationGroups.Tasks, "Change task assigned to you", "A task on a change notice was assigned to you.",
            "{actor} assigned you a change task", false, TasksLocked, false, false, 90),
        new(ReviewApproved, NotificationGroups.Tasks, "Review of your submission approved", "Your submission passed review and was released.",
            "Approved after review (last signature by {actor})", true, null, false, true, 80),
        new(ReviewRejected, NotificationGroups.Tasks, "Review of your submission rejected", "Your submission was rejected and returned to you.",
            "Rejected in review by {actor}", true, null, false, true, 80),
        new(ChangeDecisionApproved, NotificationGroups.Tasks, "Decision on a change you requested: approved", "A change request or notice you raised was approved or released.",
            "The change you requested was approved (decided by {actor})", true, null, false, true, 80),
        new(ChangeDecisionRejected, NotificationGroups.Tasks, "Decision on a change you requested: rejected", "A change request you raised was rejected.",
            "The change you requested was rejected (decided by {actor})", true, null, false, true, 80),
        new(CommentAdded, NotificationGroups.Watching, "Comment on something you follow", "A new comment on an object you watch, or a reply to your comment.",
            "{actor} commented", true, null, false, false, 50),
        new(WatchedReleased, NotificationGroups.Watching, "Watched object released", "An object you watch was released.",
            "Released (by {actor})", true, null, false, true, 40),
        new(WatchedObsoleted, NotificationGroups.Watching, "Watched object obsoleted", "An object you watch became obsolete.",
            "Obsoleted (by {actor})", true, null, false, true, 40),
        new(WatchedApproved, NotificationGroups.Watching, "Watched change approved", "A change request you watch was approved.",
            "Approved (by {actor})", true, null, false, true, 40),
        new(WatchedEstablished, NotificationGroups.Watching, "Baseline or manufacturing BOM established", "A baseline was established or a manufacturing BOM released for something you watch.",
            "Established or released (by {actor})", true, null, false, true, 40),
        new(SupplierCertExpired, NotificationGroups.Watching, "Supplier certification expired", "A certification of a supplier you watch has expired.",
            "A certification has expired", true, null, false, true, 45),
        new(SupplierCertExpiring, NotificationGroups.Watching, "Supplier certification expiring", "A certification of a supplier you watch expires soon.",
            "A certification expires soon", true, null, false, false, 35),
        new(SupplierStatusChanged, NotificationGroups.Watching, "Supplier approval status changed", "The approval status of a supplier you watch changed.",
            "The approval status changed (by {actor})", true, null, false, true, 42),
        new(ComplianceNonCompliant, NotificationGroups.Watching, "Part declaration became non-compliant", "A compliance declaration of a part you watch became non-compliant, for example after a regulatory list was updated.",
            "A compliance declaration became non-compliant", true, null, false, true, 46),
        new(ComplianceDeclarationExpired, NotificationGroups.Watching, "Part compliance declaration expired", "A compliance declaration of a part you watch has expired.",
            "A compliance declaration has expired", true, null, false, true, 44),
        new(ComplianceDeclarationExpiring, NotificationGroups.Watching, "Part compliance declaration expiring", "A compliance declaration of a part you watch expires soon.",
            "A compliance declaration expires soon", true, null, false, false, 34),
        new(ComplianceSupplierDeclaration, NotificationGroups.Watching, "Supplier declaration received", "A supplier-provided compliance declaration was recorded for a part you watch.",
            "{actor} recorded a compliance declaration provided by a supplier", true, null, false, false, 36),
        new(WatchedState, NotificationGroups.Watching, "Watched object changed state", "An object you watch moved to another lifecycle state.",
            "Changed state (by {actor})", true, null, true, false, 30),
        new(WatchedChanged, NotificationGroups.Watching, "Watched object changed", "A new version of an object you watch was checked in.",
            "Changed (by {actor})", true, null, true, false, 20),
    ];

    private static readonly ImmutableDictionary<string, NotificationKind> ByCode = All.ToImmutableDictionary(k => k.Code, StringComparer.Ordinal);

    public static NotificationKind? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var k) ? k : null;

    public static bool IsKnown(string? code) => Find(code) is not null;

    /// <summary>The kind codes of a filter tab (Mentions, Tasks, Watching).</summary>
    public static IReadOnlyList<string> CodesOf(string group) => [.. All.Where(k => k.Group == group).Select(k => k.Code)];

    /// <summary>
    /// Where a notification leads: the object's page, on the tab or anchor that matters (a comment lands on the comment, a task on its task page).
    /// The address is relative to the application base and built from the pointer alone.
    /// </summary>
    public static string Href(NotificationKind kind, string objectType, string objectNumber, Guid? refId)
    {
        var path = AwarenessKinds.Path(objectType, objectNumber);
        return kind.Code switch
        {
            CommentMention or CommentAdded => refId is { } c ? $"{path}?comment={c:D}#comments" : $"{path}#comments",
            TaskAssigned or ReviewRequested => refId is { } t ? $"tasks/{t:D}" : $"{path}#{AwarenessKinds.ReviewTab(objectType)}",
            ChangeTaskAssigned => $"{path}#tasks",
            ReviewApproved or ReviewRejected => $"{path}#{AwarenessKinds.ReviewTab(objectType)}",
            WatchedChanged or WatchedState => $"{path}#activity",
            ComplianceNonCompliant or ComplianceDeclarationExpiring or ComplianceDeclarationExpired or ComplianceSupplierDeclaration => $"{path}?tab=compliance",
            _ => path,
        };
    }

    /// <summary>The sentence of a kind with the actor's name filled in (or "Someone" when the actor is unknown).</summary>
    public static string Sentence(NotificationKind kind, string? actorName) =>
        kind.Sentence.Replace("{actor}", !string.IsNullOrWhiteSpace(actorName) ? actorName : kind.Sentence.StartsWith("{actor}", StringComparison.Ordinal) ? "Someone" : "someone", StringComparison.Ordinal);
}
