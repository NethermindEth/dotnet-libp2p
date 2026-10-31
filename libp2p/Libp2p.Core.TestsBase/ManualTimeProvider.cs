// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>A controlled clock for protocol deadlines; callbacks run outside the clock's lock.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public int CreatedTimers { get { lock (_lock) return _timers.Count; } }
    public int ActiveTimers { get { lock (_lock) return _timers.Count(t => !t.Disposed && t.Due is not null); } }
    public int DisposedTimers { get { lock (_lock) return _timers.Count(t => t.Disposed); } }
    public int ActiveTimersFor(TimeSpan dueTime)
    {
        lock (_lock) return _timers.Count(t => !t.Disposed && t.Due is not null && t.InitialDueTime == dueTime);
    }
    public TimeSpan[] DueTimes { get { lock (_lock) return _timers.Select(t => t.InitialDueTime).ToArray(); } }

    public override DateTimeOffset GetUtcNow() { lock (_lock) return DateTimeOffset.UnixEpoch + _elapsed; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            ManualTimer timer = new(this, callback, state, dueTime);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        List<ManualTimer> expired;
        lock (_lock)
        {
            _elapsed += elapsed;
            expired = _timers.Where(t => !t.Disposed && t.Due <= _elapsed).ToList();
            foreach (var timer in expired)
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : _elapsed + timer.Period;
        }
        foreach (var timer in expired) timer.Callback(timer.State);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public TimeSpan InitialDueTime { get; } = dueTime;
        public TimeSpan? Due { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                Period = period;
                return true;
            }
        }
        public void Dispose() { lock (owner._lock) { Disposed = true; Due = null; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
