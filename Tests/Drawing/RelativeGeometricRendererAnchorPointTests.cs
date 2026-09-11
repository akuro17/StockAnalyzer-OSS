using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class RelativeGeometricRendererAnchorPointTests
{
    private class TestRenderer : RelativeGeometricRenderer
    {
        public override ChartObjectType Type => (ChartObjectType)999;
        protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform) { }
        public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = 5) => false;
    }

    private static SKBitmap CreateCanvas(out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(30, 20);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    // Time axis spans 30 days across 30 canvas pixels (1 px/day); price axis spans 0-20 across
    // 20 canvas pixels, so a constant price of 10 always maps to screen Y = 10.
    private static LinearCoordinateTransform CreateTransform()
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 31),
            minPrice: 0m, maxPrice: 20m,
            canvasWidth: 30, canvasHeight: 20);

    [Fact]
    public void Render_DefaultAnchorPointIndex_HighlightsFirstPointWithAnchorColor()
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();

        using var renderer = new TestRenderer { IsSelected = true };
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 3), 10m));  // x=2, y=10
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 16), 10m)); // x=15, y=10
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 29), 10m)); // x=28, y=10

        renderer.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(0, renderer.AnchorPointIndex);
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel(2, 10));
        Assert.Equal(DrawingThemeContext.HandleColor, bitmap.GetPixel(15, 10));
        Assert.Equal(DrawingThemeContext.HandleColor, bitmap.GetPixel(28, 10));
    }

    [Fact]
    public void Render_CustomAnchorPointIndex_HighlightsThatPointInstead()
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();

        using var renderer = new TestRenderer { IsSelected = true, AnchorPointIndex = 1 };
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 3), 10m));  // x=2, y=10
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 16), 10m)); // x=15, y=10
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 29), 10m)); // x=28, y=10

        renderer.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.HandleColor, bitmap.GetPixel(2, 10));
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel(15, 10));
        Assert.Equal(DrawingThemeContext.HandleColor, bitmap.GetPixel(28, 10));
    }

    [Fact]
    public void Render_NotSelected_DrawsNoHandles()
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();

        using var renderer = new TestRenderer { IsSelected = false };
        renderer.Points.Add(new ChartPoint(new DateTime(2026, 1, 3), 10m)); // x=2, y=10

        renderer.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(0, bitmap.GetPixel(2, 10).Alpha);
    }
}
