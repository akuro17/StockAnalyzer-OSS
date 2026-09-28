using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using System.Linq;
using StockAnalyzer.Avalonia.Utilities;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

public sealed class RenkoRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _upPaint;
    private readonly SKPaint _downPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _textPaint;

    public RenkoRenderer()
    {
        _upPaint = new SKPaint { Style = SKPaintStyle.StrokeAndFill }; 
        _downPaint = new SKPaint { Style = SKPaintStyle.StrokeAndFill };
        _borderPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = LayoutConstants.DefaultStrokeWidth };
        _textPaint = new SKPaint 
        { 
            IsAntialias = true, 
            TextSize = LayoutConstants.LabelFontSize, 
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) 
        };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IRenkoRenderConfig)baseConfig;
        if (snapshot == null || snapshot.Candles.Count == 0 || config.RenkoBrickSize <= 0) return;

        if (config.Transform == null) return;
        var t = config.Transform;

        double brickSize = config.RenkoBrickSize;

        double x0 = t.ChartToScreen(new ChartPoint(new DateTime(0), 0)).X;
        double x1 = t.ChartToScreen(new ChartPoint(new DateTime(1), 0)).X;
        float columnWidth = (float)(x1 - x0);
        float bodyWidth = Math.Max(1f, columnWidth * 0.9f); 

        var theme = config.ThemeManager.CurrentTheme;
        // Update paints with context colors
        _upPaint.Color = config.RenkoBullishColor.ToSkColor();
        _downPaint.Color = config.RenkoBearishColor.ToSkColor();

        bool drawBorders = columnWidth >= 4f;
        bool drawAsLines = columnWidth <= 2f;
        bool drawTrendCount = columnWidth >= 15f;

        if (drawAsLines)
        {
            _upPaint.Style = SKPaintStyle.Stroke;
            _upPaint.StrokeWidth = 1f;
            _downPaint.Style = SKPaintStyle.Stroke;
            _downPaint.StrokeWidth = 1f;
        }
        else
        {
            _upPaint.Style = SKPaintStyle.StrokeAndFill;
            _downPaint.Style = SKPaintStyle.StrokeAndFill;
        }

        for (int i = 0; i < snapshot.Candles.Count; i++)
        {
            var brick = snapshot.Candles[i];
            int absoluteIndex = snapshot.StartIndex + i;
            
            float x = (float)t.ChartToScreen(new ChartPoint(new DateTime(absoluteIndex), 0)).X + (float)chartArea.Left;
            float centerX = x + columnWidth / 2f;

            float yOpen = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, brick.Open)).Y + (float)chartArea.Top;
            float yClose = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, brick.Close)).Y + (float)chartArea.Top;
            
            float top = Math.Min(yOpen, yClose);
            float bottom = Math.Max(yOpen, yClose);
            
            var rect = new SKRect(centerX - bodyWidth / 2f, top, centerX + bodyWidth / 2f, bottom);

            bool isUp = brick.Close >= brick.Open;
            var paint = isUp ? _upPaint : _downPaint;
            
            if (drawAsLines)
            {
                // Draw as a vertical line for extreme zoom out
                canvas.DrawLine(centerX, top, centerX, bottom, paint);
            }
            else
            {
                // Ensure at least 1px height
                if (bottom - top < 1f)
                {
                    bottom = top + 1f;
                    rect = new SKRect(centerX - bodyWidth / 2f, top, centerX + bodyWidth / 2f, bottom);
                }

                // Draw Fill
                canvas.DrawRect(rect, paint);
                
                // Draw Border
                if (drawBorders)
                {
                    canvas.DrawRect(rect, _borderPaint);
                }
            }
        }


    }

    public void Dispose()
    {
        _upPaint.Dispose();
        _downPaint.Dispose();
        _borderPaint.Dispose();
        _textPaint.Dispose();
    }
}

