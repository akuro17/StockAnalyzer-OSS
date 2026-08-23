using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class VerticalLineRangeToolsTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    [Fact]
    public void AutoElliottWaveObject_DefaultColor_InheritsDrawingThemeContextDefaultColor()
    {
        var obj = new AutoElliottWaveObject();
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
    }

    [Fact]
    public void HarmonicPatternObject_DefaultColor_InheritsDrawingThemeContextDefaultColor()
    {
        var obj = new HarmonicPatternObject();
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
    }

    [Fact]
    public void AutoElliottWaveObject_HitTest_VerticalRange_CoversFullHeight()
    {
        var t = MakeTransform();
        var start = new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 8, 0, 0), 80m);
        var obj = new AutoElliottWaveObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        var pStart = t.ChartToScreen(start);
        var pEnd = t.ChartToScreen(end);
        double midX = (pStart.X + pEnd.X) / 2;

        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 0), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 600), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pStart.X, 300), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pEnd.X, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pStart.X - 50, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pEnd.X + 50, 300), t));
    }

    [Fact]
    public void HarmonicPatternObject_HitTest_VerticalRange_CoversFullHeight()
    {
        var t = MakeTransform();
        var start = new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 8, 0, 0), 80m);
        var obj = new HarmonicPatternObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        var pStart = t.ChartToScreen(start);
        var pEnd = t.ChartToScreen(end);
        double midX = (pStart.X + pEnd.X) / 2;

        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 0), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 600), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pStart.X, 300), t));
        Assert.True(obj.HitTest(new global::Avalonia.Point(pEnd.X, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pStart.X - 50, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pEnd.X + 50, 300), t));
    }
}
