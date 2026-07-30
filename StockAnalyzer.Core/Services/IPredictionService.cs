using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Result of a trend prediction.
/// </summary>
public record PredictionResult(string Label, float Probability, Dictionary<string, float> Scores);

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
