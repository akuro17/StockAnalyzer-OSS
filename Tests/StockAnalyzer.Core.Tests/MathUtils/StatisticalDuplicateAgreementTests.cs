using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Two statistics exist twice in the code base because their numeric types and contracts differ (decimal kernel against double engine); this class keeps the pairs from drifting apart
/// without merging them (constraint audit L-4 of the statistical functions plan, 2026-11-18).
/// <list type="bullet">
/// <item>Downside variance: <see cref="AsymmetricRiskMath"/> (decimal returns, target 0) against <see cref="RiskAdjustedMetricsCalculator"/>.MeanAndDownsideVariance (double, the Sortino input). Both are mean(min(r, 0)^2) over the window length n.</item>
/// <item>Theil-Sen slope: <see cref="RobustRegressionMath.TryTheilSenSlope"/> (decimal, type 7 median) against <see cref="GeometricPatternDetector"/>.CalculateTheilSen (double, the element at index count / 2 of the sorted pair slopes).
/// The two medians are the same number for an odd count of pair slopes and differ for an even count: the pattern detector takes the upper middle slope, the kernel the mean of both middle slopes. That difference is pinned below, not hidden.</item>
/// </list>
/// Specification: docs/indicators_formulas.md, records indicators_formulas_0600 and indicators_formulas_0604.
/// </summary>
public class StatisticalDuplicateAgreementTests
{
    private const double RelativeTolerance = 1e-12;

    private static decimal[] Returns(int seed, int length)
    {
        var random = new Random(seed);
        var values = new decimal[length];
        for (int i = 0; i < length; i++)
        {
            // Two decimal places of a percent: some zeros, gains and losses.
            values[i] = Math.Round((decimal)((random.NextDouble() - 0.45) * 0.04), 4);
        }
        return values;
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 20)]
    [InlineData(3, 60)]
    [InlineData(4, 250)]
    public void DownsideVariance_EqualsTheSortinoInputOfTheBacktestReport(int seed, int length)
    {
        decimal[] window = Returns(seed, length);
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(window, 0m, out DownsideStatistics statistics));

        ImmutableArray<double> asDouble = window.Select(r => (double)r).ToImmutableArray();
        (_, double legacyVariance) = RiskAdjustedMetricsCalculator.MeanAndDownsideVariance(asDouble);

        double kernelVariance = (double)statistics.DownsideDeviation * (double)statistics.DownsideDeviation;
        Assert.True(legacyVariance > 0.0, "The seeded window must hold at least one loss.");
        Assert.InRange(Math.Abs(kernelVariance - legacyVariance) / legacyVariance, 0.0, RelativeTolerance);
    }

    [Fact]
    public void DownsideVariance_OfAWindowWithoutLosses_IsZeroInBothImplementations()
    {
        decimal[] gains = { 0.01m, 0m, 0.02m, 0.005m };
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(gains, 0m, out DownsideStatistics statistics));
        (_, double legacyVariance) = RiskAdjustedMetricsCalculator.MeanAndDownsideVariance(gains.Select(r => (double)r).ToImmutableArray());

        Assert.Equal(0m, statistics.DownsideDeviation);
        Assert.Equal(0.0, legacyVariance);
    }

    private static List<PivotPoint> Pivots(int seed, int count)
    {
        var random = new Random(seed);
        var pivots = new List<PivotPoint>(count);
        int index = random.Next(0, 5);
        for (int i = 0; i < count; i++)
        {
            index += random.Next(1, 6);
            decimal price = 100m + Math.Round((decimal)(random.NextDouble() * 20.0 - 10.0), 2);
            pivots.Add(new PivotPoint(index, new DateTime(2025, 1, 1).AddDays(index), price, isHigh: true));
        }
        return pivots;
    }

    private static decimal KernelSlope(List<PivotPoint> pivots)
    {
        int[] offsets = pivots.Select(p => p.Index).ToArray();
        decimal[] values = pivots.Select(p => p.Price).ToArray();
        var scratch = new decimal[RobustRegressionMath.PairCount(values.Length)];
        Assert.True(RobustRegressionMath.TryTheilSenSlope(offsets, values, scratch, out decimal slope));
        return slope;
    }

    [Theory]
    [InlineData(11, 3)]   // 3 pair slopes
    [InlineData(12, 6)]   // 15
    [InlineData(13, 7)]   // 21
    [InlineData(14, 10)]  // 45
    [InlineData(15, 11)]  // 55
    public void TheilSenSlope_OddPairCount_IsTheSameInBothImplementations(int seed, int pivotCount)
    {
        Assert.Equal(1, RobustRegressionMath.PairCount(pivotCount) % 2);
        List<PivotPoint> pivots = Pivots(seed, pivotCount);

        double legacy = GeometricPatternDetector.CalculateTheilSen(pivots).Slope;
        double kernel = (double)KernelSlope(pivots);

        double scale = Math.Max(Math.Abs(legacy), 1e-9);
        Assert.InRange(Math.Abs(kernel - legacy) / scale, 0.0, RelativeTolerance);
    }

    [Fact]
    public void TheilSenSlope_EvenPairCount_LegacyTakesTheUpperMiddleKernelTheMeanOfBoth()
    {
        // Prices 100, 102, 101, 105 at positions 0..3: the six pair slopes sorted are -1, 0.5, 1.5, 5/3, 2, 4.
        decimal[] prices = { 100m, 102m, 101m, 105m };
        var pivots = prices.Select((p, i) => new PivotPoint(i, new DateTime(2025, 1, 1).AddDays(i), p, isHigh: true)).ToList();
        Assert.Equal(0, RobustRegressionMath.PairCount(prices.Length) % 2);

        double legacy = GeometricPatternDetector.CalculateTheilSen(pivots).Slope;
        decimal kernel = KernelSlope(pivots);

        Assert.Equal(5.0 / 3.0, legacy, 12);                    // sorted[6 / 2]
        Assert.Equal(19m / 12m, kernel, 12);                    // (1.5 + 5/3) / 2
    }
}
