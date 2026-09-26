using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders the candlestick series on the chart.
/// </summary>
public sealed class CandleStickRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _bullishPaint;
    private readonly SKPaint _bearishPaint;
    private readonly SKPaint _neutralPaint;
    private readonly SKPaint _wickPaint;

    public CandleStickRenderer()
    {
        _bullishPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _bearishPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _neutralPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _wickPaint = new SKPaint { StrokeWidth = 1.0f, Style = SKPaintStyle.Stroke, IsAntialias = false };
    }



    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if (snapshot.Candles.Count == 0) return;
        if (config?.Transform is not ICoordinateTransform transform) return;

        var theme = config.ThemeManager.CurrentTheme;
        
        if (config.ChartType == StockAnalyzer.Core.Models.ChartType.HeikinAshi)
        {
            var haConfig = (IHeikinAshiRenderConfig)config;
            _bullishPaint.Color = haConfig.HeikinBullishColor.ToSkColor();
            _bearishPaint.Color = haConfig.HeikinBearishColor.ToSkColor();
        }
        else
        {
            var candleConfig = (ICandlestickRenderConfig)config;
            _bullishPaint.Color = candleConfig.BullishColor.ToSkColor();
            _bearishPaint.Color = candleConfig.BearishColor.ToSkColor();
            _neutralPaint.Color = candleConfig.NeutralColor.ToSkColor();
            
            // For standard candles, wick color follows axis text or border from theme to avoid hardcoding
            _wickPaint.Color = theme.AxisText.ToSkColor();
        }
        
        _wickPaint.StrokeWidth = 1.0f;

        // Calculate Interval for Time-Based Width
        TimeSpan interval = TimeSpan.FromDays(1);
        if (snapshot.Candles.Count > 1)
        {
            interval = snapshot.Candles[1].Timestamp - snapshot.Candles[0].Timestamp;
        }

        // Determine if we are rendering in Index mode
        bool isIndexMode = config.ChartType.IsIndexBased();

        // Jitter-free: width from exact distance to next available candle via transform
        double x0;
        double x1;
        if (isIndexMode)
        {
             x0 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex), 0)).X;
             x1 = transform.ChartToScreen(new ChartPoint(new DateTime(snapshot.StartIndex + 1), 0)).X;
        }
        else
        {
             if (snapshot.Candles.Count > 1)
             {
                  x0 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
                  x1 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[1].Timestamp, 0)).X;
             }
             else
             {
                  x0 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp, 0)).X;
                  x1 = transform.ChartToScreen(new ChartPoint(snapshot.Candles[0].Timestamp + interval, 0)).X;
             }
        }
        
        float candleWidth = (float)(x1 - x0);
        float bodyWidth = Math.Max(1f, candleWidth * 0.8f);

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
            
            // Center Alignment and Pixel Snap (Math.Floor ensures grouping by physical pixel)
            float snappedCenterX = (float)Math.Floor(x) + 0.5f;

            if (snappedCenterX != currentPixelX)
            {
                if (aggCount > 0)
                {
                    float yOpen = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggOpen)).Y + chartArea.Top);
                    float yClose = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggClose)).Y + chartArea.Top);
                    float yHigh = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggHigh)).Y + chartArea.Top);
                    float yLow = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggLow)).Y + chartArea.Top);

                    var bodyPaint = aggOpen < aggClose ? _bullishPaint : (aggOpen > aggClose ? _bearishPaint : _neutralPaint);

                    int maxBw = (int)Math.Floor(candleWidth - 1f);
                    if (maxBw % 2 == 0) maxBw -= 1;

                    if (maxBw >= 1)
                    {
                        // Full Candlestick (Level 0)
                        float wickBottom = Math.Max(yHigh + 1f, yLow);
                        
                        // Heikin Ashi wicks should match body color for continuity
                        if (config.ChartType == StockAnalyzer.Core.Models.ChartType.HeikinAshi)
                        {
                            _wickPaint.Color = bodyPaint.Color;
                        }

                        canvas.DrawLine(currentPixelX, yHigh, currentPixelX, wickBottom, _wickPaint);
                        
                        float top = Math.Min(yOpen, yClose);
                        float bottom = Math.Max(yOpen, yClose);
                        float h = Math.Max(1f, bottom - top);
                        
                        float bw = (float)maxBw;
                        float offset = bw / 2f;
                        
                        var bodyPaintClone = aggIsBullish ? _bullishPaint : _bearishPaint;
                        
                        // Handle unconfirmed candle transparency (70%)
                        bool isLastBatch = (i == snapshot.Candles.Count); // Loop finished or snapping changed
                        // Approximate the index for the actual data
                        bool isActuallyUnconfirmed = (i >= snapshot.Candles.Count - 1);

                        byte originalAlpha = bodyPaintClone.Color.Alpha;
                        if (isActuallyUnconfirmed)
                        {
                            bodyPaintClone.Color = bodyPaintClone.Color.WithAlpha(178); // ~70%
                        }

                        canvas.DrawRect(currentPixelX - offset, top, bw, h, bodyPaintClone);
                        canvas.DrawRect(currentPixelX - offset, top, bw, h, _wickPaint);
                        
                        if (isActuallyUnconfirmed)
                        {
                            bodyPaintClone.Color = bodyPaintClone.Color.WithAlpha(originalAlpha);
                        }
                    }
                    else
                    {
                        // Simple Vertical Line (High-Low) (Level 1) - LOD Omission (no ticks when candleWidth < 2f)
                    float lineBottom = Math.Max(yHigh + 1f, yLow);
                    
                    if (config.ChartType == StockAnalyzer.Core.Models.ChartType.HeikinAshi)
                    {
                        // 1. Draw Wick part with lower opacity
                        byte originalAlpha = bodyPaint.Color.Alpha;
                        bodyPaint.Color = bodyPaint.Color.WithAlpha(100); // ~40% opacity
                        canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, bodyPaint);

                        // 2. Draw Body part with full opacity (or 70% if unconfirmed)
                        float top = Math.Min(yOpen, yClose);
                        float bottom = Math.Max(yOpen, yClose);
                        float h = Math.Max(1f, bottom - top);
                        
                        bool isLastCandle = (i == snapshot.Candles.Count - 1);
                        bodyPaint.Color = bodyPaint.Color.WithAlpha(isLastCandle ? (byte)178 : (byte)255); // 70% vs 100%
                        canvas.DrawLine(currentPixelX, top, currentPixelX, top + h, bodyPaint);
                        
                        bodyPaint.Color = bodyPaint.Color.WithAlpha(originalAlpha); // Restore
                    }
                    else
                    {
                        canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, bodyPaint);
                    }
                    }
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
            float yOpen = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggOpen)).Y + chartArea.Top);
            float yClose = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggClose)).Y + chartArea.Top);
            float yHigh = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggHigh)).Y + chartArea.Top);
            float yLow = (float)Math.Round(transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)aggLow)).Y + chartArea.Top);

            var bodyPaint = aggIsBullish ? _bullishPaint : _bearishPaint;

            int maxBw = (int)Math.Floor(candleWidth - 1f);
            if (maxBw % 2 == 0) maxBw -= 1;

            if (maxBw >= 1)
            {
                float wickBottom = Math.Max(yHigh + 1f, yLow);
                
                // Heikin Ashi wicks should match body color for continuity
                if (config.ChartType == StockAnalyzer.Core.Models.ChartType.HeikinAshi)
                {
                    _wickPaint.Color = bodyPaint.Color;
                }

                canvas.DrawLine(currentPixelX, yHigh, currentPixelX, wickBottom, _wickPaint);
                float top = Math.Min(yOpen, yClose);
                float bottom = Math.Max(yOpen, yClose);
                float h = Math.Max(1f, bottom - top);
                
                float bw = (float)maxBw;
                float offset = bw / 2f;
                
                // Latest candle (final batch) 70% transparency
                byte originalAlpha = bodyPaint.Color.Alpha;
                bodyPaint.Color = bodyPaint.Color.WithAlpha(178);
                
                canvas.DrawRect(currentPixelX - offset, top, bw, h, bodyPaint);
                canvas.DrawRect(currentPixelX - offset, top, bw, h, _wickPaint);
                
                bodyPaint.Color = bodyPaint.Color.WithAlpha(originalAlpha);
            }
            else
            {
                float lineBottom = Math.Max(yHigh + 1f, yLow);
                
                if (config.ChartType == StockAnalyzer.Core.Models.ChartType.HeikinAshi)
                {
                    // LOD Opacity for Line Mode
                    byte originalAlpha = bodyPaint.Color.Alpha;
                    bodyPaint.Color = bodyPaint.Color.WithAlpha(100); // Wick part 40%
                    canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, bodyPaint);

                    float top = Math.Min(yOpen, yClose);
                    float bottom = Math.Max(yOpen, yClose);
                    float h = Math.Max(1f, bottom - top);
                    
                    // The final batch always contains the last candle
                    bodyPaint.Color = bodyPaint.Color.WithAlpha(178); // 70% for latest
                    canvas.DrawLine(currentPixelX, top, currentPixelX, top + h, bodyPaint);
                    bodyPaint.Color = bodyPaint.Color.WithAlpha(originalAlpha);
                }
                else
                {
                    canvas.DrawLine(currentPixelX, yHigh, currentPixelX, lineBottom, bodyPaint);
                }
            }
        }
    }

    public void Dispose()
    {
        _bullishPaint.Dispose();
        _bearishPaint.Dispose();
        _neutralPaint.Dispose();
        _wickPaint.Dispose();
    }
}
