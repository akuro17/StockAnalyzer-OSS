using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Root cause of an intermittent failure of TextHandlePositionTests (expected the handle colour, got another test's anchor colour): the renderer reused
/// one shared paint whose colour was assigned before every draw, so two threads rendering handles at the same time (xUnit runs test classes in parallel)
/// could paint each other's colour. Each thread must get its own paint state.
/// </summary>
public class SelectionHandleRendererThreadSafetyTests
{
    private const int Iterations = 20000;
    private const int BitmapSize = 8;
    private const float HandleRadius = 2f;
    private static readonly global::Avalonia.Point Centre = new(BitmapSize / 2.0, BitmapSize / 2.0);

    private static int CountWrongPixels(SKColor drawColor, Barrier barrier, bool outline)
    {
        int wrong = 0;
        using var bitmap = new SKBitmap(BitmapSize, BitmapSize);
        using var canvas = new SKCanvas(bitmap);
        barrier.SignalAndWait();
        for (int i = 0; i < Iterations; i++)
        {
            canvas.Clear(SKColors.Transparent);
            if (outline)
            {
                SelectionHandleRenderer.DrawOutline(canvas, Centre, drawColor, HandleRadius);
            }
            else
            {
                SelectionHandleRenderer.Draw(canvas, Centre, drawColor, HandleRadius);
            }
            canvas.Flush();

            // The circle's centre is filled for Draw and (with the default stroke width) transparent for DrawOutline; check a pixel on the ring for the latter.
            SKColor probe = bitmap.GetPixel(BitmapSize / 2, outline ? BitmapSize / 2 - (int)HandleRadius : BitmapSize / 2);
            if (probe != drawColor) wrong++;
        }
        return wrong;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentHandleDraws_NeverPaintTheOtherThreadsColour(bool outline)
    {
        using var barrier = new Barrier(2);

        Task<int> red = Task.Factory.StartNew(() => CountWrongPixels(SKColors.Red, barrier, outline), TaskCreationOptions.LongRunning);
        Task<int> cyan = Task.Factory.StartNew(() => CountWrongPixels(SKColors.Cyan, barrier, outline), TaskCreationOptions.LongRunning);

        Assert.Equal(0, await red);
        Assert.Equal(0, await cyan);
    }
}
