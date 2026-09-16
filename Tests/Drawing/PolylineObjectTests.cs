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

public class PolylineObjectTests
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
        var poly = new PolylineObject();

        Assert.Equal(ChartObjectType.Polyline, poly.Type);
        Assert.False(poly.IsSmooth);
        Assert.Equal(BezierSplineMath.DefaultTension, poly.Tension);
        Assert.Equal(PolylineLabelType.None, poly.LabelType);
        Assert.True(poly.ShowLabels);
        Assert.Equal(DrawingThemeContext.FontSize, poly.FontSize);
        Assert.Empty(poly.Points);
    }

    [Fact]
    public void HitTest_StraightLine_PointOnSegment_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(300, 100));

        var poly = new PolylineObject(new[] { p0, p1 }) { IsSmooth = false };

        Assert.True(poly.HitTest(new Point(200, 100), transform, tolerance: 2.0));
        Assert.False(poly.HitTest(new Point(200, 150), transform, tolerance: 2.0));
    }

    [Fact]
    public void HitTest_SmoothLine_PointOnCurve_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        // 3 points in a triangle form
        var p0 = transform.ScreenToChart(new Point(0, 0));
        var p1 = transform.ScreenToChart(new Point(50, 100));
        var p2 = transform.ScreenToChart(new Point(100, 0));

        var poly = new PolylineObject(new[] { p0, p1, p2 })
        {
            IsSmooth = true,
            Tension = 0.5
        };

        // Endpoints should always hit
        Assert.True(poly.HitTest(new Point(0, 0), transform, tolerance: 2.0));
        Assert.True(poly.HitTest(new Point(100, 0), transform, tolerance: 2.0));
        Assert.True(poly.HitTest(new Point(50, 100), transform, tolerance: 2.0));

        // Far away point should not hit
        Assert.False(poly.HitTest(new Point(50, 200), transform, tolerance: 5.0));
    }

    [Fact]
    public void HitTest_LessThanTwoPoints_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var poly = new PolylineObject();
        Assert.False(poly.HitTest(new Point(100, 100), transform));

        poly.AddPoint(transform.ScreenToChart(new Point(100, 100)));
        Assert.False(poly.HitTest(new Point(100, 100), transform));
    }

    [Fact]
    public void HitTest_LargePointCount_ExecutesArrayPool_Succeeds()
    {
        var transform = new DummyCoordinateTransform();
        var pts = new List<ChartPoint>();
        for (int i = 0; i < 150; i++)
        {
            pts.Add(transform.ScreenToChart(new Point(i * 2, i * 2)));
        }

        var poly = new PolylineObject(pts) { IsSmooth = true };

        // Test point in middle of large set
        Assert.True(poly.HitTest(new Point(100, 100), transform, tolerance: 3.0));
        Assert.False(poly.HitTest(new Point(100, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void Render_StraightAndSmooth_DrawsWithoutException()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(200, 300));
        var p2 = transform.ScreenToChart(new Point(300, 100));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        using var polyStraight = new PolylineObject(new[] { p0, p1, p2 })
        {
            IsSmooth = false,
            LabelType = PolylineLabelType.Numeric,
            ShowLabels = true
        };
        polyStraight.Render(canvas, transform);

        using var polySmooth = new PolylineObject(new[] { p0, p1, p2 })
        {
            IsSmooth = true,
            Tension = 0.5,
            LabelType = PolylineLabelType.Alphabet,
            ShowLabels = true
        };
        polySmooth.Render(canvas, transform);
    }

    [Fact]
    public void GetLabel_WaveCountingRules()
    {
        var poly = new PolylineObject { LabelType = PolylineLabelType.Numeric };
        Assert.Equal("", poly.GetLabel(0)); // Start point empty
        Assert.Equal("1", poly.GetLabel(1));
        Assert.Equal("2", poly.GetLabel(2));

        poly.LabelType = PolylineLabelType.Alphabet;
        Assert.Equal("", poly.GetLabel(0));
        Assert.Equal("A", poly.GetLabel(1));
        Assert.Equal("B", poly.GetLabel(2));

        poly.LabelType = PolylineLabelType.None;
        Assert.Equal("", poly.GetLabel(1));
    }

    [Fact]
    public void Translate_ShiftsAllPointsCorrectly()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 5), 150m);

        var poly = new PolylineObject(new[] { p0, p1 });
        poly.Translate(TimeSpan.FromDays(3), 20m);

        Assert.Equal(new DateTime(2025, 1, 4), poly.Points[0].Time);
        Assert.Equal(120m, poly.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 8), poly.Points[1].Time);
        Assert.Equal(170m, poly.Points[1].Price);
    }
}
