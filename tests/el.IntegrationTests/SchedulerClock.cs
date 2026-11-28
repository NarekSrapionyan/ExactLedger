namespace el.IntegrationTests;

/// <summary>Manually advances the actual TimeProvider timers used by hosted jobs.</summary>
internal sealed class SchedulerClock(DateTimeOffset initial) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    internal int ScheduledCount { get { lock (_gate) return _timers.Count(t => !t.Disposed && t.Next != DateTimeOffset.MaxValue); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ClockTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        ClockTimer[] due;
        lock (_gate)
        {
            _now += elapsed;
            due = _timers.Where(t => !t.Disposed && t.Next <= _now).ToArray();
            foreach (var timer in due) timer.Next = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + timer.Period;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class ClockTimer(SchedulerClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal bool Disposed { get; private set; }
        internal DateTimeOffset Next { get; set; }
        internal TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Disposed) return false;
                Next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                Period = period;
                return true;
            }
        }
        public void Dispose() { lock (clock._gate) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
