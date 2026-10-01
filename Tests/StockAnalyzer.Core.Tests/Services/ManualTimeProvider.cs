#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Manual clock: delay timers fire only when <see cref="Advance"/> passes their due time; <see cref="TimerArmed"/> counts armed delays.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = now;
    public SemaphoreSlim TimerArmed { get; } = new(0);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    /// <summary>Moves the clock without firing timers (a later <see cref="Advance"/> fires the due ones).</summary>
    public void SetNow(DateTimeOffset value) { lock (_gate) _now = value; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
            foreach (var t in due) t.Disarm();
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; private set; }
        public void Disarm() => DueAt = null;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            if (dueTime != Timeout.InfiniteTimeSpan) owner.TimerArmed.Release();
            return true;
        }
        public void Dispose() { lock (owner._gate) { DueAt = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
