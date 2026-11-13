using System.Collections.Generic;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// The intra-indicator cross marker (ShowCrossMarkers) must only use series that are charted:
/// a series hidden by <see cref="IndicatorChartSuppression"/> (KST Signal while ShowSignal is off)
/// would put the marker on an invisible line.
/// </summary>
public class ChartRenderPipelineCrossSeriesTests
{
    private static IIndicatorResult KstResult() =>
        IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { "Main", new List<decimal?> { -2m, 0m, 3m } },
            { "Signal", new List<decimal?> { null, 1m, 2m } }
        });

    private static CoreIndicatorSettings KstSetting(bool showSignal) => new()
    {
        Id = "kst_cross",
        TypeEnum = IndicatorType.KST,
        IsOverlay = false,
        ShowCrossMarkers = true,
        ParameterObject = new CoreKstParameter { ShowSignal = showSignal }
    };

    [Fact]
    public void Select_ForKstWithShowSignalOff_ReturnsNoPair()
    {
        var (shortName, longName) = ChartRenderPipeline.SelectIntraIndicatorCrossSeries(KstResult(), KstSetting(false));

        Assert.Null(shortName);
        Assert.Null(longName);
    }

    [Fact]
    public void Select_ForKstWithShowSignalOn_ReturnsMainThenSignal()
    {
        var (shortName, longName) = ChartRenderPipeline.SelectIntraIndicatorCrossSeries(KstResult(), KstSetting(true));

        Assert.Equal("Main", shortName);
        Assert.Equal("Signal", longName);
    }

    [Fact]
    public void Select_ForMacdWithTwoChartedSeries_KeepsTheFirstTwoSeries()
    {
        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { "Main", new List<decimal?> { 1m } },
            { "Signal", new List<decimal?> { 2m } },
            { "Histogram", new List<decimal?> { 3m } }
        });
        var setting = new CoreIndicatorSettings { Id = "macd_cross", TypeEnum = IndicatorType.MACD, IsOverlay = false };

        var (shortName, longName) = ChartRenderPipeline.SelectIntraIndicatorCrossSeries(result, setting);

        Assert.Equal("Main", shortName);
        Assert.Equal("Signal", longName);
    }
}
