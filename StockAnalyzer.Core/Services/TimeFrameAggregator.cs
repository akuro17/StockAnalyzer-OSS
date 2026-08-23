using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides static methods to aggregate daily OHLCV candle data
/// into higher timeframes (weekly, monthly).
/// </summary>
public static class TimeFrameAggregator
{
    /// <summary>
    /// Aggregates daily candle data into the specified target timeframe.
    /// Returns the input unchanged if target is Daily or lower.
    /// </summary>
    /// <param name="dailyCandles">Source daily candle data, must be sorted by Timestamp ascending.</param>
    /// <param name="targetTimeFrame">Target timeframe to aggregate into.</param>
    /// <returns>Aggregated candle data for the target timeframe.</returns>
    public static IReadOnlyList<Models.CandleData> Aggregate(
        IReadOnlyList<Models.CandleData> dailyCandles,
        Models.TimeFrame targetTimeFrame)
    {
        if (dailyCandles == null || dailyCandles.Count == 0)
        {
            return Array.Empty<Models.CandleData>();
        }

        return targetTimeFrame switch
        {
            Models.TimeFrame.W1 => AggregateDailyToWeekly(dailyCandles),
            Models.TimeFrame.MN1 => AggregateDailyToMonthly(dailyCandles),
            _ => dailyCandles // D1 or lower: return as-is
        };
    }

    /// <summary>
    /// Aggregates daily candles into weekly candles (ISO 8601 week, Monday start).
    /// </summary>
    public static IReadOnlyList<Models.CandleData> AggregateDailyToWeekly(
        IReadOnlyList<Models.CandleData> dailyCandles)
    {
        if (dailyCandles == null || dailyCandles.Count == 0)
        {
            return Array.Empty<Models.CandleData>();
        }

        return dailyCandles
            .GroupBy(c => GetIsoWeekKey(c.Timestamp))
            .OrderBy(g => g.Key)
            .Select(AggregateGroup)
            .ToList();
    }

    /// <summary>
    /// Aggregates daily candles into monthly candles.
    /// </summary>
    public static IReadOnlyList<Models.CandleData> AggregateDailyToMonthly(
        IReadOnlyList<Models.CandleData> dailyCandles)
    {
        if (dailyCandles == null || dailyCandles.Count == 0)
        {
            return Array.Empty<Models.CandleData>();
        }

        return dailyCandles
            .GroupBy(c => new { c.Timestamp.Year, c.Timestamp.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(AggregateGroup)
            .ToList();
    }

    /// <summary>
    /// Aggregates a group of candles into a single candle.
    /// Open = first candle's Open, Close = last candle's Close,
    /// High = max High, Low = min Low, Volume = sum of Volumes.
    /// Timestamp = first candle's Timestamp.
    /// </summary>
    private static Models.CandleData AggregateGroup<TKey>(IGrouping<TKey, Models.CandleData> group)
    {
        var candles = group.OrderBy(c => c.Timestamp).ToList();
        return new Models.CandleData(
            Timestamp: candles[0].Timestamp,
            Open: candles[0].Open,
            High: candles.Max(c => c.High),
            Low: candles.Min(c => c.Low),
            Close: candles[^1].Close,
            Volume: candles.Sum(c => c.Volume)
        );
    }

    /// <summary>
    /// Gets an ISO 8601 week key for grouping (year * 100 + week number).
    /// </summary>
    private static int GetIsoWeekKey(DateTime date)
    {
        var cal = CultureInfo.InvariantCulture.Calendar;
        int week = cal.GetWeekOfYear(date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);

        // Handle year boundary: if the week is 52/53 but the date is in January,
        // it belongs to the previous year's last week.
        int year = date.Year;
        if (week >= 52 && date.Month == 1)
        {
            year--;
        }
        else if (week == 1 && date.Month == 12)
        {
            year++;
        }

        return year * 100 + week;
    }
}
