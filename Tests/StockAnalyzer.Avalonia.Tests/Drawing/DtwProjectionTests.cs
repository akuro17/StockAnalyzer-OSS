using System;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DtwProjectionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    [Fact]
    public void DtwProjectionObject_InitialPlacement_DefaultsToMatchedColorAndNotUnmatched()
    {
        var obj = new DtwProjectionObject();

        Assert.False(obj.IsUnmatched);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(Color.Parse(ChartSettingsConstants.DefaultDtwUnmatchedColor), obj.UnmatchedColor);
        Assert.False(obj.HasMatch);
    }

    [Fact]
    public void DtwProjectionObject_WhenUnmatchedSet_IsUnmatchedIsTrue()
    {
        var obj = new DtwProjectionObject();
        obj.IsUnmatched = true;

        Assert.True(obj.IsUnmatched);
        Assert.False(obj.HasMatch);
    }

    [Fact]
    public void DtwProjectionObject_Translate_ResetsIsUnmatchedToFalse()
    {
        var obj = new DtwProjectionObject();
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 2, 0, 0), 50m));
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 60m));
        obj.IsUnmatched = true;

        obj.Translate(TimeSpan.FromHours(1), 5m);

        Assert.False(obj.IsUnmatched);
    }

    [Fact]
    public void DtwProjectionObject_VerticalRange_HitTest_CoversFullHeight()
    {
        var t = MakeTransform();
        var start = new ChartPoint(new DateTime(2024, 1, 1, 4, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 8, 0, 0), 80m);
        var obj = new DtwProjectionObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        var pStart = t.ChartToScreen(start);
        var pEnd = t.ChartToScreen(end);
        double midX = (pStart.X + pEnd.X) / 2;

        // Test hit at the very top (Y=0)
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 0), t));
        // Test hit at the very bottom (Y=600)
        Assert.True(obj.HitTest(new global::Avalonia.Point(midX, 600), t));
        // Test hit near vertical line 1 (start)
        Assert.True(obj.HitTest(new global::Avalonia.Point(pStart.X, 300), t));
        // Test hit near vertical line 2 (end)
        Assert.True(obj.HitTest(new global::Avalonia.Point(pEnd.X, 300), t));
        // Test miss outside range
        Assert.False(obj.HitTest(new global::Avalonia.Point(pStart.X - 50, 300), t));
        Assert.False(obj.HitTest(new global::Avalonia.Point(pEnd.X + 50, 300), t));
    }
}
