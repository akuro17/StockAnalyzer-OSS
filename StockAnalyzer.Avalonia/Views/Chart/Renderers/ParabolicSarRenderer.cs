using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Parabolic SAR as dots.
/// </summary>
public class ParabolicSarRenderer
{
    private readonly SKPaint _paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    public void Render(SKCanvas canvas, Rect chartArea, 
        IReadOnlyList<decimal?> values, 
        IReadOnlyList<decimal?> trendStates,
        CoreIndicatorSettings setting, 
        decimal minVal, decimal maxVal,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles, 
        TimeSpan interval, 
        ICoordinateTransform? transform)
    {
        if (values == null || values.Count == 0) return;

        var upColor = new SKColor(setting.UpColor.R, setting.UpColor.G, setting.UpColor.B, setting.UpColor.A);
        var downColor = new SKColor(setting.DownColor.R, setting.DownColor.G, setting.DownColor.B, setting.DownColor.A);
        var mainColor = new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);

        
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

        // Radius: Thickness * 1.5 ensures dots are visible but not huge.
        // WPF used Thickness * 3 for Diameter, so Radius = 1.5 * Thickness.
        float radius = (float)(setting.Thickness * 1.5);
        if (radius < 2) radius = 2; // Minimum size

        for (int i = 0; i < visibleCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count) continue;

            decimal? val = values[valueIndex];
            if (!val.HasValue) continue;

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

            // Determine Trend Color
            // Check trendStates if available
            if (setting.UseUpDownColors && trendStates != null && valueIndex < trendStates.Count && trendStates[valueIndex].HasValue)
            {
                // CoreParabolicSarIndicator returns 1 for Up, 0 for Down
                bool isUp = trendStates[valueIndex]!.Value > 0.5m;
                _paint.Color = isUp ? upColor : downColor;
            }
            else
            {
                _paint.Color = mainColor;
            }

            canvas.DrawCircle(centerX, y, radius, _paint);
        }
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
