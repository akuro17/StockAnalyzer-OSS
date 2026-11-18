using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Summary of one value series. Units: <see cref="Latest"/>, <see cref="Mean"/>, <see cref="StdDev"/> are in the series' own unit,
/// <see cref="TrendSlope"/> is that unit per bar, <see cref="ZScoreOfLatest"/> is dimensionless and <see cref="PercentileRankOfLatest"/> is a percentage (0..100).
/// </summary>
/// <param name="Count">Number of non-null values.</param>
/// <param name="Latest">Value of the final bar; null when that bar has no value.</param>
/// <param name="Mean">Arithmetic mean of all non-null values.</param>
/// <param name="StdDev">Population standard deviation (divisor n) of all non-null values; null below two values.</param>
/// <param name="ZScoreOfLatest">(Latest - Mean) / StdDev; null when undefined (flat series, missing latest).</param>
/// <param name="PercentileRankOfLatest">Percentage of the earlier non-null values that are strictly smaller than Latest.</param>
/// <param name="TrendSlope">Least-squares slope over the last <paramref name="TrendBars"/> bars (x = bar offset, nulls leave gaps); null below two non-null points in that window.</param>
/// <param name="TrendBars">The trend window that was requested.</param>
public sealed record SeriesSummary(
    int Count,
    decimal? Latest,
    decimal? Mean,
    decimal? StdDev,
    decimal? ZScoreOfLatest,
    decimal? PercentileRankOfLatest,
    decimal? TrendSlope,
    int TrendBars)
{
    /// <summary>Median (type 7 quantile at 0.5) of all non-null values; only set by <see cref="SeriesSummaryStatistics.CalculateDistribution"/>.</summary>
    public decimal? Median { get; init; }

    /// <summary>Interquartile range Q(0.75) - Q(0.25) (type 7), series unit; null below two values; only set by <see cref="SeriesSummaryStatistics.CalculateDistribution"/>.</summary>
    public decimal? Iqr { get; init; }

    /// <summary>Median absolute deviation median(|x - median|), raw (not scaled), series unit; null below two values; only set by <see cref="SeriesSummaryStatistics.CalculateDistribution"/>.</summary>
    public decimal? Mad { get; init; }

    /// <summary>Sample skewness G1, dimensionless; null below three values or for a flat series (population deviation below the larger of <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/> and <see cref="WindowMoments.SeriesRelativeFlatEpsilon"/> times the absolute mean, see <see cref="FlatWindowRule.Series"/>).</summary>
    public decimal? Skewness { get; init; }

    /// <summary>Sample excess kurtosis G2, dimensionless; null below four values or for a flat series.</summary>
    public decimal? ExcessKurtosis { get; init; }

    /// <summary>
    /// Theil-Sen slope over the same last <see cref="TrendBars"/> bars as <see cref="TrendSlope"/> (series unit per bar, each value at its own bar position); null below two
    /// non-null points and null whenever the requested <see cref="TrendBars"/> exceeds <see cref="SeriesSummaryStatistics.MaxRobustTrendBars"/> (protection of the quadratic cost).
    /// </summary>
    public decimal? RobustTrendSlope { get; init; }
}

/// <summary>
/// Pure summary statistics of a bar series (oldest first, null = no value at that bar), computed in <c>decimal</c>.
/// Definitions (owner decisions of plan 06): population standard deviation (indicators_formulas_0052); a Z-Score whose
/// deviation is below <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/> is undefined (D-06c = A);
/// the percentile rank counts earlier values strictly smaller than the latest one and does not put the latest value
/// into its own window (D-06b = A, same as the ConnorsRSI percent rank); the trend slope is the raw least-squares slope
/// over the last <c>trendBars</c> bars with x = the bar offset inside that window (D-06d = A, R-3 = X), the same normal-equation form
/// as the Regression Slope indicator (identical when the window holds no null).
/// </summary>
public static class SeriesSummaryStatistics
{
    private const decimal PercentScale = 100m;

    /// <summary>Fewest bars a least-squares trend needs (and so the smallest trend window); the one definition, read by the MCP limits contract and the tool.</summary>
    public const int MinTrendBars = 2;

    /// <summary>
    /// The largest <c>trendBars</c> for which <see cref="SeriesSummary.RobustTrendSlope"/> is calculated (the pair-slope cost is quadratic). It IS the measured window bound of the robust
    /// regression kernel, referenced rather than repeated, so the indicator Period range and this MCP limit can never drift apart. The bound is judged on the requested window, not on the
    /// number of non-null points. The smallest window is <see cref="MinTrendBars"/> (a robust slope of two points is their slope).
    /// </summary>
    public const int MaxRobustTrendBars = RobustRegressionMath.MaxWindowLength;

    /// <summary>
    /// Summarises <paramref name="values"/> (the whole calculation window, not only a returned tail).
    /// <paramref name="trendBars"/> below two yields no trend slope; range checks of the argument belong to the caller.
    /// </summary>
    public static SeriesSummary Calculate(IReadOnlyList<decimal?> values, int trendBars)
    {
        ArgumentNullException.ThrowIfNull(values);

        var valid = new List<decimal>(values.Count);
        foreach (decimal? value in values)
        {
            if (value.HasValue)
            {
                valid.Add(value.Value);
            }
        }

        decimal? latest = values.Count > 0 ? values[values.Count - 1] : null;
        int count = valid.Count;
        if (count == 0)
        {
            return new SeriesSummary(0, latest, null, null, null, null, null, trendBars);
        }

        TryMeanAndStdDev(valid, out decimal? mean, out decimal? stdDev);
        return new SeriesSummary(
            count,
            latest,
            mean,
            stdDev,
            ZScore(latest, mean, stdDev),
            PercentileRank(valid, latest),
            TrendSlope(values, trendBars),
            trendBars);
    }

    /// <summary>
    /// <see cref="Calculate"/> plus the distribution figures of <see cref="SeriesSummary"/> (median, interquartile range, median absolute deviation, sample skewness and excess kurtosis,
    /// Theil-Sen trend slope). Every field <see cref="Calculate"/> fills is identical to its result; the skewness and kurtosis use the series flat rule (population deviation below the larger of
    /// <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/> (absolute) and <see cref="WindowMoments.SeriesRelativeFlatEpsilon"/> times the absolute mean) and are computed in double and narrowed by <see cref="IndicatorCalculationHelper.ToDecimalOrNull"/>.
    /// </summary>
    public static SeriesSummary CalculateDistribution(IReadOnlyList<decimal?> values, int trendBars)
    {
        SeriesSummary summary = Calculate(values, trendBars);
        if (summary.Count == 0)
        {
            return summary;
        }

        var valid = new decimal[summary.Count];
        int index = 0;
        foreach (decimal? value in values)
        {
            if (value.HasValue)
            {
                valid[index++] = value.Value;
            }
        }

        // The moments are summed in the original bar order, so the copy is taken before the selections below reorder `valid` in place.
        decimal? skewness = null;
        decimal? kurtosis = null;
        double[] asDouble = new double[valid.Length];
        for (int i = 0; i < valid.Length; i++)
        {
            asDouble[i] = (double)valid[i];
        }

        if (WindowMoments.TryCompute(asDouble, FlatWindowRule.Series, out WindowMoments moments))
        {
            skewness = IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleSkewness);
            kurtosis = IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleExcessKurtosis);
        }

        // A selection returns an order statistic of the multiset, so each one may run on the buffer the previous one reordered.
        decimal? median = OrderStatisticsMath.Median<decimal>(valid);
        decimal? iqr = null;
        decimal? mad = null;
        if (summary.Count >= 2)
        {
            iqr = OrderStatisticsMath.InterquartileRange<decimal>(valid);
            mad = OrderStatisticsMath.MedianAbsoluteDeviation<decimal>(valid, new decimal[valid.Length]);
        }

        return summary with
        {
            Median = median,
            Iqr = iqr,
            Mad = mad,
            Skewness = skewness,
            ExcessKurtosis = kurtosis,
            RobustTrendSlope = RobustTrendSlopeOf(values, trendBars),
        };
    }

    private static decimal? RobustTrendSlopeOf(IReadOnlyList<decimal?> values, int trendBars)
    {
        if (trendBars < MinTrendBars || trendBars > MaxRobustTrendBars)
        {
            return null;
        }

        // Same window as TrendSlope: the last trendBars BARS, each non-null point at its own bar offset (a null leaves a gap).
        int windowStart = Math.Max(0, values.Count - trendBars);
        Span<int> offsets = stackalloc int[MaxRobustTrendBars];
        Span<decimal> points = stackalloc decimal[MaxRobustTrendBars];
        int n = 0;
        for (int i = windowStart; i < values.Count; i++)
        {
            if (values[i] is { } y)
            {
                offsets[n] = i - windowStart;
                points[n] = y;
                n++;
            }
        }

        if (n < MinTrendBars)
        {
            return null;
        }

        Span<decimal> scratch = stackalloc decimal[RobustRegressionMath.PairCount(MaxRobustTrendBars)];
        return RobustRegressionMath.TryTheilSenSlope(offsets.Slice(0, n), points.Slice(0, n), scratch, out decimal slope) ? slope : null;
    }

    private static void TryMeanAndStdDev(List<decimal> valid, out decimal? mean, out decimal? stdDev)
    {
        mean = null;
        stdDev = null;
        try
        {
            decimal sum = 0m;
            foreach (decimal value in valid)
            {
                sum += value;
            }

            mean = sum / valid.Count;
            if (valid.Count < 2)
            {
                return;
            }

            decimal sumSquaredDifferences = 0m;
            foreach (decimal value in valid)
            {
                decimal difference = value - mean.Value;
                sumSquaredDifferences += difference * difference;
            }

            decimal variance = Math.Max(0m, sumSquaredDifferences / valid.Count);
            stdDev = (decimal)Math.Sqrt((double)variance);
        }
        catch (OverflowException)
        {
            // Magnitudes beyond decimal range have no representable statistic: report them as undefined, never as a wrong number.
            stdDev = null;
        }
    }

    private static decimal? ZScore(decimal? latest, decimal? mean, decimal? stdDev)
    {
        if (!latest.HasValue || !mean.HasValue || !stdDev.HasValue
            || stdDev.Value < (decimal)ComparisonSeriesDefaults.FlatWindowDeviationEpsilon)
        {
            return null;
        }

        try
        {
            return (latest.Value - mean.Value) / stdDev.Value;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static decimal? PercentileRank(List<decimal> valid, decimal? latest)
    {
        // The earlier values are every non-null value except the final bar's own value (valid ends with it when it exists).
        int earlier = valid.Count - 1;
        if (!latest.HasValue || earlier < 1)
        {
            return null;
        }

        int smaller = 0;
        for (int i = 0; i < earlier; i++)
        {
            if (valid[i] < latest.Value)
            {
                smaller++;
            }
        }

        return (decimal)smaller / earlier * PercentScale;
    }

    private static decimal? TrendSlope(IReadOnlyList<decimal?> values, int trendBars)
    {
        if (trendBars < MinTrendBars)
        {
            return null;
        }

        // The window is the last trendBars BARS (all bars when the series is shorter). Only the non-null points inside it are regressed,
        // each at its own bar offset, so a null leaves a gap on the time axis instead of pulling its neighbours together.
        int windowStart = Math.Max(0, values.Count - trendBars);
        int n = 0;
        decimal sumX = 0m;
        decimal sumX2 = 0m;
        decimal sumY = 0m;
        decimal sumXY = 0m;
        try
        {
            for (int i = windowStart; i < values.Count; i++)
            {
                if (values[i] is not { } y)
                {
                    continue;
                }

                decimal x = i - windowStart;
                n++;
                sumX += x;
                sumX2 += x * x;
                sumY += y;
                sumXY += x * y;
            }

            if (n < MinTrendBars)
            {
                return null;
            }

            decimal denominator = n * sumX2 - sumX * sumX;
            return denominator == 0m ? null : (n * sumXY - sumX * sumY) / denominator;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
