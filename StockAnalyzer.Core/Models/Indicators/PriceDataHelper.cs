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
    /// Ordered list of PriceType options for UI selection:
    /// Open, High, Low, Close, Median, Midpoint, Typical, Weighted, Average,
    /// HeikinAshiOpen, HeikinAshiHigh, HeikinAshiLow, HeikinAshiClose, TrueHigh, TrueLow.
    /// Close is positioned directly below Low following the standard OHLC ordering.
    /// Heikin-Ashi values are positioned between Average and TrueHigh.
    /// </summary>
    public static IReadOnlyList<PriceType> PriceTypeOptions { get; } = new[]
    {
        PriceType.Open,
        PriceType.High,
        PriceType.Low,
        PriceType.Close,
        PriceType.Median,
        PriceType.Midpoint,
        PriceType.Typical,
        PriceType.Weighted,
        PriceType.Average,
        PriceType.HeikinAshiOpen,
        PriceType.HeikinAshiHigh,
        PriceType.HeikinAshiLow,
        PriceType.HeikinAshiClose,
        PriceType.TrueHigh,
        PriceType.TrueLow
    };

    /// <summary>
    /// Formats a human-readable display label for a given PriceType.
    /// Acts as the Single Source of Truth (SSoT) across Training Wizard, Indicator Manager, and Screener.
    /// </summary>
    public static string FormatPriceTypeLabel(PriceType field) => field switch
    {
        PriceType.Open => "Open",
        PriceType.High => "High",
        PriceType.Low => "Low",
        PriceType.Close => "Close",
        PriceType.Median => "Median (H+L)/2",
        PriceType.Midpoint => "Midpoint (O+C)/2",
        PriceType.Typical => "Typical (H+L+C)/3",
        PriceType.Weighted => "Weighted (H+L+2C)/4",
        PriceType.Average => "Average (O+H+L+C)/4",
        PriceType.HeikinAshiOpen => "Heikin-Ashi Open",
        PriceType.HeikinAshiHigh => "Heikin-Ashi High",
        PriceType.HeikinAshiLow => "Heikin-Ashi Low",
        PriceType.HeikinAshiClose => "Heikin-Ashi Close",
        PriceType.TrueHigh => "True High",
        PriceType.TrueLow => "True Low",
        _ => field.ToString()
    };

    /// <summary>
    /// Resolves the default price type for an indicator type (defaults to DefaultPriceType).
    /// </summary>
    public static PriceType GetDefaultPriceType(IndicatorType? indicatorType = null) => DefaultPriceType;

    /// <summary>
    /// Finds the index of a PriceType in the canonical PriceTypeOptions list.
    /// Returns index 4 (Median) if not found.
    /// </summary>
    public static int GetPriceTypeIndex(PriceType priceType)
    {
        for (int i = 0; i < PriceTypeOptions.Count; i++)
        {
            if (PriceTypeOptions[i] == priceType) return i;
        }
        return 4; // Default to Median
    }

    /// <summary>
    /// Gets the PriceType at the given index in PriceTypeOptions.
    /// Returns PriceType.Median if index is out of range.
    /// </summary>
    public static PriceType GetPriceTypeByIndex(int index)
    {
        return index >= 0 && index < PriceTypeOptions.Count
            ? PriceTypeOptions[index]
            : PriceType.Median;
    }

    /// <summary>
    /// Extracts the specified price field for a single CoreCandleData with optional previous close for TrueHigh/TrueLow
    /// and optional previous Heikin-Ashi state for HeikinAshi Open/High/Low/Close.
    /// </summary>
    public static decimal ExtractPrice(in CoreCandleData candle, PriceType priceType, decimal? previousClose = null, decimal? prevHaOpen = null, decimal? prevHaClose = null)
    {
        if (priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose)
        {
            decimal haOpen, haClose;
            if (prevHaOpen.HasValue && prevHaClose.HasValue)
            {
                haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
            }
            else
            {
                haOpen = (candle.Open + candle.Close) / 2.0m;
                haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
            }

            return priceType switch
            {
                PriceType.HeikinAshiOpen => haOpen,
                PriceType.HeikinAshiClose => haClose,
                PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                _ => haClose
            };
        }

        return priceType switch
        {
            PriceType.Close => candle.Close,
            PriceType.Open => candle.Open,
            PriceType.High => candle.High,
            PriceType.Low => candle.Low,
            PriceType.Median => (candle.High + candle.Low) / 2.0m,
            PriceType.Midpoint => (candle.Open + candle.Close) / 2.0m,
            PriceType.Typical => (candle.High + candle.Low + candle.Close) / 3.0m,
            PriceType.Weighted => (candle.High + candle.Low + 2.0m * candle.Close) / 4.0m,
            PriceType.Average => (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m,
            PriceType.TrueHigh => previousClose.HasValue ? Math.Max(candle.High, previousClose.Value) : candle.High,
            PriceType.TrueLow => previousClose.HasValue ? Math.Min(candle.Low, previousClose.Value) : candle.Low,
            _ => candle.Close
        };
    }

    /// <summary>
    /// Extracts the specified price field for a single CandleData with optional previous close for TrueHigh/TrueLow
    /// and optional previous Heikin-Ashi state for HeikinAshi Open/High/Low/Close.
    /// </summary>
    public static decimal ExtractPrice(in CandleData candle, PriceType priceType, decimal? previousClose = null, decimal? prevHaOpen = null, decimal? prevHaClose = null)
    {
        if (priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose)
        {
            decimal haOpen, haClose;
            if (prevHaOpen.HasValue && prevHaClose.HasValue)
            {
                haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
            }
            else
            {
                haOpen = (candle.Open + candle.Close) / 2.0m;
                haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
            }

            return priceType switch
            {
                PriceType.HeikinAshiOpen => haOpen,
                PriceType.HeikinAshiClose => haClose,
                PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                _ => haClose
            };
        }

        return priceType switch
        {
            PriceType.Close => candle.Close,
            PriceType.Open => candle.Open,
            PriceType.High => candle.High,
            PriceType.Low => candle.Low,
            PriceType.Median => (candle.High + candle.Low) / 2.0m,
            PriceType.Midpoint => (candle.Open + candle.Close) / 2.0m,
            PriceType.Typical => (candle.High + candle.Low + candle.Close) / 3.0m,
            PriceType.Weighted => (candle.High + candle.Low + 2.0m * candle.Close) / 4.0m,
            PriceType.Average => (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m,
            PriceType.TrueHigh => previousClose.HasValue ? Math.Max(candle.High, previousClose.Value) : candle.High,
            PriceType.TrueLow => previousClose.HasValue ? Math.Min(candle.Low, previousClose.Value) : candle.Low,
            _ => candle.Close
        };
    }

    /// <summary>
    /// Extracts a nullable price series (IReadOnlyList<decimal?>) from CoreCandleData (including nullable entries).
    /// </summary>
    public static List<decimal?> ExtractPriceSeries(IReadOnlyList<CoreCandleData?>? candles, PriceType priceType = PriceType.Close)
    {
        if (candles == null || candles.Count == 0)
        {
            return new List<decimal?>();
        }

        var result = new List<decimal?>(candles.Count);
        bool isHeikinAshi = priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

        if (isHeikinAshi)
        {
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                if (candle != null)
                {
                    decimal haOpen, haClose;
                    if (prevHaOpen.HasValue && prevHaClose.HasValue)
                    {
                        haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                        haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
                    }
                    else
                    {
                        haOpen = (candle.Open + candle.Close) / 2.0m;
                        haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                    }

                    decimal price = priceType switch
                    {
                        PriceType.HeikinAshiOpen => haOpen,
                        PriceType.HeikinAshiClose => haClose,
                        PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                        PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                        _ => haClose
                    };

                    result.Add(price);
                    prevHaOpen = haOpen;
                    prevHaClose = haClose;
                }
                else
                {
                    result.Add(null);
                }
            }

            return result;
        }

        for (int i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle != null)
            {
                decimal? prevClose = (i > 0 && candles[i - 1] != null) ? candles[i - 1]!.Close : null;
                result.Add(ExtractPrice(candle, priceType, prevClose));
            }
            else
            {
                result.Add(null);
            }
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
        bool isHeikinAshi = priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

        if (isHeikinAshi)
        {
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal haOpen, haClose;
                if (prevHaOpen.HasValue && prevHaClose.HasValue)
                {
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                    haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
                }
                else
                {
                    haOpen = (candle.Open + candle.Close) / 2.0m;
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                }

                decimal price = priceType switch
                {
                    PriceType.HeikinAshiOpen => haOpen,
                    PriceType.HeikinAshiClose => haClose,
                    PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                    PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                    _ => haClose
                };

                result.Add(price);
                prevHaOpen = haOpen;
                prevHaClose = haClose;
            }

            return result;
        }

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? prevClose = i > 0 ? candles[i - 1].Close : null;
            result.Add(ExtractPrice(candles[i], priceType, prevClose));
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
        bool isHeikinAshi = priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

        if (isHeikinAshi)
        {
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal haOpen, haClose;
                if (prevHaOpen.HasValue && prevHaClose.HasValue)
                {
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                    haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
                }
                else
                {
                    haOpen = (candle.Open + candle.Close) / 2.0m;
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                }

                decimal price = priceType switch
                {
                    PriceType.HeikinAshiOpen => haOpen,
                    PriceType.HeikinAshiClose => haClose,
                    PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                    PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                    _ => haClose
                };

                result.Add(price);
                prevHaOpen = haOpen;
                prevHaClose = haClose;
            }

            return result;
        }

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? prevClose = i > 0 ? candles[i - 1].Close : null;
            result.Add(ExtractPrice(candles[i], priceType, prevClose));
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
        bool isHeikinAshi = priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

        if (isHeikinAshi)
        {
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal haOpen, haClose;
                if (prevHaOpen.HasValue && prevHaClose.HasValue)
                {
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                    haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
                }
                else
                {
                    haOpen = (candle.Open + candle.Close) / 2.0m;
                    haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4.0m;
                }

                decimal price = priceType switch
                {
                    PriceType.HeikinAshiOpen => haOpen,
                    PriceType.HeikinAshiClose => haClose,
                    PriceType.HeikinAshiHigh => Math.Max(candle.High, Math.Max(haOpen, haClose)),
                    PriceType.HeikinAshiLow => Math.Min(candle.Low, Math.Min(haOpen, haClose)),
                    _ => haClose
                };

                result.Add(price);
                prevHaOpen = haOpen;
                prevHaClose = haClose;
            }

            return result;
        }

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? prevClose = i > 0 ? candles[i - 1].Close : null;
            result.Add(ExtractPrice(candles[i], priceType, prevClose));
        }

        return result;
    }
}
