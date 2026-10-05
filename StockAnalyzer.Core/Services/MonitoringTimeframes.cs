using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>The one definition of the timeframes prediction monitoring supports, legacy and producer-attested (daily, weekly, monthly bars).</summary>
internal static class MonitoringTimeframes
{
    public static bool IsSupported(TimeframeType timeframe) =>
        timeframe is TimeframeType.Daily or TimeframeType.Weekly or TimeframeType.Monthly;

    /// <summary>The monitored timeframe of a chart timeframe, or false for any other (intraday) chart timeframe.</summary>
    public static bool TryFrom(TimeFrame frame, out TimeframeType timeframe)
    {
        switch (frame)
        {
            case TimeFrame.D1: timeframe = TimeframeType.Daily; return true;
            case TimeFrame.W1: timeframe = TimeframeType.Weekly; return true;
            case TimeFrame.MN1: timeframe = TimeframeType.Monthly; return true;
            default: timeframe = default; return false;
        }
    }
}
