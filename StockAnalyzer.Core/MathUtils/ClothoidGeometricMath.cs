using System;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// High-precision, zero-allocation 2D screen-space geometric Clothoid (Cornu spiral / Euler curve) engine.
/// Computes exact C0 boundary-constrained clothoid arcs connecting two arbitrary screen points
/// with continuous linear curvature variation kappa(s) = c * s.
/// </summary>
public static class ClothoidGeometricMath
{
    private const double MaxAngleDeflection = Math.PI * 0.95; // Guard against self-intersecting loops
    public const int MaxSamplePoints = 128;
    public const int MinSamplePoints = 16;
    private const double BaseDensityPx = 5.0;
    private const double DensityCurvatureFactor = 0.5;

    /// <summary>
    /// Generates a smooth geometric clothoid arc between start and end in screen coordinates.
    /// Guarantees exact C0 endpoint matching (destination[0] == start, destination[count-1] == end)
    /// and zero heap allocation.
    /// </summary>
    /// <param name="start">Start coordinate (P0).</param>
    /// <param name="end">End coordinate (P1).</param>
    /// <param name="curvatureIntensity">Normalized curvature parameter in [-1.0, 1.0]. 0 = straight line, >0 = rightward curve, &lt;0 = leftward curve.</param>
    /// <param name="destination">Target span to receive points (length must be &gt;= 2).</param>
    /// <param name="terminalTangentAngle">Outputs the exact tangent angle (radians) at end point P1 for arrowhead alignment.</param>
    /// <returns>Number of points written to destination.</returns>
    public static int GenerateClothoidArc(
        SKPoint start,
        SKPoint end,
        double curvatureIntensity,
        Span<SKPoint> destination,
        out double terminalTangentAngle)
    {
        if (destination.Length < 2)
        {
            terminalTangentAngle = 0.0;
            return 0;
        }

        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double chordLength = Math.Sqrt(dx * dx + dy * dy);
        double chordAngle = Math.Atan2(dy, dx);

        // Guard 1: Degenerate distance (< 1 px), nearly straight (< 1e-5), or buffer insufficient for curved clothoid
        if (chordLength < 1.0 || Math.Abs(curvatureIntensity) < 1e-5 || destination.Length < MinSamplePoints + 1)
        {
            destination[0] = start;
            destination[1] = end;
            terminalTangentAngle = chordAngle;
            return 2;
        }

        // Clamp intensity and determine curvature rate c (c in [-pi, pi])
        double clampedIntensity = Math.Clamp(curvatureIntensity, -1.0, 1.0);
        double c = clampedIntensity * MaxAngleDeflection;

        // Adaptive step count: Scales with chord length and curvature intensity (up to MaxSamplePoints)
        double density = BaseDensityPx / (1.0 + Math.Abs(clampedIntensity) * DensityCurvatureFactor);
        int maxAllowed = Math.Min(MaxSamplePoints, destination.Length - 1);
        int minAllowed = Math.Min(MinSamplePoints, maxAllowed);
        int stepCount = (int)Math.Clamp(chordLength / density, minAllowed, maxAllowed);
        double h = 1.0 / stepCount;

        // 1. Numerically integrate raw unrotated Fresnel clothoid trajectory using Simpson's 1/3 rule
        // theta_raw(u) = 0.5 * c * u^2 (curvature kappa(u) = c * u starts at 0 at tail and increases to c at head)
        Span<double> rawX = stackalloc double[MaxSamplePoints + 1];
        Span<double> rawY = stackalloc double[MaxSamplePoints + 1];

        rawX[0] = 0.0;
        rawY[0] = 0.0;

        double halfC = 0.5 * c;

        for (int i = 0; i < stepCount; i++)
        {
            double u0 = i * h;
            double u1 = (i + 1) * h;
            double uMid = (u0 + u1) * 0.5;

            double theta0 = halfC * u0 * u0;
            double thetaMid = halfC * uMid * uMid;
            double theta1 = halfC * u1 * u1;

            double cos0 = Math.Cos(theta0);
            double sin0 = Math.Sin(theta0);
            double cosMid = Math.Cos(thetaMid);
            double sinMid = Math.Sin(thetaMid);
            double cos1 = Math.Cos(theta1);
            double sin1 = Math.Sin(theta1);

            double dxSegment = (h / 6.0) * (cos0 + 4.0 * cosMid + cos1);
            double dySegment = (h / 6.0) * (sin0 + 4.0 * sinMid + sin1);

            rawX[i + 1] = rawX[i] + dxSegment;
            rawY[i + 1] = rawY[i] + dySegment;
        }

        double netRawX = rawX[stepCount];
        double netRawY = rawY[stepCount];
        double rawChordLength = Math.Sqrt(netRawX * netRawX + netRawY * netRawY);

        if (rawChordLength < 1e-9)
        {
            destination[0] = start;
            destination[1] = end;
            terminalTangentAngle = chordAngle;
            return 2;
        }

        double rawChordAngle = Math.Atan2(netRawY, netRawX);
        double rotationAngle = chordAngle - rawChordAngle;
        double scale = chordLength / rawChordLength;

        double cosRot = Math.Cos(rotationAngle);
        double sinRot = Math.Sin(rotationAngle);

        // Terminal tangent angle at u = 1.0
        // theta_end = rotationAngle + 0.5 * c * (1.0)^2
        terminalTangentAngle = NormalizeAngle(rotationAngle + halfC);

        // 2. Affine transform into final screen coordinates
        destination[0] = start;
        for (int i = 1; i < stepCount; i++)
        {
            double rx = rawX[i];
            double ry = rawY[i];

            // Rotate and scale
            double sx = scale * (rx * cosRot - ry * sinRot);
            double sy = scale * (rx * sinRot + ry * cosRot);

            destination[i] = new SKPoint((float)(start.X + sx), (float)(start.Y + sy));
        }
        destination[stepCount] = end; // Strict exact C0 closure at destination endpoint

        return stepCount + 1;
    }

    /// <summary>
    /// Normalizes angle to (-PI, PI].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2.0 * Math.PI;
        while (angle <= -Math.PI) angle += 2.0 * Math.PI;
        return angle;
    }
}
