using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class DrawdownSeriesCalculatorTests
{
    [Fact]
    public void E_100_120_90_108_MDD025_Ulcer_Correct()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m });
        var sample = EquitySample.Build(result, ReportTestHelpers.Options());

        var ratioSeries = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(sample.Equity);
        var amountSeries = DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(sample.Equity);

        Assert.Equal(new[] { 0d, 0d, 0.25d, 0.10d }, ratioSeries.ToArray());

        var maxDrawdown = DrawdownSeriesCalculator.ComputeMaxDrawdown(ratioSeries);
        var maxDrawdownAmount = DrawdownSeriesCalculator.ComputeMaxDrawdownAmount(amountSeries);
        var ulcer = DrawdownSeriesCalculator.ComputeUlcerIndex(ratioSeries, sample.SampleCount);

        Assert.Equal(0.25m, maxDrawdown.Value);
        Assert.Equal(30m, maxDrawdownAmount.Value);
        Assert.Equal(System.Math.Sqrt((0d * 0d + 25d * 25d + 10d * 10d) / 3d), (double)ulcer.Value!.Value, precision: 10);
    }

    [Fact]
    public void E_100_0_MDD_Equals_1()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 0m });
        var sample = EquitySample.Build(result, ReportTestHelpers.Options());

        var ratioSeries = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(sample.Equity);
        var maxDrawdown = DrawdownSeriesCalculator.ComputeMaxDrawdown(ratioSeries);

        Assert.Equal(1m, maxDrawdown.Value);
    }

    [Fact]
    public void E_100_Negative20_MDD_Exceeds_1_NoClamp()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { -20m });
        var sample = EquitySample.Build(result, ReportTestHelpers.Options());

        var ratioSeries = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(sample.Equity);
        var maxDrawdown = DrawdownSeriesCalculator.ComputeMaxDrawdown(ratioSeries);

        Assert.Equal(1.2m, maxDrawdown.Value);
    }

    [Fact]
    public void UlcerIndex_ZeroTradingBars_InsufficientData()
    {
        var ratioSeries = ImmutableArray.Create(0d);
        var ulcer = DrawdownSeriesCalculator.ComputeUlcerIndex(ratioSeries, m: 0);

        Assert.Equal(MetricStatus.InsufficientData, ulcer.Status);
        Assert.Null(ulcer.Value);
    }
}
