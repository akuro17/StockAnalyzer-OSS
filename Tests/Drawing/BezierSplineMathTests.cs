namespace StockAnalyzer.Tests.Drawing;

using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

public class BezierSplineMathTests
{
    [Fact]
    public void BuildCatmullRomSplinePath_NullPath_ThrowsArgumentNullException()
    {
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(10, 10)];
        Assert.Throws<ArgumentNullException>(() => BezierSplineMath.BuildCatmullRomSplinePath(null!, pts));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BuildCatmullRomSplinePath_InvalidTension_ThrowsArgumentOutOfRangeException(double invalidTension)
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(10, 10), new SKPoint(20, 20)];
        Assert.Throws<ArgumentOutOfRangeException>(() => BezierSplineMath.BuildCatmullRomSplinePath(path, pts, invalidTension));
    }

    [Fact]
    public void BuildCatmullRomSplinePath_LessThanTwoPoints_DoesNotModifyPath()
    {
        using var path = new SKPath();
        BezierSplineMath.BuildCatmullRomSplinePath(path, ReadOnlySpan<SKPoint>.Empty);
        Assert.True(path.IsEmpty);

        SKPoint[] single = [new SKPoint(10, 20)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, single);
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void BuildCatmullRomSplinePath_ContainsNaNOrInf_DoesNotThrowAndNoOp()
    {
        using var path = new SKPath();
        SKPoint[] ptsWithNaN = [new SKPoint(0, 0), new SKPoint(float.NaN, 10), new SKPoint(20, 20)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, ptsWithNaN);
        Assert.True(path.IsEmpty);

        SKPoint[] ptsWithInf = [new SKPoint(0, 0), new SKPoint(10, float.PositiveInfinity), new SKPoint(20, 20)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, ptsWithInf);
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void BuildCatmullRomSplinePath_TwoPoints_CreatesSingleLine()
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(10, 20), new SKPoint(30, 40)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, pts);

        Assert.False(path.IsEmpty);
        Assert.Equal(2, path.PointCount);
        Assert.Equal(new SKPoint(10, 20), path.Points[0]);
        Assert.Equal(new SKPoint(30, 40), path.Points[1]);
    }

    [Fact]
    public void BuildCatmullRomSplinePath_ThreePoints_GeneratesCubicSegments()
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(50, 100), new SKPoint(100, 0)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, pts, 0.5);

        Assert.False(path.IsEmpty);
        // Start point (1) + 2 cubic segments (3 points each) = 7 points
        Assert.Equal(7, path.PointCount);
        Assert.Equal(new SKPoint(0, 0), path.Points[0]);
        Assert.Equal(new SKPoint(100, 0), path.Points[6]);
    }

    [Fact]
    public void BuildCatmullRomSplinePath_CollinearPoints_GeneratesStraightLine()
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(50, 50), new SKPoint(100, 100)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, pts, 0.5);

        Assert.False(path.IsEmpty);
        // Control points should lie on the same straight line y = x
        for (int i = 0; i < path.PointCount; i++)
        {
            Assert.Equal(path.Points[i].X, path.Points[i].Y, 3);
        }
    }

    [Fact]
    public void BuildCatmullRomSplinePath_ConsecutiveDuplicates_SkipsDegenerateSegments()
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(0, 0), new SKPoint(100, 100)];
        BezierSplineMath.BuildCatmullRomSplinePath(path, pts, 0.5);

        Assert.False(path.IsEmpty);
        // Only the valid segment from (0,0) to (100,100) is generated
        Assert.Equal(4, path.PointCount);
    }

    [Fact]
    public void CalculateControlPoints_DefaultTension_MatchesFormula()
    {
        SKPoint pPrev = new SKPoint(0, 0);
        SKPoint pCurr = new SKPoint(10, 0);
        SKPoint pNext = new SKPoint(20, 10);
        SKPoint pNextNext = new SKPoint(30, 10);

        BezierSplineMath.CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, 0.5, out var c1, out var c2);

        // factor = 0.5 / 3 = 1/6
        // c1 = pCurr + 1/6 * (pNext - pPrev) = (10, 0) + 1/6 * (20, 10) = (13.3333, 1.6667)
        // c2 = pNext - 1/6 * (pNextNext - pCurr) = (20, 10) - 1/6 * (20, 10) = (16.6667, 8.3333)
        Assert.Equal(10f + (20f / 6f), c1.X, 4);
        Assert.Equal(0f + (10f / 6f), c1.Y, 4);
        Assert.Equal(20f - (20f / 6f), c2.X, 4);
        Assert.Equal(10f - (10f / 6f), c2.Y, 4);
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_NullPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BezierSplineMath.BuildLogarithmicSpiralPath(null!, new SKPoint(0, 0), new SKPoint(10, 0)));
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_QuadrantCountBoundaries()
    {
        using var path = new SKPath();
        // <= 0 is no-op
        BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(0, 0), new SKPoint(10, 0), 0);
        Assert.True(path.IsEmpty);

        BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(0, 0), new SKPoint(10, 0), -5);
        Assert.True(path.IsEmpty);

        // > MaxSpiralQuadrants throws
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(0, 0), new SKPoint(10, 0), BezierSplineMath.MaxSpiralQuadrants + 1));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-100f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void BuildLogarithmicSpiralPath_InvalidMaxRadius_ThrowsArgumentOutOfRangeException(float invalidMaxRadius)
    {
        using var path = new SKPath();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(0, 0), new SKPoint(10, 0), 4, invalidMaxRadius));
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_NaNOrInfCoordinates_NoOp()
    {
        using var path = new SKPath();
        BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(float.NaN, 0), new SKPoint(10, 0));
        Assert.True(path.IsEmpty);

        BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(0, 0), new SKPoint(10, float.PositiveInfinity));
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_ZeroRadius_NoOp()
    {
        using var path = new SKPath();
        // Start equals center
        BezierSplineMath.BuildLogarithmicSpiralPath(path, new SKPoint(50, 50), new SKPoint(50, 50));
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_StandardSpiral_GeneratesQuadrants()
    {
        using var path = new SKPath();
        SKPoint center = new SKPoint(100, 100);
        SKPoint start = new SKPoint(110, 100); // rInit = 10, thetaInit = 0

        BezierSplineMath.BuildLogarithmicSpiralPath(path, center, start, quadrantCount: 4, maxRadius: 1000f);

        Assert.False(path.IsEmpty);
        // Start point (1) + 4 quadrants (3 points each) = 13 points
        Assert.Equal(13, path.PointCount);
        Assert.Equal(start, path.Points[0]);

        // After 4 quadrants (2*pi radians / 1 full turn), radius should grow by Phi^4
        double expectedRadius = 10.0 * Math.Pow(BezierSplineMath.GoldenRatioPhi, 4.0);
        SKPoint endPt = path.Points[path.PointCount - 1];
        double actualRadius = Math.Sqrt(Math.Pow(endPt.X - center.X, 2) + Math.Pow(endPt.Y - center.Y, 2));

        Assert.Equal(expectedRadius, actualRadius, 2);
    }

    [Fact]
    public void BuildLogarithmicSpiralPath_ExceedsMaxRadius_TerminatesEarly()
    {
        using var path = new SKPath();
        SKPoint center = new SKPoint(0, 0);
        SKPoint start = new SKPoint(50, 0); // rInit = 50

        // maxRadius = 100 -> after quadrant 1 (r = 50 * 1.618 = 80.9), quadrant 2 (r = 80.9 * 1.618 = 130.9 > 100) -> breaks at 2nd quadrant
        BezierSplineMath.BuildLogarithmicSpiralPath(path, center, start, quadrantCount: 16, maxRadius: 100f);

        Assert.False(path.IsEmpty);
        Assert.Equal(4, path.PointCount); // Only 1 quadrant generated
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void HitTestCubicSegment_InvalidTolerance_ThrowsArgumentOutOfRangeException(double invalidTolerance)
    {
        SKPoint p0 = new SKPoint(0, 0);
        SKPoint c1 = new SKPoint(10, 20);
        SKPoint c2 = new SKPoint(20, 20);
        SKPoint p3 = new SKPoint(30, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BezierSplineMath.HitTestCubicSegment(new SKPoint(15, 10), p0, c1, c2, p3, invalidTolerance));
    }

    [Fact]
    public void HitTestCubicSegment_NaNOrInfInputs_ReturnsFalse()
    {
        SKPoint p0 = new SKPoint(0, 0);
        SKPoint c1 = new SKPoint(10, 20);
        SKPoint c2 = new SKPoint(20, 20);
        SKPoint p3 = new SKPoint(30, 0);

        Assert.False(BezierSplineMath.HitTestCubicSegment(new SKPoint(float.NaN, 0), p0, c1, c2, p3, 5.0));
        Assert.False(BezierSplineMath.HitTestCubicSegment(new SKPoint(0, 0), new SKPoint(float.PositiveInfinity, 0), c1, c2, p3, 5.0));
    }

    [Fact]
    public void HitTestCubicSegment_PointsOnOrNearCurve_ReturnsTrue()
    {
        SKPoint p0 = new SKPoint(0, 0);
        SKPoint c1 = new SKPoint(0, 50);
        SKPoint c2 = new SKPoint(100, 50);
        SKPoint p3 = new SKPoint(100, 0);

        // Endpoints
        Assert.True(BezierSplineMath.HitTestCubicSegment(new SKPoint(0, 0), p0, c1, c2, p3, 2.0));
        Assert.True(BezierSplineMath.HitTestCubicSegment(new SKPoint(100, 0), p0, c1, c2, p3, 2.0));

        // Midpoint at u = 0.5: Q2 = (p0 + 3*c1 + 3*c2 + p3)/8 = (0+0+300+100)/8 = 50, (0+150+150+0)/8 = 37.5
        Assert.True(BezierSplineMath.HitTestCubicSegment(new SKPoint(50, 37.5f), p0, c1, c2, p3, 1.0));

        // Near midpoint within tolerance
        Assert.True(BezierSplineMath.HitTestCubicSegment(new SKPoint(50, 40f), p0, c1, c2, p3, 3.0));

        // Far away from curve
        Assert.False(BezierSplineMath.HitTestCubicSegment(new SKPoint(50, 100f), p0, c1, c2, p3, 5.0));
        Assert.False(BezierSplineMath.HitTestCubicSegment(new SKPoint(-50, 0), p0, c1, c2, p3, 5.0));
    }

    [Fact]
    public void DistancePointToSegment_DegenerateSegment_ReturnsDistanceToPoint()
    {
        SKPoint p = new SKPoint(10, 10);
        SKPoint v = new SKPoint(0, 0);
        SKPoint w = new SKPoint(0, 0); // Degenerate

        double dist = BezierSplineMath.DistancePointToSegment(p, v, w);
        Assert.Equal(Math.Sqrt(200), dist, 4);
    }

    [Fact]
    public void DistancePointToSegment_PointOnSegment_ReturnsZero()
    {
        SKPoint v = new SKPoint(0, 0);
        SKPoint w = new SKPoint(100, 0);
        SKPoint p = new SKPoint(50, 0);

        double dist = BezierSplineMath.DistancePointToSegment(p, v, w);
        Assert.Equal(0.0, dist, 5);
    }

    [Fact]
    public void DistancePointToSegment_PerpendicularProjection()
    {
        SKPoint v = new SKPoint(0, 0);
        SKPoint w = new SKPoint(100, 0);
        SKPoint p = new SKPoint(50, 25);

        double dist = BezierSplineMath.DistancePointToSegment(p, v, w);
        Assert.Equal(25.0, dist, 5);
    }

    [Fact]
    public void DistancePointToSegment_BeyondEndpoints_ReturnsDistanceToEndpoint()
    {
        SKPoint v = new SKPoint(0, 0);
        SKPoint w = new SKPoint(100, 0);
        SKPoint p = new SKPoint(130, 40); // Closest to w (100, 0)

        double dist = BezierSplineMath.DistancePointToSegment(p, v, w);
        // Distance from (130, 40) to (100, 0) = sqrt(30^2 + 40^2) = 50
        Assert.Equal(50.0, dist, 5);
    }

    [Fact]
    public void HitTestLogarithmicSpiral_PointOnOrAwayFromSpiral()
    {
        SKPoint center = new SKPoint(0, 0);
        SKPoint start = new SKPoint(10, 0); // rInit = 10, thetaInit = 0

        // Start point itself
        Assert.True(BezierSplineMath.HitTestLogarithmicSpiral(new SKPoint(10, 0), center, start, tolerance: 3.0));

        // After 1 quadrant (theta = pi/2), r = 10 * Phi = 16.18 -> (0, 16.18)
        float q1Y = (float)(10.0 * BezierSplineMath.GoldenRatioPhi);
        Assert.True(BezierSplineMath.HitTestLogarithmicSpiral(new SKPoint(0, q1Y), center, start, tolerance: 3.0));

        // Point completely off the spiral
        Assert.False(BezierSplineMath.HitTestLogarithmicSpiral(new SKPoint(500, 500), center, start, tolerance: 5.0));
    }

    [Fact]
    public void CalculateTimeDecoupledControlPoints_GuaranteesStrictlyLinearX()
    {
        SKPoint pPrev = new SKPoint(0, 100);
        SKPoint pCurr = new SKPoint(30, 200);
        SKPoint pNext = new SKPoint(90, 50);
        SKPoint pNextNext = new SKPoint(150, 300);

        BezierSplineMath.CalculateTimeDecoupledControlPoints(pPrev, pCurr, pNext, pNextNext, 0.5, out var c1, out var c2);

        // X must be exactly pCurr.X + dx/3 = 30 + 60/3 = 50
        Assert.Equal(50f, c1.X);
        // X must be exactly pNext.X - dx/3 = 90 - 60/3 = 70
        Assert.Equal(70f, c2.X);

        // Y must be Catmull-Rom: c1.Y = 200 + (50 - 100)/6 = 200 - 50/6 = 191.66667
        Assert.Equal(200f + (50f - 100f) / 6f, c1.Y, 4);
        // c2.Y = 50 - (300 - 200)/6 = 50 - 100/6 = 33.33333
        Assert.Equal(50f - (300f - 200f) / 6f, c2.Y, 4);
    }

    [Fact]
    public void BuildTimeDecoupledCatmullRomSplinePath_SharpPeak_NoTimeReversal()
    {
        using var path = new SKPath();
        SKPoint[] pts =
        [
            new SKPoint(0, 100),
            new SKPoint(10, 1000), // Huge spike
            new SKPoint(20, 50),
            new SKPoint(30, 100)
        ];

        BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(path, pts, 0.5);

        Assert.False(path.IsEmpty);
        // 1 start point + 3 cubic segments * 3 points = 10 points
        Assert.Equal(10, path.PointCount);

        // Verify X coordinates are strictly monotonically non-decreasing
        for (int i = 0; i < path.PointCount - 1; i++)
        {
            Assert.True(path.Points[i].X <= path.Points[i + 1].X,
                $"Point {i} X ({path.Points[i].X}) must be <= Point {i+1} X ({path.Points[i+1].X})");
        }
    }

    [Fact]
    public void BuildTimeDecoupledCatmullRomSplinePath_InvalidInputs_HandledSafely()
    {
        using var path = new SKPath();
        Assert.Throws<ArgumentNullException>(() => BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(null!, [new SKPoint(0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(path, [new SKPoint(0, 0), new SKPoint(1, 1)], -0.5));

        // NaN input
        SKPoint[] ptsWithNaN = [new SKPoint(0, 0), new SKPoint(float.NaN, 10), new SKPoint(20, 20)];
        BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(path, ptsWithNaN);
        Assert.True(path.IsEmpty);

        // 2 points
        SKPoint[] twoPts = [new SKPoint(0, 10), new SKPoint(20, 30)];
        BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(path, twoPts);
        Assert.Equal(2, path.PointCount);
    }

    [Fact]
    public void TC_61_8_01_FindCubicBezierYExtrema_MonotonicCurve_ReturnsZeroExtrema()
    {
        // P0(0, 0), C1(10, 10), C2(20, 20), P3(30, 30)
        Span<BezierExtremum> dest = stackalloc BezierExtremum[2];
        int count = BezierSplineMath.FindCubicBezierYExtrema(
            new SKPoint(0, 0),
            new SKPoint(10, 10),
            new SKPoint(20, 20),
            new SKPoint(30, 30),
            dest);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TC_61_8_02_FindCubicBezierYExtrema_SinusoidalSCurve_FindsExactTwoExtrema()
    {
        // P0(0, 50), C1(25, 100), C2(75, 0), P3(100, 50)
        Span<BezierExtremum> dest = stackalloc BezierExtremum[2];
        int count = BezierSplineMath.FindCubicBezierYExtrema(
            new SKPoint(0, 50),
            new SKPoint(25, 100),
            new SKPoint(75, 0),
            new SKPoint(100, 50),
            dest,
            segmentIndex: 0);

        Assert.Equal(2, count);

        // Root 1: t1 = (3 - sqrt(3))/6 ≈ 0.211325 => Low (Screen Y ≈ 64.4338)
        double expectedT1 = (3.0 - Math.Sqrt(3.0)) / 6.0;
        Assert.Equal(expectedT1, dest[0].ParameterT, 4);
        Assert.Equal(ExtremaType.Low, dest[0].Type);
        Assert.Equal(64.4338, dest[0].ScreenY, 3);
        Assert.Equal(0, dest[0].SegmentIndex);

        // Root 2: t2 = (3 + sqrt(3))/6 ≈ 0.788675 => High (Screen Y ≈ 35.5662)
        double expectedT2 = (3.0 + Math.Sqrt(3.0)) / 6.0;
        Assert.Equal(expectedT2, dest[1].ParameterT, 4);
        Assert.Equal(ExtremaType.High, dest[1].Type);
        Assert.Equal(35.5662, dest[1].ScreenY, 3);
        Assert.Equal(0, dest[1].SegmentIndex);
    }

    [Fact]
    public void TC_61_8_03_FindCubicBezierYExtrema_HorizontalLine_ReturnsZeroExtremaWithoutException()
    {
        // P0(0, 50), C1(20, 50), C2(40, 50), P3(60, 50)
        Span<BezierExtremum> dest = stackalloc BezierExtremum[2];
        int count = BezierSplineMath.FindCubicBezierYExtrema(
            new SKPoint(0, 50),
            new SKPoint(20, 50),
            new SKPoint(40, 50),
            new SKPoint(60, 50),
            dest);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TC_61_8_04_FindCubicBezierYExtrema_IEEE754Exception_HandledSafely()
    {
        // P0(NaN, 50), C1(20, Inf), C2(40, 50), P3(60, 50)
        Span<BezierExtremum> dest = stackalloc BezierExtremum[2];
        int count = BezierSplineMath.FindCubicBezierYExtrema(
            new SKPoint(float.NaN, 50),
            new SKPoint(20, float.PositiveInfinity),
            new SKPoint(40, 50),
            new SKPoint(60, 50),
            dest);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TC_61_8_05_ExtractSplineExtrema_DuplicateExtremaMerge_MergesWithinTolerance()
    {
        SKPoint[] pts =
        [
            new SKPoint(0, 50),
            new SKPoint(50, 100),
            new SKPoint(100, 50),
            new SKPoint(150, 101),
            new SKPoint(200, 50)
        ];

        Span<BezierExtremum> dest = stackalloc BezierExtremum[8];
        int count = BezierSplineMath.ExtractSplineExtrema(pts, dest, mergeTolerancePx: 2.0);

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (dest[i].Type == dest[j].Type)
                {
                    Assert.True(Math.Abs(dest[i].ScreenY - dest[j].ScreenY) > 2.0,
                        $"Extrema {i} and {j} of same type must differ by > 2.0px");
                }
            }
        }
    }

    [Fact]
    public void TC_61_8_06_ExtractSplineExtrema_SameYDifferentType_RetainsBoth()
    {
        SKPoint[] pts =
        [
            new SKPoint(0, 50),
            new SKPoint(25, 100),
            new SKPoint(75, 0),
            new SKPoint(100, 50)
        ];

        Span<BezierExtremum> dest = stackalloc BezierExtremum[4];
        int count = BezierSplineMath.ExtractSplineExtrema(pts, dest, mergeTolerancePx: 100.0);

        Assert.Equal(2, count);
        var types = new System.Collections.Generic.HashSet<ExtremaType> { dest[0].Type, dest[1].Type };
        Assert.Contains(ExtremaType.High, types);
        Assert.Contains(ExtremaType.Low, types);
    }

    [Fact]
    public void TC_61_8_07_FindCubicBezierYExtrema_BoundaryRootExclusion_RejectsRootsNearEndpoints()
    {
        Span<BezierExtremum> dest = stackalloc BezierExtremum[2];
        int count = BezierSplineMath.FindCubicBezierYExtrema(
            new SKPoint(0, 0),
            new SKPoint(10, 0),
            new SKPoint(20, 50),
            new SKPoint(30, 100),
            dest);

        for (int i = 0; i < count; i++)
        {
            Assert.True(dest[i].ParameterT > BezierSplineMath.RootBoundaryTolerance);
            Assert.True(dest[i].ParameterT < 1.0 - BezierSplineMath.RootBoundaryTolerance);
        }
    }

    [Fact]
    public void TC_61_8_08_ExtractSplineExtrema_LargeSegmentCount_HandlesUsingArrayPoolWithoutTruncation()
    {
        var pts = new SKPoint[41];
        for (int i = 0; i < 41; i++)
        {
            pts[i] = new SKPoint(i * 10f, 100f + 50f * MathF.Sin(i * 0.8f));
        }

        Span<BezierExtremum> dest = stackalloc BezierExtremum[100];
        int count = BezierSplineMath.ExtractSplineExtrema(pts, dest, mergeTolerancePx: 0.0);

        Assert.True(count > 0, "Extrema count should be greater than 0 for sinusoidal segments");
    }

    [Fact]
    public void FilterAndClusterExtrema_ProminenceFilter_FiltersOutMicroRipples()
    {
        // 5 extrema:
        // High(100, Y=100), Low(50, Y=300), High(52, Y=290: minor ripple), Low(50, Y=300), High(99, Y=105)
        var raw = new ExtremaLevel[]
        {
            new(100m, 100.0, 100.5f, ExtremaType.High, 0, 0.5),
            new(50m, 300.0, 300.5f, ExtremaType.Low, 1, 0.5),
            new(52m, 290.0, 290.5f, ExtremaType.High, 2, 0.5), // Prominence = (52-50)/50 = 4%
            new(50m, 300.0, 300.5f, ExtremaType.Low, 3, 0.5),
            new(99m, 105.0, 105.5f, ExtremaType.High, 4, 0.5)
        };

        var output = new List<ExtremaLevel>();
        // With minSwingPercent = 10.0%, the 4% ripple must be filtered out
        BezierSplineMath.FilterAndClusterExtrema(raw, output, minSwingPercent: 10.0, clusterTolerancePx: 2.0, maxLevels: 10);

        var highs = output.Where(x => x.Type == ExtremaType.High).ToList();
        Assert.Equal(2, highs.Count);
        Assert.DoesNotContain(highs, h => h.Price == 52m);
    }

    [Fact]
    public void FilterAndClusterExtrema_Clustering_AccumulatesTouchCount()
    {
        // Two peaks with ScreenY=100 and ScreenY=108 (distance 8px <= 15px)
        var raw = new ExtremaLevel[]
        {
            new(100m, 100.0, 100.5f, ExtremaType.High, 0, 0.5),
            new(50m, 300.0, 300.5f, ExtremaType.Low, 1, 0.5),
            new(98m, 108.0, 108.5f, ExtremaType.High, 2, 0.5) // Double Top
        };

        var output = new List<ExtremaLevel>();
        BezierSplineMath.FilterAndClusterExtrema(raw, output, minSwingPercent: 2.0, clusterTolerancePx: 15.0, maxLevels: 10);

        var highs = output.Where(x => x.Type == ExtremaType.High).ToList();
        Assert.Single(highs);
        Assert.Equal(2, highs[0].TouchCount);
        Assert.Equal(100m, highs[0].Price); // Keeps higher peak price
    }

    [Fact]
    public void FilterAndClusterExtrema_MaxLevels_LimitsOutputPerType()
    {
        var raw = new List<ExtremaLevel>();
        for (int i = 0; i < 6; i++)
        {
            raw.Add(new ExtremaLevel(100m + i * 10m, 100.0 - i * 20.0, (float)(100.0 - i * 20.0), ExtremaType.High, i * 2, 0.5));
            raw.Add(new ExtremaLevel(50m + i * 5m, 300.0 - i * 10.0, (float)(300.0 - i * 10.0), ExtremaType.Low, i * 2 + 1, 0.5));
        }

        var output = new List<ExtremaLevel>();
        // Limit to max 2 levels per type
        BezierSplineMath.FilterAndClusterExtrema(raw.ToArray(), output, minSwingPercent: 0.0, clusterTolerancePx: 2.0, maxLevels: 2);

        var highs = output.Where(x => x.Type == ExtremaType.High).ToList();
        var lows = output.Where(x => x.Type == ExtremaType.Low).ToList();

        Assert.Equal(2, highs.Count);
        Assert.Equal(2, lows.Count);
    }

    [Fact]
    public void FilterAndClusterExtrema_AsymmetricalWiggle_FiltersCorrectlyWithMaxBaseline()
    {
        // High at 100 with leftLow = 99 (shallow 1% dip), rightLow = 50 (deep 50% dip)
        // Span = 100 - 50 = 50.
        // True prominence is 100 - max(99, 50) = 1 (2% of span).
        var raw = new ExtremaLevel[]
        {
            new(99m, 110.0, 110.5f, ExtremaType.Low, 0, 0.5),
            new(100m, 100.0, 100.5f, ExtremaType.High, 1, 0.5),
            new(50m, 300.0, 300.5f, ExtremaType.Low, 2, 0.5)
        };

        var output = new List<ExtremaLevel>();
        // With minSwingPercent = 5.0%, the 2% asymmetrical wiggle must be filtered out
        BezierSplineMath.FilterAndClusterExtrema(raw, output, minSwingPercent: 5.0, clusterTolerancePx: 2.0, maxLevels: 10);

        var highs = output.Where(x => x.Type == ExtremaType.High).ToList();
        Assert.Empty(highs);
    }

    [Fact]
    public void FilterAndClusterExtrema_ZeroSpan_ReturnsEmptyList()
    {
        var raw = new ExtremaLevel[]
        {
            new(100m, 100.0, 100.5f, ExtremaType.High, 0, 0.5),
            new(100m, 100.0, 100.5f, ExtremaType.Low, 1, 0.5),
            new(100m, 100.0, 100.5f, ExtremaType.High, 2, 0.5)
        };

        var output = new List<ExtremaLevel>();
        BezierSplineMath.FilterAndClusterExtrema(raw, output, minSwingPercent: 2.0, clusterTolerancePx: 2.0, maxLevels: 10);

        Assert.Empty(output);
    }

    [Fact]
    public void FilterAndClusterExtrema_TieBreaker_SortsDeterministically()
    {
        // Two peaks with exact same TouchCount=1, Prominence=50%
        // High1 at Price 110 (Seg 0), High2 at Price 105 (Seg 2)
        var raw = new ExtremaLevel[]
        {
            new(50m, 300.0, 300.5f, ExtremaType.Low, 0, 0.0),
            new(110m, 90.0, 90.5f, ExtremaType.High, 0, 0.5),
            new(50m, 300.0, 300.5f, ExtremaType.Low, 1, 0.5),
            new(105m, 100.0, 100.5f, ExtremaType.High, 2, 0.5),
            new(50m, 300.0, 300.5f, ExtremaType.Low, 3, 0.5)
        };

        var output = new List<ExtremaLevel>();
        BezierSplineMath.FilterAndClusterExtrema(raw, output, minSwingPercent: 10.0, clusterTolerancePx: 2.0, maxLevels: 10);

        var highs = output.Where(x => x.Type == ExtremaType.High).ToList();
        Assert.Equal(2, highs.Count);
        // Higher price 110m should come first due to deterministic tie-breaker
        Assert.Equal(110m, highs[0].Price);
        Assert.Equal(105m, highs[1].Price);
    }
}
