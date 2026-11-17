using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Indicator Timeframe, Sprint 3 / T4: the warm-up overload of <see cref="ExtendedLookbackHelper"/> that scales the need of indicators
/// calculated on a higher frame than the chart. The one-argument overload and <c>IsEligibleChartType</c> stay as they were.
/// </summary>
public sealed class ExtendedLookbackTimeframeTests
{
    private static CoreIndicatorSettings Sma(int period, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart, bool enabled = true)
    {
        var setting = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = period }, IsEnabled = enabled };
        setting.Timeframe = timeframe;
        return setting;
    }

    private static CoreIndicatorSettings Obv(IndicatorTimeframe timeframe)
    {
        var setting = new CoreIndicatorSettings { TypeEnum = IndicatorType.OBV, IsEnabled = true };
        setting.Timeframe = timeframe;
        return setting;
    }

    [Fact]
    public void AllSameAsChart_EqualsTheOneArgumentOverload_ForEveryChartFrame()
    {
        var settings = new List<CoreIndicatorSettings> { Sma(20), Sma(50), Sma(200, enabled: false) };
        int expected = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(50, expected);
        foreach (var frame in new[] { TimeFrame.H1, TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 })
        {
            Assert.Equal(expected, ExtendedLookbackHelper.CalculateRequiredLookback(settings, frame));
        }
    }

    [Fact]
    public void WeeklySma52_OnDailyChart_NeedsFiftyFourWeeksOfDailyBars()
    {
        var settings = new[] { Sma(52, IndicatorTimeframe.Weekly) };

        Assert.Equal((52 + ExtendedLookbackHelper.AlignmentSafetyPeriods) * 5, ExtendedLookbackHelper.CalculateRequiredLookback(settings, TimeFrame.D1));
        Assert.Equal(270, ExtendedLookbackHelper.CalculateRequiredLookback(settings, TimeFrame.D1));
    }

    [Fact]
    public void MonthlySma12_OnDailyChart_Uses21DailyBarsPerMonth()
    {
        Assert.Equal((12 + 2) * 21, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(12, IndicatorTimeframe.Monthly) }, TimeFrame.D1));
    }

    [Fact]
    public void ObvWithoutParameterObject_Weekly_ContributesTheSafetyPeriodsOnly()
    {
        Assert.Equal(2 * 5, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Obv(IndicatorTimeframe.Weekly) }, TimeFrame.D1));
        Assert.Equal(0, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Obv(IndicatorTimeframe.SameAsChart) }, TimeFrame.D1));
    }

    [Fact]
    public void MonthlyOnWeeklyChart_IsNotScaled_BecauseThePipelineRefusesIt_AndWeeklyOnWeeklyChartIsUnchanged()
    {
        // A Monthly indicator on a Weekly chart is refused (U-1): there is no W1 to MN1 step, so the need stays the unscaled warm-up.
        Assert.Equal(10, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(10, IndicatorTimeframe.Monthly) }, TimeFrame.W1));
        Assert.Equal(10, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(10, IndicatorTimeframe.Weekly) }, TimeFrame.W1));
    }

    [Fact]
    public void AnIndicatorThatReadsOrIsReadByAnotherIndicator_IsNotScaled_AsThePipelineCalculatesItOnTheChartFrame()
    {
        var upstream = Sma(10, IndicatorTimeframe.Weekly);
        upstream.Id = "up";
        var downstream = Sma(20, IndicatorTimeframe.Weekly);
        downstream.Id = "down";
        downstream.SourceIndicatorId = "up";
        var free = Sma(30, IndicatorTimeframe.Weekly);
        free.Id = "free";

        Assert.Equal(20, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { upstream, downstream }, TimeFrame.D1)); // the unscaled maximum, not (20 + 2) x 5
        Assert.Equal((30 + 2) * 5, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { upstream, downstream, free }, TimeFrame.D1)); // a free one is still scaled
    }

    [Fact]
    public void FinerThanChartOrUndefinedStep_IsNotScaled()
    {
        Assert.Equal(10, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(10, IndicatorTimeframe.Weekly) }, TimeFrame.MN1));
        Assert.Equal(10, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(10, IndicatorTimeframe.Weekly) }, TimeFrame.H1)); // no defined step for an intraday chart
    }

    [Fact]
    public void ScalingSwitchedOff_ReturnsTheUnscaledNeed_AsOnAThreeLineBreakChart()
    {
        var settings = new[] { Sma(52, IndicatorTimeframe.Weekly) };

        Assert.Equal(52, ExtendedLookbackHelper.CalculateRequiredLookback(settings, TimeFrame.D1, applyTimeframeScaling: false));
        // The chart type stays eligible for warm-up (pinned) although it draws no time-based indicator bars.
        Assert.True(ExtendedLookbackHelper.IsEligibleChartType(ChartType.ThreeLineBreak));
        Assert.False(IndicatorTimeframePolicy.IsApplicable(ChartType.ThreeLineBreak));
    }

    [Fact]
    public void UnsupportedIndicatorWithATimeframe_IsNotScaled_AndDisabledOnesAreIgnored()
    {
        var pivot = new CoreIndicatorSettings { TypeEnum = IndicatorType.ClassicPivotPoints, ParameterObject = new CoreClassicPivotPointsParameter(), IsEnabled = true };
        pivot.Timeframe = IndicatorTimeframe.Monthly;

        Assert.Equal(0, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { pivot }, TimeFrame.D1));
        Assert.Equal(0, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(52, IndicatorTimeframe.Weekly, enabled: false) }, TimeFrame.D1));
    }

    [Fact]
    public void Maximum_TakesTheLargestOfScaledAndUnscaledIndicators()
    {
        var settings = new[] { Sma(300), Sma(52, IndicatorTimeframe.Weekly), Sma(5, IndicatorTimeframe.Monthly) };

        Assert.Equal(300, ExtendedLookbackHelper.CalculateRequiredLookback(settings, TimeFrame.D1)); // 270 and 147 are smaller
        Assert.Equal(270, ExtendedLookbackHelper.CalculateRequiredLookback(new[] { Sma(100), Sma(52, IndicatorTimeframe.Weekly) }, TimeFrame.D1));
    }
}
