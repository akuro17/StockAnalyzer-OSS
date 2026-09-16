using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Rolling causal FFT band-pass filter. For each position it transforms the trailing window,
/// auto-detects the dominant (highest-magnitude) non-DC frequency bin, keeps only that bin and
/// <paramref name="bandWidthBins"/> neighboring bins on each side (together with their conjugate
/// mirrors; DC is always excluded), inverts the transform, and emits the last reconstructed
/// sample -- a causal, self-tuning extraction of whatever cycle currently dominates the window.
///
/// "Causal" means <c>Trend[i]</c> depends only on samples up to and including <c>i</c>, so the
/// series never repaints as new bars arrive. Built on <see cref="FourierTransform"/>; no window
/// function and no detrending are applied (the band-pass mask itself excludes DC, so no separate
/// detrend step is needed).
/// </summary>
public static class FftBandPassFilter
{
    /// <summary>Smallest usable rolling window; matches <see cref="FftLowPassFilter"/>.</summary>
    private const int MinWindowSize = 4;

    /// <summary>Reusable length-<c>w</c> scratch spans: segment, specRe, specIm, filtRe, filtIm, recRe, recIm.</summary>
    private const int ScratchSpanCount = 7;

    /// <summary>
    /// Fills <paramref name="outTrend"/> with the rolling causal band-pass reconstruction of
    /// <paramref name="samples"/>. Warm-up positions (<c>[0 .. w-2]</c>) and every position when
    /// <c>samples.Length &lt; w</c> are set to <see cref="double.NaN"/>.
    /// </summary>
    /// <param name="samples">Input series (e.g. median price).</param>
    /// <param name="windowSize">Rolling window length; values below <see cref="MinWindowSize"/> are raised to it.</param>
    /// <param name="bandWidthBins">Bins kept on each side of the auto-detected dominant bin; clamped to <c>[0, w/2]</c>.</param>
    /// <param name="outTrend">Destination, length ≥ <paramref name="samples"/> length.</param>
    public static void RollingCausalTrend(ReadOnlySpan<double> samples, int windowSize, int bandWidthBins, Span<double> outTrend)
    {
        int n = samples.Length;
        if (outTrend.Length < n)
        {
            throw new ArgumentException("outTrend must be at least as long as samples.");
        }

        int w = Math.Max(MinWindowSize, windowSize);
        int half = w / 2;
        int bandWidth = Math.Clamp(bandWidthBins, 0, half);

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

                // Auto-detect the dominant non-DC bin: max |X_k|^2 over k = 1 .. half.
                int peakBin = 1;
                double peakMagSq = (specRe[1] * specRe[1]) + (specIm[1] * specIm[1]);
                for (int k = 2; k <= half; k++)
                {
                    double magSq = (specRe[k] * specRe[k]) + (specIm[k] * specIm[k]);
                    if (magSq > peakMagSq)
                    {
                        peakMagSq = magSq;
                        peakBin = k;
                    }
                }

                int lowBin = Math.Max(1, peakBin - bandWidth);
                int highBin = Math.Min(half, peakBin + bandWidth);

                // Keep bins [lowBin, highBin] and their conjugate mirrors; DC (bin 0) and
                // everything outside the band stay zero. Mirroring by conj(spec[k]) (not
                // spec[w-k]) reproduces numpy irfft's enforced Hermitian symmetry exactly, so
                // the inverse transform is real -- same technique as FftLowPassFilter.
                filtRe.Clear();
                filtIm.Clear();

                for (int k = lowBin; k <= highBin; k++)
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
