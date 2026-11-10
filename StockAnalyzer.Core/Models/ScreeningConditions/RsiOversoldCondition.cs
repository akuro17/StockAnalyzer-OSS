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

    public int Period => _period;
    public decimal Threshold => _threshold;

    public RsiOversoldCondition(int period = ChartConstants.DefaultRsiPeriod, decimal threshold = ChartConstants.DefaultRsiOversoldThreshold)
    {
        _period = Math.Clamp(period, 2, 250);
        _threshold = Math.Clamp(threshold, 0.0m, 100.0m);
    }

    public override string ToString()
    {
        return $"RSI({_period}) < {_threshold}";
    }

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < _period + 1) return false;

        // Calculate simple RSI for screening using algebraic single-division SSoT
        decimal gains = 0m;
        decimal losses = 0m;

        int startIndex = candles.Count - _period;
        for (int i = startIndex; i < candles.Count; i++)
        {
            decimal diff = candles[i].Close - candles[i - 1].Close;
            if (diff > 0) gains += diff;
            else losses += Math.Abs(diff);
        }

        decimal sum = gains + losses;
        if (sum <= 1e-12m) return 50.0m < _threshold;
        decimal rsi = (100.0m * gains) / sum;

        return rsi < _threshold;
    }
}
