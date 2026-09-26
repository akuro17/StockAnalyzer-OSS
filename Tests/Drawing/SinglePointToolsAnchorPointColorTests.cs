using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Follow-up to the "Gann Square of 9" AP-color fix: the same root cause (a single selection
/// handle drawn with a fixed color unrelated to AnchorPointIndex) was found, via horizontal
/// pattern search, in the remaining 6 tools that Task 7a had intentionally left unwired as
/// "single-point, cannot cycle": AnchoredVwap, BarPattern, GhostFeed, GeometricPattern,
/// VerticalLine, HorizontalLine. Each now highlights its single visible handle in
/// AnchorPointColor when AnchorPointIndex == 0 (its permanent, only reachable value for the 5
/// genuinely single-point tools), matching the pattern used across the other ~30 wired tools.
///
/// Note: GhostFeedObject is the one exception in this group that is NOT actually single-point --
/// its behavior class (TwoClickBehavior) permanently keeps 2 points (anchor + extraction
/// endpoint), so AnchorPointIndex CAN reach 1 via the "AP" button. Its Render() only ever draws
/// one handle (a derived "visual anchor"), so at index 1 the handle simply reverts to its
/// original White color -- a pre-existing latent quirk, not something this fix introduces, and
/// out of today's minimal-fix scope (fixing it would require adding a second visible handle,
/// which is a feature addition, not a bug fix).
/// </summary>
public class SinglePointToolsAnchorPointColorTests
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

    [Fact]
    public void AnchoredVwapObject_WhenSelected_HighlightsAnchorHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var anchorPt = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var obj = new AnchoredVwapObject(anchorPt) { IsSelected = true, AnchorPointIndex = 0 };
        obj.Recalculate(new List<CoreCandleData>
        {
            new CoreCandleData(anchorPt.Time, 10, 12, 9, 11, 100),
            new CoreCandleData(anchorPt.Time.AddDays(1), 11, 13, 10, 12, 100),
        });

        var s = transform.ChartToScreen(anchorPt);
        obj.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s.X, (int)s.Y));
    }

    [Fact]
    public void BarPatternObject_WhenSelected_HighlightsAnchorHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var anchorPt = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var obj = new BarPatternObject(anchorPt) { IsSelected = true, AnchorPointIndex = 0 };
        obj.Initialize(new List<CoreCandleData>
        {
            new CoreCandleData(anchorPt.Time, 10, 12, 9, 11, 100),
            new CoreCandleData(anchorPt.Time.AddDays(1), 11, 13, 10, 12, 100),
        });

        var s = transform.ChartToScreen(anchorPt);
        obj.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s.X, (int)s.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GhostFeedObject_WhenSelected_HighlightsTheMatchingRawPointHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var anchorPt = new ChartPoint(new DateTime(2026, 1, 6), 20m);
        var endPt = new ChartPoint(new DateTime(2026, 1, 8), 10m);
        var obj = new GhostFeedObject(anchorPt, endPt) { IsSelected = true, AnchorPointIndex = anchorIndex };
        obj.UpdateEndPoint(endPt, new List<CoreCandleData>
        {
            new CoreCandleData(anchorPt.Time, 10, 12, 9, 11, 100),
            new CoreCandleData(anchorPt.Time.AddDays(1), 11, 13, 10, 12, 100),
        });

        // Each real point (not the synthesized visualAnchor) now gets its own handle.
        var s0 = transform.ChartToScreen(anchorPt);
        var s1 = transform.ChartToScreen(endPt);
        obj.Render(canvas, transform);
        canvas.Flush();

        var anchorPixel = anchorIndex == 0 ? s0 : s1;
        var otherPixel = anchorIndex == 0 ? s1 : s0;
        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)anchorPixel.X, (int)anchorPixel.Y));
        Assert.NotEqual(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)otherPixel.X, (int)otherPixel.Y));
    }

    [Fact]
    public void GhostFeedObject_NonAnchorHandle_UsesGlobalControlPointColorSetting_NotHardcodedWhite()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var anchorPt = new ChartPoint(new DateTime(2026, 1, 6), 20m);
        var endPt = new ChartPoint(new DateTime(2026, 1, 8), 10m);
        var obj = new GhostFeedObject(anchorPt, endPt) { IsSelected = true, AnchorPointIndex = 0 };
        obj.UpdateEndPoint(endPt, new List<CoreCandleData>
        {
            new CoreCandleData(anchorPt.Time, 10, 12, 9, 11, 100),
            new CoreCandleData(anchorPt.Time.AddDays(1), 11, 13, 10, 12, 100),
        });

        var s1 = transform.ChartToScreen(endPt); // Non-anchor handle (AnchorPointIndex == 0, this is index 1)
        obj.Render(canvas, transform);
        canvas.Flush();

        var pixel = bitmap.GetPixel((int)s1.X, (int)s1.Y);
        Assert.NotEqual(SKColors.White, pixel);
        Assert.Equal(DrawingThemeContext.HandleColor, pixel);
    }

    [Fact]
    public void GeometricPatternObject_WhenSelected_HighlightsAnchorHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var anchorPt = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var obj = new GeometricPatternObject(anchorPt) { IsSelected = true, AnchorPointIndex = 0 };

        var s = transform.ChartToScreen(anchorPt);
        obj.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s.X, (int)s.Y));
    }

    [Fact]
    public void VerticalLineObject_WhenSelected_HighlightsHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var obj = new VerticalLineObject(p1) { IsSelected = true, AnchorPointIndex = 0 };

        var s = transform.ChartToScreen(p1);
        var clip = new SKRect(0, 0, 40, 40);
        int midY = (int)clip.MidY;
        obj.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s.X, midY));
    }

    [Fact]
    public void HorizontalLineObject_WhenSelected_HighlightsHandleInAnchorPointColor()
    {
        using var bitmap = CreateCanvas(40, out var canvas);
        var transform = CreateTransform(40);
        var p1 = new ChartPoint(new DateTime(2026, 1, 6), 10m);
        var obj = new HorizontalLineObject(p1) { IsSelected = true, AnchorPointIndex = 0 };

        var s = transform.ChartToScreen(p1);
        obj.Render(canvas, transform);
        canvas.Flush();

        Assert.Equal(DrawingThemeContext.AnchorPointColor, bitmap.GetPixel((int)s.X, (int)s.Y));
    }
}
