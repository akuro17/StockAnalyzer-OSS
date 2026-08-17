namespace StockAnalyzer.Avalonia.Drawing;

using System;
using SkiaSharp;

/// <summary>
/// Factory for generating exact quadratic rational B-spline conic sections:
/// - Circle (9-point quadratic rational B-spline, w = [1, 1/sqrt(2), 1, ...])
/// - Ellipse (9-point quadratic rational B-spline with semi-axes Rx, Ry)
/// - Parabola (3-point quadratic rational B-spline, w1 = 1.0)
/// - Hyperbola (3-point quadratic rational B-spline, w1 > 1.0)
/// </summary>
public static class NurbsConicFactory
{
    public static readonly double InvSqrt2 = 1.0 / Math.Sqrt(2.0);

    public static readonly double[] CircleWeights =
    [
        1.0, InvSqrt2, 1.0, InvSqrt2, 1.0, InvSqrt2, 1.0, InvSqrt2, 1.0
    ];

    public static readonly double[] CircleKnots =
    [
        0.0, 0.0, 0.0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1.0, 1.0, 1.0
    ];

    public static readonly double[] Conic3PointKnots =
    [
        0.0, 0.0, 0.0, 1.0, 1.0, 1.0
    ];

    public static readonly double[] ParabolaWeights =
    [
        1.0, 1.0, 1.0
    ];

    /// <summary>
    /// Computes the 9 control points for an exact circle centered at (center.X, center.Y) with radius R.
    /// Destination span must have length >= 9.
    /// </summary>
    public static void CalculateCircleControlPoints(SKPoint center, float radius, Span<SKPoint> destination)
    {
        CalculateEllipseControlPoints(center, radius, radius, destination);
    }

    /// <summary>
    /// Computes the 9 control points for an exact ellipse centered at (center.X, center.Y) with semi-axes (radiusX, radiusY).
    /// Destination span must have length >= 9.
    /// </summary>
    public static void CalculateEllipseControlPoints(SKPoint center, float radiusX, float radiusY, Span<SKPoint> destination)
    {
        if (destination.Length < 9)
            throw new ArgumentException("Destination span must have at least 9 elements.", nameof(destination));

        float cx = center.X;
        float cy = center.Y;
        float rx = Math.Max(0f, MathF.Abs(radiusX));
        float ry = Math.Max(0f, MathF.Abs(radiusY));

        destination[0] = new SKPoint(cx + rx, cy);
        destination[1] = new SKPoint(cx + rx, cy + ry);
        destination[2] = new SKPoint(cx, cy + ry);
        destination[3] = new SKPoint(cx - rx, cy + ry);
        destination[4] = new SKPoint(cx - rx, cy);
        destination[5] = new SKPoint(cx - rx, cy - ry);
        destination[6] = new SKPoint(cx, cy - ry);
        destination[7] = new SKPoint(cx + rx, cy - ry);
        destination[8] = new SKPoint(cx + rx, cy);
    }

    /// <summary>
    /// Builds a 3-point parabolic path (w1 = 1.0) into destinationPath.
    /// </summary>
    public static void BuildParabolaPath(SKPath destinationPath, ReadOnlySpan<SKPoint> points3)
    {
        if (destinationPath == null || points3.Length < 3) return;
        NurbsMath.BuildNurbsPath(destinationPath, 2, points3[..3], ParabolaWeights, Conic3PointKnots);
    }

    /// <summary>
    /// Builds a 3-point hyperbolic path (w1 > 1.0, default 2.0) into destinationPath.
    /// </summary>
    public static void BuildHyperbolaPath(SKPath destinationPath, ReadOnlySpan<SKPoint> points3, double centerWeight = 2.0)
    {
        if (destinationPath == null || points3.Length < 3) return;
        double w1 = Math.Clamp(centerWeight, 1.01, 100.0);
        Span<double> weights = stackalloc double[3] { 1.0, w1, 1.0 };
        NurbsMath.BuildNurbsPath(destinationPath, 2, points3[..3], weights, Conic3PointKnots);
    }
}
