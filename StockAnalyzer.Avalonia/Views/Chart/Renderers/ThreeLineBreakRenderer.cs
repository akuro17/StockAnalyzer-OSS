using System;
using System.Collections.Immutable;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using System.Linq;
using StockAnalyzer.Core.Theme; // Added for AppTheme
using StockAnalyzer.Avalonia.Utilities;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Three Line Break charts with up/down blocks.
/// </summary>
public sealed class ThreeLineBreakRenderer : IChartRenderer, IDisposable, IAxisProjectable
{
    private readonly SKPaint _upPaint;
    private readonly SKPaint _downPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _textPaint;

    private decimal _reversalPrice;
    private bool _showReversalPrice;
    private SKColor _reversalLineColor;

    private readonly SKPaint _reversalLinePaint;
    private readonly SKPathEffect _reversalDashEffect;

    public ThreeLineBreakRenderer()
    {
        _upPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _downPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _borderPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        _textPaint = new SKPaint 
        { 
            IsAntialias = true, 
            TextSize = 12, 
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) 
        };

        _reversalDashEffect = SKPathEffect.CreateDash(new float[] { 5f, 5f }, 0);
        _reversalLinePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            PathEffect = _reversalDashEffect,
            IsAntialias = true
        };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IThreeLineBreakRenderConfig)baseConfig;
        if (snapshot == null || snapshot.Candles == null || snapshot.Candles.Count == 0) return;
        
        var candles = snapshot.Candles;
        int candleCount = candles.Count;

        var theme = config.ThemeManager.CurrentTheme;

        // Context Unpacking
        int lineCount = config.ThreeLineBreakLineCount;
        decimal currentRawPrice = (decimal)config.CurrentPrice;
        _upPaint.Color = config.ThreeLineBreakBullishColor.ToSkColor();
        _downPaint.Color = config.ThreeLineBreakBearishColor.ToSkColor();
        _reversalLinePaint.Color = config.ReversalLineColor.ToSkColor();

        _showReversalPrice = config.ShowReversalPrice;
        _reversalLineColor = config.ReversalLineColor.ToSkColor();

        // Calculate Column Width using Transform
        var t = config.Transform;
        if (t == null) return;
        double x0 = t.ChartToScreen(new ChartPoint(new DateTime(0), 0)).X;
        double x1 = t.ChartToScreen(new ChartPoint(new DateTime(1), 0)).X;
        float columnWidth = (float)(x1 - x0);
        float bodyWidth = Math.Max(1f, columnWidth * 0.9f);

        for (int i = 0; i < candleCount; i++)
        {
            var block = candles[i];
            int absoluteIndex = snapshot.StartIndex + i;
            
            // X-Coordinate (Offset by ChartArea.Left)
            float x = (float)t.ChartToScreen(new ChartPoint(new DateTime(absoluteIndex), 0)).X + (float)chartArea.Left;
            float centerX = x + columnWidth / 2f;

            // Y-Coordinates (Offset by ChartArea.Top)
            float yOpen = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, block.Open)).Y + (float)chartArea.Top;
            float yClose = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, block.Close)).Y + (float)chartArea.Top;
            
            float top = Math.Min(yOpen, yClose);
            float bottom = Math.Max(yOpen, yClose);
            
            var rect = new SKRect(centerX - bodyWidth / 2f, top, centerX + bodyWidth / 2f, bottom);

            bool isUp = block.Close >= block.Open;
            var paint = isUp ? _upPaint : _downPaint;
            
            // Apply context color
            if (isUp)
                 _upPaint.Color = config.ThreeLineBreakBullishColor.ToSkColor();
            else
                 _downPaint.Color = config.ThreeLineBreakBearishColor.ToSkColor();

            // Draw Fill
            canvas.DrawRect(rect, paint);
            
            // Draw Border
            canvas.DrawRect(rect, _borderPaint);
        }
        // Reversal Trigger Line Visualization
        if (candleCount > 0)
        {
            var lastBlock = candles[candleCount - 1];
            bool isLastUp = lastBlock.Close >= lastBlock.Open;
            
            decimal reversalPrice = 0;
            int reversalOriginIndex = candleCount - 1; // Default to last candle

            if (candleCount >= lineCount + 1)
            {
                int startIdx = candleCount - 1 - lineCount;
                if (isLastUp)
                {
                    decimal lowestLow = decimal.MaxValue;
                    for (int j = startIdx; j < candleCount - 1; j++)
                    {
                        decimal blockLow = Math.Min(candles[j].Open, candles[j].Close);
                        if (blockLow <= lowestLow)
                        {
                            lowestLow = blockLow;
                            reversalOriginIndex = j;
                        }
                    }
                    reversalPrice = lowestLow;
                }
                else
                {
                    decimal highestHigh = decimal.MinValue;
                    for (int j = startIdx; j < candleCount - 1; j++)
                    {
                        decimal blockHigh = Math.Max(candles[j].Open, candles[j].Close);
                        if (blockHigh >= highestHigh)
                        {
                            highestHigh = blockHigh;
                            reversalOriginIndex = j;
                        }
                    }
                    reversalPrice = highestHigh;
                }
            }
            else
            {
                reversalPrice = isLastUp ? Math.Min(lastBlock.Open, lastBlock.Close) : Math.Max(lastBlock.Open, lastBlock.Close);
            }

            _reversalPrice = reversalPrice;

            if (config.ShowReversalLine)
            {
                // Draw horizontal dashed line for Reversal Price
                float yReversal = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, reversalPrice)).Y + (float)chartArea.Top;
                
                // Start line from the exact block that provides the reversal point
                int originAbsoluteIndex = snapshot.StartIndex + reversalOriginIndex;
                float originX = (float)t.ChartToScreen(new ChartPoint(new DateTime(originAbsoluteIndex), 0)).X + (float)chartArea.Left;
                
                // Start from the center of that block
                float startX = originX + columnWidth / 2f;
                float endX = (float)chartArea.Right;

                if (startX < endX)
                {
                    _reversalLinePaint.Color = config.ReversalLineColor.ToSkColor();
                    canvas.DrawLine(startX, yReversal, endX, yReversal, _reversalLinePaint);
                }
            }
        }

        // HUD / Reversal text rendering on canvas has been removed (Prompt 38-7).
        // The values are now exposed via ViewModel (DataWindowViewModel) and displayed in the Right Panel.
    }

    /* parseColor method removed - using ColorHelper */

    public System.Collections.Generic.IEnumerable<AxisLabelRequest> GetAxisProjections(ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        if (baseConfig is not IThreeLineBreakRenderConfig config || snapshot.Candles == null || snapshot.Candles.Count == 0) yield break;

        int lineCount = config.ThreeLineBreakLineCount;
        var candles = snapshot.Candles;
        int candleCount = candles.Count;
        var lastBlock = candles[candleCount - 1];
        bool isLastUp = lastBlock.Close >= lastBlock.Open;
        
        decimal reversalPrice = 0;

        if (candleCount >= lineCount + 1)
        {
            int startIdx = candleCount - 1 - lineCount;
            if (isLastUp)
            {
                decimal lowestLow = decimal.MaxValue;
                for (int j = startIdx; j < candleCount - 1; j++)
                {
                    decimal blockLow = Math.Min(candles[j].Open, candles[j].Close);
                    if (blockLow <= lowestLow) lowestLow = blockLow;
                    
                }
                reversalPrice = lowestLow;
            }
            else
            {
                decimal highestHigh = decimal.MinValue;
                for (int j = startIdx; j < candleCount - 1; j++)
                {
                    decimal blockHigh = Math.Max(candles[j].Open, candles[j].Close);
                    if (blockHigh >= highestHigh) highestHigh = blockHigh;
                }
                reversalPrice = highestHigh;
            }
        }
        else
        {
            reversalPrice = isLastUp ? Math.Min(lastBlock.Open, lastBlock.Close) : Math.Max(lastBlock.Open, lastBlock.Close);
        }

        if (config.ShowReversalPrice)
        {
            yield return new AxisLabelRequest(
                Value: reversalPrice,
                Color: config.ReversalLineColor.ToSkColor(),
                Label: reversalPrice.ToString("0.00"),
                Style: AxisLabelStyle.Default
            );
        }
    }

    public void Dispose()
    {
        _upPaint.Dispose();
        _downPaint.Dispose();
        _borderPaint.Dispose();
        _textPaint.Dispose();
        _reversalLinePaint.Dispose();
        _reversalDashEffect.Dispose();
    }
}
