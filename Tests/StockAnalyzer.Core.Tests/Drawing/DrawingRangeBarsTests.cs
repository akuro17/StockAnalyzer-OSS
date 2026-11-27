using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class DrawingRangeBarsTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Daily(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + i;
            list.Add(new CoreCandleData(Start.AddDays(i), close, close, close, close, 1000L));
        }

        return list;
    }

    [Fact]
    public void TryFind_BothEndsAreIncluded()
    {
        var candles = Daily(10);

        bool found = DrawingRangeBars.TryFind(candles, Start.AddDays(2), Start.AddDays(5), out int first, out int last);

        Assert.True(found);
        Assert.Equal(2, first);
        Assert.Equal(5, last);
    }

    [Fact]
    public void TryFind_TheOrderOfTheTwoTimesDoesNotMatter()
    {
        var candles = Daily(10);

        DrawingRangeBars.TryFind(candles, Start.AddDays(2), Start.AddDays(5), out int firstA, out int lastA);
        DrawingRangeBars.TryFind(candles, Start.AddDays(5), Start.AddDays(2), out int firstB, out int lastB);

        Assert.Equal((firstA, lastA), (firstB, lastB));
    }

    [Fact]
    public void TryFind_TimesBetweenBars_TakeTheBarsInsideOnly()
    {
        var candles = Daily(10);

        bool found = DrawingRangeBars.TryFind(candles, Start.AddDays(2).AddHours(1), Start.AddDays(5).AddHours(23), out int first, out int last);

        Assert.True(found);
        Assert.Equal(3, first);
        Assert.Equal(5, last);
    }

    [Fact]
    public void TryFind_RangeWithoutAnyBar_IsNotFound()
    {
        var candles = Daily(10);

        Assert.False(DrawingRangeBars.TryFind(candles, Start.AddDays(2).AddHours(1), Start.AddDays(2).AddHours(20), out int first, out int last));
        Assert.Equal(-1, first);
        Assert.Equal(-1, last);
        Assert.False(DrawingRangeBars.TryFind(candles, Start.AddDays(-20), Start.AddDays(-10), out _, out _));
        Assert.False(DrawingRangeBars.TryFind(candles, Start.AddDays(40), Start.AddDays(50), out _, out _));
    }

    [Fact]
    public void TryFind_EmptyAndNullLists_AreNotFound()
    {
        Assert.False(DrawingRangeBars.TryFind(new List<CoreCandleData>(), Start, Start.AddDays(1), out _, out _));
        Assert.False(DrawingRangeBars.TryFind(null, Start, Start.AddDays(1), out _, out _));
    }

    [Fact]
    public void TryFind_SingleBarRange_HasEqualIndexes()
    {
        var candles = Daily(10);

        Assert.True(DrawingRangeBars.TryFind(candles, Start.AddDays(4), Start.AddDays(4), out int first, out int last));
        Assert.Equal(4, first);
        Assert.Equal(4, last);
    }

    [Fact]
    public void TryFind_AgreesWithTheLinearScanOfRegressionTrend()
    {
        // The rule is the one of RegressionTrendValues.Fit (a linear scan over time >= start && time <= end); compare on a grid of times, bars with gaps included.
        var candles = new List<CoreCandleData>();
        DateTime time = Start;
        for (int i = 0; i < 60; i++)
        {
            time = time.AddDays(i % 7 == 5 ? 3 : 1);
            candles.Add(new CoreCandleData(time, 1m, 1m, 1m, 1m, 1L));
        }

        for (int a = -2; a < 90; a += 7)
        {
            for (int b = a; b < 100; b += 9)
            {
                DateTime t1 = Start.AddDays(a).AddHours(5);
                DateTime t2 = Start.AddDays(b).AddHours(11);
                int expectedFirst = -1;
                int expectedLast = -1;
                for (int i = 0; i < candles.Count; i++)
                {
                    if (candles[i].Timestamp >= t1 && candles[i].Timestamp <= t2)
                    {
                        if (expectedFirst < 0)
                        {
                            expectedFirst = i;
                        }

                        expectedLast = i;
                    }
                }

                bool found = DrawingRangeBars.TryFind(candles, t1, t2, out int first, out int last);
                Assert.Equal(expectedFirst >= 0, found);
                Assert.Equal(expectedFirst, first);
                Assert.Equal(expectedLast, last);
            }
        }
    }

    [Fact]
    public void FutureBarTime_UsesTheExistingBarThenTheSpan()
    {
        var candles = Daily(10);
        TimeSpan span = TimeSpan.FromDays(1.5);

        Assert.Equal(candles[4].Timestamp, DrawingRangeBars.FutureBarTime(candles, 4, 0, span));
        Assert.Equal(candles[7].Timestamp, DrawingRangeBars.FutureBarTime(candles, 4, 3, span));
        Assert.Equal(candles[9].Timestamp, DrawingRangeBars.FutureBarTime(candles, 4, 5, span));

        // Beyond the data: the newest bar time plus the span for every bar after it.
        Assert.Equal(candles[9].Timestamp + span, DrawingRangeBars.FutureBarTime(candles, 4, 6, span));
        Assert.Equal(candles[9].Timestamp + span + span, DrawingRangeBars.FutureBarTime(candles, 4, 7, span));
        Assert.Equal(candles[9].Timestamp + TimeSpan.FromTicks(span.Ticks * 20), DrawingRangeBars.FutureBarTime(candles, 4, 25, span));
    }

    [Fact]
    public void MeanBarSpan_IsTheMeanDistance_AndFallsBackToOneDay()
    {
        Assert.Equal(TimeSpan.FromDays(1), DrawingRangeBars.MeanBarSpan(Daily(10)));
        Assert.Equal(DrawingRangeBars.FallbackBarSpan, DrawingRangeBars.MeanBarSpan(Daily(1)));
        Assert.Equal(DrawingRangeBars.FallbackBarSpan, DrawingRangeBars.MeanBarSpan(new List<CoreCandleData>()));

        // Not increasing times: the mean is not positive.
        var same = new List<CoreCandleData>
        {
            new(Start, 1m, 1m, 1m, 1m, 1L),
            new(Start, 1m, 1m, 1m, 1m, 1L),
        };
        Assert.Equal(DrawingRangeBars.FallbackBarSpan, DrawingRangeBars.MeanBarSpan(same));
    }
}
