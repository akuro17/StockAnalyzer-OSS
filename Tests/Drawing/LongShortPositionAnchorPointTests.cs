using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>Task 5c: LongShortPositionObject's 3 real Points (Entry/Stop/Target) map 1:1 to its 3
/// rendered handles, so only the missing AnchorPointIndex-based render highlight needed fixing (no
/// cycling-logic change) — same shape of fix as EllipseObject/EllipseAnnulusObject in Task 5b.</summary>
public class LongShortPositionAnchorPointTests
{
    private static SKBitmap CreateCanvas(out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(80, 60);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    private static LinearCoordinateTransform CreateTransform()
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 31),
            minPrice: 0m, maxPrice: 60m,
            canvasWidth: 80, canvasHeight: 60);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Render_AnchorPointIndex_HighlightsOnlyTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();

        var entry = new ChartPoint(new DateTime(2026, 1, 10), 30m);
        var stop = new ChartPoint(new DateTime(2026, 1, 10), 15m);
        var target = new ChartPoint(new DateTime(2026, 1, 10), 45m);
        using var position = new LongShortPositionObject(entry, stop, target, isLong: true)
        {
            IsSelected = true,
            AnchorPointIndex = anchorIndex,
            BoxWidth = 30 // default (200px) would draw the Stop/Target handles off this small test canvas
        };

        var pEntry = transform.ChartToScreen(entry);
        var pStop = transform.ChartToScreen(stop);
        var pTarget = transform.ChartToScreen(target);
        double rightX = pEntry.X + position.BoxWidth;

        var handleScreenPoints = new[]
        {
            new global::Avalonia.Point(pEntry.X, pEntry.Y),   // 0: Entry
            new global::Avalonia.Point(rightX, pStop.Y),      // 1: Stop
            new global::Avalonia.Point(rightX, pTarget.Y)     // 2: Target
        };

        position.Render(canvas, transform);
        canvas.Flush();

        for (int i = 0; i < handleScreenPoints.Length; i++)
        {
            var pixel = bitmap.GetPixel((int)Math.Round(handleScreenPoints[i].X), (int)Math.Round(handleScreenPoints[i].Y));
            if (i == anchorIndex)
            {
                Assert.Equal(DrawingThemeContext.AnchorPointColor, pixel);
            }
            else
            {
                Assert.NotEqual(DrawingThemeContext.AnchorPointColor, pixel);
            }
        }
    }

    [Fact]
    public void AdvanceAnchorPoint_CyclesThroughAllThreeRealPoints()
    {
        var manager = new ChartObjectManager();
        var entry = new ChartPoint(new DateTime(2026, 1, 10), 30m);
        var stop = new ChartPoint(new DateTime(2026, 1, 10), 15m);
        var target = new ChartPoint(new DateTime(2026, 1, 10), 45m);
        var position = new LongShortPositionObject(entry, stop, target, isLong: true);
        manager.AddObject(position);

        var seen = new System.Collections.Generic.HashSet<int> { position.AnchorPointIndex };
        for (int i = 0; i < 3; i++)
        {
            manager.AdvanceAnchorPoint(position.Id);
            seen.Add(position.AnchorPointIndex);
        }

        Assert.Equal(new[] { 0, 1, 2 }, System.Linq.Enumerable.OrderBy(seen, x => x).ToArray());
        Assert.Equal(0, position.AnchorPointIndex); // 3 advances from a 3-point cycle wraps back to start
    }
}
