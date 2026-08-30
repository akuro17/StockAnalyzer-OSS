namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents a trendline candidate formed by connecting two fractal pivots of the same type.
/// </summary>
public readonly record struct TrendlineCandidate
{
    public FractalPivot StartPoint { get; init; }
    public FractalPivot EndPoint { get; init; }
    public FractalPivotType Type { get; init; }
}
