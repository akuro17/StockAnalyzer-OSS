using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

public sealed class MultiWavePatternRenderer : System.IDisposable
{
    private readonly SKPaint _activeBullishPaint;
    private readonly SKPaint _activeBearishPaint;
    private readonly SKPaint _invalidatedSignalPaint;
    private readonly SKPaint _activeLinePaint;
    private readonly SKPaint _invalidatedLinePaint;

    public MultiWavePatternRenderer()
    {
        _activeBullishPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        _activeBearishPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        
        _invalidatedSignalPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = SKColors.Gray.WithAlpha(100) };
        
        _activeLinePaint = new SKPaint 
        { 
            Style = SKPaintStyle.Stroke, 
            IsAntialias = true, 
            StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0) 
        };
        
        _invalidatedLinePaint = new SKPaint 
        { 
            Style = SKPaintStyle.Stroke, 
            IsAntialias = true, 
            StrokeWidth = 2,
            Color = SKColors.Gray.WithAlpha(100),
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0) 
        };
    }



    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if (snapshot.MultiWaveSignals == null || snapshot.MultiWaveSignals.Count == 0 || config.Transform == null)
            return;

        var t = config.Transform;
        
        var theme = config.ThemeManager?.CurrentTheme ?? new Core.Theme.ThemeColors();
        var baseBullish = theme.Bullish;
        var baseBearish = theme.Bearish;

        double x0 = t.ChartToScreen(new ChartPoint(new System.DateTime(0), 0)).X;
        double x1 = t.ChartToScreen(new ChartPoint(new System.DateTime(1), 0)).X;
        float columnWidth = (float)(x1 - x0);
        float radius = System.Math.Max(3f, columnWidth * 0.4f);

        // Render each signal with ConfidenceScore mapped to Alpha
        var signals = snapshot.MultiWaveSignals;
        for (int s = 0; s < signals.Count; s++)
        {
            var signal = signals[s];
            
            bool isInvalidated = signal.IsInvalidated;
            int endIdx = isInvalidated ? signal.InvalidationIndex : (snapshot.AllCandles?.Count - 1 ?? snapshot.StartIndex + snapshot.Candles.Count - 1);

            // Optimization: Skip if the entire signal line is completely to the left of the viewport
            if (endIdx < snapshot.StartIndex) continue;
            // Optimization: Skip if the signal starts after the visible viewport
            if (signal.TriggerIndex > snapshot.StartIndex + snapshot.Candles.Count) continue; 

            // Calculate Logical X Index mapping for non-linear charts
            double logicalTriggerIdx = config.ChartType == ChartType.Kagi ? snapshot.GetLogicalXIndex(signal.TriggerIndex, config) : signal.TriggerIndex;
            double logicalEndIdx = config.ChartType == ChartType.Kagi ? snapshot.GetLogicalXIndex(endIdx, config) : endIdx;

            // WebAI Nuclear Fix: Use NumericToScreen for absolute coordinate consistency across all non-linear charts.
            // This eliminates DateTime-based ambiguity and ensures identical mapping to main renderers.
            
            // X-Coordinates (Center of the column/bin)
            float startX = (float)t.NumericToScreen(logicalTriggerIdx, 0).X + (float)chartArea.Left;
            float endX = (float)t.NumericToScreen(logicalEndIdx, 0).X + (float)chartArea.Left;

            if (config.ChartType == ChartType.Renko || config.ChartType == ChartType.ThreeLineBreak)
            {
                // For Renko and TLB, columns are centered on ticks. (Already handled by NumericToScreen for Index mode)
                // But if convention requires bin-offset:
                startX += columnWidth / 2f;
                endX += columnWidth / 2f;
            }

            // Y-Coordinates (Discrete price levels)
            decimal effectiveTriggerPrice = signal.TriggerPrice;
            decimal effectiveInvalidationPrice = signal.InvalidationPrice;
            
            if (config.ChartType == ChartType.PointAndFigure || config.ChartType == ChartType.Renko || config.ChartType == ChartType.ThreeLineBreak)
            {
                // Center vertically in the price box/brick
                effectiveTriggerPrice += snapshot.MinBrickSize / 2m;
                effectiveInvalidationPrice += snapshot.MinBrickSize / 2m;
            }

            float triggerY = (float)t.NumericToScreen(0, (double)effectiveTriggerPrice).Y + (float)chartArea.Top;
            float invalidationY = (float)t.NumericToScreen(0, (double)effectiveInvalidationPrice).Y + (float)chartArea.Top;
            float targetMinY = (float)t.NumericToScreen(0, (double)signal.TargetPriceMin).Y + (float)chartArea.Top;
            float targetMaxY = (float)t.NumericToScreen(0, (double)signal.TargetPriceMax).Y + (float)chartArea.Top;

            double cs = System.Math.Clamp(signal.ConfidenceScore, 0.0, 1.0);
            double gamma = System.Math.Pow(cs, 1.5);
            byte alpha = (byte)(50 + gamma * 205);

            if (isInvalidated)
            {
                canvas.DrawCircle(startX, triggerY, radius, _invalidatedSignalPaint);
            }
            else
            {
                if (signal.IsBullish)
                {
                    _activeBullishPaint.Color = baseBullish.ToSkColor().WithAlpha(alpha);
                    canvas.DrawCircle(startX, triggerY, radius, _activeBullishPaint);
                }
                else
                {
                    _activeBearishPaint.Color = baseBearish.ToSkColor().WithAlpha(alpha);
                    canvas.DrawCircle(startX, triggerY, radius, _activeBearishPaint);
                }
            }

            // 2. Draw the Invalidation Line (Support/Resistance)
            if (isInvalidated)
            {
                canvas.DrawLine(startX, invalidationY, endX, invalidationY, _invalidatedLinePaint);
            }
            else
            {
                if (signal.IsBullish)
                {
                    _activeLinePaint.Color = baseBullish.ToSkColor().WithAlpha(alpha);
                }
                else
                {
                    _activeLinePaint.Color = baseBearish.ToSkColor().WithAlpha(alpha);
                }
                canvas.DrawLine(startX, invalidationY, endX, invalidationY, _activeLinePaint);
            }
        }
    }



    public void Dispose()
    {
        _activeBullishPaint.Dispose();
        _activeBearishPaint.Dispose();
        _invalidatedSignalPaint.Dispose();
        _activeLinePaint.Dispose();
        _invalidatedLinePaint.Dispose();
    }
}
