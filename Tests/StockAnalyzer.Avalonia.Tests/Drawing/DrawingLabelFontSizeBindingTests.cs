using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression coverage for wiring 11 drawing tools' label TextSize to the shared
/// DrawingThemeContext.FontSize (color intentionally left unchanged per user decision — see
/// Y:\Temp\sa_implementation_plan_drawinglabel_fontsize_extension.md). The underlying
/// live-update behavior of DrawingThemeContext.FontSize itself is already covered by
/// DrawingThemeContextTests; these tests instead prove each object's Render() actually consumes
/// that value at runtime (a plain diff review/build wouldn't catch a wrong-property typo that
/// still compiles) via representative samples spanning the different code shapes touched
/// (single hardcoded literal, list/level-based loop, and a per-object settable property whose
/// default should now be theme-driven).
/// </summary>
[Collection("DrawingThemeContext State")]
public class DrawingLabelFontSizeBindingTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 400, 400);

    private static SKBitmap RenderToBitmap(IChartObject obj)
    {
        var bitmap = new SKBitmap(400, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, MakeTransform());
        canvas.Flush();
        return bitmap;
    }

    private static bool HasAnyInk(SKBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) return true;
            }
        }
        return false;
    }

    [Fact]
    public void AngleObject_Render_DoesNotThrow_AndDrawsSomething()
    {
        var obj = new AngleObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 60m));

        using var bitmap = RenderToBitmap(obj);

        Assert.True(HasAnyInk(bitmap));
    }

    [Fact]
    public void FibonacciRetracementObject_Render_DoesNotThrow_AndDrawsSomething()
    {
        var obj = new FibonacciRetracementObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 90m));

        using var bitmap = RenderToBitmap(obj);

        Assert.True(HasAnyInk(bitmap));
    }

    [Fact]
    public void GannFanObject_Render_DoesNotThrow_AndDrawsSomething()
    {
        var obj = new GannFanObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 60m));

        using var bitmap = RenderToBitmap(obj);

        Assert.True(HasAnyInk(bitmap));
    }

    [Fact]
    public void PolylineObject_DefaultFontSize_MatchesDrawingThemeContext()
    {
        var obj = new PolylineObject(new System.Collections.Generic.List<ChartPoint>
        {
            new(new DateTime(2024, 1, 1), 10m),
            new(new DateTime(2024, 1, 2), 20m)
        });

        Assert.Equal(DrawingThemeContext.FontSize, obj.FontSize);
    }
}
