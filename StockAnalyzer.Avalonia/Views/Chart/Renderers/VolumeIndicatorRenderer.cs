using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders standard volume based on candle open/close.
/// </summary>
public class VolumeIndicatorRenderer
{
    private readonly SKPaint _paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
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

        var upColor = new SKColor(setting.UpColor.R, setting.UpColor.G, setting.UpColor.B, setting.UpColor.A);
        var downColor = new SKColor(setting.DownColor.R, setting.DownColor.G, setting.DownColor.B, setting.DownColor.A);

        var mainColor = new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);

        // Zero Y is essentially the bottom for volume, but we use standard conversion
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

            float centerX;
            if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
            {
                 centerX = (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + i)), 0)).X;
            }
            else if (transform != null && candles != null)
            {
                 var t = GetCandleTime(i, candles, interval);
                 centerX = (float)transform.ChartToScreen(new ChartPoint(t, 0)).X;
            }
            else
            {
                 centerX = (float)chartArea.Left + i * candleWidth + candleWidth / 2f;
            }
            
            float y = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, val.Value)).Y ?? 0) + (double)chartArea.Top);

            // Determine color from candle data if available
            if (setting.UseUpDownColors && candles != null && valueIndex < candles.Count)
            {
                var candle = candles[valueIndex];
                _paint.Color = candle.IsBullish ? upColor : downColor;
            }
            else
            {
                _paint.Color = mainColor;
            }

            // Draw Rect
            float top = Math.Min(y, zeroY); // Volume bars usually go from 0 up to value
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
