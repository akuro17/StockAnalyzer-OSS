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
    // Cached, reused across all callers and frames. Chart rendering is single-threaded
    // (UI thread only), so mutating shared paint fields between sequential Draw() calls is safe.
    private static readonly SKPaint FillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private static readonly SKPaint StrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };

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
}
