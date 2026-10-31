using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="SeriesSummaryStatistics"/> to hand-computed values and to the existing ZScore / Regression Slope indicators
/// (population deviation, x = 0..n-1), plus the owner decisions D-06b = A (smaller-count rank, latest excluded) and D-06c = A (flat window = null).
/// </summary>
public class SeriesSummaryStatisticsTests
{
    private static readonly decimal Tolerance = 0.000000000001m;

    private static decimal?[] Series(params decimal?[] values) => values;

    private static List<CoreCandleData> CandlesFromCloses(IReadOnlyList<decimal> closes)
    {
        var candles = new List<CoreCandleData>(closes.Count);
        for (int i = 0; i < closes.Count; i++)
        {
            decimal c = closes[i];
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c, c, c, c, 1000));
        }

        return candles;
    }

    private static List<decimal> WavyCloses(int count) =>
        Enumerable.Range(0, count).Select(i => 100m + ((i * 37) % 23) * 0.37m - (i % 5) * 0.11m).ToList();

    [Fact]
    public void EmptySeries_HasZeroCountAndNoStatistics()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Array.Empty<decimal?>(), 20);

        Assert.Equal(0, s.Count);
        Assert.Null(s.Latest);
        Assert.Null(s.Mean);
        Assert.Null(s.StdDev);
        Assert.Null(s.ZScoreOfLatest);
        Assert.Null(s.PercentileRankOfLatest);
        Assert.Null(s.TrendSlope);
        Assert.Equal(20, s.TrendBars);
    }

    [Fact]
    public void AllNullSeries_HasZeroCountAndNoStatistics()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(null, null, null), 2);

        Assert.Equal(0, s.Count);
        Assert.Null(s.Latest);
        Assert.Null(s.Mean);
        Assert.Null(s.TrendSlope);
    }

    [Fact]
    public void SingleValue_HasMeanButNoDeviationRankOrSlope()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(null, 7.5m), 2);

        Assert.Equal(1, s.Count);
        Assert.Equal(7.5m, s.Latest);
        Assert.Equal(7.5m, s.Mean);
        Assert.Null(s.StdDev);
        Assert.Null(s.ZScoreOfLatest);
        Assert.Null(s.PercentileRankOfLatest);
        Assert.Null(s.TrendSlope);
    }

    [Fact]
    public void LinearSeries_MatchesHandComputedValues()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(1m, 2m, 3m, 4m, 5m), 5);

        Assert.Equal(5, s.Count);
        Assert.Equal(5m, s.Latest);
        Assert.Equal(3m, s.Mean);
        Assert.NotNull(s.StdDev);
        Assert.InRange(Math.Abs(s.StdDev!.Value - (decimal)Math.Sqrt(2.0)), 0m, Tolerance); // population: sqrt(10 / 5)
        Assert.InRange(Math.Abs(s.ZScoreOfLatest!.Value - 2m / s.StdDev.Value), 0m, Tolerance);
        Assert.Equal(100m, s.PercentileRankOfLatest); // 4 of the 4 earlier values are smaller
        Assert.Equal(1m, s.TrendSlope);
    }

    [Fact]
    public void PercentileRank_OwnerExample_CountsOnlySmallerEarlierValues()
    {
        // D-06b = A: earlier values [3,5,5,8], latest 5 -> 1 smaller of 4 -> 25 (not the median rank 50, not the at-or-below rank 75).
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(3m, 5m, 5m, 8m, 5m), 2);

        Assert.Equal(25m, s.PercentileRankOfLatest);
    }

    [Fact]
    public void PercentileRank_LatestIsLowest_IsZero()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(4m, 5m, 6m, 1m), 2);

        Assert.Equal(0m, s.PercentileRankOfLatest);
    }

    [Fact]
    public void PercentileRank_IgnoresNullsInTheWindow()
    {
        // Earlier non-null values [2,4], latest 6 -> both smaller -> 100.
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(null, null, 2m, null, 4m, 6m), 2);

        Assert.Equal(3, s.Count);
        Assert.Equal(100m, s.PercentileRankOfLatest);
        Assert.Equal(2m, s.TrendSlope); // last two valid values 4, 6
    }

    [Fact]
    public void NullFinalBar_HasNoLatestZScoreOrRank_ButKeepsWindowStatistics()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(1m, 2m, 3m, null), 2);

        Assert.Equal(3, s.Count);
        Assert.Null(s.Latest);
        Assert.Equal(2m, s.Mean);
        Assert.NotNull(s.StdDev);
        Assert.Null(s.ZScoreOfLatest);
        Assert.Null(s.PercentileRankOfLatest);
        Assert.Null(s.TrendSlope); // the last 2 bars hold one value only
        Assert.Equal(1m, SeriesSummaryStatistics.Calculate(Series(1m, 2m, 3m, null), 3).TrendSlope); // bars 2,3 at x = 0,1
    }

    [Fact]
    public void PercentileRank_WithNoEarlierValue_IsNull()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(null, null, 5m), 2);

        Assert.Equal(1, s.Count);
        Assert.Equal(5m, s.Latest);
        Assert.Null(s.PercentileRankOfLatest); // no division by zero
    }

    [Fact]
    public void TrendSlope_KeepsTheBarGapOfANull_InsteadOfClosingIt()
    {
        // Bars 0..4 = [10, null, 12, 13, 14] are y = 10 + x, so keeping the gap gives exactly 1.
        // Closing the gap (x = 0,1,2,3 for 10,12,13,14) would give a different slope.
        decimal? gapKept = SeriesSummaryStatistics.Calculate(Series(10m, null, 12m, 13m, 14m), 5).TrendSlope;
        decimal? gapClosed = SeriesSummaryStatistics.Calculate(Series(10m, 12m, 13m, 14m), 4).TrendSlope;

        Assert.Equal(1m, gapKept);
        Assert.NotEqual(gapKept, gapClosed);
    }

    [Fact]
    public void TrendSlope_WindowIsTheLastTrendBarsBars_NotTheLastValidValues()
    {
        // Window = last 4 bars [null, 12, 13, 14] -> three points at x = 1,2,3 (the 10 outside the window is not used).
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(10m, null, 12m, 13m, 14m), 4);

        Assert.Equal(1m, s.TrendSlope);
        Assert.Equal(4, s.Count);
    }

    [Fact]
    public void TrendSlope_WithFewerThanTwoValuesInTheWindow_IsNull()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(5m, null, null, 7m), 3);

        Assert.Null(s.TrendSlope);
        Assert.NotNull(s.StdDev);
    }

    [Fact]
    public void NegativeValues_AreHandled()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(-3m, -1m, -2m, -6m), 2);

        Assert.Equal(-3m, s.Mean);
        Assert.Equal(-6m, s.Latest);
        Assert.Equal(0m, s.PercentileRankOfLatest);
        Assert.Equal(-4m, s.TrendSlope);
        Assert.True(s.ZScoreOfLatest < 0m);
    }

    [Fact]
    public void FlatSeries_HasZeroDeviationAndUndefinedZScore()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(5m, 5m, 5m, 5m), 3);

        Assert.Equal(0m, s.StdDev);
        Assert.Null(s.ZScoreOfLatest); // D-06c = A: undefined, not 0
        Assert.Equal(0m, s.PercentileRankOfLatest); // nothing is strictly smaller
        Assert.Equal(0m, s.TrendSlope);
    }

    [Fact]
    public void DeviationBelowTheFlatEpsilon_HasUndefinedZScore()
    {
        decimal belowEpsilon = (decimal)ComparisonSeriesDefaults.FlatWindowDeviationEpsilon / 10m;
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(100m, 100m, 100m + belowEpsilon), 2);

        Assert.NotNull(s.StdDev);
        Assert.True(s.StdDev < (decimal)ComparisonSeriesDefaults.FlatWindowDeviationEpsilon);
        Assert.Null(s.ZScoreOfLatest);
    }

    [Fact]
    public void DeviationAtLeastTheFlatEpsilon_HasZScore()
    {
        decimal step = (decimal)ComparisonSeriesDefaults.FlatWindowDeviationEpsilon * 10m;
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(100m, 100m, 100m + step), 2);

        Assert.NotNull(s.ZScoreOfLatest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void TrendBarsBelowTwo_HasNoSlope(int trendBars)
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(1m, 2m, 3m), trendBars);

        Assert.Null(s.TrendSlope);
        Assert.Equal(trendBars, s.TrendBars);
    }

    [Fact]
    public void TrendBarsLargerThanTheSeries_UsesEveryExistingBarAsTheWindow()
    {
        Assert.Equal(1m, SeriesSummaryStatistics.Calculate(Series(null, 1m, 2m, 3m), 20).TrendSlope); // x = 1,2,3
        Assert.Equal(1m, SeriesSummaryStatistics.Calculate(Series(1m, 2m), 20).TrendSlope);
        Assert.Null(SeriesSummaryStatistics.Calculate(Series(null, 2m), 20).TrendSlope);
    }

    [Fact]
    public void ValuesBeyondDecimalRange_GiveUndefinedDeviationInsteadOfThrowing()
    {
        // The sum fits in decimal but the squared deviations (about 1e40) do not.
        decimal big = 1e20m;
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(big, -big, big), 2);

        Assert.Equal(3, s.Count);
        Assert.NotNull(s.Mean);
        Assert.Null(s.StdDev);
        Assert.Null(s.ZScoreOfLatest);
    }

    [Fact]
    public void LargeButRepresentableValues_KeepExactMean()
    {
        SeriesSummary s = SeriesSummaryStatistics.Calculate(Series(1000000000m, 3000000000m), 2);

        Assert.Equal(2000000000m, s.Mean);
        Assert.Equal(1000000000m, s.StdDev);
    }

    [Fact]
    public void NullValues_ArgumentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SeriesSummaryStatistics.Calculate(null!, 2));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(20, 60)]
    [InlineData(5, 40)]
    public void MeanDeviationAndZScore_EqualTheZScoreIndicatorOverTheSameWindow(int period, int barCount)
    {
        List<decimal> closes = WavyCloses(barCount);
        var indicator = new CoreZScoreIndicator { Period = period };
        indicator.Calculate(CandlesFromCloses(closes));
        decimal? indicatorValue = indicator.Values[barCount - 1];

        decimal?[] lastWindow = closes.Skip(barCount - period).Select(c => (decimal?)c).ToArray();
        SeriesSummary s = SeriesSummaryStatistics.Calculate(lastWindow, 2);

        Assert.NotNull(indicatorValue);
        Assert.NotNull(s.ZScoreOfLatest);
        Assert.Equal(indicatorValue!.Value, s.ZScoreOfLatest!.Value);
    }

    [Fact]
    public void FlatWindow_ZScoreIndicatorGivesZero_WhileTheSummaryGivesNull()
    {
        var indicator = new CoreZScoreIndicator { Period = 5 };
        indicator.Calculate(CandlesFromCloses(Enumerable.Repeat(50m, 8).ToList()));

        SeriesSummary s = SeriesSummaryStatistics.Calculate(Enumerable.Repeat((decimal?)50m, 5).ToArray(), 2);

        Assert.Equal(0m, indicator.Values[7]);
        Assert.Null(s.ZScoreOfLatest);
    }

    [Theory]
    [InlineData(14, 50)]
    [InlineData(5, 20)]
    [InlineData(2, 9)]
    public void TrendSlope_EqualsTheRegressionSlopeIndicatorOverTheSameWindow(int period, int barCount)
    {
        List<decimal> closes = WavyCloses(barCount);
        var indicator = new CoreRegressionSlopeIndicator { Period = period };
        indicator.Calculate(CandlesFromCloses(closes));
        decimal? indicatorValue = indicator.Values[barCount - 1];

        SeriesSummary s = SeriesSummaryStatistics.Calculate(closes.Select(c => (decimal?)c).ToArray(), period);

        Assert.NotNull(indicatorValue);
        Assert.Equal(indicatorValue, s.TrendSlope);
    }
}
