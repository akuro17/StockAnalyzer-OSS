namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;

/// <summary>
/// Represents a single Cubic Bézier segment defined by 4 control points in screen coordinates.
/// </summary>
public readonly record struct CubicBezierSegment(SKPoint P0, SKPoint C1, SKPoint C2, SKPoint P3);

/// <summary>
/// High-performance, Zero-Allocation mathematical utility for Bézier and Catmull-Rom spline curves.
/// Strictly decoupled from UI frameworks (Platform-Agnostic, SkiaSharp-only).
/// </summary>
public static class BezierSplineMath
{
    public const double DefaultTension = 0.5;
    public const double GoldenRatioPhi = 1.618033988749895;
    public static readonly double SpiralGrowthB = Math.Log(GoldenRatioPhi) / (Math.PI / 2.0);
    public const int DefaultSpiralQuadrants = 16;
    public const int MaxSpiralQuadrants = 64;
    public const float EpsilonSquared = 1e-12f;
    public const float MinRadius = 1e-4f;
    public const float DefaultMaxRadius = 10000f;
    public const double RootBoundaryTolerance = 1e-4;

    /// <summary>
    /// Calculates the two Cubic Bézier control points (C1, C2) for a Catmull-Rom segment [pCurr, pNext]
    /// given adjacent boundary points pPrev and pNextNext.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CalculateControlPoints(
        SKPoint pPrev,
        SKPoint pCurr,
        SKPoint pNext,
        SKPoint pNextNext,
        double tension,
        out SKPoint c1,
        out SKPoint c2)
    {
        float factor = (float)(tension / 3.0);
        c1 = new SKPoint(
            pCurr.X + factor * (pNext.X - pPrev.X),
            pCurr.Y + factor * (pNext.Y - pPrev.Y)
        );
        c2 = new SKPoint(
            pNext.X - factor * (pNextNext.X - pCurr.X),
            pNext.Y - factor * (pNextNext.Y - pCurr.Y)
        );
    }

    /// <summary>
    /// Calculates control points for a Time-Decoupled Catmull-Rom segment (FR-61-7-01 / WebAI Refined Model).
    /// X coordinates are strictly linear (X_C1 = X_i + dx/3, X_C2 = X_{i+1} - dx/3), eliminating time-reversal loops.
    /// Y coordinates follow uniform Catmull-Rom interpolation (tau = 0.5).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CalculateTimeDecoupledControlPoints(
        SKPoint pPrev,
        SKPoint pCurr,
        SKPoint pNext,
        SKPoint pNextNext,
        double tension,
        out SKPoint c1,
        out SKPoint c2)
    {
        float dxThird = (pNext.X - pCurr.X) / 3.0f;
        float yFactor = (float)(tension / 3.0);
        c1 = new SKPoint(
            pCurr.X + dxThird,
            pCurr.Y + yFactor * (pNext.Y - pPrev.Y)
        );
        c2 = new SKPoint(
            pNext.X - dxThird,
            pNext.Y - yFactor * (pNextNext.Y - pCurr.Y)
        );
    }

    /// <summary>
    /// Builds a Time-Decoupled Catmull-Rom Cubic Bézier spline directly into an SKPath with Zero Managed Allocation.
    /// Guarantees that the X axis progresses strictly linearly (X(t) = X_i + t * dx) without time-reversal cusps or loops.
    /// </summary>
    public static void BuildTimeDecoupledCatmullRomSplinePath(
        SKPath destinationPath,
        ReadOnlySpan<SKPoint> points,
        double tension = DefaultTension)
    {
        if (destinationPath == null) throw new ArgumentNullException(nameof(destinationPath));
        if (tension < 0.0 || tension > 1.0 || double.IsNaN(tension) || double.IsInfinity(tension))
            throw new ArgumentOutOfRangeException(nameof(tension), "Tension must be a finite number between 0.0 and 1.0.");

        int count = points.Length;
        if (count < 2) return;

        // IEEE 754 Safety Guard: Abort on any NaN/Inf
        for (int i = 0; i < count; i++)
        {
            if (float.IsNaN(points[i].X) || float.IsNaN(points[i].Y) ||
                float.IsInfinity(points[i].X) || float.IsInfinity(points[i].Y))
                return;
        }

        if (count == 2)
        {
            destinationPath.MoveTo(points[0]);
            destinationPath.LineTo(points[1]);
            return;
        }

        destinationPath.MoveTo(points[0]);

        for (int i = 0; i < count - 1; i++)
        {
            SKPoint pCurr = points[i];
            SKPoint pNext = points[i + 1];

            // Skip degenerate consecutive points
            if (DistanceSquared(pCurr, pNext) < EpsilonSquared)
                continue;

            SKPoint pPrev = (i == 0)
                ? new SKPoint(2f * points[0].X - points[1].X, 2f * points[0].Y - points[1].Y)
                : points[i - 1];

            SKPoint pNextNext = (i == count - 2)
                ? new SKPoint(2f * points[count - 1].X - points[count - 2].X, 2f * points[count - 1].Y - points[count - 2].Y)
                : points[i + 2];

            CalculateTimeDecoupledControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out var c1, out var c2);
            destinationPath.CubicTo(c1, c2, pNext);
        }
    }

    /// <summary>
    /// Builds a Catmull-Rom Cubic Bézier spline directly into an SKPath with Zero Managed Allocation.
    /// </summary>
    public static void BuildCatmullRomSplinePath(
        SKPath destinationPath,
        ReadOnlySpan<SKPoint> points,
        double tension = DefaultTension)
    {
        if (destinationPath == null) throw new ArgumentNullException(nameof(destinationPath));
        if (tension < 0.0 || tension > 1.0 || double.IsNaN(tension) || double.IsInfinity(tension))
            throw new ArgumentOutOfRangeException(nameof(tension), "Tension must be a finite number between 0.0 and 1.0.");

        int count = points.Length;
        if (count < 2) return;

        // IEEE 754 Safety Guard: Abort on any NaN/Inf
        for (int i = 0; i < count; i++)
        {
            if (float.IsNaN(points[i].X) || float.IsNaN(points[i].Y) ||
                float.IsInfinity(points[i].X) || float.IsInfinity(points[i].Y))
                return;
        }

        if (count == 2)
        {
            destinationPath.MoveTo(points[0]);
            destinationPath.LineTo(points[1]);
            return;
        }

        destinationPath.MoveTo(points[0]);

        for (int i = 0; i < count - 1; i++)
        {
            SKPoint pCurr = points[i];
            SKPoint pNext = points[i + 1];

            // Skip degenerate consecutive points
            if (DistanceSquared(pCurr, pNext) < EpsilonSquared)
                continue;

            SKPoint pPrev = (i == 0)
                ? new SKPoint(2f * points[0].X - points[1].X, 2f * points[0].Y - points[1].Y)
                : points[i - 1];

            SKPoint pNextNext = (i == count - 2)
                ? new SKPoint(2f * points[count - 1].X - points[count - 2].X, 2f * points[count - 1].Y - points[count - 2].Y)
                : points[i + 2];

            CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out var c1, out var c2);
            destinationPath.CubicTo(c1, c2, pNext);
        }
    }

    /// <summary>
    /// Builds a Logarithmic Spiral path composed of 90-degree Cubic Bézier arcs.
    /// </summary>
    public static void BuildLogarithmicSpiralPath(
        SKPath destinationPath,
        SKPoint center,
        SKPoint startPoint,
        int quadrantCount = DefaultSpiralQuadrants,
        float maxRadius = DefaultMaxRadius)
    {
        if (destinationPath == null) throw new ArgumentNullException(nameof(destinationPath));
        if (quadrantCount <= 0) return;
        if (quadrantCount > MaxSpiralQuadrants)
            throw new ArgumentOutOfRangeException(nameof(quadrantCount), $"Quadrant count must not exceed {MaxSpiralQuadrants}.");
        if (maxRadius <= 0f || float.IsNaN(maxRadius) || float.IsInfinity(maxRadius))
            throw new ArgumentOutOfRangeException(nameof(maxRadius), "Max radius must be a positive finite number.");

        if (float.IsNaN(center.X) || float.IsNaN(center.Y) || float.IsNaN(startPoint.X) || float.IsNaN(startPoint.Y) ||
            float.IsInfinity(center.X) || float.IsInfinity(center.Y) || float.IsInfinity(startPoint.X) || float.IsInfinity(startPoint.Y))
            return;

        float dx = startPoint.X - center.X;
        float dy = startPoint.Y - center.Y;
        double rInit = Math.Sqrt(dx * dx + dy * dy);
        if (rInit < MinRadius) return;

        double thetaInit = Math.Atan2(dy, dx);
        double b = SpiralGrowthB;
        double tangentScale = Math.PI / 6.0;

        destinationPath.MoveTo(startPoint);

        double currentR = rInit;
        double currentTheta = thetaInit;
        SKPoint currentP = startPoint;

        for (int k = 0; k < quadrantCount; k++)
        {
            double nextTheta = currentTheta + (Math.PI / 2.0);
            double nextR = currentR * GoldenRatioPhi;

            if (nextR > maxRadius) break;

            SKPoint nextP = new SKPoint(
                (float)(center.X + nextR * Math.Cos(nextTheta)),
                (float)(center.Y + nextR * Math.Sin(nextTheta))
            );

            double cosCur = Math.Cos(currentTheta);
            double sinCur = Math.Sin(currentTheta);
            SKPoint tCur = new SKPoint(
                (float)(tangentScale * currentR * (b * cosCur - sinCur)),
                (float)(tangentScale * currentR * (b * sinCur + cosCur))
            );

            double cosNext = Math.Cos(nextTheta);
            double sinNext = Math.Sin(nextTheta);
            SKPoint tNext = new SKPoint(
                (float)(tangentScale * nextR * (b * cosNext - sinNext)),
                (float)(tangentScale * nextR * (b * sinNext + cosNext))
            );

            SKPoint c1 = new SKPoint(currentP.X + tCur.X, currentP.Y + tCur.Y);
            SKPoint c2 = new SKPoint(nextP.X - tNext.X, nextP.Y - tNext.Y);

            destinationPath.CubicTo(c1, c2, nextP);

            currentR = nextR;
            currentTheta = nextTheta;
            currentP = nextP;
        }
    }

    /// <summary>
    /// Evaluates if a point is within tolerance pixels of a Cubic Bézier segment (4-subdivision linear approximation).
    /// </summary>
    public static bool HitTestCubicSegment(
        SKPoint screenPoint,
        SKPoint p0,
        SKPoint c1,
        SKPoint c2,
        SKPoint p3,
        double tolerance)
    {
        if (tolerance < 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a non-negative finite number.");

        if (float.IsNaN(screenPoint.X) || float.IsNaN(screenPoint.Y) || float.IsInfinity(screenPoint.X) || float.IsInfinity(screenPoint.Y) ||
            float.IsNaN(p0.X) || float.IsNaN(p0.Y) || float.IsInfinity(p0.X) || float.IsInfinity(p0.Y) ||
            float.IsNaN(c1.X) || float.IsNaN(c1.Y) || float.IsInfinity(c1.X) || float.IsInfinity(c1.Y) ||
            float.IsNaN(c2.X) || float.IsNaN(c2.Y) || float.IsInfinity(c2.X) || float.IsInfinity(c2.Y) ||
            float.IsNaN(p3.X) || float.IsNaN(p3.Y) || float.IsInfinity(p3.X) || float.IsInfinity(p3.Y))
            return false;

        // 5 evaluation points
        SKPoint q0 = p0;
        SKPoint q1 = new SKPoint(
            (27f * p0.X + 27f * c1.X + 9f * c2.X + p3.X) / 64f,
            (27f * p0.Y + 27f * c1.Y + 9f * c2.Y + p3.Y) / 64f);
        SKPoint q2 = new SKPoint(
            (p0.X + 3f * c1.X + 3f * c2.X + p3.X) / 8f,
            (p0.Y + 3f * c1.Y + 3f * c2.Y + p3.Y) / 8f);
        SKPoint q3 = new SKPoint(
            (p0.X + 9f * c1.X + 27f * c2.X + 27f * p3.X) / 64f,
            (p0.Y + 9f * c1.Y + 27f * c2.Y + 27f * p3.Y) / 64f);
        SKPoint q4 = p3;

        return DistancePointToSegment(screenPoint, q0, q1) <= tolerance ||
               DistancePointToSegment(screenPoint, q1, q2) <= tolerance ||
               DistancePointToSegment(screenPoint, q2, q3) <= tolerance ||
               DistancePointToSegment(screenPoint, q3, q4) <= tolerance;
    }

    /// <summary>
    /// Performs hit-testing against a logarithmic spiral path by evaluating each 90-degree Bézier quadrant segment.
    /// </summary>
    public static bool HitTestLogarithmicSpiral(
        SKPoint screenPoint,
        SKPoint center,
        SKPoint startPoint,
        double tolerance = 5.0,
        int quadrantCount = DefaultSpiralQuadrants,
        float maxRadius = DefaultMaxRadius)
    {
        if (tolerance < 0.0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a non-negative finite number.");
        if (quadrantCount <= 0 || quadrantCount > MaxSpiralQuadrants) return false;
        if (maxRadius <= 0f || float.IsNaN(maxRadius) || float.IsInfinity(maxRadius)) return false;

        if (float.IsNaN(screenPoint.X) || float.IsNaN(screenPoint.Y) || float.IsInfinity(screenPoint.X) || float.IsInfinity(screenPoint.Y) ||
            float.IsNaN(center.X) || float.IsNaN(center.Y) || float.IsInfinity(center.X) || float.IsInfinity(center.Y) ||
            float.IsNaN(startPoint.X) || float.IsNaN(startPoint.Y) || float.IsInfinity(startPoint.X) || float.IsInfinity(startPoint.Y))
            return false;

        float dx = startPoint.X - center.X;
        float dy = startPoint.Y - center.Y;
        double rInit = Math.Sqrt(dx * dx + dy * dy);
        if (rInit < MinRadius) return false;

        double thetaInit = Math.Atan2(dy, dx);
        double b = SpiralGrowthB;
        double tangentScale = Math.PI / 6.0;

        double currentR = rInit;
        double currentTheta = thetaInit;
        SKPoint currentP = startPoint;

        for (int k = 0; k < quadrantCount; k++)
        {
            double nextTheta = currentTheta + (Math.PI / 2.0);
            double nextR = currentR * GoldenRatioPhi;

            if (nextR > maxRadius) break;

            SKPoint nextP = new SKPoint(
                (float)(center.X + nextR * Math.Cos(nextTheta)),
                (float)(center.Y + nextR * Math.Sin(nextTheta))
            );

            double cosCur = Math.Cos(currentTheta);
            double sinCur = Math.Sin(currentTheta);
            SKPoint tCur = new SKPoint(
                (float)(tangentScale * currentR * (b * cosCur - sinCur)),
                (float)(tangentScale * currentR * (b * sinCur + cosCur))
            );

            double cosNext = Math.Cos(nextTheta);
            double sinNext = Math.Sin(nextTheta);
            SKPoint tNext = new SKPoint(
                (float)(tangentScale * nextR * (b * cosNext - sinNext)),
                (float)(tangentScale * nextR * (b * sinNext + cosNext))
            );

            SKPoint c1 = new SKPoint(currentP.X + tCur.X, currentP.Y + tCur.Y);
            SKPoint c2 = new SKPoint(nextP.X - tNext.X, nextP.Y - tNext.Y);

            if (HitTestCubicSegment(screenPoint, currentP, c1, c2, nextP, tolerance))
                return true;

            currentR = nextR;
            currentTheta = nextTheta;
            currentP = nextP;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DistancePointToSegment(SKPoint p, SKPoint v, SKPoint w)
    {
        double vx = v.X, vy = v.Y;
        double wx = w.X, wy = w.Y;
        double px = p.X, py = p.Y;

        double l2 = (wx - vx) * (wx - vx) + (wy - vy) * (wy - vy);
        if (l2 < EpsilonSquared)
            return Math.Sqrt((px - vx) * (px - vx) + (py - vy) * (py - vy));

        double t = ((px - vx) * (wx - vx) + (py - vy) * (wy - vy)) / l2;
        t = Math.Clamp(t, 0.0, 1.0);
        double projX = vx + t * (wx - vx);
        double projY = vy + t * (wy - vy);
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(SKPoint a, SKPoint b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Evaluates a 1D Cubic Bézier polynomial for a given parameter t in [0, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateCubicBezier(double y0, double y1, double y2, double y3, double t)
    {
        double u = 1.0 - t;
        return u * u * u * y0 + 3.0 * u * u * t * y1 + 3.0 * u * t * t * y2 + t * t * t * y3;
    }

    /// <summary>
    /// Finds analytical extrema points for a single Cubic Bézier segment in screen space with Zero Heap Allocation.
    /// Returns the number of valid roots written to <paramref name="destination"/> (0, 1, or 2).
    /// </summary>
    public static int FindCubicBezierYExtrema(
        SKPoint p0,
        SKPoint c1,
        SKPoint c2,
        SKPoint p3,
        Span<BezierExtremum> destination,
        int segmentIndex = 0)
    {
        if (destination.Length < 2)
            throw new ArgumentException("Destination span must have a capacity of at least 2.", nameof(destination));
        if (segmentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(segmentIndex), "Segment index must be non-negative.");

        // IEEE 754 Safety Guard
        if (float.IsNaN(p0.X) || float.IsNaN(p0.Y) || float.IsNaN(c1.X) || float.IsNaN(c1.Y) ||
            float.IsNaN(c2.X) || float.IsNaN(c2.Y) || float.IsNaN(p3.X) || float.IsNaN(p3.Y) ||
            float.IsInfinity(p0.X) || float.IsInfinity(p0.Y) || float.IsInfinity(c1.X) || float.IsInfinity(c1.Y) ||
            float.IsInfinity(c2.X) || float.IsInfinity(c2.Y) || float.IsInfinity(p3.X) || float.IsInfinity(p3.Y))
            return 0;

        double y0 = p0.Y, y1 = c1.Y, y2 = c2.Y, y3 = p3.Y;
        double a = -y0 + 3.0 * y1 - 3.0 * y2 + y3;
        double b = 2.0 * (y0 - 2.0 * y1 + y2);
        double c = y1 - y0;

        double scaleY = Math.Max(Math.Max(Math.Abs(y0), Math.Abs(y1)), Math.Max(Math.Abs(y2), Math.Max(Math.Abs(y3), 1.0)));
        double tolC = 1e-12 * scaleY;
        double tolD = 1e-24 * scaleY * scaleY;

        int written = 0;

        // Case 1: Linear derivative (|A| <= tolC)
        if (Math.Abs(a) <= tolC)
        {
            if (Math.Abs(b) > tolC)
            {
                double t = -c / b;
                if (t > RootBoundaryTolerance && t < (1.0 - RootBoundaryTolerance))
                {
                    double screenY = EvaluateCubicBezier(y0, y1, y2, y3, t);
                    double secondDeriv = 3.0 * b;
                    if (Math.Abs(secondDeriv) > tolC)
                    {
                        ExtremaType type = secondDeriv > 0 ? ExtremaType.High : ExtremaType.Low;
                        destination[written++] = new BezierExtremum(screenY, type, segmentIndex, t);
                    }
                }
            }
            return written;
        }

        // Case 2: Quadratic derivative (|A| > tolC)
        double discriminant = b * b - 4.0 * a * c;
        if (discriminant <= tolD)
            return 0;

        double sqrtD = Math.Sqrt(discriminant);
        double sgnB = b >= 0.0 ? 1.0 : -1.0;
        double q = -0.5 * (b + sgnB * sqrtD);

        double tA = q / a;
        double tB = Math.Abs(q) > 1e-30 ? (c / q) : tA;

        double t1 = Math.Min(tA, tB);
        double t2 = Math.Max(tA, tB);

        Span<double> candidateT = stackalloc double[2] { t1, t2 };
        for (int i = 0; i < 2; i++)
        {
            double t = candidateT[i];
            if (t > RootBoundaryTolerance && t < (1.0 - RootBoundaryTolerance))
            {
                double screenY = EvaluateCubicBezier(y0, y1, y2, y3, t);
                double secondDeriv = 6.0 * a * t + 3.0 * b;
                if (Math.Abs(secondDeriv) > tolC)
                {
                    ExtremaType type = secondDeriv > 0 ? ExtremaType.High : ExtremaType.Low;
                    destination[written++] = new BezierExtremum(screenY, type, segmentIndex, t);
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Extracts, deduplicates, and populates all extrema levels across an entire spline curve with Zero Allocation.
    /// </summary>
    public static int ExtractSplineExtrema(
        ReadOnlySpan<SKPoint> screenPoints,
        Span<BezierExtremum> destination,
        double tension = DefaultTension,
        bool isTimeDecoupled = true,
        double mergeTolerancePx = 2.0)
    {
        int count = screenPoints.Length;
        if (count < 2 || destination.Length == 0) return 0;
        if (mergeTolerancePx < 0.0 || double.IsNaN(mergeTolerancePx) || double.IsInfinity(mergeTolerancePx)) return 0;
        if (tension < 0.0 || tension > 1.0 || double.IsNaN(tension) || double.IsInfinity(tension)) return 0;

        int segCount = count - 1;
        int maxRoots = segCount * 2;

        BezierExtremum[]? rentedExtrema = null;
        Span<BezierExtremum> rawExtrema = maxRoots <= 64
            ? stackalloc BezierExtremum[maxRoots]
            : (rentedExtrema = ArrayPool<BezierExtremum>.Shared.Rent(maxRoots)).AsSpan(0, maxRoots);

        try
        {
            int rawCount = 0;

            for (int i = 0; i < segCount; i++)
            {
                SKPoint pCurr = screenPoints[i];
                SKPoint pNext = screenPoints[i + 1];
                if (DistanceSquared(pCurr, pNext) < EpsilonSquared) continue;

                SKPoint pPrev = (i == 0)
                    ? new SKPoint(2f * screenPoints[0].X - screenPoints[1].X, 2f * screenPoints[0].Y - screenPoints[1].Y)
                    : screenPoints[i - 1];

                SKPoint pNextNext = (i == count - 2)
                    ? new SKPoint(2f * screenPoints[count - 1].X - screenPoints[count - 2].X, 2f * screenPoints[count - 1].Y - screenPoints[count - 2].Y)
                    : screenPoints[i + 2];

                SKPoint c1, c2;
                if (isTimeDecoupled)
                    CalculateTimeDecoupledControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out c1, out c2);
                else
                    CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out c1, out c2);

                if (rawCount + 2 <= rawExtrema.Length)
                {
                    Span<BezierExtremum> segRoots = rawExtrema.Slice(rawCount, 2);
                    int found = FindCubicBezierYExtrema(pCurr, c1, c2, pNext, segRoots, i);
                    rawCount += found;
                }
            }

            // Deduplication & Merging (SegmentIndex ASC -> ParameterT ASC)
            int outputCount = 0;
            for (int i = 0; i < rawCount; i++)
            {
                var candidate = rawExtrema[i];
                bool isDuplicate = false;
                for (int j = 0; j < outputCount; j++)
                {
                    if (Math.Abs(destination[j].ScreenY - candidate.ScreenY) <= mergeTolerancePx &&
                        destination[j].Type == candidate.Type)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate && outputCount < destination.Length)
                {
                    destination[outputCount++] = candidate;
                }
            }

            return outputCount;
        }
        finally
        {
            if (rentedExtrema != null) ArrayPool<BezierExtremum>.Shared.Return(rentedExtrema);
        }
    }

    /// <summary>
    /// Filters raw extrema by prominence (swing depth), clusters nearby levels within tolerance,
    /// and selects top-ranked levels by touch count and prominence up to maxLevels per type (Zero-Allocation).
    /// </summary>
    public static void FilterAndClusterExtrema(
        ReadOnlySpan<ExtremaLevel> rawLevels,
        List<ExtremaLevel> outputList,
        double minSwingPercent = 2.0,
        double clusterTolerancePx = 15.0,
        int maxLevels = 5,
        decimal totalSpanMin = 0m,
        decimal totalSpanMax = 0m)
    {
        if (outputList == null) throw new ArgumentNullException(nameof(outputList));
        outputList.Clear();

        int count = rawLevels.Length;
        if (count == 0) return;

        // If 1 or 2 extrema, return them directly
        if (count <= 2)
        {
            for (int i = 0; i < count; i++)
            {
                outputList.Add(rawLevels[i]);
            }
            return;
        }

        // Calculate span
        double span = 0.0;
        if (totalSpanMax > totalSpanMin)
        {
            span = (double)(totalSpanMax - totalSpanMin);
        }
        else
        {
            decimal minP = rawLevels[0].Price, maxP = rawLevels[0].Price;
            for (int i = 1; i < count; i++)
            {
                if (rawLevels[i].Price < minP) minP = rawLevels[i].Price;
                if (rawLevels[i].Price > maxP) maxP = rawLevels[i].Price;
            }
            span = (double)(maxP - minP);
        }
        if (span <= 1e-6) return;

        ExtremaLevel[]? rentedCandidates = null;
        Span<ExtremaLevel> candidates = count <= 64
            ? stackalloc ExtremaLevel[count]
            : (rentedCandidates = ArrayPool<ExtremaLevel>.Shared.Rent(count)).AsSpan(0, count);

        ExtremaLevel[]? rentedHigh = null;
        Span<ExtremaLevel> highClusters = count <= 64
            ? stackalloc ExtremaLevel[count]
            : (rentedHigh = ArrayPool<ExtremaLevel>.Shared.Rent(count)).AsSpan(0, count);

        ExtremaLevel[]? rentedLow = null;
        Span<ExtremaLevel> lowClusters = count <= 64
            ? stackalloc ExtremaLevel[count]
            : (rentedLow = ArrayPool<ExtremaLevel>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            int candidateCount = 0;

            // 1. Prominence Filter
            for (int i = 0; i < count; i++)
            {
                var cur = rawLevels[i];
                double prominence = 0.0;

                if (cur.Type == ExtremaType.High)
                {
                    decimal leftLow = decimal.MaxValue;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (rawLevels[j].Type == ExtremaType.Low)
                        {
                            leftLow = rawLevels[j].Price;
                            break;
                        }
                        if (rawLevels[j].Price < leftLow) leftLow = rawLevels[j].Price;
                    }

                    decimal rightLow = decimal.MaxValue;
                    for (int j = i + 1; j < count; j++)
                    {
                        if (rawLevels[j].Type == ExtremaType.Low)
                        {
                            rightLow = rawLevels[j].Price;
                            break;
                        }
                        if (rawLevels[j].Price < rightLow) rightLow = rawLevels[j].Price;
                    }

                    decimal baseLow;
                    if (leftLow != decimal.MaxValue && rightLow != decimal.MaxValue)
                        baseLow = Math.Max(leftLow, rightLow);
                    else if (leftLow != decimal.MaxValue)
                        baseLow = leftLow;
                    else if (rightLow != decimal.MaxValue)
                        baseLow = rightLow;
                    else
                        baseLow = (totalSpanMin > 0) ? totalSpanMin : cur.Price;

                    prominence = Math.Max(0.0, (double)(cur.Price - baseLow));
                }
                else
                {
                    decimal leftHigh = decimal.MinValue;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (rawLevels[j].Type == ExtremaType.High)
                        {
                            leftHigh = rawLevels[j].Price;
                            break;
                        }
                        if (rawLevels[j].Price > leftHigh) leftHigh = rawLevels[j].Price;
                    }

                    decimal rightHigh = decimal.MinValue;
                    for (int j = i + 1; j < count; j++)
                    {
                        if (rawLevels[j].Type == ExtremaType.High)
                        {
                            rightHigh = rawLevels[j].Price;
                            break;
                        }
                        if (rawLevels[j].Price > rightHigh) rightHigh = rawLevels[j].Price;
                    }

                    decimal baseHigh;
                    if (leftHigh != decimal.MinValue && rightHigh != decimal.MinValue)
                        baseHigh = Math.Min(leftHigh, rightHigh);
                    else if (leftHigh != decimal.MinValue)
                        baseHigh = leftHigh;
                    else if (rightHigh != decimal.MinValue)
                        baseHigh = rightHigh;
                    else
                        baseHigh = (totalSpanMax > 0) ? totalSpanMax : cur.Price;

                    prominence = Math.Max(0.0, (double)(baseHigh - cur.Price));
                }

                double promPercent = (prominence / span) * 100.0;
                if (promPercent >= minSwingPercent || minSwingPercent <= 0.0)
                {
                    candidates[candidateCount++] = new ExtremaLevel(
                        cur.Price, cur.ScreenY, cur.SnappedY, cur.Type, cur.SegmentIndex, cur.ParameterT,
                        TouchCount: 1, Prominence: promPercent);
                }
            }

            // 2. Clustering
            int highClusterCount = 0;
            int lowClusterCount = 0;

            for (int i = 0; i < candidateCount; i++)
            {
                var cand = candidates[i];
                if (cand.Type == ExtremaType.High)
                {
                    bool merged = false;
                    for (int k = 0; k < highClusterCount; k++)
                    {
                        if (Math.Abs(highClusters[k].ScreenY - cand.ScreenY) <= clusterTolerancePx)
                        {
                            var existing = highClusters[k];
                            bool isHigher = cand.Price > existing.Price;
                            decimal p = isHigher ? cand.Price : existing.Price;
                            double sy = isHigher ? cand.ScreenY : existing.ScreenY;
                            float sny = isHigher ? cand.SnappedY : existing.SnappedY;
                            double prom = Math.Max(existing.Prominence, cand.Prominence);
                            int seg = isHigher ? cand.SegmentIndex : existing.SegmentIndex;
                            double t = isHigher ? cand.ParameterT : existing.ParameterT;

                            highClusters[k] = new ExtremaLevel(p, sy, sny, ExtremaType.High, seg, t, existing.TouchCount + 1, prom);
                            merged = true;
                            break;
                        }
                    }
                    if (!merged && highClusterCount < highClusters.Length)
                    {
                        highClusters[highClusterCount++] = cand;
                    }
                }
                else
                {
                    bool merged = false;
                    for (int k = 0; k < lowClusterCount; k++)
                    {
                        if (Math.Abs(lowClusters[k].ScreenY - cand.ScreenY) <= clusterTolerancePx)
                        {
                            var existing = lowClusters[k];
                            bool isLower = cand.Price < existing.Price;
                            decimal p = isLower ? cand.Price : existing.Price;
                            double sy = isLower ? cand.ScreenY : existing.ScreenY;
                            float sny = isLower ? cand.SnappedY : existing.SnappedY;
                            double prom = Math.Max(existing.Prominence, cand.Prominence);
                            int seg = isLower ? cand.SegmentIndex : existing.SegmentIndex;
                            double t = isLower ? cand.ParameterT : existing.ParameterT;

                            lowClusters[k] = new ExtremaLevel(p, sy, sny, ExtremaType.Low, seg, t, existing.TouchCount + 1, prom);
                            merged = true;
                            break;
                        }
                    }
                    if (!merged && lowClusterCount < lowClusters.Length)
                    {
                        lowClusters[lowClusterCount++] = cand;
                    }
                }
            }

            // 3. Ranking & Top-N Selection
            SortClusters(highClusters.Slice(0, highClusterCount));
            SortClusters(lowClusters.Slice(0, lowClusterCount));

            int takeHigh = maxLevels > 0 ? Math.Min(maxLevels, highClusterCount) : highClusterCount;
            for (int i = 0; i < takeHigh; i++)
            {
                outputList.Add(highClusters[i]);
            }

            int takeLow = maxLevels > 0 ? Math.Min(maxLevels, lowClusterCount) : lowClusterCount;
            for (int i = 0; i < takeLow; i++)
            {
                outputList.Add(lowClusters[i]);
            }
        }
        finally
        {
            if (rentedCandidates != null) ArrayPool<ExtremaLevel>.Shared.Return(rentedCandidates);
            if (rentedHigh != null) ArrayPool<ExtremaLevel>.Shared.Return(rentedHigh);
            if (rentedLow != null) ArrayPool<ExtremaLevel>.Shared.Return(rentedLow);
        }
    }

    [ThreadStatic]
    private static SKPath? t_segmentScratchPath;

    [ThreadStatic]
    private static SKPathMeasure? t_segmentMeasure;

    /// <summary>
    /// 隣接する制御点ペア(points[i], points[i+1])ごとに、実際に描画される曲線区間の弧長境界
    /// （パス全体の弧長に対する累積終端位置）と、制御点ペア自体の直線ベクトルの向き（度）を算出する。
    /// <see cref="BuildCatmullRomSplinePath"/>が生成するパスと1:1で対応するよう、同一の縮退判定
    /// （<see cref="DistanceSquared"/> &lt; <see cref="EpsilonSquared"/>で当該ペアをスキップ）を用いる。
    /// Zero-Allocation: 曲線区間の弧長計測にはスレッド静的な使い捨てパス/計測器を再利用する。
    /// </summary>
    /// <returns>実際に書き込まれた区間数（縮退ペアを除く）。</returns>
    public static int ComputeSegmentArcLengthsAndDirections(
        ReadOnlySpan<SKPoint> points,
        bool isSmooth,
        double tension,
        Span<float> destinationEndArcLengths,
        Span<float> destinationDirectionAngles)
    {
        int count = points.Length;
        if (count < 2) return 0;
        if (destinationEndArcLengths.Length < count - 1 || destinationDirectionAngles.Length < count - 1)
            throw new ArgumentException("Destination spans must have a capacity of at least points.Length - 1.");

        bool useCubic = isSmooth && count >= 3;
        SKPath? scratch = null;
        SKPathMeasure? measure = null;
        if (useCubic)
        {
            scratch = t_segmentScratchPath ??= new SKPath();
            measure = t_segmentMeasure ??= new SKPathMeasure();
        }

        float cumulative = 0f;
        int written = 0;

        for (int i = 0; i < count - 1; i++)
        {
            SKPoint pCurr = points[i];
            SKPoint pNext = points[i + 1];

            if (DistanceSquared(pCurr, pNext) < EpsilonSquared)
                continue;

            float segLength;
            if (useCubic)
            {
                SKPoint pPrev = (i == 0)
                    ? new SKPoint(2f * points[0].X - points[1].X, 2f * points[0].Y - points[1].Y)
                    : points[i - 1];

                SKPoint pNextNext = (i == count - 2)
                    ? new SKPoint(2f * points[count - 1].X - points[count - 2].X, 2f * points[count - 1].Y - points[count - 2].Y)
                    : points[i + 2];

                CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out var c1, out var c2);

                scratch!.Reset();
                scratch.MoveTo(pCurr);
                scratch.CubicTo(c1, c2, pNext);
                measure!.SetPath(scratch, false);
                segLength = measure.Length;
            }
            else
            {
                segLength = MathF.Sqrt(DistanceSquared(pCurr, pNext));
            }

            cumulative += segLength;
            destinationEndArcLengths[written] = cumulative;
            destinationDirectionAngles[written] = MathF.Atan2(pNext.Y - pCurr.Y, pNext.X - pCurr.X) * PathTextRenderer.RadToDeg;
            written++;
        }

        return written;
    }

    private static void SortClusters(Span<ExtremaLevel> clusters)
    {
        for (int i = 1; i < clusters.Length; i++)
        {
            var key = clusters[i];
            int j = i - 1;
            while (j >= 0 && CompareClusters(clusters[j], key) < 0)
            {
                clusters[j + 1] = clusters[j];
                j--;
            }
            clusters[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareClusters(ExtremaLevel a, ExtremaLevel b)
    {
        if (a.TouchCount != b.TouchCount)
            return a.TouchCount.CompareTo(b.TouchCount);

        int promComp = a.Prominence.CompareTo(b.Prominence);
        if (promComp != 0)
            return promComp;

        // Tie-breaker 1: Price (For High: higher price first; For Low: lower price first)
        int priceComp = a.Type == ExtremaType.High
            ? a.Price.CompareTo(b.Price)
            : b.Price.CompareTo(a.Price);
        if (priceComp != 0)
            return priceComp;

        // Tie-breaker 2: Time/Segment order (Earlier first)
        int segComp = b.SegmentIndex.CompareTo(a.SegmentIndex);
        if (segComp != 0)
            return segComp;

        return b.ParameterT.CompareTo(a.ParameterT);
    }
}
