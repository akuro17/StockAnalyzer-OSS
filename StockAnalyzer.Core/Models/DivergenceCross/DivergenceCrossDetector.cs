using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.DivergenceCross;

/// <summary>
/// Detects divergences between price and indicator series,
/// and moving average crossovers (Golden Cross / Dead Cross).
/// Uses local extrema extraction (fractal logic) for robust pivot detection.
/// </summary>
public static class DivergenceCrossDetector
{
    /// <summary>
    /// Minimum number of data points required for divergence detection.
    /// </summary>
    public const int MinimumDataCount = 10;

    /// <summary>
    /// Detects divergences between price highs/lows and an indicator series.
    /// </summary>
    /// <param name="priceHighs">High prices for each bar.</param>
    /// <param name="priceLows">Low prices for each bar.</param>
    /// <param name="indicatorValues">Indicator values (e.g., RSI) for each bar.</param>
    /// <param name="pivotOrder">
    /// Number of bars on each side to confirm a local extremum.
    /// Higher values produce fewer, more significant pivots. Default: 5.
    /// </param>
    /// <returns>List of detected divergence signals.</returns>
    public static IReadOnlyList<DivergenceSignal> DetectDivergences(
        IReadOnlyList<decimal> priceHighs,
        IReadOnlyList<decimal> priceLows,
        IReadOnlyList<decimal?> indicatorValues,
        int pivotOrder = 5)
    {
        if (priceHighs == null || priceLows == null || indicatorValues == null)
            return Array.Empty<DivergenceSignal>();

        int count = Math.Min(priceHighs.Count, Math.Min(priceLows.Count, indicatorValues.Count));
        if (count < MinimumDataCount)
            return Array.Empty<DivergenceSignal>();

        // Extract local extrema from price and indicator
        var priceSwingHighs = ExtractLocalMaxima(priceHighs, pivotOrder);
        var priceSwingLows = ExtractLocalMinima(priceLows, pivotOrder);
        var indicatorHighs = ExtractLocalMaximaNullable(indicatorValues, pivotOrder);
        var indicatorLows = ExtractLocalMinimaNullable(indicatorValues, pivotOrder);

        var signals = new List<DivergenceSignal>();

        // Detect bearish divergences (comparing highs)
        DetectHighDivergences(priceSwingHighs, indicatorHighs, signals);

        // Detect bullish divergences (comparing lows)
        DetectLowDivergences(priceSwingLows, indicatorLows, signals);

        return signals;
    }

    /// <summary>
    /// Detects crossover events between two series (e.g., short MA vs long MA).
    /// </summary>
    /// <param name="shortSeries">The faster/shorter period series.</param>
    /// <param name="longSeries">The slower/longer period series.</param>
    /// <returns>List of detected cross signals.</returns>
    public static IReadOnlyList<CrossSignal> DetectCrosses(
        IReadOnlyList<decimal?> shortSeries,
        IReadOnlyList<decimal?> longSeries)
    {
        if (shortSeries == null || longSeries == null)
            return Array.Empty<CrossSignal>();

        int count = Math.Min(shortSeries.Count, longSeries.Count);
        if (count < 2)
            return Array.Empty<CrossSignal>();

        var signals = new List<CrossSignal>();

        for (int i = 1; i < count; i++)
        {
            decimal? prevShort = shortSeries[i - 1];
            decimal? prevLong = longSeries[i - 1];
            decimal? currShort = shortSeries[i];
            decimal? currLong = longSeries[i];

            if (!prevShort.HasValue || !prevLong.HasValue ||
                !currShort.HasValue || !currLong.HasValue)
                continue;

            decimal prevDiff = prevShort.Value - prevLong.Value;
            decimal currDiff = currShort.Value - currLong.Value;

            // Golden Cross: short crosses above long
            if (prevDiff <= 0 && currDiff > 0)
            {
                signals.Add(new CrossSignal(
                    SignalType.GoldenCross,
                    i,
                    currShort.Value,
                    currLong.Value));
            }
            // Dead Cross: short crosses below long
            else if (prevDiff >= 0 && currDiff < 0)
            {
                signals.Add(new CrossSignal(
                    SignalType.DeadCross,
                    i,
                    currShort.Value,
                    currLong.Value));
            }
        }

        return signals;
    }

    /// <summary>
    /// Extracts local maxima (swing highs) from a price series using fractal logic.
    /// A local maximum is a bar whose value is higher than all surrounding bars within the given order.
    /// </summary>
    internal static List<(int Index, decimal Value)> ExtractLocalMaxima(
        IReadOnlyList<decimal> values, int order)
    {
        var maxima = new List<(int Index, decimal Value)>();
        if (values.Count < 2 * order + 1) return maxima;

        for (int i = order; i < values.Count - order; i++)
        {
            bool isMax = true;
            decimal val = values[i];

            for (int j = 1; j <= order; j++)
            {
                if (values[i - j] >= val || values[i + j] >= val)
                {
                    isMax = false;
                    break;
                }
            }

            if (isMax)
            {
                maxima.Add((i, val));
            }
        }

        return maxima;
    }

    /// <summary>
    /// Extracts local minima (swing lows) from a price series using fractal logic.
    /// </summary>
    internal static List<(int Index, decimal Value)> ExtractLocalMinima(
        IReadOnlyList<decimal> values, int order)
    {
        var minima = new List<(int Index, decimal Value)>();
        if (values.Count < 2 * order + 1) return minima;

        for (int i = order; i < values.Count - order; i++)
        {
            bool isMin = true;
            decimal val = values[i];

            for (int j = 1; j <= order; j++)
            {
                if (values[i - j] <= val || values[i + j] <= val)
                {
                    isMin = false;
                    break;
                }
            }

            if (isMin)
            {
                minima.Add((i, val));
            }
        }

        return minima;
    }

    /// <summary>
    /// Extracts local maxima from a nullable indicator series, skipping null values.
    /// </summary>
    internal static List<(int Index, decimal Value)> ExtractLocalMaximaNullable(
        IReadOnlyList<decimal?> values, int order)
    {
        var maxima = new List<(int Index, decimal Value)>();
        if (values.Count < 2 * order + 1) return maxima;

        for (int i = order; i < values.Count - order; i++)
        {
            if (!values[i].HasValue) continue;
            decimal val = values[i]!.Value;
            bool isMax = true;

            for (int j = 1; j <= order; j++)
            {
                decimal? left = values[i - j];
                decimal? right = values[i + j];

                if (!left.HasValue || !right.HasValue ||
                    left.Value >= val || right.Value >= val)
                {
                    isMax = false;
                    break;
                }
            }

            if (isMax)
            {
                maxima.Add((i, val));
            }
        }

        return maxima;
    }

    /// <summary>
    /// Extracts local minima from a nullable indicator series, skipping null values.
    /// </summary>
    internal static List<(int Index, decimal Value)> ExtractLocalMinimaNullable(
        IReadOnlyList<decimal?> values, int order)
    {
        var minima = new List<(int Index, decimal Value)>();
        if (values.Count < 2 * order + 1) return minima;

        for (int i = order; i < values.Count - order; i++)
        {
            if (!values[i].HasValue) continue;
            decimal val = values[i]!.Value;
            bool isMin = true;

            for (int j = 1; j <= order; j++)
            {
                decimal? left = values[i - j];
                decimal? right = values[i + j];

                if (!left.HasValue || !right.HasValue ||
                    left.Value <= val || right.Value <= val)
                {
                    isMin = false;
                    break;
                }
            }

            if (isMin)
            {
                minima.Add((i, val));
            }
        }

        return minima;
    }

    /// <summary>
    /// Compares consecutive swing highs between price and indicator to detect
    /// Regular Bearish and Hidden Bearish divergences.
    /// </summary>
    private static void DetectHighDivergences(
        List<(int Index, decimal Value)> priceHighs,
        List<(int Index, decimal Value)> indicatorHighs,
        List<DivergenceSignal> signals)
    {
        if (priceHighs.Count < 2 || indicatorHighs.Count < 2) return;

        // Match price swing highs with nearest indicator swing highs
        for (int pi = 1; pi < priceHighs.Count; pi++)
        {
            var prevPrice = priceHighs[pi - 1];
            var currPrice = priceHighs[pi];

            // Find nearest indicator highs to each price high
            var prevIndHigh = FindNearest(indicatorHighs, prevPrice.Index);
            var currIndHigh = FindNearest(indicatorHighs, currPrice.Index);

            if (prevIndHigh == null || currIndHigh == null) continue;
            if (prevIndHigh.Value.Index == currIndHigh.Value.Index) continue;

            bool priceHigherHigh = currPrice.Value > prevPrice.Value;
            bool indicatorHigherHigh = currIndHigh.Value.Value > prevIndHigh.Value.Value;

            // Regular Bearish: price higher high + indicator lower high
            if (priceHigherHigh && !indicatorHigherHigh)
            {
                signals.Add(new DivergenceSignal(
                    SignalType.RegularBearishDivergence,
                    prevPrice.Index, currPrice.Index,
                    prevPrice.Value, currPrice.Value,
                    prevIndHigh.Value.Index, currIndHigh.Value.Index,
                    prevIndHigh.Value.Value, currIndHigh.Value.Value));
            }
            // Hidden Bearish: price lower high + indicator higher high
            else if (!priceHigherHigh && indicatorHigherHigh)
            {
                signals.Add(new DivergenceSignal(
                    SignalType.HiddenBearishDivergence,
                    prevPrice.Index, currPrice.Index,
                    prevPrice.Value, currPrice.Value,
                    prevIndHigh.Value.Index, currIndHigh.Value.Index,
                    prevIndHigh.Value.Value, currIndHigh.Value.Value));
            }
        }
    }

    /// <summary>
    /// Compares consecutive swing lows between price and indicator to detect
    /// Regular Bullish and Hidden Bullish divergences.
    /// </summary>
    private static void DetectLowDivergences(
        List<(int Index, decimal Value)> priceLows,
        List<(int Index, decimal Value)> indicatorLows,
        List<DivergenceSignal> signals)
    {
        if (priceLows.Count < 2 || indicatorLows.Count < 2) return;

        for (int pi = 1; pi < priceLows.Count; pi++)
        {
            var prevPrice = priceLows[pi - 1];
            var currPrice = priceLows[pi];

            var prevIndLow = FindNearest(indicatorLows, prevPrice.Index);
            var currIndLow = FindNearest(indicatorLows, currPrice.Index);

            if (prevIndLow == null || currIndLow == null) continue;
            if (prevIndLow.Value.Index == currIndLow.Value.Index) continue;

            bool priceLowerLow = currPrice.Value < prevPrice.Value;
            bool indicatorLowerLow = currIndLow.Value.Value < prevIndLow.Value.Value;

            // Regular Bullish: price lower low + indicator higher low
            if (priceLowerLow && !indicatorLowerLow)
            {
                signals.Add(new DivergenceSignal(
                    SignalType.RegularBullishDivergence,
                    prevPrice.Index, currPrice.Index,
                    prevPrice.Value, currPrice.Value,
                    prevIndLow.Value.Index, currIndLow.Value.Index,
                    prevIndLow.Value.Value, currIndLow.Value.Value));
            }
            // Hidden Bullish: price higher low + indicator lower low
            else if (!priceLowerLow && indicatorLowerLow)
            {
                signals.Add(new DivergenceSignal(
                    SignalType.HiddenBullishDivergence,
                    prevPrice.Index, currPrice.Index,
                    prevPrice.Value, currPrice.Value,
                    prevIndLow.Value.Index, currIndLow.Value.Index,
                    prevIndLow.Value.Value, currIndLow.Value.Value));
            }
        }
    }

    /// <summary>
    /// Finds the nearest pivot to a given bar index.
    /// </summary>
    private static (int Index, decimal Value)? FindNearest(
        List<(int Index, decimal Value)> pivots, int targetIndex)
    {
        if (pivots.Count == 0) return null;

        (int Index, decimal Value) best = pivots[0];
        int bestDist = Math.Abs(pivots[0].Index - targetIndex);

        for (int i = 1; i < pivots.Count; i++)
        {
            int dist = Math.Abs(pivots[i].Index - targetIndex);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pivots[i];
            }
        }

        return best;
    }
}
