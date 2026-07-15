using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders visual markers (triangles) at golden cross and dead cross points
/// between oscillator line series.
/// </summary>
public sealed class CrossMarkerRenderer : IDisposable
{
    private readonly SKPaint _gcPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _dcPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _markerPath = new SKPath();

    public void Dispose()
    {
        _gcPaint?.Dispose();
        _dcPaint?.Dispose();
        _markerPath?.Dispose();
    }

    /// <summary>
    /// Half-size of the triangle marker in pixels.
    /// </summary>
    private const float MarkerHalfSize = 5f;

    /// <summary>
    /// Renders cross markers for detected golden/dead cross signals.
    /// </summary>
    /// <param name="canvas">SkiaSharp canvas to draw on.</param>
    /// <param name="targetRect">The panel/chart area rectangle.</param>
    /// <param name="signals">Detected cross signals from DivergenceCrossDetector.</param>
    /// <param name="crossValues">Values at the cross point (average of short/long) for Y positioning.</param>
    /// <param name="minVal">Minimum value for vertical scaling.</param>
    /// <param name="maxVal">Maximum value for vertical scaling.</param>
    /// <param name="visibleCandles">Number of visible candles.</param>
    /// <param name="offset">Data offset for alignment.</param>
    /// <param name="candles">Candle data for time-based coordinate calculation.</param>
    /// <param name="interval">Time interval between candles.</param>
    /// <param name="transform">Coordinate transform for time-to-screen mapping.</param>
    public void Render(
        SKCanvas canvas,
        Rect targetRect,
        IReadOnlyList<CrossSignal> signals,
        IReadOnlyList<decimal?> shortSeriesValues,
        IReadOnlyList<decimal?> longSeriesValues,
        decimal minVal, decimal maxVal,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        ICoordinateTransform? transform,
        IChartRenderConfig config)
    {
        if (signals == null || signals.Count == 0) return;

        decimal range = maxVal - minVal;
        if (range == 0) range = 1m;

        // Base width for fallback if transform is unavailable
        float candleWidth = (float)(targetRect.Width / Math.Max(1, candles.Count));

        // Pre-configure paints from theme (ZeroAllocation: updating properties of existing instances)
        var theme = config.ThemeManager.CurrentTheme;
        _gcPaint.Color = theme.CrossMarkerGolden.ToSkColor();
        _dcPaint.Color = theme.CrossMarkerDead.ToSkColor();

        for (int i = 0; i < signals.Count; i++)
        {
            var signal = signals[i];

            // Calculate Y manually from the panel's min/max range to support sub-windows
            decimal crossValue = (signal.ShortValue + signal.LongValue) / 2m;
            decimal rangeVal = maxVal - minVal;
            if (rangeVal <= 0) rangeVal = 1m;
            decimal normalizedY = 1m - (crossValue - minVal) / rangeVal;

            // Get X from the transform using absolute index (CrossIndex)
            float x = GetX(signal.CrossIndex, targetRect, candles, interval, transform, candleWidth);
            
            // Skip rendering if the point is outside the current viewport
            if (x < -MarkerHalfSize || x > (float)targetRect.Width + MarkerHalfSize) continue;

            // Center Alignment and Pixel Snap (Matches CandleStickRenderer.cs)
            float centerX = (float)Math.Floor(x) + 0.5f;
            float y = (float)(targetRect.Height * (double)normalizedY);

            if (signal.Type == SignalType.GoldenCross)
            {
                DrawUpTriangle(canvas, centerX, y, _gcPaint, _markerPath);
            }
            else if (signal.Type == SignalType.DeadCross)
            {
                DrawDownTriangle(canvas, centerX, y, _dcPaint, _markerPath);
            }
        }
    }

    /// <summary>
    /// Draws an upward-pointing triangle (Golden Cross marker).
    /// </summary>
    private static void DrawUpTriangle(SKCanvas canvas, float cx, float cy, SKPaint paint, SKPath path)
    {
        path.Reset();
        path.MoveTo(cx, cy - MarkerHalfSize);                           // Top
        path.LineTo(cx - MarkerHalfSize, cy + MarkerHalfSize);          // Bottom-left
        path.LineTo(cx + MarkerHalfSize, cy + MarkerHalfSize);          // Bottom-right
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Draws a downward-pointing triangle (Dead Cross marker).
    /// </summary>
    private static void DrawDownTriangle(SKCanvas canvas, float cx, float cy, SKPaint paint, SKPath path)
    {
        path.Reset();
        path.MoveTo(cx, cy + MarkerHalfSize);                           // Bottom
        path.LineTo(cx - MarkerHalfSize, cy - MarkerHalfSize);          // Top-left
        path.LineTo(cx + MarkerHalfSize, cy - MarkerHalfSize);          // Top-right
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static float GetX(
        int index, Rect targetRect,
        IReadOnlyList<CoreCandleData>? candles, TimeSpan interval,
        ICoordinateTransform? transform, float candleWidth)
    {
        // Aligned with LineIndicatorRenderer.GetX for pixel-perfect match
        if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
        {
            return (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, index)), 0)).X;
        }

        if (transform != null && candles != null)
        {
            var t = GetCandleTime(index, candles, interval);
            return (float)transform.ChartToScreen(new ChartPoint(t, 0)).X;
        }

        return (float)targetRect.Left + index * candleWidth;
    }

    private static DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue;
        if (index < candles.Count) return candles[index].Timestamp;
        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }
}
