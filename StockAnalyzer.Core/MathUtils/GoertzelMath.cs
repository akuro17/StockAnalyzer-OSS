using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Rolling single-bin DFT (Goertzel) amplitude at one target period. Output unit: price-native amplitude
/// (a pure sinusoid of amplitude A at the target period yields ~A). Causal: the value at bar t uses bars t-P+1..t only.
/// Allocates nothing.
/// </summary>
public static class GoertzelMath
{
    /// <summary>Smallest period the single-bin DFT is defined for; smaller requests are clamped to it.</summary>
    public const int MinTargetPeriod = StockAnalyzer.Core.Models.IndicatorDefaultConstants.FourierTransformMinTargetPeriod;

    /// <summary>
    /// For each bar t &gt;= P-1 (P = clamped <paramref name="targetPeriod"/>): removes the mean of the P-bar window, runs the
    /// Goertzel recurrence q0 = 2·cos(2π/P)·q1 − q2 + x for bin k=1 and writes 2·|X1|/P.
    /// Bars before the first full window are <see cref="double.NaN"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="outAmplitude"/> is shorter than <paramref name="samples"/>.</exception>
    public static void CalculateRollingAmplitude(ReadOnlySpan<double> samples, int targetPeriod, Span<double> outAmplitude)
    {
        if (outAmplitude.Length < samples.Length)
        {
            throw new ArgumentException("Output span must be at least as long as the samples.", nameof(outAmplitude));
        }

        int period = Math.Max(targetPeriod, MinTargetPeriod);
        outAmplitude[..samples.Length].Fill(double.NaN);

        double omega = MathConstants.TwoPi / period;
        double cosOmega = Math.Cos(omega);
        double sinOmega = Math.Sin(omega);
        double coefficient = 2.0 * cosOmega;

        for (int t = period - 1; t < samples.Length; t++)
        {
            ReadOnlySpan<double> window = samples.Slice(t - period + 1, period);

            double sum = 0.0;
            for (int j = 0; j < window.Length; j++)
            {
                sum += window[j];
            }
            double mean = sum / period;

            double q1 = 0.0;
            double q2 = 0.0;
            for (int j = 0; j < window.Length; j++)
            {
                double q0 = coefficient * q1 - q2 + (window[j] - mean);
                q2 = q1;
                q1 = q0;
            }

            double real = q1 - q2 * cosOmega;
            double imag = q2 * sinOmega;
            outAmplitude[t] = 2.0 * Math.Sqrt(real * real + imag * imag) / period;
        }
    }
}
