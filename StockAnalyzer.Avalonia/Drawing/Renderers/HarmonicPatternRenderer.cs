using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class HarmonicPatternRenderer
{
    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HarmonicPatternObject hObj || hObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(hObj.Points[0]);
        var p2 = transform.ChartToScreen(hObj.Points[1]);

        var rect = new SKRect(
            (float)Math.Min(p1.X, p2.X),
            (float)Math.Min(p1.Y, p2.Y),
            (float)Math.Max(p1.X, p2.X),
            (float)Math.Max(p1.Y, p2.Y));

        // 1. Draw Selection Background & Border
        RenderSelectionArea(canvas, hObj, rect, isSelected);

        // 2. Draw Cached Patterns (with hover focus support)
        if (hObj.CachedResults != null && hObj.CachedResults.Count > 0)
        {
            for (int i = 0; i < hObj.CachedResults.Count; i++)
            {
                // When a label is hovered, only render the focused pattern
                if (hObj.HoveredResultIndex >= 0 && i != hObj.HoveredResultIndex)
                    continue;

                RenderPattern(canvas, transform, hObj, hObj.CachedResults[i]);
            }
        }
    }

    private void RenderSelectionArea(SKCanvas canvas, HarmonicPatternObject hObj, SKRect rect, bool isSelected)
    {
        // Background
        using (var bgPaint = new SKPaint
        {
            Color = new SKColor(hObj.SkiaColor.Red, hObj.SkiaColor.Green, hObj.SkiaColor.Blue, 30),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, bgPaint);
        }

        // Border
        using (var borderPaint = new SKPaint
        {
            Color = isSelected ? hObj.SkiaColor : hObj.SkiaColor.WithAlpha(100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)hObj.Thickness + 1 : 1.0f,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, borderPaint);
            
            // Draw Handles if selected
            if (isSelected)
            {
                using var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
                canvas.DrawCircle(rect.Left, rect.Top, 4, handlePaint);
                canvas.DrawCircle(rect.Right, rect.Top, 4, handlePaint);
                canvas.DrawCircle(rect.Right, rect.Bottom, 4, handlePaint);
                canvas.DrawCircle(rect.Left, rect.Bottom, 4, handlePaint);
            }
        }
    }

    private void RenderPattern(SKCanvas canvas, ICoordinateTransform transform, HarmonicPatternObject hObj, HarmonicPatternResult result)
    {
        // Convert the 5 points to screen coordinates
        var screenX = transform.ChartToScreen(new ChartPoint(result.X.Time, result.X.Price));
        var screenA = transform.ChartToScreen(new ChartPoint(result.A.Time, result.A.Price));
        var screenB = transform.ChartToScreen(new ChartPoint(result.B.Time, result.B.Price));
        var screenC = transform.ChartToScreen(new ChartPoint(result.C.Time, result.C.Price));
        var screenD = transform.ChartToScreen(new ChartPoint(result.D.Time, result.D.Price));

        var skFillColor = result.IsBullish ? AppTheme.HarmonicFillBull : AppTheme.HarmonicFillBear;

        var skLineColor = result.IsBullish ? AppTheme.GeometricSupportLine : AppTheme.GeometricResistanceLine;

        // 1. Draw Triangles (XAB and BCD)
        using (var fillPaint = new SKPaint { Color = skFillColor, Style = SKPaintStyle.Fill, IsAntialias = true })
        using (var linePaint = new SKPaint { Color = skLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true })
        {
            // Triangle XAB
            using (var path1 = new SKPath())
            {
                path1.MoveTo((float)screenX.X, (float)screenX.Y);
                path1.LineTo((float)screenA.X, (float)screenA.Y);
                path1.LineTo((float)screenB.X, (float)screenB.Y);
                path1.Close();
                canvas.DrawPath(path1, fillPaint);
                canvas.DrawPath(path1, linePaint);
            }

            // Triangle BCD
            using (var path2 = new SKPath())
            {
                path2.MoveTo((float)screenB.X, (float)screenB.Y);
                path2.LineTo((float)screenC.X, (float)screenC.Y);
                path2.LineTo((float)screenD.X, (float)screenD.Y);
                path2.Close();
                canvas.DrawPath(path2, fillPaint);
                canvas.DrawPath(path2, linePaint);
            }
            
            // Line XC
            canvas.DrawLine((float)screenX.X, (float)screenX.Y, (float)screenC.X, (float)screenC.Y, linePaint);
            // Line XD
            using (var dashedPaint = new SKPaint { Color = skLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) })
            {
                canvas.DrawLine((float)screenX.X, (float)screenX.Y, (float)screenD.X, (float)screenD.Y, dashedPaint);
            }
        }

        // 2. Draw PRZ (Potential Reversal Zone)
        var skPrzColor = result.IsBullish ? AppTheme.HarmonicPrzBull : AppTheme.HarmonicPrzBear;

        var przLowScreen = transform.ChartToScreen(new ChartPoint(result.D.Time, result.PrzLow));
        var przHighScreen = transform.ChartToScreen(new ChartPoint(result.D.Time, result.PrzHigh));
        
        // Extend PRZ slightly to the right for visibility
        float przWidth = 40.0f; // px
        
        var przRect = new SKRect(
            (float)screenD.X,
            (float)Math.Min(przHighScreen.Y, przLowScreen.Y), // Skia Y is inverted (0 at top)
            (float)screenD.X + przWidth,
            (float)Math.Max(przHighScreen.Y, przLowScreen.Y)
        );

        if (hObj.ShowPrz)
        {
            // Fill
            using (var przFillPaint = new SKPaint { Color = skPrzColor.WithAlpha(50), Style = SKPaintStyle.Fill, IsAntialias = true })
            {
                canvas.DrawRect(przRect, przFillPaint);
            }

            // Border
            using (var przBorderPaint = new SKPaint { Color = skPrzColor.WithAlpha(150), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true })
            {
                canvas.DrawRect(przRect, przBorderPaint);
            }
        }

        // 3. Draw Text (Pattern Name and Score)
        string labelText = $"{result.PatternType} ({(result.ConfidenceScore * 100):F1}%)";
        using (var textPaint = new SKPaint
        {
            Color = skLineColor,
            TextSize = 12,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        })
        {
            // Position text below or above D point depending on direction
            float textY = result.IsBullish ? przRect.Bottom + 16 : przRect.Top - 8;
            canvas.DrawText(labelText, (float)screenD.X, textY, textPaint);
        }
    }
}
