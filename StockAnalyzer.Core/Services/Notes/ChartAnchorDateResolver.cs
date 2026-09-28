using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Resolves a Note's saved <c>ChartAnchorDate</c>/<c>ChartTimeframe</c> (spec section 6.2) to a
/// concrete index within a candle series, so the chart can jump to the date the user was
/// analyzing when they wrote the Note. Pure function only - it does not touch
/// <c>ChartViewModel</c> or any rendering state; wiring the result into
/// <c>VisibleStartIndex</c> is left to the caller (Step 90-1-17).
/// </summary>
public static class ChartAnchorDateResolver
{
    /// <summary>
    /// Finds the index in <paramref name="candles"/> (must be sorted ascending by Timestamp) that
    /// best represents <paramref name="anchorDate"/> under <paramref name="timeframe"/>, following
    /// spec section 6.2's rule.
    /// </summary>
    /// <remarks>
    /// For Daily, "rounding to the first business day of the period" is simply the date itself, so
    /// this reduces to an exact-date search. For Weekly/Monthly, each candle's Timestamp is already
    /// the first actual trading day of its aggregation period (see
    /// <see cref="TimeFrameAggregator.AggregateDailyToWeekly"/>), so "round anchorDate to the first
    /// business day of its containing week/month" is equivalent to finding whichever candle's
    /// period (ISO week or calendar month) contains <paramref name="anchorDate"/> - this correctly
    /// lands on a holiday-shifted first trading day without needing raw daily data. Only when no
    /// candle's period contains <paramref name="anchorDate"/> at all (e.g. before the ticker was
    /// listed, or beyond the loaded range) does this fall back to the nearest earlier candle by raw
    /// date, then the nearest later one. Returns null only when <paramref name="candles"/> is empty.
    /// </remarks>
    public static int? Resolve(IReadOnlyList<CandleData> candles, DateTime anchorDate, TimeFrame timeframe)
    {
        if (candles.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < candles.Count; i++)
        {
            if (IsWithinPeriod(candles[i].Timestamp, anchorDate, timeframe))
            {
                return i;
            }
        }

        for (var i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].Timestamp.Date < anchorDate.Date)
            {
                return i;
            }
        }

        for (var i = 0; i < candles.Count; i++)
        {
            if (candles[i].Timestamp.Date > anchorDate.Date)
            {
                return i;
            }
        }

        // Unreachable when candles.Count > 0: every candle is either within-period, earlier, or
        // later. Kept only as a defensive fallback against future changes to this method.
        return null;
    }

    private static bool IsWithinPeriod(DateTime candleTimestamp, DateTime anchorDate, TimeFrame timeframe) => timeframe switch
    {
        TimeFrame.W1 => StartOfIsoWeek(candleTimestamp.Date) == StartOfIsoWeek(anchorDate.Date),
        TimeFrame.MN1 => candleTimestamp.Year == anchorDate.Year && candleTimestamp.Month == anchorDate.Month,
        _ => candleTimestamp.Date == anchorDate.Date,
    };

    /// <summary>
    /// Monday of the ISO 8601 week containing <paramref name="date"/>, matching how
    /// <see cref="TimeFrameAggregator.AggregateDailyToWeekly"/> groups trading days into weekly candles.
    /// </summary>
    private static DateTime StartOfIsoWeek(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
