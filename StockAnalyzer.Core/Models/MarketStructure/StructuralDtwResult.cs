using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.MarketStructure;

/// <summary>
/// Represents a single similar historical pattern found by the structural DTW algorithm.
/// </summary>
public class SimilarPatternResult
{
    /// <summary>The structural DTW distance (lower = more similar).</summary>
    public double Distance { get; set; }

    /// <summary>The similarity probability (0.0 to 1.0, higher = more similar).</summary>
    public double Probability { get; set; }

    /// <summary>The start index in the historical candle array.</summary>
    public int StartIndex { get; set; }

    /// <summary>The end index in the historical candle array.</summary>
    public int EndIndex { get; set; }

    /// <summary>
    /// The future price path after the matched pattern, expressed as percentage change from the match endpoint.
    /// Each element represents the % change at that future step.
    /// </summary>
    public IReadOnlyList<double> FuturePath { get; set; } = System.Array.Empty<double>();
}

/// <summary>
/// Contains the results of a structural DTW analysis including MESA/EGARCH metadata.
/// </summary>
public class StructuralDtwResult
{
    /// <summary>The MESA-estimated dominant cycle period used for DTW window sizing.</summary>
    public int DominantPeriod { get; }

    /// <summary>The actual DTW comparison window length used.</summary>
    public int DtwWindow { get; }

    /// <summary>The EGARCH-estimated current query volatility.</summary>
    public double QueryVolatility { get; }

    /// <summary>The top-K similar patterns found, sorted by distance ascending.</summary>
    public IReadOnlyList<SimilarPatternResult> Matches { get; }

    /// <summary>Whether the analysis completed successfully.</summary>
    public bool IsSuccessful { get; }

    /// <summary>Error message if the analysis failed.</summary>
    public string? ErrorMessage { get; }

    private StructuralDtwResult(
        int dominantPeriod, int dtwWindow, double queryVolatility,
        IReadOnlyList<SimilarPatternResult> matches,
        bool isSuccessful, string? errorMessage)
    {
        DominantPeriod = dominantPeriod;
        DtwWindow = dtwWindow;
        QueryVolatility = queryVolatility;
        Matches = matches;
        IsSuccessful = isSuccessful;
        ErrorMessage = errorMessage;
    }

    public static StructuralDtwResult Success(
        int dominantPeriod, int dtwWindow, double queryVolatility,
        IReadOnlyList<SimilarPatternResult> matches)
        => new(dominantPeriod, dtwWindow, queryVolatility, matches, true, null);

    public static StructuralDtwResult Failure(string errorMessage)
        => new(0, 0, 0, System.Array.Empty<SimilarPatternResult>(), false, errorMessage);
}
