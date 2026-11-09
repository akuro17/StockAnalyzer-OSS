namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Output calculation mode for Cross-Ticker Dynamic Time Warping (DTW) indicator.
/// </summary>
public enum DtwOutputMode
{
    /// <summary>
    /// Normalized DTW distance (RMSE_DTW = rawDistance / sqrt(Period)).
    /// </summary>
    Distance = 0,

    /// <summary>
    /// Bounded similarity score in range [0, 1] (1 / (1 + RMSE_DTW)).
    /// </summary>
    Similarity = 1
}
