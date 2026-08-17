using System;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression tests for the "Callout/Text with an embedded line break renders as a tofu-box
/// glyph instead of wrapping" bug. SKCanvas.DrawText/SKPaint.MeasureText don't interpret '\n' as
/// a line break; MultilineTextRenderer (used by CalloutObject and TextObject) splits and
/// draws/measures each line separately.
/// </summary>
public class MultilineTextRenderingTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 400, 400);

    private static SKBitmap RenderToBitmap(IChartObject obj)
    {
        var bitmap = new SKBitmap(400, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, MakeTransform());
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Height (in pixels) of the smallest bounding row-range containing any non-transparent pixel.</summary>
    private static int InkVerticalSpan(SKBitmap bitmap)
    {
        int minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return maxY >= minY ? (maxY - minY + 1) : 0;
    }

    [Theory]
    [InlineData("Line1\nLine2\nLine3")]
    [InlineData("Line1\r\nLine2\r\nLine3")]
    public void SplitLines_HandlesUnixAndWindowsLineEndings(string text)
    {
        var lines = MultilineTextRenderer.SplitLines(text);

        Assert.Equal(new[] { "Line1", "Line2", "Line3" }, lines);
    }

    [Fact]
    public void CalloutObject_MultilineText_RendersTallerThanSingleLine_NotJustOneRowOfTofu()
    {
        // Anchor and body are placed almost on top of each other so the dashed connector line's
        // own ink contributes negligibly to the vertical span, keeping the measurement focused
        // on the text box itself.
        var single = new CalloutObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 1), 50m))
        { Text = "Line1" };
        var multi = new CalloutObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 1), 50m))
        { Text = "Line1\nLine2\nLine3" };

        using var singleBitmap = RenderToBitmap(single);
        using var multiBitmap = RenderToBitmap(multi);

        int singleSpan = InkVerticalSpan(singleBitmap);
        int multiSpan = InkVerticalSpan(multiBitmap);

        Assert.True(singleSpan > 0);
        // A 3-line block must be meaningfully taller than 1 line (not collapsed into one row).
        Assert.True(multiSpan > singleSpan * 2,
            $"Expected 3-line block (span={multiSpan}px) to be much taller than 1-line (span={singleSpan}px)");
    }

    [Fact]
    public void TextObject_MultilineText_RendersTallerThanSingleLine_NotJustOneRowOfTofu()
    {
        var single = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 50m), "Line1");
        var multi = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 50m), "Line1\nLine2\nLine3");

        using var singleBitmap = RenderToBitmap(single);
        using var multiBitmap = RenderToBitmap(multi);

        int singleSpan = InkVerticalSpan(singleBitmap);
        int multiSpan = InkVerticalSpan(multiBitmap);

        Assert.True(singleSpan > 0);
        Assert.True(multiSpan > singleSpan * 2,
            $"Expected 3-line block (span={multiSpan}px) to be much taller than 1-line (span={singleSpan}px)");
    }

    [Fact]
    public void TextObject_HitTest_MultilineText_HitsBelowFirstLine()
    {
        var multi = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 50m), "Line1\nLine2\nLine3");
        var transform = MakeTransform();
        var center = transform.ChartToScreen(multi.Points[0]);

        // A point well below the anchor (where only line 2/3 would be) must still hit the
        // multi-line block's expanded bounds, not just a single-line-height box around center.
        var belowFirstLine = new global::Avalonia.Point(center.X, center.Y + 20);

        Assert.True(multi.HitTest(belowFirstLine, transform));
    }

    /// <summary>Leftmost non-transparent column per row that has any ink (used to check left-alignment).</summary>
    private static System.Collections.Generic.List<int> LeftmostInkColumnPerRow(SKBitmap bitmap)
    {
        var result = new System.Collections.Generic.List<int>();
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    result.Add(x);
                    break;
                }
            }
        }
        return result;
    }

    [Fact]
    public void TextObject_MultilineText_ShortAndLongLinesShareTheSameLeftEdge_NotCentered()
    {
        // "A" is a single narrow character; if lines were still individually centered (the old
        // behavior), its ink would start far to the right of the long line's ink. Left-aligned,
        // both lines' leftmost ink columns should be nearly identical.
        var obj = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 50m), "A\nAVeryMuchLongerSecondLine");

        using var bitmap = RenderToBitmap(obj);
        var leftmostPerRow = LeftmostInkColumnPerRow(bitmap);

        Assert.NotEmpty(leftmostPerRow);
        int spread = leftmostPerRow.Max() - leftmostPerRow.Min();
        Assert.True(spread < 15,
            $"Expected all lines to start near the same left edge (spread &lt; 15px), but spread was {spread}px");
    }

    // --- SAで制約確認 Phase 1: MultilineTextRenderer.SplitLines() must not be called from
    // Render()/HitTest() every frame (SA_RENDERING_PERFORMANCE.md ZeroAllocation rule). Fixed by
    // caching the split lines on CalloutObject/TextObject, invalidated on Text change. ---

    [Fact]
    public void TextObject_ChangingText_ReflectsNewContentAfterCacheInvalidation()
    {
        var obj = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 50m), "Short");
        using var beforeBitmap = RenderToBitmap(obj);
        int beforeSpan = InkVerticalSpan(beforeBitmap);

        obj.Text = "Short\nNow\nThree\nLines";
        using var afterBitmap = RenderToBitmap(obj);
        int afterSpan = InkVerticalSpan(afterBitmap);

        // If the cached split-lines array were stale (not invalidated on Text change), the
        // second render would still show only 1 line's worth of height.
        Assert.True(afterSpan > beforeSpan * 2,
            $"Expected the new 4-line text to render taller (span={afterSpan}px) than the original 1-line text (span={beforeSpan}px) after changing Text");
    }

    [Fact]
    public void CalloutObject_ChangingText_ReflectsNewContentAfterCacheInvalidation()
    {
        var obj = new CalloutObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 1), 50m))
        { Text = "Short" };
        using var beforeBitmap = RenderToBitmap(obj);
        int beforeSpan = InkVerticalSpan(beforeBitmap);

        obj.Text = "Short\nNow\nThree\nLines";
        using var afterBitmap = RenderToBitmap(obj);
        int afterSpan = InkVerticalSpan(afterBitmap);

        Assert.True(afterSpan > beforeSpan * 2,
            $"Expected the new 4-line text to render taller (span={afterSpan}px) than the original 1-line text (span={beforeSpan}px) after changing Text");
    }

    // --- SAで実装: ライン追従テキストツール (LineTextObject) — Task 5 統合検証 ---
    // Top/Bottom are fully independent per-side settings (text, font size, alignment, offset);
    // these tests prove that independence is visible in the actual rendered pixels, not just in
    // the object's property getters.

    [Fact]
    public void LineTextObject_TopAndBottomText_RenderOnOppositeSidesOfTheLineSimultaneously()
    {
        var lineText = new LineTextObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 2), 50m)) // same price -> a horizontal line on screen
        {
            TopText = "T",
            TopOffsetPx = 20,
            BottomText = "B",
            BottomOffsetPx = 20
        };

        var transform = MakeTransform();
        int lineScreenY = (int)Math.Round(transform.ChartToScreen(lineText.Points[0]).Y);

        using var bitmap = RenderToBitmap(lineText);

        bool inkAboveLine = false;
        bool inkBelowLine = false;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0) continue;
                if (y < lineScreenY - 2) inkAboveLine = true;
                if (y > lineScreenY + 2) inkBelowLine = true;
            }
        }

        Assert.True(inkAboveLine, "Expected TopText ink strictly above the line's screen Y");
        Assert.True(inkBelowLine, "Expected BottomText ink strictly below the line's screen Y");
    }

    [Fact]
    public void LineTextObject_LargerTopFontSize_RendersTallerInkThanSmallerFontSize()
    {
        var p1 = new ChartPoint(new DateTime(2024, 1, 1), 50m);
        var p2 = new ChartPoint(new DateTime(2024, 1, 2), 50m);

        var small = new LineTextObject(p1, p2) { TopText = "Label", TopFontSize = 8 };
        var large = new LineTextObject(p1, p2) { TopText = "Label", TopFontSize = 40 };

        using var smallBitmap = RenderToBitmap(small);
        using var largeBitmap = RenderToBitmap(large);

        int smallSpan = InkVerticalSpan(smallBitmap);
        int largeSpan = InkVerticalSpan(largeBitmap);

        Assert.True(smallSpan > 0);
        Assert.True(largeSpan > smallSpan * 2,
            $"Expected TopFontSize=40 text (span={largeSpan}px) to render much taller than TopFontSize=8 text (span={smallSpan}px)");
    }

    [Fact]
    public void LineTextObject_ChangingTopFontSize_DoesNotChangeBottomFontSizeProperty()
    {
        // Pixel-level cross-contamination would be hard to isolate reliably (Top/Bottom ink can
        // overlap rows), so this proves independence at the boundary that actually drives
        // rendering: the two SKPaint-backed properties themselves must never leak into each other.
        var lineText = new LineTextObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 2), 50m))
        {
            BottomFontSize = 20
        };

        lineText.TopFontSize = 8;
        Assert.Equal(20, lineText.BottomFontSize);

        lineText.TopFontSize = 60;
        Assert.Equal(20, lineText.BottomFontSize);
    }
}
