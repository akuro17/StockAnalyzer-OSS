using System;
using Avalonia;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders semi-transparent highlight bands on the main chart area
/// to indicate the DTW Oscillator comparison windows.
/// When the mouse hovers over a DTW sub-panel, two bands are drawn:
///   - "Current" window (blue): the segment being analyzed
///   - "Lagged" window (orange): the past segment it is compared against
/// </summary>
public sealed class DtwHighlightRenderer : IDisposable
{
    private readonly SKPaint _currentWindowPaint;
    private readonly SKPaint _laggedWindowPaint;

    public DtwHighlightRenderer()
    {
        _currentWindowPaint = new SKPaint
        {
            Color = new SKColor(30, 144, 255, 40), // DodgerBlue with low alpha
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };

        _laggedWindowPaint = new SKPaint
        {
            Color = new SKColor(255, 165, 0, 40), // Orange with low alpha
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };
    }

    /// <summary>
    /// Renders the DTW comparison window highlights on the main chart area.
    /// </summary>
    /// <param name="canvas">The SkiaSharp canvas to draw on.</param>
    /// <param name="chartArea">The main chart drawing area (already translated by chartArea.Left).</param>
    /// <param name="candleCount">Total number of visible candles.</param>
    /// <param name="hoverIndex">The candle index the mouse is currently over.</param>
    /// <param name="period">The DTW period parameter (window size).</param>
    /// <param name="lag">The DTW lag parameter (how far back to compare).</param>
    public void Render(
        SKCanvas canvas,
        Rect chartArea,
        int candleCount,
        int hoverIndex,
        int period,
        int lag)
    {
        if (candleCount <= 0 || period <= 0) return;

        // Current window: [hoverIndex - period, hoverIndex]
        int currentStart = Math.Max(0, hoverIndex - period);
        int currentEnd = Math.Min(candleCount - 1, hoverIndex);

        // Lagged window: [hoverIndex - period - lag, hoverIndex - lag]
        int laggedStart = Math.Max(0, hoverIndex - period - lag);
        int laggedEnd = Math.Max(0, hoverIndex - lag);

        // Convert index ranges to screen X coordinates
        // chartArea is in the translated coordinate space (Left = 0 after Translate)
        float candleWidth = (float)chartArea.Width / candleCount;
        float chartTop = (float)chartArea.Top;
        float chartBottom = (float)chartArea.Bottom;

        // Draw lagged window first (behind current)
        if (laggedEnd > laggedStart)
        {
            float lx1 = laggedStart * candleWidth;
            float lx2 = (laggedEnd + 1) * candleWidth;
            canvas.DrawRect(lx1, chartTop, lx2 - lx1, chartBottom - chartTop, _laggedWindowPaint);
        }

        // Draw current window
        if (currentEnd > currentStart)
        {
            float cx1 = currentStart * candleWidth;
            float cx2 = (currentEnd + 1) * candleWidth;
            canvas.DrawRect(cx1, chartTop, cx2 - cx1, chartBottom - chartTop, _currentWindowPaint);
        }
    }

    public void Dispose()
    {
        _currentWindowPaint.Dispose();
        _laggedWindowPaint.Dispose();
    }
}
