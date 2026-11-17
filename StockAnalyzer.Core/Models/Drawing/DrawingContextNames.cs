namespace StockAnalyzer.Core.Models.Drawing;

/// <summary>
/// Names of the drawing contexts as a drawing file stores them (the name of the application's <c>ChartDrawingContextType</c> member). The one definition for the read-only
/// readers of the saved drawings (sa_get_chart_drawings, the saved drawings of sa_render_chart_image).
/// </summary>
public static class DrawingContextNames
{
    /// <summary>The context of the candle, bar and Heikin-Ashi charts.</summary>
    public const string Standard = "Standard";
}
