using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression tests: text-centered drawing objects (Text, Callout, PriceLabel) must
/// never draw a selection handle on/near their text, since it visually overlaps and
/// obscures the characters, and dragging the text body already works without it.
/// TextObject has no anchor separate from its text, so it shows no handle at all when
/// selected. Callout/PriceLabel keep their (non-overlapping) anchor handle only.
/// </summary>
public class TextHandlePositionTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static SKBitmap RenderSelected(IChartObject obj, ICoordinateTransform t)
    {
        var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, t);
        canvas.Flush();
        return bitmap;
    }

    private static bool BitmapContainsColor(SKBitmap bitmap, SKColor color)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color) return true;
            }
        }
        return false;
    }

    [Fact]
    public void TextObject_Selected_DrawsNoHandleAnywhere()
    {
        var t = MakeTransform();
        var anchor = new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m);
        var obj = new TextObject(anchor, "Hello") { IsSelected = true };

        using var bitmap = RenderSelected(obj, t);

        Assert.False(BitmapContainsColor(bitmap, SKColors.Red));
    }

    [Fact]
    public void CalloutObject_Selected_NoHandleNearTextBody()
    {
        var t = MakeTransform();
        var anchor = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 80m);
        var body = new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m);
        var obj = new CalloutObject(anchor, body) { IsSelected = true };

        var bodyCenter = t.ChartToScreen(body);
        using var bitmap = RenderSelected(obj, t);

        // No handle pixel within the immediate vicinity of the text body.
        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            Assert.NotEqual(SKColors.Red, bitmap.GetPixel((int)bodyCenter.X + dx, (int)bodyCenter.Y + dy));
        }

        // The anchor handle must still be shown (unaffected by this change).
        var anchorScreen = t.ChartToScreen(anchor);
        Assert.Equal(SKColors.Red, bitmap.GetPixel((int)anchorScreen.X, (int)anchorScreen.Y));
    }

    [Fact]
    public void PriceLabelObject_Selected_NoHandleNearLabelText()
    {
        var t = MakeTransform();
        var anchor = new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 80m);
        var labelPos = new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m);
        var obj = new PriceLabelObject(anchor, labelPos) { IsSelected = true };

        var labelCenter = t.ChartToScreen(labelPos);
        using var bitmap = RenderSelected(obj, t);

        for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            Assert.NotEqual(SKColors.Red, bitmap.GetPixel((int)labelCenter.X + dx, (int)labelCenter.Y + dy));
        }

        var anchorScreen = t.ChartToScreen(anchor);
        Assert.Equal(SKColors.Red, bitmap.GetPixel((int)anchorScreen.X, (int)anchorScreen.Y));
    }
}
