using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

public class RsiOversoldCondition : IScreeningCondition
{
    private readonly int _period;
    private readonly decimal _threshold;

    public RsiOversoldCondition(int period = ChartConstants.DefaultRsiPeriod, decimal threshold = ChartConstants.DefaultRsiOversoldThreshold)
    {
        _period = period;
        _threshold = threshold;
    }

    public override string ToString() => $"RSI({_period}) < {_threshold} (Oversold)";

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count <= _period) return false;

        // CoreRsiIndicator calculates RSI for entire series
        // For screening, we only care about the latest value?
        // Let's use CoreRsiIndicator logic but simplified or direct call?
        // Since CoreRsiIndicator is a class, we can instantiate it.
        
        var rsiIndicator = new CoreRsiIndicator();
        // CoreRsiIndicator needs params? It uses default if not specified or set via Parameters
        // But Parameters are usually set via UI.
        // Let's look at CoreRsiIndicator implementation to see how to pass parameters.
        // If it doesn't take params in constructor, we might need to rely on default or set properties.
        
        // Actually, CoreRsiIndicator implements ICoreIndicator.
        // Let's implement RSI calculation directly here for robustness and speed, 
        // to avoid dependency on Indicator instantiation overhead if possible,
        // OR use the core indicator if it's clean.
        // Given "Single Source of Truth", we SHOULD use CoreRsiIndicator.
        
        // However, CoreRsiIndicator might require specific setup.
        // Let's assume a simple calculation for now to decouple slightly for screening performance,
        // or just reference the logic.
        // Let's use the standard "Wilder's RSI" logic here for clarity in screening.
        
        return CalculateLastRsi(candles, _period) < _threshold;
    }

    private decimal CalculateLastRsi(IReadOnlyList<CandleData> candles, int period)
    {
        if (candles.Count <= period) return ChartConstants.RsiNeutralValue; // Neutral

        // Simple RSI implementation for screening
        // Need to calculate gain/loss for at least period + 1
        
        // Optimization: We only need the latest RSI.
        // But Wilder's smoothing requires history.
        // We need at least 2-3x period for accurate smoothing.
        
        int start = candles.Count - (period * 3);
        if (start < 0) start = 0; // Use all available if less than ideal history
        if (candles.Count - start <= period) return ChartConstants.RsiNeutralValue;

        decimal avgGain = 0;
        decimal avgLoss = 0;

        // First average (SMA)
        for (int i = start + 1; i <= start + period; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            if (change > 0) avgGain += change;
            else avgLoss -= change;
        }
        avgGain /= period;
        avgLoss /= period;

        // Subsequent smoothing (Wilder's)
        for (int i = start + period + 1; i < candles.Count; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0; // Absolute loss

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        if (avgLoss == 0) return ChartConstants.RsiMaxValue;
        decimal rs = avgGain / avgLoss;
        return ChartConstants.RsiMaxValue - (100m / (1m + rs));
    }
}
