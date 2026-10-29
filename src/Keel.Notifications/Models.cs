using Keel.Core;

namespace Keel.Notifications;

public enum NotificationFilter { All, Unread, Mentions, Tasks, Watching }

/// <summary>
/// One notification as the viewer sees it, rendered just now. A notification whose object the viewer can no longer read is <see cref="Restricted"/>: it has no
/// number, name, kind, actor, link or banner, only the neutral text and its time (ADR-0030).
/// </summary>
public sealed record NotificationItem(
    Guid Id, bool Restricted, bool IsRead, int Occurrences, DateTimeOffset At, string Text, string? Group, string? Number, string? Name, string? ObjectLabel,
    string? Banner, Markings? Markings, string? ActorName, string? Href)
{
    public const string RestrictedText = "A notification you can no longer view";
}

public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, int Total, int Unread);

/// <summary>The center the bell, the flyout and the notifications page read and change. Every method works on the given subject's own rows only.</summary>
public interface INotificationCenter
{
    /// <summary>Unread notifications of the user, including rows that are now restricted (each counts once). A count of rows: it names nothing.</summary>
    Task<int> CountUnreadAsync(Subject subject, CancellationToken ct = default);

    Task<NotificationPage> ListAsync(Subject subject, NotificationFilter filter, int skip, int take, CancellationToken ct = default);

    /// <summary>Marks one of the user's notifications read (a notification of someone else is not touched).</summary>
    Task<bool> MarkReadAsync(Subject subject, Guid id, CancellationToken ct = default);

    Task<int> MarkReadAsync(Subject subject, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    Task<int> MarkAllReadAsync(Subject subject, CancellationToken ct = default);

    Task<NotificationPreferences> GetPreferencesAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Returns false for a kind that cannot be turned off when <paramref name="inApp"/> is false, and for an unknown kind.</summary>
    Task<bool> SetKindPreferenceAsync(Subject subject, string kind, bool inApp, bool notifyOwn, CancellationToken ct = default);

    Task SetAutoWatchAsync(Subject subject, bool autoWatch, CancellationToken ct = default);
}

public sealed record KindPreference(NotificationKind Kind, bool InApp, bool NotifyOwn);

public sealed record NotificationPreferences(bool AutoWatch, IReadOnlyList<KindPreference> Kinds);

public enum WatchLevel { All, Decisions }

public static class WatchLevels
{
    public static string ToDb(WatchLevel level) => level == WatchLevel.Decisions ? "decisions" : "all";
    public static WatchLevel FromDb(string value) => value == "decisions" ? WatchLevel.Decisions : WatchLevel.All;
}

/// <summary>One row of the watch list. An object the user can no longer read is <see cref="Available"/> = false and carries nothing but its opaque id and date.</summary>
public sealed record WatchItem(
    Guid Id, WatchLevel Level, DateTimeOffset Since, bool Available, string? Kind, string? Number, string? Name, string? StateLabel, string? Banner, Markings? Markings, string? Href);

public interface IWatchService
{
    Task<WatchLevel?> GetAsync(Subject subject, string kind, string number, CancellationToken ct = default);

    /// <summary>Starts or changes a watch. NotFound when the user may not read the object (audited as a denial); Conflict at the cap.</summary>
    Task<Outcome<WatchLevel>> SetAsync(Subject subject, string kind, string number, WatchLevel level, string? correlationId, CancellationToken ct = default);

    Task RemoveAsync(Subject subject, string kind, string number, CancellationToken ct = default);

    Task<bool> RemoveByIdAsync(Subject subject, Guid id, CancellationToken ct = default);

    Task<bool> SetLevelByIdAsync(Subject subject, Guid id, WatchLevel level, CancellationToken ct = default);

    Task<IReadOnlyList<WatchItem>> ListAsync(Subject subject, CancellationToken ct = default);
}

/// <summary>A comment as the viewer sees it. A removed comment is a tombstone: its text is empty unless the viewer is an auditor.</summary>
public sealed record CommentView(
    Guid Id, Guid? ParentId, string AuthorName, string AuthorSub, DateTimeOffset At, bool Removed, string? Body, DateTimeOffset? RemovedAt, string? RemovedByName, string? RemovedReason,
    bool CanDelete, IReadOnlyList<CommentView> Replies);

public sealed record CommentPage(IReadOnlyList<CommentView> Threads, int TotalComments, int TotalThreads, bool CanPost, bool IsAuditor);

public sealed record MentionCandidate(string Username, string DisplayName);

public sealed record PostedComment(Guid Id, int Mentioned);

public interface ICommentService
{
    /// <summary>Top-level comments with their replies. Not found when the viewer may not read the object (the denial is audited).</summary>
    Task<Outcome<CommentPage>> ListAsync(Subject subject, string kind, string number, bool newestFirst, int skip, int take, string? correlationId, CancellationToken ct = default);

    /// <summary>The number of live (not removed) comments the viewer can see; null when the viewer may not read the object.</summary>
    Task<int?> CountAsync(Subject subject, string kind, string number, CancellationToken ct = default);

    Task<Outcome<PostedComment>> PostAsync(Subject subject, string kind, string number, string body, Guid? parentId, string? correlationId, CancellationToken ct = default);

    Task<Outcome<bool>> DeleteAsync(Subject subject, Guid commentId, string reason, string? correlationId, CancellationToken ct = default);

    /// <summary>Directory users, whose stored attributes would let them read the object, whose login or name starts with <paramref name="prefix"/>.</summary>
    Task<Outcome<IReadOnlyList<MentionCandidate>>> MentionCandidatesAsync(Subject subject, string kind, string number, string prefix, CancellationToken ct = default);

    /// <summary>The objects a comment text names (<c>P-000015</c>), resolved for this viewer: only the ones the viewer may read come back.</summary>
    Task<IReadOnlyDictionary<string, ObjectChip>> ResolveReferencesAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default);
}

/// <summary>A readable object a comment refers to, shown as a chip.</summary>
public sealed record ObjectChip(string Kind, string Number, string Name, string Banner, string Href);

public sealed record ActivityEntry(
    long Seq, DateTimeOffset At, string ActorName, string Sentence, string Category, bool IsView, IReadOnlyList<ActivityDetail> Details, string? Href);

/// <summary>A safe field of an event for the "Show details" expander. <see cref="Href"/> is set for a link to another object the viewer may read.</summary>
public sealed record ActivityDetail(string Label, string Value, string? Href);

public sealed record ActivityPage(IReadOnlyList<ActivityEntry> Entries, long? NextBefore, bool IsAuditor);

public interface IActivityFeed
{
    /// <summary>Events about one object from the audit log, newest first, for a viewer who may read it (NotFound otherwise). <paramref name="before"/> pages by sequence number.</summary>
    Task<Outcome<ActivityPage>> GetAsync(Subject subject, string kind, string number, long? before, int take, bool includeReads, string? correlationId, CancellationToken ct = default);

    /// <summary>Latest events across the objects the user watches that the user may still read, newest first.</summary>
    Task<IReadOnlyList<WatchedActivity>> WatchedActivityAsync(Subject subject, int take, CancellationToken ct = default);
}

public sealed record WatchedActivity(string Kind, string Number, string Name, string Banner, Markings Markings, string Href, ActivityEntry Entry);
