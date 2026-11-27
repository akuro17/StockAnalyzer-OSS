using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The end-point snap of a fixed-period drawing range (settings expansion Run 5, DS-6 = B): the pure calculation.</summary>
public sealed class DrawingRangeSnapTests
{
    private const int Min = RobustRegressionMath.MinWindowLength;
    private const int Max = RobustRegressionTrendValues.MaxBars;
    private static readonly DateTime Start = new(2025, 1, 1);

    private static List<CoreCandleData> Bars(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CoreCandleData(Start.AddDays(i), 100m, 101m, 99m, 100m, 1000L));
        }

        return list;
    }

    private static DateTime T(int bar) => Start.AddDays(bar);

    private static RangeSnapResult Snap(List<CoreCandleData> candles, DateTime t1, DateTime t2, int period, RangeSnapKind kind, int dragged = -1)
    {
        Assert.True(DrawingRangeSnap.TryCompute(candles, t1, t2, period, Min, Max, kind, dragged, out RangeSnapResult result));
        return result;
    }

    [Fact]
    public void Placement_StartsOnTheEarlierBar_AndEndsPeriodMinusOneBarsLater()
    {
        RangeSnapResult r = Snap(Bars(100), T(10), T(15), 30, RangeSnapKind.Placement);

        Assert.Equal(10, r.StartIndex);
        Assert.Equal(39, r.EndIndex);
        Assert.Equal(T(10), r.StartTime);
        Assert.Equal(T(39), r.EndTime);
        Assert.Equal(30, r.Period);
        Assert.Equal(30, r.BarCount);
    }

    [Fact]
    public void Placement_RoundsAnAnchorBetweenBarsToTheNextBar()
    {
        RangeSnapResult r = Snap(Bars(100), T(10).AddHours(6), T(12), 30, RangeSnapKind.Placement);
        Assert.Equal(11, r.StartIndex);
        Assert.Equal(40, r.EndIndex);
    }

    [Fact]
    public void Placement_RightToLeft_GivesTheSameRangeAsLeftToRight()
    {
        List<CoreCandleData> candles = Bars(100);
        Assert.Equal(Snap(candles, T(10), T(25), 30, RangeSnapKind.Placement), Snap(candles, T(25), T(10), 30, RangeSnapKind.Placement));
    }

    [Fact]
    public void ARangeAtTheEndOfTheData_IsShorterThanThePeriodAndStopsAtTheLastBar()
    {
        RangeSnapResult r = Snap(Bars(100), T(90), T(95), 30, RangeSnapKind.BodyMove);

        Assert.Equal(90, r.StartIndex);
        Assert.Equal(99, r.EndIndex);
        Assert.Equal(10, r.BarCount);
        Assert.Equal(30, r.Period);   // the period setting is not shrunk
    }

    [Fact]
    public void NoBarToSnapTo_GivesNoResult()
    {
        List<CoreCandleData> candles = Bars(50);
        Assert.False(DrawingRangeSnap.TryCompute(candles, T(80), T(90), 30, Min, Max, RangeSnapKind.Placement, -1, out _));
        Assert.False(DrawingRangeSnap.TryCompute(new List<CoreCandleData>(), T(1), T(2), 30, Min, Max, RangeSnapKind.Placement, -1, out _));
        Assert.False(DrawingRangeSnap.TryCompute(null, T(1), T(2), 30, Min, Max, RangeSnapKind.Placement, -1, out _));
        Assert.False(DrawingRangeSnap.TryCompute(candles, T(1), T(2), 30, 0, Max, RangeSnapKind.Placement, -1, out _));
    }

    [Fact]
    public void LaterHandleRelease_WritesTheWidthBackAsThePeriod_AndKeepsTheStart()
    {
        // Anchors at bars 5 and 34 (a 30 bar range); the later handle is dragged to bar 24 (+3 hours): the bar at or before it is 24.
        RangeSnapResult r = Snap(Bars(100), T(5), T(24).AddHours(3), 30, RangeSnapKind.HandleRelease, dragged: 1);

        Assert.Equal(5, r.StartIndex);
        Assert.Equal(24, r.EndIndex);
        Assert.Equal(20, r.Period);
        Assert.Equal(T(24), r.EndTime);
    }

    [Fact]
    public void LaterHandleRelease_IsClampedToThePeriodLimits()
    {
        List<CoreCandleData> candles = Bars(Max + 200);

        RangeSnapResult wide = Snap(candles, T(5), T(Max + 80), 30, RangeSnapKind.HandleRelease, dragged: 1);
        Assert.Equal(Max, wide.Period);
        Assert.Equal(5 + Max - 1, wide.EndIndex);

        RangeSnapResult narrow = Snap(candles, T(5), T(5), 30, RangeSnapKind.HandleRelease, dragged: 1);
        Assert.Equal(Min, narrow.Period);
        Assert.Equal(5 + Min - 1, narrow.EndIndex);

        RangeSnapResult beyondData = Snap(candles, T(5), T(Max + 5000), 30, RangeSnapKind.HandleRelease, dragged: 1);
        Assert.Equal(Max, beyondData.Period);
    }

    [Fact]
    public void LaterHandleRelease_NearTheEndOfTheData_IsBoundedByTheLastBar()
    {
        RangeSnapResult r = Snap(Bars(100), T(90), T(500), 30, RangeSnapKind.HandleRelease, dragged: 1);

        Assert.Equal(90, r.StartIndex);
        Assert.Equal(99, r.EndIndex);
        Assert.Equal(10, r.Period);   // the width up to the last bar
    }

    [Fact]
    public void EarlierHandleRelease_SlidesTheWindow_AndKeepsThePeriod()
    {
        // Anchors at bars 5 (dragged to bar 20) and 34.
        RangeSnapResult r = Snap(Bars(100), T(20), T(34), 30, RangeSnapKind.HandleRelease, dragged: 0);

        Assert.Equal(20, r.StartIndex);
        Assert.Equal(49, r.EndIndex);
        Assert.Equal(30, r.Period);
    }

    [Fact]
    public void ACrossingHandle_IsDecidedByTheTimeOrderAtRelease()
    {
        // Point 1 (formerly the later one) is dragged left of point 0: it is now the earlier handle, the window slides, the period stays.
        RangeSnapResult crossed = Snap(Bars(100), T(20), T(10), 25, RangeSnapKind.HandleRelease, dragged: 1);
        Assert.Equal(10, crossed.StartIndex);
        Assert.Equal(34, crossed.EndIndex);
        Assert.Equal(25, crossed.Period);

        // Point 0 (formerly the earlier one) is dragged right of point 1: it is now the later handle and sets the width.
        RangeSnapResult crossedOther = Snap(Bars(100), T(30), T(10), 25, RangeSnapKind.HandleRelease, dragged: 0);
        Assert.Equal(10, crossedOther.StartIndex);
        Assert.Equal(30, crossedOther.EndIndex);
        Assert.Equal(21, crossedOther.Period);
    }

    [Fact]
    public void EqualTimes_CountAsTheLaterHandle()
    {
        RangeSnapResult r = Snap(Bars(100), T(10), T(10), 30, RangeSnapKind.HandleRelease, dragged: 1);
        Assert.Equal(Min, r.Period);
    }

    [Fact]
    public void BodyMove_KeepsThePeriod_AndReSnapsTheStart()
    {
        RangeSnapResult r = Snap(Bars(100), T(12).AddHours(5), T(41).AddHours(5), 30, RangeSnapKind.BodyMove);

        Assert.Equal(13, r.StartIndex);
        Assert.Equal(42, r.EndIndex);
        Assert.Equal(30, r.Period);
    }

    [Fact]
    public void BodyMove_OverAGapInTheBars_StillSpansExactlyPeriodBars()
    {
        // Weekends: only Monday to Friday bars exist, so 30 bars are not 30 calendar days.
        var candles = new List<CoreCandleData>();
        for (DateTime d = Start; candles.Count < 80; d = d.AddDays(1))
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
            {
                candles.Add(new CoreCandleData(d, 100m, 101m, 99m, 100m, 1000L));
            }
        }

        RangeSnapResult r = Snap(candles, candles[3].Timestamp, candles[10].Timestamp, 30, RangeSnapKind.BodyMove);

        Assert.Equal(3, r.StartIndex);
        Assert.Equal(32, r.EndIndex);
        Assert.Equal(30, r.BarCount);
        Assert.True((r.EndTime - r.StartTime).TotalDays > 30);
    }

    [Fact]
    public void PeriodChange_KeepsTheStart_AndMovesTheEnd()
    {
        List<CoreCandleData> candles = Bars(100);

        RangeSnapResult shorter = Snap(candles, T(5), T(34), 10, RangeSnapKind.PeriodChange);
        Assert.Equal(5, shorter.StartIndex);
        Assert.Equal(14, shorter.EndIndex);
        Assert.Equal(10, shorter.Period);

        RangeSnapResult longer = Snap(candles, T(5), T(14), 30, RangeSnapKind.PeriodChange);
        Assert.Equal(5, longer.StartIndex);
        Assert.Equal(34, longer.EndIndex);
    }

    [Theory]
    [InlineData(-3, Min)]
    [InlineData(0, Min)]
    [InlineData(1, Min)]
    [InlineData(99, 99)]
    [InlineData(Max, Max)]
    [InlineData(Max + 1, Max)]
    [InlineData(99999, Max)]
    public void ThePeriodIsClampedToTheLimits(int period, int expected)
    {
        RangeSnapResult r = Snap(Bars(Max + 200), T(5), T(10), period, RangeSnapKind.Placement);
        Assert.Equal(expected, r.Period);
        Assert.Equal(5 + expected - 1, r.EndIndex);
    }

    [Fact]
    public void TheSnapIsIdempotent()
    {
        List<CoreCandleData> candles = Bars(100);
        RangeSnapResult first = Snap(candles, T(7).AddHours(2), T(31), 22, RangeSnapKind.BodyMove);
        RangeSnapResult second = Snap(candles, first.StartTime, first.EndTime, first.Period, RangeSnapKind.BodyMove);
        Assert.Equal(first, second);
    }
}
