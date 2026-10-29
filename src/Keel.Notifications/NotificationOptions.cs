namespace Keel.Notifications;

/// <summary>Configuration section <c>Keel:Notifications</c> (docs/ops/notifications.md). The defaults suit a single node.</summary>
public sealed class NotificationOptions
{
    public const string Section = "Keel:Notifications";

    /// <summary>The newest notifications kept per user; older ones are dropped when a new one is written.</summary>
    public int MaxPerUser { get; set; } = 500;
    /// <summary>Repeats of a collapsible notification (the same object changed again) inside this window update one row's counter.</summary>
    public int DedupeWindowMinutes { get; set; } = 5;
    /// <summary>Read notifications older than this many days are purged by the hosted retention job. 0 (the default) turns the job off: nothing is purged by age.</summary>
    public int RetentionDays { get; set; }
    public int PurgeIntervalHours { get; set; } = 6;
    /// <summary>Runs the hosted service that turns outbox events into notifications for watchers.</summary>
    public bool FanOutEnabled { get; set; } = true;
    public int FanOutPollSeconds { get; set; } = 3;
    public int FanOutBatchSize { get; set; } = 200;
    public int MaxWatchesPerUser { get; set; } = 500;
    public int MaxCommentsPerObject { get; set; } = 2000;
    public int CommentsPerMinutePerUser { get; set; } = 10;
    /// <summary>The bell refreshes this long after a notification is written, so the writing transaction has committed by then.</summary>
    public int PublishDelayMilliseconds { get; set; } = 400;

    /// <summary>The optional e-mail channel (<c>Keel:Notifications:Email</c>); off by default.</summary>
    public EmailOptions Email { get; set; } = new();

    public TimeSpan DedupeWindow => TimeSpan.FromMinutes(DedupeWindowMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxPerUser is < 10 or > 100_000) errors.Add("Keel:Notifications:MaxPerUser must be between 10 and 100000.");
        if (DedupeWindowMinutes is < 0 or > 1440) errors.Add("Keel:Notifications:DedupeWindowMinutes must be between 0 and 1440.");
        if (RetentionDays is < 0 or > 3650) errors.Add("Keel:Notifications:RetentionDays must be between 0 (off) and 3650.");
        if (PurgeIntervalHours is < 1 or > 168) errors.Add("Keel:Notifications:PurgeIntervalHours must be between 1 and 168.");
        if (FanOutPollSeconds is < 1 or > 300) errors.Add("Keel:Notifications:FanOutPollSeconds must be between 1 and 300.");
        if (FanOutBatchSize is < 1 or > 5000) errors.Add("Keel:Notifications:FanOutBatchSize must be between 1 and 5000.");
        if (MaxWatchesPerUser is < 1 or > 100_000) errors.Add("Keel:Notifications:MaxWatchesPerUser must be between 1 and 100000.");
        if (MaxCommentsPerObject is < 1 or > 100_000) errors.Add("Keel:Notifications:MaxCommentsPerObject must be between 1 and 100000.");
        if (CommentsPerMinutePerUser is < 1 or > 600) errors.Add("Keel:Notifications:CommentsPerMinutePerUser must be between 1 and 600.");
        if (PublishDelayMilliseconds is < 0 or > 10_000) errors.Add("Keel:Notifications:PublishDelayMilliseconds must be between 0 and 10000.");
        errors.AddRange(Email.Validate());
        return errors;
    }
}

/// <summary>The size limits that bound what one user or one object can accumulate.</summary>
public static class AwarenessLimits
{
    public const int CommentLength = 4000;
    public const int DeleteReasonLength = 500;
    public const int FlyoutSize = 8;
    public const int PageSize = 25;
    public const int MaxMentionsPerComment = 10;
}

/// <summary>The pure rule that collapses repeated notifications (unit-tested without a database).</summary>
public static class NotificationDedupe
{
    /// <summary>
    /// True when a new notification of a collapsible kind should be folded into an existing one: the existing one is still unread and was written
    /// (or last bumped) no more than <paramref name="window"/> ago. A read notification is never reopened: the user has dealt with it, so the next change is news again.
    /// </summary>
    public static bool ShouldCollapse(bool collapsibleKind, DateTimeOffset existingAt, bool existingRead, DateTimeOffset now, TimeSpan window) =>
        collapsibleKind && !existingRead && window > TimeSpan.Zero && now - existingAt <= window && now >= existingAt;
}
