using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Pure C# Fast Fourier Transform engine. Computes the exact discrete Fourier transform
/// in O(N log N) for <b>any</b> length N:
/// <list type="bullet">
///   <item>N is a power of two → in-place iterative radix-2 Cooley–Tukey.</item>
///   <item>otherwise → Bluestein's chirp-z algorithm, which wraps a power-of-two FFT and
///   still returns the exact N-point DFT (no zero-padding of the frequency grid).</item>
/// </list>
/// Sign convention:
/// <list type="bullet">
///   <item>Forward: <c>X[k] = Σ_{n=0}^{N−1} x[n]·e^(−i·2π·k·n/N)</c>.</item>
///   <item>Inverse: <c>x[n] = (1/N)·Σ_{k=0}^{N−1} X[k]·e^(+i·2π·k·n/N)</c>, so
///   <c>Inverse(Forward(x)) == x</c> within floating-point tolerance.</item>
/// </list>
/// Hot-path buffers are rented from <see cref="ArrayPool{T}"/> or stack-allocated; no LINQ,
/// no per-call heap growth on the power-of-two path beyond the two caller-supplied spans.
/// </summary>
public static class FourierTransform
{
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>Max element count for a stack-allocated trig table before falling back to the pool.</summary>
    private const int StackAllocThreshold = 512;

    /// <summary>
    /// Forward DFT of a real-valued signal. Writes X[k] for k = 0 .. N−1 into
    /// <paramref name="outReal"/> / <paramref name="outImag"/> (each must have length ≥ signal length).
    /// </summary>
    public static void Forward(ReadOnlySpan<double> signal, Span<double> outReal, Span<double> outImag)
    {
        int n = signal.Length;
        if (outReal.Length < n || outImag.Length < n)
        {
            throw new ArgumentException("Output spans must be at least as long as the input signal.");
        }

        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            outReal[0] = signal[0];
            outImag[0] = 0.0;
            return;
        }

        for (int i = 0; i < n; i++)
        {
            outReal[i] = signal[i];
            outImag[i] = 0.0;
        }

        Span<double> re = outReal[..n];
        Span<double> im = outImag[..n];

        if ((n & (n - 1)) == 0)
        {
            TransformRadix2(re, im);
        }
        else
        {
            TransformBluestein(re, im);
        }
    }

    /// <summary>
    /// Inverse DFT of a complex spectrum: writes <c>x[n] = (1/N)·Σ_k X[k]·e^(+i·2π·k·n/N)</c>
    /// for n = 0 .. N−1 into <paramref name="outReal"/> / <paramref name="outImag"/>
    /// (each must have length ≥ the spectrum length; <paramref name="real"/> and
    /// <paramref name="imag"/> must be the same length).
    /// A Hermitian-symmetric spectrum (as produced by <see cref="Forward"/> of a real signal)
    /// yields a real result (imaginary part ≈ 0).
    /// Implemented via the identity <c>IFFT(X) = (1/N)·conj(FFT(conj(X)))</c>, reusing the
    /// forward radix-2 / Bluestein cores unchanged.
    /// </summary>
    public static void Inverse(ReadOnlySpan<double> real, ReadOnlySpan<double> imag, Span<double> outReal, Span<double> outImag)
    {
        int n = real.Length;
        if (imag.Length != n)
        {
            throw new ArgumentException("The real and imaginary spans must be the same length.");
        }

        if (outReal.Length < n || outImag.Length < n)
        {
            throw new ArgumentException("Output spans must be at least as long as the input spectrum.");
        }

        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            outReal[0] = real[0];
            outImag[0] = imag[0];
            return;
        }

        // conj(X)
        for (int i = 0; i < n; i++)
        {
            outReal[i] = real[i];
            outImag[i] = -imag[i];
        }

        Span<double> re = outReal[..n];
        Span<double> im = outImag[..n];

        // forward e^(−i…) transform of conj(X)
        if ((n & (n - 1)) == 0)
        {
            TransformRadix2(re, im);
        }
        else
        {
            TransformBluestein(re, im);
        }

        // (1/N)·conj(…)
        double inv = 1.0 / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= inv;
            im[i] = -im[i] * inv;
        }
    }

    /// <summary>
    /// In-place iterative radix-2 Cooley–Tukey forward transform.
    /// <paramref name="re"/>/<paramref name="im"/> length MUST be a power of two.
    /// </summary>
    private static void TransformRadix2(Span<double> re, Span<double> im)
    {
        int n = re.Length;
        if (n == 1)
        {
            return;
        }

        int levels = TrailingZeroBits(n);
        if ((1 << levels) != n)
        {
            throw new ArgumentException("radix-2 transform length must be a power of two.");
        }

        int half = n / 2;
        bool pooled = half > StackAllocThreshold;
        double[]? rentedCos = pooled ? ArrayPool<double>.Shared.Rent(half) : null;
        double[]? rentedSin = pooled ? ArrayPool<double>.Shared.Rent(half) : null;
        Span<double> cosTable = pooled ? rentedCos.AsSpan(0, half) : stackalloc double[half];
        Span<double> sinTable = pooled ? rentedSin.AsSpan(0, half) : stackalloc double[half];

        try
        {
            for (int i = 0; i < half; i++)
            {
                double angle = TwoPi * i / n;
                cosTable[i] = Math.Cos(angle);
                sinTable[i] = Math.Sin(angle);
            }

            // Bit-reversed addressing permutation.
            for (int i = 0; i < n; i++)
            {
                int j = ReverseBits(i, levels);
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            // Decimation-in-time butterflies (forward: X[k] = Σ x[i]·e^(−j·2π·k·i/N)).
            for (int size = 2; size <= n; size <<= 1)
            {
                int halfSize = size / 2;
                int tableStep = n / size;
                for (int i = 0; i < n; i += size)
                {
                    for (int j = i, k = 0; j < i + halfSize; j++, k += tableStep)
                    {
                        int l = j + halfSize;
                        double tpRe = re[l] * cosTable[k] + im[l] * sinTable[k];
                        double tpIm = -re[l] * sinTable[k] + im[l] * cosTable[k];
                        re[l] = re[j] - tpRe;
                        im[l] = im[j] - tpIm;
                        re[j] += tpRe;
                        im[j] += tpIm;
                    }
                }

                if (size == n)
                {
                    break;
                }
            }
        }
        finally
        {
            if (rentedCos != null)
            {
                ArrayPool<double>.Shared.Return(rentedCos);
            }

            if (rentedSin != null)
            {
                ArrayPool<double>.Shared.Return(rentedSin);
            }
        }
    }

    /// <summary>
    /// In-place inverse radix-2 transform (Σ x[i]·e^(+j·…), scaled by 1/N).
    /// Length MUST be a power of two.
    /// </summary>
    private static void InverseRadix2(Span<double> re, Span<double> im)
    {
        int n = re.Length;

        // Swapping the roles of real/imag flips the exponent sign; scale to make it a true inverse.
        TransformRadix2(im, re);

        double inv = 1.0 / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= inv;
            im[i] *= inv;
        }
    }

    /// <summary>
    /// Bluestein's chirp-z forward transform for arbitrary N. Produces the exact N-point DFT.
    /// </summary>
    private static void TransformBluestein(Span<double> re, Span<double> im)
    {
        int n = re.Length;

        int m = 1;
        while (m < (n * 2) + 1)
        {
            m <<= 1;
        }

        double[] cosTable = ArrayPool<double>.Shared.Rent(n);
        double[] sinTable = ArrayPool<double>.Shared.Rent(n);
        double[] aRe = ArrayPool<double>.Shared.Rent(m);
        double[] aIm = ArrayPool<double>.Shared.Rent(m);
        double[] bRe = ArrayPool<double>.Shared.Rent(m);
        double[] bIm = ArrayPool<double>.Shared.Rent(m);

        try
        {
            Array.Clear(aRe, 0, m);
            Array.Clear(aIm, 0, m);
            Array.Clear(bRe, 0, m);
            Array.Clear(bIm, 0, m);

            long twoN = 2L * n;
            for (int i = 0; i < n; i++)
            {
                long j = (long)i * i % twoN; // i² mod 2N keeps the angle accurate for large i.
                double angle = Math.PI * j / n;
                cosTable[i] = Math.Cos(angle);
                sinTable[i] = Math.Sin(angle);
            }

            for (int i = 0; i < n; i++)
            {
                aRe[i] = re[i] * cosTable[i] + im[i] * sinTable[i];
                aIm[i] = -re[i] * sinTable[i] + im[i] * cosTable[i];
            }

            bRe[0] = cosTable[0];
            bIm[0] = sinTable[0];
            for (int i = 1; i < n; i++)
            {
                bRe[i] = bRe[m - i] = cosTable[i];
                bIm[i] = bIm[m - i] = sinTable[i];
            }

            // Circular convolution of a and b via the power-of-two FFT.
            Span<double> aReSpan = aRe.AsSpan(0, m);
            Span<double> aImSpan = aIm.AsSpan(0, m);
            Span<double> bReSpan = bRe.AsSpan(0, m);
            Span<double> bImSpan = bIm.AsSpan(0, m);

            TransformRadix2(aReSpan, aImSpan);
            TransformRadix2(bReSpan, bImSpan);

            for (int i = 0; i < m; i++)
            {
                double tmp = aReSpan[i] * bReSpan[i] - aImSpan[i] * bImSpan[i];
                aImSpan[i] = aImSpan[i] * bReSpan[i] + aReSpan[i] * bImSpan[i];
                aReSpan[i] = tmp;
            }

            InverseRadix2(aReSpan, aImSpan);

            for (int i = 0; i < n; i++)
            {
                re[i] = aReSpan[i] * cosTable[i] + aImSpan[i] * sinTable[i];
                im[i] = -aReSpan[i] * sinTable[i] + aImSpan[i] * cosTable[i];
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(cosTable);
            ArrayPool<double>.Shared.Return(sinTable);
            ArrayPool<double>.Shared.Return(aRe);
            ArrayPool<double>.Shared.Return(aIm);
            ArrayPool<double>.Shared.Return(bRe);
            ArrayPool<double>.Shared.Return(bIm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TrailingZeroBits(int value)
    {
        int count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReverseBits(int value, int width)
    {
        int result = 0;
        for (int i = 0; i < width; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
