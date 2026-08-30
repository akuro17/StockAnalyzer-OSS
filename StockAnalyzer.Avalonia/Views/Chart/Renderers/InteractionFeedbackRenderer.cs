using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders interaction feedback elements like drawing previews.
/// </summary>
public sealed class InteractionFeedbackRenderer
{
    public void Render(
        SKCanvas canvas, 
        global::Avalonia.Point? snapPoint, 
        IChartObject? currentDrawingObject, 
        ICoordinateTransform transform)
    {
        // 1. Render Current Drawing Object (Preview)
        if (currentDrawingObject != null)
        {
            currentDrawingObject.Render(canvas, transform);
        }

        // Magnet snap marker and Smart Guide line visual indicators are suppressed per user request:
        // the underlying object-to-object snap and magnet-snap tracking are active,
        // but visual line/ring indicators are not drawn on the screen.
    }
}
