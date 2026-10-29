using System.Collections.Concurrent;

namespace Keel.Notifications;

/// <summary>What the bell needs to hear about: "something new may exist for this user". It carries no content.</summary>
public interface INotificationHub
{
    /// <summary>Calls <paramref name="onChanged"/> (on any thread) whenever a notification is written for the user. Dispose to stop. The signal carries nothing: the subscriber re-queries.</summary>
    IDisposable Subscribe(string userSub, Action onChanged);

    /// <summary>Tells the user's subscribers, after <paramref name="delay"/>, that something new may exist.</summary>
    void Publish(string userSub, TimeSpan delay);
}

/// <summary>
/// In-process, per-user publish/subscribe (singleton). Circuits subscribe for the bell; the notification writer publishes after it wrote a row. It is deliberately
/// content-free (the subscriber re-queries under its own access) and in memory: with several nodes a user's circuit only hears about notifications written
/// on its own node, and the bell's polling fallback (every 60 seconds) covers the rest (docs/ops/notifications.md).
/// </summary>
public sealed class NotificationHub : INotificationHub
{
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscribers = new(StringComparer.Ordinal);

    private sealed class Subscription(NotificationHub hub, string user, Action callback) : IDisposable
    {
        public Action Callback { get; } = callback;
        public void Dispose() => hub.Remove(user, this);
    }

    public int SubscriberCount(string userSub) => _subscribers.TryGetValue(userSub, out var list) ? Snapshot(list).Length : 0;

    public IDisposable Subscribe(string userSub, Action onChanged)
    {
        var sub = new Subscription(this, userSub, onChanged);
        var list = _subscribers.GetOrAdd(userSub, _ => []);
        lock (list) list.Add(sub);
        return sub;
    }

    private void Remove(string user, Subscription sub)
    {
        if (!_subscribers.TryGetValue(user, out var list)) return;
        lock (list)
        {
            list.Remove(sub);
            if (list.Count == 0) _subscribers.TryRemove(new KeyValuePair<string, List<Subscription>>(user, list));
        }
    }

    private static Subscription[] Snapshot(List<Subscription> list)
    {
        lock (list) return [.. list];
    }

    public void Publish(string userSub, TimeSpan delay)
    {
        if (!_subscribers.ContainsKey(userSub)) return;
        if (delay <= TimeSpan.Zero) { Deliver(userSub); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            Deliver(userSub);
        });
    }

    private void Deliver(string userSub)
    {
        if (!_subscribers.TryGetValue(userSub, out var list)) return;
        foreach (var s in Snapshot(list))
        {
            try { s.Callback(); }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { /* a circuit that is going away */ }
        }
    }
}
