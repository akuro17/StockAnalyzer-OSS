using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// High-performance DTW (Dynamic Time Warping) distance engine for 1D sequences.
/// Memory: O(min(M, N)) via a 2-row rolling buffer (same pattern as <see cref="FrechetMath"/>).
/// Supports an optional Sakoe-Chiba band constraint.
/// </summary>
public static class DtwMath
{
    /// <summary>Radius value meaning "no Sakoe-Chiba constraint" (full matrix). Mirrored by server.py.</summary>
    public const int UnconstrainedRadius = -1;

    /// <summary>
    /// Calculates the DTW distance between two 1D sequences: sqrt of the accumulated squared
    /// point distance along the optimal warping path. The unconstrained result follows the same definition as
    /// tslearn.metrics.dtw. The banded case for sequences of different lengths uses this project's own linear
    /// diagonal (see <c>sakoeChibaRadius</c>); its cell set has not been verified against tslearn.
    /// The result does not depend on argument order: the shorter sequence always becomes the column dimension,
    /// so the band moves at most one column per row and a warping path always exists for a radius of 1 or more.
    /// Allocates 0 bytes on the GC heap when min(p.Length, q.Length) &lt;= 512.
    /// </summary>
    /// <param name="p">First sequence (expected to be Z-normalized by the caller).</param>
    /// <param name="q">Second sequence (expected to be Z-normalized by the caller).</param>
    /// <param name="sakoeChibaRadius">
    /// Sakoe-Chiba band half-width in index units. <see cref="UnconstrainedRadius"/> (-1) = unconstrained (full matrix);
    /// 0 = diagonal only (Euclidean distance for equal lengths); greater than 0 = row i (of the longer sequence)
    /// allows columns within the radius of floor(i * (shorterLength - 1) / (longerLength - 1)).
    /// Cells outside the band are treated as +infinity.
    /// </param>
    /// <returns>DTW distance (non-negative), or double.NaN if either sequence contains NaN.</returns>
    /// <exception cref="ArgumentException">Either sequence is empty, or the radius is below <see cref="UnconstrainedRadius"/>.</exception>
    public static double Calculate(ReadOnlySpan<double> p, ReadOnlySpan<double> q, int sakoeChibaRadius = UnconstrainedRadius)
    {
        if (p.Length == 0 || q.Length == 0)
        {
            throw new ArgumentException("Input series must not be empty.");
        }

        if (sakoeChibaRadius < UnconstrainedRadius)
        {
            throw new ArgumentException(
                $"Sakoe-Chiba radius must be >= {UnconstrainedRadius} ({UnconstrainedRadius} = unconstrained).",
                nameof(sakoeChibaRadius));
        }

        for (int i = 0; i < p.Length; i++)
        {
            if (double.IsNaN(p[i])) return double.NaN;
        }
        for (int j = 0; j < q.Length; j++)
        {
            if (double.IsNaN(q[j])) return double.NaN;
        }

        if (p.Length == 1 && q.Length == 1)
        {
            return Math.Abs(p[0] - q[0]);
        }

        // Ensure q is the shorter sequence (M <= N) for O(min(N, M)) buffer allocation.
        return p.Length < q.Length
            ? CalculateInternal(q, p, sakoeChibaRadius)
            : CalculateInternal(p, q, sakoeChibaRadius);
    }

    private static double CalculateInternal(ReadOnlySpan<double> p, ReadOnlySpan<double> q, int radius)
    {
        int m = q.Length;

        if (m <= MathBufferLimits.StackAllocThreshold)
        {
            Span<double> prev = stackalloc double[m];
            Span<double> curr = stackalloc double[m];
            return ComputeCore(p, q, radius, prev, curr);
        }

        double[] poolPrev = ArrayPool<double>.Shared.Rent(m);
        double[] poolCurr = ArrayPool<double>.Shared.Rent(m);
        try
        {
            return ComputeCore(p, q, radius, poolPrev.AsSpan(0, m), poolCurr.AsSpan(0, m));
        }
        finally
        {
            ArrayPool<double>.Shared.Return(poolPrev);
            ArrayPool<double>.Shared.Return(poolCurr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double ComputeCore(
        ReadOnlySpan<double> p,
        ReadOnlySpan<double> q,
        int radius,
        Span<double> prev,
        Span<double> curr)
    {
        int n = p.Length;
        int m = q.Length;
        bool banded = radius != UnconstrainedRadius;

        // Base row: i = 0 (band center is column 0)
        double d0 = p[0] - q[0];
        prev[0] = d0 * d0;
        for (int j = 1; j < m; j++)
        {
            if (banded && j > radius)
            {
                prev[j] = double.PositiveInfinity;
                continue;
            }

            double d = p[0] - q[j];
            prev[j] = prev[j - 1] + d * d;
        }

        // Subsequent rows: i = 1 .. n-1 (n >= 2 here, so n - 1 > 0)
        for (int i = 1; i < n; i++)
        {
            int jStart = 0;
            int jEnd = m - 1;
            if (banded)
            {
                int center = (int)((long)i * (m - 1) / (n - 1));
                jStart = Math.Max(0, center - radius);
                jEnd = Math.Min(m - 1, center + radius);
            }

            curr.Fill(double.PositiveInfinity);
            double pi = p[i];
            for (int j = jStart; j <= jEnd; j++)
            {
                double d = pi - q[j];
                double best = prev[j];
                if (j > 0)
                {
                    best = Math.Min(best, Math.Min(prev[j - 1], curr[j - 1]));
                }

                curr[j] = d * d + best;
            }

            curr.CopyTo(prev);
        }

        return Math.Sqrt(prev[m - 1]);
    }
}
