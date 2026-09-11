using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Task 5a: RectangleObject is stored as only 2 chart points (opposite corners) but is rendered and
/// dragged with 4 corner handles (see RectangleObject.Render / ChartInteractionController's corner
/// math). These tests verify the "AP" anchor point highlight and cycling correctly reach all 4
/// visual corners, not just the 2 real stored points.
/// </summary>
public class RectangleAnchorPointTests
{
    private static SKBitmap CreateCanvas(out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(30, 20);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    // Time axis spans 30 days across 30 canvas pixels (1 px/day); price axis spans 0-20 across
    // 20 canvas pixels (screen Y = CanvasHeight - price, i.e. higher price = smaller Y).
    private static LinearCoordinateTransform CreateTransform()
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 31),
            minPrice: 0m, maxPrice: 20m,
            canvasWidth: 30, canvasHeight: 20);

    // p1 = (day 5, price 15) -> screen (5, 5); p2 = (day 20, price 5) -> screen (20, 15).
    // Corners: 0=TopLeft(5,5) 1=TopRight(20,5) 2=BottomRight(20,15) 3=BottomLeft(5,15).
    private static RectangleObject CreateRectangle()
        => new RectangleObject(new ChartPoint(new DateTime(2026, 1, 6), 15m), new ChartPoint(new DateTime(2026, 1, 21), 5m));

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(1, 20, 5)]
    [InlineData(2, 20, 15)]
    [InlineData(3, 5, 15)]
    public void Render_AnchorPointIndex_HighlightsTheCorrectVirtualCorner(int anchorIndex, int expectedX, int expectedY)
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();
        using var rect = CreateRectangle();
        rect.IsSelected = true;
        rect.AnchorPointIndex = anchorIndex;

        rect.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel(expectedX, expectedY));

        var otherCorners = new (int x, int y)[] { (5, 5), (20, 5), (20, 15), (5, 15) };
        foreach (var (x, y) in otherCorners)
        {
            if (x == expectedX && y == expectedY) continue;
            Assert.Equal(DrawingThemeContext.HandleColor, bitmap.GetPixel(x, y));
        }
    }

    [Fact]
    public void AdvanceAnchorPoint_CyclesThroughAllFourVirtualCorners_ThenWraps()
    {
        var manager = new ChartObjectManager();
        var rect = CreateRectangle();
        manager.AddObject(rect);

        Assert.Equal(0, rect.AnchorPointIndex);

        Assert.True(manager.AdvanceAnchorPoint(rect.Id));
        Assert.Equal(1, rect.AnchorPointIndex);

        Assert.True(manager.AdvanceAnchorPoint(rect.Id));
        Assert.Equal(2, rect.AnchorPointIndex);

        Assert.True(manager.AdvanceAnchorPoint(rect.Id));
        Assert.Equal(3, rect.AnchorPointIndex);

        Assert.True(manager.AdvanceAnchorPoint(rect.Id));
        Assert.Equal(0, rect.AnchorPointIndex); // wraps back to the start
    }
}
