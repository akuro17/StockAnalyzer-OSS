using System.Collections.Generic;
using StockAnalyzer.Core.Models.GeometricPattern;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition based on geometric charting formations.
/// Filters stocks by their latest detected macro-level trend formation.
/// </summary>
public class GeometricPatternCondition : IScreeningCondition
{
    private readonly GeometricFormationType _targetType;
    private readonly decimal _zigzagThreshold;

    /// <summary>
    /// Creates a new geometric pattern screening condition.
    /// </summary>
    /// <param name="targetType">The geometric formation type to screen for (e.g., AscendingChannel, BullishFlag).</param>
    /// <param name="zigzagThreshold">
    /// The minimum percentage swing to constitute a pivot point (default: 5.0%).
    /// Lower values detect more minor swings, higher values detect major macro formations.
    /// </param>
    public GeometricPatternCondition(GeometricFormationType targetType, decimal zigzagThreshold = ChartConstants.DefaultGeometricZigZagThreshold)
    {
        _targetType = targetType;
        _zigzagThreshold = zigzagThreshold;
    }

    public override string ToString()
    {
        return $"Geometric Pattern ({_targetType})";
    }

    /// <summary>
    /// Checks if the latest detected geometric formation matches the target type.
    /// Uses the pure C# <see cref="GeometricPatternDetector"/> (no Python dependency).
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < ChartConstants.GeometricMinPivotCount * 2) 
            return false;

        var latest = GeometricPatternDetector.DetectLatest(candles, _zigzagThreshold);
        if (latest == null) 
            return false;

        return latest.Type == _targetType;
    }
}
