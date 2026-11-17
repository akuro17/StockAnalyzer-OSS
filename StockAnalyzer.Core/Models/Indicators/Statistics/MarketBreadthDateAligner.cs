using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Shared trading-date alignment and value-conversion helpers for <see cref="IUniverseBreadthIndicator"/> implementations.
/// </summary>
internal static class MarketBreadthDateAligner
{
    /// <summary>ISO date format emitted by <see cref="MarketBreadthCalculator"/> in <c>Series.Dates</c>.</summary>
    internal const string DateKeyFormat = "yyyy-MM-dd";

    internal static void ValidateUniverseGroupId(string universeGroupId, string paramName = "UniverseGroupId")
    {
        if (!string.IsNullOrEmpty(universeGroupId) && !Guid.TryParse(universeGroupId, out _))
        {
            throw new ArgumentOutOfRangeException(paramName, universeGroupId, $"{paramName} must be empty or a valid GUID.");
        }
    }

    internal static void ValidatePeriod(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be between {min} and {max}.");
        }
    }

    /// <summary>
    /// The fast EMA period of the McClellan Oscillator must be strictly shorter than the slow one: equal periods give an oscillator of 0 on every bar,
    /// and a fast period above the slow one reverses its sign. Called after the range checks of both periods, so a value outside its own range is reported first.
    /// </summary>
    internal static void ValidateFastBelowSlow(int fastPeriod, int slowPeriod, string fastParamName)
    {
        if (fastPeriod >= slowPeriod)
        {
            throw new ArgumentOutOfRangeException(
                fastParamName,
                fastPeriod,
                $"{fastParamName} ({fastPeriod}) must be strictly less than the slow period ({slowPeriod}).");
        }
    }

    /// <summary>
    /// Maps each candle bar index to its matching index in <paramref name="result"/>.<c>Series.Dates</c>,
    /// or <c>-1</c> when that trading date is absent from the universe breadth series.
    /// Returns <c>null</c> when no universe result is injected or <paramref name="universeGroupId"/> is empty/invalid.
    /// </summary>
    internal static int[]? MapCandleIndices(
        IReadOnlyList<CoreCandleData> candles,
        MarketBreadthResult? result,
        string universeGroupId,
        TimeFrame timeFrame)
    {
        if (candles.Count == 0
            || result is null
            || string.IsNullOrWhiteSpace(universeGroupId)
            || !Guid.TryParse(universeGroupId, out _)
            || result.Series.Dates.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> breadthDates = result.Series.Dates;
        var indexByDate = new Dictionary<string, int>(breadthDates.Count, StringComparer.Ordinal);
        for (int i = 0; i < breadthDates.Count; i++)
        {
            indexByDate[breadthDates[i]] = i;
        }

        var mapped = new int[candles.Count];
        for (int i = 0; i < candles.Count; i++)
        {
            DateTime matchDate = TimeFrameAggregator.PeriodMatchDate(candles[i].Timestamp, timeFrame);
            string key = matchDate.ToString(DateKeyFormat, CultureInfo.InvariantCulture);
            mapped[i] = indexByDate.TryGetValue(key, out int breadthIndex) ? breadthIndex : -1;
        }

        return mapped;
    }

    internal static void PopulateFromDoubles(
        List<decimal?> target,
        int barCount,
        int[]? mappedIndices,
        IReadOnlyList<double?> source,
        double scale = 1.0)
        => PopulateAligned(
            target,
            barCount,
            mappedIndices,
            source,
            raw => raw is double value
                ? IndicatorCalculationHelper.ToDecimalOrNull(scale == 1.0 ? value : value * scale)
                : null);

    internal static void PopulateFromInts(
        List<decimal?> target,
        int barCount,
        int[]? mappedIndices,
        IReadOnlyList<int?> source)
        => PopulateAligned(target, barCount, mappedIndices, source, static raw => raw is int value ? value : null);

    internal static void PopulateFromDecimals(
        List<decimal?> target,
        int barCount,
        int[]? mappedIndices,
        IReadOnlyList<decimal?> source)
        => PopulateAligned(target, barCount, mappedIndices, source, static raw => raw);

    private static void PopulateAligned<T>(
        List<decimal?> target,
        int barCount,
        int[]? mappedIndices,
        IReadOnlyList<T> source,
        Func<T, decimal?> convert)
    {
        target.Clear();
        if (mappedIndices is null)
        {
            FillNulls(target, barCount);
            return;
        }

        for (int i = 0; i < barCount; i++)
        {
            int idx = mappedIndices[i];
            target.Add(idx >= 0 && idx < source.Count ? convert(source[idx]) : null);
        }
    }

    private static void FillNulls(List<decimal?> target, int barCount)
    {
        for (int i = 0; i < barCount; i++)
        {
            target.Add(null);
        }
    }
}
