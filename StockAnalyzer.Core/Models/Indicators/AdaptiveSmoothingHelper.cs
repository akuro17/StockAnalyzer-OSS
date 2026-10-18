using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Reusable mathematical helper for adaptive indicators driven by dynamic cycle/period series.
/// Provides pure functions for continuous alpha smoothing (Wilder RSI, EMA), fractional smoothing (Fractional SMA),
/// and driver mapping/smoothing (Direct, InverseRatio, NormalizedRange).
/// Single Source of Truth (SSoT) for adaptive calculations shared between oscillators and moving averages.
/// Supports both raw CandleData and arbitrary price/indicator series (IReadOnlyList<decimal?>).
/// </summary>
public static class AdaptiveSmoothingHelper
{
    /// <summary>
    /// Clamps the input cycle/period value within [minPeriod, maxPeriod] and falls back to defaultPeriod if invalid or null.
    /// </summary>
    public static decimal ClampPeriod(decimal? cycle, decimal defaultPeriod, decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault, decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault)
    {
        if (!cycle.HasValue || cycle.Value <= 0m)
        {
            return Math.Clamp(defaultPeriod, minPeriod, maxPeriod);
        }

        return Math.Clamp(cycle.Value, minPeriod, maxPeriod);
    }

    /// <summary>
    /// Maps a raw driver value to an effective period based on the specified mapping mode and clamps it within [minPeriod, maxPeriod].
    /// </summary>
    public static decimal MapPeriod(
        decimal? driverValue, 
        decimal defaultPeriod, 
        decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        DynamicPeriodMappingMode mode = DynamicPeriodMappingMode.Direct,
        decimal? referenceValue = null)
    {
        // NormalizedRange treats 0 as the legitimate low end of its [0,1] input ratio,
        // so only Direct/InverseRatio reject non-positive driver values as invalid.
        if (!driverValue.HasValue || (mode != DynamicPeriodMappingMode.NormalizedRange && driverValue.Value <= 0m))
        {
            return Math.Clamp(defaultPeriod, minPeriod, maxPeriod);
        }

        decimal driverVal = driverValue.Value;
        decimal mapped;
        switch (mode)
        {
            case DynamicPeriodMappingMode.InverseRatio:
                decimal refVal = (referenceValue.HasValue && referenceValue.Value > 0m) ? referenceValue.Value : defaultPeriod;
                mapped = defaultPeriod * (refVal / driverVal);
                break;

            case DynamicPeriodMappingMode.NormalizedRange:
                decimal ratio = Math.Clamp(driverVal, 0m, 1m);
                mapped = minPeriod + (1.0m - ratio) * (maxPeriod - minPeriod);
                break;

            case DynamicPeriodMappingMode.Direct:
            default:
                mapped = driverVal;
                break;
        }

        return Math.Clamp(mapped, minPeriod, maxPeriod);
    }

    /// <summary>
    /// Indicator types whose output is already expressed in bars (a cycle/period length), so their raw values
    /// can drive another indicator's period directly (<see cref="DynamicPeriodMappingMode.Direct"/>).
    /// </summary>
    public static bool IsPeriodNativeDriverType(IndicatorType? driverType)
        => driverType == IndicatorType.HilbertTransform 
        || driverType == IndicatorType.FFTCycle 
        || driverType == IndicatorType.Autocorrelation;

    /// <summary>
    /// Converts a non-period-native driver series (e.g. a price-scale indicator such as SMA/EMA) into a usable
    /// period series by min-max normalizing it over its own full range and mapping the result into
    /// [minPeriod, maxPeriod] via <see cref="DynamicPeriodMappingMode.NormalizedRange"/>. Without this step, raw
    /// driver values (e.g. stock prices) are clamped directly as bar counts and saturate at maxPeriod, making the
    /// "dynamic" period effectively constant regardless of the driver's actual value.
    /// </summary>
    public static List<decimal?> NormalizeDriverSeries(IReadOnlyList<decimal?>? rawDriver, decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault, decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault)
    {
        if (rawDriver == null || rawDriver.Count == 0)
        {
            return new List<decimal?>();
        }

        decimal? min = null;
        decimal? max = null;
        foreach (var v in rawDriver)
        {
            if (!v.HasValue) continue;
            if (min == null || v.Value < min.Value) min = v.Value;
            if (max == null || v.Value > max.Value) max = v.Value;
        }

        var result = new List<decimal?>(rawDriver.Count);
        if (min == null || max == null || max.Value <= min.Value)
        {
            // No usable range (all null or constant): leave unmapped so downstream ClampPeriod falls back to the indicator's own default period.
            for (int i = 0; i < rawDriver.Count; i++) result.Add(null);
            return result;
        }

        decimal range = max.Value - min.Value;
        foreach (var v in rawDriver)
        {
            if (!v.HasValue)
            {
                result.Add(null);
                continue;
            }

            decimal ratio = (v.Value - min.Value) / range;
            result.Add(MapPeriod(ratio, minPeriod, minPeriod, maxPeriod, DynamicPeriodMappingMode.NormalizedRange));
        }

        return result;
    }

    /// <summary>
    /// Normalizes a dynamic period/driver series into the target period range [minPeriod, maxPeriod]
    /// strictly causally using a trailing rolling lookback window [max(0, i - lookbackWindow + 1) .. i].
    /// Eliminates look-ahead bias inherent in full-series normalization while preserving backward compatibility.
    /// </summary>
    public static IReadOnlyList<decimal?> NormalizeDriverSeriesCausal(
        IReadOnlyList<decimal?>? rawDriver,
        int lookbackWindow = 100,
        decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault)
    {
        if (rawDriver == null || rawDriver.Count == 0)
        {
            return Array.Empty<decimal?>();
        }

        int window = Math.Max(1, lookbackWindow);
        decimal minBound = Math.Min(minPeriod, maxPeriod);
        decimal maxBound = Math.Max(minPeriod, maxPeriod);
        decimal midPeriod = Math.Round((minBound + maxBound) / 2m, 0, MidpointRounding.AwayFromZero);

        var result = new List<decimal?>(rawDriver.Count);

        for (int i = 0; i < rawDriver.Count; i++)
        {
            var current = rawDriver[i];
            if (!current.HasValue)
            {
                result.Add(null);
                continue;
            }

            int start = Math.Max(0, i - window + 1);
            decimal minVal = current.Value;
            decimal maxVal = current.Value;

            for (int k = start; k <= i; k++)
            {
                var v = rawDriver[k];
                if (!v.HasValue) continue;
                if (v.Value < minVal) minVal = v.Value;
                if (v.Value > maxVal) maxVal = v.Value;
            }

            decimal range = maxVal - minVal;
            if (range == 0m)
            {
                result.Add(midPeriod);
            }
            else
            {
                decimal ratio = (current.Value - minVal) / range;
                decimal mapped = minBound + ratio * (maxBound - minBound);
                result.Add(Math.Clamp(mapped, minBound, maxBound));
            }
        }

        return result;
    }

    /// <summary>
    /// Smooths a dynamic period/driver series using a lightweight exponential filter to reduce per-bar jitter.
    /// </summary>
    public static List<decimal?> SmoothDriverSeries(IReadOnlyList<decimal?>? dynamicPeriods, decimal smoothingBeta = 0.2m)
    {
        if (dynamicPeriods == null || dynamicPeriods.Count == 0)
        {
            return new List<decimal?>();
        }

        var smoothed = new List<decimal?>(dynamicPeriods.Count);
        decimal? prev = null;

        for (int i = 0; i < dynamicPeriods.Count; i++)
        {
            var val = dynamicPeriods[i];
            if (!val.HasValue || val.Value <= 0m)
            {
                smoothed.Add(prev);
            }
            else
            {
                if (!prev.HasValue)
                {
                    prev = val.Value;
                }
                else
                {
                    prev = smoothingBeta * val.Value + (1.0m - smoothingBeta) * prev.Value;
                }
                smoothed.Add(prev);
            }
        }

        return smoothed;
    }

    /// <summary>
    /// Calculates the Wilder smoothing factor alpha = 1 / P for a given cycle.
    /// </summary>
    public static decimal CalculateAlphaForWilder(decimal? cycle, decimal defaultPeriod, decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault, decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault)
    {
        decimal p = ClampPeriod(cycle, defaultPeriod, minPeriod, maxPeriod);
        return p > 0m ? 1.0m / p : 1.0m / defaultPeriod;
    }

    /// <summary>
    /// Calculates the Exponential Moving Average smoothing factor alpha = 2 / (P + 1) for a given cycle.
    /// </summary>
    public static decimal CalculateAlphaForEma(decimal? cycle, decimal defaultPeriod, decimal minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault, decimal maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault)
    {
        decimal p = ClampPeriod(cycle, defaultPeriod, minPeriod, maxPeriod);
        return p > 0m ? 2.0m / (p + 1.0m) : 2.0m / (defaultPeriod + 1.0m);
    }

    /// <summary>
    /// Calculates Adaptive Relative Strength Index (RSI) using Wilder's smoothing with per-bar dynamic periods from a price series.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveWilderRsi(
        IReadOnlyList<decimal?> series,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 14,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        if (series == null || series.Count == 0)
        {
            return new List<decimal?>();
        }

        var periods = smoothDriver ? SmoothDriverSeries(dynamicPeriods) : dynamicPeriods;
        var results = new List<decimal?>(series.Count);

        if (series.Count <= defaultPeriod)
        {
            for (int i = 0; i < series.Count; i++)
            {
                results.Add(null);
            }
            return results;
        }

        decimal avgGain = 0m;
        decimal avgLoss = 0m;
        int validChanges = 0;
        bool isInitialized = false;

        for (int i = 0; i < series.Count; i++)
        {
            if (i == 0 || !series[i].HasValue || !series[i - 1].HasValue)
            {
                if (!isInitialized)
                {
                    avgGain = 0m;
                    avgLoss = 0m;
                    validChanges = 0;
                }
                results.Add(null);
                continue;
            }

            decimal curr = series[i]!.Value;
            decimal prev = series[i - 1]!.Value;
            decimal change = curr - prev;
            decimal gain = change > 0m ? change : 0m;
            decimal loss = change < 0m ? -change : 0m;

            if (!isInitialized)
            {
                avgGain += gain;
                avgLoss += loss;
                validChanges++;

                if (validChanges == defaultPeriod)
                {
                    avgGain /= defaultPeriod;
                    avgLoss /= defaultPeriod;
                    results.Add(CalculateRsiFromAverages(avgGain, avgLoss));
                    isInitialized = true;
                }
                else
                {
                    results.Add(null);
                }
            }
            else
            {
                decimal? cycle = (periods != null && i < periods.Count) ? periods[i] : null;
                decimal alpha = CalculateAlphaForWilder(cycle, defaultPeriod, minPeriod, maxPeriod);
                avgGain = (1.0m - alpha) * avgGain + alpha * gain;
                avgLoss = (1.0m - alpha) * avgLoss + alpha * loss;
                results.Add(CalculateRsiFromAverages(avgGain, avgLoss));
            }
        }

        return results;
    }

    /// <summary>
    /// Overload for CoreCandleData.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveWilderRsi(
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 14,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        var series = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Close);
        return CalculateAdaptiveWilderRsi(series, dynamicPeriods, defaultPeriod, minPeriod, maxPeriod, smoothDriver);
    }

    /// <summary>
    /// Calculates Adaptive Exponential Moving Average (EMA) with per-bar dynamic periods from a price series.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveEma(
        IReadOnlyList<decimal?> series,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 20,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        if (series == null || series.Count == 0)
        {
            return new List<decimal?>();
        }

        var periods = smoothDriver ? SmoothDriverSeries(dynamicPeriods) : dynamicPeriods;
        var results = new List<decimal?>(series.Count);

        if (series.Count < defaultPeriod)
        {
            for (int i = 0; i < series.Count; i++)
            {
                results.Add(null);
            }
            return results;
        }

        decimal? ema = null;
        for (int i = 0; i < series.Count; i++)
        {
            if (i < defaultPeriod - 1)
            {
                results.Add(null);
                continue;
            }

            if (ema == null)
            {
                decimal sum = 0m;
                bool valid = true;
                for (int j = 0; j < defaultPeriod; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue)
                    {
                        valid = false;
                        break;
                    }
                    sum += val.Value;
                }

                if (valid)
                {
                    ema = sum / defaultPeriod;
                    results.Add(ema);
                }
                else
                {
                    results.Add(null);
                }
            }
            else
            {
                var val = series[i];
                if (val.HasValue)
                {
                    decimal? cycle = (periods != null && i < periods.Count) ? periods[i] : null;
                    decimal alpha = CalculateAlphaForEma(cycle, defaultPeriod, minPeriod, maxPeriod);
                    ema = alpha * val.Value + (1.0m - alpha) * ema.Value;
                    results.Add(ema);
                }
                else
                {
                    ema = null;
                    results.Add(null);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Overload for CoreCandleData.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveEma(
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 20,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        var series = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Close);
        return CalculateAdaptiveEma(series, dynamicPeriods, defaultPeriod, minPeriod, maxPeriod, smoothDriver);
    }

    /// <summary>
    /// Calculates Adaptive Simple Moving Average (SMA) with per-bar continuous dynamic window lengths (Fractional SMA).
    /// Uses linear interpolation between floor(P) and ceil(P) to ensure smooth, continuous transitions without discrete steps.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveSma(
        IReadOnlyList<decimal?> series,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 20,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        if (series == null || series.Count == 0)
        {
            return new List<decimal?>();
        }

        var periods = smoothDriver ? SmoothDriverSeries(dynamicPeriods) : dynamicPeriods;
        var results = new List<decimal?>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            decimal? rawDriver = (periods != null && i < periods.Count) ? periods[i] : null;
            decimal periodDecimal = ClampPeriod(rawDriver, defaultPeriod, minPeriod, maxPeriod);

            int kFloor = (int)Math.Floor(periodDecimal);
            int kCeil = (int)Math.Ceiling(periodDecimal);
            decimal frac = periodDecimal - kFloor;

            if (i < kFloor - 1)
            {
                results.Add(null);
                continue;
            }

            decimal sumFloor = 0m;
            bool validFloor = true;
            for (int j = 0; j < kFloor; j++)
            {
                var val = series[i - j];
                if (!val.HasValue)
                {
                    validFloor = false;
                    break;
                }
                sumFloor += val.Value;
            }

            if (!validFloor)
            {
                results.Add(null);
                continue;
            }

            decimal smaFloor = sumFloor / kFloor;

            if (kFloor == kCeil || frac == 0m || i < kCeil - 1)
            {
                results.Add(smaFloor);
            }
            else
            {
                var ceilVal = series[i - (kCeil - 1)];
                if (!ceilVal.HasValue)
                {
                    results.Add(null);
                    continue;
                }
                decimal sumCeil = sumFloor + ceilVal.Value;
                decimal smaCeil = sumCeil / kCeil;

                // Continuous Linear Interpolation between floor and ceil
                decimal interpolated = (1.0m - frac) * smaFloor + frac * smaCeil;
                results.Add(interpolated);
            }
        }

        return results;
    }

    /// <summary>
    /// Overload for CoreCandleData.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveSma(
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyList<decimal?>? dynamicPeriods,
        int defaultPeriod = 20,
        int minPeriod = IndicatorDefaultConstants.DynamicPeriodMinDefault,
        int maxPeriod = IndicatorDefaultConstants.DynamicPeriodMaxDefault,
        bool smoothDriver = false)
    {
        var series = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Close);
        return CalculateAdaptiveSma(series, dynamicPeriods, defaultPeriod, minPeriod, maxPeriod, smoothDriver);
    }

    private static decimal CalculateRsiFromAverages(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m)
        {
            return avgGain == 0m ? 50.0m : 100.0m;
        }

        decimal rs = avgGain / avgLoss;
        return 100.0m - (100.0m / (1.0m + rs));
    }

    /// <summary>
    /// Applies the specified sorting-based smoothing method to a list of values.
    /// Preserved for backward compatibility.
    /// </summary>
    public static decimal ApplySorting(List<decimal> slice, SmoothingMethod method)
    {
        if (slice == null || slice.Count == 0) return 0m;
        return ApplySpanSmoothing(CollectionsMarshal.AsSpan(slice), method);
    }

    /// <summary>
    /// Applies the specified sorting-based smoothing method to a read-only span of values with zero heap allocations.
    /// Uses stackalloc for lengths <= 128 and ArrayPool<decimal>.Shared for larger windows.
    /// </summary>
    public static decimal ApplySpanSmoothing(ReadOnlySpan<decimal> window, SmoothingMethod method)
    {
        if (window.IsEmpty) return 0m;
        if (window.Length == 1) return window[0];

        int length = window.Length;
        decimal[]? rented = null;
        Span<decimal> buffer = length <= 128
            ? stackalloc decimal[length]
            : (rented = ArrayPool<decimal>.Shared.Rent(length)).AsSpan(0, length);

        try
        {
            window.CopyTo(buffer);
            buffer.Sort();

            int mid = length / 2;
            switch (method)
            {
                case SmoothingMethod.Median:
                    return (length % 2 == 1)
                        ? buffer[mid]
                        : (buffer[mid - 1] + buffer[mid]) / 2.0m;

                case SmoothingMethod.TrimmedMean10:
                    int trim = (int)(length * 0.1);
                    int count = length - 2 * trim;
                    if (count <= 0)
                    {
                        return (length % 2 == 1)
                            ? buffer[mid]
                            : (buffer[mid - 1] + buffer[mid]) / 2.0m;
                    }
                    decimal sum = 0m;
                    for (int k = trim; k < length - trim; k++)
                    {
                        sum += buffer[k];
                    }
                    return sum / count;

                case SmoothingMethod.Midhinge:
                    if (length < 4)
                    {
                        return (length % 2 == 1)
                            ? buffer[mid]
                            : (buffer[mid - 1] + buffer[mid]) / 2.0m;
                    }
                    decimal q1 = buffer[length / 4];
                    decimal q3 = buffer[(length * 3) / 4];
                    return (q1 + q3) / 2.0m;

                default:
                    return buffer[mid];
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Calculates a rolling smoothed series using the specified smoothing method (Median, TrimmedMean10, Midhinge)
    /// across fixed or dynamic periods with zero per-bar heap allocations.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveRolling(
        IReadOnlyList<decimal?> series,
        int period,
        SmoothingMethod method = SmoothingMethod.Median)
    {
        if (series == null || series.Count == 0)
        {
            return new List<decimal?>();
        }

        var results = new List<decimal?>(series.Count);
        if (period <= 0 || series.Count < period)
        {
            for (int i = 0; i < series.Count; i++) results.Add(null);
            return results;
        }

        decimal[]? rented = null;
        Span<decimal> buffer = period <= 128
            ? stackalloc decimal[period]
            : (rented = ArrayPool<decimal>.Shared.Rent(period)).AsSpan(0, period);

        try
        {
            for (int i = 0; i < series.Count; i++)
            {
                if (i < period - 1)
                {
                    results.Add(null);
                    continue;
                }

                bool hasNull = false;
                for (int j = 0; j < period; j++)
                {
                    var val = series[i - period + 1 + j];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    buffer[j] = val.Value;
                }

                if (hasNull)
                {
                    results.Add(null);
                }
                else
                {
                    results.Add(ApplySpanSmoothing(buffer.Slice(0, period), method));
                }
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<decimal>.Shared.Return(rented);
            }
        }

        return results;
    }

    /// <summary>
    /// Overload for CoreCandleData.
    /// </summary>
    public static List<decimal?> CalculateAdaptiveRolling(
        IReadOnlyList<CoreCandleData> candles,
        int period,
        SmoothingMethod method = SmoothingMethod.Median,
        PriceType priceType = PriceType.Close)
    {
        var series = PriceDataHelper.ExtractPriceSeries(candles, priceType);
        return CalculateAdaptiveRolling(series, period, method);
    }
}

/// <summary>
/// Smoothing method for adaptive and non-linear rolling window calculations.
/// </summary>
public enum SmoothingMethod
{
    Median,
    TrimmedMean10,
    Midhinge
}
