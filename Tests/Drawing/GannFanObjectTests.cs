using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class GannFanObjectTests
{
    [Fact]
    public void GannFanObject_PropertiesAndRender_UniformThickness()
    {
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        var transform = new LinearCoordinateTransform(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31),
            0m, 200m,
            800, 600);

        var p1 = new ChartPoint(new DateTime(2026, 1, 5), 50m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 15), 150m);
        var fan = new GannFanObject(p1, p2)
        {
            Thickness = 2.5
        };

        Assert.Equal(ChartObjectType.GannFan, fan.Type);
        Assert.Equal(2.5, fan.Thickness);

        // Rendering should succeed without exception
        fan.Render(canvas, transform);
        canvas.Flush();

        // Hit test on the start point should return true
        var screenP1 = transform.ChartToScreen(p1);
        Assert.True(fan.HitTest(screenP1, transform));
    }
}
