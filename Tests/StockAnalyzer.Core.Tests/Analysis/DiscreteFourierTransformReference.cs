#nullable enable
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Frozen O(N^2) naive discrete Fourier transform — the exact inner loop used by
/// <c>FftSpectrumAnalysis</c> / <c>FftProjectionAnalysis</c> before the FFT migration
/// (Cooley–Tukey radix-2 / Bluestein chirp-z). Kept verbatim as the numeric oracle
/// that the fast implementation is cross-checked against for arbitrary N.
///
/// DO NOT optimize or "modernize" this. Its only value is being a literal transcription
/// of the pre-migration math, so any divergence in the fast engine shows up as a test failure.
/// </summary>
internal static class DiscreteFourierTransformReference
{
    /// <summary>
    /// Forward transform X[k] = Σ_i x[i]·e^(−j·2π·k·i/n) for k = 0 .. n−1.
    /// Matches the pre-migration engine convention:
    /// re += x[i]·cos(2π·k·i/n); im −= x[i]·sin(2π·k·i/n).
    /// </summary>
    public static (double[] Real, double[] Imag) Forward(IReadOnlyList<double> samples)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));

        int n = samples.Count;
        var real = new double[n];
        var imag = new double[n];

        for (int k = 0; k < n; k++)
        {
            double re = 0.0;
            double im = 0.0;
            double angleStep = 2.0 * Math.PI * k / n;

            for (int i = 0; i < n; i++)
            {
                double angle = angleStep * i;
                re += samples[i] * Math.Cos(angle);
                im -= samples[i] * Math.Sin(angle);
            }

            real[k] = re;
            imag[k] = im;
        }

        return (real, imag);
    }

    /// <summary>
    /// Frozen O(N^2) naive inverse transform x[n] = (1/N)·Σ_k X[k]·e^(+j·2π·k·n/N) for n = 0 .. N−1.
    /// The numeric oracle for <c>FourierTransform.Inverse</c>. DO NOT optimize.
    /// </summary>
    public static (double[] Real, double[] Imag) Inverse(IReadOnlyList<double> real, IReadOnlyList<double> imag)
    {
        if (real == null) throw new ArgumentNullException(nameof(real));
        if (imag == null) throw new ArgumentNullException(nameof(imag));
        if (real.Count != imag.Count) throw new ArgumentException("real and imag must be the same length.");

        int n = real.Count;
        var outReal = new double[n];
        var outImag = new double[n];

        for (int nn = 0; nn < n; nn++)
        {
            double re = 0.0;
            double im = 0.0;
            double angleStep = 2.0 * Math.PI * nn / n;

            for (int k = 0; k < n; k++)
            {
                double angle = angleStep * k;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);
                re += real[k] * c - imag[k] * s;
                im += real[k] * s + imag[k] * c;
            }

            outReal[nn] = re / n;
            outImag[nn] = im / n;
        }

        return (outReal, outImag);
    }
}
