using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders interaction feedback elements like magnet snap markers and drawing previews.
/// </summary>
public sealed class InteractionFeedbackRenderer : System.IDisposable
{
    private readonly SKPaint _snapPaint;
    
    public InteractionFeedbackRenderer()
    {
        _snapPaint = new SKPaint
        {
             Color = SKColors.Orange,
             Style = SKPaintStyle.Stroke,
             StrokeWidth = 2,
             IsAntialias = true
        };
    }

    public void Render(SKCanvas canvas, global::Avalonia.Point? snapPoint, IChartObject? currentDrawingObject, ICoordinateTransform transform)
    {
        // Render Current Drawing Object (Preview)
        if (currentDrawingObject != null)
        {
            currentDrawingObject.Render(canvas, transform);
        }

        // Draw Magnet Snap Marker
        if (snapPoint.HasValue)
        {
             canvas.DrawCircle((float)snapPoint.Value.X, (float)snapPoint.Value.Y, 5, _snapPaint);
        }
    }

    public void Dispose()
    {
        _snapPaint.Dispose();
    }
}
