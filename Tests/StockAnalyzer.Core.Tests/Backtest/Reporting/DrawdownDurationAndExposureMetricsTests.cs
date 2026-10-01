using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>MaxDepthDrawdownDuration / LongestDrawdownDuration / TimeInMarket / Exposure (Y:\Temp\sa_implementation_plan_BacktestReportExtendedMetrics.md, Tasks 4-5).</summary>
public class DrawdownDurationAndExposureMetricsTests
{
    private static (MetricValue Depth, MetricValue Max) Durations(params decimal[] equityIncludingInitial)
    {
        ImmutableArray<decimal> equity = equityIncludingInitial.ToImmutableArray();
        ImmutableArray<double> ratio = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(equity);
        return (
            DrawdownSeriesCalculator.ComputeMaxDepthDrawdownDuration(equity, ratio).Value,
            DrawdownSeriesCalculator.ComputeLongestDrawdownDuration(equity).Value);
    }

    // ---- Task 4 ---------------------------------------------------------------------------------

    [Fact]
    public void Duration_TwoEpisodes_DeepestIsShorterThanLongest()
    {
        // idx:      0    1    2    3    4    5    6    7    8    9
        // equity: 100  110  105  100  110  120  118  119  117  121
        // Episode A: peak idx1, below at 2..3, recovers idx4 (E>=peak) -> 3 bars, depth 10/110 (deepest).
        // Episode B: peak idx5, below at 6..8, recovers idx9 -> 4 bars, depth 3/120.
        var (depth, max) = Durations(100m, 110m, 105m, 100m, 110m, 120m, 118m, 119m, 117m, 121m);

        Assert.Equal(3m, depth.Value);
        Assert.Equal(4m, max.Value);
        Assert.Equal(MetricUnit.Bars, depth.Unit);
        Assert.Equal(MetricUnit.Bars, max.Unit);
    }

    [Fact]
    public void Duration_NoDrawdown_ValidZero_IncludingFlatEquity()
    {
        var (depth, max) = Durations(100m, 101m, 101m, 102m);

        Assert.Equal(MetricStatus.Valid, depth.Status);
        Assert.Equal(0m, depth.Value);
        Assert.Equal(0m, max.Value);
    }

    [Fact]
    public void Duration_Unrecovered_CountedToLastSampleBar()
    {
        // peak idx1, never regains 110; last index is 3 -> 3 - 1 = 2 bars (right-censored).
        var (depth, max) = Durations(100m, 110m, 105m, 104m);

        Assert.Equal(2m, depth.Value);
        Assert.Equal(2m, max.Value);
    }

    [Fact]
    public void Duration_LatestEqualPeakIsUsed()
    {
        // equity 100, 100, 90, 100: the peak is the LATEST equal high (idx1), so 3 - 1 = 2, not 3 - 0.
        var (depth, max) = Durations(100m, 100m, 90m, 100m);

        Assert.Equal(2m, depth.Value);
        Assert.Equal(2m, max.Value);
    }

    [Fact]
    public void Duration_EqualDepthTie_UsesFirstTrough()
    {
        // Two identical 10/110 drawdowns. First trough idx2 -> peak idx1, recovery idx3 (E>=peak) -> 2 bars. The second episode has the
        // same length but must not matter for MaxDepthDrawdownDuration; make it longer to prove the first trough is the one used.
        var (depth, max) = Durations(100m, 110m, 100m, 110m, 105m, 100m, 100m);
        // second episode: peak idx3, unrecovered at idx6 -> 3 bars but its depth 10/110 ties the first -> first trough wins (2 bars).

        Assert.Equal(2m, depth.Value);
        Assert.Equal(3m, max.Value);
    }

    [Fact]
    public void Duration_MaxIsNeverBelowDeepestEpisodeDuration()
    {
        foreach (decimal[] path in new[]
        {
            new[] { 100m, 90m, 95m, 100m, 80m },
            new[] { 100m, 120m, 60m, 130m, 129m, 128m, 127m },
            new[] { 100m, 99m, 98m, 97m },
        })
        {
            var (depth, max) = Durations(path);
            Assert.True(max.Value >= depth.Value, string.Join(",", path));
        }
    }

    [Fact]
    public void Duration_NoObservedBars_InsufficientData()
    {
        var (depth, max) = Durations(100m);

        Assert.Equal(MetricStatus.InsufficientData, depth.Status);
        Assert.Equal(MetricReason.EmptyInput, depth.Reason);
        Assert.Equal(MetricStatus.InsufficientData, max.Status);
        Assert.Equal(MetricReason.EmptyInput, max.Reason);
    }

    [Fact]
    public void Generator_PopulatesDrawdownDurations_MatchingTheCalculators()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 110m, 105m, 100m, 110m, 120m, 118m, 119m, 117m, 121m }),
            ReportTestHelpers.Options());

        Assert.Equal(3m, report.MaxDepthDrawdownDuration!.Value.Value);
        Assert.Equal(4m, report.LongestDrawdownDuration!.Value.Value);
    }

    // ---- Task 5 ---------------------------------------------------------------------------------

    private static readonly ImmutableArray<BacktestTrade> NoTrades = ImmutableArray<BacktestTrade>.Empty;

    private static EquityPoint Point(int index, bool inMarket, bool viaMargin = false) => new(
        index, ReportTestHelpers.BaseUtc.AddDays(index), 100m, 100m,
        MarketValue: inMarket && !viaMargin ? 50m : 0m,
        HeldMargin: inMarket && viaMargin ? 50m : 0m);

    [Fact]
    public void Exposure_CountsOnlyPostWarmupBars_AndBothAccountStyles()
    {
        // flags per bar: 1 1 | 0 1(margin) 1(cash) 0   -> warmup (first 2 bars) excluded; 2 of the 4 sample bars are in the market.
        var points = ImmutableArray.Create(
            Point(0, true), Point(1, true), Point(2, false), Point(3, true, viaMargin: true), Point(4, true), Point(5, false));

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 2, tradingStartIndex: 2);

        Assert.Equal(2m, timeInMarket.Value);
        Assert.Equal(MetricUnit.Bars, timeInMarket.Unit);
        Assert.Equal(0.5m, exposure.Value);
        Assert.Equal(MetricUnit.ExposureRatio, exposure.Unit);
    }

    [Fact]
    public void Exposure_PositionStillOpenAtTheEnd_IsCounted()
    {
        var points = ImmutableArray.Create(Point(0, false), Point(1, true), Point(2, true), Point(3, true));

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(3m, timeInMarket.Value);
        Assert.Equal(0.75m, exposure.Value);
    }

    [Fact]
    public void Exposure_NeverInMarket_ValidZero()
    {
        var points = ImmutableArray.Create(Point(0, false), Point(1, false));

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(0m, timeInMarket.Value);
        Assert.Equal(0m, exposure.Value);
        Assert.Equal(MetricStatus.Valid, exposure.Status);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(2, 5)]
    public void Exposure_EmptySample_InsufficientData(int pointCount, int historyStartIndex)
    {
        var points = Enumerable.Range(0, pointCount).Select(i => Point(i, true)).ToImmutableArray();

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex, historyStartIndex);

        Assert.Equal(MetricStatus.InsufficientData, timeInMarket.Status);
        Assert.Equal(MetricReason.EmptyInput, timeInMarket.Reason);
        Assert.Equal(MetricStatus.InsufficientData, exposure.Status);
        Assert.Equal(MetricUnit.ExposureRatio, exposure.Unit);
    }

    [Fact]
    public void Generator_PopulatesExposure_AndReportValidates()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 101m, 102m, 103m }),
            ReportTestHelpers.Options());

        // ReportTestHelpers builds flat equity points (MarketValue = HeldMargin = 0): never in the market.
        Assert.Equal(0m, report.TimeInMarket!.Value.Value);
        Assert.Equal(0m, report.Exposure!.Value.Value);
        // Exposure == 0 makes CAGR / Exposure a zero division: Undefined(ZeroDivisor), never a fabricated number.
        Assert.Equal(MetricStatus.Undefined, report.ExposureAdjustedCAGR!.Value.Status);
        Assert.Equal(MetricReason.ZeroDivisor, report.ExposureAdjustedCAGR.Value.Reason);
        BacktestReportValidator.Validate(report);
    }

    // ---- Task 6 ---------------------------------------------------------------------------------

    private static MetricValue Ratio(decimal value, MetricUnit unit) => MetricValue.Valid(value, unit);

    [Theory]
    [InlineData(0.12, 0.5, 0.24)]
    [InlineData(-0.10, 0.5, -0.20)]
    [InlineData(0.10, 1, 0.10)]
    [InlineData(0, 0.3, 0)]
    public void ExposureAdjustedCagr_IsCagrDividedByExposure_InReturnRatio(double cagr, double exposure, double expected)
    {
        MetricValue result = ExposureMetricsCalculator.ComputeExposureAdjustedCagr(
            Ratio((decimal)cagr, MetricUnit.ReturnRatio), Ratio((decimal)exposure, MetricUnit.ExposureRatio));

        Assert.Equal(MetricStatus.Valid, result.Status);
        Assert.Equal(MetricUnit.ReturnRatio, result.Unit);
        Assert.Equal((decimal)expected, result.Value);
    }

    [Fact]
    public void ExposureAdjustedCagr_ZeroExposure_UndefinedZeroDivisor()
    {
        MetricValue result = ExposureMetricsCalculator.ComputeExposureAdjustedCagr(
            Ratio(0.1m, MetricUnit.ReturnRatio), Ratio(0m, MetricUnit.ExposureRatio));

        Assert.Equal(MetricStatus.Undefined, result.Status);
        Assert.Equal(MetricReason.ZeroDivisor, result.Reason);
        Assert.Equal(MetricUnit.ReturnRatio, result.Unit);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ExposureAdjustedCagr_NonValidCagr_PropagatesBeforeExposureIsLookedAt()
    {
        MetricValue coverageGated = MetricValue.NonValid(
            MetricStatus.NotApplicable, MetricUnit.ReturnRatio, MetricReason.EvaluationCoverageUnverified);

        MetricValue result = ExposureMetricsCalculator.ComputeExposureAdjustedCagr(
            coverageGated, Ratio(0m, MetricUnit.ExposureRatio));

        Assert.Equal(MetricStatus.NotApplicable, result.Status);
        Assert.Equal(MetricReason.EvaluationCoverageUnverified, result.Reason);
        Assert.Equal(MetricUnit.ReturnRatio, result.Unit);
    }

    [Fact]
    public void ExposureAdjustedCagr_NonValidExposure_Propagates()
    {
        MetricValue noSample = MetricValue.NonValid(
            MetricStatus.InsufficientData, MetricUnit.ExposureRatio, MetricReason.EmptyInput);

        MetricValue result = ExposureMetricsCalculator.ComputeExposureAdjustedCagr(
            Ratio(0.1m, MetricUnit.ReturnRatio), noSample);

        Assert.Equal(MetricStatus.InsufficientData, result.Status);
        Assert.Equal(MetricReason.EmptyInput, result.Reason);
    }
}
