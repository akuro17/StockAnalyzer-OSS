using System.Collections.Generic;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition based on BOS/CHoCH market structure detection.
/// Filters stocks by their latest detected market structure shift type.
/// </summary>
public class MarketStructureCondition : IScreeningCondition
{
    private readonly MarketStructureType _targetType;
    private readonly decimal _zigzagThreshold;

    /// <summary>
    /// Creates a new market structure screening condition.
    /// </summary>
    /// <param name="targetType">
    /// The market structure type to screen for:
    /// <list type="bullet">
    /// <item><description><see cref="MarketStructureType.BullishBOS"/> — uptrend continuation (higher highs + higher lows)</description></item>
    /// <item><description><see cref="MarketStructureType.BearishBOS"/> — downtrend continuation (lower lows + lower highs)</description></item>
    /// <item><description><see cref="MarketStructureType.BullishCHoCH"/> — downtrend to uptrend reversal</description></item>
    /// <item><description><see cref="MarketStructureType.BearishCHoCH"/> — uptrend to downtrend reversal</description></item>
    /// </list>
    /// </param>
    /// <param name="zigzagThreshold">
    /// The minimum percentage swing to constitute a pivot point (default: 5.0%).
    /// Lower values detect more pivots (more sensitive), higher values only detect major swings.
    /// </param>
    public MarketStructureCondition(MarketStructureType targetType, decimal zigzagThreshold = 5.0m)
    {
        _targetType = targetType;
        _zigzagThreshold = zigzagThreshold;
    }

    public override string ToString()
    {
        return $"Market Structure ({_targetType})";
    }

    /// <summary>
    /// Checks if the latest detected market structure shift matches the target type.
    /// Uses the C# pure-logic <see cref="MarketStructureDetector"/> (no Python dependency).
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < 10) return false;

        var latest = MarketStructureDetector.DetectLatest(candles, _zigzagThreshold);
        if (latest == null) return false;

        return latest.Type == _targetType;
    }
}
