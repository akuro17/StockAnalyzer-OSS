using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class GannGridObjectHitTestTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static GannGridObject MakeGrid(LinearCoordinateTransform t, out global::Avalonia.Point p1)
    {
        var start = new ChartPoint(new DateTime(2024, 1, 1, 2, 0, 0), 80m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 20m);
        var obj = new GannGridObject(start, end);
        p1 = t.ChartToScreen(start);
        return obj;
    }

    [Fact]
    public void HitTest_ClickInsideOriginalDefiningBox_ReturnsTrue()
    {
        var t = MakeTransform();
        var obj = MakeGrid(t, out var p1);

        // Center of the small defining rectangle is itself a grid-line intersection.
        var hit = obj.HitTest(p1, t);

        Assert.True(hit);
    }

    [Fact]
    public void HitTest_ClickOnExtendedGridLineFarFromDefiningBox_ReturnsTrue()
    {
        // Regression test: previously, HitTest only accepted clicks inside the tiny
        // P1-P2 box used to define cell size, even though Render() draws the grid
        // pattern across the entire visible chart. Shift+Click delete therefore
        // failed for clicks anywhere on the (visually obvious) extended grid lines.
        var t = MakeTransform();
        var obj = MakeGrid(t, out var p1);
        double dx = 133.33333333333334; // matches the cell width derived from MakeGrid's points

        // A point several grid cells away from the defining box (but still within the visible
        // 800x600 canvas), snapped onto a vertical grid line.
        var farOnLine = new global::Avalonia.Point(p1.X + dx * 3, 500);
        var hit = obj.HitTest(farOnLine, t);

        Assert.True(hit);
    }

    [Fact]
    public void HitTest_ClickFarFromAnyGridLine_ReturnsFalse()
    {
        // Sanity check: the fix must not make the whole canvas clickable — only
        // points near an actual rendered line (vertical, horizontal, or either
        // diagonal family) should hit. This offset (half a cell horizontally,
        // a quarter cell vertically) is deliberately chosen to stay far from
        // all four line families for this object's cell aspect ratio.
        var t = MakeTransform();
        var obj = MakeGrid(t, out var p1);
        double dx = 133.33333333333334;
        double dy = 360.0;

        var midCell = new global::Avalonia.Point(p1.X + dx / 2, p1.Y + dy / 4);
        var hit = obj.HitTest(midCell, t);

        Assert.False(hit);
    }
}
