using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Utilities;

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
        if (IsHeikinAshi(priceType))
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
        if (IsHeikinAshi(priceType))
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
    /// True for HeikinAshiOpen, HeikinAshiHigh, HeikinAshiLow and HeikinAshiClose. SSoT of the predicate: every caller uses this method.
    /// </summary>
    public static bool IsHeikinAshi(PriceType priceType)
        => priceType is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

    /// <summary>
    /// Fills <paramref name="destination"/>[k] with the price of <c>candles[startIndex + k]</c>, equal to the series value of
    /// <see cref="ExtractPriceSeries(IReadOnlyList{CoreCandleData}?, PriceType)"/> for the same bar: True High / True Low use the
    /// previous bar's close (also for the first bar of the window when <paramref name="startIndex"/> &gt; 0), and Heikin-Ashi types follow the recursion of indicators_formulas_0466 over the whole list from <c>candles[0]</c>
    /// (<see cref="HeikinAshiConverter.GetStateAt"/> once, then one step per bar), so the value equals
    /// <see cref="HeikinAshiConverter.Convert(IReadOnlyList{CoreCandleData}?)"/> for the same list. When a caller only has a
    /// slice, the recursion starts at the slice's first candle; the difference to the full-history value halves every bar.
    /// </summary>
    public static void FillPrices(IReadOnlyList<CoreCandleData> candles, int startIndex, Span<decimal> destination, PriceType priceType)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (startIndex < 0 || startIndex > candles.Count - destination.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), $"Require 0 <= startIndex ({startIndex}) <= Count ({candles.Count}) - destination.Length ({destination.Length}).");
        }

        if (destination.Length == 0)
        {
            return;
        }

        if (!IsHeikinAshi(priceType))
        {
            for (int k = 0; k < destination.Length; k++)
            {
                int index = startIndex + k;
                decimal? previousClose = index > 0 ? candles[index - 1].Close : null;
                destination[k] = ExtractPrice(candles[index], priceType, previousClose);
            }

            return;
        }

        decimal? haOpen = null;
        decimal? haClose = null;
        if (startIndex > 0)
        {
            (decimal stateOpen, decimal stateClose) = HeikinAshiConverter.GetStateAt(candles, startIndex - 1);
            haOpen = stateOpen;
            haClose = stateClose;
        }

        for (int k = 0; k < destination.Length; k++)
        {
            var candle = candles[startIndex + k];
            destination[k] = ExtractPrice(candle, priceType, null, haOpen, haClose);

            if (haOpen.HasValue && haClose.HasValue)
            {
                (haOpen, haClose) = HeikinAshiConverter.NextState(candle, haOpen.Value, haClose.Value);
            }
            else
            {
                (decimal seedOpen, decimal seedClose) = HeikinAshiConverter.CalculateInitialValues(candle);
                haOpen = seedOpen;
                haClose = seedClose;
            }
        }
    }

    /// <summary>
    /// Copies <c>window.Length</c> prices starting at <paramref name="start"/> into <paramref name="window"/> as double.
    /// Returns false when any price in the range is missing (a missing price must not be substituted by a value).
    /// </summary>
    public static bool TryLoadWindow(IReadOnlyList<decimal?> prices, int start, Span<double> window)
    {
        for (int j = 0; j < window.Length; j++)
        {
            decimal? price = prices[start + j];
            if (!price.HasValue)
            {
                return false;
            }
            window[j] = (double)price.Value;
        }
        return true;
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
        bool isHeikinAshi = IsHeikinAshi(priceType);

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
    /// Extracts a price series from secondary candles aligned to primary candles by calendar date without look-ahead or truncation.
    /// Preserves chronological order and length parity with primary candles.
    /// </summary>
    public static List<decimal?> ExtractDateAlignedPriceSeries(
        IReadOnlyList<CoreCandleData> primaryCandles,
        IReadOnlyList<CoreCandleData?>? secondaryCandles,
        PriceType priceType)
    {
        if (primaryCandles == null || primaryCandles.Count == 0)
        {
            return new List<decimal?>();
        }

        if (secondaryCandles == null || secondaryCandles.Count == 0)
        {
            var nulls = new List<decimal?>(primaryCandles.Count);
            for (int i = 0; i < primaryCandles.Count; i++)
            {
                nulls.Add(null);
            }
            return nulls;
        }

        // Fast-path: Check if secondaryCandles is already 1:1 aligned with primaryCandles
        if (secondaryCandles.Count == primaryCandles.Count)
        {
            bool matches = true;
            for (int i = 0; i < primaryCandles.Count; i++)
            {
                var sc = secondaryCandles[i];
                if (sc != null)
                {
                    var sDate = sc.Timestamp.Date;
                    var pDate = primaryCandles[i].Timestamp.Date;
                    if (sDate != pDate && sc.Timestamp != default && primaryCandles[i].Timestamp != default)
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (matches)
            {
                return ExtractPriceSeries(secondaryCandles, priceType);
            }
        }

        // Zero-allocation two-pointer chronological scan (O(N + M))
        var result = new List<decimal?>(primaryCandles.Count);
        bool isHeikinAshi = IsHeikinAshi(priceType);
        decimal? prevHaOpen = null;
        decimal? prevHaClose = null;
        decimal? prevClose = null;
        decimal? lastMatchedPrice = null;
        DateTime lastMatchedDate = default;

        int s = 0;
        int m = secondaryCandles.Count;

        for (int p = 0; p < primaryCandles.Count; p++)
        {
            var pc = primaryCandles[p];
            if (pc.Timestamp == default)
            {
                result.Add(null);
                continue;
            }

            var pDate = pc.Timestamp.Date;

            // Advance secondary pointer while secondary candle date is strictly before primary date
            while (s < m)
            {
                var sc = secondaryCandles[s];
                if (sc == null || sc.Timestamp == default)
                {
                    s++;
                    continue;
                }

                var sDate = sc.Timestamp.Date;
                if (sDate < pDate)
                {
                    // Secondary candle precedes the current primary date.
                    // Advance recursive Heikin-Ashi state and prevClose to maintain chronological continuity.
                    var c = sc;
                    if (isHeikinAshi)
                    {
                        decimal haClose = (c.Open + c.High + c.Low + c.Close) / 4.0m;
                        decimal haOpen = (prevHaOpen.HasValue && prevHaClose.HasValue)
                            ? (prevHaOpen.Value + prevHaClose.Value) / 2.0m
                            : (c.Open + c.Close) / 2.0m;
                        prevHaOpen = haOpen;
                        prevHaClose = haClose;
                    }
                    prevClose = c.Close;
                    s++;
                }
                else
                {
                    break;
                }
            }

            // Check if current secondary candle matches pDate
            CoreCandleData? matched = null;
            if (s < m)
            {
                var sc = secondaryCandles[s];
                if (sc != null && sc.Timestamp != default && sc.Timestamp.Date == pDate)
                {
                    matched = sc;
                    s++;
                }
            }

            if (matched != null)
            {
                var c = matched;
                decimal price;
                if (isHeikinAshi)
                {
                    decimal haClose = (c.Open + c.High + c.Low + c.Close) / 4.0m;
                    decimal haOpen = (prevHaOpen.HasValue && prevHaClose.HasValue)
                        ? (prevHaOpen.Value + prevHaClose.Value) / 2.0m
                        : (c.Open + c.Close) / 2.0m;

                    price = priceType switch
                    {
                        PriceType.HeikinAshiOpen => haOpen,
                        PriceType.HeikinAshiClose => haClose,
                        PriceType.HeikinAshiHigh => Math.Max(c.High, Math.Max(haOpen, haClose)),
                        PriceType.HeikinAshiLow => Math.Min(c.Low, Math.Min(haOpen, haClose)),
                        _ => haClose
                    };

                    prevHaOpen = haOpen;
                    prevHaClose = haClose;
                }
                else
                {
                    price = ExtractPrice(c, priceType, prevClose);
                }

                result.Add(price);
                prevClose = c.Close;
                lastMatchedPrice = price;
                lastMatchedDate = pDate;
            }
            else if (lastMatchedPrice.HasValue && pDate == lastMatchedDate)
            {
                // Multiple primary candles on the same date: reuse the matched secondary price for this date
                result.Add(lastMatchedPrice.Value);
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
        bool isHeikinAshi = IsHeikinAshi(priceType);

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
        bool isHeikinAshi = IsHeikinAshi(priceType);

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
        bool isHeikinAshi = IsHeikinAshi(priceType);

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
    /// Extracts a non-nullable price series as <c>double</c> for the span-based math engines
    /// (the decimal-to-double conversion happens only at this input boundary).
    /// </summary>
    public static double[] ExtractDoubleSeries(IReadOnlyList<CoreCandleData>? candles, PriceType priceType)
    {
        var prices = ExtractNonNullablePriceSeries(candles, priceType);
        var result = new double[prices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (double)prices[i];
        }

        return result;
    }
}
