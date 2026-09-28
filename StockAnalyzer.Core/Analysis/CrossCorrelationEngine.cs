using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance, zero-allocation computation engine for rolling cross-correlation analysis (CCF)
/// and dynamic lead-lag detection between two time series.
/// </summary>
public static class CrossCorrelationEngine
{
    public const double VarianceEpsilon = 1e-20;
    public const double SubBinEpsilon = 1e-12;

    /// <summary>
    /// Computes rolling cross-correlation values at a fixed lag using Span buffers.
    /// Operates with zero heap allocations inside the rolling window loop.
    /// </summary>
    /// <param name="x">Primary series X (e.g. current symbol price/returns).</param>
    /// <param name="y">Secondary series Y (e.g. comparison symbol price/returns).</param>
    /// <param name="windowSize">Rolling evaluation window (must be &gt;= 2).</param>
    /// <param name="lag">Integer lag. If &gt; 0, X leads Y by lag bars. If &lt; 0, Y leads X by |lag| bars.</param>
    /// <param name="destination">Output span for correlation values [-1.0, 1.0], matching series length.</param>
    public static void CalculateRollingCcf(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        int windowSize,
        int lag,
        Span<double?> destination)
    {
        int n = Math.Min(x.Length, y.Length);
        if (destination.Length < n)
        {
            throw new ArgumentException("Destination span is shorter than input series.", nameof(destination));
        }

        int absLag = Math.Abs(lag);
        int requiredWarmup = windowSize + absLag - 1;
        int warmupLimit = Math.Min(n, requiredWarmup);

        for (int i = 0; i < warmupLimit; i++)
        {
            destination[i] = null;
        }

        if (n <= requiredWarmup || windowSize < 2) return;

        for (int t = requiredWarmup; t < n; t++)
        {
            // Causal slice selection:
            // If lag >= 0: X leads Y by lag bars (Y lags X). Current Y_t mirrors past X_{t-lag}.
            //              Therefore X is at t - lag, Y is at t.
            // If lag < 0:  Y leads X by |lag| bars (X lags Y, let m = -lag). Current X_t mirrors past Y_{t-m}.
            //              Therefore X is at t, Y is at t - m = t + lag.
            int xEnd = (lag >= 0) ? t - lag : t;
            int yEnd = (lag >= 0) ? t : t + lag;

            ReadOnlySpan<double> u = x.Slice(xEnd - windowSize + 1, windowSize);
            ReadOnlySpan<double> v = y.Slice(yEnd - windowSize + 1, windowSize);

            destination[t] = ComputeCorrelationCoefficient(u, v);
        }
    }

    /// <summary>
    /// Computes optimal lead-lag and peak cross-correlation across a specified lag range [minLag, maxLag].
    /// Employs sign-aware, edge-safe parabolic sub-bin interpolation and optional EMA smoothing.
    /// </summary>
    public static void CalculateOptimalLeadLag(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        int windowSize,
        int minLag,
        int maxLag,
        double threshold,
        CrossCorrelationPeakSelectionMode selectionMode,
        Span<double?> outPeakCorrelation,
        Span<double?> outOptimalLag,
        int smoothingPeriod = 1)
    {
        int n = Math.Min(x.Length, y.Length);
        if (outPeakCorrelation.Length < n || outOptimalLag.Length < n)
        {
            throw new ArgumentException("Output spans are shorter than input series.");
        }

        if (minLag > maxLag)
        {
            (minLag, maxLag) = (maxLag, minLag);
        }

        int maxAbsLag = Math.Max(Math.Abs(minLag), Math.Abs(maxLag));
        int requiredWarmup = windowSize + maxAbsLag - 1;
        int warmupLimit = Math.Min(n, requiredWarmup);

        for (int i = 0; i < warmupLimit; i++)
        {
            outPeakCorrelation[i] = null;
            outOptimalLag[i] = null;
        }

        if (n <= requiredWarmup || windowSize < 2) return;

        int lagCount = maxLag - minLag + 1;
        int scratchRange = lagCount + 2; // Extra bounds for parabolic interpolation
        double[]? pooledR = null;
        Span<double> rValues = scratchRange <= 256
            ? stackalloc double[scratchRange]
            : (pooledR = ArrayPool<double>.Shared.Rent(scratchRange)).AsSpan(0, scratchRange);

        try
        {
            double? prevSmoothedLag = null;
            double alpha = smoothingPeriod > 1 ? 2.0 / (smoothingPeriod + 1.0) : 1.0;

            for (int t = requiredWarmup; t < n; t++)
            {
                int evalMinLag = minLag - 1;
                int evalMaxLag = maxLag + 1;

                for (int k = evalMinLag; k <= evalMaxLag; k++)
                {
                    int arrayIdx = k - evalMinLag;
                    int xEnd = (k >= 0) ? t - k : t;
                    int yEnd = (k >= 0) ? t : t + k;

                    if (xEnd - windowSize + 1 < 0 || yEnd - windowSize + 1 < 0)
                    {
                        rValues[arrayIdx] = double.NaN;
                        continue;
                    }

                    ReadOnlySpan<double> u = x.Slice(xEnd - windowSize + 1, windowSize);
                    ReadOnlySpan<double> v = y.Slice(yEnd - windowSize + 1, windowSize);

                    double? r = ComputeCorrelationCoefficient(u, v);
                    rValues[arrayIdx] = r ?? double.NaN;
                }

                // Peak finding in [minLag, maxLag]
                int bestLag = int.MinValue;
                double bestMetric = double.MinValue;
                double bestR = double.NaN;

                for (int k = minLag; k <= maxLag; k++)
                {
                    int arrayIdx = k - evalMinLag;
                    double rCurr = rValues[arrayIdx];
                    if (double.IsNaN(rCurr)) continue;

                    double metric = selectionMode == CrossCorrelationPeakSelectionMode.MaxAbsoluteCorrelation
                        ? Math.Abs(rCurr)
                        : rCurr;

                    if (metric >= threshold && metric > bestMetric)
                    {
                        bestMetric = metric;
                        bestLag = k;
                        bestR = rCurr;
                    }
                }

                if (bestLag != int.MinValue && !double.IsNaN(bestR))
                {
                    double continuousLag = bestLag;

                    // Parabolic interpolation: only if strictly within interior (not on minLag or maxLag boundaries)
                    if (bestLag > minLag && bestLag < maxLag)
                    {
                        int bestIdx = bestLag - evalMinLag;
                        double rPrev = rValues[bestIdx - 1];
                        double rCurr = rValues[bestIdx];
                        double rNext = rValues[bestIdx + 1];

                        if (!double.IsNaN(rPrev) && !double.IsNaN(rNext))
                        {
                            // Sign-aware metric for parabolic peak (ensures concave-upwards fit for absolute correlation)
                            double sPrev = selectionMode == CrossCorrelationPeakSelectionMode.MaxAbsoluteCorrelation ? Math.Abs(rPrev) : rPrev;
                            double sCurr = selectionMode == CrossCorrelationPeakSelectionMode.MaxAbsoluteCorrelation ? Math.Abs(rCurr) : rCurr;
                            double sNext = selectionMode == CrossCorrelationPeakSelectionMode.MaxAbsoluteCorrelation ? Math.Abs(rNext) : rNext;

                            if (sCurr > sPrev && sCurr >= sNext)
                            {
                                double denom = 2.0 * (2.0 * sCurr - sPrev - sNext);
                                if (denom > SubBinEpsilon)
                                {
                                    double delta = Math.Clamp((sNext - sPrev) / denom, -0.5, 0.5);
                                    continuousLag = Math.Clamp(bestLag + delta, (double)minLag, (double)maxLag);
                                }
                            }
                        }
                    }

                    // EMA smoothing across consecutive valid optimal lag bars
                    double effectiveLag = continuousLag;
                    if (smoothingPeriod > 1)
                    {
                        effectiveLag = prevSmoothedLag.HasValue
                            ? (alpha * continuousLag + (1.0 - alpha) * prevSmoothedLag.Value)
                            : continuousLag;
                        prevSmoothedLag = effectiveLag;
                    }

                    outPeakCorrelation[t] = bestR;
                    outOptimalLag[t] = effectiveLag;
                }
                else
                {
                    outPeakCorrelation[t] = null;
                    outOptimalLag[t] = null;
                    prevSmoothedLag = null;
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

    /// <summary>
    /// Decimal list overload for fixed-lag rolling cross-correlation.
    /// Converts nullable decimal series to pooled double spans and converts results back to decimal?.
    /// </summary>
    public static List<decimal?> CalculateRollingCcf(
        IReadOnlyList<decimal?> seriesX,
        IReadOnlyList<decimal?> seriesY,
        int windowSize,
        int lag)
    {
        if (seriesX == null || seriesY == null || seriesX.Count == 0 || seriesY.Count == 0)
        {
            return new List<decimal?>();
        }

        int n = Math.Min(seriesX.Count, seriesY.Count);
        double[] rentedX = ArrayPool<double>.Shared.Rent(n);
        double[] rentedY = ArrayPool<double>.Shared.Rent(n);
        double?[] rentedResult = ArrayPool<double?>.Shared.Rent(n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                rentedX[i] = seriesX[i].HasValue ? (double)seriesX[i]!.Value : double.NaN;
                rentedY[i] = seriesY[i].HasValue ? (double)seriesY[i]!.Value : double.NaN;
            }

            CalculateRollingCcf(
                rentedX.AsSpan(0, n),
                rentedY.AsSpan(0, n),
                windowSize,
                lag,
                rentedResult.AsSpan(0, n));

            var result = new List<decimal?>(seriesX.Count);
            for (int i = 0; i < n; i++)
            {
                result.Add(rentedResult[i].HasValue
                    ? (decimal)Math.Round(rentedResult[i]!.Value, 8, MidpointRounding.AwayFromZero)
                    : null);
            }
            for (int i = n; i < seriesX.Count; i++)
            {
                result.Add(null);
            }

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedX);
            ArrayPool<double>.Shared.Return(rentedY);
            ArrayPool<double?>.Shared.Return(rentedResult);
        }
    }

    /// <summary>
    /// Decimal list overload for optimal lead-lag and peak cross-correlation with optional smoothing.
    /// </summary>
    public static (List<decimal?> PeakCorrelation, List<decimal?> OptimalLag) CalculateOptimalLeadLag(
        IReadOnlyList<decimal?> seriesX,
        IReadOnlyList<decimal?> seriesY,
        int windowSize,
        int minLag,
        int maxLag,
        double threshold,
        CrossCorrelationPeakSelectionMode selectionMode,
        int smoothingPeriod = 1)
    {
        if (seriesX == null || seriesY == null || seriesX.Count == 0 || seriesY.Count == 0)
        {
            return (new List<decimal?>(), new List<decimal?>());
        }

        int n = Math.Min(seriesX.Count, seriesY.Count);
        double[] rentedX = ArrayPool<double>.Shared.Rent(n);
        double[] rentedY = ArrayPool<double>.Shared.Rent(n);
        double?[] rentedPeak = ArrayPool<double?>.Shared.Rent(n);
        double?[] rentedLag = ArrayPool<double?>.Shared.Rent(n);

        try
        {
            for (int i = 0; i < n; i++)
            {
                rentedX[i] = seriesX[i].HasValue ? (double)seriesX[i]!.Value : double.NaN;
                rentedY[i] = seriesY[i].HasValue ? (double)seriesY[i]!.Value : double.NaN;
            }

            CalculateOptimalLeadLag(
                rentedX.AsSpan(0, n),
                rentedY.AsSpan(0, n),
                windowSize,
                minLag,
                maxLag,
                threshold,
                selectionMode,
                rentedPeak.AsSpan(0, n),
                rentedLag.AsSpan(0, n),
                smoothingPeriod);

            var outPeak = new List<decimal?>(seriesX.Count);
            var outLag = new List<decimal?>(seriesX.Count);

            for (int i = 0; i < n; i++)
            {
                outPeak.Add(rentedPeak[i].HasValue
                    ? (decimal)Math.Round(rentedPeak[i]!.Value, 8, MidpointRounding.AwayFromZero)
                    : null);
                outLag.Add(rentedLag[i].HasValue
                    ? (decimal)Math.Round(rentedLag[i]!.Value, 4, MidpointRounding.AwayFromZero)
                    : null);
            }
            for (int i = n; i < seriesX.Count; i++)
            {
                outPeak.Add(null);
                outLag.Add(null);
            }

            return (outPeak, outLag);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedX);
            ArrayPool<double>.Shared.Return(rentedY);
            ArrayPool<double?>.Shared.Return(rentedPeak);
            ArrayPool<double?>.Shared.Return(rentedLag);
        }
    }

    /// <summary>
    /// Converts a volume series [V_0, V_1, ..., V_{N-1}] into a zero-safe log difference series
    /// delta_ln = ln(1 + V_t) - ln(1 + V_{t-1}). Index 0 is always null.
    /// Safely handles zero volumes without generating -Infinity or NaN.
    /// </summary>
    public static List<decimal?> ConvertVolumeToLogDifferences(IReadOnlyList<decimal?> volumes)
    {
        if (volumes == null || volumes.Count == 0) return new List<decimal?>();

        var result = new List<decimal?>(volumes.Count) { null };

        for (int i = 1; i < volumes.Count; i++)
        {
            var curr = volumes[i];
            var prev = volumes[i - 1];

            if (curr.HasValue && prev.HasValue && curr.Value >= 0m && prev.Value >= 0m)
            {
                double c = (double)curr.Value;
                double p = (double)prev.Value;
                double diff = Math.Log(1.0 + c) - Math.Log(1.0 + p);
                if (double.IsNaN(diff) || double.IsInfinity(diff))
                {
                    result.Add(null);
                }
                else
                {
                    result.Add((decimal)diff);
                }
            }
            else
            {
                result.Add(null);
            }
        }

        return result;
    }

    private static double? ComputeCorrelationCoefficient(ReadOnlySpan<double> u, ReadOnlySpan<double> v)
    {
        int len = u.Length;
        double sumU = 0.0;
        double sumV = 0.0;
        for (int j = 0; j < len; j++)
        {
            sumU += u[j];
            sumV += v[j];
        }
        double meanU = sumU / len;
        double meanV = sumV / len;

        double sumUU = 0.0;
        double sumVV = 0.0;
        double sumUV = 0.0;
        for (int j = 0; j < len; j++)
        {
            double du = u[j] - meanU;
            double dv = v[j] - meanV;
            sumUU += du * du;
            sumVV += dv * dv;
            sumUV += du * dv;
        }

        if (sumUU <= VarianceEpsilon || sumVV <= VarianceEpsilon)
        {
            return null;
        }

        double denom = Math.Sqrt(sumUU * sumVV);
        if (denom <= 0.0 || double.IsNaN(denom) || double.IsInfinity(denom))
        {
            return null;
        }

        double r = sumUV / denom;
        if (double.IsNaN(r) || double.IsInfinity(r))
        {
            return null;
        }

        return Math.Clamp(r, -1.0, 1.0);
    }
}
