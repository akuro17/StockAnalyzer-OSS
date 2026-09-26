using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders indicators as a Step Line (Horizontal then Vertical).
/// Used for Renko, PointAndFigure, Kagi, etc.
/// </summary>
public class StepLineRenderer
{
    private readonly SKPaint _paint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    public void Render(SKCanvas canvas, Rect chartArea, 
        IReadOnlyList<decimal?> values, 
        CoreIndicatorSettings setting, 
        decimal minVal, decimal maxVal,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles, 
        TimeSpan interval, 
        ICoordinateTransform? transform)
    {
        if (values == null || values.Count == 0) return;

        _paint.Color = new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);
        _paint.StrokeWidth = (float)setting.Thickness;

        using var path = new SKPath();
        bool isStarted = false;
        
        float candleWidth;
        if (transform != null && candles != null && candles.Count > 0)
        {
             double x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0)).X;
             double x1 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp + interval, 0)).X;
             candleWidth = (float)(x1 - x0);
        }
        else
        {
             candleWidth = (float)(chartArea.Width / visibleCandles);
        }

        decimal range = maxVal - minVal;
        if (range == 0) range = 1;

        float? prevX = null;
        float? prevY = null;

        for (int i = 0; i < visibleCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count) continue;

            decimal? val = values[valueIndex];
            if (!val.HasValue) 
            {
                isStarted = false;
                prevX = null;
                prevY = null;
                continue;
            }

            // X Coordinate
            float x;
            if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
            {
                 x = (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + i)), 0)).X;
            }
            else if (transform != null && candles != null)
            {
                 var t = GetCandleTime(i, candles, interval);
                 x = (float)transform.ChartToScreen(new ChartPoint(t, 0)).X;
            }
            else
            {
                 x = (float)chartArea.Left + i * candleWidth;
            }
            
            float centerX = x + candleWidth / 2f;
            float y = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, val.Value)).Y ?? 0) + (double)chartArea.Top);

            if (!isStarted)
            {
                path.MoveTo(centerX, y);
                isStarted = true;
            }
            else
            {
                if (prevX.HasValue && prevY.HasValue)
                {
                    // Step Line Logic:
                    // Draw Horizontal to current X, then Vertical to current Y
                    // Note: WPF logic was:
                    // points.Add(new Point(x, lastPoint.Y)); // Horizontal
                    // points.Add(new Point(x, y));           // Vertical
                    
                    // So we draw from (prevX, prevY) to (centerX, prevY)
                    path.LineTo(centerX, prevY.Value);
                    // Then to (centerX, y)
                    path.LineTo(centerX, y);
                }
            }

            prevX = centerX;
            prevY = y;
        }

        canvas.DrawPath(path, _paint);
    }

    private DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue;
        
        if (index < candles.Count)
        {
            return candles[index].Timestamp;
        }
        
        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }
}
