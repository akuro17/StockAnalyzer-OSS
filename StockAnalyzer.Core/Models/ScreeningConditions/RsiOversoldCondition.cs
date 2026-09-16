using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks if RSI is below a specified oversold threshold.
/// </summary>
public class RsiOversoldCondition : IScreeningCondition
{
    private readonly int _period;
    private readonly decimal _threshold;

    public RsiOversoldCondition(int period = ChartConstants.DefaultRsiPeriod, decimal threshold = ChartConstants.DefaultRsiOversoldThreshold)
    {
        _period = period;
        _threshold = threshold;
    }

    public override string ToString()
    {
        return $"RSI({_period}) < {_threshold}";
    }

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < _period + 1) return false;

        // Calculate simple RSI for screening
        decimal gains = 0m;
        decimal losses = 0m;

        int startIndex = candles.Count - _period;
        for (int i = startIndex; i < candles.Count; i++)
        {
            decimal diff = candles[i].Close - candles[i - 1].Close;
            if (diff > 0) gains += diff;
            else losses += Math.Abs(diff);
        }

        if (losses == 0m) return false;
        decimal rs = (gains / _period) / (losses / _period);
        decimal rsi = 100m - (100m / (1m + rs));

        return rsi < _threshold;
    }
}
