using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Task 7a: 23 more IChartObject implementers (out of the 34 identified as still missing
/// AnchorPointIndex-based handle highlighting after Task 5) had the same "AnchorPointIndex == i ?
/// AnchorPointColor : previous-color" ternary added to their existing SelectionHandleRenderer.Draw
/// call sites. Rather than duplicating a near-identical pixel test per file, these tests cover one
/// representative per structurally distinct call pattern found during the survey:
///  - GannBoxObject: 2-point explicit calls with a `radius:` named argument (13 files share this shape)
///  - TriangleObject: 3-point explicit calls, no radius argument (5 files share this shape)
///  - TargetPriceProjectionObject: loop over Points.Count (1 file)
///  - FixedRangeVolumeProfileObject: handles drawn unconditionally (not gated by IsSelected)
///  - DtwProjectionObject: handle drawing delegated to a separate *Renderer class
/// Objects with only 1 point were intentionally left unwired (ChartObjectManager.AdvanceAnchorPoint
/// is a no-op below 2 points, so there is never a second index to distinguish with color).
/// </summary>
public class RemainingToolsAnchorPointTests
{
    private static SKBitmap CreateCanvas(int size, out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(size, size);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    private static LinearCoordinateTransform CreateTransform(int size)
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 1).AddDays(size),
            minPrice: 0m, maxPrice: size,
            canvasWidth: size, canvasHeight: size);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GannBoxObject_AnchorPointIndex_HighlightsTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 21), 30m);
        var box = new GannBoxObject(p1, p2) { IsSelected = true, AnchorPointIndex = anchorIndex };

        var s1 = transform.ChartToScreen(p1);
        var s2 = transform.ChartToScreen(p2);
        box.Render(canvas, transform);
        canvas.Flush();

        var anchorPixel = anchorIndex == 0 ? s1 : s2;
        var otherPixel = anchorIndex == 0 ? s2 : s1;
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)anchorPixel.X, (int)anchorPixel.Y));
        Assert.NotEqual(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)otherPixel.X, (int)otherPixel.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TriangleObject_AnchorPointIndex_HighlightsTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 5), 30m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 15), 10m);
        var p3 = new ChartPoint(new DateTime(2026, 1, 25), 30m);
        var triangle = new TriangleObject(p1, p2, p3) { IsSelected = true, AnchorPointIndex = anchorIndex };

        var screenPoints = new[] { transform.ChartToScreen(p1), transform.ChartToScreen(p2), transform.ChartToScreen(p3) };
        triangle.Render(canvas, transform);
        canvas.Flush();

        for (int i = 0; i < screenPoints.Length; i++)
        {
            var pixel = bitmap.GetPixel((int)screenPoints[i].X, (int)screenPoints[i].Y);
            if (i == anchorIndex) Assert.Equal(DrawingThemeContext.AnchorPointColor, pixel);
            else Assert.NotEqual(DrawingThemeContext.AnchorPointColor, pixel);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TargetPriceProjectionObject_AnchorPointIndex_HighlightsTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 5), 10m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 15), 20m);
        var p3 = new ChartPoint(new DateTime(2026, 1, 25), 30m);
        var projection = new TargetPriceProjectionObject(p1, p2, p3) { IsSelected = true, AnchorPointIndex = anchorIndex };

        var screenPoints = new[] { transform.ChartToScreen(p1), transform.ChartToScreen(p2), transform.ChartToScreen(p3) };
        projection.Render(canvas, transform);
        canvas.Flush();

        for (int i = 0; i < screenPoints.Length; i++)
        {
            var pixel = bitmap.GetPixel((int)screenPoints[i].X, (int)screenPoints[i].Y);
            if (i == anchorIndex) Assert.Equal(DrawingThemeContext.AnchorPointColor, pixel);
            else Assert.NotEqual(DrawingThemeContext.AnchorPointColor, pixel);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FixedRangeVolumeProfileObject_AnchorPointIndex_HighlightsTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 21), 30m);
        using var profile = new FixedRangeVolumeProfileObject(p1, p2) { AnchorPointIndex = anchorIndex };

        var s1 = transform.ChartToScreen(p1);
        var s2 = transform.ChartToScreen(p2);
        profile.Render(canvas, transform); // handles are drawn unconditionally, not gated by IsSelected
        canvas.Flush();

        var anchorPixel = anchorIndex == 0 ? s1 : s2;
        var otherPixel = anchorIndex == 0 ? s2 : s1;
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)anchorPixel.X, (int)anchorPixel.Y));
        Assert.NotEqual(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)otherPixel.X, (int)otherPixel.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DtwProjectionObject_AnchorPointIndex_HighlightsTheMatchingHandle(int anchorIndex)
    {
        const int size = 40;
        using var bitmap = CreateCanvas(size, out var canvas);
        var transform = CreateTransform(size);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 21), 30m);
        var dtw = new DtwProjectionObject { IsSelected = true, AnchorPointIndex = anchorIndex };
        dtw.Points.Add(p1);
        dtw.Points.Add(p2);

        var s1 = transform.ChartToScreen(p1);
        var s2 = transform.ChartToScreen(p2);
        int midY = size / 2; // DtwProjectionRenderer draws handles at canvas.LocalClipBounds.MidY

        dtw.Render(canvas, transform);
        canvas.Flush();

        var anchorX = anchorIndex == 0 ? (int)s1.X : (int)s2.X;
        var otherX = anchorIndex == 0 ? (int)s2.X : (int)s1.X;
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel(anchorX, midY));
        Assert.NotEqual(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel(otherX, midY));
    }

    /// <summary>
    /// GannSquareOfNineObject has only 1 point, so it can never cycle (ChartObjectManager.AdvanceAnchorPoint
    /// no-ops below 2 points), but the AP button still selects it (see DrawingObjectItemViewModel fix).
    /// Its single handle must still render in AnchorPointColor to give the user visual confirmation that
    /// it is the anchor point -- its Render() was previously drawing the handle with the plain default
    /// selection color regardless of AnchorPointIndex.
    /// </summary>
    [Fact]
    public void GannSquareOfNineObject_WhenSelected_HighlightsItsOnlyHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var square = new GannSquareOfNineObject(p1) { IsSelected = true, AnchorPointIndex = 0 };

        var s1 = transform.ChartToScreen(p1);
        square.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s1.X, (int)s1.Y));
    }
}
