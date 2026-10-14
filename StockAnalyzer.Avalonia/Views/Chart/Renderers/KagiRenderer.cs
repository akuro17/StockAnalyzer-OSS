using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using System.Linq;
using StockAnalyzer.Avalonia.Utilities;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Kagi charts using Yang (thick/green) and Yin (thin/red) lines.
/// </summary>
public sealed class KagiRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _yangUpPaint;
    private readonly SKPaint _yangDownPaint;
    private readonly SKPaint _yinUpPaint;
    private readonly SKPaint _yinDownPaint;

    public KagiRenderer()
    {
        _yangUpPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        _yangDownPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        _yinUpPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        _yinDownPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IKagiRenderConfig)baseConfig;
        if (snapshot == null || snapshot.Candles.Count == 0) return;

        // Step 42-1-1: Dynamic Snap Logic (Removed unused PixelSnap)

        // Update paint colors
        _yangUpPaint.Color = config.KagiBullishColor.ToSkColor();
        _yangDownPaint.Color = config.KagiBearishColor.ToSkColor();
        _yinUpPaint.Color = config.KagiBullishColor.ToSkColor();
        _yinDownPaint.Color = config.KagiBearishColor.ToSkColor();

        // Update paint thickness (Yang = thick, Yin = thin)
        float scaling = (float)config.RenderScaling;
        float yinThickness = (float)config.KagiLineThickness * scaling;
        float yangThickness = ((float)config.KagiLineThickness + 1.0f) * scaling;
        _yangUpPaint.StrokeWidth = yangThickness;
        _yangDownPaint.StrokeWidth = yangThickness;
        _yinUpPaint.StrokeWidth = yinThickness;
        _yinDownPaint.StrokeWidth = yinThickness;

        if (config.Transform == null) return;
        var t = config.Transform;
        
        // Draw Kagi lines - only advance X on reversals
        float lastScreenY = 0;
        bool lastIsUp = true;
        bool lastIsYang = true;
        bool isAnchorBlock = false;
        
        for (int i = 0; i < snapshot.Candles.Count; i++)
        {
            var segment = snapshot.Candles[i];
            
            // Re-acquire raw physical bounds (sanitized by framework)
            decimal bottomPrice = Math.Min(segment.Open, segment.Close);
            decimal topPrice = Math.Max(segment.Open, segment.Close);
            
            // IsYang (State) is now stored in Volume (1 = Yang, 0 = Yin)
            bool isYang = segment.Volume == 1;
            
            // Physical drawing direction is calculated from Open/Close
            bool isUp = segment.Close >= segment.Open;
            
            // Direction determines Color (Up = Green, Down = Red)
            // State determines Thickness (Yang = Thick, Yin = Thin)
            SKPaint paint;
            if (isYang) paint = isUp ? _yangUpPaint : _yangDownPaint;
            else paint = isUp ? _yinUpPaint : _yinDownPaint;

            double logicalXIndex = snapshot.GetLogicalXIndex(i, config);
            float centerX = (float)t.ChartToScreen(new ChartPoint(new DateTime((int)logicalXIndex), 0)).X + (float)chartArea.Left;

            // Screen Y coordinates (Smaller Y = Higher Price)
            float yTop = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, topPrice)).Y + (float)chartArea.Top;
            float yBottom = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, bottomPrice)).Y + (float)chartArea.Top;

            if (i == 0)
            {
                // Handle Initial State: detect flat anchor block (Open == Close)
                isAnchorBlock = segment.Open == segment.Close;
                if (!isAnchorBlock)
                {
                    // Non-flat first segment: draw vertical line normally
                    canvas.DrawLine(centerX, yTop, centerX, yBottom, paint);
                }
                // Flat anchor: no vertical line to draw (zero height), just record position
                
                lastScreenY = isAnchorBlock ? yTop : (isUp ? yTop : yBottom); 
                lastIsYang = isYang;
                lastIsUp = isUp;
            }
            else
            {
                // When transitioning from flat anchor (index 0) to first trend segment (index 1),
                // always draw horizontal connector regardless of direction match
                int prevLogicalX = (int)snapshot.GetLogicalXIndex(i - 1, config);
                int currLogicalX = (int)logicalXIndex;
                bool needsHorizontalConnector = currLogicalX != prevLogicalX || isAnchorBlock;
                
                if (needsHorizontalConnector)
                {
                    float prevCenterX = (float)t.ChartToScreen(new ChartPoint(new DateTime(prevLogicalX), 0)).X + (float)chartArea.Left;
                    
                    // 1. Draw horizontal shoulder using the PRECEDING segment's properties (Thickness Inheritance)
                    SKPaint prevPaint;
                    if (lastIsYang) prevPaint = lastIsUp ? _yangUpPaint : _yangDownPaint;
                    else prevPaint = lastIsUp ? _yinUpPaint : _yinDownPaint;
                    
                    canvas.DrawLine(prevCenterX, lastScreenY, centerX, lastScreenY, prevPaint);
                    
                    // 2. Draw vertical segment using the NEW properties (Z-Order: Vertical after horizontal)
                    canvas.DrawLine(centerX, lastScreenY, centerX, isUp ? yTop : yBottom, paint);
                    
                    isAnchorBlock = false; // Clear after first use
                }
                else
                {
                    // Continuation
                    canvas.DrawLine(centerX, lastScreenY, centerX, isUp ? yTop : yBottom, paint);
                }
                
                lastScreenY = isUp ? yTop : yBottom;
                lastIsUp = isUp;
                lastIsYang = isYang;
            }
        }
        
        // On-chart labels for Kagi reversals have been migrated to the right-side DataWindow.
        // See DataWindowViewModel for extraction logic.
    }

    public void Dispose()
    {
        _yangUpPaint.Dispose();
        _yangDownPaint.Dispose();
        _yinUpPaint.Dispose();
        _yinDownPaint.Dispose();
    }
}

