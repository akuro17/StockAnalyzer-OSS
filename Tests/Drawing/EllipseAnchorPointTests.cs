using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Task 5b: EllipseObject/EllipseAnnulusObject already store one real ChartPoint per displayed
/// handle (unlike Rectangle), so AdvanceAnchorPoint's cycling logic needs no special-casing for
/// them — only their Render() methods were missing any AnchorPointIndex-based highlight at all.
/// </summary>
public class EllipseAnchorPointTests
{
    private static SKBitmap CreateCanvas(out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(60, 60);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    private static LinearCoordinateTransform CreateTransform()
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 31),
            minPrice: 0m, maxPrice: 60m,
            canvasWidth: 60, canvasHeight: 60);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Render_AnchorPointIndex_HighlightsOnlyTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();

        var center = new ChartPoint(new DateTime(2026, 1, 10), 30m);
        var corner = new ChartPoint(new DateTime(2026, 1, 20), 45m);
        var ellipse = new EllipseObject(center, corner) { IsSelected = true, AnchorPointIndex = anchorIndex };
        // Pull the two circumference angle handles apart from their coincident construction default
        // (both start pinned to the same boundary point) so indices 2 and 3 land on distinct pixels.
        ellipse.Points[2] = new ChartPoint(new DateTime(2026, 1, 15), 45m);
        ellipse.Points[3] = new ChartPoint(new DateTime(2026, 1, 10), 45m);

        var handles = ellipse.GetSelectionHandleScreenPositions(transform);
        Assert.Equal(4, handles.Length);

        ellipse.Render(canvas, transform);
        canvas.Flush();

        for (int i = 0; i < handles.Length; i++)
        {
            var pixel = bitmap.GetPixel((int)Math.Round(handles[i].X), (int)Math.Round(handles[i].Y));
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
    public void AdvanceAnchorPoint_CyclesThroughAllFourRealPoints()
    {
        var manager = new ChartObjectManager();
        var center = new ChartPoint(new DateTime(2026, 1, 10), 30m);
        var corner = new ChartPoint(new DateTime(2026, 1, 20), 45m);
        var ellipse = new EllipseObject(center, corner);
        manager.AddObject(ellipse);

        var seen = new System.Collections.Generic.HashSet<int> { ellipse.AnchorPointIndex };
        for (int i = 0; i < 4; i++)
        {
            manager.AdvanceAnchorPoint(ellipse.Id);
            seen.Add(ellipse.AnchorPointIndex);
        }

        Assert.Equal(new[] { 0, 1, 2, 3 }, System.Linq.Enumerable.OrderBy(seen, x => x).ToArray());
        Assert.Equal(0, ellipse.AnchorPointIndex); // 4 advances from a 4-point cycle wraps back to start
    }
}
