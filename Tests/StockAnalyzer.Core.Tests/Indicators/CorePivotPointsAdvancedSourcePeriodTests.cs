using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T3: PivotPointsAdvanced SourcePeriod (PreviousBar / Week / Month), shared scan with ClassicPivotPoints.</summary>
public class CorePivotPointsAdvancedSourcePeriodTests
{
    private static CoreCandleData Bar(DateTime day, decimal o, decimal h, decimal l, decimal c) => new(day, o, h, l, c, 1000);

    private static IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles, PivotPointType type, PivotSourcePeriod period)
    {
        var indicator = new CorePivotPointsAdvancedIndicator();
        indicator.Configure(new CorePivotPointsAdvancedParameter { PivotType = type, SourcePeriod = period });
        return indicator.Calculate(candles);
    }

    // Week 1 (Mon 2026-09-14 .. Fri 09-18): H=120 L=96 C=108. Week 2 (09-21 .. 09-25): H=130 L=100 C=115. Opens are changed by `openShift`.
    private static List<CoreCandleData> TwoWeeksAndABar(decimal openShift = 0m) => new()
    {
        Bar(new DateTime(2026, 9, 14), 100m, 110m, 98m, 105m),
        Bar(new DateTime(2026, 9, 15), 105m, 120m, 100m, 115m),
        Bar(new DateTime(2026, 9, 16), 115m, 118m, 96m, 110m),
        Bar(new DateTime(2026, 9, 17), 110m, 114m, 99m, 101m),
        Bar(new DateTime(2026, 9, 18), 101m, 112m, 97m, 108m),
        Bar(new DateTime(2026, 9, 21), 109m + openShift, 125m, 104m, 120m),
        Bar(new DateTime(2026, 9, 22), 120m + openShift, 130m, 110m, 125m),
        Bar(new DateTime(2026, 9, 23), 125m + openShift, 128m, 100m, 105m),
        Bar(new DateTime(2026, 9, 24), 105m + openShift, 119m, 102m, 117m),
        Bar(new DateTime(2026, 9, 25), 117m + openShift, 121m, 108m, 115m),
        Bar(new DateTime(2026, 9, 28), 116m + openShift, 118m, 112m, 114m),
    };

    [Theory]
    [InlineData(PivotPointType.Standard)]
    [InlineData(PivotPointType.Fibonacci)]
    [InlineData(PivotPointType.Woodie)]
    [InlineData(PivotPointType.Camarilla)]
    public void PreviousBar_IsTheDefault_AndEqualsTheOldPerBarResult(PivotPointType type)
    {
        var candles = TwoWeeksAndABar();
        var plain = new CorePivotPointsAdvancedIndicator { PivotType = type };
        Assert.Equal(PivotSourcePeriod.PreviousBar, plain.SourcePeriod);
        IIndicatorResult byDefault = plain.Calculate(candles);
        IIndicatorResult explicitPrevious = Calculate(candles, type, PivotSourcePeriod.PreviousBar);

        // Every bar i must equal the levels of a one-bar series made of bar i-1 only evaluated as "week" of one bar:
        // here: the per-bar result of bar i equals the result that a PreviousBar run gives for the pair (i-1, i).
        foreach (string name in new[] { "Main", "R1", "R2", "R3", "R4", "S1", "S2", "S3", "S4" })
        {
            Assert.Equal(byDefault.GetSeries(name), explicitPrevious.GetSeries(name));
            Assert.Null(byDefault.GetSeries(name)[0]);
        }

        for (int i = 1; i < candles.Count; i++)
        {
            var pair = new List<CoreCandleData> { candles[i - 1], candles[i] };
            IIndicatorResult single = Calculate(pair, type, PivotSourcePeriod.PreviousBar);
            foreach (string name in new[] { "Main", "R1", "R2", "R3", "R4", "S1", "S2", "S3", "S4" })
            {
                Assert.Equal(single.GetSeries(name)[1], byDefault.GetSeries(name)[i]);
            }
        }
    }

    [Fact]
    public void Week_StandardLevelsOfWeekTwoComeFromWeekOne_AndWeekOneIsNull()
    {
        IIndicatorResult result = Calculate(TwoWeeksAndABar(), PivotPointType.Standard, PivotSourcePeriod.Week);

        // From H=120 L=96 C=108: P=108, R1=120, S1=96, D=24, R2=132, S2=84, R3=144, S3=72.
        for (int i = 0; i < 5; i++) Assert.Null(result.GetSeries("Main")[i]);
        for (int i = 5; i < 10; i++)
        {
            Assert.Equal(108m, result.GetSeries("Main")[i]);
            Assert.Equal(120m, result.GetSeries("R1")[i]);
            Assert.Equal(132m, result.GetSeries("R2")[i]);
            Assert.Equal(144m, result.GetSeries("R3")[i]);
            Assert.Equal(96m, result.GetSeries("S1")[i]);
            Assert.Equal(84m, result.GetSeries("S2")[i]);
            Assert.Equal(72m, result.GetSeries("S3")[i]);
            Assert.Null(result.GetSeries("R4")[i]);
        }

        // Week 3 (one bar) from week 2: H=130 L=100 C=115 -> P=115.
        Assert.Equal(115m, result.GetSeries("Main")[10]);
    }

    [Fact]
    public void Week_WoodieUsesThePreviousPeriodsClose_NeverTheCurrentOpen()
    {
        IIndicatorResult result = Calculate(TwoWeeksAndABar(), PivotPointType.Woodie, PivotSourcePeriod.Week);
        IIndicatorResult shiftedOpens = Calculate(TwoWeeksAndABar(openShift: 40m), PivotPointType.Woodie, PivotSourcePeriod.Week);

        // P = (H + L + 2 C) / 4 = (120 + 96 + 216) / 4 = 108 for every bar of week 2.
        for (int i = 5; i < 10; i++)
        {
            Assert.Equal(108m, result.GetSeries("Main")[i]);
            Assert.Equal(result.GetSeries("Main")[i], shiftedOpens.GetSeries("Main")[i]);
            Assert.Equal(result.GetSeries("R3")[i], shiftedOpens.GetSeries("R3")[i]);
        }
    }

    [Fact]
    public void Month_CamarillaR4AndS4ComeFromThePreviousMonth()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(new DateTime(2026, 8, 3), 100m, 110m, 90m, 105m),
            Bar(new DateTime(2026, 8, 28), 105m, 130m, 100m, 125m),   // August: H=130 L=90 C=125 -> D=40
            Bar(new DateTime(2026, 9, 15), 125m, 128m, 120m, 122m),
            Bar(new DateTime(2026, 9, 30), 122m, 135m, 118m, 123m),
        };
        IIndicatorResult result = Calculate(candles, PivotPointType.Camarilla, PivotSourcePeriod.Month);

        Assert.Null(result.GetSeries("R4")[0]);
        Assert.Null(result.GetSeries("R4")[1]);
        foreach (int i in new[] { 2, 3 })
        {
            Assert.Equal(125m + 40m * 1.1m / 2m, result.GetSeries("R4")[i]); // 147
            Assert.Equal(125m - 40m * 1.1m / 2m, result.GetSeries("S4")[i]); // 103
            Assert.Equal(125m + 40m * 1.1m / 4m, result.GetSeries("R3")[i]);
        }
    }

    [Fact]
    public void Parameter_DisplayNameAndValidation()
    {
        Assert.Equal("PivotPointsAdvanced (Standard)", new CorePivotPointsAdvancedParameter().GetDisplayName("PivotPointsAdvanced"));
        Assert.Equal("PivotPointsAdvanced (Camarilla, Month)",
            new CorePivotPointsAdvancedParameter { PivotType = PivotPointType.Camarilla, SourcePeriod = PivotSourcePeriod.Month }.GetDisplayName("PivotPointsAdvanced"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePivotPointsAdvancedParameter { SourcePeriod = (PivotSourcePeriod)99 }.Validate());
    }
}
