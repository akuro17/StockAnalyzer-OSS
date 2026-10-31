using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Optimization;

/// <summary>Plan 04 / T3: window generation is a pure function of the bar count and three lengths in bars.</summary>
public sealed class WalkForwardWindowPlannerTests
{
    private static IReadOnlyList<WalkForwardWindow> Plan(int bars, WalkForwardMode mode, int inSample, int outOfSample, int step)
        => WalkForwardWindowPlanner.Plan(bars, mode, inSample, outOfSample, step);

    [Fact]
    public void RollingWindowsSlideByTheStepAndKeepTheInSampleLength()
    {
        IReadOnlyList<WalkForwardWindow> windows = Plan(20, WalkForwardMode.Rolling, 6, 2, 3);

        Assert.Equal(
            new[] { (0, 0, 5, 6, 7), (1, 3, 8, 9, 10), (2, 6, 11, 12, 13), (3, 9, 14, 15, 16), (4, 12, 17, 18, 19) },
            windows.Select(w => (w.Index, w.InSampleStart, w.InSampleEnd, w.OutOfSampleStart, w.OutOfSampleEnd)));
        Assert.All(windows, w => Assert.Equal(6, w.InSampleEnd - w.InSampleStart + 1));
    }

    [Fact]
    public void AnchoredWindowsKeepTheFirstBarAndGrow()
    {
        IReadOnlyList<WalkForwardWindow> windows = Plan(20, WalkForwardMode.Anchored, 6, 2, 4);

        Assert.Equal(
            new[] { (0, 0, 5, 6, 7), (1, 0, 9, 10, 11), (2, 0, 13, 14, 15), (3, 0, 17, 18, 19) },
            windows.Select(w => (w.Index, w.InSampleStart, w.InSampleEnd, w.OutOfSampleStart, w.OutOfSampleEnd)));
    }

    [Theory]
    [InlineData(WalkForwardMode.Rolling)]
    [InlineData(WalkForwardMode.Anchored)]
    public void TheOutOfSamplePartIsAlwaysDirectlyAfterTheInSamplePartAndNeverPastTheLastBar(WalkForwardMode mode)
    {
        foreach (int bars in new[] { 0, 1, 7, 8, 9, 50, 101 })
        {
            foreach (int step in new[] { 1, 2, 5, 13 })
            {
                IReadOnlyList<WalkForwardWindow> windows = Plan(bars, mode, 5, 3, step);
                Assert.All(windows, w =>
                {
                    Assert.Equal(w.InSampleEnd + 1, w.OutOfSampleStart);
                    Assert.True(w.InSampleStart <= w.InSampleEnd);
                    Assert.Equal(3, w.OutOfSampleEnd - w.OutOfSampleStart + 1);
                    Assert.True(w.OutOfSampleEnd <= bars - 1);
                    Assert.True(w.InSampleEnd < w.OutOfSampleStart);
                });
                Assert.Equal(Enumerable.Range(0, windows.Count), windows.Select(w => w.Index));
            }
        }
    }

    [Fact]
    public void ALastWindowWhoseOutOfSamplePartWouldBeShortIsNotMade()
    {
        // IS 5, OOS 3: the first window needs 8 bars. 8 bars -> one window, 7 bars -> none.
        Assert.Single(Plan(8, WalkForwardMode.Rolling, 5, 3, 100));
        Assert.Equal(1, Plan(8, WalkForwardMode.Rolling, 5, 3, 1).Count);
        Assert.Empty(Plan(7, WalkForwardMode.Rolling, 5, 3, 1));
        Assert.Empty(Plan(7, WalkForwardMode.Anchored, 5, 3, 1));
    }

    [Fact]
    public void WindowsOverlapWhenTheStepIsShorterThanTheWindowAndDoNotWhenItIsAsLong()
    {
        IReadOnlyList<WalkForwardWindow> overlapping = Plan(40, WalkForwardMode.Rolling, 10, 5, 5);
        IReadOnlyList<WalkForwardWindow> tiled = Plan(40, WalkForwardMode.Rolling, 10, 5, 15);

        Assert.True(overlapping[1].InSampleStart <= overlapping[0].OutOfSampleEnd);
        Assert.Equal(tiled[0].OutOfSampleEnd + 1, tiled[1].InSampleStart);
    }

    [Fact]
    public void NoBarsGiveNoWindows()
    {
        Assert.Empty(Plan(0, WalkForwardMode.Rolling, 1, 1, 1));
        Assert.Single(Plan(2, WalkForwardMode.Rolling, 1, 1, int.MaxValue));
    }

    [Fact]
    public void HugeLengthsDoNotOverflow()
    {
        Assert.Empty(Plan(1000, WalkForwardMode.Rolling, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.Empty(Plan(1000, WalkForwardMode.Anchored, int.MaxValue, 1, int.MaxValue));
        Assert.Single(Plan(1000, WalkForwardMode.Rolling, 1, 1, int.MaxValue));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(-1, 1, 1)]
    public void ALengthOrStepBelowOneIsRefused(int inSample, int outOfSample, int step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(100, WalkForwardMode.Rolling, inSample, outOfSample, step));
    }

    [Fact]
    public void ANegativeBarCountIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(-1, WalkForwardMode.Rolling, 1, 1, 1));
    }

    [Fact]
    public void ThePlanIsDeterministic()
    {
        Assert.Equal(Plan(500, WalkForwardMode.Anchored, 100, 25, 25), Plan(500, WalkForwardMode.Anchored, 100, 25, 25));
    }
}
