using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class NurbsObjectsTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    [Fact]
    public void NurbsTrendCurveObject_PropertiesAndDefaults()
    {
        var curve = new NurbsTrendCurveObject();
        Assert.Equal(ChartObjectType.NurbsTrendCurve, curve.Type);
        Assert.Equal(3, curve.Degree);

        curve.Degree = 10;
        Assert.Equal(5, curve.Degree); // Clamped to MaxDegree 5

        curve.Degree = 0;
        Assert.Equal(1, curve.Degree); // Clamped to Min 1
    }

    [Fact]
    public void NurbsTrendCurveObject_WeightsSynchronization()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(200, 300));
        var p2 = transform.ScreenToChart(new Point(300, 100));

        var curve = new NurbsTrendCurveObject([p0, p1]);
        Assert.Equal(2, curve.Weights.Count);
        Assert.Equal(1.0, curve.GetWeight(0));
        Assert.Equal(1.0, curve.GetWeight(1));

        curve.AddPoint(p2, 2.5);
        Assert.Equal(3, curve.Points.Count);
        Assert.Equal(3, curve.Weights.Count);
        Assert.Equal(2.5, curve.GetWeight(2));

        curve.SetWeight(1, 10.0);
        Assert.Equal(10.0, curve.GetWeight(1));
    }

    [Fact]
    public void NurbsTrendCurveObject_HitTest_AccurateDetection()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(300, 400));
        var p2 = transform.ScreenToChart(new Point(500, 200));

        var curve = new NurbsTrendCurveObject([p0, p1, p2]);

        // Start and end points must hit
        Assert.True(curve.HitTest(new Point(100, 200), transform, 5.0));
        Assert.True(curve.HitTest(new Point(500, 200), transform, 5.0));

        // Midpoint at t=0.5 for uniform degree 2 is at X=300, Y=300
        Assert.True(curve.HitTest(new Point(300, 300), transform, 5.0));

        // Far away points must not hit
        Assert.False(curve.HitTest(new Point(0, 0), transform, 5.0));
        Assert.False(curve.HitTest(new Point(300, 100), transform, 5.0));
    }

    [Fact]
    public void NurbsConicFactory_CalculateCircleControlPoints_CalculatesCorrectNinePoints()
    {
        SKPoint center = new SKPoint(100f, 100f);
        float radius = 50f;
        Span<SKPoint> pts = stackalloc SKPoint[9];

        NurbsConicFactory.CalculateCircleControlPoints(center, radius, pts);

        // P0 and P8 must be (cx + r, cy) = (150, 100)
        Assert.Equal(new SKPoint(150f, 100f), pts[0]);
        Assert.Equal(new SKPoint(150f, 100f), pts[8]);

        // P2 must be (cx, cy + r) = (100, 150)
        Assert.Equal(new SKPoint(100f, 150f), pts[2]);

        // P4 must be (cx - r, cy) = (50, 100)
        Assert.Equal(new SKPoint(50f, 100f), pts[4]);

        // P6 must be (cx, cy - r) = (100, 50)
        Assert.Equal(new SKPoint(100f, 50f), pts[6]);
    }

    [Fact]
    public void NurbsConicFactory_CalculateEllipseControlPoints_CalculatesCorrectNinePoints()
    {
        SKPoint center = new SKPoint(200f, 150f);
        float rx = 80f;
        float ry = 40f;
        Span<SKPoint> pts = stackalloc SKPoint[9];

        NurbsConicFactory.CalculateEllipseControlPoints(center, rx, ry, pts);

        Assert.Equal(new SKPoint(280f, 150f), pts[0]);
        Assert.Equal(new SKPoint(280f, 190f), pts[1]);
        Assert.Equal(new SKPoint(200f, 190f), pts[2]);
        Assert.Equal(new SKPoint(120f, 190f), pts[3]);
        Assert.Equal(new SKPoint(120f, 150f), pts[4]);
        Assert.Equal(new SKPoint(120f, 110f), pts[5]);
        Assert.Equal(new SKPoint(200f, 110f), pts[6]);
        Assert.Equal(new SKPoint(280f, 110f), pts[7]);
        Assert.Equal(new SKPoint(280f, 150f), pts[8]);
    }

    [Fact]
    public void NurbsConicFactory_BuildParabolaAndHyperbolaPath_PopulatesPaths()
    {
        using var parabolaPath = new SKPath();
        using var hyperbolaPath = new SKPath();

        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(100, 200), new SKPoint(200, 0)];

        NurbsConicFactory.BuildParabolaPath(parabolaPath, pts);
        Assert.False(parabolaPath.IsEmpty);
        Assert.True(parabolaPath.PointCount > 2);

        NurbsConicFactory.BuildHyperbolaPath(hyperbolaPath, pts, 3.0);
        Assert.False(hyperbolaPath.IsEmpty);
        Assert.True(hyperbolaPath.PointCount > 2);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.70710678)] // 1/sqrt(2): exact circular arc
    [InlineData(0.9)]
    public void NurbsConicFactory_BuildConicArcPath_ProducesBoundedArc(double weight)
    {
        using var path = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(100, 200), new SKPoint(200, 0)];

        NurbsConicFactory.BuildConicArcPath(path, pts, weight);

        Assert.False(path.IsEmpty);
        Assert.True(path.PointCount > 2);

        // A bounded conic arc (0 < w1 < 1) must start and end at the given endpoints,
        // unlike the open/unbounded parabola and hyperbola curves.
        Assert.Equal(pts[0], path.Points[0]);
        Assert.Equal(pts[2], path.Points[path.PointCount - 1]);

        // The curve must stay within the bounding box of the 3 control points (bounded, closed arc).
        foreach (var p in path.Points)
        {
            Assert.InRange(p.X, -0.5f, 200.5f);
            Assert.InRange(p.Y, -0.5f, 200.5f);
        }
    }

    [Fact]
    public void NurbsConicFactory_BuildConicArcPath_ClampsWeightToOpenUnitRange()
    {
        using var lowPath = new SKPath();
        using var highPath = new SKPath();
        SKPoint[] pts = [new SKPoint(0, 0), new SKPoint(100, 200), new SKPoint(200, 0)];

        // Weights outside (0, 1) must be clamped rather than producing a degenerate/invalid path.
        NurbsConicFactory.BuildConicArcPath(lowPath, pts, -5.0);
        NurbsConicFactory.BuildConicArcPath(highPath, pts, 5.0);

        Assert.False(lowPath.IsEmpty);
        Assert.False(highPath.IsEmpty);
    }

    [Fact]
    public void NurbsConicObject_HitTest_CircumferenceAndFill()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(300, 300));
        var edge = transform.ScreenToChart(new Point(300, 400)); // Radius = 100

        var conic = new NurbsConicObject(center, edge);
        Assert.Equal(ChartObjectType.NurbsConic, conic.Type);

        // Point on circumference (300, 400), (400, 300), (200, 300) must hit
        Assert.True(conic.HitTest(new Point(300, 400), transform, 5.0));
        Assert.True(conic.HitTest(new Point(400, 300), transform, 5.0));
        Assert.True(conic.HitTest(new Point(200, 300), transform, 5.0));

        // Center point (300, 300) should NOT hit when not filled
        Assert.False(conic.HitTest(new Point(300, 300), transform, 5.0));

        // Center point (300, 300) MUST hit when filled
        conic.IsFilled = true;
        conic.FillColor = Colors.Orange;
        Assert.True(conic.HitTest(new Point(300, 300), transform, 5.0));

        // Point far outside circle (300, 550) must not hit
        Assert.False(conic.HitTest(new Point(300, 550), transform, 5.0));
    }

    [Fact]
    public void NurbsObjects_DrawGeometry_ExecutionSafety()
    {
        var transform = new DummyCoordinateTransform();
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(200, 200));
        var p2 = transform.ScreenToChart(new Point(300, 100));

        var curve = new NurbsTrendCurveObject([p0, p1, p2]);
        var conic = new NurbsConicObject(p0, p1) { IsFilled = true, FillColor = Colors.Blue };
        var ellipse = new NurbsEllipseObject(p0, p1) { IsFilled = true, FillColor = Colors.Red };
        var parabola = new NurbsParabolaObject(p0, p1, p2);
        var hyperbola = new NurbsHyperbolaObject(p0, p1, p2) { Weight = 3.0 };
        var conicArc = new NurbsConicArcObject(p0, p1, p2);

        // Should draw without exceptions
        curve.Render(canvas, transform);
        conic.Render(canvas, transform);
        ellipse.Render(canvas, transform);
        parabola.Render(canvas, transform);
        hyperbola.Render(canvas, transform);
        conicArc.Render(canvas, transform);

        curve.Dispose();
        conic.Dispose();
        ellipse.Dispose();
        parabola.Dispose();
        hyperbola.Dispose();
        conicArc.Dispose();
    }

    [Fact]
    public void DedicatedConicObjects_DefaultColorsAndTypes()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(200, 200));
        var p2 = transform.ScreenToChart(new Point(300, 100));

        var curve = new NurbsTrendCurveObject();
        Assert.Equal(Colors.Green, curve.Color);

        var conic = new NurbsConicObject();
        Assert.Equal(Colors.Green, conic.Color);

        var ellipse = new NurbsEllipseObject(p0, p1);
        Assert.Equal(ChartObjectType.NurbsEllipse, ellipse.Type);
        Assert.Equal(Colors.Green, ellipse.Color);

        var parabola = new NurbsParabolaObject(p0, p1, p2);
        Assert.Equal(ChartObjectType.NurbsParabola, parabola.Type);
        Assert.Equal(Colors.Green, parabola.Color);

        var hyperbola = new NurbsHyperbolaObject(p0, p1, p2);
        Assert.Equal(ChartObjectType.NurbsHyperbola, hyperbola.Type);
        Assert.Equal(Colors.Green, hyperbola.Color);
        Assert.Equal(2.0, hyperbola.Weight);

        var conicArc = new NurbsConicArcObject(p0, p1, p2);
        Assert.Equal(ChartObjectType.NurbsConicArc, conicArc.Type);
        Assert.Equal(Colors.Green, conicArc.Color);
        Assert.Equal(NurbsConicFactory.InvSqrt2, conicArc.Weight);

        conicArc.Weight = 5.0;
        Assert.Equal(0.99, conicArc.Weight); // Clamped to Max 0.99

        conicArc.Weight = -1.0;
        Assert.Equal(0.01, conicArc.Weight); // Clamped to Min 0.01
    }

    [Fact]
    public void DedicatedConicObjects_HitTest_AccurateDetection()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250)); // rx = 100, ry = 50

        var ellipse = new NurbsEllipseObject(center, corner);
        Assert.True(ellipse.HitTest(new Point(300, 200), transform, 5.0)); // (cx+rx, cy)
        Assert.True(ellipse.HitTest(new Point(200, 250), transform, 5.0)); // (cx, cy+ry)
        Assert.False(ellipse.HitTest(new Point(200, 200), transform, 5.0)); // Center when not filled
        ellipse.IsFilled = true;
        ellipse.FillColor = Colors.Red;
        Assert.True(ellipse.HitTest(new Point(200, 200), transform, 5.0)); // Center when filled

        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(200, 100));
        var p2 = transform.ScreenToChart(new Point(300, 300));

        var parabola = new NurbsParabolaObject(p0, p1, p2);
        Assert.True(parabola.HitTest(new Point(100, 300), transform, 5.0)); // start
        Assert.True(parabola.HitTest(new Point(300, 300), transform, 5.0)); // end
        Assert.False(parabola.HitTest(new Point(0, 0), transform, 5.0)); // far away

        var hyperbola = new NurbsHyperbolaObject(p0, p1, p2) { Weight = 2.0 };
        Assert.True(hyperbola.HitTest(new Point(100, 300), transform, 5.0));
        Assert.True(hyperbola.HitTest(new Point(300, 300), transform, 5.0));
        Assert.False(hyperbola.HitTest(new Point(0, 0), transform, 5.0));

        var conicArc = new NurbsConicArcObject(p0, p1, p2);
        Assert.True(conicArc.HitTest(new Point(100, 300), transform, 5.0)); // start
        Assert.True(conicArc.HitTest(new Point(300, 300), transform, 5.0)); // end
        Assert.False(conicArc.HitTest(new Point(0, 0), transform, 5.0)); // far away
    }
}
