namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using SkiaSharp;

public readonly record struct NurbsPoint4D(double X, double Y, double Z, double W)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKPoint ToCartesian2D()
    {
        if (Math.Abs(W) <= 1e-12) return new SKPoint((float)X, (float)Y);
        return new SKPoint((float)(X / W), (float)(Y / W));
    }
}

/// <summary>
/// SSoT Pure Math Core for Non-Uniform Rational B-Splines (NURBS).
/// Provides zero-allocation curve evaluation via Homogeneous de Boor algorithm
/// and deterministic step-sampled path rendering for SkiaSharp canvas.
/// </summary>
public static class NurbsMath
{
    public const int MaxDegree = 5;
    public const double MinWeight = 1e-4;
    public const double MaxWeight = 100.0;

    /// <summary>
    /// Generates Clamped Uniform Knot Vector: destination length must be (controlPointCount + degree + 1).
    /// </summary>
    public static void GenerateClampedKnotVector(int controlPointCount, int degree, Span<double> destination)
    {
        int n = controlPointCount - 1;
        int m = n + degree + 1;
        if (destination.Length != m + 1)
            throw new ArgumentException($"Destination span length must be {m + 1}, but was {destination.Length}.");

        double denom = n - degree + 1;
        for (int i = 0; i <= m; i++)
        {
            if (i <= degree) destination[i] = 0.0;
            else if (i >= m - degree) destination[i] = 1.0;
            else destination[i] = (i - degree) / denom;
        }
    }

    /// <summary>
    /// Generates Chord-Length Parameterized Clamped Knot Vector based on cumulative control point distances.
    /// Improves curve smoothness and minimizes distortion for irregularly spaced points (The NURBS Book Eq. 9.8).
    /// Destination length must be (points.Length + degree + 1).
    /// </summary>
    public static void GenerateChordLengthKnotVector(ReadOnlySpan<SKPoint> points, int degree, Span<double> destination)
    {
        int count = points.Length;
        int p = Math.Min(degree, count - 1);
        int n = count - 1;
        int m = n + p + 1;
        if (destination.Length != m + 1)
            throw new ArgumentException($"Destination span length must be {m + 1}, but was {destination.Length}.");

        if (count < 2 || p < 1)
        {
            GenerateClampedKnotVector(count, degree, destination);
            return;
        }

        Span<double> uBar = stackalloc double[count <= 256 ? count : 256];
        if (count > 256)
        {
            GenerateClampedKnotVector(count, degree, destination);
            return;
        }

        double totalDist = 0.0;
        uBar[0] = 0.0;
        for (int i = 1; i < count; i++)
        {
            float dx = points[i].X - points[i - 1].X;
            float dy = points[i].Y - points[i - 1].Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            totalDist += dist;
            uBar[i] = totalDist;
        }

        if (totalDist <= 1e-12)
        {
            GenerateClampedKnotVector(count, degree, destination);
            return;
        }

        // Normalize cumulative distances
        for (int i = 1; i < count - 1; i++)
        {
            uBar[i] /= totalDist;
        }
        uBar[count - 1] = 1.0;

        // First p + 1 knots are 0.0
        for (int i = 0; i <= p; i++)
        {
            destination[i] = 0.0;
        }

        // Last p + 1 knots are 1.0
        for (int i = m - p; i <= m; i++)
        {
            destination[i] = 1.0;
        }

        // Internal knots by averaging parameter values (Piegl & Tiller Eq. 9.8)
        for (int j = 1; j <= n - p; j++)
        {
            double sum = 0.0;
            for (int i = j; i <= j + p - 1; i++)
            {
                sum += uBar[i];
            }
            destination[j + p] = sum / p;
        }
    }

    /// <summary>
    /// Finds knot span index k such that u_k <= t < u_{k+1} using binary search (The NURBS Book Algorithm A2.1).
    /// If t >= u_{n+1}, returns n.
    /// </summary>
    public static int FindSpan(double t, int degree, int n, ReadOnlySpan<double> knots)
    {
        double uEnd = knots[n + 1];
        if (Math.Abs(t - uEnd) < 1e-12 || t >= uEnd)
            return n;

        int low = degree;
        int high = n + 1;
        int mid = (low + high) / 2;

        while (t < knots[mid] || t >= knots[mid + 1])
        {
            if (t < knots[mid])
                high = mid;
            else
                low = mid;

            mid = (low + high) / 2;
        }

        return mid;
    }

    /// <summary>
    /// Evaluates C(t) using Homogeneous de Boor with zero heap allocations.
    /// </summary>
    public static bool TryEvaluate(
        double t,
        int requestedDegree,
        ReadOnlySpan<SKPoint> controlPoints,
        ReadOnlySpan<double> weights,
        ReadOnlySpan<double> knots,
        out SKPoint result)
    {
        result = default;
        int count = controlPoints.Length;
        if (count < 2 || weights.Length != count) return false;

        // Check for NaN or Infinity in control points and weights
        for (int i = 0; i < count; i++)
        {
            if (float.IsNaN(controlPoints[i].X) || float.IsInfinity(controlPoints[i].X) ||
                float.IsNaN(controlPoints[i].Y) || float.IsInfinity(controlPoints[i].Y) ||
                double.IsNaN(weights[i]) || double.IsInfinity(weights[i]))
            {
                return false;
            }
        }

        // Check parameter t
        if (double.IsNaN(t) || double.IsInfinity(t)) return false;

        // Deterministic degree degradation
        int p = Math.Min(requestedDegree, count - 1);
        if (p < 1 || p > MaxDegree) return false;

        int n = count - 1;
        int m = n + p + 1;
        if (knots.Length != m + 1) return false;

        // Check knots validity
        for (int i = 0; i < knots.Length; i++)
        {
            if (double.IsNaN(knots[i]) || double.IsInfinity(knots[i])) return false;
            if (i > 0 && knots[i] < knots[i - 1]) return false; // Must be non-decreasing
        }

        // Domain [u_p, u_{n+1}]
        double uStart = knots[p];
        double uEnd = knots[n + 1];
        if (uEnd <= uStart) return false;

        // Clamp parameter t to valid domain [u_p, u_{n+1}]
        if (t <= uStart) t = uStart;
        else if (t >= uEnd) t = uEnd;

        // Locate knot span k using O(log n) binary search
        int k = FindSpan(t, p, n, knots);
        if (k < p || k > n) return false;

        // Stack-allocated homogeneous points for de Boor recursion with SIMD Vector4 acceleration
        Span<Vector4> d = stackalloc Vector4[p + 1];
        for (int j = 0; j <= p; j++)
        {
            int idx = k - p + j;
            float w = (float)Math.Clamp(weights[idx], MinWeight, MaxWeight);
            d[j] = new Vector4(controlPoints[idx].X * w, controlPoints[idx].Y * w, 0.0f, w);
        }

        for (int r = 1; r <= p; r++)
        {
            for (int j = p; j >= r; j--)
            {
                int iKnot = k - p + j;
                double denom = knots[iKnot + p + 1 - r] - knots[iKnot];
                float alpha = (float)((Math.Abs(denom) < 1e-14) ? 0.0 : (t - knots[iKnot]) / denom);

                // SIMD 4-lane vectorized Lerp / Fused Multiply-Add across (X, Y, Z, W)
                d[j] = d[j - 1] * (1.0f - alpha) + d[j] * alpha;
            }
        }

        float wFinal = d[p].W;
        if (MathF.Abs(wFinal) < 1e-12f) return false;
        result = new SKPoint(d[p].X / wFinal, d[p].Y / wFinal);
        return true;
    }

    /// <summary>
    /// Builds NURBS path into an existing SKPath using deterministic step sampling (Zero-Allocation).
    /// </summary>
    public static void BuildNurbsPath(
        SKPath destinationPath,
        int degree,
        ReadOnlySpan<SKPoint> controlPoints,
        ReadOnlySpan<double> weights,
        ReadOnlySpan<double> customKnots = default)
    {
        if (destinationPath == null || controlPoints.Length < 2) return;
        int count = controlPoints.Length;
        int p = Math.Min(degree, count - 1);
        if (p < 1 || p > MaxDegree) return;

        int knotCount = count + p + 1;

        if (knotCount > 256) return;

        Span<double> localKnots = stackalloc double[knotCount];
        if (!customKnots.IsEmpty && customKnots.Length == knotCount)
        {
            customKnots.CopyTo(localKnots);
        }
        else
        {
            GenerateClampedKnotVector(count, p, localKnots);
        }

        int sampleCount = Math.Clamp(count * 20, 40, 128);
        double tStart = localKnots[p];
        double tEnd = localKnots[count];
        double dt = (tEnd - tStart) / (sampleCount - 1);

        if (TryEvaluate(tStart, p, controlPoints, weights, localKnots, out var p0))
        {
            destinationPath.MoveTo(p0);
            for (int i = 1; i < sampleCount; i++)
            {
                double t = tStart + i * dt;
                if (TryEvaluate(t, p, controlPoints, weights, localKnots, out var pt))
                {
                    destinationPath.LineTo(pt);
                }
            }
        }
    }
}
