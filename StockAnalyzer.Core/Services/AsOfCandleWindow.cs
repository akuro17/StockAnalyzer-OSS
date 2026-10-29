using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The one definition of "the bars known on a date": the DAILY series is cut at the date first and a weekly or monthly bar is aggregated afterwards,
/// so a period that straddles the date holds only the days up to it (it is dated by its first day and never includes a later day).
/// </summary>
internal static class AsOfCandleWindow
{
    /// <summary>
    /// The bars of <paramref name="timeFrame"/> built from the days of <paramref name="daily"/> dated on or before <paramref name="endDate"/> (chronological), the latest
    /// <paramref name="count"/> of them (all of them when <paramref name="count"/> is not positive). Empty when no day is on or before the date.
    /// </summary>
    public static IReadOnlyList<CandleData> Build(IReadOnlyList<CandleData> daily, DateOnly endDate, TimeFrame timeFrame, int count)
    {
        // A series the date cuts nothing from is read as it is (the usual scan: the as-of date is today or the latest stored day); only a cut makes a copy.
        IReadOnlyList<CandleData> dailyUpToDate = IsDatedOnOrBefore(daily, endDate)
            ? daily
            : daily.Where(bar => DateOnly.FromDateTime(bar.Timestamp) <= endDate).ToList();
        IReadOnlyList<CandleData> upToDate = TimeFrameAggregator.Aggregate(dailyUpToDate, timeFrame);
        return count > 0 && upToDate.Count > count ? upToDate.Skip(upToDate.Count - count).ToList() : upToDate;
    }

    private static bool IsDatedOnOrBefore(IReadOnlyList<CandleData> bars, DateOnly endDate)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            if (DateOnly.FromDateTime(bars[i].Timestamp) > endDate)
            {
                return false;
            }
        }

        return true;
    }
}
