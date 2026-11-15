using System;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The number labels of the Fibonacci Time Zone sit at the bottom edge of the chart on every coordinate transform
/// (the main chart uses <see cref="GenericCoordinateTransform"/>, not <see cref="LinearCoordinateTransform"/>).
/// </summary>
public sealed class FibonacciTimeZoneLabelTests
{
    private const int Width = 800;
    private const int Height = 600;
    private const int LabelBandTop = 570;
    private const int ColumnsRightOfTheLine = 4;
    private const int ColumnsInTheProbe = 8;
    private static readonly DateTime T1 = new(2026, 1, 1);

    private static FibonacciTimeZoneObject CreateObject() =>
        new(new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(5), 110m))
        {
            Color = Color.FromArgb(255, 64, 160, 240),
            Thickness = 2.0
        };

    private static SKBitmap Render(IChartObject obj, ICoordinateTransform transform)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, transform);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>True when a pixel of the label band beside the line of zone 0 is not background (the label "0" is there; the vertical line itself is excluded).</summary>
    private static bool HasLabelPixels(SKBitmap bitmap, double zeroLineX)
    {
        int left = (int)Math.Round(zeroLineX) + ColumnsRightOfTheLine;
        for (int x = left; x < left + ColumnsInTheProbe; x++)
        {
            for (int y = LabelBandTop; y < Height; y++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.Black) return true;
            }
        }

        return false;
    }

    [Fact]
    public void GenericTransform_DrawsTheLabelsAtTheBottomEdge()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(T1.AddDays(-5), T1.AddDays(60), 80m, 140m);
        using var obj = CreateObject();
        double zeroLineX = transform.ChartToScreen(obj.Points[0]).X;

        using SKBitmap bitmap = Render(obj, transform);

        Assert.True(HasLabelPixels(bitmap, zeroLineX));
    }

    [Fact]
    public void PixelTransform_DrawsTheLabelsAtTheBottomEdge()
    {
        var transform = new PixelCoordinateTransform();
        using var obj = new FibonacciTimeZoneObject(PixelCoordinateTransform.Chart(100, 300), PixelCoordinateTransform.Chart(160, 310))
        {
            Color = Color.FromArgb(255, 64, 160, 240),
            Thickness = 2.0
        };
        double zeroLineX = transform.ChartToScreen(obj.Points[0]).X;

        using SKBitmap bitmap = Render(obj, transform);

        Assert.True(HasLabelPixels(bitmap, zeroLineX));
    }
}
