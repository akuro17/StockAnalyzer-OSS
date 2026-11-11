using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

/// <summary>
/// The paints and paths one projection-drawing renderer reuses between frames instead of creating them in <c>Render()</c>
/// (SA_RENDERING_PERFORMANCE: no <c>new SKPaint</c> / <c>new SKPath</c> inside <c>Render()</c>). A renderer keeps one instance
/// per thread in a <c>[ThreadStatic]</c> field, so every drawing object of that type shares it without owning native handles
/// (the pattern of <c>SelectionHandleRenderer</c>). Each field has one role; the renderer sets the properties that differ per
/// call (color, stroke width) right before it draws with it, and calls <c>Reset()</c> on a path before building it.
/// </summary>
internal sealed class ProjectionRenderScratch
{
    /// <summary>Fills (range background, confidence band); color set per use.</summary>
    public SKPaint FillPaint { get; } = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

    /// <summary>Vertical range lines; color and stroke width set per use.</summary>
    public SKPaint RangeLinePaint { get; } = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = false };

    /// <summary>Dashed confidence-band boundary; color and stroke width set per use.</summary>
    public SKPaint BoundaryPaint { get; }

    /// <summary>Dashed projected path; color and stroke width set per use.</summary>
    public SKPaint PathPaint { get; }

    /// <summary>Matched-pattern highlight fill; its color is set per use.</summary>
    public SKPaint HighlightFillPaint { get; } = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };

    /// <summary>Dashed matched-pattern highlight border; color and stroke width set per use.</summary>
    public SKPaint HighlightBorderPaint { get; }

    public SKPath BandPath { get; } = new SKPath();
    public SKPath UpperPath { get; } = new SKPath();
    public SKPath LowerPath { get; } = new SKPath();
    public SKPath ProjectedPath { get; } = new SKPath();

    /// <param name="projectedPathDashLength">Dash length (= gap) of the projected path, in pixels.</param>
    public ProjectionRenderScratch(float projectedPathDashLength)
    {
        BoundaryPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = Dash(ChartTheme.ProjectionBandBoundaryDashLength)
        };
        PathPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = Dash(projectedPathDashLength)
        };
        HighlightBorderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = Dash(ChartTheme.ProjectionMatchBorderDashLength)
        };
    }

    private static SKPathEffect Dash(float length) => SKPathEffect.CreateDash(new[] { length, length }, 0f);
}
