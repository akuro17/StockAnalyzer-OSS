using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.MarketStructure;

/// <summary>
/// Detects market structure shifts (BOS/CHoCH) from candle data using pivot point analysis.
/// BOS (Break of Structure) indicates trend continuation.
/// CHoCH (Change of Character) indicates potential trend reversal.
/// </summary>
public static class MarketStructureDetector
{
    /// <summary>
    /// Minimum number of candles needed for meaningful structure analysis.
    /// </summary>
    public const int MinimumCandleCount = 5;

    /// <summary>
    /// Detects all market structure shifts in the given candle data.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="zigzagThresholdPercent">
    /// The minimum percentage change required to register a new pivot point.
    /// Higher values produce fewer, more significant pivots.
    /// </param>
    /// <returns>A list of detected structure shifts, ordered by index.</returns>
    public static IReadOnlyList<MarketStructureShift> Detect(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThresholdPercent = 5.0m)
    {
        if (candles == null || candles.Count < MinimumCandleCount)
        {
            return Array.Empty<MarketStructureShift>();
        }

        // Step 1: Extract pivot points using simplified ZigZag
        var pivots = ExtractPivots(candles, zigzagThresholdPercent);

        if (pivots.Count < 4)
        {
            return Array.Empty<MarketStructureShift>();
        }

        // Step 2: Analyze pivot sequences for BOS/CHoCH
        return AnalyzePivotSequence(pivots, candles);
    }

    /// <summary>
    /// Gets the most recent structure shift from the candle data.
    /// Useful for screening where only the latest state matters.
    /// </summary>
    public static MarketStructureShift? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThresholdPercent = 5.0m)
    {
        var shifts = Detect(candles, zigzagThresholdPercent);
        return shifts.Count > 0 ? shifts[shifts.Count - 1] : null;
    }

    /// <summary>
    /// Extracts pivot points (swing highs and swing lows) using a ZigZag-like algorithm.
    /// </summary>
    public static IReadOnlyList<PivotPoint> ExtractPivots(
        IReadOnlyList<CandleData> candles,
        decimal thresholdPercent)
    {
        if (candles.Count < 3)
        {
            return Array.Empty<PivotPoint>();
        }

        var pivots = new List<PivotPoint>();
        decimal threshold = thresholdPercent / 100m;

        // Initialize with the first candle
        decimal lastHigh = candles[0].High;
        decimal lastLow = candles[0].Low;
        int lastHighIdx = 0;
        int lastLowIdx = 0;

        // initial direction: unknown (0), up (1), down (-1)
        int direction = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            decimal high = candles[i].High;
            decimal low = candles[i].Low;

            if (direction >= 0) // currently in upswing or unknown
            {
                if (high > lastHigh)
                {
                    lastHigh = high;
                    lastHighIdx = i;
                }

                // Check for reversal down
                if (lastHigh > 0 && (lastHigh - low) / lastHigh >= threshold)
                {
                    if (direction != 0 || pivots.Count == 0)
                    {
                        pivots.Add(new PivotPoint(lastHighIdx, candles[lastHighIdx].Time, lastHigh, isHigh: true));
                    }
                    direction = -1;
                    lastLow = low;
                    lastLowIdx = i;
                    continue; // Skip checking for reversal up on the same candle
                }
            }

            if (direction <= 0) // currently in downswing or unknown
            {
                if (low < lastLow)
                {
                    lastLow = low;
                    lastLowIdx = i;
                }

                // Check for reversal up
                if (lastLow > 0 && (high - lastLow) / lastLow >= threshold)
                {
                    if (direction != 0 || pivots.Count == 0)
                    {
                        pivots.Add(new PivotPoint(lastLowIdx, candles[lastLowIdx].Time, lastLow, isHigh: false));
                    }
                    direction = 1;
                    lastHigh = high;
                    lastHighIdx = i;
                }
            }
        }

        // Add the last unconfirmed pivot
        if (pivots.Count > 0)
        {
            var lastPivot = pivots[pivots.Count - 1];
            if (lastPivot.IsHigh && lastLowIdx > lastPivot.Index)
            {
                pivots.Add(new PivotPoint(lastLowIdx, candles[lastLowIdx].Time, lastLow, isHigh: false));
            }
            else if (!lastPivot.IsHigh && lastHighIdx > lastPivot.Index)
            {
                pivots.Add(new PivotPoint(lastHighIdx, candles[lastHighIdx].Time, lastHigh, isHigh: true));
            }
        }

        return pivots;
    }

    /// <summary>
    /// Analyzes a sequence of pivot points to detect BOS and CHoCH patterns.
    /// </summary>
    public static IReadOnlyList<MarketStructureShift> AnalyzePivotSequence(
        IReadOnlyList<PivotPoint> pivots,
        IReadOnlyList<CandleData> candles)
    {
        var shifts = new List<MarketStructureShift>();

        // We need at least 4 pivots to compare two consecutive highs and two consecutive lows.
        // Track the trend state based on previous pivot comparisons.
        // TrendState: 0 = unknown, 1 = bullish structure, -1 = bearish structure
        int trendState = 0;

        // Collect separate high and low pivots for comparison
        var highs = new List<PivotPoint>();
        var lows = new List<PivotPoint>();

        foreach (var pivot in pivots)
        {
            if (pivot.IsHigh)
                highs.Add(pivot);
            else
                lows.Add(pivot);
        }

        // We need at least 2 highs and 2 lows to make any comparison
        if (highs.Count < 2 || lows.Count < 2)
        {
            return shifts;
        }

        // Determine initial trend from first two pairs
        bool initialHigherHighs = highs[1].Price > highs[0].Price;
        bool initialHigherLows = lows[1].Price > lows[0].Price;

        if (initialHigherHighs && initialHigherLows)
            trendState = 1; // Bullish
        else if (!initialHigherHighs && !initialHigherLows)
            trendState = -1; // Bearish
        else
            trendState = initialHigherHighs ? 1 : -1; // Fallback to Highs direction if mixed

        // Analyze subsequent pivot pairs
        int highIdx = 2;
        int lowIdx = 2;

        while (highIdx < highs.Count || lowIdx < lows.Count)
        {
            // Determine which pivot comes next chronologically
            bool processHigh = false;
            if (highIdx < highs.Count && lowIdx < lows.Count)
            {
                processHigh = highs[highIdx].Index <= lows[lowIdx].Index;
            }
            else if (highIdx < highs.Count)
            {
                processHigh = true;
            }

            if (processHigh && highIdx < highs.Count)
            {
                var currentHigh = highs[highIdx];
                var previousHigh = highs[highIdx - 1];
                bool higherHigh = currentHigh.Price > previousHigh.Price;

                // Find the most recent low before this high for context
                var recentLow = lows.Count > 0 ? lows[Math.Min(lowIdx, lows.Count) - 1] : new PivotPoint(0, System.DateTime.MinValue, 0, false);

                if (trendState == 1 && higherHigh)
                {
                    // Uptrend continues: higher high in bullish structure = BOS
                    shifts.Add(new MarketStructureShift(
                        MarketStructureType.BullishBOS,
                        currentHigh.Index,
                        currentHigh.Time,
                        currentHigh.Price,
                        previousHigh.Price,
                        previousHigh.Index,
                        previousHigh.Time,
                        recentLow.Price,
                        recentLow.Index,
                        recentLow.Time));
                }
                else if (trendState == -1 && higherHigh)
                {
                    // Previously bearish, now higher high = CHoCH (bullish reversal)
                    shifts.Add(new MarketStructureShift(
                        MarketStructureType.BullishCHoCH,
                        currentHigh.Index,
                        currentHigh.Time,
                        currentHigh.Price,
                        previousHigh.Price,
                        previousHigh.Index,
                        previousHigh.Time,
                        recentLow.Price,
                        recentLow.Index,
                        recentLow.Time));
                    trendState = 1;
                }

                highIdx++;
            }
            else if (lowIdx < lows.Count)
            {
                var currentLow = lows[lowIdx];
                var previousLow = lows[lowIdx - 1];
                bool lowerLow = currentLow.Price < previousLow.Price;

                // Find the most recent high before this low for context
                var recentHigh = highs.Count > 0 ? highs[Math.Min(highIdx, highs.Count) - 1] : new PivotPoint(0, System.DateTime.MinValue, 0, true);

                if (trendState == -1 && lowerLow)
                {
                    // Downtrend continues: lower low in bearish structure = BOS
                    shifts.Add(new MarketStructureShift(
                        MarketStructureType.BearishBOS,
                        currentLow.Index,
                        currentLow.Time,
                        currentLow.Price,
                        recentHigh.Price,
                        recentHigh.Index,
                        recentHigh.Time,
                        previousLow.Price,
                        previousLow.Index,
                        previousLow.Time));
                }
                else if (trendState == 1 && lowerLow)
                {
                    // Previously bullish, now lower low = CHoCH (bearish reversal)
                    shifts.Add(new MarketStructureShift(
                        MarketStructureType.BearishCHoCH,
                        currentLow.Index,
                        currentLow.Time,
                        currentLow.Price,
                        recentHigh.Price,
                        recentHigh.Index,
                        recentHigh.Time,
                        previousLow.Price,
                        previousLow.Index,
                        previousLow.Time));
                    trendState = -1;
                }

                lowIdx++;
            }
        }

        return shifts;
    }
}

/// <summary>
/// Represents a single pivot point (swing high or swing low) in a price series.
/// </summary>
public class PivotPoint
{
    /// <summary>The index in the candle array where this pivot occurs.</summary>
    public int Index { get; }

    /// <summary>The timestamp of the pivot.</summary>
    public System.DateTime Time { get; }

    /// <summary>The price level of the pivot (High for swing highs, Low for swing lows).</summary>
    public decimal Price { get; }

    /// <summary>True if this is a swing high, false if swing low.</summary>
    public bool IsHigh { get; }

    public PivotPoint(int index, System.DateTime time, decimal price, bool isHigh)
    {
        Index = index;
        Time = time;
        Price = price;
        IsHigh = isHigh;
    }

    public override string ToString()
        => $"{(IsHigh ? "H" : "L")}[{Index}]={Price}";
}
