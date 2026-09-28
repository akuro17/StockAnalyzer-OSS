using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class CatenaryCurveObjectTests
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
            // Direct 1:1 mapping for tests
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
    public void HitTest_PointOnCurve_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();

        // Screen coords: P0=(100, 200), P1=(500, 200), P2=(300, 260)
        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(500, 200));
        var p2 = transform.ScreenToChart(new Point(300, 260));

        var obj = new CatenaryCurveObject(p0, p1, p2);

        // Test midpoint (300, 260)
        bool hitMid = obj.HitTest(new Point(300, 260), transform, tolerance: 5.0);
        Assert.True(hitMid, "Midpoint of sag should hit");

        // Test intermediate point
        var solved = CatenaryMath.Solve(new Point(100, 200), new Point(500, 200), new Point(300, 260));
        Assert.NotNull(solved);
        double sampleY = solved.Value.EvaluateY(200);

        bool hitSample = obj.HitTest(new Point(200, sampleY), transform, tolerance: 5.0);
        Assert.True(hitSample, "Point on curve at X=200 should hit");
    }

    [Fact]
    public void HitTest_PointFarFromCurve_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();

        var p0 = transform.ScreenToChart(new Point(100, 200));
        var p1 = transform.ScreenToChart(new Point(500, 200));
        var p2 = transform.ScreenToChart(new Point(300, 260));

        var obj = new CatenaryCurveObject(p0, p1, p2);

        // Point far away vertically
        bool hitFarY = obj.HitTest(new Point(300, 100), transform, tolerance: 5.0);
        Assert.False(hitFarY, "Point far away vertically should not hit");

        // Point outside X bounds
        bool hitOutsideX = obj.HitTest(new Point(600, 200), transform, tolerance: 5.0);
        Assert.False(hitOutsideX, "Point outside X bounds should not hit");
    }

    [Fact]
    public void Translate_MovesAllPointsCorrectly()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 150m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 80m);

        var obj = new CatenaryCurveObject(p0, p1, p2);

        obj.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(new DateTime(2025, 1, 3), obj.Points[0].Time);
        Assert.Equal(110m, obj.Points[0].Price);

        Assert.Equal(new DateTime(2025, 1, 13), obj.Points[1].Time);
        Assert.Equal(160m, obj.Points[1].Price);

        Assert.Equal(new DateTime(2025, 1, 8), obj.Points[2].Time);
        Assert.Equal(90m, obj.Points[2].Price);
    }

    [Fact]
    public void SynchronizeMidpoint_AutoCentersP2Time()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 200m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 2), 120m); // Initially non-centered time

        var obj = new CatenaryCurveObject(p0, p1, p2);

        // Constructor automatically calls SynchronizeMidpoint
        Assert.Equal(new DateTime(2025, 1, 6), obj.Points[2].Time);
        Assert.Equal(120m, obj.Points[2].Price);
    }

    [Fact]
    public void CatenaryCurveBehavior_AutoMidpointDuringDrawing()
    {
        var behavior = new CatenaryCurveBehavior();
        var startPoint = new ChartPoint(new DateTime(2025, 1, 1), 100m);

        var obj = behavior.CreateObject(startPoint);
        Assert.Equal(3, obj.Points.Count);

        // Step 1: User dragging P1 to (Jan 11, 200m)
        var p1Drag = new ChartPoint(new DateTime(2025, 1, 11), 200m);
        behavior.UpdatePoint(obj, 1, p1Drag);

        Assert.Equal(p1Drag.Time, obj.Points[1].Time);
        Assert.Equal(p1Drag.Price, obj.Points[1].Price);
        Assert.Equal(new DateTime(2025, 1, 6), obj.Points[2].Time); // Centered at Jan 6
        Assert.Equal(150m, obj.Points[2].Price); // Average of 100 and 200

        // Step 2: User dragging P2 sag to price 250m
        var p2Drag = new ChartPoint(new DateTime(2025, 1, 20), 250m); // Mouse time is ignored, only price applied
        behavior.UpdatePoint(obj, 2, p2Drag);

        Assert.Equal(new DateTime(2025, 1, 6), obj.Points[2].Time); // Still centered at Jan 6
        Assert.Equal(250m, obj.Points[2].Price);
    }

    [Fact]
    public void ManualPriceEditing_PreservesCenteredMidpoint()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 200m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 150m);

        var obj = new CatenaryCurveObject(p0, p1, p2);

        // Simulate dialog manual price changes
        decimal manualP0 = 120m;
        decimal manualP1 = 220m;
        decimal manualP2 = 180m;

        obj.Points[0] = new ChartPoint(obj.Points[0].Time, manualP0);
        obj.Points[1] = new ChartPoint(obj.Points[1].Time, manualP1);
        obj.Points[2] = new ChartPoint(new DateTime((obj.Points[0].Time.Ticks + obj.Points[1].Time.Ticks) / 2), manualP2);

        Assert.Equal(120m, obj.Points[0].Price);
        Assert.Equal(220m, obj.Points[1].Price);
        Assert.Equal(180m, obj.Points[2].Price);
        Assert.Equal(new DateTime(2025, 1, 6), obj.Points[2].Time);
    }
}
