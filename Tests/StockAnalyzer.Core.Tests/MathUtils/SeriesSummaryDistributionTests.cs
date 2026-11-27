using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Statistical functions plan, Run R8 (M1): <see cref="SeriesSummaryStatistics.CalculateDistribution"/> and <see cref="RobustRegressionMath.TryTheilSenSlope"/>.
/// The existing summary (<see cref="SeriesSummaryStatistics.Calculate"/>) must stay identical; the new fields are pinned to hand values and scipy references.
/// </summary>
public class SeriesSummaryDistributionTests
{
    private static IReadOnlyList<decimal?> Series(params decimal?[] values) => values;

    private static SeriesSummary WithoutDistribution(SeriesSummary summary) =>
        summary with { Median = null, Iqr = null, Mad = null, Skewness = null, ExcessKurtosis = null, RobustTrendSlope = null };

    [Fact]
    public void Calculate_LeavesEveryDistributionFieldNull_AndCalculateDistributionKeepsEveryExistingFieldIdentical()
    {
        IReadOnlyList<decimal?> values = Series(1m, 4m, null, 9m, 16m, 25m, null, 49m, 64m);

        SeriesSummary plain = SeriesSummaryStatistics.Calculate(values, 5);
        SeriesSummary distribution = SeriesSummaryStatistics.CalculateDistribution(values, 5);

        Assert.Null(plain.Median);
        Assert.Null(plain.Iqr);
        Assert.Null(plain.Mad);
        Assert.Null(plain.Skewness);
        Assert.Null(plain.ExcessKurtosis);
        Assert.Null(plain.RobustTrendSlope);
        Assert.Equal(plain, WithoutDistribution(distribution)); // the whole record, every existing field
        Assert.NotNull(distribution.Median);
    }

    [Fact]
    public void OneToNine_GiveTheHandAndScipyValues()
    {
        // 1..9: median 5, Q(0.75) = 7, Q(0.25) = 3 -> IQR 4, |x - 5| = 4 3 2 1 0 1 2 3 4 -> MAD 2, G1 = 0, G2 = -1.2 (scipy bias=False), slope of the last 5 bars (5..9) = 1.
        SeriesSummary summary = SeriesSummaryStatistics.CalculateDistribution(Enumerable.Range(1, 9).Select(i => (decimal?)i).ToList(), 5);

        Assert.Equal(5m, summary.Median);
        Assert.Equal(4m, summary.Iqr);
        Assert.Equal(2m, summary.Mad);
        Assert.Equal(0m, Math.Round(summary.Skewness!.Value, 12));
        Assert.Equal(-1.2m, Math.Round(summary.ExcessKurtosis!.Value, 12));
        Assert.Equal(1m, summary.RobustTrendSlope);
        Assert.Equal(1m, summary.TrendSlope);
    }

    [Fact]
    public void AnAsymmetricSeries_MatchesScipy()
    {
        // scipy.stats.skew / kurtosis with bias=False; np.median, Q(0.75) - Q(0.25) = 2.625, MAD = 1.25.
        SeriesSummary summary = SeriesSummaryStatistics.CalculateDistribution(Series(2m, 3.5m, 3m, 7.5m, 4m, 4.5m, 12m, 5.5m), 5);

        Assert.Equal(4.25m, summary.Median);
        Assert.Equal(2.625m, summary.Iqr);
        Assert.Equal(1.25m, summary.Mad);
        Assert.Equal(1.5491491010227925, (double)summary.Skewness!.Value, 12);
        Assert.Equal(2.4969827375421776, (double)summary.ExcessKurtosis!.Value, 12);
    }

    [Fact]
    public void FewValues_HaveOnlyTheFiguresTheyCanSupport()
    {
        SeriesSummary one = SeriesSummaryStatistics.CalculateDistribution(Series(null, 7m), 5);
        Assert.Equal(7m, one.Median);
        Assert.Null(one.Iqr);
        Assert.Null(one.Mad);
        Assert.Null(one.Skewness);
        Assert.Null(one.ExcessKurtosis);
        Assert.Null(one.RobustTrendSlope);

        SeriesSummary two = SeriesSummaryStatistics.CalculateDistribution(Series(1m, 3m), 5);
        Assert.Equal(2m, two.Median);
        Assert.Equal(1m, two.Iqr);
        Assert.Equal(1m, two.Mad);
        Assert.Null(two.Skewness);
        Assert.Null(two.ExcessKurtosis);
        Assert.Equal(2m, two.RobustTrendSlope);

        SeriesSummary three = SeriesSummaryStatistics.CalculateDistribution(Series(1m, 2m, 6m), 5);
        Assert.NotNull(three.Skewness);
        Assert.Null(three.ExcessKurtosis);

        SeriesSummary four = SeriesSummaryStatistics.CalculateDistribution(Series(1m, 2m, 6m, 7m), 5);
        Assert.NotNull(four.ExcessKurtosis);

        SeriesSummary empty = SeriesSummaryStatistics.CalculateDistribution(Series(null, null), 5);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Median);
        Assert.Null(empty.RobustTrendSlope);
    }

    [Fact]
    public void AFlatSeries_HasNoSkewnessAndKurtosis_ButAMedianAndZeroSpreads()
    {
        SeriesSummary flat = SeriesSummaryStatistics.CalculateDistribution(Series(5m, 5m, 5m, 5m, 5m), 5);

        Assert.Equal(5m, flat.Median);
        Assert.Equal(0m, flat.Iqr);
        Assert.Equal(0m, flat.Mad);
        Assert.Null(flat.Skewness);
        Assert.Null(flat.ExcessKurtosis);
        Assert.Equal(0m, flat.RobustTrendSlope);
    }

    [Fact]
    public void ALargeConstantSeries_HasNoSkewnessAndKurtosis_NotTheNoiseOfItsRoundedMean()
    {
        // 30 x 1,000,000.1: the mean of equal doubles is off by an ulp, the two-pass deviation is about 5e-10, above the absolute flat threshold of 1e-10.
        // The relative term of the Series rule (1e-12 x |mean|) treats it as a constant: owner decision D-3 = A of the peer-review follow-up.
        SeriesSummary summary = SeriesSummaryStatistics.CalculateDistribution(Enumerable.Repeat((decimal?)1000000.1m, 30).ToList(), 5);

        Assert.Equal(1000000.1m, summary.Median);
        Assert.Equal(0m, summary.Iqr);
        Assert.Null(summary.Skewness);
        Assert.Null(summary.ExcessKurtosis);
    }

    [Fact]
    public void ARobustTrendSlope_UsesTheSameBarPositionsAsTheTrendSlope_AMissingBarLeavesAGap()
    {
        // 10, (gap), 14, 16 at bar offsets 0, 2, 3: every pair slope is 2 (4 / 2, 6 / 3, 2 / 1).
        SeriesSummary gap = SeriesSummaryStatistics.CalculateDistribution(Series(10m, null, 14m, 16m), 4);
        Assert.Equal(2m, gap.RobustTrendSlope);
        Assert.Equal(gap.TrendSlope, gap.RobustTrendSlope);

        // Only the last trendBars bars count: the first values are outside the window.
        SeriesSummary window = SeriesSummaryStatistics.CalculateDistribution(Series(100m, -50m, 1m, 2m, 3m, 4m), 4);
        Assert.Equal(1m, window.RobustTrendSlope);

        // One non-null point in the window: no slope.
        Assert.Null(SeriesSummaryStatistics.CalculateDistribution(Series(1m, 2m, 3m, null, null, 9m), 3).RobustTrendSlope);
    }

    [Fact]
    public void AnOutlier_MovesTheLeastSquaresSlope_ButNotTheRobustSlope()
    {
        // y = 2 i with one gross outlier at bar 7 of 10.
        var values = Enumerable.Range(0, 10).Select(i => (decimal?)(2 * i)).ToList();
        values[7] = 500m;

        SeriesSummary summary = SeriesSummaryStatistics.CalculateDistribution(values, 10);

        Assert.Equal(2m, summary.RobustTrendSlope);
        Assert.True(summary.TrendSlope > 10m); // about 16.7: 2 + 486 * (7 - 4.5) / 82.5
    }

    [Fact]
    public void TheRobustSlopeIsCalculatedOnlyUpToTheMeasuredBound_AndNeverFailsAbove()
    {
        var values = Enumerable.Range(0, 100).Select(i => (decimal?)(3 * i)).ToList();

        SeriesSummary atTheBound = SeriesSummaryStatistics.CalculateDistribution(values, SeriesSummaryStatistics.MaxRobustTrendBars);
        SeriesSummary aboveTheBound = SeriesSummaryStatistics.CalculateDistribution(values, SeriesSummaryStatistics.MaxRobustTrendBars + 1);
        SeriesSummary atTheMinimum = SeriesSummaryStatistics.CalculateDistribution(values, SeriesSummaryStatistics.MinTrendBars);

        Assert.Equal(3m, atTheBound.RobustTrendSlope);
        Assert.Null(aboveTheBound.RobustTrendSlope);
        Assert.Equal(3m, aboveTheBound.TrendSlope);
        Assert.NotNull(aboveTheBound.Median);
        Assert.Equal(3m, atTheMinimum.RobustTrendSlope);
        Assert.Null(SeriesSummaryStatistics.CalculateDistribution(values, 5000).RobustTrendSlope);
    }

    [Fact]
    public void TheBoundIsTheOneDefinitionOfTheKernelWindowLimit_AndTheIndicatorHasItsOwnLargerMeasuredBound()
    {
        Assert.Equal(RobustRegressionMath.MaxWindowLength, SeriesSummaryStatistics.MaxRobustTrendBars);
        Assert.True(IndicatorDefaultConstants.TheilSenMaxPeriod > RobustRegressionMath.MaxWindowLength);   // DI-1 = A2: the indicator bound (70) is independent of the 30 bar bound
        Assert.Equal(30, SeriesSummaryStatistics.MaxRobustTrendBars);
        Assert.True(SeriesSummaryStatistics.MinTrendBars <= SeriesSummaryStatistics.MaxRobustTrendBars);
    }

    [Fact]
    public void TheilSenSlopeOfPoints_EqualsTheSlopeOfTheContiguousFit_AndRejectsBadInput()
    {
        decimal[] values = { 100.25m, 100.9m, 100.4m, 101.6m, 101.1m, 102.35m, 101.9m, 103.2m };
        int[] contiguous = Enumerable.Range(0, values.Length).ToArray();
        decimal[] scratch = new decimal[RobustRegressionMath.PairCount(values.Length)];
        decimal[] fitScratch = new decimal[RobustRegressionMath.ScratchLength(values.Length, siegel: false)];

        Assert.True(RobustRegressionMath.TryTheilSenSlope(contiguous, values, scratch, out decimal slope));
        Assert.True(RobustRegressionMath.TryFit(values, siegel: false, fitScratch, out RobustLine line));
        Assert.Equal(line.Slope, slope);

        Assert.False(RobustRegressionMath.TryTheilSenSlope(new[] { 0, 2, 2 }, new[] { 1m, 2m, 3m }, scratch, out _)); // positions must increase
        Assert.False(RobustRegressionMath.TryTheilSenSlope(new[] { 0, 1 }, new[] { 1m, 2m, 3m }, scratch, out _)); // length mismatch
        Assert.False(RobustRegressionMath.TryTheilSenSlope(new[] { 0 }, new[] { 1m }, scratch, out _)); // one point
        Assert.False(RobustRegressionMath.TryTheilSenSlope(contiguous, values, new decimal[3], out _)); // scratch too short
        Assert.False(RobustRegressionMath.TryTheilSenSlope(
            Enumerable.Range(0, RobustRegressionMath.MaxWindowLength + 1).ToArray(),
            new decimal[RobustRegressionMath.MaxWindowLength + 1],
            new decimal[RobustRegressionMath.PairCount(RobustRegressionMath.MaxWindowLength + 1)],
            out _)); // above the measured bound
        Assert.False(RobustRegressionMath.TryTheilSenSlope(new[] { 0, 1 }, new[] { decimal.MaxValue, decimal.MinValue }, scratch, out _)); // overflow is a failure, not an exception
    }

    [Fact]
    public void TheTheilSenSlopeKernel_AllocatesNothing()
    {
        decimal[] values = { 1m, 3m, 2m, 6m, 5m, 8m };
        int[] offsets = { 0, 1, 3, 4, 5, 6 };
        decimal[] scratch = new decimal[RobustRegressionMath.PairCount(values.Length)];
        RobustRegressionMath.TryTheilSenSlope(offsets, values, scratch, out _);

        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                RobustRegressionMath.TryTheilSenSlope(offsets, values, scratch, out _);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
