using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides static methods to aggregate daily OHLCV candle data
/// into higher timeframes (weekly, monthly).
/// </summary>
public static class TimeFrameAggregator
{
    /// <summary>
    /// The last calendar day of the period a bar belongs to: the Sunday of its ISO week (W1), the last day of its month (MN1), or the bar's own date
    /// (any other frame). <paramref name="barTimestamp"/> is the bar's timestamp, which for W1 and MN1 is the first trading day of the period.
    /// A period is complete once the data (or an as-of date) reaches this day.
    /// </summary>
    public static DateTime PeriodEnd(DateTime barTimestamp, Models.TimeFrame timeFrame)
    {
        DateTime date = barTimestamp.Date;
        return timeFrame switch
        {
            Models.TimeFrame.W1 => SundayOfIsoWeek(date),
            Models.TimeFrame.MN1 => new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month), 0, 0, 0, date.Kind),
            _ => date,
        };
    }

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

    // The one definition of a week: the grouping key and the period end below both come from the ISO 8601 week (Monday start) of the date.
    private static (int Year, int Week) IsoWeekOf(DateTime date) => (ISOWeek.GetYear(date), ISOWeek.GetWeekOfYear(date));

    private static int GetIsoWeekKey(DateTime date)
    {
        var (year, week) = IsoWeekOf(date);
        return year * 100 + week;
    }

    private static DateTime SundayOfIsoWeek(DateTime date)
    {
        var (year, week) = IsoWeekOf(date);
        return DateTime.SpecifyKind(ISOWeek.ToDateTime(year, week, DayOfWeek.Sunday), date.Kind);
    }
}
