namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using SkiaSharp;

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

    /// <summary>Largest knot count the stack-allocated knot vectors of path building and hit testing accept; a curve needing more is not built or tested.</summary>
    internal const int MaxKnotCount = 256;

    /// <summary>Largest control-point count for which chord-length knots are generated (stack buffer of the cumulative chord lengths); more points fall back to uniform knots.</summary>
    private const int MaxChordLengthPointCount = 256;

    /// <summary>A cumulative chord length at or below this is treated as zero (all control points coincide), so uniform knots are used.</summary>
    private const double MinChordLengthTotal = 1e-12;

    /// <summary>A knot span below this has no width: the de Boor blend factor is 0 instead of a division by (almost) zero.</summary>
    private const double MinKnotSpan = 1e-14;

    /// <summary>A homogeneous weight below this in magnitude makes the curve point undefined (the evaluation reports failure).</summary>
    private const float MinHomogeneousWeight = 1e-12f;

    /// <summary>Curve samples per control point when a path is sampled or hit-tested.</summary>
    private const int SamplesPerControlPoint = 20;

    private const int MinSampleCount = 40;
    private const int MaxSampleCount = 128;

    /// <summary>Number of curve samples for a path or hit test over <paramref name="controlPointCount"/> control points: 20 per point, at least 40 and at most 128.</summary>
    internal static int ResolveSampleCount(int controlPointCount) =>
        Math.Clamp(controlPointCount * SamplesPerControlPoint, MinSampleCount, MaxSampleCount);

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

        Span<double> uBar = stackalloc double[count <= MaxChordLengthPointCount ? count : MaxChordLengthPointCount];
        if (count > MaxChordLengthPointCount)
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

        if (totalDist <= MinChordLengthTotal)
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
                float alpha = (float)((Math.Abs(denom) < MinKnotSpan) ? 0.0 : (t - knots[iKnot]) / denom);

                // SIMD 4-lane vectorized Lerp / Fused Multiply-Add across (X, Y, Z, W)
                d[j] = d[j - 1] * (1.0f - alpha) + d[j] * alpha;
            }
        }

        float wFinal = d[p].W;
        if (MathF.Abs(wFinal) < MinHomogeneousWeight) return false;
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

        if (knotCount > MaxKnotCount) return;

        Span<double> localKnots = stackalloc double[knotCount];
        if (!customKnots.IsEmpty && customKnots.Length == knotCount)
        {
            customKnots.CopyTo(localKnots);
        }
        else
        {
            GenerateClampedKnotVector(count, p, localKnots);
        }

        int sampleCount = ResolveSampleCount(count);
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

    /// <summary>Sample count of the hit test of the three-point NURBS objects (conic arc, hyperbola, parabola).</summary>
    internal const int ThreePointHitTestSampleCount = 40;

    /// <summary>Squared distance (screen pixels) from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    internal static double DistanceSquaredToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 <= BezierSplineMath.EpsilonSquared)
        {
            float px = p.X - a.X;
            float py = p.Y - a.Y;
            return px * px + py * py;
        }

        float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
        t = Math.Clamp(t, 0f, 1f);
        float projX = a.X + t * dx;
        float projY = a.Y + t * dy;
        float dX = p.X - projX;
        float dY = p.Y - projY;
        return dX * dX + dY * dY;
    }

    /// <summary>
    /// Hit test of a NURBS curve in screen space: false outside the control-point bounding box expanded by
    /// <paramref name="effectiveTolerance"/>, otherwise true when the curve, sampled at <paramref name="sampleCount"/> parameters of the
    /// clamped knot vector, passes within the tolerance of <paramref name="screenPoint"/>. Returns false for fewer than 2 control points,
    /// an effective degree below 1, a weight count that differs from the point count, or more than <see cref="MaxKnotCount"/> knots.
    /// </summary>
    internal static bool HitTestNurbsCurve(SKPoint screenPoint, int requestedDegree, ReadOnlySpan<SKPoint> controlPoints, ReadOnlySpan<double> weights, double effectiveTolerance, int sampleCount)
    {
        if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "At least 2 samples are required.");

        int count = controlPoints.Length;
        int p = Math.Min(requestedDegree, count - 1);
        if (count < 2 || p < 1 || weights.Length != count) return false;

        float minX = controlPoints[0].X, maxX = controlPoints[0].X;
        float minY = controlPoints[0].Y, maxY = controlPoints[0].Y;
        for (int i = 1; i < count; i++)
        {
            if (controlPoints[i].X < minX) minX = controlPoints[i].X;
            if (controlPoints[i].X > maxX) maxX = controlPoints[i].X;
            if (controlPoints[i].Y < minY) minY = controlPoints[i].Y;
            if (controlPoints[i].Y > maxY) maxY = controlPoints[i].Y;
        }

        if (screenPoint.X < minX - effectiveTolerance || screenPoint.X > maxX + effectiveTolerance ||
            screenPoint.Y < minY - effectiveTolerance || screenPoint.Y > maxY + effectiveTolerance)
        {
            return false;
        }

        int knotCount = count + p + 1;
        if (knotCount > MaxKnotCount) return false;

        Span<double> knots = stackalloc double[knotCount];
        GenerateClampedKnotVector(count, p, knots);

        double tStart = knots[p];
        double tEnd = knots[count];
        double dt = (tEnd - tStart) / (sampleCount - 1);

        if (!TryEvaluate(tStart, p, controlPoints, weights, knots, out var prevSample))
            return false;

        double tolSquared = effectiveTolerance * effectiveTolerance;
        for (int i = 1; i < sampleCount; i++)
        {
            double t = tStart + i * dt;
            if (TryEvaluate(t, p, controlPoints, weights, knots, out var currSample))
            {
                if (DistanceSquaredToSegment(screenPoint, prevSample, currSample) <= tolSquared)
                    return true;
                prevSample = currSample;
            }
        }

        return false;
    }
}
