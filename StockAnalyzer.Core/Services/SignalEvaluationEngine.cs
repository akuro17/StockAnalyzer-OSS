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

    public static bool EvaluateBundle(BundledSignalCondition? bundle, IReadOnlyList<CandleData>? candles, TickerMetadata metadata = default)
    {
        if (bundle is null || bundle.Conditions is null || bundle.Conditions.Count == 0 || candles is null || candles.Count == 0)
        {
            return false;
        }

        bool hasActiveCondition = false;
        for (int i = 0; i < bundle.Conditions.Count; i++)
        {
            ScreenerIndicatorEntry? entry = bundle.Conditions[i];
            if (entry is null || !entry.IsEnabled)
            {
                continue;
            }

            hasActiveCondition = true;
            if (!EvaluateEntry(entry, candles, metadata))
            {
                return false;
            }
        }

        return hasActiveCondition;
    }
}
