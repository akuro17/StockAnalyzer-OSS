using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Two windows of values discretised over their SHARED range into equal-width bins with Laplace smoothing, as two probability vectors a Kullback-Leibler divergence can take
/// (<see cref="DslQuantAndInfoMath.KlDivergence"/> needs probability vectors with no zero in the second one). With the union range <c>[min, max]</c> of both windows and <c>B</c> bins, the bin of a value
/// <c>v</c> is <c>min(B - 1, floor((v - min) / (max - min) x B))</c>; the probability of bin <c>b</c> of a window of <c>N</c> values with <c>c_b</c> of them in the bin is
/// <c>(c_b + alpha) / (N + B x alpha)</c>. Both vectors sum to 1 and every entry is positive. Unit: probability (dimensionless).
/// </summary>
public static class SmoothedHistogramMath
{
    /// <summary>The Laplace smoothing added to every bin count: small enough not to move a bin with data, large enough to keep an empty bin above the kernel's 1e-12 floor.</summary>
    public const double LaplaceAlpha = 1e-6;

    /// <summary>
    /// Fills <paramref name="first"/> and <paramref name="second"/> (each of length <paramref name="binCount"/>) with the smoothed probabilities of the two windows.
    /// False when a window is empty, <paramref name="binCount"/> is below 1 or a buffer is shorter than <paramref name="binCount"/>, a value is not finite, or the union range is empty
    /// (<c>max == min</c>: all values equal, the bins are undefined).
    /// </summary>
    public static bool TryBuild(ReadOnlySpan<double> firstWindow, ReadOnlySpan<double> secondWindow, int binCount, Span<double> first, Span<double> second)
    {
        if (firstWindow.IsEmpty || secondWindow.IsEmpty || binCount < 1 || first.Length < binCount || second.Length < binCount)
        {
            return false;
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        if (!Extend(firstWindow, ref min, ref max) || !Extend(secondWindow, ref min, ref max))
        {
            return false;
        }

        double range = max - min;
        if (!(range > 0.0) || !double.IsFinite(range))
        {
            return false;
        }

        Count(firstWindow, min, range, binCount, first);
        Count(secondWindow, min, range, binCount, second);
        Smooth(first.Slice(0, binCount), firstWindow.Length);
        Smooth(second.Slice(0, binCount), secondWindow.Length);
        return true;
    }

    /// <summary>Widens the range by the window; false when a value is not finite.</summary>
    private static bool Extend(ReadOnlySpan<double> window, ref double min, ref double max)
    {
        foreach (double value in window)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return true;
    }

    private static void Count(ReadOnlySpan<double> window, double min, double range, int binCount, Span<double> counts)
    {
        counts.Slice(0, binCount).Clear();
        foreach (double value in window)
        {
            int bin = (int)Math.Floor((value - min) / range * binCount);
            counts[Math.Min(binCount - 1, Math.Max(0, bin))] += 1.0;
        }
    }

    private static void Smooth(Span<double> counts, int total)
    {
        double denominator = total + (counts.Length * LaplaceAlpha);
        for (int b = 0; b < counts.Length; b++)
        {
            counts[b] = (counts[b] + LaplaceAlpha) / denominator;
        }
    }
}
