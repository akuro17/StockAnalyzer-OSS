using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

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
    int TrendBars);

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
