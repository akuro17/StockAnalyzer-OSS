using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Represents a single harmonic frequency component extracted from Fourier Transform analysis.
/// </summary>
public sealed record FftHarmonicComponent(
    int BinIndex,
    double Frequency,
    double Period,
    double Magnitude,
    double Power,
    double Phase);

/// <summary>
/// Encapsulates the results of an FFT future trajectory projection calculation.
/// </summary>
public sealed class FftProjectionResult
{
    public static readonly FftProjectionResult Empty = new(
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<FftHarmonicComponent>(),
        0.0, 0.0, 0.0, 0);

    public IReadOnlyList<Point> ProjectedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public IReadOnlyList<FftHarmonicComponent> DominantHarmonics { get; }
    public double ResidualStdDev { get; }
    public double Slope { get; }
    public double Intercept { get; }
    public int SampleCount { get; }

    public FftProjectionResult(
        IReadOnlyList<Point> projectedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        IReadOnlyList<FftHarmonicComponent> dominantHarmonics,
        double residualStdDev,
        double slope,
        double intercept,
        int sampleCount)
    {
        ProjectedPoints = projectedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        DominantHarmonics = dominantHarmonics;
        ResidualStdDev = residualStdDev;
        Slope = slope;
        Intercept = intercept;
        SampleCount = sampleCount;
    }
}

/// <summary>
/// Pure C# mathematical engine for FFT-based future trajectory projection. The transform is a true
/// Fast Fourier Transform (<see cref="FourierTransform"/>: Cooley-Tukey radix-2 / Bluestein chirp-z).
/// Decomposes in-sample price series into linear trend and dominant harmonic components,
/// then extrapolates the reconstructed waveform and confidence cone forward into future coordinates.
/// </summary>
public static class FftProjectionAnalysis
{
    public const int MinSampleCount = 4;

    /// <summary>
    /// Computes the Fourier harmonic decomposition and projects the trajectory forward into future steps.
    /// </summary>
    public static FftProjectionResult CalculateProjection(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int futureSteps = 20,
        TimeSpan timeframeSpan = default,
        int harmonicCount = 3,
        bool applyDetrend = true,
        double minPeriod = 3.0,
        double maxPeriod = double.MaxValue,
        bool showConfidenceBand = true,
        decimal confidenceMultiplier = 2.0m)
    {
        if (samples == null || timestamps == null || samples.Count < MinSampleCount || samples.Count != timestamps.Count)
        {
            return FftProjectionResult.Empty;
        }

        int n = samples.Count;
        double[] processed = new double[n];
        double slope = 0.0;
        double intercept = 0.0;

        // 1. Endpoint Matching Detrending (or Mean Subtraction if detrend is disabled)
        if (applyDetrend)
        {
            // Endpoint Matching Detrending: Trend(i) = y[0] + ((y[n-1] - y[0]) / (n - 1)) * i
            // Guarantees x[0] = 0 and x[n-1] = 0, eliminating boundary discontinuities and suppressing Gibbs phenomenon/spectral leakage.
            slope = (n > 1) ? (samples[n - 1] - samples[0]) / (n - 1) : 0.0;
            intercept = samples[0];

            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i] - (slope * i + intercept);
            }
        }
        else
        {
            double sumY = 0;
            for (int i = 0; i < n; i++)
            {
                sumY += samples[i];
            }
            intercept = sumY / n;
            slope = 0.0;

            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i] - intercept;
            }
        }

        // 2. Fast Fourier Transform (Cooley-Tukey radix-2 / Bluestein chirp-z) of the real signal.
        //    The full complex spectrum is computed once in O(n log n); harmonic bins k = 1..n/2 are
        //    then read out, identical to the former O(n^2) direct evaluation.
        int halfN = n / 2;
        var rawComponents = new List<FftHarmonicComponent>();

        double[] rentedRe = ArrayPool<double>.Shared.Rent(n);
        double[] rentedIm = ArrayPool<double>.Shared.Rent(n);
        try
        {
            Span<double> spectrumRe = rentedRe.AsSpan(0, n);
            Span<double> spectrumIm = rentedIm.AsSpan(0, n);
            FourierTransform.Forward(processed, spectrumRe, spectrumIm);

            for (int k = 1; k <= halfN; k++)
            {
                double freq = (double)k / n;
                double period = (double)n / k;

                if (period < minPeriod || period > maxPeriod)
                {
                    continue;
                }

                double re = spectrumRe[k];
                double im = spectrumIm[k];

                // Nyquist frequency (k = n/2 when n is even) does not have a symmetric negative frequency counterpart,
                // so its one-sided spectral magnitude scale factor is 1/N rather than 2/N.
                double scaleFactor = (n % 2 == 0 && k == halfN) ? (1.0 / n) : (2.0 / n);
                double magnitude = scaleFactor * Math.Sqrt(re * re + im * im);
                double power = magnitude * magnitude;
                double phase = Math.Atan2(im, re);

                rawComponents.Add(new FftHarmonicComponent(k, freq, period, magnitude, power, phase));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedRe);
            ArrayPool<double>.Shared.Return(rentedIm);
        }

        // 3. Select Top-K Dominant Harmonics with Non-Maximum Suppression (NMS)
        // Prevents adjacent spectral leakage bins (e.g. 20b and 21b) from monopolizing multiple slots.
        int kCount = Math.Max(1, Math.Min(harmonicCount, 10));
        var sortedByPower = rawComponents.OrderByDescending(c => c.Power).ToList();
        var dominantHarmonics = new List<FftHarmonicComponent>(kCount);

        foreach (var candidate in sortedByPower)
        {
            if (dominantHarmonics.Count >= kCount) break;

            // Check if candidate frequency is sufficiently separated (>= 15% ratio difference) from already selected components
            bool isTooClose = dominantHarmonics.Any(existing =>
            {
                double ratio = candidate.Frequency / existing.Frequency;
                return ratio >= 0.85 && ratio <= 1.15;
            });

            if (!isTooClose)
            {
                dominantHarmonics.Add(candidate);
            }
        }

        // If NMS was too strict to fill kCount, fill remaining slots with highest available power
        if (dominantHarmonics.Count < kCount)
        {
            foreach (var candidate in sortedByPower)
            {
                if (dominantHarmonics.Count >= kCount) break;
                if (!dominantHarmonics.Contains(candidate))
                {
                    dominantHarmonics.Add(candidate);
                }
            }
        }

        // 4. In-Sample Residual Variance Calculation & Endpoint Residual Offset
        double sumSqResiduals = 0.0;
        double endModelVal = slope * (n - 1) + intercept;
        foreach (var h in dominantHarmonics)
        {
            endModelVal += h.Magnitude * Math.Cos(2.0 * Math.PI * h.Frequency * (n - 1) + h.Phase);
        }
        double endResidualDelta = samples[n - 1] - endModelVal;

        for (int i = 0; i < n; i++)
        {
            double modelVal = slope * i + intercept;
            foreach (var h in dominantHarmonics)
            {
                modelVal += h.Magnitude * Math.Cos(2.0 * Math.PI * h.Frequency * i + h.Phase);
            }

            double diff = samples[i] - modelVal;
            sumSqResiduals += diff * diff;
        }

        double residualStdDev = Math.Sqrt(sumSqResiduals / n);
        double minStdDev = Math.Max(0.01, Math.Abs(samples[^1]) * 0.005);
        if (residualStdDev < minStdDev)
        {
            residualStdDev = minStdDev;
        }

        // 5. Future Extrapolation, Continuity Blending & Uncertainty Cone Diffusion
        int steps = Math.Clamp(futureSteps, 1, 100);
        if (timeframeSpan <= TimeSpan.Zero)
        {
            if (timestamps.Count >= 2)
            {
                double avgMs = (timestamps[^1] - timestamps[0]).TotalMilliseconds / (timestamps.Count - 1);
                if (avgMs > 0)
                {
                    timeframeSpan = TimeSpan.FromMilliseconds(avgMs);
                }
            }
            if (timeframeSpan <= TimeSpan.Zero)
            {
                timeframeSpan = TimeSpan.FromDays(1);
            }
        }

        var projectedPoints = new List<Point>(steps + 1);
        var upperPoints = new List<Point>(steps + 1);
        var lowerPoints = new List<Point>(steps + 1);

        // First point connects from the last candle
        var lastTime = timestamps[^1];
        var lastPrice = samples[^1];
        var initialPoint = new Point((double)lastTime.Ticks, lastPrice);
        projectedPoints.Add(initialPoint);
        upperPoints.Add(initialPoint);
        lowerPoints.Add(initialPoint);

        double multiplier = Math.Max(0.0, (double)confidenceMultiplier);
        const double ContinuityDecay = 0.85; // Exponential decay for smooth C0 continuity transition

        for (int m = 1; m <= steps; m++)
        {
            int futureIndex = (n - 1) + m;
            DateTime futureTime = lastTime + (timeframeSpan * m);

            double projectedPrice = slope * futureIndex + intercept;
            foreach (var h in dominantHarmonics)
            {
                projectedPrice += h.Magnitude * Math.Cos(2.0 * Math.PI * h.Frequency * futureIndex + h.Phase);
            }

            // Continuity blending: add decaying delta from last candle residual to eliminate jump
            projectedPrice += endResidualDelta * Math.Pow(ContinuityDecay, m);

            // Square-root-of-time uncertainty cone diffusion: sqrt(1 + 0.5 * m)
            double margin = showConfidenceBand
                ? multiplier * residualStdDev * Math.Sqrt(1.0 + 0.5 * m)
                : 0.0;

            double upperPrice = projectedPrice + margin;
            double lowerPrice = projectedPrice - margin;

            projectedPoints.Add(new Point((double)futureTime.Ticks, projectedPrice));
            upperPoints.Add(new Point((double)futureTime.Ticks, upperPrice));
            lowerPoints.Add(new Point((double)futureTime.Ticks, lowerPrice));
        }

        return new FftProjectionResult(
            projectedPoints,
            upperPoints,
            lowerPoints,
            dominantHarmonics,
            residualStdDev,
            slope,
            intercept,
            n);
    }
}