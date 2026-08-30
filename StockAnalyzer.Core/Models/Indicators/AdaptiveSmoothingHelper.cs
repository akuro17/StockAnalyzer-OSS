using System;
using System.Collections.Generic;

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
        => driverType == IndicatorType.HilbertTransform || driverType == IndicatorType.FFTCycle;

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

        // 1. Warmup nulls for indices [0 .. defaultPeriod - 1]
        for (int i = 0; i < defaultPeriod; i++)
        {
            results.Add(null);
        }

        // 2. Initial sum accumulation over the first defaultPeriod changes (indices 1 .. defaultPeriod)
        decimal sumGain = 0m;
        decimal sumLoss = 0m;

        for (int i = 1; i <= defaultPeriod; i++)
        {
            decimal curr = series[i] ?? 0m;
            decimal prev = series[i - 1] ?? 0m;
            decimal change = curr - prev;
            if (change > 0m)
            {
                sumGain += change;
            }
            else
            {
                sumLoss += -change;
            }
        }

        decimal avgGain = sumGain / defaultPeriod;
        decimal avgLoss = sumLoss / defaultPeriod;

        // 3. Compute initial RSI at index = defaultPeriod
        decimal initialRsi = CalculateRsiFromAverages(avgGain, avgLoss);
        results.Add(initialRsi);

        // 4. Adaptive recurrence loop for indices [defaultPeriod + 1 .. Count - 1]
        for (int i = defaultPeriod + 1; i < series.Count; i++)
        {
            decimal curr = series[i] ?? 0m;
            decimal prev = series[i - 1] ?? 0m;
            decimal change = curr - prev;
            decimal gain = change > 0m ? change : 0m;
            decimal loss = change < 0m ? -change : 0m;

            decimal? cycle = (periods != null && i < periods.Count) ? periods[i] : null;
            decimal alpha = CalculateAlphaForWilder(cycle, defaultPeriod, minPeriod, maxPeriod);

            avgGain = (1.0m - alpha) * avgGain + alpha * gain;
            avgLoss = (1.0m - alpha) * avgLoss + alpha * loss;

            results.Add(CalculateRsiFromAverages(avgGain, avgLoss));
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

        // 1. Warmup nulls for indices [0 .. defaultPeriod - 2]
        for (int i = 0; i < defaultPeriod - 1; i++)
        {
            results.Add(null);
        }

        // 2. Initial SMA at index = defaultPeriod - 1
        decimal sum = 0m;
        for (int i = 0; i < defaultPeriod; i++)
        {
            sum += series[i] ?? 0m;
        }

        decimal ema = sum / defaultPeriod;
        results.Add(ema);

        // 3. Adaptive EMA recurrence loop for indices [defaultPeriod .. Count - 1]
        for (int i = defaultPeriod; i < series.Count; i++)
        {
            decimal? cycle = (periods != null && i < periods.Count) ? periods[i] : null;
            decimal alpha = CalculateAlphaForEma(cycle, defaultPeriod, minPeriod, maxPeriod);

            decimal price = series[i] ?? 0m;
            ema = alpha * price + (1.0m - alpha) * ema;
            results.Add(ema);
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
            for (int j = 0; j < kFloor; j++)
            {
                sumFloor += series[i - j] ?? 0m;
            }
            decimal smaFloor = sumFloor / kFloor;

            if (kFloor == kCeil || frac == 0m || i < kCeil - 1)
            {
                results.Add(smaFloor);
            }
            else
            {
                decimal sumCeil = sumFloor + (series[i - (kCeil - 1)] ?? 0m);
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
}
