using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Directional curvature constraint for parabolic Hough transform detection.
/// </summary>
public enum ParabolicHoughCurvatureSign
{
    Both = 0,
    Convex = 1,  // a > 0 (U-shaped cup, bullish reversal / base)
    Concave = 2  // a < 0 (Inverted U-shaped dome, bubble top / peak)
}

/// <summary>
/// Represents a parabola detected by Parabolic Hough Transform in price chart space:
/// Price(x) = a * x^2 + b * x + c, where x is the relative bar index from StartBar.
/// </summary>
public readonly record struct HoughDetectedParabola(
    double NormA,
    double NormB,
    double NormC,
    double CurvaturePrice,
    double SlopePrice,
    decimal InterceptPrice,
    int StartBar,
    int EndBar,
    decimal StartPrice,
    decimal EndPrice,
    decimal VertexPrice,
    int VertexBar,
    int Votes,
    double RSquared,
    double Strength,
    ParabolicHoughCurvatureSign CurvatureSign
)
{
    /// <summary>
    /// Calculates the estimated price on this parabola at a given bar index.
    /// </summary>
    public decimal GetPriceAt(int barIndex)
    {
        double x = barIndex - StartBar;
        double priceD = CurvaturePrice * x * x + SlopePrice * x + (double)InterceptPrice;
        return (decimal)priceD;
    }
}

/// <summary>
/// The result container returned by ParabolicHoughTransformEngine.
/// </summary>
public record ParabolicHoughResult(
    IReadOnlyList<HoughDetectedParabola> Parabolas,
    int TotalCandidatePoints
)
{
    public static readonly ParabolicHoughResult Empty = new(
        Array.Empty<HoughDetectedParabola>(),
        0);

    public bool IsEmpty => Parabolas.Count == 0;
}
