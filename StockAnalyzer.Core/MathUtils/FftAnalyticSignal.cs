using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Rolling causal FFT analytic-signal filter. For each bar it transforms the trailing window,
/// suppresses the negative-frequency half of the spectrum (and doubles the positive half),
/// inverts the transform, and emits the instantaneous phase and amplitude of the last
/// reconstructed sample — the pure-C# equivalent of
/// <c>z = numpy.fft.ifft(H(numpy.fft.fft(segment)))</c> where <c>H</c> is the Hilbert mask
/// <c>[X0, 2·X1 .. 2·X(N/2-1), X(N/2), 0 .. 0]</c>, followed by
/// <c>phase = numpy.angle(z[-1])</c>, <c>envelope = numpy.abs(z[-1])</c>.
///
/// The complex result is the analytic signal <c>z[n] = x[n] + i·H{x}[n]</c>, whose modulus is
/// the instantaneous amplitude (envelope) and whose argument is the instantaneous phase.
///
/// "Causal" means the output at bar <c>i</c> depends only on samples up to and including
/// <c>i</c>, so the series never repaints as new bars arrive. Built on
/// <see cref="FourierTransform"/> (<see cref="FourierTransform.Forward"/> /
/// <see cref="FourierTransform.Inverse"/> are reused unchanged). No window function and no
/// detrending are applied.
///
/// Sign convention: <see cref="FourierTransform"/> uses forward <c>e^(−i…)</c> and inverse
/// <c>(1/N)·e^(+i…)</c>, so the negative frequencies are the upper spectrum half
/// <c>k ∈ [N/2+1, N−1]</c>; those bins are the ones zeroed here.
/// </summary>
public static class FftAnalyticSignal
{
    /// <summary>Smallest usable rolling window; matches <see cref="FftLowPassFilter"/>.</summary>
    private const int MinWindowSize = 4;

    /// <summary>Reusable length-<c>w</c> scratch spans: segment, specRe, specIm, hRe, hIm, recRe, recIm.</summary>
    private const int ScratchSpanCount = 7;

    /// <summary>
    /// Fills <paramref name="outPhaseRad"/> (instantaneous phase, radians in (−π, π]) and
    /// <paramref name="outEnvelope"/> (instantaneous amplitude, same unit as the samples) with
    /// the rolling causal analytic-signal reconstruction of <paramref name="samples"/>.
    /// Warm-up positions (<c>[0 .. w-2]</c>) and every position when
    /// <c>samples.Length &lt; w</c> are set to <see cref="double.NaN"/>.
    /// </summary>
    /// <param name="samples">Input series (e.g. median price).</param>
    /// <param name="windowSize">Rolling window length; values below <see cref="MinWindowSize"/> are raised to it.</param>
    /// <param name="outPhaseRad">Destination for the instantaneous phase, length ≥ <paramref name="samples"/> length.</param>
    /// <param name="outEnvelope">Destination for the instantaneous amplitude, length ≥ <paramref name="samples"/> length.</param>
    public static void RollingCausalAnalyticSignal(
        ReadOnlySpan<double> samples,
        int windowSize,
        Span<double> outPhaseRad,
        Span<double> outEnvelope)
    {
        int n = samples.Length;
        if (outPhaseRad.Length < n)
        {
            throw new ArgumentException("outPhaseRad must be at least as long as samples.");
        }

        if (outEnvelope.Length < n)
        {
            throw new ArgumentException("outEnvelope must be at least as long as samples.");
        }

        int w = Math.Max(MinWindowSize, windowSize);

        for (int i = 0; i < n; i++)
        {
            outPhaseRad[i] = double.NaN;
            outEnvelope[i] = double.NaN;
        }

        if (n < w)
        {
            return;
        }

        double[] pool = ArrayPool<double>.Shared.Rent(w * ScratchSpanCount);
        try
        {
            Span<double> block = pool;
            Span<double> segment = block.Slice(0, w);
            Span<double> specRe = block.Slice(w, w);
            Span<double> specIm = block.Slice(2 * w, w);
            Span<double> hRe = block.Slice(3 * w, w);
            Span<double> hIm = block.Slice(4 * w, w);
            Span<double> recRe = block.Slice(5 * w, w);
            Span<double> recIm = block.Slice(6 * w, w);

            int half = w / 2;
            bool evenWindow = (w & 1) == 0;

            for (int i = w - 1; i < n; i++)
            {
                samples.Slice(i - w + 1, w).CopyTo(segment);

                // Full complex spectrum of the raw window.
                FourierTransform.Forward(segment, specRe, specIm);

                // Hilbert mask: keep DC, double the positive frequencies, keep Nyquist (even w),
                // zero the negative-frequency half. The inverse of this is the analytic signal.
                hRe.Clear();
                hIm.Clear();

                hRe[0] = specRe[0];
                hIm[0] = specIm[0];

                // Even w: double bins 1..half-1, then keep the exact Nyquist bin (half)
                // undoubled below. Odd w has no exact Nyquist bin, so bin `half` (== (w-1)/2)
                // is itself the last positive-frequency bin and must also be doubled here --
                // leaving it at the loop's exclusive bound (as for even w) silently zeroes the
                // topmost positive-frequency component of every odd-length window.
                int lastDoubledBin = evenWindow ? half - 1 : half;
                for (int k = 1; k <= lastDoubledBin; k++)
                {
                    hRe[k] = 2.0 * specRe[k];
                    hIm[k] = 2.0 * specIm[k];
                }

                if (evenWindow)
                {
                    hRe[half] = specRe[half];
                    hIm[half] = specIm[half];
                }

                // k = half+1 .. w-1 stay zero (negative frequencies).

                FourierTransform.Inverse(hRe, hIm, recRe, recIm);

                // Causal: only the last reconstructed sample is emitted.
                double re = recRe[w - 1];
                double im = recIm[w - 1];
                outPhaseRad[i] = Math.Atan2(im, re);
                outEnvelope[i] = Math.Sqrt((re * re) + (im * im));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(pool);
        }
    }
}
