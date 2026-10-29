using Bunit;
using Keel.Core;
using Keel.Notifications;
using Keel.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keel.UnitTests;

/// <summary>In-memory stand-ins for the awareness services (UX-4) so the bell, the lists, the comment thread, the watch button and the activity feed can be tested without a database.</summary>
internal static class AwarenessTestKit
{
    internal sealed class FakeCenter : INotificationCenter
    {
        public List<NotificationItem> Items { get; } = [];
        public int Unread { get; set; }
        public List<Guid> MarkedRead { get; } = [];
        public int MarkAllCalls { get; private set; }
        public int ListCalls { get; private set; }
        public Func<Task>? BeforeCount { get; set; }
        public bool ThrowLocked { get; set; }

        public async Task<int> CountUnreadAsync(Subject subject, CancellationToken ct = default)
        {
            if (ThrowLocked) throw new SessionLockedException();
            if (BeforeCount is not null) await BeforeCount();
            return Unread;
        }

        public Task<NotificationPage> ListAsync(Subject subject, NotificationFilter filter, int skip, int take, CancellationToken ct = default)
        {
            if (ThrowLocked) throw new SessionLockedException();
            ListCalls++;
            return Task.FromResult(new NotificationPage([.. Items.Skip(skip).Take(take)], Items.Count, Unread));
        }

        public Task<bool> MarkReadAsync(Subject subject, Guid id, CancellationToken ct = default)
        {
            MarkedRead.Add(id);
            Unread = Math.Max(0, Unread - 1);
            return Task.FromResult(true);
        }

        public Task<int> MarkReadAsync(Subject subject, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            MarkedRead.AddRange(ids);
            return Task.FromResult(ids.Count);
        }

        public Task<int> MarkAllReadAsync(Subject subject, CancellationToken ct = default)
        {
            MarkAllCalls++;
            Unread = 0;
            return Task.FromResult(0);
        }

        public Task<NotificationPreferences> GetPreferencesAsync(Subject subject, CancellationToken ct = default) =>
            Task.FromResult(new NotificationPreferences(true, [.. NotificationKinds.All.Select(k => new KindPreference(k, true, false))]));

        public Task<bool> SetKindPreferenceAsync(Subject subject, string kind, bool inApp, bool notifyOwn, CancellationToken ct = default) => Task.FromResult(true);

        public Task SetAutoWatchAsync(Subject subject, bool autoWatch, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeWatches : IWatchService
    {
        public Dictionary<(string, string), WatchLevel> Watched { get; } = [];
        public bool Deny { get; set; }

        public Task<WatchLevel?> GetAsync(Subject subject, string kind, string number, CancellationToken ct = default) =>
            Task.FromResult<WatchLevel?>(Watched.TryGetValue((kind, number), out var l) ? l : null);

        public Task<Outcome<WatchLevel>> SetAsync(Subject subject, string kind, string number, WatchLevel level, string? correlationId, CancellationToken ct = default)
        {
            if (Deny) return Task.FromResult(Outcome<WatchLevel>.NotFound());
            Watched[(kind, number)] = level;
            return Task.FromResult(Outcome<WatchLevel>.Ok(level));
        }

        public Task RemoveAsync(Subject subject, string kind, string number, CancellationToken ct = default)
        {
            Watched.Remove((kind, number));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveByIdAsync(Subject subject, Guid id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetLevelByIdAsync(Subject subject, Guid id, WatchLevel level, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<WatchItem>> ListAsync(Subject subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
    }

    internal sealed class FakeComments : ICommentService
    {
        public List<CommentView> Threads { get; } = [];
        public bool CanPost { get; set; } = true;
        public bool IsAuditor { get; set; }
        public List<(string Body, Guid? Parent)> Posted { get; } = [];
        public List<(Guid Id, string Reason)> Deleted { get; } = [];
        public List<MentionCandidate> Candidates { get; } = [];
        public Dictionary<string, ObjectChip> Chips { get; } = [];
        public bool NotFound { get; set; }

        public Task<Outcome<CommentPage>> ListAsync(Subject subject, string kind, string number, bool newestFirst, int skip, int take, string? correlationId, CancellationToken ct = default)
        {
            if (NotFound) return Task.FromResult(Outcome<CommentPage>.NotFound());
            var ordered = newestFirst ? Threads.OrderByDescending(t => t.At).ToList() : [.. Threads.OrderBy(t => t.At)];
            return Task.FromResult(Outcome<CommentPage>.Ok(new CommentPage([.. ordered.Skip(skip).Take(take)], Threads.Count(t => !t.Removed), Threads.Count, CanPost, IsAuditor)));
        }

        public Task<int?> CountAsync(Subject subject, string kind, string number, CancellationToken ct = default) => Task.FromResult<int?>(Threads.Count(t => !t.Removed));

        public Task<Outcome<PostedComment>> PostAsync(Subject subject, string kind, string number, string body, Guid? parentId, string? correlationId, CancellationToken ct = default)
        {
            Posted.Add((body, parentId));
            var id = Guid.CreateVersion7();
            var view = new CommentView(id, parentId, subject.DisplayName, subject.Id, DateTimeOffset.UtcNow, false, body, null, null, null, true, []);
            if (parentId is { } p)
            {
                var i = Threads.FindIndex(t => t.Id == p);
                if (i >= 0) Threads[i] = Threads[i] with { Replies = [.. Threads[i].Replies, view] };
            }
            else Threads.Add(view);
            return Task.FromResult(Outcome<PostedComment>.Ok(new PostedComment(id, 0)));
        }

        public Task<Outcome<bool>> DeleteAsync(Subject subject, Guid commentId, string reason, string? correlationId, CancellationToken ct = default)
        {
            Deleted.Add((commentId, reason));
            var i = Threads.FindIndex(t => t.Id == commentId);
            if (i >= 0) Threads[i] = Threads[i] with { Removed = true, Body = IsAuditor ? Threads[i].Body : null, CanDelete = false, RemovedAt = DateTimeOffset.UtcNow, RemovedByName = subject.DisplayName };
            return Task.FromResult(Outcome<bool>.Ok(true));
        }

        public Task<Outcome<IReadOnlyList<MentionCandidate>>> MentionCandidatesAsync(Subject subject, string kind, string number, string prefix, CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<MentionCandidate>>.Ok([.. Candidates.Where(c => c.Username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))]));

        public Task<IReadOnlyDictionary<string, ObjectChip>> ResolveReferencesAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ObjectChip>>(Chips.Where(c => numbers.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value));
    }

    internal sealed class FakeFeed : IActivityFeed
    {
        public List<ActivityEntry> Entries { get; } = [];
        public bool IsAuditor { get; set; }
        public bool NotFound { get; set; }
        public List<bool> IncludeReadsRequests { get; } = [];

        public Task<Outcome<ActivityPage>> GetAsync(Subject subject, string kind, string number, long? before, int take, bool includeReads, string? correlationId, CancellationToken ct = default)
        {
            IncludeReadsRequests.Add(includeReads);
            if (NotFound) return Task.FromResult(Outcome<ActivityPage>.NotFound());
            return Task.FromResult(Outcome<ActivityPage>.Ok(new ActivityPage([.. Entries.Where(e => includeReads || !e.IsView)], null, IsAuditor)));
        }

        public Task<IReadOnlyList<WatchedActivity>> WatchedActivityAsync(Subject subject, int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WatchedActivity>>([]);
    }

    public static NotificationItem Item(string number, string name, bool read = false, int occurrences = 1, DateTimeOffset? at = null, string text = "alice asked you to review") =>
        new(Guid.CreateVersion7(), false, read, occurrences, at ?? DateTimeOffset.UtcNow, text, "tasks", number, name, "Part", "UNCLASSIFIED · DISTRIBUTION A", null, "Alice Example", $"parts/{number}#review");

    public static NotificationItem Restricted(bool read = false) =>
        new(Guid.CreateVersion7(), true, read, 1, DateTimeOffset.UtcNow, NotificationItem.RestrictedText, null, null, null, null, null, null, null, null);

    /// <summary>Registers the fakes and the hub (the real in-memory one). Existing registrations win, so a test can supply its own fake first.</summary>
    public static (FakeCenter Center, FakeWatches Watches, FakeComments Comments, FakeFeed Feed, NotificationHub Hub) Register(BunitContext ctx)
    {
        var center = new FakeCenter();
        var watches = new FakeWatches();
        var comments = new FakeComments();
        var feed = new FakeFeed();
        var hub = new NotificationHub();
        ctx.Services.TryAddSingleton<INotificationCenter>(center);
        ctx.Services.TryAddSingleton<IWatchService>(watches);
        ctx.Services.TryAddSingleton<ICommentService>(comments);
        ctx.Services.TryAddSingleton<IActivityFeed>(feed);
        ctx.Services.TryAddSingleton<INotificationHub>(hub);
        ctx.Services.TryAddSingleton(TimeProvider.System);
        ctx.Services.TryAddScoped<CommentDrafts>();
        return (center, watches, comments, feed, hub);
    }
}
