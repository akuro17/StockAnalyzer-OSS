using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents a single chart pattern detected by the DTW-based pattern recognition service.
/// </summary>
public class DetectedPattern
{
    /// <summary>
    /// The name of the detected pattern (e.g., "HeadAndShoulders", "DoubleBottom").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Heuristic similarity score of the pattern match in (0.0, 1.0], computed as exp(-DTW / sqrt(window)) with an
    /// optional short-window penalty. It is not a statistical probability and is not calibrated against outcomes;
    /// scores are only comparable within the same window settings.
    /// </summary>
    public double Probability { get; set; }

    /// <summary>
    /// The start index in the candle array where the pattern begins.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// The end index in the candle array where the pattern ends.
    /// </summary>
    public int EndIndex { get; set; }
}

/// <summary>
/// Contains the results of a chart pattern recognition analysis.
/// </summary>
public class PatternRecognitionResult
{
    /// <summary>
    /// The list of detected patterns, sorted by probability descending.
    /// </summary>
    public IReadOnlyList<DetectedPattern> Patterns { get; }

    /// <summary>
    /// Whether the analysis completed successfully.
    /// </summary>
    public bool IsSuccessful { get; }

    /// <summary>
    /// Error message if the analysis failed.
    /// </summary>
    public string? ErrorMessage { get; }

    private PatternRecognitionResult(IReadOnlyList<DetectedPattern> patterns, bool isSuccessful, string? errorMessage)
    {
        Patterns = patterns;
        IsSuccessful = isSuccessful;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a successful result with the detected patterns.
    /// </summary>
    public static PatternRecognitionResult Success(IReadOnlyList<DetectedPattern> patterns)
        => new(patterns, true, null);

    /// <summary>
    /// Creates a failure result with an error message.
    /// </summary>
    public static PatternRecognitionResult Failure(string errorMessage)
        => new(System.Array.Empty<DetectedPattern>(), false, errorMessage);
}
