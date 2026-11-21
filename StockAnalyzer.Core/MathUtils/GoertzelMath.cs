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

    /// <summary>The period at which bin k=1 is the Nyquist bin (k = P/2): a one-sided spectrum does not double it.</summary>
    private const int NyquistPeriod = 2;

    /// <summary>One-sided spectrum factor of an ordinary positive-frequency bin (the negative-frequency mirror image doubles the amplitude).</summary>
    private const double PositiveFrequencyFactor = 2.0;

    /// <summary>One-sided spectrum factor of the Nyquist bin, which is its own mirror image.</summary>
    private const double NyquistFactor = 1.0;

    /// <summary>
    /// For each bar t &gt;= P-1 (P = clamped <paramref name="targetPeriod"/>): removes the mean of the P-bar window, runs the
    /// Goertzel recurrence q0 = 2·cos(2π/P)·q1 − q2 + x for bin k=1 and writes the one-sided amplitude c·|X1|/P (c = 2; c = 1 for P = 2, where bin 1 is the Nyquist bin and has no mirror image).
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
        double singleSidedFactor = period == NyquistPeriod ? NyquistFactor : PositiveFrequencyFactor;

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
            outAmplitude[t] = singleSidedFactor * Math.Sqrt(real * real + imag * imag) / period;
        }
    }
}
