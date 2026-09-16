namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Represents a single point on the Reverse Watch Curve.
/// </summary>
public sealed class ReverseWatchCurvePoint
{
    public required DateTime Date { get; init; }
    public required decimal PriceAverage { get; init; }
    public required decimal VolumeAverage { get; init; }
    // Raw OHLCV data for display
    public required decimal Open { get; init; }
    public required decimal High { get; init; }
    public required decimal Low { get; init; }
    public required decimal Close { get; init; }
    public required decimal Volume { get; init; }

    public required int Index { get; init; }
    public ReverseWatchPhase Phase { get; set; } = ReverseWatchPhase.None;
}

/// <summary>
/// Defines the scaling boundaries for the chart.
/// Uses decimal for precision (Constraint C001).
/// </summary>
public sealed class ReverseWatchCurveBounds
{
    public required decimal MinPrice { get; init; }
    public required decimal MaxPrice { get; init; }
    public required decimal MinVolume { get; init; }
    public required decimal MaxVolume { get; init; }
}

/// <summary>
/// Container for the complete analysis result.
/// </summary>
public sealed class ReverseWatchCurveData
{
    public required List<ReverseWatchCurvePoint> Points { get; init; }
    public required ReverseWatchCurveBounds Bounds { get; init; }
    public required int Period { get; init; }
    public required string StockCode { get; init; }
    
    // Debug Info for Visualization
    public double ScalingFactor { get; init; } = 1.0;
    public double LastAngleDegrees { get; init; }
    public double LastNormalizedVolPct { get; init; }
    public double LastPricePct { get; init; }
}
