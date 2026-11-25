using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// What an engine reports for one bar. <see cref="BullishFires"/> and <see cref="BearishFires"/> are the un-tied flags after the direction filter (both can be set on one bar);
/// <see cref="Direction"/> is the tie-broken answer (+1, -1, or 0 when nothing fires), <see cref="Score"/> and <see cref="ConfirmedAtIndex"/> are those of the event that won the tie.
/// <see cref="RuleCode"/> is for the rule-based types (Granville) and 0 otherwise.
/// </summary>
public readonly record struct PatternBarResult(bool BullishFires, bool BearishFires, int Direction, decimal? Score, int ConfirmedAtIndex, int RuleCode)
{
    /// <summary>True when any side fires (the value of <c>Main</c>).</summary>
    public bool Fires => BullishFires || BearishFires;
}

/// <summary>
/// The life cycle of the signals of a pattern engine (spec 7): an event lights the bars <c>[firstBar, firstBar + persistence]</c> (0 = the signal bar only), is cut by an invalidation (from the
/// invalidating bar on; the bars before keep their value, nothing is rewritten), and its key is consumed so that the same pivot tuple never fires again, not even after the window.
/// <para>
/// An engine adds events as it scans the bars in order and calls <see cref="InvalidateFrom"/> when it sees an invalidating bar; <see cref="Resolve"/> turns the events into one
/// <see cref="PatternBarResult"/> per bar. The direction filter is applied before the tie-break (plan R-13), and the tie-break is spec 1.2: the higher score, then the later confirmation,
/// then the bullish side.
/// </para>
/// </summary>
/// <typeparam name="TKey">The identity of the pivot tuple that the event consumes.</typeparam>
public sealed class PatternEventTimeline<TKey> where TKey : notnull
{
    private readonly int _barCount;
    private readonly HashSet<TKey> _consumed = new();
    private readonly List<Event> _events = new();

    /// <param name="barCount">Number of bars of the series; events are clamped to it.</param>
    public PatternEventTimeline(int barCount)
    {
        if (barCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(barCount));
        }

        _barCount = barCount;
    }

    /// <summary>Number of events added so far.</summary>
    public int EventCount => _events.Count;

    /// <summary>True when <paramref name="key"/> has fired before (the tuple is consumed).</summary>
    public bool IsConsumed(TKey key) => _consumed.Contains(key);

    /// <summary>
    /// Adds an event for <paramref name="key"/> unless it is consumed. <paramref name="direction"/> is +1 or -1; <paramref name="persistenceBars"/> is the number of bars after
    /// <paramref name="firstBar"/> that stay lit (0 = the signal bar only).
    /// </summary>
    /// <returns>True and the event id when the event was added; false when the key was consumed already (nothing is added).</returns>
    public bool TryFire(TKey key, int direction, int firstBar, int persistenceBars, decimal? score, int confirmedAtIndex, out int eventId)
    {
        if (direction != 1 && direction != -1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "The direction of an event is +1 or -1.");
        }

        if (persistenceBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(persistenceBars));
        }

        if (firstBar < 0 || firstBar >= _barCount)
        {
            throw new ArgumentOutOfRangeException(nameof(firstBar));
        }

        eventId = -1;
        if (!_consumed.Add(key))
        {
            return false;
        }

        long last = (long)firstBar + persistenceBars;
        _events.Add(new Event(direction, firstBar, (int)Math.Min(last, _barCount - 1), score, confirmedAtIndex));
        eventId = _events.Count - 1;
        return true;
    }

    /// <summary>
    /// The event stops at <paramref name="bar"/> (inclusive): that bar and every later bar of the event read 0; earlier bars keep their value (spec 7.2). An invalidation at or before the first bar
    /// cancels the event completely.
    /// </summary>
    public void InvalidateFrom(int eventId, int bar)
    {
        Event e = _events[eventId];
        if (bar < e.InvalidatedFrom)
        {
            _events[eventId] = e with { InvalidatedFrom = bar };
        }
    }

    /// <summary>One result per bar. A side that is not allowed (<paramref name="allowBullish"/>/<paramref name="allowBearish"/> false) is dropped before the tie-break.</summary>
    public PatternBarResult[] Resolve(bool allowBullish = true, bool allowBearish = true)
    {
        var bull = new Best[_barCount];
        var bear = new Best[_barCount];

        foreach (Event e in _events)
        {
            if ((e.Direction > 0 && !allowBullish) || (e.Direction < 0 && !allowBearish))
            {
                continue;
            }

            Best[] side = e.Direction > 0 ? bull : bear;
            int end = Math.Min(e.LastBar, e.InvalidatedFrom - 1);
            for (int b = e.FirstBar; b <= end; b++)
            {
                side[b] = side[b].Exists ? Better(side[b], e) : new Best(true, e.Score, e.ConfirmedAtIndex);
            }
        }

        var result = new PatternBarResult[_barCount];
        for (int b = 0; b < _barCount; b++)
        {
            Best u = bull[b];
            Best d = bear[b];
            if (!u.Exists && !d.Exists)
            {
                continue;
            }

            int direction;
            Best winner;
            if (u.Exists && !d.Exists)
            {
                direction = 1;
                winner = u;
            }
            else if (!u.Exists)
            {
                direction = -1;
                winner = d;
            }
            else
            {
                // Spec 1.2: score first (only when both sides have one and they differ), then the later confirmation, then bullish.
                int cmp = 0;
                if (u.Score.HasValue && d.Score.HasValue)
                {
                    cmp = u.Score.Value.CompareTo(d.Score.Value);
                }

                if (cmp == 0)
                {
                    cmp = u.ConfirmedAtIndex.CompareTo(d.ConfirmedAtIndex);
                }

                bool bullishWins = cmp >= 0;
                direction = bullishWins ? 1 : -1;
                winner = bullishWins ? u : d;
            }

            result[b] = new PatternBarResult(u.Exists, d.Exists, direction, winner.Score, winner.ConfirmedAtIndex, 0);
        }

        return result;
    }

    private static Best Better(Best current, Event candidate)
    {
        int cmp = 0;
        if (current.Score.HasValue && candidate.Score.HasValue)
        {
            cmp = candidate.Score.Value.CompareTo(current.Score.Value);
        }

        if (cmp == 0)
        {
            cmp = candidate.ConfirmedAtIndex.CompareTo(current.ConfirmedAtIndex);
        }

        return cmp > 0 ? new Best(true, candidate.Score, candidate.ConfirmedAtIndex) : current;
    }

    private readonly record struct Event(int Direction, int FirstBar, int LastBar, decimal? Score, int ConfirmedAtIndex, int InvalidatedFrom = int.MaxValue);

    private readonly record struct Best(bool Exists, decimal? Score, int ConfirmedAtIndex);
}
