using System;
using System.Collections.Generic;
using global::Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class FibonacciSpiralObjectTests
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
    public void Constructor_DefaultProperties_InitializedCorrectly()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(100, 100));
        var start = transform.ScreenToChart(new Point(120, 100));

        var spiral = new FibonacciSpiralObject(center, start);

        Assert.Equal(ChartObjectType.FibonacciSpiral, spiral.Type);
        Assert.Equal(2, spiral.Points.Count);
        Assert.Equal(center, spiral.Points[0]);
        Assert.Equal(start, spiral.Points[1]);
    }

    [Fact]
    public void HitTest_Handles_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var start = transform.ScreenToChart(new Point(250, 200));

        var spiral = new FibonacciSpiralObject(center, start);

        // Center hit
        Assert.True(spiral.HitTest(new Point(200, 200), transform, tolerance: 3.0));
        // Start handle hit
        Assert.True(spiral.HitTest(new Point(250, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_PointOnSpiralCurve_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var start = transform.ScreenToChart(new Point(250, 200)); // rInit = 50, theta = 0

        var spiral = new FibonacciSpiralObject(center, start);

        // Quadrant 1 end (theta = pi/2): (200, 200 + 50 * 1.618034) = (200, 280.9)
        double q1Y = 200.0 + 50.0 * BezierSplineMath.GoldenRatioPhi;
        Assert.True(spiral.HitTest(new Point(200, q1Y), transform, tolerance: 3.0));

        // Point far off
        Assert.False(spiral.HitTest(new Point(400, 100), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_InsufficientPoints_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var spiral = new FibonacciSpiralObject();

        Assert.False(spiral.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void Render_DrawsToCanvasWithoutException()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var start = transform.ScreenToChart(new Point(250, 200));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        using var spiral = new FibonacciSpiralObject(center, start);
        spiral.Render(canvas, transform);
    }

    [Fact]
    public void Translate_MovesCenterAndStart()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 5), 150m);

        var spiral = new FibonacciSpiralObject(p0, p1);
        spiral.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(new DateTime(2025, 1, 3), spiral.Points[0].Time);
        Assert.Equal(110m, spiral.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 7), spiral.Points[1].Time);
        Assert.Equal(160m, spiral.Points[1].Price);
    }
}
