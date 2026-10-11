using System;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class ShapeStrokeStyleTests
{
    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);

    [Fact]
    public void DrawingStrokeStyleExtensions_ReturnsExpectedPathEffects()
    {
        Assert.Null(DrawingStrokeStyle.Solid.ToSkPathEffect());
        Assert.NotNull(DrawingStrokeStyle.Dash.ToSkPathEffect());
        Assert.NotNull(DrawingStrokeStyle.Dot.ToSkPathEffect());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void RectangleObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var rect = new RectangleObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, rect.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        rect.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void TriangleObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        using var tri = new TriangleObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m), new ChartPoint(t3, 80m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, tri.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        tri.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void EllipseObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var ellipse = new EllipseObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, ellipse.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        ellipse.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void EllipseAnnulusObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var annulus = new EllipseAnnulusObject(
            new ChartPoint(t1, 100m),
            new ChartPoint(t1.AddDays(10), 200m),
            new ChartPoint(t1.AddDays(2), 120m),
            new ChartPoint(t1.AddDays(8), 180m),
            new ChartPoint(t1.AddDays(5), 150m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, annulus.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        annulus.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void ParallelChannelObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var channel = new ParallelChannelObject(
            new ChartPoint(t1, 100m),
            new ChartPoint(t1.AddDays(5), 150m),
            new ChartPoint(t1.AddDays(2), 120m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, channel.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        channel.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void GannBoxObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var box = new GannBoxObject(new ChartPoint(t1, 100m), new ChartPoint(t1.AddDays(10), 200m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, box.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        box.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void HorizontalAndVerticalLineObjects_ApplyStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hLine = new HorizontalLineObject(new ChartPoint(t1, 100m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };
        var vLine = new VerticalLineObject(new ChartPoint(t1, 100m))
        {
            StrokeStyle = style,
            Thickness = 2.0
        };

        Assert.Equal(style, hLine.StrokeStyle);
        Assert.Equal(style, vLine.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        hLine.Render(surface.Canvas, CreateTransform());
        vLine.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void FreehandObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var freehand = new FreehandObject
        {
            StrokeStyle = style,
            Thickness = 2.0
        };
        freehand.Points.Add(new ChartPoint(t1, 100m));
        freehand.Points.Add(new ChartPoint(t1.AddDays(1), 120m));

        Assert.Equal(style, freehand.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        freehand.Render(surface.Canvas, CreateTransform());
    }

    [Theory]
    [InlineData(DrawingStrokeStyle.Solid)]
    [InlineData(DrawingStrokeStyle.Dash)]
    [InlineData(DrawingStrokeStyle.Dot)]
    public void HighlighterObject_AppliesStrokeStyle(DrawingStrokeStyle style)
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var highlighter = new HighlighterObject
        {
            StrokeStyle = style,
            Thickness = 12.0
        };
        highlighter.Points.Add(new ChartPoint(t1, 100m));
        highlighter.Points.Add(new ChartPoint(t1.AddDays(1), 120m));

        Assert.Equal(style, highlighter.StrokeStyle);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        highlighter.Render(surface.Canvas, CreateTransform());
    }
}
