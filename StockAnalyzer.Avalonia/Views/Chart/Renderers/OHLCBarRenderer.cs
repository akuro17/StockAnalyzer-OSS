using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders OHLC Bar Chart (Western Bar Chart).
/// ...
/// </summary>
public sealed class OHLCBarRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _bullishPaint;
    private readonly SKPaint _bearishPaint;

    public OHLCBarRenderer()
    {
        _bullishPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        _bearishPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IOhlcBarRenderConfig)baseConfig;
        if (snapshot.Candles.Count == 0) return;
        if (config?.Transform is not ICoordinateTransform transform) return;

        var theme = config.ThemeManager.CurrentTheme;
        _bullishPaint.Color = config.OhlcBullishColor.ToSkColor();
        _bearishPaint.Color = config.OhlcBearishColor.ToSkColor();

        // Calculate Interval for Time-Based Width
        TimeSpan interval = TimeSpan.FromDays(1);
        if (snapshot.Candles.Count > 1)
        {
            interval = snapshot.Candles[1].Timestamp - snapshot.Candles[0].Timestamp;
        }

        // Determine if we are rendering in Index mode
        bool isIndexMode = config.ChartType.IsIndexBased();

        // Jitter-free: width from time interval via transform
        double x0;
        double x1;
        if (isIndexMode)
        {
             x0 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex), 0)).X;
             x1 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex + 1), 0)).X;
        }
        else
        {
             x0 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
             x1 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp + interval, 0)).X;
        }
        
        float candleWidth = (float)(x1 - x0);
        float tickLength = Math.Max(2f, candleWidth * 0.4f); 

        // Pixel Snap constants (High-DPI aware)
        float scaling = (float)config.RenderScaling;
        float pixelSize = 1.0f / scaling;
        float halfPixel = 0.5f * pixelSize;

        // M4 / LOD Aggregation state
        float currentPixelX = float.MinValue;
        float aggOpen = 0f;
        float aggHigh = float.MinValue;
        float aggLow = float.MaxValue;
        float aggClose = 0f;
        bool aggIsBullish = true;
        int aggCount = 0;

        for (int i = 0; i < snapshot.Candles.Count; i++)
        {
            var candle = snapshot.Candles[i];
            float x;
            if (isIndexMode)
            {
                int absoluteIndex = snapshot.StartIndex + i;
                x = (float)transform.ChartToScreen(new ChartPoint(new DateTime(absoluteIndex), 0)).X + (float)chartArea.Left;
            }
            else
            {
                x = (float)transform.ChartToScreen(new ChartPoint(candle.Timestamp, 0)).X + (float)chartArea.Left;
            }
            
            // Pixel Snap ensuring grouping by physical pixel (High-DPI aware)
            float snappedCenterX = (float)Math.Floor(x * scaling) / scaling + halfPixel;

            if (snappedCenterX != currentPixelX)
            {
                if (aggCount > 0)
                {
                    float yOpen = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggOpen)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
                    float yClose = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggClose)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
                    float yHigh = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggHigh)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
                    float yLow = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggLow)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;

                    var paint = aggIsBullish ? _bullishPaint : _bearishPaint;

                    float lineBottom = Math.Max(yHigh + 1f, yLow);
                    canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, paint);

                    // Draw ticks even at small widths to ensure OHLC structure visibility
                    // Using tLen for pixel-perfect alignment
                    float tLen = Math.Max(1f, (float)Math.Floor((candleWidth - 1.1f) / 2f));
                    canvas.DrawLine(currentPixelX - tLen, yOpen, currentPixelX, yOpen, paint);
                    canvas.DrawLine(currentPixelX, yClose, currentPixelX + tLen, yClose, paint);
                }

                currentPixelX = snappedCenterX;
                aggOpen = (float)candle.Open;
                aggHigh = (float)candle.High;
                aggLow = (float)candle.Low;
                aggClose = (float)candle.Close;
                aggIsBullish = candle.IsBullish;
                aggCount = 1;
            }
            else
            {
                if ((float)candle.High > aggHigh) aggHigh = (float)candle.High;
                if ((float)candle.Low < aggLow) aggLow = (float)candle.Low;
                aggClose = (float)candle.Close;
                aggIsBullish = aggClose >= aggOpen;
                aggCount++;
            }
        }
        
        // Flush the final batch
        if (aggCount > 0)
        {
            float yOpen = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggOpen)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
            float yClose = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggClose)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
            float yHigh = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggHigh)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;
            float yLow = (float)Math.Floor(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggLow)).Y * scaling + chartArea.Top * scaling) / scaling + halfPixel;

            var paint = aggIsBullish ? _bullishPaint : _bearishPaint;

            float lineBottom = Math.Max(yHigh + 1f, yLow);
            canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, paint);

            float tLen = Math.Max(1f, (float)Math.Floor((candleWidth - 1.1f) / 2f));
            canvas.DrawLine(currentPixelX - tLen, yOpen, currentPixelX, yOpen, paint);
            canvas.DrawLine(currentPixelX, yClose, currentPixelX + tLen, yClose, paint);
        }
    }

    public void Dispose()
    {
        _bullishPaint.Dispose();
        _bearishPaint.Dispose();
    }
}

