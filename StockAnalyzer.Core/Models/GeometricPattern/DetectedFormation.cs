using System;

namespace StockAnalyzer.Core.Models.GeometricPattern;

/// <summary>
/// Represents a detected geometric chart formation with its trendline parameters.
/// The upper and lower trendlines are expressed as: Price = Slope * Index + Intercept.
/// </summary>
public class DetectedFormation
{
    /// <summary>The classified formation type.</summary>
    public GeometricFormationType Type { get; }

    /// <summary>Start index in the candle array where the formation begins.</summary>
    public int StartIndex { get; }

    /// <summary>End index in the candle array where the formation ends (inclusive).</summary>
    public int EndIndex { get; }

    /// <summary>Slope of the upper (resistance) trendline (price units per candle index).</summary>
    public double UpperSlope { get; }

    /// <summary>Y-intercept of the upper trendline at the formation start index.</summary>
    public double UpperIntercept { get; internal set; }

    /// <summary>Slope of the lower (support) trendline (price units per candle index).</summary>
    public double LowerSlope { get; }

    /// <summary>Y-intercept of the lower trendline at the formation start index.</summary>
    public double LowerIntercept { get; internal set; }

    /// <summary>
    /// Goodness-of-fit score (0.0 to 1.0) based on how well the pivot points
    /// align with the computed trendlines (R-squared average).
    /// </summary>
    public double ConfidenceScore { get; }

    /// <summary>True if a strong directional pole was detected immediately before the formation.</summary>
    public bool HasPole { get; }

    /// <summary>True if a distinct breakout occurred after the last pivot.</summary>
    public bool IsBrokenOut { get; init; } = true;

    /// <summary>The start <see cref="DateTime"/> of the formation.</summary>
    public DateTime StartTime { get; }

    /// <summary>The end <see cref="DateTime"/> of the formation.</summary>
    public DateTime EndTime { get; }

    public DetectedFormation(
        GeometricFormationType type,
        int startIndex,
        int endIndex,
        double upperSlope,
        double upperIntercept,
        double lowerSlope,
        double lowerIntercept,
        double confidenceScore,
        bool hasPole,
        DateTime startTime,
        DateTime endTime)
    {
        Type = type;
        StartIndex = startIndex;
        EndIndex = endIndex;
        UpperSlope = upperSlope;
        UpperIntercept = upperIntercept;
        LowerSlope = lowerSlope;
        LowerIntercept = lowerIntercept;
        ConfidenceScore = Math.Clamp(confidenceScore, 0.0, 1.0);
        HasPole = hasPole;
        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>
    /// Calculates the upper trendline price at a given candle index.
    /// </summary>
    public double UpperPriceAt(int index) => UpperSlope * (index - StartIndex) + UpperIntercept;

    /// <summary>
    /// Calculates the lower trendline price at a given candle index.
    /// </summary>
    public double LowerPriceAt(int index) => LowerSlope * (index - StartIndex) + LowerIntercept;

    public override string ToString()
        => $"{Type} [{StartIndex}-{EndIndex}] Confidence={ConfidenceScore:F2} Pole={HasPole}";
        
    public void Translate(double priceDelta)
    {
        UpperIntercept += priceDelta;
        LowerIntercept += priceDelta;
    }
}
