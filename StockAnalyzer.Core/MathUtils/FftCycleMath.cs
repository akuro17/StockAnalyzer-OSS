using System;
using System.Buffers;
using System.Numerics;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Rolling dominant-cycle estimate from a Hann-windowed FFT (reuses <see cref="FourierTransform"/> unchanged).
/// Units: cycle = period in bars; strength = dimensionless ratio (peak magnitude / mean magnitude of bins 1..W/2);
/// oscillator = price-native amplitude of the dominant bin projected on its phase. Causal: bar t uses bars t-W+1..t only.
/// </summary>
public static class FftCycleMath
{
    /// <summary>Smallest FFT window the dominant-bin search is defined for (bins 0..W/2 must contain a non-DC bin beyond k=1).</summary>
    public const int MinWindowSize = StockAnalyzer.Core.Models.IndicatorDefaultConstants.FftCycleMinWindowSize;

    /// <summary>Rounds to the nearest power of two (an exact tie rounds down); values below 1 give 1.</summary>
    public static int NearestPowerOfTwo(int value)
    {
        if (value < 1)
        {
            return 1;
        }

        long lower = 1L << BitOperations.Log2((uint)value);
        long upper = lower << 1;
        long nearest = (value - lower) <= (upper - value) ? lower : upper;
        return (int)Math.Min(nearest, 1 << 30);
    }

    /// <summary>
    /// For each bar t &gt;= W-1 (W = <see cref="NearestPowerOfTwo"/>(<paramref name="requestedWindowSize"/>)): removes the window mean,
    /// applies a symmetric Hann window (0.5 − 0.5·cos(2πn/(W−1))), takes the FFT and picks the largest-magnitude bin k in 1..W/2
    /// (first one on ties). Writes cycle = W/k, strength = |X[k]| / mean(|X[1..W/2]|) (NaN when that mean is 0) and
    /// oscillator = Re(X[k]) / (W/2). Every output is <see cref="double.NaN"/> before the first full window, and for the whole
    /// series when the series is shorter than W or W &lt; <see cref="MinWindowSize"/>.
    /// </summary>
    /// <exception cref="ArgumentException">An output span is shorter than <paramref name="samples"/>.</exception>
    public static void CalculateRollingFftCycle(
        ReadOnlySpan<double> samples,
        int requestedWindowSize,
        Span<double> outCycle,
        Span<double> outStrength,
        Span<double> outOscillator)
    {
        if (outCycle.Length < samples.Length || outStrength.Length < samples.Length || outOscillator.Length < samples.Length)
        {
            throw new ArgumentException("Output spans must be at least as long as the samples.");
        }

        outCycle[..samples.Length].Fill(double.NaN);
        outStrength[..samples.Length].Fill(double.NaN);
        outOscillator[..samples.Length].Fill(double.NaN);

        int window = NearestPowerOfTwo(requestedWindowSize);
        if (window < MinWindowSize || samples.Length < window)
        {
            return;
        }

        int scratchLength = 4 * window;
        double[]? rented = scratchLength > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(scratchLength) : null;
        Span<double> scratch = rented != null ? rented.AsSpan(0, scratchLength) : stackalloc double[scratchLength];

        try
        {
            Span<double> hann = scratch[..window];
            Span<double> windowed = scratch.Slice(window, window);
            Span<double> real = scratch.Slice(2 * window, window);
            Span<double> imagOrMagnitude = scratch.Slice(3 * window, window);

            for (int n = 0; n < window; n++)
            {
                hann[n] = 0.5 - 0.5 * Math.Cos(MathConstants.TwoPi * n / (window - 1));
            }

            int binCount = window / 2 + 1;
            double halfWindow = window / 2.0;

            for (int t = window - 1; t < samples.Length; t++)
            {
                ReadOnlySpan<double> segment = samples.Slice(t - window + 1, window);

                double sum = 0.0;
                for (int j = 0; j < window; j++)
                {
                    sum += segment[j];
                }
                double mean = sum / window;

                for (int j = 0; j < window; j++)
                {
                    windowed[j] = (segment[j] - mean) * hann[j];
                }

                FourierTransform.Forward(windowed, real, imagOrMagnitude);

                // Magnitudes replace the imaginary parts in place (real parts are kept for the oscillator).
                int peakBin = 1;
                double peakMagnitude = -1.0;
                double magnitudeSum = 0.0;
                for (int k = 1; k < binCount; k++)
                {
                    double magnitude = Math.Sqrt(real[k] * real[k] + imagOrMagnitude[k] * imagOrMagnitude[k]);
                    imagOrMagnitude[k] = magnitude;
                    magnitudeSum += magnitude;
                    if (magnitude > peakMagnitude)
                    {
                        peakMagnitude = magnitude;
                        peakBin = k;
                    }
                }

                double meanMagnitude = magnitudeSum / (binCount - 1);
                outCycle[t] = (double)window / peakBin;
                outStrength[t] = meanMagnitude > 0.0 ? peakMagnitude / meanMagnitude : double.NaN;
                outOscillator[t] = real[peakBin] / halfWindow;
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }
}
