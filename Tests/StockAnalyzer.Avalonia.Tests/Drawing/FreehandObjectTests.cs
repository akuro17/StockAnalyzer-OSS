using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FreehandObjectTests
{
    [Fact]
    public void Selection_DoesNotDrawOrHitInvisibleFrame()
    {
        var transform = CreateTransform();
        using var obj = new FreehandObject(new[]
        {
            transform.ScreenToChart(new Point(20, 20)),
            transform.ScreenToChart(new Point(80, 80))
        });
        using var normal = new SKBitmap(100, 100);
        using var selected = new SKBitmap(100, 100);
        using (var canvas = new SKCanvas(normal))
        {
            canvas.Clear(SKColors.Transparent);
            obj.Render(canvas, transform);
        }
        obj.IsSelected = true;
        using (var canvas = new SKCanvas(selected))
        {
            canvas.Clear(SKColors.Transparent);
            obj.Render(canvas, transform);
        }
        Assert.Equal(normal.Pixels, selected.Pixels);
        Assert.True(obj.HitTest(new Point(50, 50), transform));
        Assert.False(obj.HitTest(new Point(16, 84), transform));
        Assert.False(obj.HitTest(new Point(50, 16), transform));
    }

    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void TypeRegistry_DiscoversFreehandObject()
    {
        var registeredTypes = ChartObjectTypeRegistry.GetRegisteredTypes();

        Assert.NotNull(registeredTypes);
        Assert.Contains(nameof(FreehandObject), registeredTypes.Keys);
        Assert.Equal(typeof(FreehandObject), registeredTypes[nameof(FreehandObject)]);
    }

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var obj = new FreehandObject();

        Assert.Equal(ChartObjectType.Freehand, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
        Assert.True(obj.IsVisible);
        Assert.False(obj.IsLocked);
        Assert.Empty(obj.Points);
    }

    [Fact]
    public void Render_EmptyPoints_DoesNotThrow()
    {
        var obj = new FreehandObject();
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void Render_SinglePoint_DrawsWithoutException()
    {
        var obj = new FreehandObject(new[] { new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m) });
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void Render_MultiplePoints_DrawsWithoutException()
    {
        var points = new[]
        {
            new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m),
            new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 105m)
        };
        var obj = new FreehandObject(points);
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void Render_AllIdenticalPoints_DrawsAsSinglePoint()
    {
        var pt = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var obj = new FreehandObject(new[] { pt, pt, pt });
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_EmptyPoints_ReturnsFalse()
    {
        var obj = new FreehandObject();
        var transform = CreateTransform();

        Assert.False(obj.HitTest(new Point(50, 50), transform));
    }

    [Fact]
    public void HitTest_SinglePoint_HitAndMiss()
    {
        var transform = CreateTransform();
        var pt = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var obj = new FreehandObject(new[] { pt });

        var screenPt = transform.ChartToScreen(pt);

        // Exactly on point
        Assert.True(obj.HitTest(screenPt, transform, tolerance: 5.0));

        // Just inside tolerance
        Assert.True(obj.HitTest(new Point(screenPt.X + 3, screenPt.Y + 3), transform, tolerance: 5.0));

        // Outside tolerance
        Assert.False(obj.HitTest(new Point(screenPt.X + 20, screenPt.Y + 20), transform, tolerance: 5.0));
    }

    [Fact]
    public void HitTest_MultiplePoints_OnSegmentReturnsTrue()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 110m);
        var obj = new FreehandObject(new[] { p1, p2 });

        var sp1 = transform.ChartToScreen(p1);
        var sp2 = transform.ChartToScreen(p2);
        var midpoint = new Point((sp1.X + sp2.X) / 2.0, (sp1.Y + sp2.Y) / 2.0);

        Assert.True(obj.HitTest(midpoint, transform, tolerance: 5.0));
        Assert.False(obj.HitTest(new Point(midpoint.X + 50, midpoint.Y + 50), transform, tolerance: 5.0));
    }

    [Fact]
    public void HitTest_WhenNotVisible_ReturnsFalse()
    {
        var transform = CreateTransform();
        var pt = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var obj = new FreehandObject(new[] { pt }) { IsVisible = false };

        var screenPt = transform.ChartToScreen(pt);
        Assert.False(obj.HitTest(screenPt, transform, tolerance: 10.0));
    }

    [Fact]
    public void Translate_DoesNotModifyPoints_PositionIsFixed()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1 = new ChartPoint(t0, 100m);
        var p2 = new ChartPoint(t0.AddDays(1), 105m);
        var obj = new FreehandObject(new[] { p1, p2 });

        obj.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(p1.Time, obj.Points[0].Time);
        Assert.Equal(p1.Price, obj.Points[0].Price);
        Assert.Equal(p2.Time, obj.Points[1].Time);
        Assert.Equal(p2.Price, obj.Points[1].Price);
    }

    [Fact]
    public void Constructor_WithNullPoints_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FreehandObject(null!));
    }

    [Fact]
    public void Render_WhenSelected_DrawsStrokeWithoutException()
    {
        var points = new[]
        {
            new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m),
            new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 105m)
        };
        var obj = new FreehandObject(points) { IsSelected = true };
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_WhenSelected_DoesNotHitFormerFrame()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), 150m);
        var obj = new FreehandObject(new[] { p1, p2 }) { IsSelected = true };

        var sp1 = transform.ChartToScreen(p1);
        var sp2 = transform.ChartToScreen(p2);

        float minX = (float)Math.Min(sp1.X, sp2.X);
        float maxX = (float)Math.Max(sp1.X, sp2.X);
        float minY = (float)Math.Min(sp1.Y, sp2.Y);
        float maxY = (float)Math.Max(sp1.Y, sp2.Y);
        float pad = 4f;

        // The former frame corner is not part of the stroke.
        var corner = new Point(minX - pad, minY - pad);
        Assert.False(obj.HitTest(corner, transform));

        // The former frame edge is not part of the stroke either.
        var topEdge = new Point((minX + maxX) / 2.0, minY - pad);
        Assert.False(obj.HitTest(topEdge, transform));

        // When unselected, corner handle hit test should return false (unless on the stroke)
        obj.IsSelected = false;
        Assert.False(obj.HitTest(corner, transform));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var obj = new FreehandObject();
        obj.Dispose();
        obj.Dispose(); // Should not throw
    }

    [Fact]
    public void Render_AfterDispose_SelfHealsAndDrawsWithoutException()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m);
        var obj = new FreehandObject(new[] { p1, p2 });
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));

        // Call dispose
        obj.Dispose();

        // Render after dispose: must self-heal native resources and not throw ObjectDisposedException or AV
        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_PointsOutsideAABB_ReturnsFalse()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 110m);
        var obj = new FreehandObject(new[] { p1, p2 });

        var sp1 = transform.ChartToScreen(p1);
        var sp2 = transform.ChartToScreen(p2);

        // Point completely far away in X and Y
        Assert.False(obj.HitTest(new Point(sp1.X - 200, sp1.Y - 200), transform));
        Assert.False(obj.HitTest(new Point(sp2.X + 200, sp2.Y + 200), transform));
    }
}
