using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// A single class label and its associated probability score.
/// </summary>
public readonly record struct ClassScore(string Label, float Score);

/// <summary>
/// Result of a trend prediction.
/// </summary>
public sealed record PredictionResult(
    string Label,
    float Probability,
    IReadOnlyList<ClassScore> Scores,
    float Confidence = 0f,
    float Entropy = 0f,
    bool IsFallback = false)
{
    /// <summary>
    /// A safe, non-throwing fallback result returned when prediction cannot be produced.
    /// </summary>
    public static readonly PredictionResult Empty = new(
        Label: "Unknown",
        Probability: 0f,
        Scores: System.Array.Empty<ClassScore>(),
        Confidence: 0f,
        Entropy: 0f,
        IsFallback: true);
}

/// <summary>
/// Service for predicting price trends using machine learning models.
/// </summary>
public interface IPredictionService
{
    /// <summary>
    /// Initializes the prediction service (e.g., loading models).
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Predicts the direction of the next candle based on recent data.
    /// </summary>
    /// <param name="candles">The recent candle data to analyze.</param>
    /// <returns>A prediction result containing the predicted label and probability.</returns>
    Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles);
}
