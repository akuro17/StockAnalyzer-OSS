using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders indicator values as a histogram (bar chart).
/// </summary>
public class HistogramRenderer
{
    private readonly SKPaint _paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    public void Render(SKCanvas canvas, Rect chartArea, 
        IReadOnlyList<decimal?> values, 
        CoreIndicatorSettings setting, 
        decimal minVal, decimal maxVal,
        int visibleCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData> candles, 
        TimeSpan interval, 
        ICoordinateTransform? transform,
        SKColor? overrideColor = null,
        float xOffset = 0f)
    {
        if (values == null || values.Count == 0) return;
        if (visibleCandles <= 0) return;

        var upColor = new SKColor(setting.UpColor.R, setting.UpColor.G, setting.UpColor.B, setting.UpColor.A);
        var downColor = new SKColor(setting.DownColor.R, setting.DownColor.G, setting.DownColor.B, setting.DownColor.A);
        var mainColor = overrideColor ?? new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);


        float zeroY = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, 0m)).Y ?? 0) + (double)chartArea.Top); 

        float candleWidth;
        if (transform != null && candles != null && candles.Count > 1)
        {
             double x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0)).X;
             double x1 = transform.ChartToScreen(new ChartPoint(candles[1].Timestamp, 0)).X;
             candleWidth = (float)(x1 - x0);
        }
        else if (transform != null && candles != null && candles.Count == 1)
        {
             double x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0)).X;
             double x1 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp + interval, 0)).X;
             candleWidth = (float)(x1 - x0);
        }
        else
        {
             candleWidth = (float)(chartArea.Width / visibleCandles);
        }
        
        float barWidth = candleWidth * 0.8f; 
        decimal range = maxVal - minVal;
        if (range == 0) range = 1;

        for (int i = 0; i < visibleCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count) continue;

            decimal? val = values[valueIndex];
            if (!val.HasValue) continue;

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
            
            float centerX = transform != null ? (x + xOffset) : x + candleWidth / 2f + xOffset;
            float y = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, val.Value)).Y ?? 0) + (double)chartArea.Top);

            // Determine color
            if (setting.UseUpDownColors || setting.TypeEnum == IndicatorType.MACD)
            {
                _paint.Color = val.Value >= 0 ? upColor : downColor;
            }
            else
            {
                _paint.Color = mainColor;
            }

            // Draw Rect
            float top = Math.Min(y, zeroY);
            float bottom = Math.Max(y, zeroY);
            float left = centerX - barWidth / 2f;
            
            var rect = new SKRect(left, top, left + barWidth, bottom);
            canvas.DrawRect(rect, _paint);
        }
    }

    private DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue;
        if (index < candles.Count) return candles[index].Timestamp;
        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }
}
