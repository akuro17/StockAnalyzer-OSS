using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Represents a single frequency/period bin in an FFT spectrum analysis.
/// </summary>
public sealed record FftSpectrumBin(
    int BinIndex,
    double Frequency,
    double Period,
    double Magnitude,
    double Power,
    double NormalizedPower,
    double Phase,
    bool IsDominant);

/// <summary>
/// Encapsulates the results of an FFT spectrum calculation over a discrete price window.
/// </summary>
public sealed class FftSpectrumResult
{
    public static readonly FftSpectrumResult Empty = new(Array.Empty<FftSpectrumBin>(), null, 0, 0, 0);

    public IReadOnlyList<FftSpectrumBin> Bins { get; }
    public FftSpectrumBin? DominantBin { get; }
    public double DominantPeriod => DominantBin?.Period ?? 0;
    public double DominantPower => DominantBin?.Power ?? 0;
    public int SampleCount { get; }

    public FftSpectrumResult(
        IReadOnlyList<FftSpectrumBin> bins,
        FftSpectrumBin? dominantBin,
        double dominantPeriod,
        double dominantPower,
        int sampleCount)
    {
        Bins = bins;
        DominantBin = dominantBin;
        SampleCount = sampleCount;
    }
}

/// <summary>
/// Pure C# mathematical engine for FFT spectrum analysis. The transform is a true Fast Fourier
/// Transform (<see cref="FourierTransform"/>: Cooley-Tukey radix-2 / Bluestein chirp-z),
/// computing harmonic frequency bins, periods, magnitudes, and power distributions in O(n log n).
/// </summary>
public static class FftSpectrumAnalysis
{
    public const int MinSampleCount = 4;

    /// <summary>
    /// Computes the FFT spectrum over a sequence of real price values.
    /// Applies optional linear detrending and Hanning windowing.
    /// </summary>
    public static FftSpectrumResult CalculateSpectrum(
        IReadOnlyList<double> samples,
        bool applyDetrend = true,
        bool applyHanningWindow = true,
        double minPeriod = 2.0,
        double maxPeriod = double.MaxValue)
    {
        if (samples == null || samples.Count < MinSampleCount)
        {
            return FftSpectrumResult.Empty;
        }

        int n = samples.Count;
        double[] processed = new double[n];

        // 1. Detrending (Linear regression fit subtraction)
        if (applyDetrend)
        {
            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumXX = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += samples[i];
                sumXY += (double)i * samples[i];
                sumXX += (double)i * i;
            }

            double denominator = (n * sumXX - sumX * sumX);
            double slope = (Math.Abs(denominator) > 1e-12)
                ? (n * sumXY - sumX * sumY) / denominator
                : 0.0;
            double intercept = (sumY - slope * sumX) / n;

            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i] - (slope * i + intercept);
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                processed[i] = samples[i];
            }
        }

        // 2. Hanning Window: w[i] = 0.5 * (1 - cos(2*PI*i / (N - 1)))
        if (applyHanningWindow && n > 1)
        {
            double factor = 2.0 * Math.PI / (n - 1);
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 * (1.0 - Math.Cos(factor * i));
                processed[i] *= w;
            }
        }

        // 3. Fast Fourier Transform (Cooley-Tukey radix-2 / Bluestein chirp-z) for the real signal.
        //    The full complex spectrum is computed once in O(n log n); bins k = 1..n/2 are then
        //    read out (DC component k=0 excluded), identical to the former O(n^2) direct evaluation.
        int halfN = n / 2;
        var rawBins = new List<(int BinIndex, double Frequency, double Period, double Magnitude, double Power, double Phase)>();

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

                double magnitude = (2.0 / n) * Math.Sqrt(re * re + im * im);
                double power = magnitude * magnitude;
                double phase = Math.Atan2(im, re);

                rawBins.Add((k, freq, period, magnitude, power, phase));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedRe);
            ArrayPool<double>.Shared.Return(rentedIm);
        }

        if (rawBins.Count == 0)
        {
            return new FftSpectrumResult(Array.Empty<FftSpectrumBin>(), null, 0, 0, n);
        }

        // 4. Find dominant peak
        double maxPower = 0;
        int dominantIndex = -1;
        for (int i = 0; i < rawBins.Count; i++)
        {
            if (rawBins[i].Power > maxPower)
            {
                maxPower = rawBins[i].Power;
                dominantIndex = i;
            }
        }

        var resultBins = new List<FftSpectrumBin>(rawBins.Count);
        FftSpectrumBin? dominantBin = null;

        for (int i = 0; i < rawBins.Count; i++)
        {
            var raw = rawBins[i];
            bool isDominant = (i == dominantIndex);
            double normPower = (maxPower > 1e-12) ? (raw.Power / maxPower) : 0.0;

            var bin = new FftSpectrumBin(
                raw.BinIndex,
                raw.Frequency,
                raw.Period,
                raw.Magnitude,
                raw.Power,
                normPower,
                raw.Phase,
                isDominant);

            resultBins.Add(bin);
            if (isDominant)
            {
                dominantBin = bin;
            }
        }

        return new FftSpectrumResult(
            resultBins,
            dominantBin,
            dominantBin?.Period ?? 0,
            dominantBin?.Power ?? 0,
            n);
    }

    /// <summary>
    /// Overload for CoreCandleData sequence with custom price selector.
    /// </summary>
    public static FftSpectrumResult CalculateSpectrum(
        IEnumerable<CoreCandleData> candles,
        Func<CoreCandleData, double> priceSelector,
        bool applyDetrend = true,
        bool applyHanningWindow = true,
        double minPeriod = 2.0,
        double maxPeriod = double.MaxValue)
    {
        if (candles == null) return FftSpectrumResult.Empty;
        var samples = candles.Select(priceSelector).ToList();
        return CalculateSpectrum(samples, applyDetrend, applyHanningWindow, minPeriod, maxPeriod);
    }
}
