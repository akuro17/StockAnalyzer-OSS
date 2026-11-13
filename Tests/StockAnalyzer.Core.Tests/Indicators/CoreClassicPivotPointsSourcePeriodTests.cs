using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T2: ClassicPivotPoints SourcePeriod (PreviousBar / Week / Month).</summary>
public class CoreClassicPivotPointsSourcePeriodTests
{
    private static CoreCandleData Bar(DateTime day, decimal o, decimal h, decimal l, decimal c) => new(day, o, h, l, c, 1000);

    private static readonly string[] LevelNames = { "Main", "R1", "R2", "R3", "S1", "S2", "S3" };

    // Independent expectation of the seven levels for one H/L/C.
    private static decimal[] Levels(decimal h, decimal l, decimal c)
    {
        decimal p = (h + l + c) / 3m;
        decimal diff = h - l;
        decimal r1 = 2m * p - l;
        decimal s1 = 2m * p - h;
        return new[] { p, r1, p + diff, r1 + diff, s1, p - diff, s1 - diff };
    }

    private static void AssertBar(IIndicatorResult result, int index, decimal[]? expected)
    {
        for (int k = 0; k < LevelNames.Length; k++)
        {
            decimal? actual = result.GetSeries(LevelNames[k])[index];
            if (expected is null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.Equal(expected[k], actual);
            }
        }
    }

    private static IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles, PivotSourcePeriod period)
    {
        var indicator = new CoreClassicPivotPointsIndicator();
        indicator.Configure(new CoreClassicPivotPointsParameter { SourcePeriod = period });
        return indicator.Calculate(candles);
    }

    // Week 1: Mon 2026-09-14 .. Fri 09-18  H=120 L=96 C=108.   Week 2: 09-21 .. 09-25  H=130 L=100 C=115.   Week 3: 09-28 .. 10-02.
    private static List<CoreCandleData> ThreeWeeks(bool week2StartsOnTuesday = false)
    {
        var list = new List<CoreCandleData>
        {
            Bar(new DateTime(2026, 9, 14), 100m, 110m, 98m, 105m),
            Bar(new DateTime(2026, 9, 15), 105m, 120m, 100m, 115m),
            Bar(new DateTime(2026, 9, 16), 115m, 118m, 96m, 110m),
            Bar(new DateTime(2026, 9, 17), 110m, 114m, 99m, 101m),
            Bar(new DateTime(2026, 9, 18), 101m, 112m, 97m, 108m),
        };
        if (!week2StartsOnTuesday)
        {
            list.Add(Bar(new DateTime(2026, 9, 21), 109m, 125m, 104m, 120m));
        }

        list.AddRange(new[]
        {
            Bar(new DateTime(2026, 9, 22), 120m, 130m, 110m, 125m),
            Bar(new DateTime(2026, 9, 23), 125m, 128m, 100m, 105m),
            Bar(new DateTime(2026, 9, 24), 105m, 119m, 102m, 117m),
            Bar(new DateTime(2026, 9, 25), 117m, 121m, 108m, 115m),
            Bar(new DateTime(2026, 9, 28), 116m, 118m, 112m, 114m),
            Bar(new DateTime(2026, 9, 29), 114m, 117m, 111m, 113m),
            Bar(new DateTime(2026, 10, 2), 113m, 116m, 109m, 112m),
        });
        return list;
    }

    [Fact]
    public void PreviousBar_IsTheDefault_AndMatchesAnIndependentPerBarRecalculation()
    {
        var candles = ThreeWeeks();
        var plain = new CoreClassicPivotPointsIndicator();
        Assert.Equal(PivotSourcePeriod.PreviousBar, plain.SourcePeriod);
        IIndicatorResult defaultResult = plain.Calculate(candles);
        IIndicatorResult explicitResult = Calculate(candles, PivotSourcePeriod.PreviousBar);

        AssertBar(defaultResult, 0, null);
        AssertBar(explicitResult, 0, null);
        for (int i = 1; i < candles.Count; i++)
        {
            decimal[] expected = Levels(candles[i - 1].High, candles[i - 1].Low, candles[i - 1].Close);
            AssertBar(defaultResult, i, expected);
            AssertBar(explicitResult, i, expected);
        }
    }

    [Fact]
    public void Week_FirstWeekIsNull_AndEveryBarOfALaterWeekShowsThePreviousWeeksLevels()
    {
        var candles = ThreeWeeks();
        IIndicatorResult result = Calculate(candles, PivotSourcePeriod.Week);
        Assert.Equal(candles.Count, result.GetSeries("Main").Count);

        decimal[] fromWeek1 = Levels(120m, 96m, 108m);
        decimal[] fromWeek2 = Levels(130m, 100m, 115m);
        Assert.Equal(108m, fromWeek1[0]);

        for (int i = 0; i < 5; i++) AssertBar(result, i, null);
        for (int i = 5; i < 10; i++) AssertBar(result, i, fromWeek1);
        for (int i = 10; i < candles.Count; i++) AssertBar(result, i, fromWeek2);
    }

    [Fact]
    public void Week_AHolidayStartingTheWeekOnTuesdayStillBelongsToThatWeek()
    {
        var candles = ThreeWeeks(week2StartsOnTuesday: true); // no Monday 09-21
        IIndicatorResult result = Calculate(candles, PivotSourcePeriod.Week);

        decimal[] fromWeek1 = Levels(120m, 96m, 108m);
        decimal[] fromWeek2 = Levels(130m, 100m, 115m); // Tue..Fri of week 2: H=130 L=100 C=115
        for (int i = 0; i < 5; i++) AssertBar(result, i, null);
        for (int i = 5; i < 9; i++) AssertBar(result, i, fromWeek1);
        for (int i = 9; i < candles.Count; i++) AssertBar(result, i, fromWeek2);
    }

    [Fact]
    public void Month_UsesThePreviousCalendarMonth_AndALaterBarNeverSeesItsOwnMonth()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(new DateTime(2026, 8, 3), 100m, 110m, 90m, 105m),
            Bar(new DateTime(2026, 8, 28), 105m, 130m, 100m, 125m),
            Bar(new DateTime(2026, 9, 15), 125m, 128m, 120m, 122m),
            Bar(new DateTime(2026, 9, 30), 122m, 135m, 118m, 123m),
            Bar(new DateTime(2026, 10, 1), 123m, 124m, 119m, 121m),
        };
        IIndicatorResult result = Calculate(candles, PivotSourcePeriod.Month);

        decimal[] fromAugust = Levels(130m, 90m, 125m);
        decimal[] fromSeptember = Levels(135m, 118m, 123m);
        AssertBar(result, 0, null);
        AssertBar(result, 1, null);
        AssertBar(result, 2, fromAugust);
        AssertBar(result, 3, fromAugust);
        AssertBar(result, 4, fromSeptember);
        Assert.Equal(115m, fromAugust[0]);
    }

    [Fact]
    public void Week_SingleBarAndEmptyInput()
    {
        IIndicatorResult single = Calculate(new List<CoreCandleData> { Bar(new DateTime(2026, 9, 14), 1m, 2m, 1m, 2m) }, PivotSourcePeriod.Week);
        AssertBar(single, 0, null);
        IIndicatorResult empty = Calculate(new List<CoreCandleData>(), PivotSourcePeriod.Week);
        Assert.True(empty.IsSuccessful);
    }

    [Fact]
    public void Parameter_DisplayNameValidationAndDefaultEntry()
    {
        Assert.Equal("ClassicPivotPoints", new CoreClassicPivotPointsParameter().GetDisplayName("ClassicPivotPoints"));
        Assert.Equal("ClassicPivotPoints (Week)", new CoreClassicPivotPointsParameter { SourcePeriod = PivotSourcePeriod.Week }.GetDisplayName("ClassicPivotPoints"));
        Assert.Equal("ClassicPivotPoints (Month)", new CoreClassicPivotPointsParameter { SourcePeriod = PivotSourcePeriod.Month }.GetDisplayName("ClassicPivotPoints"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreClassicPivotPointsParameter { SourcePeriod = (PivotSourcePeriod)99 }.Validate());

        CoreIndicatorSettings entry = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.ClassicPivotPoints);
        var parameter = Assert.IsType<CoreClassicPivotPointsParameter>(entry.ParameterObject);
        Assert.Equal(PivotSourcePeriod.PreviousBar, parameter.SourcePeriod);
    }

    [Fact]
    public void ScanRejectsPreviousBar()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PivotSourcePeriodScan.ScanPreviousPeriods(ThreeWeeks(), PivotSourcePeriod.PreviousBar));
    }
}
