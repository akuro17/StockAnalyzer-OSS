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
    public static bool Matches(IReadOnlyList<CandleData> acceptedChartBars, IReadOnlyList<CandleData> sourceBars)
    {
        var byTime = sourceBars.ToDictionary(c => PredictionLogService.Utc(c.Timestamp));
        return !acceptedChartBars.Any(c => !byTime.TryGetValue(PredictionLogService.Utc(c.Timestamp), out var source)
            || c.Open != source.Open || c.High != source.High || c.Low != source.Low || c.Close != source.Close || c.Volume != source.Volume);
    }
}
