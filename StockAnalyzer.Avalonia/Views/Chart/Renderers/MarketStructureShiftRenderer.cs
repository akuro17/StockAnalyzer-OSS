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
public sealed class MarketStructureShiftRenderer : IDisposable
{
    private readonly SKPaint _bosPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _chochPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaintBos;
    private readonly SKPaint _textPaintChoch;
    private readonly SKPathEffect _dashEffect;
    private readonly SKTypeface _labelTypeface;

    public MarketStructureShiftRenderer()
    {
        _dashEffect = SKPathEffect.CreateDash(
            new float[] { ChartTheme.MarketStructureDashOn, ChartTheme.MarketStructureDashOff },
            ChartTheme.MarketStructureDashPhase);
        _bosPaint.PathEffect = _dashEffect;
        _chochPaint.PathEffect = _dashEffect;

        _labelTypeface = SKTypeface.FromFamilyName(
            ChartTheme.MarketStructureLabelFontFamily, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        _textPaintBos = new SKPaint
        {
            TextSize = ChartTheme.MarketStructureLabelTextSize,
            IsAntialias = true,
            Typeface = _labelTypeface
        };
        _textPaintChoch = new SKPaint
        {
            TextSize = ChartTheme.MarketStructureLabelTextSize,
            IsAntialias = true,
            Typeface = _labelTypeface
        };
    }

    public void Dispose()
    {
        _bosPaint.Dispose();
        _chochPaint.Dispose();
        _textPaintBos.Dispose();
        _textPaintChoch.Dispose();
        _dashEffect.Dispose();
        _labelTypeface.Dispose();
    }

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

        // Cached paints (dashed via the shared effect): only the per-call width and colors are set here.
        float strokeWidth = (float)setting.Thickness;
        _bosPaint.StrokeWidth = strokeWidth;
        _bosPaint.Color = bosColor;
        _chochPaint.StrokeWidth = strokeWidth;
        _chochPaint.Color = chochColor;
        _textPaintBos.Color = bosColor;
        _textPaintChoch.Color = chochColor;
        SKPaint bosPaint = _bosPaint;
        SKPaint chochPaint = _chochPaint;
        SKPaint textPaintBos = _textPaintBos;
        SKPaint textPaintChoch = _textPaintChoch;

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
