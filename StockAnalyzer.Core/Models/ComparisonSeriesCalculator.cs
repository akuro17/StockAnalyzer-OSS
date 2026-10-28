using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Shared defaults and bounds of the relative-performance comparison inputs (the chart's own default is pinned by a test).
/// </summary>
public static class ComparisonSeriesDefaults
{
    /// <summary>Default Z-Score window in bars; the single definition behind <c>GlobalChartSettings.ComparisonZScorePeriod</c>, the chart view model, the export proxy and the range calculator.</summary>
    public const int DefaultZScorePeriod = 20;

    /// <summary>Smallest Z-Score window that can produce a value (a window of one bar has zero deviation).</summary>
    public const int MinZScorePeriod = 2;

    /// <summary>Largest Z-Score window the settings dialog offers.</summary>
    public const int MaxZScorePeriod = 500;

    /// <summary>
    /// Absolute population standard deviation, in price units, below which a Z-Score window counts as flat and has no value.
    /// A numeric guard (not a tunable); it is not relative to the price level.
    /// </summary>
    public const double FlatWindowDeviationEpsilon = 1e-10;
}

/// <summary>
/// One symbol's values of a relative-performance comparison over a window of the aligned timeline.
/// </summary>
/// <param name="Values">One entry per window bar, oldest first; null where the value is undefined.</param>
/// <param name="BaseIndex">Performance only: index in the aligned timeline of the bar whose close is the 0 % base; null when there is none.</param>
/// <param name="BasePrice">Performance only: the close at <paramref name="BaseIndex"/>; null when there is none.</param>
public sealed record ComparisonSeries(decimal?[] Values, int? BaseIndex, decimal? BasePrice);

/// <summary>Per-symbol result of <see cref="ComparisonSeriesCalculator.Calculate"/>, keyed like <see cref="ComparisonAlignedData.Series"/>.</summary>
public sealed record ComparisonSeriesResult(IReadOnlyDictionary<string, ComparisonSeries> Series);

/// <summary>
/// The values the Relative Performance chart plots, per <see cref="ComparisonMode"/>, without any rendering concern.
/// Formulas are those of the chart (ChartDataCoordinator.CreateRelativePerformanceSnapshot):
/// Performance = (Close − BaseClose) / BaseClose × 100; Ratio = Close / PrimaryClose; Spread = Close − PrimaryClose;
/// ZScore = (Close − mean) / population standard deviation over the last <c>zScorePeriod</c> bars
/// (<see cref="ComparisonPriceRangeCalculator.ComputeZScore"/>, computed in double there).
/// </summary>
public static class ComparisonSeriesCalculator
{
    /// <summary>
    /// Calculates the values of every series of <paramref name="data"/> for the bars <c>startIndex .. startIndex + count - 1</c>.
    /// <paramref name="startIndex"/> is clamped to 0 and the window to the timeline; the Performance base is searched from the
    /// window's first bar (the chart's left edge): that bar's close, else the nearest earlier close, else the nearest later close.
    /// </summary>
    public static ComparisonSeriesResult Calculate(ComparisonAlignedData data, int startIndex, int count, ComparisonMode mode, int zScorePeriod)
    {
        ArgumentNullException.ThrowIfNull(data);

        int start = Math.Max(0, startIndex);
        int windowCount = Math.Max(0, Math.Min(count, data.Timestamps.Length - start));
        data.Series.TryGetValue(data.PrimarySymbol, out CandleData?[]? primarySeries);

        var result = new Dictionary<string, ComparisonSeries>(data.Series.Count);
        foreach (KeyValuePair<string, CandleData?[]> entry in data.Series)
        {
            CandleData?[] series = entry.Value;
            var values = new decimal?[windowCount];
            int? baseIndex = null;
            decimal? basePrice = null;

            if (series != null && series.Length > 0)
            {
                if (mode == ComparisonMode.Performance)
                {
                    baseIndex = FindBaseIndex(series, start);
                    basePrice = baseIndex.HasValue ? series[baseIndex.Value]!.Value.Close : null;
                }

                for (int i = 0; i < windowCount; i++)
                {
                    int sourceIndex = start + i;
                    if (sourceIndex >= series.Length || !series[sourceIndex].HasValue)
                    {
                        continue;
                    }

                    decimal close = series[sourceIndex]!.Value.Close;
                    values[i] = mode switch
                    {
                        ComparisonMode.Performance => basePrice is { } baseClose ? PerformanceValue(close, baseClose) : null,
                        ComparisonMode.Ratio => PrimaryClose(primarySeries, sourceIndex) is { } ratioClose ? RatioValue(close, ratioClose) : null,
                        ComparisonMode.Spread => PrimaryClose(primarySeries, sourceIndex) is { } spreadClose ? SpreadValue(close, spreadClose) : null,
                        ComparisonMode.ZScore => ComparisonPriceRangeCalculator.ComputeZScore(series, sourceIndex, zScorePeriod, out double z) ? (decimal)z : null,
                        _ => null
                    };
                }
            }

            result[entry.Key] = new ComparisonSeries(values, baseIndex, basePrice);
        }

        return new ComparisonSeriesResult(result);
    }

    /// <summary>Performance: percentage change of <paramref name="close"/> from <paramref name="baseClose"/>; null when the base is 0. Shared with <see cref="ComparisonPriceRangeCalculator"/>.</summary>
    internal static decimal? PerformanceValue(decimal close, decimal baseClose)
        => baseClose != 0m ? (close - baseClose) / baseClose * 100m : null;

    /// <summary>Ratio: <paramref name="close"/> divided by the primary symbol's close; null when that is 0. Shared with <see cref="ComparisonPriceRangeCalculator"/>.</summary>
    internal static decimal? RatioValue(decimal close, decimal primaryClose)
        => primaryClose != 0m ? close / primaryClose : null;

    /// <summary>Spread: <paramref name="close"/> minus the primary symbol's close (price units). Shared with <see cref="ComparisonPriceRangeCalculator"/>.</summary>
    internal static decimal SpreadValue(decimal close, decimal primaryClose)
        => close - primaryClose;

    private static decimal? PrimaryClose(CandleData?[]? primarySeries, int index)
        => primarySeries != null && index < primarySeries.Length && primarySeries[index] is { } candle ? candle.Close : null;

    /// <summary>
    /// Index of the Performance base bar: the bar at <paramref name="start"/> (a non-negative index; beyond the series it counts as the last bar),
    /// else the nearest earlier bar with a close, else the nearest later one; null when the series has no close at all.
    /// Shared with <see cref="ComparisonPriceRangeCalculator"/> so the chart's range and the values use one definition.
    /// </summary>
    internal static int? FindBaseIndex(CandleData?[] series, int start)
    {
        if (series.Length > start && series[start].HasValue)
        {
            return start;
        }

        int anchor = Math.Min(start, series.Length - 1);
        for (int i = anchor; i >= 0; i--)
        {
            if (series[i].HasValue)
            {
                return i;
            }
        }

        for (int i = anchor + 1; i < series.Length; i++)
        {
            if (series[i].HasValue)
            {
                return i;
            }
        }

        return null;
    }
}
