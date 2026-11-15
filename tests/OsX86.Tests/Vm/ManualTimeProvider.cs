namespace OsX86.Tests.Vm;

/// <summary>A clock the test moves by hand: timers fire only in <see cref="Advance"/>, so the grace and the watchdog are tested without waiting.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _ticks;
        }
    }

    /// <summary>How many timers are waiting.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock and fires every timer that became due, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks += (target - _now).Ticks;
                    _now = target;
                    return;
                }

                _ticks += (next.Due!.Value - _now).Ticks;
                _now = next.Due!.Value;
                next.Due = next.Period is { } period && period > TimeSpan.Zero ? _now + period : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
