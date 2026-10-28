namespace Keel.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock and timers move only when the test says so: <see cref="Advance"/> moves the clock and fires every
/// timer that became due, in due-time order, synchronously. Used for session timers, grace periods and backoff, so tests never sleep.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override long GetTimestamp() { lock (_gate) return _now.UtcTicks; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    public void SetUtcNow(DateTimeOffset value) => Advance(value - GetUtcNow());

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) { _now = target; return; }
                if (next.Due > _now) _now = next.Due!.Value;
            }
            next.Fire();
        }
    }

    private void Remove(ManualTimer timer) { lock (_gate) _timers.Remove(timer); }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;
            callback(state);
        }

        public void Dispose() => owner.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
