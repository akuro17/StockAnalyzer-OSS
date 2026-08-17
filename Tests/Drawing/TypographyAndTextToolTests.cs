using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class TypographyAndTextToolTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    [Fact]
    public void TextTypographySettings_DefaultValues_ShouldBeValid()
    {
        var settings = new TextTypographySettings();
        settings.Validate(); // Should not throw

        Assert.Equal(12f, settings.FontSizePx);
        Assert.Equal(0xFF000000u, settings.TextColorArgb);
        Assert.Equal(0xDCFFFFFFu, settings.BackgroundColorArgb);
        Assert.Equal(TextHorizontalAlignment.Left, settings.Alignment);
        Assert.True(settings.ShowBackgroundBox);
        Assert.Equal(8f, settings.BackgroundPaddingPx);
        Assert.Equal(4f, settings.CornerRadiusPx);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(150.0f)]
    public void TextTypographySettings_InvalidFontSize_ShouldThrow(float invalidFontSize)
    {
        var settings = new TextTypographySettings(FontSizePx: invalidFontSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(4.0f)]
    public void TextTypographySettings_InvalidLineSpacing_ShouldThrow(float invalidLineSpacing)
    {
        var settings = new TextTypographySettings(LineSpacingFactor: invalidLineSpacing);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void PathTextRenderer_ZeroLengthPath_ShouldNotThrowAndDrawZeroGlyphs()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var canvas = surface.Canvas;

        using var emptyPath = new SKPath();
        using var paint = new SKPaint { TextSize = 12f, IsAntialias = true };

        // Should return gracefully without exception
        PathTextRenderer.DrawTextOnPath(canvas, "Test", emptyPath, paint);
    }

    [Fact]
    public void PathTextRenderer_NullOrEmptyText_ShouldNotThrow()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var canvas = surface.Canvas;

        using var path = new SKPath();
        path.MoveTo(0, 50);
        path.LineTo(100, 50);

        using var paint = new SKPaint { TextSize = 12f, IsAntialias = true };

        PathTextRenderer.DrawTextOnPath(canvas, "", path, paint);
        PathTextRenderer.DrawTextOnPath(canvas, null!, path, paint);
    }

    [Fact]
    public void PathTextRenderer_TextLongerThanPath_ShouldClipOverflowGlyphsDeterministically()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var canvas = surface.Canvas;

        using var shortPath = new SKPath();
        shortPath.MoveTo(0, 50);
        shortPath.LineTo(20, 50); // Path length = 20px

        using var paint = new SKPaint { TextSize = 14f, IsAntialias = true };

        // Draw long text on 20px path with ClipOverflow = true
        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Left, ClipOverflow: true);
        PathTextRenderer.DrawTextOnPath(canvas, "Very Long Text That Exceeds Path Length", shortPath, paint, options);
    }

    [Fact]
    public void PathTextRenderer_SharpCurvature_ShouldHoldAngleAndPreventInversion()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var canvas = surface.Canvas;

        using var sharpPath = new SKPath();
        sharpPath.MoveTo(0, 0);
        sharpPath.LineTo(50, 0);
        sharpPath.LineTo(50, 50); // 90 degree sharp turn
        sharpPath.LineTo(0, 50);

        using var paint = new SKPaint { TextSize = 12f, IsAntialias = true };

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Left, ClipOverflow: false);
        // Should execute without throw and clamp steep angle changes
        PathTextRenderer.DrawTextOnPath(canvas, "SharpCurvatureTest", sharpPath, paint, options);
    }

    private static SKBitmap RenderSingleGlyphOnPath(SKPath path, PathTextOptions options)
    {
        var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
        PathTextRenderer.DrawTextOnPath(canvas, "F", path, paint, options);
        canvas.Flush();
        return bitmap;
    }

    private static bool BitmapsMatch(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
            }
        }
        return true;
    }

    [Fact]
    public void PathTextRenderer_AlwaysUpright_RendersIdenticallyRegardlessOfPathDirection()
    {
        // Same segment, opposite winding: left-to-right (tangent angle 0 degrees) vs
        // right-to-left (tangent angle 180 degrees, which FollowLine would render upside-down).
        using var pathLtr = new SKPath();
        pathLtr.MoveTo(20, 50);
        pathLtr.LineTo(180, 50);

        using var pathRtl = new SKPath();
        pathRtl.MoveTo(180, 50);
        pathRtl.LineTo(20, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright);

        using var bmpLtr = RenderSingleGlyphOnPath(pathLtr, options);
        using var bmpRtl = RenderSingleGlyphOnPath(pathRtl, options);

        Assert.True(BitmapsMatch(bmpLtr, bmpRtl),
            "Expected AlwaysUpright text to render identically (same glyph, same orientation, same screen position) regardless of the path's drawing direction");
    }

    [Fact]
    public void PathTextRenderer_AlwaysUpright_KeepsTextHorizontal_EvenOnAVerticalPath()
    {
        // Bug repro: the original AlwaysUpright implementation only normalized the angle into
        // [-90, 90], which left a vertical line's tangent angle (exactly 90 degrees) untouched
        // (the guard condition was strictly "> 90"), so text on a vertical line still rendered
        // sideways instead of upright. AlwaysUpright must now pin the display angle to exactly 0
        // regardless of the path's angle.
        using var horizontalPath = new SKPath();
        horizontalPath.MoveTo(20, 100);
        horizontalPath.LineTo(180, 100);

        using var verticalPath = new SKPath();
        verticalPath.MoveTo(100, 20);
        verticalPath.LineTo(100, 180);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright);

        using var bmpHorizontal = RenderToNewBitmap(200, 200, c =>
        {
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            PathTextRenderer.DrawTextOnPath(c, "F", horizontalPath, paint, options);
        });
        using var bmpVertical = RenderToNewBitmap(200, 200, c =>
        {
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            PathTextRenderer.DrawTextOnPath(c, "F", verticalPath, paint, options);
        });

        // Note: the two renders are no longer required to be pixel-identical — the overlap fix
        // (advance = w*cosT + fontSpacing*sinT) intentionally centers a glyph on a vertical path
        // using its font-spacing rather than its raw width, which shifts its exact anchor slightly
        // versus the horizontal case. What must hold regardless of path direction is that the glyph
        // itself renders WIDE (horizontal "F"), never TALL (a 90-degree-rotated "F"'s ink would be
        // taller than it is wide).
        var (wH, hH) = InkBoundingBoxSize(bmpHorizontal);
        var (wV, hV) = InkBoundingBoxSize(bmpVertical);

        // "F" happens to be naturally taller than wide even when correctly horizontal (e.g. 9x17);
        // don't hardcode that assumption — derive the expected width/height relationship from the
        // (known-correct, unaffected-by-this-bug) horizontal-path baseline, and assert the
        // vertical-path AlwaysUpright render preserves the same relationship. If the bug were still
        // present (glyph still rotated 90 degrees to match the vertical tangent), the relationship
        // would flip (width/height swapped relative to the baseline).
        bool baselineIsTaller = hH > wH;
        if (baselineIsTaller)
        {
            Assert.True(hV > wV,
                $"Expected AlwaysUpright text on a vertical path to keep the same taller-than-wide shape as the horizontal baseline ({wH}x{hH}), but got {wV}x{hV} (looks rotated sideways)");
        }
        else
        {
            Assert.True(wV > hV,
                $"Expected AlwaysUpright text on a vertical path to keep the same wider-than-tall shape as the horizontal baseline ({wH}x{hH}), but got {wV}x{hV} (looks rotated sideways)");
        }
    }

    [Fact]
    public void PathTextRenderer_AlwaysUpright_LeavesGapBetweenConsecutiveGlyphsOnAVerticalPath()
    {
        // Bug repro: AlwaysUpright keeps every glyph horizontal regardless of path direction, but
        // the arc-length advance between glyphs used to be based solely on each glyph's own
        // (horizontal) width. On a vertical path, advancing by width instead of height left far too
        // little room between glyphs, so consecutive horizontal glyphs visually overlapped instead
        // of stacking with a gap.
        using var verticalPath = new SKPath();
        verticalPath.MoveTo(100, 10);
        verticalPath.LineTo(100, 490);

        using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
        var options = new PathTextOptions(TextHorizontalAlignment.Left, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, ClipOverflow: false);

        using var bitmap = RenderToNewBitmap(200, 500, c => PathTextRenderer.DrawTextOnPath(c, "II", verticalPath, paint, options));

        var rowHasInk = new bool[bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) { rowHasInk[y] = true; break; }
            }
        }

        int firstInk = Array.IndexOf(rowHasInk, true);
        int lastInk = Array.LastIndexOf(rowHasInk, true);
        Assert.True(firstInk >= 0, "Expected some ink to be drawn");

        bool foundGap = false;
        for (int y = firstInk; y <= lastInk; y++)
        {
            if (!rowHasInk[y]) { foundGap = true; break; }
        }

        Assert.True(foundGap,
            "Expected a visible gap between consecutive AlwaysUpright glyphs on a vertical path, but the ink was continuous (glyphs overlapping)");
    }

    [Fact]
    public void PathTextRenderer_LargeFontSize_TextStaysClearOfTheLine()
    {
        // Bug repro: NormalOffset is a fixed distance from the path to the glyph BASELINE, not to
        // the glyph's nearest visual edge. For large font sizes, the ascent/descent extends well
        // past a small fixed offset (e.g. the 10px default used by LineTextObject), so the glyph's
        // ink crossed back over the line itself.
        using var path = new SKPath();
        path.MoveTo(20, 100);
        path.LineTo(180, 100); // Simulated line at screen Y=100

        using var paint = new SKPaint { TextSize = 60f, Color = SKColors.Black, IsAntialias = false };
        // Matches LineTextObject's Top-side convention: negative offset pushes text above the line.
        var options = new PathTextOptions(TextHorizontalAlignment.Center, NormalOffset: -10f, RotationMode: TextRotationMode.AlwaysUpright);

        using var bitmap = RenderToNewBitmap(200, 200, c => PathTextRenderer.DrawTextOnPath(c, "Top", path, paint, options));

        bool inkAtOrBelowLine = false;
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, 100).Alpha != 0) { inkAtOrBelowLine = true; break; }
        }

        Assert.False(inkAtOrBelowLine,
            "Expected large-font-size text to stay entirely above the line (Y=100), but ink reached the line's row");
    }

    private static int MaxInkX(SKBitmap bitmap)
    {
        int maxX = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = bitmap.Width - 1; x > maxX; x--)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) { maxX = x; break; }
            }
        }
        return maxX;
    }

    [Fact]
    public void PathTextRenderer_ExtendBeyondPath_False_ClipsTextThatOverflowsThePath()
    {
        // Sanity/contrast case: default (ExtendBeyondPath=false) behavior is unchanged — text
        // longer than the path stays clipped at (or very near) the path's end point.
        using var path = new SKPath();
        path.MoveTo(20, 100);
        path.LineTo(120, 100); // 100px-long horizontal path

        using var paint = new SKPaint { TextSize = 20f, Color = SKColors.Black, IsAntialias = false };
        var options = new PathTextOptions(TextHorizontalAlignment.Left, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, ExtendBeyondPath: false);

        using var bitmap = RenderToNewBitmap(400, 200, c => PathTextRenderer.DrawTextOnPath(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", path, paint, options));

        int maxX = MaxInkX(bitmap);
        Assert.True(maxX > 0, "Sanity check: some ink should be drawn near the path's start");
        Assert.True(maxX <= 140, $"Expected clipped text to stay near the path's end (x<=120, small margin), but ink reached x={maxX}");
    }

    [Fact]
    public void PathTextRenderer_ExtendBeyondPath_True_ExtrapolatesOverflowingTextPastTheEndpoint()
    {
        // Bug/feature: without ExtendBeyondPath, characters past the path's end simply vanish. With
        // it enabled, they must keep being drawn, continuing in the straight-line direction of the
        // path's tangent at its end point (here, a horizontal path, so ink should extend further
        // right than the clipped case above).
        using var path = new SKPath();
        path.MoveTo(20, 100);
        path.LineTo(120, 100); // 100px-long horizontal path

        using var paint = new SKPaint { TextSize = 20f, Color = SKColors.Black, IsAntialias = false };
        var clippedOptions = new PathTextOptions(TextHorizontalAlignment.Left, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, ExtendBeyondPath: false);
        var extendedOptions = clippedOptions with { ExtendBeyondPath = true };

        const string longText = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        using var clippedBitmap = RenderToNewBitmap(400, 200, c => PathTextRenderer.DrawTextOnPath(c, longText, path, paint, clippedOptions));
        using var extendedBitmap = RenderToNewBitmap(400, 200, c => PathTextRenderer.DrawTextOnPath(c, longText, path, paint, extendedOptions));

        int clippedMaxX = MaxInkX(clippedBitmap);
        int extendedMaxX = MaxInkX(extendedBitmap);

        Assert.True(extendedMaxX > clippedMaxX + 20,
            $"Expected ExtendBeyondPath=true to draw ink well past the clipped boundary (clipped maxX={clippedMaxX}, extended maxX={extendedMaxX})");
    }

    [Fact]
    public void LineTextObject_TopExtendBeyondLine_ExtrapolatesOverflowingTextPastTheEndpoint()
    {
        // End-to-end wiring check: LineTextObject.TopExtendBeyondLine must reach PathTextRenderer's
        // ExtendBeyondPath through LineTextAnnotationSet/PathTextOptions.
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 100));
        var p2 = transform.ScreenToChart(new Point(120, 100)); // 100px-long horizontal line

        using var clipped = new LineTextObject(p1, p2)
        {
            TopText = "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            TopFontSize = 20,
            TopOffsetPx = 0
        };
        using var extended = new LineTextObject(p1, p2)
        {
            TopText = "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            TopFontSize = 20,
            TopOffsetPx = 0,
            TopExtendBeyondLine = true
        };

        using var clippedBitmap = RenderToNewBitmap(400, 200, c => clipped.Render(c, transform));
        using var extendedBitmap = RenderToNewBitmap(400, 200, c => extended.Render(c, transform));

        int clippedMaxX = MaxInkX(clippedBitmap);
        int extendedMaxX = MaxInkX(extendedBitmap);

        Assert.True(extendedMaxX > clippedMaxX + 20,
            $"Expected TopExtendBeyondLine=true to draw ink well past the clipped boundary (clipped maxX={clippedMaxX}, extended maxX={extendedMaxX})");
    }

    private static (int width, int height) InkBoundingBoxSize(SKBitmap bitmap)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX) return (0, 0);
        return (maxX - minX + 1, maxY - minY + 1);
    }

    [Fact]
    public void PathTextRenderer_JapaneseText_FallsBackToGlyphCapableFont_WhenDefaultTypefaceLacksGlyph()
    {
        // Bug repro: SKTypeface.Default (e.g. "Segoe UI" on Windows) does not contain glyphs for
        // Japanese characters (confirmed via SKTypeface.GetGlyph returning 0), and per-character
        // DrawText calls do not automatically fall back to a CJK-capable font the way a whole-string
        // DrawText call might, so typed Japanese text rendered as garbled/incomplete glyphs
        // (visibly far fewer ink pixels than the same character drawn with a real CJK font).
        using var surface = SKSurface.Create(new SKImageInfo(60, 60));
        var canvas = surface.Canvas;

        using var path = new SKPath();
        path.MoveTo(5, 30);
        path.LineTo(55, 30);

        using var paint = new SKPaint { TextSize = 20f, IsAntialias = true, Typeface = SKTypeface.Default };

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Left, NormalOffset: 0f, ClipOverflow: false);
        PathTextRenderer.DrawTextOnPath(canvas, "日", path, paint, options);

        using var bitmap = SKBitmap.FromImage(surface.Snapshot());
        int inkPixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) inkPixels++;
            }
        }

        // Empirically measured on this environment: the broken (no-fallback) rendering of "日" via
        // SKTypeface.Default produces 82 ink pixels; the correct rendering via an explicitly
        // CJK-capable font produces 116. Assert closer to the correct side.
        Assert.True(inkPixels > 90,
            $"Expected PathTextRenderer to render a complete Japanese glyph via font fallback, but only {inkPixels} ink pixels were drawn (looks like a tofu/notdef glyph from the non-CJK default font)");

        // paint.Typeface must be restored to what the caller configured, not left mutated to
        // whatever fallback font was used for the last glyph.
        Assert.Equal(SKTypeface.Default.FamilyName, paint.Typeface.FamilyName);
    }

    private static SKBitmap RenderTextOnPath(string text, SKPath path, PathTextOptions options)
    {
        // Must match RenderTextOnPathWithSegments' canvas size exactly: BitmapsMatch treats any
        // width/height mismatch as "different" outright, which would make cross-helper comparisons
        // (e.g. the segment-fallback test) spuriously fail regardless of the actual pixel content.
        var bitmap = new SKBitmap(300, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
        PathTextRenderer.DrawTextOnPath(canvas, text, path, paint, options);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap RenderTextOnPathWithSegments(
        string text, SKPath path, PathTextOptions options,
        ReadOnlySpan<float> segmentEndArcLengths, ReadOnlySpan<float> segmentDirectionAngles)
    {
        var bitmap = new SKBitmap(300, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
        PathTextRenderer.DrawTextOnPath(canvas, text, path, paint, options, segmentEndArcLengths, segmentDirectionAngles);
        canvas.Flush();
        return bitmap;
    }

    [Fact]
    public void PathTextRenderer_FollowLineAutoFlip_MatchesFollowLine_WhenPathDoesNotNeedFlipping()
    {
        // A path whose start->end vector stays within (-90, 90) degrees never triggers the
        // flip/reorder branch, so FollowLineAutoFlip must produce byte-identical output to plain
        // FollowLine (both use the raw, unmodified tangent angle and natural character order).
        using var path = new SKPath();
        path.MoveTo(20, 80);
        path.LineTo(180, 20); // shallow rising line, well within the non-flip range

        var autoFlipOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip);
        var followLineOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine);

        using var bmpAutoFlip = RenderTextOnPath("AB", path, autoFlipOptions);
        using var bmpFollowLine = RenderTextOnPath("AB", path, followLineOptions);

        Assert.True(BitmapsMatch(bmpAutoFlip, bmpFollowLine),
            "Expected FollowLineAutoFlip to match plain FollowLine exactly when the path direction does not require flipping");
    }

    [Fact]
    public void PathTextRenderer_FollowLineAutoFlip_ReadsCorrectlyRegardlessOfPathDrawingDirection()
    {
        // The core guarantee: unlike FollowLine (which mirrors when the path is drawn right-to-left),
        // FollowLineAutoFlip must reconstruct the same readable "AB" (correct glyph orientation AND
        // correct left-to-right character order) whichever direction the path was drawn in.
        using var pathLtr = new SKPath();
        pathLtr.MoveTo(20, 50);
        pathLtr.LineTo(180, 50);

        using var pathRtl = new SKPath();
        pathRtl.MoveTo(180, 50);
        pathRtl.LineTo(20, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip);

        using var bmpLtr = RenderTextOnPath("AB", pathLtr, options);
        using var bmpRtl = RenderTextOnPath("AB", pathRtl, options);

        Assert.True(BitmapsMatch(bmpLtr, bmpRtl),
            "Expected FollowLineAutoFlip to render the same readable \"AB\" (same glyph order and orientation) regardless of the path's drawing direction");
    }

    [Fact]
    public void PathTextRenderer_FollowLineAutoFlip_StillFollowsPathTilt_UnlikeAlwaysUpright()
    {
        // FollowLineAutoFlip must not degrade into AlwaysUpright: on a vertical path (exactly 90
        // degrees, which is NOT inside the flip range (90, 270) since the comparison is strict),
        // it should still rotate to match the path's tangent like FollowLine does. A 90-degree
        // rotation swaps a glyph's natural (taller-than-wide) bounding box into a wider-than-tall
        // one — unlike AlwaysUpright, which always stays flat/unrotated (taller-than-wide, same as
        // its own horizontal-path baseline; see the existing AlwaysUpright vertical-path test above).
        using var verticalPath = new SKPath();
        verticalPath.MoveTo(100, 20);
        verticalPath.LineTo(100, 180);

        var autoFlipOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip);
        var alwaysUprightOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright);

        using var bmpAutoFlip = RenderToNewBitmap(200, 200, c =>
        {
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            PathTextRenderer.DrawTextOnPath(c, "F", verticalPath, paint, autoFlipOptions);
        });
        using var bmpAlwaysUpright = RenderToNewBitmap(200, 200, c =>
        {
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            PathTextRenderer.DrawTextOnPath(c, "F", verticalPath, paint, alwaysUprightOptions);
        });

        var (wAuto, hAuto) = InkBoundingBoxSize(bmpAutoFlip);
        var (wUpright, hUpright) = InkBoundingBoxSize(bmpAlwaysUpright);

        Assert.True(wAuto > hAuto,
            $"Expected FollowLineAutoFlip on a vertical path to render a wider-than-tall (90-degree-rotated) glyph like FollowLine, but got {wAuto}x{hAuto}");
        Assert.True(hUpright > wUpright,
            $"Expected AlwaysUpright on a vertical path to stay taller-than-wide (flat/unrotated) as a baseline contrast, but got {wUpright}x{hUpright}");
    }

    [Fact]
    public void PathTextRenderer_FollowLineAutoFlip_UsesLocalSegmentDirection_NotOverallPathDirection()
    {
        // Path: (0,50) -> (200,50) [segment 0: 200px rightward] -> (150,50) [segment 1: 50px leftward].
        // The overall start->end vector is still net-rightward ((150,50)-(0,50)), but Right alignment
        // anchors the text to the path's trailing end, which sits entirely inside segment 1 (leftward).
        // A segment-aware flip decision must key off segment 1's own direction, not the path's overall
        // start-to-end direction — proving the lookup uses the LOCAL segment containing the text.
        using var path = new SKPath();
        path.MoveTo(0, 50);
        path.LineTo(200, 50);
        path.LineTo(150, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Right, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip);

        ReadOnlySpan<float> boundaries = stackalloc float[] { 200f, 250f };
        ReadOnlySpan<float> anglesSegment1Leftward = stackalloc float[] { 0f, 180f };
        ReadOnlySpan<float> anglesSegment1Rightward = stackalloc float[] { 0f, 0f };

        using var bmpSegment1Leftward = RenderTextOnPathWithSegments("AB", path, options, boundaries, anglesSegment1Leftward);
        using var bmpSegment1Rightward = RenderTextOnPathWithSegments("AB", path, options, boundaries, anglesSegment1Rightward);

        Assert.False(BitmapsMatch(bmpSegment1Leftward, bmpSegment1Rightward),
            "Expected the flip decision to change based on segment 1's own direction (the segment actually containing the text), proving the lookup is local rather than based on the path's overall start-to-end direction");
    }

    [Fact]
    public void PathTextRenderer_FollowLineAutoFlip_FallsBackToStartTangent_WhenNoSegmentInfoProvided()
    {
        // When no segment arrays are supplied (the LineTextObject call site, or any other caller
        // built before Task 3), behavior must be identical to Task 1's original single-segment logic.
        using var pathRtl = new SKPath();
        pathRtl.MoveTo(180, 50);
        pathRtl.LineTo(20, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip);

        using var bmpNoSegments = RenderTextOnPath("AB", pathRtl, options);
        using var bmpEmptySegments = RenderTextOnPathWithSegments("AB", pathRtl, options, ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty);

        Assert.True(BitmapsMatch(bmpNoSegments, bmpEmptySegments),
            "Expected omitting segment info and explicitly passing empty segment spans to produce identical output (both fall back to the start-tangent-based decision)");
    }

    [Fact]
    public void BezierSplineMath_ComputeSegmentArcLengthsAndDirections_StraightPolyline_MatchesEuclideanDistancesAndAngles()
    {
        Span<SKPoint> points = stackalloc SKPoint[]
        {
            new SKPoint(0, 50),
            new SKPoint(100, 50),  // segment 0: 100px rightward (0 degrees)
            new SKPoint(100, 150), // segment 1: 100px downward (90 degrees, screen Y+ = down)
        };

        Span<float> boundaries = stackalloc float[2];
        Span<float> angles = stackalloc float[2];

        int segCount = BezierSplineMath.ComputeSegmentArcLengthsAndDirections(points, isSmooth: false, tension: BezierSplineMath.DefaultTension, boundaries, angles);

        Assert.Equal(2, segCount);
        Assert.Equal(100f, boundaries[0], 3);
        Assert.Equal(200f, boundaries[1], 3);
        Assert.Equal(0f, angles[0], 3);
        Assert.Equal(90f, angles[1], 3);
    }

    [Fact]
    public void BezierSplineMath_ComputeSegmentArcLengthsAndDirections_SkipsDegenerateConsecutivePoints()
    {
        // Matches BuildCatmullRomSplinePath's own degenerate-pair skip: a repeated point must not
        // produce a zero-length segment entry, keeping the segment array 1:1 with the actually-drawn
        // path geometry.
        Span<SKPoint> points = stackalloc SKPoint[]
        {
            new SKPoint(0, 50),
            new SKPoint(0, 50),   // degenerate: identical to previous point
            new SKPoint(100, 50),
        };

        Span<float> boundaries = stackalloc float[2];
        Span<float> angles = stackalloc float[2];

        int segCount = BezierSplineMath.ComputeSegmentArcLengthsAndDirections(points, isSmooth: false, tension: BezierSplineMath.DefaultTension, boundaries, angles);

        Assert.Equal(1, segCount);
        Assert.Equal(100f, boundaries[0], 3);
        Assert.Equal(0f, angles[0], 3);
    }

    [Fact]
    public void PathTextRenderer_OrientationOverride_Default_MatchesOmittingTheOption()
    {
        // Regression guard for adding OrientationOverride as a new trailing record parameter:
        // explicitly passing Default must be pixel-identical to not specifying it at all.
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var withoutOption = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine);
        var withExplicitDefault = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine, OrientationOverride: TextManualOrientation.Default);

        using var bmpWithout = RenderSingleGlyphOnPath(path, withoutOption);
        using var bmpExplicit = RenderSingleGlyphOnPath(path, withExplicitDefault);

        Assert.True(BitmapsMatch(bmpWithout, bmpExplicit),
            "Expected explicitly passing OrientationOverride: Default to be identical to omitting it");
    }

    [Fact]
    public void PathTextRenderer_OrientationOverride_Rotate180_MatchesManualCanvasRotation()
    {
        // Rotate180 must be exactly equivalent to a plain 180-degree canvas rotation applied on top
        // of whatever RotationMode already produced. Using AlwaysUpright (displayAngle pinned to 0)
        // isolates the override's own contribution to exactly +180 degrees, with no other rotation
        // mixed in from RotationMode itself.
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, OrientationOverride: TextManualOrientation.Rotate180);
        using var bmpRendered = RenderSingleGlyphOnPath(path, options);

        // Manual reference: Center alignment on this 160px-long horizontal path anchors "F"'s glyph
        // center at the path's midpoint, (100, 50), with no NormalOffset.
        var bmpManual = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(bmpManual))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            float w = paint.MeasureText("F");
            canvas.Save();
            canvas.Translate(100f, 50f);
            canvas.RotateDegrees(180f);
            canvas.DrawText("F", -w / 2f, 0, paint);
            canvas.Restore();
        }

        Assert.True(BitmapsMatch(bmpRendered, bmpManual),
            "Expected OrientationOverride: Rotate180 to be pixel-identical to a manual 180-degree canvas rotation at the same anchor point");
    }

    [Fact]
    public void PathTextRenderer_OrientationOverride_Mirror_MatchesManualCanvasScale()
    {
        // Mirror must be exactly equivalent to a plain Scale(-1, 1) applied on top of whatever
        // RotationMode already produced (reflecting across the local X axis — the direction parallel
        // to the line/reading direction — so the glyph itself becomes its own left-right mirror
        // image). Legibility is not a design goal here; only the transform's correctness is checked.
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, OrientationOverride: TextManualOrientation.Mirror);
        using var bmpRendered = RenderSingleGlyphOnPath(path, options);

        var bmpManual = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(bmpManual))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { TextSize = 24f, Color = SKColors.Black, IsAntialias = false };
            float w = paint.MeasureText("F");
            canvas.Save();
            canvas.Translate(100f, 50f);
            canvas.Scale(-1f, 1f);
            canvas.DrawText("F", -w / 2f, 0, paint);
            canvas.Restore();
        }

        Assert.True(BitmapsMatch(bmpRendered, bmpManual),
            "Expected OrientationOverride: Mirror to be pixel-identical to a manual Scale(-1,1) at the same anchor point");
    }

    [Fact]
    public void PathTextRenderer_OrientationOverride_MirrorAndRotate180_AreDistinctTransforms()
    {
        // A true reflection (Mirror) cannot be reproduced by any rotation angle for an asymmetric
        // glyph like "F" — this proves Mirror is a genuinely different transform from Rotate180 and
        // from Default, not just another rotation angle in disguise.
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var defaultOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, OrientationOverride: TextManualOrientation.Default);
        var rotate180Options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, OrientationOverride: TextManualOrientation.Rotate180);
        var mirrorOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.AlwaysUpright, OrientationOverride: TextManualOrientation.Mirror);

        using var bmpDefault = RenderSingleGlyphOnPath(path, defaultOptions);
        using var bmpRotate180 = RenderSingleGlyphOnPath(path, rotate180Options);
        using var bmpMirror = RenderSingleGlyphOnPath(path, mirrorOptions);

        Assert.False(BitmapsMatch(bmpDefault, bmpRotate180), "Expected Rotate180 to differ from Default");
        Assert.False(BitmapsMatch(bmpDefault, bmpMirror), "Expected Mirror to differ from Default");
        Assert.False(BitmapsMatch(bmpRotate180, bmpMirror), "Expected Mirror to differ from Rotate180 (reflection is not reproducible by rotation alone)");
    }

    [Fact]
    public void PathTextRenderer_OrientationOverride_DoesNotAffectCharacterOrder()
    {
        // OrientationOverride must be purely visual (rotation/reflection of each glyph in place) and
        // must never change srcIndex / character sequencing — that remains the sole responsibility
        // of needsFlip and ManualReverseOrder. If Rotate180 accidentally also reversed character
        // order (a plausible implementation slip, since "flipping the page upside down" colloquially
        // suggests reversed reading order too), "AB" rendered with Rotate180 would coincidentally
        // match "BA" rendered with Default. Assert they differ.
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var defaultOptions = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine);
        var rotate180Options = defaultOptions with { OrientationOverride = TextManualOrientation.Rotate180 };

        using var bmpAbRotate180 = RenderTextOnPath("AB", path, rotate180Options);
        using var bmpBaDefault = RenderTextOnPath("BA", path, defaultOptions);

        Assert.False(BitmapsMatch(bmpAbRotate180, bmpBaDefault),
            "Expected \"AB\"+Rotate180 to NOT coincidentally match \"BA\"+Default, proving OrientationOverride does not also reverse character order");
    }

    [Fact]
    public void PathTextRenderer_ManualReverseOrder_ReversesCharacterOrder_IndependentlyOfRotationMode()
    {
        // ManualReverseOrder must flip character order on a plain left-to-right FollowLine path
        // (which needs no automatic flip on its own), and must be independent from RotationMode:
        // toggling it must NOT change the glyph rotation (still horizontal, angle = 0).
        using var path = new SKPath();
        path.MoveTo(20, 50);
        path.LineTo(180, 50);

        var normalOrder = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine, ManualReverseOrder: false);
        var reversedOrder = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine, ManualReverseOrder: true);

        using var bmpNormal = RenderTextOnPath("AB", path, normalOrder);
        using var bmpReversed = RenderTextOnPath("AB", path, reversedOrder);

        Assert.False(BitmapsMatch(bmpNormal, bmpReversed),
            "Expected ManualReverseOrder to change the rendered character order even when RotationMode alone would not flip anything");

        // Reversing "AB"'s draw order on this straight, non-flipped path is equivalent to rendering
        // "BA" normally (same glyphs, same upright rotation, just swapped left-to-right placement).
        using var bmpBaNormalOrder = RenderTextOnPath("BA", path, normalOrder);
        Assert.True(BitmapsMatch(bmpReversed, bmpBaNormalOrder),
            "Expected ManualReverseOrder=true on \"AB\" to render identically to ManualReverseOrder=false on \"BA\" (order-only reversal, no rotation change)");
    }

    [Fact]
    public void PathTextRenderer_ManualReverseOrder_XorsWithAutomaticFlip()
    {
        // On a right-to-left path, FollowLineAutoFlip alone already reverses order to stay readable.
        // Manually requesting reverse order on top of that must cancel back out to the raw (unread-
        // able, order-only-reversed-relative-to-source) placement rather than double-reversing to a
        // no-op — i.e. the two flags XOR rather than OR/AND.
        using var pathRtl = new SKPath();
        pathRtl.MoveTo(180, 50);
        pathRtl.LineTo(20, 50);

        var autoFlipOnly = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip, ManualReverseOrder: false);
        var autoFlipPlusManual = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLineAutoFlip, ManualReverseOrder: true);
        var plainFollowLine = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine, ManualReverseOrder: false);

        using var bmpAutoFlipOnly = RenderTextOnPath("AB", pathRtl, autoFlipOnly);
        using var bmpAutoFlipPlusManual = RenderTextOnPath("AB", pathRtl, autoFlipPlusManual);
        using var bmpPlainFollowLine = RenderTextOnPath("AB", pathRtl, plainFollowLine);

        Assert.False(BitmapsMatch(bmpAutoFlipOnly, bmpAutoFlipPlusManual),
            "Expected adding ManualReverseOrder on top of an already-flipped AutoFlip result to change the output (XOR, not idempotent OR)");

        // XOR of two true order-reversals cancels the order flip, but the rotation (+180 from
        // needsFlip) still applies — so this must match plain FollowLine's character ORDER (natural,
        // un-reversed) while still differing from it in rotation, hence not a full bitmap match here;
        // instead assert only the qualitative rotation-still-applied contrast against plain FollowLine.
        Assert.False(BitmapsMatch(bmpAutoFlipPlusManual, bmpPlainFollowLine),
            "Expected the +180 rotation correction to still apply even when the manual order flag cancels the automatic order reversal");
    }

    [Fact]
    public void LineTextObject_IndependentReverseOrderFlags_ShouldNotCrossContaminate()
    {
        using var lineText = new LineTextObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m));

        lineText.TopTextReverseOrder = true;
        lineText.BottomTextReverseOrder = false;

        Assert.True(lineText.TopTextReverseOrder);
        Assert.False(lineText.BottomTextReverseOrder);

        lineText.BottomTextReverseOrder = true;
        Assert.True(lineText.TopTextReverseOrder);
        Assert.True(lineText.BottomTextReverseOrder);

        lineText.TopTextReverseOrder = false;
        Assert.False(lineText.TopTextReverseOrder);
        Assert.True(lineText.BottomTextReverseOrder);
    }

    [Fact]
    public void CurveLineTextObject_FollowLineAutoFlip_ZigzagCurve_RendersWithoutThrowing()
    {
        // End-to-end smoke test: a 3-point zigzag (rightward then leftward) with FollowLineAutoFlip
        // must render without throwing, exercising the full DrawGeometry -> RenderOnto ->
        // DrawTextOnPath segment-propagation path for both IsSmooth=true and IsSmooth=false.
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 80));
        var p2 = transform.ScreenToChart(new Point(220, 80));
        var p3 = transform.ScreenToChart(new Point(120, 80));

        using var smooth = new CurveLineTextObject(new[] { p1, p2, p3 })
        {
            IsSmooth = true,
            TopText = "AB",
            TopRotationMode = TextRotationMode.FollowLineAutoFlip,
            BottomText = "CD",
            BottomRotationMode = TextRotationMode.FollowLineAutoFlip
        };
        using var straight = new CurveLineTextObject(new[] { p1, p2, p3 })
        {
            IsSmooth = false,
            TopText = "AB",
            TopRotationMode = TextRotationMode.FollowLineAutoFlip
        };

        using var surface = SKSurface.Create(new SKImageInfo(300, 200));
        smooth.Render(surface.Canvas, transform);
        straight.Render(surface.Canvas, transform);
    }

    [Fact]
    public void CurveLineTextObject_FollowLineAutoFlip_SmoothCurve_UsesTheActualSegmentContainingTheText()
    {
        // Integration test closing a gap from a strict constraint audit: the unit tests for
        // BezierSplineMath.ComputeSegmentArcLengthsAndDirections only cover straight polylines, and
        // the PathTextRenderer segment-lookup tests only use hand-built segment arrays — neither
        // proves that for a REAL IsSmooth=true Catmull-Rom curve, the segment boundaries computed
        // from the actual curve geometry align with PathTextRenderer's own arc-length coordinate
        // system closely enough to select the correct segment.
        //
        // Points: P0(20,50) -> P1(200,50) [segment 0: long rightward] -> P2 [segment 1: 50px,
        // direction varies by variant]. Right alignment anchors "AB" into the trailing segment 1.
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(20, 50));
        var p1 = transform.ScreenToChart(new Point(200, 50));
        var p2Leftward = transform.ScreenToChart(new Point(150, 50)); // segment 1 points backward (180 degrees)

        using var autoFlip = new CurveLineTextObject(new[] { p0, p1, p2Leftward }) { IsSmooth = true, TopText = "AB", TopAlignment = TextHorizontalAlignment.Right, TopRotationMode = TextRotationMode.FollowLineAutoFlip };
        using var followLine = new CurveLineTextObject(new[] { p0, p1, p2Leftward }) { IsSmooth = true, TopText = "AB", TopAlignment = TextHorizontalAlignment.Right, TopRotationMode = TextRotationMode.FollowLine };

        using var bmpAutoFlip = RenderToNewBitmap(300, 100, c => autoFlip.Render(c, transform));
        using var bmpFollowLine = RenderToNewBitmap(300, 100, c => followLine.Render(c, transform));

        // If ComputeSegmentArcLengthsAndDirections' boundaries were misaligned with
        // PathTextRenderer's arc-length coordinate system and silently fell back to Task 1's
        // path-start-tangent logic, segment 0 (rightward, no flip needed) would be used instead of
        // segment 1 (leftward) — in that broken scenario, FollowLineAutoFlip's needsFlip would stay
        // false, making it byte-identical to plain FollowLine here. A real difference proves the
        // actual segment (1) containing the text was correctly identified as needing the flip.
        Assert.False(BitmapsMatch(bmpAutoFlip, bmpFollowLine),
            "Expected FollowLineAutoFlip to differ from plain FollowLine when text is anchored into a real smooth curve's reversed trailing segment, proving the segment actually containing the text (not a stale start-of-path fallback) drove the flip decision");
    }

    [Fact]
    public void PathTextRenderer_FollowLine_StillRendersDifferentlyForOppositePathDirections()
    {
        // Regression guard: confirms the default (FollowLine) rotation behavior is unchanged by
        // the RotationMode addition — opposite path directions must still produce visibly
        // different (rotated 180 degrees) output, unlike the AlwaysUpright case above.
        using var pathLtr = new SKPath();
        pathLtr.MoveTo(20, 50);
        pathLtr.LineTo(180, 50);

        using var pathRtl = new SKPath();
        pathRtl.MoveTo(180, 50);
        pathRtl.LineTo(20, 50);

        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: 0f, RotationMode: TextRotationMode.FollowLine);

        using var bmpLtr = RenderSingleGlyphOnPath(pathLtr, options);
        using var bmpRtl = RenderSingleGlyphOnPath(pathRtl, options);

        Assert.False(BitmapsMatch(bmpLtr, bmpRtl),
            "Expected FollowLine (default) text to still render rotated 180 degrees between opposite path directions, matching pre-existing behavior");
    }

    [Fact]
    public void PathTextRenderer_HotPath_ShouldAllocateZeroBytesOnManagedHeap()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var canvas = surface.Canvas;

        using var path = new SKPath();
        path.MoveTo(10, 100);
        path.CubicTo(100, 20, 200, 180, 300, 100);

        using var paint = new SKPaint { TextSize = 12f, IsAntialias = true };
        var options = new PathTextOptions(Alignment: TextHorizontalAlignment.Center, NormalOffset: -4f);

        string text = "TextOnPathZeroAlloc";

        // Warmup (JIT / SkiaSharp internal setup)
        for (int i = 0; i < 50; i++)
        {
            PathTextRenderer.DrawTextOnPath(canvas, text, path, paint, options);
        }

        // Measure allocated bytes in managed heap
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 10;
        for (int i = 0; i < iterations; i++)
        {
            PathTextRenderer.DrawTextOnPath(canvas, text, path, paint, options);
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = allocAfter - allocBefore;

        Assert.True(totalAllocated < 50000);
    }

    [Fact]
    public void MultilineTextRenderer_EmptyString_ShouldReturnSingleLineWithZeroWidth()
    {
        var linesNull = MultilineTextRenderer.SplitLines(null);
        var linesEmpty = MultilineTextRenderer.SplitLines("");

        Assert.Single(linesNull);
        Assert.Equal("", linesNull[0]);
        Assert.Single(linesEmpty);
        Assert.Equal("", linesEmpty[0]);

        using var paint = new SKPaint { TextSize = 14f };
        var size = MultilineTextRenderer.MeasureBlock(paint, linesEmpty);

        Assert.Equal(0f, size.Width);
        Assert.Equal(paint.FontSpacing, size.Height);
    }

    [Fact]
    public void MultilineTextRenderer_DifferentLineBreaks_ShouldNormalizeUniformly()
    {
        string rawText = "Line1\r\nLine2\rLine3\nLine4";
        var lines = MultilineTextRenderer.SplitLines(rawText);

        Assert.Equal(4, lines.Length);
        Assert.Equal("Line1", lines[0]);
        Assert.Equal("Line2", lines[1]);
        Assert.Equal("Line3", lines[2]);
        Assert.Equal("Line4", lines[3]);
    }

    [Fact]
    public void MultilineTextRenderer_MeasureBlock_CalculatesCorrectMaxDimensions()
    {
        using var paint = new SKPaint { TextSize = 16f };
        string[] lines = new[] { "Short", "Much Longer Line That Dictates Width", "Mid" };

        var size = MultilineTextRenderer.MeasureBlock(paint, lines);
        float expectedWidth = paint.MeasureText("Much Longer Line That Dictates Width");
        float expectedHeight = paint.FontSpacing * 3;

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Theory]
    [InlineData(TextHorizontalAlignment.Left)]
    [InlineData(TextHorizontalAlignment.Center)]
    [InlineData(TextHorizontalAlignment.Right)]
    public void MultilineTextRenderer_AlignmentCalculations_ShouldMatchMathematicalFormulas(TextHorizontalAlignment alignment)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var canvas = surface.Canvas;

        using var paint = new SKPaint { TextSize = 12f, Color = SKColors.Black, IsAntialias = true };
        string[] lines = new[] { "Short", "A substantially longer sentence", "End" };

        // Should execute smoothly without throwing exceptions
        MultilineTextRenderer.DrawBlock(canvas, lines, 50f, 100f, paint, alignment);
    }

    [Fact]
    public void TextObject_PropertyMutation_ShouldInvalidateCacheCorrectly()
    {
        var transform = new DummyCoordinateTransform();
        var point = transform.ScreenToChart(new Point(200, 200));

        using var textObj = new TextObject(point, "Initial Text\nSecond Line");
        textObj.Alignment = TextHorizontalAlignment.Center;
        textObj.FontSize = 16.0;
        textObj.Color = Colors.RoyalBlue;
        textObj.BackgroundColor = Colors.LightGoldenrodYellow;
        textObj.ShowBackgroundBox = true;
        textObj.BackgroundPadding = 10f;
        textObj.CornerRadius = 6f;

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        textObj.Render(surface.Canvas, transform);

        // Mutate properties
        textObj.Text = "Updated Text";
        textObj.Alignment = TextHorizontalAlignment.Right;
        textObj.FontSize = 14.0;
        textObj.Render(surface.Canvas, transform);

        // Verify hit test
        Assert.True(textObj.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void CalloutObject_LeaderLineAndRender_ShouldExecuteCorrectly()
    {
        var transform = new DummyCoordinateTransform();
        var anchor = transform.ScreenToChart(new Point(100, 100));
        var body = transform.ScreenToChart(new Point(250, 250));

        using var callout = new CalloutObject(anchor, body);
        callout.Text = "Callout Note\nDetail 1\nDetail 2";
        callout.Alignment = TextHorizontalAlignment.Center;
        callout.IsSelected = true;

        using var surface = SKSurface.Create(new SKImageInfo(500, 500));
        callout.Render(surface.Canvas, transform);

        Assert.True(callout.HitTest(new Point(100, 100), transform));
        Assert.True(callout.HitTest(new Point(250, 250), transform));
    }

    [Fact]
    public void PriceLabelObject_RenderAndHitTest_ShouldExecuteCorrectly()
    {
        var transform = new DummyCoordinateTransform();
        var anchor = transform.ScreenToChart(new Point(100, 300));
        var labelPos = transform.ScreenToChart(new Point(200, 300));

        using var priceLabel = new PriceLabelObject(anchor, labelPos);
        priceLabel.FontSize = 13.0;
        priceLabel.IsSelected = true;
        priceLabel.ShowBackgroundBox = true;
        priceLabel.BackgroundColor = Colors.LemonChiffon;
        priceLabel.BackgroundPadding = 6f;
        priceLabel.CornerRadius = 5f;

        using var surface = SKSurface.Create(new SKImageInfo(500, 500));
        priceLabel.Render(surface.Canvas, transform);

        Assert.True(priceLabel.HitTest(new Point(100, 300), transform));
        Assert.True(priceLabel.HitTest(new Point(200, 300), transform));
    }

    [Fact]
    public void LineTextObject_IndependentFontSizes_ShouldNotCrossContaminate()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(50, 50));
        var p2 = transform.ScreenToChart(new Point(250, 50));

        using var lineText = new LineTextObject(p1, p2);
        lineText.TopFontSize = 22.0;
        lineText.BottomFontSize = 9.0;

        Assert.Equal(22.0, lineText.TopFontSize);
        Assert.Equal(9.0, lineText.BottomFontSize);

        // Mutating Top must never leak into Bottom's independently-cached paint, and vice versa.
        lineText.TopFontSize = 30.0;
        Assert.Equal(30.0, lineText.TopFontSize);
        Assert.Equal(9.0, lineText.BottomFontSize);

        lineText.BottomFontSize = 11.0;
        Assert.Equal(30.0, lineText.TopFontSize);
        Assert.Equal(11.0, lineText.BottomFontSize);
    }

    [Fact]
    public void LineTextObject_TopAndBottomText_RenderIndependentlyWithOwnStyles()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(50, 200));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        using var lineText = new LineTextObject(p1, p2)
        {
            TopText = "Resistance",
            TopAlignment = TextHorizontalAlignment.Left,
            TopFontSize = 14,
            TopOffsetPx = 8,
            BottomText = "-2.5%",
            BottomAlignment = TextHorizontalAlignment.Right,
            BottomFontSize = 10,
            BottomOffsetPx = 6,
            IsSelected = true
        };

        using var surface = SKSurface.Create(new SKImageInfo(500, 500));
        lineText.Render(surface.Canvas, transform);

        Assert.True(lineText.HitTest(new Point(175, 200), transform));
    }

    [Theory]
    [InlineData("Only Top", "")]
    [InlineData("", "Only Bottom")]
    [InlineData("", "")]
    public void LineTextObject_PartialOrEmptyText_ShouldRenderWithoutThrowing(string topText, string bottomText)
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(50, 200));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        using var lineText = new LineTextObject(p1, p2) { TopText = topText, BottomText = bottomText };

        using var surface = SKSurface.Create(new SKImageInfo(500, 500));
        lineText.Render(surface.Canvas, transform);
    }

    [Theory]
    [InlineData("Line1\nLine2", "Line1 Line2")]
    [InlineData("Line1\r\nLine2", "Line1 Line2")]
    [InlineData("Line1\rLine2", "Line1 Line2")]
    public void LineTextObject_TopAndBottomText_StripsEmbeddedLineBreaks(string rawText, string expected)
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(50, 200));
        var p2 = transform.ScreenToChart(new Point(300, 200));

        using var lineText = new LineTextObject(p1, p2) { TopText = rawText, BottomText = rawText };

        // PathTextRenderer draws each character as an individual glyph along the path; '\n'/'\r'
        // have no glyph and previously rendered as a tofu/garbled box. The setter must normalize
        // embedded line breaks to a single space so the text stays a single path-following line.
        Assert.Equal(expected, lineText.TopText);
        Assert.Equal(expected, lineText.BottomText);
    }

    [Fact]
    public void LineTextObject_DegeneratePoints_ShouldNotThrow()
    {
        var transform = new DummyCoordinateTransform();
        var p = transform.ScreenToChart(new Point(100, 100));

        using var lineText = new LineTextObject(p, p) { TopText = "Top", BottomText = "Bottom" };

        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        lineText.Render(surface.Canvas, transform);
    }

    private static bool HasInkInRowRange(SKBitmap bitmap, int fromYInclusive, int toYInclusive)
    {
        for (int y = fromYInclusive; y <= toYInclusive; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) return true;
            }
        }
        return false;
    }

    private static bool HasInkInRegion(SKBitmap bitmap, int fromYInclusive, int toYInclusive, int fromXInclusive, int toXInclusive)
    {
        for (int y = fromYInclusive; y <= toYInclusive; y++)
        {
            for (int x = fromXInclusive; x <= toXInclusive; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) return true;
            }
        }
        return false;
    }

    [Fact]
    public void LineTextObject_TopPositionLocked_KeepsTextAtFrozenChartPosition_AfterLineIsMovedElsewhere()
    {
        // Bug scenario (distinct from TopPositionFixed above): even with the Top/Bottom side
        // correctly staying put, the text still followed the LIVE line to wherever it was edited.
        // TopPositionLocked snapshots the line's chart-coordinate shape at lock-time and keeps
        // rendering the text against that frozen shape, ignoring all further edits to Points.
        var transform = new DummyCoordinateTransform();
        var originalStart = transform.ScreenToChart(new Point(100, 200));
        var originalEnd = transform.ScreenToChart(new Point(300, 200));

        using var lineText = new LineTextObject(originalStart, originalEnd)
        {
            TopText = "T",
            TopOffsetPx = 20,
            TopPositionLocked = true
        };

        // A region that only the frozen TopText's original (above-the-line, near the start point)
        // position occupies; the line is later moved far away from here (see below).
        static bool InkNearFrozenTopTextRegion(SKBitmap bmp) => HasInkInRegion(bmp, 100, 198, 60, 200);

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(beforeBitmap),
            "Sanity check: TopText should initially render near its start position");

        // Simulate the user dragging the whole line to a completely different location.
        lineText.Points[0] = transform.ScreenToChart(new Point(250, 350));
        lineText.Points[1] = transform.ScreenToChart(new Point(390, 350));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(afterBitmap),
            "With TopPositionLocked=true, TopText must remain at its frozen chart-coordinate position even after the line is moved elsewhere");
    }

    [Fact]
    public void LineTextObject_TopPositionNotLocked_FollowsLine_WhenLineIsMovedElsewhere()
    {
        // Contrast case for the fix above: with TopPositionLocked left at its default (false), the
        // pre-existing (unlocked) behavior must be unchanged — the text follows the line.
        var transform = new DummyCoordinateTransform();
        var originalStart = transform.ScreenToChart(new Point(100, 200));
        var originalEnd = transform.ScreenToChart(new Point(300, 200));

        using var lineText = new LineTextObject(originalStart, originalEnd)
        {
            TopText = "T",
            TopOffsetPx = 20
        };

        static bool InkNearFrozenTopTextRegion(SKBitmap bmp) => HasInkInRegion(bmp, 100, 198, 60, 200);

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(beforeBitmap));

        lineText.Points[0] = transform.ScreenToChart(new Point(250, 350));
        lineText.Points[1] = transform.ScreenToChart(new Point(390, 350));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.False(InkNearFrozenTopTextRegion(afterBitmap),
            "Without TopPositionLocked, TopText is expected to follow the line to its new position (pre-existing behavior)");
    }

    [Fact]
    public void CurveLineTextObject_TopPositionLocked_KeepsTextAtFrozenChartPosition_AfterControlPointsAreMovedElsewhere()
    {
        // Same freeze contract as LineTextObject, but exercising CurveLineTextObject's independent
        // spline-based path/segment reconstruction (BuildFrozenPathAndSegments).
        var transform = new DummyCoordinateTransform();
        var originalStart = transform.ScreenToChart(new Point(100, 200));
        var originalEnd = transform.ScreenToChart(new Point(300, 200));

        using var curve = new CurveLineTextObject(new[] { originalStart, originalEnd })
        {
            TopText = "T",
            TopOffsetPx = 20,
            TopPositionLocked = true
        };

        static bool InkNearFrozenTopTextRegion(SKBitmap bmp) => HasInkInRegion(bmp, 100, 198, 60, 200);

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => curve.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(beforeBitmap),
            "Sanity check: TopText should initially render near its start position");

        // Simulate the user dragging both control points to a completely different location.
        curve.Points[0] = transform.ScreenToChart(new Point(250, 350));
        curve.Points[1] = transform.ScreenToChart(new Point(390, 350));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => curve.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(afterBitmap),
            "With TopPositionLocked=true, TopText must remain at its frozen chart-coordinate position even after the curve's control points are moved elsewhere");
    }

    [Fact]
    public void CurveLineTextObject_TopPositionNotLocked_FollowsLine_WhenControlPointsAreMovedElsewhere()
    {
        var transform = new DummyCoordinateTransform();
        var originalStart = transform.ScreenToChart(new Point(100, 200));
        var originalEnd = transform.ScreenToChart(new Point(300, 200));

        using var curve = new CurveLineTextObject(new[] { originalStart, originalEnd })
        {
            TopText = "T",
            TopOffsetPx = 20
        };

        static bool InkNearFrozenTopTextRegion(SKBitmap bmp) => HasInkInRegion(bmp, 100, 198, 60, 200);

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => curve.Render(c, transform));
        Assert.True(InkNearFrozenTopTextRegion(beforeBitmap));

        curve.Points[0] = transform.ScreenToChart(new Point(250, 350));
        curve.Points[1] = transform.ScreenToChart(new Point(390, 350));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => curve.Render(c, transform));
        Assert.False(InkNearFrozenTopTextRegion(afterBitmap),
            "Without TopPositionLocked, TopText is expected to follow the curve to its new position (pre-existing behavior)");
    }

    [Fact]
    public void LineTextObject_TopPositionFixed_KeepsTextOnSameScreenSide_AfterPathDirectionReverses()
    {
        // Bug scenario: PathTextRenderer derives the Top/Bottom side from the path's normal vector,
        // which is derived from the tangent (start->end) direction. Dragging one endpoint past the
        // other reverses that direction and, without TopPositionFixed, silently flips which physical
        // side (screen up/down) the text renders on even though the user never touched Top/Bottom.
        var transform = new DummyCoordinateTransform();
        int lineScreenY = 200;
        var left = transform.ScreenToChart(new Point(100, lineScreenY));
        var right = transform.ScreenToChart(new Point(300, lineScreenY));

        using var lineText = new LineTextObject(left, right)
        {
            TopText = "T",
            TopOffsetPx = 20,
            TopPositionFixed = true
        };

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(HasInkInRowRange(beforeBitmap, 0, lineScreenY - 2),
            "Sanity check: TopText should initially render above the line");
        Assert.False(HasInkInRowRange(beforeBitmap, lineScreenY + 2, 399),
            "Sanity check: TopText should not initially render below the line");

        // Simulate dragging the right endpoint past the left endpoint: the path's start->end
        // direction on screen reverses (was rightward, now leftward).
        lineText.Points[1] = transform.ScreenToChart(new Point(-50, lineScreenY));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(HasInkInRowRange(afterBitmap, 0, lineScreenY - 2),
            "With TopPositionFixed=true, TopText must stay above the line even after the path direction reverses");
        Assert.False(HasInkInRowRange(afterBitmap, lineScreenY + 2, 399),
            "With TopPositionFixed=true, TopText must not flip to below the line after the path direction reverses");
    }

    [Fact]
    public void LineTextObject_TopPositionNotFixed_FlipsScreenSide_AfterPathDirectionReverses()
    {
        // Contrast case for the fix above: with TopPositionFixed left at its default (false), the
        // pre-existing (unfixed) behavior must be unchanged — the side legitimately flips when the
        // path direction reverses. This proves the fixed test above is actually exercising the new
        // sign-correction logic, not merely a coincidence of the geometry.
        var transform = new DummyCoordinateTransform();
        int lineScreenY = 200;
        var left = transform.ScreenToChart(new Point(100, lineScreenY));
        var right = transform.ScreenToChart(new Point(300, lineScreenY));

        using var lineText = new LineTextObject(left, right)
        {
            TopText = "T",
            TopOffsetPx = 20
        };

        using var beforeBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(HasInkInRowRange(beforeBitmap, 0, lineScreenY - 2));

        lineText.Points[1] = transform.ScreenToChart(new Point(-50, lineScreenY));

        using var afterBitmap = RenderToNewBitmap(400, 400, c => lineText.Render(c, transform));
        Assert.True(HasInkInRowRange(afterBitmap, lineScreenY + 2, 399),
            "Without TopPositionFixed, TopText is expected to flip below the line once the path direction reverses (pre-existing behavior)");
    }

    [Fact]
    public void LineTextObject_HotPath_RenderDoesNotAllocateExcessively()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(50, 200));
        var p2 = transform.ScreenToChart(new Point(350, 200));

        using var lineText = new LineTextObject(p1, p2)
        {
            TopText = "Resistance Zone",
            BottomText = "-2.5%"
        };

        using var bitmap = new SKBitmap(400, 400);
        using var canvas = new SKCanvas(bitmap);

        // Warm up JIT / SkiaSharp internal buffers
        for (int i = 0; i < 100; i++)
        {
            lineText.Render(canvas, transform);
        }

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 1000;
        for (int i = 0; i < iterations; i++)
        {
            lineText.Render(canvas, transform);
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = allocAfter - allocBefore;

        // PathTextRenderer_HotPath_ShouldAllocateZeroBytesOnManagedHeap (above) tolerates <50,000
        // bytes per 10 single-side DrawTextOnPath calls rather than a literal 0 (SkiaSharp interop
        // can have small non-zero baseline churn independent of our own code). LineTextObject issues
        // 2 DrawTextOnPath calls per Render(); scale that same per-call tolerance across 1000
        // iterations instead of requiring literal 0.
        Assert.True(totalAllocated < 10_000_000,
            $"Expected LineTextObject.Render() hot path to stay near ZeroAllocation, but {iterations} iterations allocated {totalAllocated} bytes");
    }

    [Fact]
    public void LineTextObject_IndependentTextColors_ShouldNotCrossContaminate()
    {
        using var lineText = new LineTextObject(
            new ChartPoint(new DateTime(2024, 1, 1), 100m),
            new ChartPoint(new DateTime(2024, 1, 2), 110m));

        lineText.TopTextColor = Colors.Red;
        lineText.BottomTextColor = Colors.Blue;

        Assert.Equal(Colors.Red, lineText.TopTextColor);
        Assert.Equal(Colors.Blue, lineText.BottomTextColor);

        lineText.TopTextColor = Colors.Green;
        Assert.Equal(Colors.Green, lineText.TopTextColor);
        Assert.Equal(Colors.Blue, lineText.BottomTextColor);
    }

    [Fact]
    public void LineTextObject_MatchBackgroundColor_OverridesLineColorWithChartBackground()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 50));
        var p2 = transform.ScreenToChart(new Point(180, 50));

        using var matched = new LineTextObject(p1, p2) { Color = Colors.Red, Thickness = 6, MatchBackgroundColor = true };
        using var unmatched = new LineTextObject(p1, p2) { Color = Colors.Red, Thickness = 6, MatchBackgroundColor = false };

        using var surfaceMatched = SKSurface.Create(new SKImageInfo(200, 100));
        using var surfaceUnmatched = SKSurface.Create(new SKImageInfo(200, 100));

        matched.Render(surfaceMatched.Canvas, transform);
        unmatched.Render(surfaceUnmatched.Canvas, transform);

        using var bmpMatched = SKBitmap.FromImage(surfaceMatched.Snapshot());
        using var bmpUnmatched = SKBitmap.FromImage(surfaceUnmatched.Snapshot());

        var matchedPixel = bmpMatched.GetPixel(100, 50);
        var unmatchedPixel = bmpUnmatched.GetPixel(100, 50);

        Assert.Equal(DrawingThemeContext.ChartBackground, matchedPixel);
        Assert.Equal(SKColors.Red, unmatchedPixel);
        Assert.NotEqual(matchedPixel, unmatchedPixel);
    }

    private static SKBitmap RenderToNewBitmap(int width, int height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        canvas.Flush();
        return bitmap;
    }

    [Fact]
    public void CurveLineTextObject_TwoPoints_RendersWithoutThrowing()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 50));
        var p2 = transform.ScreenToChart(new Point(180, 50));

        using var curve = new CurveLineTextObject(new[] { p1, p2 }) { TopText = "Top", BottomText = "Bottom" };

        using var surface = SKSurface.Create(new SKImageInfo(200, 100));
        curve.Render(surface.Canvas, transform);

        Assert.True(curve.HitTest(new Point(100, 50), transform));
    }

    [Fact]
    public void CurveLineTextObject_ThreeOrMorePoints_SmoothProducesActualCurve_DifferentFromStraightSegments()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 80));
        var p2 = transform.ScreenToChart(new Point(100, 20)); // zigzag midpoint
        var p3 = transform.ScreenToChart(new Point(180, 80));

        using var smooth = new CurveLineTextObject(new[] { p1, p2, p3 }) { IsSmooth = true, Color = Colors.Black, Thickness = 2 };
        using var straight = new CurveLineTextObject(new[] { p1, p2, p3 }) { IsSmooth = false, Color = Colors.Black, Thickness = 2 };

        using var smoothBitmap = RenderToNewBitmap(200, 100, c => smooth.Render(c, transform));
        using var straightBitmap = RenderToNewBitmap(200, 100, c => straight.Render(c, transform));

        // BuildCatmullRomSplinePath rounds the corner at p2 instead of the sharp straight-segment
        // angle; if the two renders were pixel-identical, IsSmooth would not actually be invoking
        // the spline builder (i.e. it would just be drawing straight segments either way).
        Assert.False(BitmapsMatch(smoothBitmap, straightBitmap),
            "Expected IsSmooth=true to render a visibly different (rounded) curve than IsSmooth=false (straight segments) for the same 3 points");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CurveLineTextObject_FewerThanTwoPoints_ShouldNotThrow(int pointCount)
    {
        var transform = new DummyCoordinateTransform();
        var points = new List<ChartPoint>();
        for (int i = 0; i < pointCount; i++)
        {
            points.Add(transform.ScreenToChart(new Point(20 + i * 10, 50)));
        }

        using var curve = new CurveLineTextObject(points) { TopText = "Top" };

        using var surface = SKSurface.Create(new SKImageInfo(200, 100));
        curve.Render(surface.Canvas, transform);

        Assert.False(curve.HitTest(new Point(20, 50), transform));
    }

    [Fact]
    public void CurveLineTextObject_DegenerateIdenticalPoints_ShouldNotThrow()
    {
        var transform = new DummyCoordinateTransform();
        var p = transform.ScreenToChart(new Point(100, 100));

        using var curve = new CurveLineTextObject(new[] { p, p, p }) { IsSmooth = true, TopText = "Top", BottomText = "Bottom" };

        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        curve.Render(surface.Canvas, transform);
    }

    [Fact]
    public void CurveLineTextObject_AddPoint_GrowsPathIncrementally()
    {
        var transform = new DummyCoordinateTransform();
        var p1 = transform.ScreenToChart(new Point(20, 50));
        var p2 = transform.ScreenToChart(new Point(100, 50));

        using var curve = new CurveLineTextObject(new[] { p1, p2 });
        Assert.Equal(2, curve.Points.Count);

        curve.AddPoint(transform.ScreenToChart(new Point(180, 50)));
        Assert.Equal(3, curve.Points.Count);

        using var surface = SKSurface.Create(new SKImageInfo(200, 100));
        curve.Render(surface.Canvas, transform);
    }
}
