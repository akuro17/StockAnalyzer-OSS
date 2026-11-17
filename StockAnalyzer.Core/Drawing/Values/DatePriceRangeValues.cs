using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Headless calculation of Date/Price Range metrics between two chart anchors over candle data.
/// </summary>
public static class DatePriceRangeValues
{
    // Positions in the array returned by Calculate. This is the single definition of the layout: the object that shows the values and the tests read
    // these constants instead of repeating the numbers (the first six keep their original positions; 6..9 were appended later).

    /// <summary>Number of values the first six positions hold (price, bars, volume); the later values start after them.</summary>
    public const int BaseValueCount = 6;

    /// <summary>Position of <c>HighestHigh</c>.</summary>
    public const int HighestHighIndex = 6;

    /// <summary>Position of <c>LowestLow</c>.</summary>
    public const int LowestLowIndex = 7;

    /// <summary>Position of <c>MaxDrawdown</c>.</summary>
    public const int MaxDrawdownIndex = 8;

    /// <summary>Position of <c>Volatility</c>.</summary>
    public const int VolatilityIndex = 9;

    /// <summary>Length of the array returned by Calculate.</summary>
    public const int ValueCount = 10;

    /// <summary>Text of a price value (HighestHigh, LowestLow): three decimals.</summary>
    private const string PriceFormat = "F3";

    /// <summary>Text of a percentage (MaxDrawdown, Volatility): two decimals, then the percent sign.</summary>
    private const string PercentFormat = "F2";

    /// <summary>Text of a value that is not defined.</summary>
    private const string NoValueText = "-";

    public static DrawingCalculatedValue[] Calculate(
        DrawingAnchor p1,
        DrawingAnchor p2,
        IReadOnlyList<CoreCandleData>? candles,
        IndicatorColor color)
    {
        var priceDelta = p2.Price - p1.Price;
        decimal? priceChangePercent = p1.Price <= 0m ? null : (priceDelta / p1.Price) * 100m;

        var tMin = p1.Time <= p2.Time ? p1.Time : p2.Time;
        var tMax = p1.Time <= p2.Time ? p2.Time : p1.Time;
        var calendarDays = (tMax.Date - tMin.Date).Days;

        var (barCount, totalVolume) = AggregateRange(candles, tMin, tMax);
        decimal? averageVolume = (barCount == 0 || !totalVolume.HasValue)
            ? null
            : (decimal)totalVolume.Value / barCount;

        // Extreme prices and close-based risk statistics: their own pass, so they stay defined when the volume sum overflows.
        RangeRiskStats? risk = null;
        if (candles != null && TryGetRangeIndices(candles, tMin, tMax, out int firstIndex, out int lastIndex))
        {
            risk = RangeRiskMath.Compute(candles, firstIndex, lastIndex);
        }

        decimal? highestHigh = risk?.HighestHigh;
        decimal? lowestLow = risk?.LowestLow;
        decimal? maxDrawdown = risk?.MaxDrawdownPercent;
        decimal? volatility = risk?.VolatilityPercent;

        return new[]
        {
            new DrawingCalculatedValue(
                "PriceDelta",
                "Price Delta",
                priceDelta,
                FormatSignedTwoDecimals(priceDelta),
                color),
            new DrawingCalculatedValue(
                "PriceChangePercent",
                "Price Change %",
                priceChangePercent,
                priceChangePercent.HasValue
                    ? $"{FormatSignedTwoDecimals(priceChangePercent.Value)}%"
                    : NoValueText,
                color),
            new DrawingCalculatedValue(
                "BarCount",
                "Bar Count",
                barCount,
                barCount.ToString(DrawingValueFormat.Culture),
                color),
            new DrawingCalculatedValue(
                "CalendarDays",
                "Calendar Days",
                calendarDays,
                calendarDays.ToString(DrawingValueFormat.Culture),
                color),
            new DrawingCalculatedValue(
                "TotalVolume",
                "Total Volume",
                totalVolume,
                totalVolume.HasValue
                    ? totalVolume.Value.ToString("N0", DrawingValueFormat.Culture)
                    : NoValueText,
                color),
            new DrawingCalculatedValue(
                "AverageVolume",
                "Average Volume",
                averageVolume,
                averageVolume.HasValue
                    ? averageVolume.Value.ToString("N0", DrawingValueFormat.Culture)
                    : NoValueText,
                color),
            new DrawingCalculatedValue(
                "HighestHigh",
                "Highest High",
                highestHigh,
                FormatOrDash(highestHigh, PriceFormat),
                color),
            new DrawingCalculatedValue(
                "LowestLow",
                "Lowest Low",
                lowestLow,
                FormatOrDash(lowestLow, PriceFormat),
                color),
            new DrawingCalculatedValue(
                "MaxDrawdown",
                "Max Drawdown %",
                maxDrawdown,
                FormatPercentOrDash(maxDrawdown),
                color),
            new DrawingCalculatedValue(
                "Volatility",
                "Volatility %/bar",
                volatility,
                FormatPercentOrDash(volatility),
                color)
        };
    }

    private static string FormatOrDash(decimal? value, string format)
        => value.HasValue ? value.Value.ToString(format, DrawingValueFormat.Culture) : NoValueText;

    private static string FormatPercentOrDash(decimal? value)
        => value.HasValue ? value.Value.ToString(PercentFormat, DrawingValueFormat.Culture) + "%" : NoValueText;

    private static string FormatSignedTwoDecimals(decimal value)
        => value.ToString("+0.00;-0.00;0.00", DrawingValueFormat.Culture);

    private static (int barCount, long? totalVolume) AggregateRange(
        IReadOnlyList<CoreCandleData>? candles,
        DateTime tMin,
        DateTime tMax)
    {
        if (candles == null || !TryGetRangeIndices(candles, tMin, tMax, out int lowerIdx, out int upperIdx))
        {
            return (0, 0L);
        }

        int barCount = upperIdx - lowerIdx + 1;
        long sum = 0L;
        try
        {
            checked
            {
                for (int i = lowerIdx; i <= upperIdx; i++)
                {
                    sum += candles[i].Volume;
                }
            }

            return (barCount, sum);
        }
        catch (OverflowException)
        {
            return (barCount, null);
        }
    }

    /// <summary>The first and last index of the bars whose <c>Timestamp</c> lies in [tMin, tMax]; false when no bar does.</summary>
    private static bool TryGetRangeIndices(
        IReadOnlyList<CoreCandleData> candles,
        DateTime tMin,
        DateTime tMax,
        out int lowerIdx,
        out int upperIdx)
    {
        lowerIdx = 0;
        upperIdx = -1;
        if (candles.Count == 0)
        {
            return false;
        }

        lowerIdx = FindLowerBound(candles, tMin);
        if (lowerIdx >= candles.Count || candles[lowerIdx].Timestamp > tMax)
        {
            return false;
        }

        upperIdx = FindUpperBound(candles, lowerIdx, tMax);
        return upperIdx >= lowerIdx;
    }

    private static int FindLowerBound(IReadOnlyList<CoreCandleData> candles, DateTime tMin)
    {
        int lo = 0;
        int hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp < tMin)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static int FindUpperBound(IReadOnlyList<CoreCandleData> candles, int startIndex, DateTime tMax)
    {
        int lo = startIndex;
        int hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp <= tMax)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo - 1;
    }
}
