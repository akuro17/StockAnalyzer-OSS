using System;
using Avalonia;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a ruler tool for measuring price, time, and bars.
/// Activated via Shift + Drag.
/// </summary>
public sealed class RulerRenderer : IDisposable
{
    private readonly SKPaint _linePaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _areaPaint;
    private readonly SKFont _font;

    public bool IsActive { get; set; }
    public global::Avalonia.Point StartPoint { get; set; }
    public global::Avalonia.Point EndPoint { get; set; }

    public RulerRenderer()
    {
        _linePaint = new SKPaint
        {
            StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            IsAntialias = true
        };

        _bgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        
        _areaPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true
        };

        _font = new SKFont(SKTypeface.Default, 12);
    }

    public void Render(SKCanvas canvas, global::Avalonia.Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if (!IsActive) return;
        if (snapshot.Candles.Count == 0) return;

        var theme = config.ThemeManager.CurrentTheme;
        _linePaint.Color = theme.RulerStroke.ToSkColor();
        _bgPaint.Color = theme.RulerBackground.ToSkColor();
        _areaPaint.Color = theme.RulerArea.ToSkColor();
        _textPaint.Color = theme.RulerText.ToSkColor();

        // Draw semi-transparent background rectangle for the area
        var areaRect = new SKRect(
            Math.Min((float)StartPoint.X, (float)EndPoint.X),
            Math.Min((float)StartPoint.Y, (float)EndPoint.Y),
            Math.Max((float)StartPoint.X, (float)EndPoint.X),
            Math.Max((float)StartPoint.Y, (float)EndPoint.Y));
        
        canvas.DrawRect(areaRect, _areaPaint);

        // Draw Measurement Line
        canvas.DrawLine((float)StartPoint.X, (float)StartPoint.Y, (float)EndPoint.X, (float)EndPoint.Y, _linePaint);

        // Calculate Data
        var startPrice = YToPrice((float)StartPoint.Y, chartArea, snapshot);
        var endPrice = YToPrice((float)EndPoint.Y, chartArea, snapshot);
        var priceDiff = endPrice - startPrice;
        var pricePercent = startPrice != 0 ? (priceDiff / startPrice) * 100 : 0;

        // Note: XToIndex is still needed for ruler logic but we have viewport context for ScreenToTime.
        // For Phase 1, we will just keep XToIndex if we didn't remove it from ChartHelpers OR replace it.
        // Wait, I DID remove it from ChartHelpers. I must replace it here.
        // I will use transform.ScreenToChart if available.
        
        // TEMPORARY: Re-adding the logic locally or using the transform.
        var startIndex = (int)Math.Floor((StartPoint.X - chartArea.X) / (chartArea.Width / snapshot.Candles.Count));
        var endIndex = (int)Math.Floor((EndPoint.X - chartArea.X) / (chartArea.Width / snapshot.Candles.Count));

        // Clamp indices
        var cCount = snapshot.Candles.Count;
        var validStart = Math.Clamp(startIndex, 0, cCount - 1);
        var validEnd = Math.Clamp(endIndex, 0, cCount - 1);

        var bars = endIndex - startIndex; // Raw difference in bars (visual)
        
        var startTime = snapshot.Candles[validStart].Timestamp;
        var endTime = snapshot.Candles[validEnd].Timestamp;
        var timeSpan = endTime - startTime;

        // Format Text
        var line1 = $"{priceDiff:F2} ({pricePercent:F2}%)";
        var line2 = $"{Math.Abs(bars)} bars, {(int)timeSpan.TotalDays}d";

        // Draw Label at EndPoint
        float x = (float)EndPoint.X + 10;
        float y = (float)EndPoint.Y;

        // Measure Text
        var width1 = _textPaint.MeasureText(line1);
        var width2 = _textPaint.MeasureText(line2);
        var maxWidth = Math.Max(width1, width2);
        var lineHeight = _font.Size + 5;
        var padding = 5;
        var boxHeight = lineHeight * 2 + padding * 2;
        var boxWidth = maxWidth + padding * 2;

        // Simple boundary check
        if (x + boxWidth > chartArea.Right) x = (float)EndPoint.X - boxWidth - 10;
        if (y + boxHeight > chartArea.Bottom) y = (float)EndPoint.Y - boxHeight - 10;

        var rect = new SKRect(x, y, x + boxWidth, y + boxHeight);
        canvas.DrawRoundRect(rect, 4, 4, _bgPaint); // _bgPaint is label background
        
        canvas.DrawText(line1, x + padding, y + padding + _font.Size, _font, _textPaint);
        canvas.DrawText(line2, x + padding, y + padding + _font.Size * 2 + 5, _font, _textPaint);
    }

    private static decimal YToPrice(float y, global::Avalonia.Rect chartArea, ChartDataSnapshot snapshot)
    {
        if (snapshot.PriceRange == 0) return snapshot.MinPrice;
        float chartHeight = (float)chartArea.Height;
        float relativeY = y - (float)chartArea.Y;
        decimal normalizedY = 1m - (decimal)relativeY / (decimal)chartHeight;
        return snapshot.MinPrice + normalizedY * snapshot.PriceRange;
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _bgPaint.Dispose();
        _textPaint.Dispose();
        _areaPaint.Dispose();
        _font.Dispose();
    }
}
