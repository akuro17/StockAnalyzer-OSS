using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>Task 5b: EllipseAnnulusObject's 5 real Points map 1:1 to its 5 rendered handles, so only
/// the missing AnchorPointIndex-based render highlight needed fixing (no cycling-logic change).</summary>
public class EllipseAnnulusAnchorPointTests
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

    private static EllipseAnnulusObject CreateAnnulus() => new EllipseAnnulusObject(
        boundsStart: new ChartPoint(new DateTime(2026, 1, 5), 10m),
        boundsEnd: new ChartPoint(new DateTime(2026, 1, 25), 50m),
        startAnglePoint: new ChartPoint(new DateTime(2026, 1, 25), 30m),
        endAnglePoint: new ChartPoint(new DateTime(2026, 1, 15), 50m),
        innerRadiusPoint: new ChartPoint(new DateTime(2026, 1, 15), 30m));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Render_AnchorPointIndex_HighlightsOnlyTheMatchingHandle(int anchorIndex)
    {
        using var bitmap = CreateCanvas(out var canvas);
        var transform = CreateTransform();
        var annulus = CreateAnnulus();
        annulus.IsSelected = true;
        annulus.AnchorPointIndex = anchorIndex;

        var handleScreenPoints = new global::Avalonia.Point[5];
        for (int i = 0; i < 5; i++) handleScreenPoints[i] = transform.ChartToScreen(annulus.Points[i]);

        annulus.Render(canvas, transform);
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
}
