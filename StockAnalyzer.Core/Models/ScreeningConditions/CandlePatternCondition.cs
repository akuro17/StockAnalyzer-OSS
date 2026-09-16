using System.Collections.Generic;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks if the latest candle matches a specific 
/// classical candlestick pattern (e.g., Bullish Marubozu, Doji).
/// </summary>
public class CandlePatternCondition : IScreeningCondition
{
    private readonly CandlePatternType _targetPattern;

    /// <summary>
    /// Creates a new candle pattern condition.
    /// </summary>
    /// <param name="targetPattern">
    /// The specific candlestick pattern to screen for.
    /// Note: 'None' will practically match candles that have no clear pattern.
    /// </param>
    public CandlePatternCondition(CandlePatternType targetPattern)
    {
        _targetPattern = targetPattern;
    }

    public override string ToString()
    {
        return $"Candle Pattern ({_targetPattern})";
    }

    /// <summary>
    /// Checks if the most recent candle in the provided data matches the target pattern.
    /// Uses <see cref="CandlePatternDetector"/> which considers local average volatility context.
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count == 0) return false;

        var detectedPattern = CandlePatternDetector.DetectPattern(candles);

        return detectedPattern == _targetPattern;
    }
}
