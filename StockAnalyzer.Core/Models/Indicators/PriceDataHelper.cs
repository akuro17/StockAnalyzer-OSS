using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// High-performance helper to extract price series from candle data based on PriceType.
/// </summary>
public static class PriceDataHelper
{
    /// <summary>
    /// Default price source type for technical indicators.
    /// Configurable internally to support flexible default adjustments.
    /// </summary>
    public static PriceType DefaultPriceType { get; set; } = PriceType.Close;

    /// <summary>
    /// Ordered list of PriceType options for UI selection: Open, High, Low, Close, Median, Typical, Weighted.
    /// Close is positioned directly below Low following the standard OHLC ordering.
    /// </summary>
    public static IReadOnlyList<PriceType> PriceTypeOptions { get; } = new[]
    {
        PriceType.Open,
        PriceType.High,
        PriceType.Low,
        PriceType.Close,
        PriceType.Median,
        PriceType.Typical,
        PriceType.Weighted
    };

    /// <summary>
    /// Resolves the default price type for an indicator type (defaults to DefaultPriceType).
    /// </summary>
    public static PriceType GetDefaultPriceType(IndicatorType? indicatorType = null) => DefaultPriceType;

    /// <summary>
    /// Extracts the specified price field for a single CoreCandleData.
    /// </summary>
    public static decimal ExtractPrice(in CoreCandleData candle, PriceType priceType)
    {
        return priceType switch
        {
            PriceType.Close => candle.Close,
            PriceType.Open => candle.Open,
            PriceType.High => candle.High,
            PriceType.Low => candle.Low,
            PriceType.Median => (candle.High + candle.Low) / 2.0m,
            PriceType.Typical => (candle.High + candle.Low + candle.Close) / 3.0m,
            PriceType.Weighted => (candle.High + candle.Low + 2.0m * candle.Close) / 4.0m,
            _ => candle.Close
        };
    }

    /// <summary>
    /// Extracts the specified price field for a single CandleData.
    /// </summary>
    public static decimal ExtractPrice(in CandleData candle, PriceType priceType)
    {
        return priceType switch
        {
            PriceType.Close => candle.Close,
            PriceType.Open => candle.Open,
            PriceType.High => candle.High,
            PriceType.Low => candle.Low,
            PriceType.Median => (candle.High + candle.Low) / 2.0m,
            PriceType.Typical => (candle.High + candle.Low + candle.Close) / 3.0m,
            PriceType.Weighted => (candle.High + candle.Low + 2.0m * candle.Close) / 4.0m,
            _ => candle.Close
        };
    }

    /// <summary>
    /// Extracts a nullable price series (IReadOnlyList<decimal?>) from CoreCandleData.
    /// </summary>
    public static List<decimal?> ExtractPriceSeries(IReadOnlyList<CoreCandleData>? candles, PriceType priceType = PriceType.Close)
    {
        if (candles == null || candles.Count == 0)
        {
            return new List<decimal?>();
        }

        var result = new List<decimal?>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            result.Add(ExtractPrice(candles[i], priceType));
        }

        return result;
    }

    /// <summary>
    /// Extracts a nullable price series (IReadOnlyList<decimal?>) from CandleData.
    /// </summary>
    public static List<decimal?> ExtractPriceSeries(IReadOnlyList<CandleData>? candles, PriceType priceType = PriceType.Close)
    {
        if (candles == null || candles.Count == 0)
        {
            return new List<decimal?>();
        }

        var result = new List<decimal?>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            result.Add(ExtractPrice(candles[i], priceType));
        }

        return result;
    }

    /// <summary>
    /// Extracts a non-nullable price series (IReadOnlyList<decimal>) from CoreCandleData.
    /// </summary>
    public static List<decimal> ExtractNonNullablePriceSeries(IReadOnlyList<CoreCandleData>? candles, PriceType priceType = PriceType.Close)
    {
        if (candles == null || candles.Count == 0)
        {
            return new List<decimal>();
        }

        var result = new List<decimal>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            result.Add(ExtractPrice(candles[i], priceType));
        }

        return result;
    }

    /// <summary>
    /// Extracts a non-nullable price series (IReadOnlyList<decimal>) from CandleData.
    /// </summary>
    public static List<decimal> ExtractNonNullablePriceSeries(IReadOnlyList<CandleData>? candles, PriceType priceType = PriceType.Close)
    {
        if (candles == null || candles.Count == 0)
        {
            return new List<decimal>();
        }

        var result = new List<decimal>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            result.Add(ExtractPrice(candles[i], priceType));
        }

        return result;
    }
}
