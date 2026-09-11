using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// High-performance, zero-allocation mathematical engine for calculating Discrete Fréchet Distance
/// between 1D and 2D polyline sequences using dynamic programming with O(min(N, M)) memory footprint.
/// </summary>
public static class FrechetMath
{
    private const int StackAllocThreshold = 512;

    /// <summary>
    /// Calculates the 1D Discrete Fréchet Distance between two numerical sequences.
    /// Allocates 0 bytes on the GC heap when min(p.Length, q.Length) &lt;= 512.
    /// </summary>
    /// <param name="p">First sequence.</param>
    /// <param name="q">Second sequence.</param>
    /// <returns>The discrete Fréchet distance (coupling measure bottleneck cost), or double.NaN if either sequence contains NaN.</returns>
    /// <exception cref="ArgumentException">Thrown when either sequence is empty.</exception>
    public static double CalculateDiscreteFrechetDistance(ReadOnlySpan<double> p, ReadOnlySpan<double> q)
    {
        if (p.Length == 0 || q.Length == 0)
        {
            throw new ArgumentException("Input series must not be empty.");
        }

        // Check for NaN
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

        // Ensure q is the shorter sequence (M <= N) for O(min(N, M)) column buffer allocation
        if (p.Length < q.Length)
        {
            return Calculate1DInternal(q, p);
        }

        return Calculate1DInternal(p, q);
    }

    private static double Calculate1DInternal(ReadOnlySpan<double> p, ReadOnlySpan<double> q)
    {
        int n = p.Length;
        int m = q.Length;

        if (m <= StackAllocThreshold)
        {
            Span<double> prev = stackalloc double[m];
            Span<double> curr = stackalloc double[m];
            return Compute1DCore(p, q, n, m, prev, curr);
        }
        else
        {
            double[] poolPrev = ArrayPool<double>.Shared.Rent(m);
            double[] poolCurr = ArrayPool<double>.Shared.Rent(m);
            try
            {
                Span<double> prev = poolPrev.AsSpan(0, m);
                Span<double> curr = poolCurr.AsSpan(0, m);
                return Compute1DCore(p, q, n, m, prev, curr);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(poolPrev);
                ArrayPool<double>.Shared.Return(poolCurr);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Compute1DCore(
        ReadOnlySpan<double> p,
        ReadOnlySpan<double> q,
        int n,
        int m,
        Span<double> prev,
        Span<double> curr)
    {
        // Base row: i = 0
        prev[0] = Math.Abs(p[0] - q[0]);
        for (int j = 1; j < m; j++)
        {
            prev[j] = Math.Max(prev[j - 1], Math.Abs(p[0] - q[j]));
        }

        // Subsequent rows: i = 1 .. n-1
        for (int i = 1; i < n; i++)
        {
            double pi = p[i];
            curr[0] = Math.Max(prev[0], Math.Abs(pi - q[0]));

            for (int j = 1; j < m; j++)
            {
                double cost = Math.Abs(pi - q[j]);
                double minPrev = Math.Min(Math.Min(curr[j - 1], prev[j]), prev[j - 1]);
                curr[j] = Math.Max(minPrev, cost);
            }

            // Copy curr to prev for next iteration
            curr.CopyTo(prev);
        }

        return prev[m - 1];
    }

    /// <summary>
    /// Calculates the 2D Discrete Fréchet Distance between two coordinate sequences (T, P).
    /// </summary>
    /// <param name="p">First 2D sequence.</param>
    /// <param name="q">Second 2D sequence.</param>
    /// <returns>The 2D discrete Fréchet distance, or double.NaN if any coordinate is NaN.</returns>
    /// <exception cref="ArgumentException">Thrown when either sequence is empty.</exception>
    public static double CalculateDiscreteFrechetDistance2D(
        ReadOnlySpan<(double T, double P)> p,
        ReadOnlySpan<(double T, double P)> q)
    {
        if (p.Length == 0 || q.Length == 0)
        {
            throw new ArgumentException("Input series must not be empty.");
        }

        for (int i = 0; i < p.Length; i++)
        {
            if (double.IsNaN(p[i].T) || double.IsNaN(p[i].P)) return double.NaN;
        }
        for (int j = 0; j < q.Length; j++)
        {
            if (double.IsNaN(q[j].T) || double.IsNaN(q[j].P)) return double.NaN;
        }

        if (p.Length == 1 && q.Length == 1)
        {
            double dt = p[0].T - q[0].T;
            double dp = p[0].P - q[0].P;
            return Math.Sqrt(dt * dt + dp * dp);
        }

        if (p.Length < q.Length)
        {
            return Calculate2DInternal(q, p);
        }

        return Calculate2DInternal(p, q);
    }

    private static double Calculate2DInternal(
        ReadOnlySpan<(double T, double P)> p,
        ReadOnlySpan<(double T, double P)> q)
    {
        int n = p.Length;
        int m = q.Length;

        if (m <= StackAllocThreshold)
        {
            Span<double> prev = stackalloc double[m];
            Span<double> curr = stackalloc double[m];
            return Compute2DCore(p, q, n, m, prev, curr);
        }
        else
        {
            double[] poolPrev = ArrayPool<double>.Shared.Rent(m);
            double[] poolCurr = ArrayPool<double>.Shared.Rent(m);
            try
            {
                Span<double> prev = poolPrev.AsSpan(0, m);
                Span<double> curr = poolCurr.AsSpan(0, m);
                return Compute2DCore(p, q, n, m, prev, curr);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(poolPrev);
                ArrayPool<double>.Shared.Return(poolCurr);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Compute2DCore(
        ReadOnlySpan<(double T, double P)> p,
        ReadOnlySpan<(double T, double P)> q,
        int n,
        int m,
        Span<double> prev,
        Span<double> curr)
    {
        // Base row: i = 0
        var p0 = p[0];
        var q0 = q[0];
        double dt0 = p0.T - q0.T;
        double dp0 = p0.P - q0.P;
        prev[0] = Math.Sqrt(dt0 * dt0 + dp0 * dp0);

        for (int j = 1; j < m; j++)
        {
            var qj = q[j];
            double dt = p0.T - qj.T;
            double dp = p0.P - qj.P;
            double d = Math.Sqrt(dt * dt + dp * dp);
            prev[j] = Math.Max(prev[j - 1], d);
        }

        // Subsequent rows: i = 1 .. n-1
        for (int i = 1; i < n; i++)
        {
            var pi = p[i];
            double dtI0 = pi.T - q0.T;
            double dpI0 = pi.P - q0.P;
            curr[0] = Math.Max(prev[0], Math.Sqrt(dtI0 * dtI0 + dpI0 * dpI0));

            for (int j = 1; j < m; j++)
            {
                var qj = q[j];
                double dt = pi.T - qj.T;
                double dp = pi.P - qj.P;
                double cost = Math.Sqrt(dt * dt + dp * dp);
                double minPrev = Math.Min(Math.Min(curr[j - 1], prev[j]), prev[j - 1]);
                curr[j] = Math.Max(minPrev, cost);
            }

            curr.CopyTo(prev);
        }

        return prev[m - 1];
    }
}
