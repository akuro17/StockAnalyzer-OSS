using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class InteractionFeedbackRendererTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 1000;
        public double CanvasHeight => 800;
        public Rect ScreenRect => new Rect(0, 0, 1000, 800);
        public double ViewportX => 0;
        public double ViewportWidth => 1000;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint) => new Point(100, 100);
        public ChartPoint ScreenToChart(Point screenPoint) => new ChartPoint(DateTime.Now, 100m);
        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 100.0;
    }

    [Fact]
    public void Render_WithDrawingPreviewObject_DrawsWithoutErrors()
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800));
        var canvas = surface.Canvas;

        var previewObj = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 10), 200m));

        var transform = new DummyCoordinateTransform();

        // Act - should execute safely without exceptions
        renderer.Render(canvas, null, previewObj, transform);

        // Verify surface remains valid
        Assert.NotNull(surface);
    }

    [Fact]
    public void Render_WithNullPreviewObject_ExecutesSafely()
    {
        var renderer = new InteractionFeedbackRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800));
        var canvas = surface.Canvas;
        var transform = new DummyCoordinateTransform();

        // Act with null
        renderer.Render(canvas, null, null, transform);

        Assert.NotNull(surface);
    }
}
