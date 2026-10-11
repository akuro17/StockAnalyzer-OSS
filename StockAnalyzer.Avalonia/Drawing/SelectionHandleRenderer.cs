using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Shared, ZeroAllocation-compliant renderer for the selection handle circles
/// ("anchor dots") drawn on <see cref="IChartObject"/> vertices when selected.
/// Consolidates the previously duplicated `if (IsSelected) { new SKPaint(); DrawCircle(...) }`
/// pattern found across the Drawing object implementations (SA_RENDERING_PERFORMANCE.md
/// Section 1: "Prohibit new SKPaint ... inside Render(). Reuse cached fields.").
/// Callers with multiple/variable-length points MUST call <see cref="Draw"/> once per point
/// in a loop rather than materializing a points array/list, to avoid a collection allocation
/// inside Render() (also prohibited by SA_RENDERING_PERFORMANCE.md).
/// </summary>
public static class SelectionHandleRenderer
{
    // Cached and reused across all callers and frames of a thread (still zero allocation after the first draw on a thread). The colour is assigned before
    // every draw, so the paint must not be shared between threads: two threads rendering at once (chart rendering is normally UI-thread only, but tests
    // render in parallel) would otherwise paint each other's colour.
    [ThreadStatic] private static SKPaint? _fillPaint;
    [ThreadStatic] private static SKPaint? _strokePaint;

    private static SKPaint FillPaint => _fillPaint ??= new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private static SKPaint StrokePaint => _strokePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };

    /// <summary>
    /// Draws a single filled selection handle circle at the given screen-space point.
    /// </summary>
    public static void Draw(SKCanvas canvas, global::Avalonia.Point screenPoint, SKColor? color = null, float radius = ChartConstants.DefaultHandleRadius)
    {
        FillPaint.Color = color ?? DrawingThemeContext.HandleColor;
        canvas.DrawCircle((float)screenPoint.X, (float)screenPoint.Y, radius, FillPaint);
    }

    /// <summary>
    /// Draws a stroked (outline-only) selection handle circle, used by tools that highlight
    /// a secondary/derived anchor point distinctly from the primary Fill handles (e.g. Pitchfork's
    /// Schiff anchor emphasis).
    /// </summary>
    public static void DrawOutline(SKCanvas canvas, global::Avalonia.Point screenPoint, SKColor? color = null, float radius = ChartConstants.DefaultHandleRadius, float strokeWidth = 2f)
    {
        StrokePaint.Color = color ?? DrawingThemeContext.HandleColor;
        StrokePaint.StrokeWidth = strokeWidth;
        canvas.DrawCircle((float)screenPoint.X, (float)screenPoint.Y, radius, StrokePaint);
    }

    [ThreadStatic] private static SKPaint? _aabbFramePaint;
    private static SKPaint AabbFramePaint => _aabbFramePaint ??= new SKPaint
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 4f, 4f }, 0f)
    };

    /// <summary>
    /// Draws an 8-point AABB deformation handle frame over the specified screen rectangle.
    /// Handles are placed at the 4 corners and 4 edge midpoints:
    /// 0=TopLeft, 1=TopCenter, 2=TopRight, 3=RightCenter,
    /// 4=BottomRight, 5=BottomCenter, 6=BottomLeft, 7=LeftCenter.
    /// </summary>
    public static void DrawAabbHandles(
        SKCanvas canvas,
        global::Avalonia.Rect screenRect,
        SKColor? handleColor = null,
        SKColor? anchorColor = null,
        SKColor? frameColor = null,
        int anchorHandleIndex = 0,
        float radius = ChartConstants.DefaultHandleRadius)
    {
        float left = (float)screenRect.Left;
        float right = (float)screenRect.Right;
        float top = (float)screenRect.Top;
        float bottom = (float)screenRect.Bottom;
        float midX = (left + right) * 0.5f;
        float midY = (top + bottom) * 0.5f;

        var framePaint = AabbFramePaint;
        framePaint.Color = frameColor ?? DrawingThemeContext.DefaultSkColor;
        framePaint.StrokeWidth = 1f;
        canvas.DrawRect(left, top, right - left, bottom - top, framePaint);

        SKColor hColor = handleColor ?? DrawingThemeContext.HandleColor;
        SKColor aColor = anchorColor ?? DrawingThemeContext.AnchorPointColor;

        Draw(canvas, new global::Avalonia.Point(left, top), anchorHandleIndex == 0 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(midX, top), anchorHandleIndex == 1 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(right, top), anchorHandleIndex == 2 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(right, midY), anchorHandleIndex == 3 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(right, bottom), anchorHandleIndex == 4 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(midX, bottom), anchorHandleIndex == 5 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(left, bottom), anchorHandleIndex == 6 ? aColor : hColor, radius);
        Draw(canvas, new global::Avalonia.Point(left, midY), anchorHandleIndex == 7 ? aColor : hColor, radius);
    }
}
