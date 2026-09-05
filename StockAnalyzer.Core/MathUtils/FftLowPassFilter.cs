using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Rolling causal FFT low-pass filter. For each position it transforms the trailing window,
/// keeps only the lowest <c>numHarmonics</c> frequency bins (DC included) together with their
/// conjugate mirrors, inverts the transform, and emits the last reconstructed sample — the
/// pure-C# equivalent of <c>numpy.fft.irfft(mask(numpy.fft.rfft(segment)), n=window)[-1]</c>.
///
/// "Causal" means <c>Trend[i]</c> depends only on samples up to and including <c>i</c>, so the
/// series never repaints as new bars arrive. Built on <see cref="FourierTransform"/>; no window
/// function and no detrending are applied (the DC/trend component must survive the filter).
/// </summary>
public static class FftLowPassFilter
{
    /// <summary>Smallest usable rolling window; matches the Python reference clamp.</summary>
    private const int MinWindowSize = 4;

    /// <summary>Number of reusable length-<c>w</c> scratch spans carved from the pooled buffer.</summary>
    private const int ScratchSpanCount = 7;

    /// <summary>
    /// Fills <paramref name="outTrend"/> with the rolling causal low-pass reconstruction of
    /// <paramref name="samples"/>. Warm-up positions (<c>[0 .. w-2]</c>) and every position when
    /// <c>samples.Length &lt; w</c> are set to <see cref="double.NaN"/>.
    /// </summary>
    /// <param name="samples">Input series (e.g. median price).</param>
    /// <param name="windowSize">Rolling window length; values below <see cref="MinWindowSize"/> are raised to it.</param>
    /// <param name="numHarmonics">Low-frequency bins to keep (DC included); clamped to <c>[1, w/2 + 1]</c>.</param>
    /// <param name="outTrend">Destination, length ≥ <paramref name="samples"/> length.</param>
    public static void RollingCausalTrend(ReadOnlySpan<double> samples, int windowSize, int numHarmonics, Span<double> outTrend)
    {
        int n = samples.Length;
        if (outTrend.Length < n)
        {
            throw new ArgumentException("outTrend must be at least as long as samples.");
        }

        int w = Math.Max(MinWindowSize, windowSize);
        int keptBins = Math.Clamp(numHarmonics, 1, (w / 2) + 1);

        for (int i = 0; i < n; i++)
        {
            outTrend[i] = double.NaN;
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
            Span<double> filtRe = block.Slice(3 * w, w);
            Span<double> filtIm = block.Slice(4 * w, w);
            Span<double> recRe = block.Slice(5 * w, w);
            Span<double> recIm = block.Slice(6 * w, w);

            for (int i = w - 1; i < n; i++)
            {
                samples.Slice(i - w + 1, w).CopyTo(segment);

                // Full complex spectrum of the raw window.
                FourierTransform.Forward(segment, specRe, specIm);

                // Keep bins 0 .. keptBins-1 and their conjugate mirrors; zero everything else.
                // Mirroring by conj(spec[k]) (not spec[w-k]) reproduces numpy irfft's enforced
                // Hermitian symmetry exactly, so the inverse transform is real.
                filtRe.Clear();
                filtIm.Clear();

                filtRe[0] = specRe[0];
                filtIm[0] = 0.0;

                for (int k = 1; k < keptBins; k++)
                {
                    int mirror = w - k;
                    if (mirror == k)
                    {
                        // Nyquist bin (even w): single, real.
                        filtRe[k] = specRe[k];
                        filtIm[k] = 0.0;
                    }
                    else
                    {
                        filtRe[k] = specRe[k];
                        filtIm[k] = specIm[k];
                        filtRe[mirror] = specRe[k];
                        filtIm[mirror] = -specIm[k];
                    }
                }

                FourierTransform.Inverse(filtRe, filtIm, recRe, recIm);

                // Causal: only the last reconstructed sample is emitted.
                outTrend[i] = recRe[w - 1];
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(pool);
        }
    }
}
