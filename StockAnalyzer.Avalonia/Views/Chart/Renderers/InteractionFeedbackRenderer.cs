using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders interaction feedback elements like drawing previews.
/// </summary>
public sealed class InteractionFeedbackRenderer
{
    /// <param name="panelIndex">
    /// The panel this pass renders (-1 = main chart, 0..K-1 = sub-window panels). The in-progress
    /// preview is drawn only when it belongs to this panel, so a sub-panel shape is never also
    /// painted on the main chart (and vice versa).
    /// </param>
    public void Render(
        SKCanvas canvas,
        global::Avalonia.Point? snapPoint,
        IChartObject? currentDrawingObject,
        ICoordinateTransform transform,
        int panelIndex = -1)
    {
        // 1. Render Current Drawing Object (Preview) — only for the panel this pass owns.
        if (currentDrawingObject != null && currentDrawingObject.PanelIndex == panelIndex)
        {
            currentDrawingObject.Render(canvas, transform);
        }

        // Magnet snap marker and Smart Guide line visual indicators are suppressed per user request:
        // the underlying object-to-object snap and magnet-snap tracking are active,
        // but visual line/ring indicators are not drawn on the screen.
    }
}
