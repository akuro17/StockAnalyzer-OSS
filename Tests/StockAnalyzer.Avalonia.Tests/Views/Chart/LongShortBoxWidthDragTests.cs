using System;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// Regression tests for Task 2 of the Long/Short position resize feature:
/// dragging the Stop/Target handle (at the box's right edge) resizes BoxWidth based
/// on horizontal screen distance from Entry, clamped to a minimum width so the box
/// cannot invert (right edge crossing back past Entry).
/// </summary>
public class LongShortBoxWidthDragTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static LongShortPositionObject MakeObject(LinearCoordinateTransform t, out Point entryScreen)
    {
        var entry = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m);
        var stop = new ChartPoint(entry.Time, 40m);
        var target = new ChartPoint(entry.Time, 60m);
        var obj = new LongShortPositionObject(entry, stop, target, isLong: true);
        entryScreen = t.ChartToScreen(entry);
        return obj;
    }

    [Fact]
    public void ComputeLongShortBoxWidth_MousePastEntry_ReturnsDistanceFromEntry()
    {
        var t = MakeTransform();
        var obj = MakeObject(t, out var entryScreen);

        var mousePos = new Point(entryScreen.X + 150, entryScreen.Y);
        double width = ChartInteractionController.ComputeLongShortBoxWidth(obj, mousePos, t);

        Assert.Equal(150, width, precision: 3);
    }

    [Fact]
    public void ComputeLongShortBoxWidth_MouseDraggedPastEntryToTheLeft_ClampsToMinimumWidth()
    {
        var t = MakeTransform();
        var obj = MakeObject(t, out var entryScreen);

        // Drag far to the left of Entry (would produce a negative/inverted width).
        var mousePos = new Point(entryScreen.X - 500, entryScreen.Y);
        double width = ChartInteractionController.ComputeLongShortBoxWidth(obj, mousePos, t);

        Assert.Equal(ChartConstants.LongShortMinBoxWidth, width, precision: 3);
    }

    [Fact]
    public void ComputeLongShortBoxWidth_MouseSlightlyPastEntry_ClampsToMinimumWidth()
    {
        var t = MakeTransform();
        var obj = MakeObject(t, out var entryScreen);

        // Just barely to the right, less than the minimum width.
        var mousePos = new Point(entryScreen.X + 5, entryScreen.Y);
        double width = ChartInteractionController.ComputeLongShortBoxWidth(obj, mousePos, t);

        Assert.Equal(ChartConstants.LongShortMinBoxWidth, width, precision: 3);
    }

    [Fact]
    public void GetLongShortPositionHandles_MatchesRenderedPositions_EntryLeftStopTargetRight()
    {
        var t = MakeTransform();
        var obj = MakeObject(t, out var entryScreen);
        obj.BoxWidth = 120;

        var handles = ChartInteractionController.GetLongShortPositionHandles(obj, t);

        Assert.Equal(3, handles.Length);
        // Entry (index 0) stays at the left edge (Entry's own screen position).
        Assert.Equal(entryScreen.X, handles[0].X, precision: 3);
        Assert.Equal(entryScreen.Y, handles[0].Y, precision: 3);
        // Stop/Target (index 1/2) are at the right edge (Entry.X + BoxWidth).
        Assert.Equal(entryScreen.X + obj.BoxWidth, handles[1].X, precision: 3);
        Assert.Equal(entryScreen.X + obj.BoxWidth, handles[2].X, precision: 3);
        // Their Y still matches the Stop/Target price positions.
        var stopScreen = t.ChartToScreen(obj.Points[1]);
        var targetScreen = t.ChartToScreen(obj.Points[2]);
        Assert.Equal(stopScreen.Y, handles[1].Y, precision: 3);
        Assert.Equal(targetScreen.Y, handles[2].Y, precision: 3);
    }
}
