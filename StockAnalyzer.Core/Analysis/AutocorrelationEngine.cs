using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance, zero-allocation computation engine for rolling autocorrelation analysis
/// and dominant cycle extraction.
/// </summary>
public static class AutocorrelationEngine
{
    public const double VarianceEpsilon = 1e-20;
    public const double SubBinEpsilon = 1e-12;

    /// <summary>
    /// Computes dominant cycle period and correlation metrics over a series of prices using Span buffers.
    /// Operates with zero heap allocations inside the rolling window loop.
    /// </summary>
    public static void CalculateDominantPeriod(
        ReadOnlySpan<double> prices,
        int windowSize,
        int minLag,
        int maxLag,
        double threshold,
        int smoothingPeriod,
        int defaultPeriod,
        AutocorrelationPeakSelectionMode selectionMode,
        AutocorrelationFallbackPolicy fallbackPolicy,
        int maxHoldBars,
        Span<double?> outDominantPeriod,
        Span<double?> outRawPeriod,
        Span<double?> outPeakCorrelation,
        int lag = 0,
        double minProminence = 0.01)
    {
        int n = prices.Length;
        int effectiveMax = (lag > 0) ? Math.Max(maxLag, lag) : maxLag;
        int effectiveMin = (lag > 0) ? Math.Min(minLag, lag) : minLag;
        int requiredWarmup = windowSize + effectiveMax;

        int warmupLimit = Math.Min(n, requiredWarmup);
        for (int i = 0; i < warmupLimit; i++)
        {
            outDominantPeriod[i] = null;
            outRawPeriod[i] = null;
            outPeakCorrelation[i] = null;
        }

        if (n <= requiredWarmup) return;

        int scratchSize = effectiveMax + 2;
        double[]? pooledR = null;
        Span<double> rValues = scratchSize <= 256
            ? stackalloc double[scratchSize]
            : (pooledR = ArrayPool<double>.Shared.Rent(scratchSize)).AsSpan(0, scratchSize);

        try
        {
            double? lastValidPeriod = null;
            int missCount = 0;
            double? prevSmoothed = null;
            double alpha = 2.0 / (smoothingPeriod + 1.0);

            for (int t = requiredWarmup; t < n; t++)
            {
                ReadOnlySpan<double> u = prices.Slice(t - windowSize + 1, windowSize);
                double sumU = 0.0;
                for (int j = 0; j < windowSize; j++) sumU += u[j];
                double meanU = sumU / windowSize;

                double sumUU = 0.0;
                for (int j = 0; j < windowSize; j++)
                {
                    double du = u[j] - meanU;
                    sumUU += du * du;
                }

                if (sumUU <= VarianceEpsilon)
                {
                    HandleFallback(defaultPeriod, fallbackPolicy, maxHoldBars, ref lastValidPeriod, ref missCount, ref prevSmoothed, alpha, t, outDominantPeriod, outRawPeriod, outPeakCorrelation);
                    continue;
                }

                int calcStartLag = Math.Max(1, effectiveMin - 1);
                int calcEndLag = effectiveMax + 1;

                for (int k = calcStartLag; k <= calcEndLag; k++)
                {
                    ReadOnlySpan<double> v = prices.Slice(t - k - windowSize + 1, windowSize);
                    double sumV = 0.0;
                    for (int j = 0; j < windowSize; j++) sumV += v[j];
                    double meanV = sumV / windowSize;

                    double sumVV = 0.0;
                    double sumUV = 0.0;
                    for (int j = 0; j < windowSize; j++)
                    {
                        double du = u[j] - meanU;
                        double dv = v[j] - meanV;
                        sumVV += dv * dv;
                        sumUV += du * dv;
                    }

                    if (sumVV <= VarianceEpsilon)
                    {
                        rValues[k] = double.NaN;
                    }
                    else
                    {
                        double r = sumUV / Math.Sqrt(sumUU * sumVV);
                        rValues[k] = Math.Clamp(r, -1.0, 1.0);
                    }
                }

                int bestK = -1;
                double bestR = -2.0;

                for (int k = minLag; k <= maxLag; k++)
                {
                    double rPrev = rValues[k - 1];
                    double rCurr = rValues[k];
                    double rNext = rValues[k + 1];

                    if (double.IsNaN(rPrev) || double.IsNaN(rCurr) || double.IsNaN(rNext))
                    {
                        continue;
                    }

                    bool isPeak = (rCurr > rPrev) && (rCurr >= rNext);
                    double prominence = rCurr - Math.Max(rPrev, rNext);
                    if (isPeak && rCurr >= threshold && prominence >= minProminence)
                    {
                        if (selectionMode == AutocorrelationPeakSelectionMode.FirstPeak)
                        {
                            bestK = k;
                            bestR = rCurr;
                            break;
                        }
                        else
                        {
                            if (rCurr > bestR + 1e-4)
                            {
                                bestR = rCurr;
                                bestK = k;
                            }
                        }
                    }
                }

                if (bestK >= minLag && bestK <= maxLag)
                {
                    double pPrev = rValues[bestK - 1];
                    double pCurr = rValues[bestK];
                    double pNext = rValues[bestK + 1];
                    double denom = 2.0 * (2.0 * pCurr - pPrev - pNext);
                    double delta = (denom > SubBinEpsilon) ? Math.Clamp((pNext - pPrev) / denom, -0.5, 0.5) : 0.0;
                    double continuousK = bestK + delta;

                    lastValidPeriod = continuousK;
                    missCount = 0;

                    double smoothed = prevSmoothed.HasValue
                        ? (alpha * continuousK + (1.0 - alpha) * prevSmoothed.Value)
                        : continuousK;
                    prevSmoothed = smoothed;

                    outDominantPeriod[t] = smoothed;
                    outRawPeriod[t] = continuousK;
                    outPeakCorrelation[t] = (lag > 0)
                        ? (double.IsNaN(rValues[lag]) ? null : rValues[lag])
                        : (bestR > -2.0 ? bestR : null);
                }
                else
                {
                    HandleFallback(defaultPeriod, fallbackPolicy, maxHoldBars, ref lastValidPeriod, ref missCount, ref prevSmoothed, alpha, t, outDominantPeriod, outRawPeriod, outPeakCorrelation);
                    if (lag > 0)
                    {
                        outPeakCorrelation[t] = double.IsNaN(rValues[lag]) ? null : rValues[lag];
                    }
                }
            }
        }
        finally
        {
            if (pooledR != null)
            {
                ArrayPool<double>.Shared.Return(pooledR);
            }
        }
    }

    private static void HandleFallback(
        int defaultPeriod,
        AutocorrelationFallbackPolicy fallbackPolicy,
        int maxHoldBars,
        ref double? lastValidPeriod,
        ref int missCount,
        ref double? prevSmoothed,
        double alpha,
        int t,
        Span<double?> outDominantPeriod,
        Span<double?> outRawPeriod,
        Span<double?> outPeakCorrelation)
    {
        double periodVal;
        if (fallbackPolicy == AutocorrelationFallbackPolicy.HoldPrevious && lastValidPeriod.HasValue && missCount < maxHoldBars)
        {
            missCount++;
            periodVal = lastValidPeriod.Value;
        }
        else
        {
            missCount = maxHoldBars + 1;
            periodVal = defaultPeriod;
        }

        double smoothed = prevSmoothed.HasValue
            ? (alpha * periodVal + (1.0 - alpha) * prevSmoothed.Value)
            : periodVal;
        prevSmoothed = smoothed;

        outDominantPeriod[t] = smoothed;
        outRawPeriod[t] = null;
        outPeakCorrelation[t] = null;
    }

    public static (List<decimal?> DominantPeriod, List<decimal?> RawPeriod, List<decimal?> PeakCorrelation) CalculateDominantPeriod(
        IReadOnlyList<decimal?> series,
        int windowSize,
        int minLag,
        int maxLag,
        double threshold,
        int smoothingPeriod,
        int defaultPeriod,
        AutocorrelationPeakSelectionMode selectionMode,
        AutocorrelationFallbackPolicy fallbackPolicy,
        int maxHoldBars,
        int lag = 0,
        double minProminence = 0.01)
    {
        if (series == null || series.Count == 0)
        {
            return (new List<decimal?>(), new List<decimal?>(), new List<decimal?>());
        }

        int n = series.Count;
        double[] rentedPrices = ArrayPool<double>.Shared.Rent(n);
        double?[] rentedDom = ArrayPool<double?>.Shared.Rent(n);
        double?[] rentedRaw = ArrayPool<double?>.Shared.Rent(n);
        double?[] rentedPeak = ArrayPool<double?>.Shared.Rent(n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                rentedPrices[i] = (double)(series[i] ?? 0m);
            }

            CalculateDominantPeriod(
                rentedPrices.AsSpan(0, n),
                windowSize,
                minLag,
                maxLag,
                threshold,
                smoothingPeriod,
                defaultPeriod,
                selectionMode,
                fallbackPolicy,
                maxHoldBars,
                rentedDom.AsSpan(0, n),
                rentedRaw.AsSpan(0, n),
                rentedPeak.AsSpan(0, n),
                lag,
                minProminence);

            var domList = new List<decimal?>(n);
            var rawList = new List<decimal?>(n);
            var peakList = new List<decimal?>(n);

            for (int i = 0; i < n; i++)
            {
                domList.Add(rentedDom[i].HasValue ? (decimal)Math.Round(rentedDom[i]!.Value, 8, MidpointRounding.AwayFromZero) : null);
                rawList.Add(rentedRaw[i].HasValue ? (decimal)Math.Round(rentedRaw[i]!.Value, 8, MidpointRounding.AwayFromZero) : null);
                peakList.Add(rentedPeak[i].HasValue ? (decimal)Math.Round(rentedPeak[i]!.Value, 8, MidpointRounding.AwayFromZero) : null);
            }

            return (domList, rawList, peakList);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedPrices);
            ArrayPool<double?>.Shared.Return(rentedDom);
            ArrayPool<double?>.Shared.Return(rentedRaw);
            ArrayPool<double?>.Shared.Return(rentedPeak);
        }
    }
}
