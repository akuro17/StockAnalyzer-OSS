using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Indicator Timeframe, Sprint 3 / T4: the chart-type rule of <see cref="ChartViewModel.ApplyChartTypeTimeframePolicy"/> (pure function over settings).</summary>
public sealed class ChartViewModelIndicatorTimeframeTests
{
    private static CoreIndicatorSettings Sma(string id, IndicatorTimeframe timeframe, bool enabled = true)
    {
        var setting = new CoreIndicatorSettings { Id = id, TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = 20 }, IsEnabled = enabled };
        setting.Timeframe = timeframe;
        return setting;
    }

    [Theory]
    [InlineData(ChartType.Candlestick)]
    [InlineData(ChartType.HeikinAshi)]
    [InlineData(ChartType.OHLCBar)]
    [InlineData(ChartType.Line)]
    [InlineData(ChartType.Area)]
    [InlineData(ChartType.Dot)]
    [InlineData(ChartType.Invisible)]
    public void ApplicableChartType_ReturnsTheInputUnchanged(ChartType chartType)
    {
        var settings = new List<CoreIndicatorSettings> { Sma("a", IndicatorTimeframe.Weekly) };

        var result = ChartViewModel.ApplyChartTypeTimeframePolicy(settings, chartType, out var ignored);

        Assert.Same(settings, result);
        Assert.Null(ignored);
    }

    [Theory]
    [InlineData(ChartType.ThreeLineBreak)]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.ReverseWatch)]
    [InlineData(ChartType.RelativePerformance)]
    public void NonApplicableChartType_PassesSameAsChartClones_AndFlagsTheOriginals(ChartType chartType)
    {
        var weekly = Sma("w", IndicatorTimeframe.Weekly);
        var monthly = Sma("m", IndicatorTimeframe.Monthly);
        var same = Sma("s", IndicatorTimeframe.SameAsChart);
        var settings = new List<CoreIndicatorSettings> { same, weekly, monthly };

        var result = ChartViewModel.ApplyChartTypeTimeframePolicy(settings, chartType, out var ignored);

        Assert.Equal(3, result.Count);
        Assert.Same(same, result[0]);
        Assert.NotSame(weekly, result[1]);
        Assert.NotSame(monthly, result[2]);
        Assert.Equal(new[] { "s", "w", "m" }, result.Select(r => r.Id).ToArray());
        Assert.All(result, r => Assert.Equal(IndicatorTimeframe.SameAsChart, r.Timeframe));
        Assert.Equal(IndicatorTimeframe.Weekly, weekly.Timeframe); // originals are never changed
        Assert.Equal(IndicatorTimeframe.Monthly, monthly.Timeframe);
        Assert.NotNull(ignored);
        Assert.Equal(new[] { weekly, monthly }, ignored!.ToArray());
    }

    [Fact]
    public void NonApplicableChartType_LeavesDisabledUnsupportedAndSameSettingsAlone()
    {
        var disabled = Sma("d", IndicatorTimeframe.Weekly, enabled: false);
        var pivot = new CoreIndicatorSettings { Id = "p", TypeEnum = IndicatorType.ClassicPivotPoints, ParameterObject = new CoreClassicPivotPointsParameter(), IsEnabled = true };
        pivot.Timeframe = IndicatorTimeframe.Monthly; // an invalid combination the normalisation would reset; the rule must not touch it
        var settings = new List<CoreIndicatorSettings> { disabled, pivot, Sma("s", IndicatorTimeframe.SameAsChart) };

        var result = ChartViewModel.ApplyChartTypeTimeframePolicy(settings, ChartType.Renko, out var ignored);

        Assert.Same(settings, result);
        Assert.Null(ignored);
    }
}
