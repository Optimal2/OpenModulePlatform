namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ManualTimeProvider : TimeProvider
{
    private long _currentTicks;
    private readonly List<ManualTimer> _timers = [];
    private readonly object _lock = new();

    public ManualTimeProvider(DateTimeOffset start)
    {
        _currentTicks = start.Ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return new DateTimeOffset(_currentTicks, TimeSpan.Zero);
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _currentTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime, period, this);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    /// True while any undisposed timer still has a due time scheduled. Tests
    /// use this to know the subject has registered its next wait before they
    /// advance time; advancing earlier silently strands the next timer a full
    /// interval in the future.
    /// </summary>
    public bool HasPendingTimer
    {
        get
        {
            lock (_lock)
            {
                return _timers.Any(static timer => timer.IsPending);
            }
        }
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Cannot advance time backward.");
        }

        ManualTimer[] timers;
        lock (_lock)
        {
            _currentTicks += delta.Ticks;
            timers = _timers.ToArray();
        }

        foreach (var timer in timers)
        {
            timer.Check();
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly ManualTimeProvider _provider;
        private long _dueTimeTicks;
        private long _periodTicks;
        private bool _disposed;

        public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period, ManualTimeProvider provider)
        {
            _callback = callback;
            _state = state;
            _provider = provider;
            _dueTimeTicks = dueTime == Timeout.InfiniteTimeSpan ? -1 : provider.GetTimestamp() + dueTime.Ticks;
            _periodTicks = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
        }

        public bool IsPending => !_disposed && _dueTimeTicks >= 0;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueTimeTicks = dueTime == Timeout.InfiniteTimeSpan ? -1 : _provider.GetTimestamp() + dueTime.Ticks;
            _periodTicks = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
            return true;
        }

        public void Check()
        {
            if (_disposed)
            {
                return;
            }

            var now = _provider.GetTimestamp();
            if (_dueTimeTicks < 0 || now < _dueTimeTicks)
            {
                return;
            }

            _dueTimeTicks = _periodTicks < 0 ? -1 : now + _periodTicks;

            _callback(_state);
        }

        public void Dispose()
        {
            _disposed = true;
            _provider.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
