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

public class FibonacciEllipseObjectTests
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
        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(200, 250));

        var ellipse = new FibonacciEllipseObject(p0, p1, p2);

        Assert.Equal(ChartObjectType.FibonacciEllipse, ellipse.Type);
        Assert.Equal(3, ellipse.Points.Count);
        Assert.Equal(p0, ellipse.Points[0]);
        Assert.Equal(p1, ellipse.Points[1]);
        Assert.Equal(p2, ellipse.Points[2]);
    }

    [Fact]
    public void HitTest_Handles_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(200, 250));

        var ellipse = new FibonacciEllipseObject(p0, p1, p2);

        // Handles hit test
        Assert.True(ellipse.HitTest(new Point(100, 300), transform, tolerance: 3.0));
        Assert.True(ellipse.HitTest(new Point(300, 300), transform, tolerance: 3.0));
        Assert.True(ellipse.HitTest(new Point(200, 250), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_Boundary_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        // Horizontal major axis from (100, 300) to (300, 300). Center = (200, 300), a0 = 100
        // Minor axis reference point (200, 250) -> b0 = 50
        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(200, 250));

        var ellipse = new FibonacciEllipseObject(p0, p1, p2);

        // Top of base ellipse (level 1.0): (200, 300 - 50) = (200, 250)
        Assert.True(ellipse.HitTest(new Point(200, 250), transform, tolerance: 3.0));
        // Bottom of base ellipse (level 1.0): (200, 300 + 50) = (200, 350)
        Assert.True(ellipse.HitTest(new Point(200, 350), transform, tolerance: 3.0));
        // Right vertex of base ellipse (level 1.0): (200 + 100, 300) = (300, 300)
        Assert.True(ellipse.HitTest(new Point(300, 300), transform, tolerance: 3.0));
        // Left vertex of base ellipse (level 1.0): (200 - 100, 300) = (100, 300)
        Assert.True(ellipse.HitTest(new Point(100, 300), transform, tolerance: 3.0));

        // Far away point
        Assert.False(ellipse.HitTest(new Point(500, 500), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_FilledEllipse_InsideReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(200, 250));

        var ellipse = new FibonacciEllipseObject(p0, p1, p2)
        {
            IsFilled = true
        };

        // Center point (200, 300) should hit when filled
        Assert.True(ellipse.HitTest(new Point(200, 300), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_InsufficientPoints_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var ellipse = new FibonacciEllipseObject();

        Assert.False(ellipse.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void Render_DrawsToCanvasWithoutException()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 300));
        var p1 = transform.ScreenToChart(new Point(300, 200)); // Angled
        var p2 = transform.ScreenToChart(new Point(200, 280));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        using var ellipse = new FibonacciEllipseObject(p0, p1, p2)
        {
            IsFilled = true
        };

        ellipse.Render(canvas, transform);
    }

    [Fact]
    public void Translate_MovesAllThreePoints()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 5), 150m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 3), 120m);

        var ellipse = new FibonacciEllipseObject(p0, p1, p2);
        ellipse.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(new DateTime(2025, 1, 3), ellipse.Points[0].Time);
        Assert.Equal(110m, ellipse.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 7), ellipse.Points[1].Time);
        Assert.Equal(160m, ellipse.Points[1].Price);
        Assert.Equal(new DateTime(2025, 1, 5), ellipse.Points[2].Time);
        Assert.Equal(130m, ellipse.Points[2].Price);
    }
}
