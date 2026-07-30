using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Validation;

/// <summary>
/// Provides shared validation methods for pattern formation process constraints.
/// Evaluates time duration, volatility significance, and temporal symmetry
/// to filter out patterns formed over too few candles or with insufficient price action.
/// </summary>
public static class PatternFormationValidator
{
    /// <summary>
    /// Validates that a pattern spans at least the minimum required number of bars.
    /// </summary>
    /// <param name="span">The number of bars (candles) the pattern covers (endIndex - startIndex).</param>
    /// <param name="minBars">The minimum number of bars required for the pattern category.</param>
    /// <returns>True if the pattern meets the minimum bar requirement.</returns>
    public static bool ValidateMinBars(int span, int minBars)
    {
        return span >= minBars;
    }

    /// <summary>
    /// Validates that each leg of a pattern has sufficient price movement relative to local ATR.
    /// A "leg" is defined by a start and end index in the candle array.
    /// </summary>
    /// <param name="legs">
    /// List of (startIndex, endIndex) tuples defining each leg of the pattern.
    /// </param>
    /// <param name="candles">The full candle data array.</param>
    /// <param name="atrMultiplier">
    /// The minimum price movement per leg expressed as a multiple of the local ATR.
    /// </param>
    /// <returns>True if all legs meet the volatility threshold.</returns>
    public static bool ValidateVolatility(
        IReadOnlyList<(int StartIndex, int EndIndex)> legs,
        IReadOnlyList<CandleData> candles,
        double atrMultiplier)
    {
        if (legs == null || legs.Count == 0 || candles == null || candles.Count == 0)
            return false;

        for (int i = 0; i < legs.Count; i++)
        {
            var (startIdx, endIdx) = legs[i];
            if (startIdx < 0 || endIdx >= candles.Count || startIdx >= endIdx)
                return false;

            double atr = ComputeLocalATR(candles, startIdx, endIdx);
            if (atr <= 0)
                continue; // Degenerate case: flat prices — handled by ratio check

            decimal legMove = Math.Abs(candles[endIdx].Close - candles[startIdx].Close);
            if ((double)legMove < atr * atrMultiplier)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates time symmetry across pattern legs by ensuring no single leg's duration
    /// is disproportionately long or short compared to others.
    /// </summary>
    /// <param name="legDurations">
    /// The duration (in bars) of each leg. Must contain at least 2 elements.
    /// </param>
    /// <param name="maxRatio">
    /// The maximum allowed ratio between the longest and shortest leg durations.
    /// For example, 5.0 means the longest leg cannot be more than 5x the shortest.
    /// </param>
    /// <returns>True if all leg duration ratios are within the allowed range.</returns>
    public static bool ValidateTimeSymmetry(IReadOnlyList<int> legDurations, double maxRatio)
    {
        if (legDurations == null || legDurations.Count < 2)
            return true; // Single leg or empty — no symmetry to validate

        int minDuration = int.MaxValue;
        int maxDuration = int.MinValue;

        for (int i = 0; i < legDurations.Count; i++)
        {
            if (legDurations[i] <= 0)
                return false; // Zero-duration legs are invalid

            if (legDurations[i] < minDuration)
                minDuration = legDurations[i];
            if (legDurations[i] > maxDuration)
                maxDuration = legDurations[i];
        }

        if (minDuration <= 0)
            return false;

        // Prevent very short legs (like a 1-candle spike) from creating artificially high ratios.
        // Assume anything under 3 bars is "just a quick spike" and cap the min denominator.
        double effectiveMin = Math.Max(3.0, minDuration);
        
        double ratio = (double)maxDuration / effectiveMin;
        return ratio <= maxRatio;
    }

    /// <summary>
    /// Computes the Average True Range (ATR) over a specific range of candles.
    /// Uses classic Wilder ATR calculation: max(H-L, |H-Cprev|, |L-Cprev|).
    /// </summary>
    internal static double ComputeLocalATR(IReadOnlyList<CandleData> candles, int startIndex, int endIndex)
    {
        if (startIndex > endIndex)
            return 0;

        double totalTR = 0;
        int validCount = 0;

        for (int i = startIndex; i <= endIndex; i++)
        {
            double high = (double)candles[i].High;
            double low = (double)candles[i].Low;
            double tr = high - low;

            if (i > 0)
            {
                double prevClose = (double)candles[i - 1].Close;
                double hl = high - low;
                double hc = Math.Abs(high - prevClose);
                double lc = Math.Abs(low - prevClose);
                tr = Math.Max(hl, Math.Max(hc, lc));
            }

            totalTR += tr;
            validCount++;
        }

        return validCount > 0 ? totalTR / validCount : 0;
    }
}
