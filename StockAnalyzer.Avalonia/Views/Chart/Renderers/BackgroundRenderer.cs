using Avalonia;
using SkiaSharp;
using Avalonia.Skia; // Added for ToSKRect
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the background of the chart.
/// </summary>
public sealed class BackgroundRenderer : System.IDisposable
{
    private readonly SKPaint _backgroundPaint;

    public BackgroundRenderer()
    {
        _backgroundPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill
        };
    }

    public void Render(SKCanvas canvas, ChartLayoutContext layout, IChartRenderConfig config)
    {
        _backgroundPaint.Color = config.ThemeManager.CurrentTheme.ChartBackground.ToSkColor();
        
        // Fill the entire control area (0, 0 to Width, Height) with the background color
        // This ensures margins are also covered by the chart background theme.
        canvas.DrawRect(new SKRect(0, 0, (float)layout.TotalBounds.Width, (float)layout.TotalBounds.Height), _backgroundPaint);
    }

    public void Dispose()
    {
        _backgroundPaint.Dispose();
    }
}
