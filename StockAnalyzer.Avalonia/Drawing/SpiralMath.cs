namespace StockAnalyzer.Avalonia.Drawing;

using System;
using SkiaSharp;
using StockAnalyzer.Core.MathUtils;

/// <summary>
/// Allocation-free screen-space spiral path construction and hit testing.
/// </summary>
public static class SpiralMath
{
    public const double DefaultMaxTurns = 5.0;
    public const double MinimumTurns = 1.0;
    public const double MaximumTurns = 20.0;
    public const double DefaultLogarithmicGrowthRate = 0.1;
    public const double MaximumLogarithmicGrowthRate = 1.0;
    public const double DefaultArchimedeanPitchPixels = 50.0;
    public const double MaximumArchimedeanPitchPixels = 2000.0;
    private const int CubicSegmentsPerTurn = 16;
    private const double FullTurnRadians = MathConstants.TwoPi;

    public static void BuildLogarithmicPath(SKPath destinationPath, SKPoint center, SKPoint startPoint, SpiralDirection direction, double growthRate, double maxTurns, float maxRadius)
    {
        if (!IsValidInput(destinationPath, center, startPoint, maxTurns, maxRadius) || !IsValidDirection(direction) || growthRate < 0.0 || growthRate > MaximumLogarithmicGrowthRate || !double.IsFinite(growthRate)) return;
        BuildPath(destinationPath, center, startPoint, direction, maxTurns, maxRadius, growthRate, 0.0, true);
    }

    public static void BuildArchimedeanPath(SKPath destinationPath, SKPoint center, SKPoint startPoint, SpiralDirection direction, double pitchPixels, double maxTurns, float maxRadius)
    {
        if (!IsValidInput(destinationPath, center, startPoint, maxTurns, maxRadius) || !IsValidDirection(direction) || pitchPixels < 0.0 || pitchPixels > MaximumArchimedeanPitchPixels || !double.IsFinite(pitchPixels)) return;
        BuildPath(destinationPath, center, startPoint, direction, maxTurns, maxRadius, 0.0, pitchPixels, false);
    }

    public static bool HitTestLogarithmic(SKPoint screenPoint, SKPoint center, SKPoint startPoint, SpiralDirection direction, double growthRate, double maxTurns, float maxRadius, double tolerance)
    {
        if (!IsValidDirection(direction) || growthRate < 0.0 || growthRate > MaximumLogarithmicGrowthRate || !double.IsFinite(growthRate)) return false;
        return HitTestPath(screenPoint, center, startPoint, direction, maxTurns, maxRadius, tolerance, growthRate, 0.0, true);
    }

    public static bool HitTestArchimedean(SKPoint screenPoint, SKPoint center, SKPoint startPoint, SpiralDirection direction, double pitchPixels, double maxTurns, float maxRadius, double tolerance)
    {
        if (!IsValidDirection(direction) || pitchPixels < 0.0 || pitchPixels > MaximumArchimedeanPitchPixels || !double.IsFinite(pitchPixels)) return false;
        return HitTestPath(screenPoint, center, startPoint, direction, maxTurns, maxRadius, tolerance, 0.0, pitchPixels, false);
    }

    private static void BuildPath(SKPath destinationPath, SKPoint center, SKPoint startPoint, SpiralDirection direction, double maxTurns, float maxRadius, double growthRate, double pitchPixels, bool logarithmic)
    {
        double radius = Radius(center, startPoint);
        if (radius < BezierSplineMath.MinRadius || radius > maxRadius) return;
        if ((logarithmic && growthRate == 0.0) || (!logarithmic && pitchPixels == 0.0))
        {
            destinationPath.AddCircle(center.X, center.Y, (float)radius);
            return;
        }

        destinationPath.MoveTo(startPoint);
        GetSegmentCount(maxTurns, out int segmentCount);
        double initialAngle = Math.Atan2(startPoint.Y - center.Y, startPoint.X - center.X);
        double directionSign = direction == SpiralDirection.Clockwise ? 1.0 : -1.0;
        for (int index = 1; index <= segmentCount; index++)
        {
            if (!TryGetCubicSegment(center, startPoint, radius, initialAngle, directionSign, index, segmentCount, maxTurns, maxRadius, growthRate, pitchPixels, logarithmic, out CubicBezierSegment segment, out bool reachedRadiusLimit)) break;
            destinationPath.CubicTo(segment.C1, segment.C2, segment.P3);
            if (reachedRadiusLimit) break;
        }
    }

    private static bool HitTestPath(SKPoint screenPoint, SKPoint center, SKPoint startPoint, SpiralDirection direction, double maxTurns, float maxRadius, double tolerance, double growthRate, double pitchPixels, bool logarithmic)
    {
        if (!IsValidHitInput(screenPoint, center, startPoint, maxTurns, maxRadius, tolerance)) return false;
        double radius = Radius(center, startPoint);
        if (radius < BezierSplineMath.MinRadius || radius > maxRadius) return false;
        if ((logarithmic && growthRate == 0.0) || (!logarithmic && pitchPixels == 0.0))
            return Math.Abs(Radius(center, screenPoint) - radius) <= tolerance;

        GetSegmentCount(maxTurns, out int segmentCount);
        double initialAngle = Math.Atan2(startPoint.Y - center.Y, startPoint.X - center.X);
        double directionSign = direction == SpiralDirection.Clockwise ? 1.0 : -1.0;
        for (int index = 1; index <= segmentCount; index++)
        {
            if (!TryGetCubicSegment(center, startPoint, radius, initialAngle, directionSign, index, segmentCount, maxTurns, maxRadius, growthRate, pitchPixels, logarithmic, out CubicBezierSegment segment, out bool reachedRadiusLimit)) break;
            if (BezierSplineMath.HitTestCubicSegment(screenPoint, segment.P0, segment.C1, segment.C2, segment.P3, tolerance)) return true;
            if (reachedRadiusLimit) break;
        }
        return false;
    }

    private static bool TryGetCubicSegment(
        SKPoint center,
        SKPoint startPoint,
        double initialRadius,
        double initialAngle,
        double directionSign,
        int index,
        int segmentCount,
        double maxTurns,
        float maxRadius,
        double growthRate,
        double pitchPixels,
        bool logarithmic,
        out CubicBezierSegment segment,
        out bool reachedRadiusLimit)
    {
        double previousTraveledAngle = FullTurnRadians * maxTurns * (index - 1) / segmentCount;
        double traveledAngle = FullTurnRadians * maxTurns * index / segmentCount;
        double previousRadius = CalculateRadius(initialRadius, previousTraveledAngle, growthRate, pitchPixels, logarithmic);
        double currentRadius = CalculateRadius(initialRadius, traveledAngle, growthRate, pitchPixels, logarithmic);
        reachedRadiusLimit = false;
        if (!double.IsFinite(previousRadius) || !double.IsFinite(currentRadius) || previousRadius > maxRadius)
        {
            segment = default;
            return false;
        }
        if (currentRadius > maxRadius)
        {
            double radiusLimitAngle = CalculateRadiusLimitAngle(initialRadius, maxRadius, growthRate, pitchPixels, logarithmic);
            if (!double.IsFinite(radiusLimitAngle) || radiusLimitAngle <= previousTraveledAngle)
            {
                segment = default;
                return false;
            }
            traveledAngle = radiusLimitAngle;
            currentRadius = maxRadius;
            reachedRadiusLimit = true;
        }

        double previousAngle = initialAngle + directionSign * previousTraveledAngle;
        double currentAngle = initialAngle + directionSign * traveledAngle;
        double previousRadialDerivative = logarithmic ? growthRate * previousRadius : pitchPixels / FullTurnRadians;
        double currentRadialDerivative = logarithmic ? growthRate * currentRadius : pitchPixels / FullTurnRadians;
        GetPointAndDerivative(center, previousRadius, previousAngle, directionSign, previousRadialDerivative, out SKPoint previousPoint, out SKPoint previousDerivative);
        GetPointAndDerivative(center, currentRadius, currentAngle, directionSign, currentRadialDerivative, out SKPoint currentPoint, out SKPoint currentDerivative);
        float controlScale = (float)((traveledAngle - previousTraveledAngle) / 3.0);
        segment = new CubicBezierSegment(
            index == 1 ? startPoint : previousPoint,
            new SKPoint(previousPoint.X + previousDerivative.X * controlScale, previousPoint.Y + previousDerivative.Y * controlScale),
            new SKPoint(currentPoint.X - currentDerivative.X * controlScale, currentPoint.Y - currentDerivative.Y * controlScale),
            currentPoint);
        return true;
    }

    private static double CalculateRadius(double initialRadius, double traveledAngle, double growthRate, double pitchPixels, bool logarithmic)
        => logarithmic
            ? initialRadius * Math.Exp(growthRate * traveledAngle)
            : initialRadius + pitchPixels * traveledAngle / FullTurnRadians;

    private static double CalculateRadiusLimitAngle(double initialRadius, float maxRadius, double growthRate, double pitchPixels, bool logarithmic)
        => logarithmic
            ? Math.Log(maxRadius / initialRadius) / growthRate
            : (maxRadius - initialRadius) * FullTurnRadians / pitchPixels;

    private static bool IsValidInput(SKPath destinationPath, SKPoint center, SKPoint startPoint, double maxTurns, float maxRadius)
        => destinationPath != null && IsFinite(center) && IsFinite(startPoint) && IsValidLimits(maxTurns, maxRadius);

    private static bool IsValidHitInput(SKPoint screenPoint, SKPoint center, SKPoint startPoint, double maxTurns, float maxRadius, double tolerance)
        => IsFinite(screenPoint) && IsFinite(center) && IsFinite(startPoint) && IsValidLimits(maxTurns, maxRadius) && tolerance >= 0.0 && double.IsFinite(tolerance);

    private static bool IsValidLimits(double maxTurns, float maxRadius)
        => maxTurns >= MinimumTurns && maxTurns <= MaximumTurns && double.IsFinite(maxTurns) && maxRadius > 0.0f && float.IsFinite(maxRadius);

    private static bool IsFinite(SKPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsValidDirection(SpiralDirection direction) => Enum.IsDefined(typeof(SpiralDirection), direction);

    private static double Radius(SKPoint center, SKPoint point)
    {
        double x = point.X - center.X;
        double y = point.Y - center.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static void GetSegmentCount(double maxTurns, out int segmentCount)
        => segmentCount = (int)Math.Ceiling(maxTurns * CubicSegmentsPerTurn);

    private static void GetPointAndDerivative(SKPoint center, double radius, double angle, double directionSign, double radialDerivative, out SKPoint point, out SKPoint derivative)
    {
        double cosine = Math.Cos(angle);
        double sine = Math.Sin(angle);
        point = new SKPoint((float)(center.X + radius * cosine), (float)(center.Y + radius * sine));
        derivative = new SKPoint(
            (float)(radialDerivative * cosine - directionSign * radius * sine),
            (float)(radialDerivative * sine + directionSign * radius * cosine));
    }
}
