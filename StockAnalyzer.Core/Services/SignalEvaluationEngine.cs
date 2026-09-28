using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services;

public static class SignalEvaluationEngine
{
    public static bool EvaluateEntry(ScreenerIndicatorEntry entry, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
    {
        if (entry == null || !entry.IsEnabled || candles == null || candles.Count == 0) return false;
        return entry.IsMet(candles, metadata);
    }

    public static bool EvaluateBundle(BundledSignalCondition bundle, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
    {
        if (bundle == null || bundle.Conditions.Count == 0) return false;

        foreach (var entry in bundle.Conditions)
        {
            if (entry.IsEnabled && !EvaluateEntry(entry, candles, metadata))
            {
                return false;
            }
        }
        return true;
    }
}
