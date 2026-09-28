using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders standard indicator lines (Single color or Up/Down colors).
/// </summary>
public class LineIndicatorRenderer
{
    private readonly SKPaint _paint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
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

        _paint.StrokeWidth = (float)setting.Thickness;
        
        SKColor mainColor = overrideColor ?? new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);

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

        if (setting.UseUpDownColors)
        {
            RenderWithUpDownColors(canvas, _paint, chartArea, minVal, range, values, setting, visibleCandles, offset, startIndex, candles, interval, transform, candleWidth, xOffset);
        }
        else
        {
            _paint.Color = mainColor;
            RenderSingleColor(canvas, _paint, chartArea, minVal, range, values, visibleCandles, offset, startIndex, candles, interval, transform, candleWidth, xOffset);
        }
    }

    private void RenderSingleColor(SKCanvas canvas, SKPaint paint, Rect chartArea, decimal minVal, decimal range,
        IReadOnlyList<decimal?> values, int totalCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData>? candles, TimeSpan interval, ICoordinateTransform? transform, float candleWidth, float xOffset)
    {
        using var path = new SKPath();
        bool isStarted = false;

        for (int i = 0; i < totalCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count) continue;

            decimal? val = values[valueIndex];
            if (!val.HasValue)
            {
                // Do not break the line for null values (e.g. ZigZag pivot gaps)
                continue;
            }

            float centerX = GetX(i, chartArea, candles, interval, transform, candleWidth, startIndex, xOffset);
            float y = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, val.Value)).Y ?? 0) + (double)chartArea.Top);

            if (!isStarted)
            {
                path.MoveTo(centerX, y);
                isStarted = true;
            }
            else
            {
                path.LineTo(centerX, y);
            }
        }

        canvas.DrawPath(path, paint);
    }

    private void RenderWithUpDownColors(SKCanvas canvas, SKPaint paint, Rect chartArea, decimal minVal, decimal range,
        IReadOnlyList<decimal?> values, CoreIndicatorSettings setting, int totalCandles, int offset, int startIndex,
        IReadOnlyList<CoreCandleData>? candles, TimeSpan interval, ICoordinateTransform? transform, float candleWidth, float xOffset)
    {
        var upColor = new SKColor(setting.UpColor.R, setting.UpColor.G, setting.UpColor.B, setting.UpColor.A);
        var downColor = new SKColor(setting.DownColor.R, setting.DownColor.G, setting.DownColor.B, setting.DownColor.A);

        float? prevX = null;
        float? prevY = null;
        decimal? prevVal = null;

        for (int i = 0; i < totalCandles; i++)
        {
            int valueIndex = i - offset;
            if (valueIndex < 0 || valueIndex >= values.Count)
            {
                prevX = null; prevY = null; prevVal = null;
                continue;
            }

            decimal? val = values[valueIndex];
            if (!val.HasValue)
            {
                continue;
            }

            float centerX = GetX(i, chartArea, candles, interval, transform, candleWidth, startIndex, xOffset);
            float y = (float)((transform?.ChartToScreen(new ChartPoint(DateTime.MinValue, val.Value)).Y ?? 0) + (double)chartArea.Top);

            if (prevX.HasValue && prevY.HasValue && prevVal.HasValue)
            {
                paint.Color = val.Value >= prevVal.Value ? upColor : downColor;
                canvas.DrawLine(prevX.Value, prevY.Value, centerX, y, paint);
            }

            prevX = centerX;
            prevY = y;
            prevVal = val;
        }
    }

    private float GetX(int index, Rect chartArea, IReadOnlyList<CoreCandleData>? candles, TimeSpan interval, ICoordinateTransform? transform, float candleWidth, int startIndex, float xOffset)
    {
        if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
        {
             return (float)transform.ChartToScreen(new ChartPoint(new DateTime(Math.Max(0, startIndex + index)), 0)).X + xOffset;
        }

        if (transform != null && candles != null)
        {
             var t = GetCandleTime(index, candles, interval);
             return (float)transform.ChartToScreen(new ChartPoint(t, 0)).X + xOffset;
        }
        else
        {
             return (float)chartArea.Left + index * candleWidth + xOffset;
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
