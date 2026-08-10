using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders Market Structure Shifts (BOS/CHoCH) as distinct horizontal lines with text labels.
/// </summary>
public class MarketStructureShiftRenderer
{
    public void Render(
        SKCanvas canvas, 
        Rect chartArea, 
        IReadOnlyList<MarketStructureShift> shifts,
        CoreIndicatorSettings setting,
        decimal minVal, 
        decimal maxVal, 
        ICoordinateTransform? transform,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval)
    {
        if (shifts == null || shifts.Count == 0 || transform == null || candles.Count == 0) return;

        // Base colors
        SKColor bosColor = new SKColor(33, 150, 243, 200); // Default Blue
        SKColor chochColor = new SKColor(255, 152, 0, 200); // Default Orange
        
        // Attempt to extract from settings if available
        if (setting.SeriesColors != null)
        {
            foreach (var sc in setting.SeriesColors)
            {
                if (sc.Name == "BOS") bosColor = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
                if (sc.Name == "CHoCH") chochColor = new SKColor(sc.Color.R, sc.Color.G, sc.Color.B, sc.Color.A);
            }
        }

        using var bosPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = (float)setting.Thickness, Color = bosColor };
        using var chochPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = (float)setting.Thickness, Color = chochColor };
        
        // Optional: Make it dashed
        bosPaint.PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0);
        chochPaint.PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0);

        using var textPaintBos = new SKPaint
        {
            Color = bosColor,
            TextSize = 10f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };
        
        using var textPaintChoch = new SKPaint
        {
            Color = chochColor,
            TextSize = 10f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        decimal range = maxVal - minVal;
        if (range == 0) range = 1;

        foreach (var shift in shifts)
        {
            SKPaint linePaint;
            SKPaint textPaint;
            string label;
            
            // Get pivot index and level
            decimal level;

            if (shift.Type == MarketStructureType.BullishBOS || shift.Type == MarketStructureType.BearishBOS)
            {
                linePaint = bosPaint;
                textPaint = textPaintBos;
                label = "BOS";
            }
            else
            {
                linePaint = chochPaint;
                textPaint = textPaintChoch;
                label = "CHoCH";
            }
            
            System.DateTime startTime;
            
            if (shift.Type == MarketStructureType.BullishBOS || shift.Type == MarketStructureType.BullishCHoCH)
            {
                startTime = shift.PreviousPivotHighTime;
                level = shift.PreviousPivotHigh;
            }
            else
            {
                startTime = shift.PreviousPivotLowTime;
                level = shift.PreviousPivotLow;
            }

            System.DateTime endTime = shift.Time;

            float y = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, level)).Y + (float)chartArea.Top;

            // Calculate X coordinates
            float startX, endX;
            if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
            {
                int sIdx = (shift.Type == MarketStructureType.BullishBOS || shift.Type == MarketStructureType.BullishCHoCH) 
                    ? shift.PreviousPivotHighIndex : shift.PreviousPivotLowIndex;
                int eIdx = shift.Index;

                startX = (float)transform.ChartToScreen(new ChartPoint(new DateTime(sIdx), 0)).X;
                endX = (float)transform.ChartToScreen(new ChartPoint(new DateTime(eIdx), 0)).X;
            }
            else
            {
                startX = (float)transform.ChartToScreen(new ChartPoint(startTime, 0)).X;
                endX = (float)transform.ChartToScreen(new ChartPoint(endTime, 0)).X;
            }

            // Draw horizontal line
            canvas.DrawLine(startX, y, endX, y, linePaint);

            // Draw label
            float textWidth = textPaint.MeasureText(label);
            float textX = startX + (endX - startX) / 2f - textWidth / 2f;
            float textY = y - 4f; // Slightly above the line
            if (shift.Type == MarketStructureType.BearishBOS || shift.Type == MarketStructureType.BearishCHoCH)
            {
                // For bearish break (breaking a low), put text slightly below the line
                textY = y + 12f; 
            }

            canvas.DrawText(label, textX, textY, textPaint);
        }
    }
}
