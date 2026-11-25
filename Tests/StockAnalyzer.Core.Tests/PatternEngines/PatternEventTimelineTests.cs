using System;
using System.Linq;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Plan ChartPatterns56 V2, run V2-1: persistence, invalidation priority, consumed tuples (spec 7) and the tie-break (spec 1.2, plan R-5/R-13).</summary>
public class PatternEventTimelineTests
{
    private const int Bars = 30;

    private static string Lit(PatternBarResult[] r) => new(r.Select(x => x.Fires ? '1' : '0').ToArray());

    private static string Zeros(int n) => new('0', n);

    private static string Mask(int first, int last) => Zeros(first) + new string('1', last - first + 1) + Zeros(Bars - last - 1);

    private static PatternEventTimeline<int> NewTimeline() => new(Bars);

    [Fact]
    public void Persistence0_LightsTheSignalBarOnly()
    {
        PatternEventTimeline<int> t = NewTimeline();
        Assert.True(t.TryFire(1, 1, 10, 0, null, 10, out _));
        Assert.Equal(Mask(10, 10), Lit(t.Resolve()));
    }

    [Fact]
    public void Persistence5_LightsSixBars_AndIsClampedAtTheEndOfTheSeries()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 5, null, 10, out _);
        t.TryFire(2, -1, 27, 5, null, 27, out _);

        string expected = new(Enumerable.Range(0, Bars).Select(b => (b >= 10 && b <= 15) || b >= 27 ? '1' : '0').ToArray());
        Assert.Equal(expected, Lit(t.Resolve())); // 27 + 5 would be 32: clamped to the last bar (29)
    }

    [Fact]
    public void Invalidation_CutsFromThatBarOn_AndKeepsTheEarlierBars()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 5, null, 10, out int id);
        t.InvalidateFrom(id, 13);

        Assert.Equal(Mask(10, 12), Lit(t.Resolve()));
    }

    [Fact]
    public void Invalidation_AtOrBeforeTheFirstBar_CancelsTheEvent()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 5, null, 10, out int a);
        t.TryFire(2, 1, 20, 5, null, 20, out int b);
        t.InvalidateFrom(a, 10);
        t.InvalidateFrom(b, 5);

        Assert.Equal(Zeros(Bars), Lit(t.Resolve()));
    }

    [Fact]
    public void Invalidation_TwoCalls_TheEarlierBarWins()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 9, null, 10, out int id);
        t.InvalidateFrom(id, 16);
        t.InvalidateFrom(id, 12);
        t.InvalidateFrom(id, 18);

        Assert.Equal(Mask(10, 11), Lit(t.Resolve()));
    }

    [Fact]
    public void Consumed_TheSameKeyNeverFiresAgain_EvenAfterTheWindow()
    {
        PatternEventTimeline<(int, int)> t = new(Bars);
        Assert.True(t.TryFire((3, 7), 1, 10, 2, null, 10, out _));
        Assert.True(t.IsConsumed((3, 7)));

        Assert.False(t.TryFire((3, 7), 1, 25, 2, null, 25, out int id)); // long after the window of the first event
        Assert.Equal(-1, id);
        Assert.Equal(1, t.EventCount);
        Assert.True(t.TryFire((3, 8), 1, 25, 2, null, 25, out _));       // another tuple is free
    }

    [Fact]
    public void DirectionFilter_IsAppliedBeforeTheTieBreak()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 0, 0.4m, 10, out _);
        t.TryFire(2, -1, 10, 0, 0.9m, 10, out _);

        PatternBarResult both = t.Resolve()[10];
        Assert.Equal(-1, both.Direction); // the higher score wins the tie

        PatternBarResult bullOnly = t.Resolve(allowBullish: true, allowBearish: false)[10];
        Assert.True(bullOnly.BullishFires);
        Assert.False(bullOnly.BearishFires);
        Assert.Equal(1, bullOnly.Direction); // the disallowed side cannot hide the allowed one

        PatternBarResult bearOnly = t.Resolve(allowBullish: false, allowBearish: true)[10];
        Assert.False(bearOnly.BullishFires);
        Assert.Equal(-1, bearOnly.Direction);
    }

    [Fact]
    public void Tie_BothSidesFire_BothFlagsAreSet_AndTheScoreDecides()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 0, 0.7m, 99, out _);
        t.TryFire(2, -1, 10, 0, 0.8m, 5, out _);

        PatternBarResult r = t.Resolve()[10];
        Assert.True(r.BullishFires);
        Assert.True(r.BearishFires);
        Assert.Equal(-1, r.Direction); // the score beats the later confirmation
        Assert.Equal(0.8m, r.Score);
        Assert.Equal(5, r.ConfirmedAtIndex);
        Assert.True(r.Fires);
    }

    [Fact]
    public void Tie_EqualScores_TheLaterConfirmationWins()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 0, 0.5m, 8, out _);
        t.TryFire(2, -1, 10, 0, 0.5m, 9, out _);
        Assert.Equal(-1, t.Resolve()[10].Direction);
    }

    [Fact]
    public void Tie_NoScores_TheLaterConfirmationWins_ThenBullish()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 0, null, 8, out _);
        t.TryFire(2, -1, 10, 0, null, 9, out _);
        Assert.Equal(-1, t.Resolve()[10].Direction);

        PatternEventTimeline<int> same = NewTimeline();
        same.TryFire(1, 1, 10, 0, null, 9, out _);
        same.TryFire(2, -1, 10, 0, null, 9, out _);
        Assert.Equal(1, same.Resolve()[10].Direction); // explicit convention of spec 1.2
    }

    [Fact]
    public void SameSide_TheBestEventProvidesTheScore()
    {
        PatternEventTimeline<int> t = NewTimeline();
        t.TryFire(1, 1, 10, 3, 0.4m, 10, out _);
        t.TryFire(2, 1, 12, 3, 0.9m, 12, out _);

        Assert.Equal(0.4m, t.Resolve()[10].Score);
        Assert.Equal(0.9m, t.Resolve()[12].Score);
        Assert.Equal(0.9m, t.Resolve()[13].Score);
        Assert.Equal(0.9m, t.Resolve()[15].Score);
    }

    [Fact]
    public void BarsWithoutAnEvent_AreTheDefault()
    {
        PatternBarResult r = NewTimeline().Resolve()[3];
        Assert.False(r.Fires);
        Assert.Equal(0, r.Direction);
        Assert.Null(r.Score);
        Assert.Equal(0, r.RuleCode);
    }

    [Fact]
    public void BadArguments_Throw()
    {
        PatternEventTimeline<int> t = NewTimeline();
        Assert.Throws<ArgumentOutOfRangeException>(() => t.TryFire(1, 0, 10, 0, null, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.TryFire(1, 1, -1, 0, null, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.TryFire(1, 1, Bars, 0, null, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.TryFire(1, 1, 10, -1, null, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PatternEventTimeline<int>(-1));
        Assert.Equal(0, t.EventCount); // a rejected call consumes nothing
        Assert.False(t.IsConsumed(1));
    }
}
