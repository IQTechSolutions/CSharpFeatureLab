namespace FeatureLab.Web.Tests;

internal sealed class ManualTimeProvider(
    DateTimeOffset initialUtcNow) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = initialUtcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_sync)
        {
            _utcNow += duration;
            foreach (var timer in _timers.ToArray())
            {
                timer.CollectDueCallbacks(_utcNow, callbacks);
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    public void AdvanceWithoutRunningTimers(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        lock (_sync)
        {
            _utcNow += duration;
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? _next;
        private TimeSpan _period;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (_disposed)
                {
                    return false;
                }

                _next = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._utcNow + dueTime;
                _period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _next = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void CollectDueCallbacks(
            DateTimeOffset utcNow,
            ICollection<(TimerCallback Callback, object? State)> callbacks)
        {
            if (_disposed || _next is null)
            {
                return;
            }

            while (_next <= utcNow)
            {
                callbacks.Add((callback, state));
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _next = null;
                    return;
                }

                _next += _period;
            }
        }
    }
}
