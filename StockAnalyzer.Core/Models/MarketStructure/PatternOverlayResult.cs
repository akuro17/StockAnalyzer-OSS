using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.MarketStructure;

/// <summary>
/// Represents a single overlay pattern: a past similar segment and its actual future price path.
/// Used for DTW Pattern Overlay visualization on the chart.
/// </summary>
public class OverlayPattern
{
    /// <summary>The DTW distance to the current query pattern.</summary>
    public double Distance { get; set; }

    /// <summary>The similarity probability (0.0 to 1.0).</summary>
    public double Probability { get; set; }

    /// <summary>Start index of the matched segment in the historical data.</summary>
    public int StartIndex { get; set; }

    /// <summary>End index of the matched segment in the historical data.</summary>
    public int EndIndex { get; set; }

    /// <summary>The actual prices of the matched segment (for analog chart rendering).</summary>
    public IReadOnlyList<double> MatchedPrices { get; set; } = Array.Empty<double>();

    /// <summary>The raw future prices following the matched segment.</summary>
    public IReadOnlyList<double> FutureRawPrices { get; set; } = Array.Empty<double>();

    /// <summary>The future path as percentage change from the match endpoint.</summary>
    public IReadOnlyList<double> FuturePercentChange { get; set; } = Array.Empty<double>();
}

/// <summary>
/// Result of a DTW Pattern Overlay search: contains multiple overlay patterns
/// that can be drawn on the chart alongside the current price action.
/// </summary>
public class PatternOverlayResult
{
    /// <summary>The query length used for matching.</summary>
    public int QueryLength { get; }

    /// <summary>The overlay patterns found, sorted by distance ascending.</summary>
    public IReadOnlyList<OverlayPattern> Patterns { get; }

    /// <summary>Whether the search completed successfully.</summary>
    public bool IsSuccessful { get; }

    /// <summary>Error message if the search failed.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Optional Petitjean DBA ensemble barycenter trajectory (percentage change from anchor close).</summary>
    public IReadOnlyList<double> DbaBarycenterPercentChange { get; }

    /// <summary>Optional weighted 15th percentile DBA ensemble lower band (percentage change from anchor close).</summary>
    public IReadOnlyList<double> DbaLowerBandPercentChange { get; }

    /// <summary>Optional weighted 85th percentile DBA ensemble upper band (percentage change from anchor close).</summary>
    public IReadOnlyList<double> DbaUpperBandPercentChange { get; }

    internal PatternOverlayResult(
        int queryLength,
        IReadOnlyList<OverlayPattern> patterns,
        bool isSuccessful,
        string? errorMessage,
        IReadOnlyList<double>? dbaBarycenterPercentChange = null,
        IReadOnlyList<double>? dbaLowerBandPercentChange = null,
        IReadOnlyList<double>? dbaUpperBandPercentChange = null)
    {
        QueryLength = queryLength;
        Patterns = patterns;
        IsSuccessful = isSuccessful;
        ErrorMessage = errorMessage;
        DbaBarycenterPercentChange = dbaBarycenterPercentChange ?? Array.Empty<double>();
        DbaLowerBandPercentChange = dbaLowerBandPercentChange ?? Array.Empty<double>();
        DbaUpperBandPercentChange = dbaUpperBandPercentChange ?? Array.Empty<double>();
    }

    public static PatternOverlayResult Success(int queryLength, IReadOnlyList<OverlayPattern> patterns)
        => new(queryLength, patterns, true, null);

    public static PatternOverlayResult SuccessWithEnsemble(
        int queryLength,
        IReadOnlyList<OverlayPattern> patterns,
        IReadOnlyList<double> barycenterPercentChange,
        IReadOnlyList<double> lowerBandPercentChange,
        IReadOnlyList<double> upperBandPercentChange)
        => new(queryLength, patterns, true, null, barycenterPercentChange, lowerBandPercentChange, upperBandPercentChange);

    public static PatternOverlayResult Failure(string errorMessage)
        => new(0, Array.Empty<OverlayPattern>(), false, errorMessage);
}
