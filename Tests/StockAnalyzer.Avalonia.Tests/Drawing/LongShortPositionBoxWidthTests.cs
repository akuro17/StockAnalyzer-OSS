using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression tests for LongShortPositionObject's user-resizable box width (Task 1 of
/// the Long/Short handle-repositioning implementation): Stop/Target handles are drawn
/// at the box's right edge (Entry.X + BoxWidth), not at the left edge (Entry.X), and
/// BoxWidth is a mutable, user-adjustable property rather than the previous fixed constant.
/// </summary>
public class LongShortPositionBoxWidthTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    [Fact]
    public void BoxWidth_DefaultsToLegacyConstant()
    {
        var entry = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var stop = new ChartPoint(entry.Time, 40m);
        var target = new ChartPoint(entry.Time, 60m);
        var obj = new LongShortPositionObject(entry, stop, target, isLong: true);

        Assert.Equal(StockAnalyzer.Avalonia.Common.ChartConstants.LongShortBoxWidth, obj.BoxWidth);
    }

    [Fact]
    public void Render_Selected_StopTargetHandles_AreAtRightEdge_NotLeftEdge()
    {
        var t = MakeTransform();
        var entry = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var stop = new ChartPoint(entry.Time, 40m);
        var target = new ChartPoint(entry.Time, 60m);
        var obj = new LongShortPositionObject(entry, stop, target, isLong: true)
        {
            IsSelected = true,
            BoxWidth = 100
        };

        var entryScreen = t.ChartToScreen(entry);
        var stopScreen = t.ChartToScreen(stop);
        var rightX = (int)(entryScreen.X + obj.BoxWidth);

        var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();

        // No red handle at the old left-edge position for the Stop handle.
        Assert.NotEqual(SKColors.Red, bitmap.GetPixel((int)entryScreen.X, (int)stopScreen.Y));
        // A red handle exists at the new right-edge position.
        Assert.Equal(SKColors.Red, bitmap.GetPixel(rightX, (int)stopScreen.Y));

        bitmap.Dispose();
    }
}
