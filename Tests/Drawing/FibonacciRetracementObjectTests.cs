using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class FibonacciRetracementObjectTests
{
    private static LinearCoordinateTransform CreateTransform()
        => new(new DateTime(2026, 1, 1), new DateTime(2026, 1, 11), 0m, 300m, 1000, 600);

    [Fact]
    public void DefaultState_HasSevenLevels_AndDefaultExtendMode()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        Assert.False(fib.ShowExtensions);
        Assert.Equal(FibonacciRetracementExtendMode.Default, fib.ExtendMode);
        Assert.Equal(7, fib.Levels.Count);
        Assert.Contains(0f, fib.Levels);
        Assert.Contains(0.236f, fib.Levels);
        Assert.Contains(0.382f, fib.Levels);
        Assert.Contains(0.5f, fib.Levels);
        Assert.Contains(0.618f, fib.Levels);
        Assert.Contains(0.786f, fib.Levels);
        Assert.Contains(1.0f, fib.Levels);
    }

    [Fact]
    public void ShowExtensions_True_HasFifteenLevelsIncludingPositiveAndNegativeExtensions()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ShowExtensions = true;

        Assert.Equal(15, fib.Levels.Count);

        // Positive extensions
        Assert.Contains(1.272f, fib.Levels);
        Assert.Contains(1.618f, fib.Levels);
        Assert.Contains(2.618f, fib.Levels);
        Assert.Contains(4.236f, fib.Levels);

        // Negative extensions (0% reference, negative direction)
        Assert.Contains(-0.272f, fib.Levels);
        Assert.Contains(-0.618f, fib.Levels);
        Assert.Contains(-1.618f, fib.Levels);
        Assert.Contains(-3.236f, fib.Levels);
    }

    [Fact]
    public void GetCalculatedValues_WithShowExtensions_IncludesAllExtensionLevels()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m); // Start (100%)
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 200m); // End (0%)
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ShowExtensions = true;
        var values = fib.GetCalculatedValues(p2.Time);

        // Range + 15 levels = 16 values
        Assert.Equal(16, values.Count);

        // Negative extension: -0.272: price = 200 + (100 - 200) * (-0.272) = 200 + 27.2 = 227.2
        var extNeg27 = Assert.Single(values, v => v.Key == "Fib_-0.272");
        Assert.Equal(227.2m, extNeg27.NumericValue);

        // Positive extension: 1.272: price = 200 + (100 - 200) * (1.272) = 200 - 127.2 = 72.8
        var extPos127 = Assert.Single(values, v => v.Key == "Fib_1.272");
        Assert.Equal(72.8m, extPos127.NumericValue);
    }

    [Fact]
    public void HitTest_Default_OnlyHitsWithinBasePointsSpan()
    {
        var transform = CreateTransform();
        // p1: 2026-01-01 -> X = 0, Price = 100 -> Y = 400
        // p2: 2026-01-11 -> X = 1000, Price = 200 -> Y = 200
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 11), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        // 50% level Y = 200 + (400 - 200) * 0.5 = 300
        // Inside span (X = 500, Y = 300) => True
        Assert.True(fib.HitTest(new global::Avalonia.Point(500, 300), transform));

        // Outside span right (X = 1100, Y = 300) => False
        Assert.False(fib.HitTest(new global::Avalonia.Point(1100, 300), transform));

        // Outside span left (X = -50, Y = 300) => False
        Assert.False(fib.HitTest(new global::Avalonia.Point(-50, 300), transform));
    }

    [Fact]
    public void HitTest_ExtendRight_HitsBeyondEndPointToCanvasRight()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 6), 200m); // X = 500, Y = 200
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ExtendMode = FibonacciRetracementExtendMode.ExtendRight;

        // 50% level Y = 300. Beyond end point (X = 800, Y = 300) => True
        Assert.True(fib.HitTest(new global::Avalonia.Point(800, 300), transform));

        // Outside left (X = -50, Y = 300) => False
        Assert.False(fib.HitTest(new global::Avalonia.Point(-50, 300), transform));
    }

    [Fact]
    public void HitTest_ExtendBoth_HitsBeyondBothSides()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 3), 100m); // X = 200
        var p2 = new ChartPoint(new DateTime(2026, 1, 6), 200m); // X = 500
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ExtendMode = FibonacciRetracementExtendMode.ExtendBoth;

        // Beyond right (X = 800, Y = 300) => True
        Assert.True(fib.HitTest(new global::Avalonia.Point(800, 300), transform));

        // Beyond left (X = 50, Y = 300) => True (within [0, canvasWidth])
        Assert.True(fib.HitTest(new global::Avalonia.Point(50, 300), transform));
    }

    [Fact]
    public void HitTest_CyclicLines_HitsOnVerticalIntervalLines()
    {
        var transform = CreateTransform();
        // p1: X = 0 (2026-01-01), p2: X = 200 (2026-01-03) => interval = 200
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 3), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ExtendMode = FibonacciRetracementExtendMode.CyclicLines;

        // Next cycle line at X = 400 (k=2), arbitrary Y (100) that is NOT a horizontal level
        Assert.True(fib.HitTest(new global::Avalonia.Point(400, 100), transform));
        Assert.True(fib.HitTest(new global::Avalonia.Point(600, 100), transform));

        // Non-cycle line X
        Assert.False(fib.HitTest(new global::Avalonia.Point(500, 100), transform));
    }

    [Fact]
    public void HitTest_FibTimeZone_HitsOnFibonacciVerticalLines()
    {
        var transform = CreateTransform();
        // p1: X = 0 (2026-01-01), p2: X = 100 (2026-01-02) => interval = 100
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ExtendMode = FibonacciRetracementExtendMode.FibTimeZone;

        // Fib numbers: 0, 1, 2, 3, 5, 8...
        // X = 0 + 100 * 3 = 300 => True
        Assert.True(fib.HitTest(new global::Avalonia.Point(300, 100), transform));

        // X = 0 + 100 * 5 = 500 => True
        Assert.True(fib.HitTest(new global::Avalonia.Point(500, 100), transform));

        // Non-fib multiple: X = 400 (fib 4 doesn't exist in standard sequence) => False
        Assert.False(fib.HitTest(new global::Avalonia.Point(400, 100), transform));
    }

    [Fact]
    public void PanelDefinition_CanHandle_FibonacciRetracementOnly()
    {
        var def = new FibonacciRetracementSettingsPanelDefinition();
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow, 200m);

        using var fib = new FibonacciRetracementObject(p1, p2);
        using var trend = new TrendLineObject(p1, p2);

        Assert.True(def.CanHandle(fib));
        Assert.False(def.CanHandle(trend));
    }

    [Fact]
    public void HitTest_UsesCanvasWidthDirectly_WithoutConcreteTypeCast()
    {
        var transform = CreateTransform();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 6), 200m);
        using var fib = new FibonacciRetracementObject(p1, p2);

        fib.ExtendMode = FibonacciRetracementExtendMode.ExtendRight;

        // Verify that hit test works up to CanvasWidth
        Assert.True(transform.CanvasWidth > 0);
        Assert.True(fib.HitTest(new global::Avalonia.Point(transform.CanvasWidth - 10, 300), transform));
    }
}
