using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Interface for processing and normalizing market data for machine learning models.
/// </summary>
public interface IMLDataProcessor
{
    /// <summary>
    /// Normalizes candle data into a flat float array suitable for tensor input.
    /// Performs min-max normalization on price (OHLC) and volume.
    /// </summary>
    /// <param name="candles">The candles to normalize.</param>
    /// <returns>A flat float array with 5 features (OHLCV) per candle.</returns>
    float[] NormalizeCandles(IReadOnlyList<CandleData> candles);

    /// <summary>
    /// Normalizes candle data into the provided destination span.
    /// </summary>
    /// <param name="candles">The candles to normalize.</param>
    /// <param name="destination">The destination span for normalized features.</param>
    void NormalizeCandles(IReadOnlyList<CandleData> candles, Span<float> destination);

    /// <summary>
    /// Normalizes a specific range of candle data into the provided destination span.
    /// This avoids unnecessary list slicing or copying.
    /// </summary>
    /// <param name="candles">The source candles.</param>
    /// <param name="startIndex">The starting index in the source list.</param>
    /// <param name="count">The number of candles to process.</param>
    /// <param name="destination">The destination span for normalized features.</param>
    void NormalizeCandles(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);
}
