using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The single rule deciding whether an evidence snapshot describes the bars the chart accepted: every accepted bar must exist in
/// the source at the same UTC instant with identical OHLCV. A period snapshot of another timeframe (for example daily evidence for
/// locally aggregated weekly bars) therefore never attests these bars.
/// </summary>
public static class MonitoringAlignment
{
    public static bool Matches(
        IReadOnlyList<CandleData> acceptedChartBars,
        IReadOnlyList<CandleData> sourceBars,
        TimeFrame timeFrame = TimeFrame.D1)
    {
        var byTime = new Dictionary<DateTimeOffset, CandleData>(sourceBars.Count);
        foreach (var s in sourceBars)
        {
            if (!byTime.TryAdd(NormalizeKey(s.Timestamp, timeFrame), s))
                return false;
        }

        return !acceptedChartBars.Any(c => !byTime.TryGetValue(NormalizeKey(c.Timestamp, timeFrame), out var source)
            || c.Open != source.Open || c.High != source.High || c.Low != source.Low || c.Close != source.Close || c.Volume != source.Volume);
    }

    private static DateTimeOffset NormalizeKey(DateTime timestamp, TimeFrame timeFrame)
    {
        if (timeFrame is TimeFrame.W1 or TimeFrame.MN1 && timestamp.TimeOfDay == TimeSpan.Zero)
            return PredictionLogService.Utc(TimeFrameAggregator.PeriodStart(timestamp, timeFrame));
        return PredictionLogService.Utc(timestamp);
    }
}
