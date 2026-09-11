using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression tests for a systemic bug found across several 2-point IChartObject
/// implementations: <c>new Avalonia.Rect(Point, Point)</c> does NOT normalize its
/// arguments (unlike WPF's Rect). If the second point is positioned before the
/// first on either axis (a very common real-world drag direction — e.g. dragging
/// from a later/lower point back to an earlier/higher one), the resulting Rect has
/// negative Width/Height and `Contains()` always returns false. This silently made
/// the affected object types unselectable/undeletable (via Shift+Click or Eraser)
/// whenever drawn in that direction.
/// </summary>
public class ReversedDragHitTestTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static (ChartPoint start, ChartPoint end) ReversedPoints()
    {
        // Drag FROM later-time/lower-price TO earlier-time/higher-price, i.e.
        // Points[1] is "before" Points[0] on both axes on screen.
        var start = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 20m);
        var end = new ChartPoint(new DateTime(2024, 1, 1, 2, 0, 0), 80m);
        return (start, end);
    }

    private static global::Avalonia.Point Midpoint(LinearCoordinateTransform t, ChartPoint a, ChartPoint b)
    {
        var pa = t.ChartToScreen(a);
        var pb = t.ChartToScreen(b);
        return new global::Avalonia.Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
    }

    [Fact]
    public void RectangleObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new RectangleObject(start, end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void GannBoxObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new GannBoxObject(start, end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void GannSquare144Object_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new GannSquare144Object(start, end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new FixedRangeVolumeProfileObject(start, end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void HarmonicPatternObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new HarmonicPatternObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void DtwProjectionObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new DtwProjectionObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void AutoElliottWaveObject_ReversedDrag_ClickInsideBox_IsHit()
    {
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new AutoElliottWaveObject();
        obj.Points.Add(start);
        obj.Points.Add(end);

        Assert.True(obj.HitTest(Midpoint(t, start, end), t));
    }

    [Fact]
    public void ChartObjectManager_GetObjectAt_ReversedDragGannSquare144_FindsObject()
    {
        // End-to-end style check through the same manager path used by
        // Shift+Click delete and the Eraser tool.
        var t = MakeTransform();
        var (start, end) = ReversedPoints();
        var obj = new GannSquare144Object(start, end);
        var manager = new ChartObjectManager();
        manager.AddObject(obj);

        var found = manager.GetObjectAt(Midpoint(t, start, end), t);

        Assert.NotNull(found);
        Assert.Equal(obj.Id, found!.Id);
    }
}
