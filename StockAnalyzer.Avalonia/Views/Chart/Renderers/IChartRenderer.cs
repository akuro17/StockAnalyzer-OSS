using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Unified interface for all chart renderers.
/// Allows dynamic dispatch and easier extensibility.
/// </summary>
public interface IChartRenderer
{
    /// <summary>
    /// Renders the chart content onto the canvas.
    /// </summary>
    /// <param name="canvas">The SkiaSharp canvas to draw on.</param>
    /// <param name="chartArea">The specific area allocated for the chart (excluding margins/volume).</param>
    /// <param name="snapshot">The data snapshot containing candles and state.</param>
    /// <param name="context">Configuration context containing user settings and colors.</param>
    void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config);
}
