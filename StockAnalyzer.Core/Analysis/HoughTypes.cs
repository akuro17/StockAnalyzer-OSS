using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Mode of coordinate normalization applied before Hough transform voting.
/// </summary>
public enum HoughNormalizationMode
{
    Raw,
    Relative,
    Log,
    MinMax,
    ZScore
}

/// <summary>
/// Structural classification of a detected line.
/// </summary>
public enum HoughLineType
{
    Neutral,
    Support,
    Resistance,
    TrendUp,
    TrendDown
}

/// <summary>
/// A candidate 2D point fed into the Hough Transform engine.
/// </summary>
public readonly record struct HoughPoint(
    int BarIndex,
    decimal Price,
    double Weight = 1.0
);

/// <summary>
/// Represents a line detected by Hough Transform in price chart space.
/// </summary>
public readonly record struct HoughDetectedLine(
    double Rho,
    double Theta,
    int Votes,
    double Slope,
    double NormalizedSlope,
    int StartBar,
    int EndBar,
    decimal StartPrice,
    decimal EndPrice,
    double Strength,
    int TouchCount,
    HoughLineType LineType,
    bool IsVertical = false,
    int Span = 0,
    double RSquared = 1.0
)
{
    /// <summary>
    /// Calculates the estimated price on this line at a given bar index.
    /// </summary>
    public decimal GetPriceAt(int barIndex)
    {
        if (IsVertical) return StartPrice;
        double barDiff = barIndex - StartBar;
        return StartPrice + (decimal)(Slope * barDiff);
    }
}

/// <summary>
/// Represents a parallel channel detected from a pair of Hough lines.
/// </summary>
public readonly record struct HoughChannel(
    HoughDetectedLine UpperLine,
    HoughDetectedLine LowerLine,
    decimal Width,
    double Slope,
    double RelativePosition
);

/// <summary>
/// The result container returned by HoughTransformEngine.
/// </summary>
public record HoughTransformResult(
    IReadOnlyList<HoughDetectedLine> Lines,
    IReadOnlyList<HoughChannel> Channels,
    int TotalCandidatePoints,
    int AccumulatorRows,
    int AccumulatorCols
)
{
    public static readonly HoughTransformResult Empty = new(
        Array.Empty<HoughDetectedLine>(),
        Array.Empty<HoughChannel>(),
        0, 0, 0);
}
