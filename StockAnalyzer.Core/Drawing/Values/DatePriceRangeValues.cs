using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Headless calculation of Date/Price Range metrics between two chart anchors over candle data.
/// </summary>
public static class DatePriceRangeValues
{
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
                    : "-",
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
                    : "-",
                color),
            new DrawingCalculatedValue(
                "AverageVolume",
                "Average Volume",
                averageVolume,
                averageVolume.HasValue
                    ? averageVolume.Value.ToString("N0", DrawingValueFormat.Culture)
                    : "-",
                color)
        };
    }

    private static string FormatSignedTwoDecimals(decimal value)
        => value.ToString("+0.00;-0.00;0.00", DrawingValueFormat.Culture);

    private static (int barCount, long? totalVolume) AggregateRange(
        IReadOnlyList<CoreCandleData>? candles,
        DateTime tMin,
        DateTime tMax)
    {
        if (candles == null || candles.Count == 0)
        {
            return (0, 0L);
        }

        int lowerIdx = FindLowerBound(candles, tMin);
        if (lowerIdx >= candles.Count || candles[lowerIdx].Timestamp > tMax)
        {
            return (0, 0L);
        }

        int upperIdx = FindUpperBound(candles, lowerIdx, tMax);
        if (upperIdx < lowerIdx)
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
