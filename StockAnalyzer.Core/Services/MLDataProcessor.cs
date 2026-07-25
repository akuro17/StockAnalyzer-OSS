using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service for processing and normalizing market data for machine learning models.
/// </summary>
public class MLDataProcessor : IMLDataProcessor
{
    private readonly ILogger<MLDataProcessor> _logger;
    private const int FeaturesPerCandle = 5;

    public MLDataProcessor(ILogger<MLDataProcessor>? logger = null)
    {
        _logger = logger ?? NullLogger<MLDataProcessor>.Instance;
    }

    public float[] NormalizeCandles(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            _logger.LogWarning("Normalization skipped: Candle list is null or empty.");
            return Array.Empty<float>();
        }

        var features = new float[candles.Count * FeaturesPerCandle];
        NormalizeCandles(candles, 0, candles.Count, features);
        return features;
    }

    public void NormalizeCandles(IReadOnlyList<CandleData> candles, Span<float> destination)
    {
        if (candles == null) return;
        NormalizeCandles(candles, 0, candles.Count, destination);
    }

    public void NormalizeCandles(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count * FeaturesPerCandle)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} candles.", destination.Length, count);
            return;
        }

        // Find min/max for normalization using for loop to avoid LINQ enumerator allocations
        float minPrice = float.MaxValue;
        float maxPrice = float.MinValue;
        long minVol = long.MaxValue;
        long maxVol = long.MinValue;

        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            if ((float)c.Low < minPrice) minPrice = (float)c.Low;
            if ((float)c.High > maxPrice) maxPrice = (float)c.High;
            if (c.Volume < minVol) minVol = c.Volume;
            if (c.Volume > maxVol) maxVol = c.Volume;
        }

        float priceRange = maxPrice - minPrice;
        if (priceRange == 0) priceRange = 1f;

        float volRange = (float)(maxVol - minVol);
        if (volRange == 0) volRange = 1f;

        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            int offset = i * FeaturesPerCandle;
            // Normalize OHLC by price range in the window
            destination[offset + 0] = ((float)c.Open - minPrice) / priceRange;
            destination[offset + 1] = ((float)c.High - minPrice) / priceRange;
            destination[offset + 2] = ((float)c.Low - minPrice) / priceRange;
            destination[offset + 3] = ((float)c.Close - minPrice) / priceRange;
            // Normalize Volume
            destination[offset + 4] = ((float)c.Volume - (float)minVol) / volRange;
        }
    }
}
