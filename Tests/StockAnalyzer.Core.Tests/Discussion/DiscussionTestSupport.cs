#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// A clock that only moves when a test moves it. Timers created from it fire, in due order, while the clock is advanced past their due time, so
/// expiry, waits and the sweeper are exactly reproducible. (A real <see cref="TimeProvider"/> implementation; nothing from the project is faked.)
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that became due on the way, each at its own due time.</summary>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_sync)
        {
            target = _now + delta;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_sync)
            {
                due = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (due is null)
                {
                    _now = target;
                    return;
                }

                _now = due.Due!.Value > _now ? due.Due.Value : _now;
                due.Rearm();
            }

            due.Fire();
        }
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._sync)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                if (!_owner._timers.Contains(this))
                {
                    _owner._timers.Add(this);
                }
            }

            return true;
        }

        /// <summary>Called with the owner's lock held, before the callback runs: a periodic timer is scheduled again, a one-shot timer is disarmed.</summary>
        public void Rearm() => Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A hub on a manual clock, with short helpers for the steps nearly every test repeats.</summary>
internal sealed class HubRig : IDisposable
{
    public HubRig(Action<DiscussionSettings>? configure = null, bool sweeper = false, DiscussionIds? ids = null)
    {
        Settings = new DiscussionSettings();
        configure?.Invoke(Settings);
        Time = new ManualTimeProvider();
        Hub = new DiscussionHub(Settings, Time, ids, startSweeper: sweeper);
    }

    public DiscussionSettings Settings { get; }

    public ManualTimeProvider Time { get; }

    public DiscussionHub Hub { get; }

    public string NewSession(string title = "Title") => Hub.Create(title, null, DiscussionTurnMode.Free).Session.Id;

    public (string Token, string Handle) Join(string sessionId, string name)
    {
        DiscussionJoinResult joined = Hub.Join(sessionId, name);
        return (joined.ParticipantToken, joined.Handle);
    }

    public DiscussionPostResult Post(string sessionId, string token, string content, string? requestId = null)
        => Hub.Post(sessionId, token, new DiscussionPostRequest(content, ClientRequestId: requestId));

    /// <summary>All messages of a session as its first participant reads them.</summary>
    public IReadOnlyList<DiscussionReadMessage> Transcript(string sessionId, string token)
        => Hub.ReadAsync(sessionId, token, 0, Settings.MaxReadLimit).GetAwaiter().GetResult().Messages;

    public void Dispose() => Hub.Dispose();

    public static string CodeOf(Action action) => Assert.Throws<DiscussionException>(action).Code;

    public static DiscussionException Refused(Action action) => Assert.Throws<DiscussionException>(action);
}
