using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class SelectionHandleRendererTests
{
    private static SKBitmap CreateCanvas(out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(20, 20);
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return bitmap;
    }

    [Fact]
    public void Draw_SinglePoint_PaintsDefaultRedCircleAtPoint()
    {
        using var bitmap = CreateCanvas(out var canvas);

        SelectionHandleRenderer.Draw(canvas, new Point(10, 10));
        canvas.Flush();

        Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void Draw_CustomColor_PaintsRequestedColor()
    {
        using var bitmap = CreateCanvas(out var canvas);

        SelectionHandleRenderer.Draw(canvas, new Point(10, 10), SKColors.Yellow);
        canvas.Flush();

        Assert.Equal(SKColors.Yellow, bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void Draw_CalledPerPoint_PaintsCircleAtEachPoint()
    {
        // Multi-point / variable-length callers (e.g. Polyline) invoke Draw() once per
        // point in a loop rather than passing a collection, to stay ZeroAllocation-compliant.
        using var bitmap = CreateCanvas(out var canvas);

        SelectionHandleRenderer.Draw(canvas, new Point(2, 2));
        SelectionHandleRenderer.Draw(canvas, new Point(17, 17));
        canvas.Flush();

        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(17, 17));
    }

    [Fact]
    public void DrawOutline_LeavesCenterUnfilled_ButPaintsEdge()
    {
        using var bitmap = CreateCanvas(out var canvas);

        SelectionHandleRenderer.DrawOutline(canvas, new Point(10, 10), SKColors.Blue, radius: 6f, strokeWidth: 6f);
        canvas.Flush();

        // Center of a stroked (outline-only) circle must remain unpainted (transparent);
        // the ring spans roughly radius 3-9, so the exact center is well outside the stroke.
        Assert.Equal(0, bitmap.GetPixel(10, 10).Alpha);
        // A pixel comfortably inside the stroke band (away from anti-aliased edges) must be painted.
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(16, 10));
    }

    [Fact]
    public void Draw_SequentialCallsWithDifferentColors_DoNotBleedIntoEachOther()
    {
        // Verifies the shared/cached SKPaint field is correctly re-colored per call
        // rather than leaking state between sequential Draw() invocations.
        using var bitmap = CreateCanvas(out var canvas);

        SelectionHandleRenderer.Draw(canvas, new Point(3, 3), SKColors.Black);
        SelectionHandleRenderer.Draw(canvas, new Point(16, 16), SKColors.Red);
        canvas.Flush();

        Assert.Equal(SKColors.Black, bitmap.GetPixel(3, 3));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(16, 16));
    }
}
