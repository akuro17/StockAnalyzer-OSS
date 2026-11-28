using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks if Wilder RSI is below a specified oversold threshold.
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

        var closes = new decimal?[candles.Count];
        for (int i = 0; i < candles.Count; i++)
        {
            closes[i] = candles[i].Close;
        }

        var rsiIndicator = new CoreRsiIndicator
        {
            Period = _period,
            PriceSource = PriceType.Close
        };

        IIndicatorResult result = rsiIndicator.CalculateSeries(closes);
        decimal? latestRsi = result.IsSuccessful && result.MainValues.Count > 0
            ? result.MainValues[^1]
            : null;

        return latestRsi.HasValue && latestRsi.Value < _threshold;
    }
}
