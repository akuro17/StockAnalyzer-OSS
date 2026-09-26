using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class HarmonicPatternRenderer : IDisposable
{
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _linePaint;
    private readonly SKPaint _dashedPaint;
    private readonly SKPathEffect _dashEffect;
    private readonly SKPaint _przFillPaint;
    private readonly SKPaint _przBorderPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPath _path1;
    private readonly SKPath _path2;
    private bool _disposed;

    public HarmonicPatternRenderer()
    {
        _bgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };

        _dashEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0);
        _dashedPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
            PathEffect = _dashEffect
        };

        _przFillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _przBorderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            TextSize = DrawingThemeContext.DrawingFontSize,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        _path1 = new SKPath();
        _path2 = new SKPath();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HarmonicPatternObject hObj || hObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(hObj.Points[0]);
        var p2 = transform.ChartToScreen(hObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        // 1. Draw Selection Background & Border
        RenderSelectionArea(canvas, hObj, left, right, x1, x2, clip, isSelected);

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

    private void RenderSelectionArea(SKCanvas canvas, HarmonicPatternObject hObj, float left, float right, float x1, float x2, SKRect clip, bool isSelected)
    {
        // Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        _bgPaint.Color = new SKColor(hObj.SkiaFillColor.Red, hObj.SkiaFillColor.Green, hObj.SkiaFillColor.Blue, (byte)(255 * hObj.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _bgPaint);

        // Vertical Lines
        _borderPaint.Color = isSelected ? hObj.SkiaColor : hObj.SkiaColor.WithAlpha(180);
        _borderPaint.StrokeWidth = isSelected ? (float)hObj.Thickness + 1 : (float)hObj.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _borderPaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _borderPaint);
        
        // Draw Handles if selected
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), hObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), hObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
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

        _fillPaint.Color = skFillColor;
        _linePaint.Color = skLineColor;

        // 1. Draw Triangles (XAB and BCD)
        _path1.Reset();
        _path1.MoveTo((float)screenX.X, (float)screenX.Y);
        _path1.LineTo((float)screenA.X, (float)screenA.Y);
        _path1.LineTo((float)screenB.X, (float)screenB.Y);
        _path1.Close();
        canvas.DrawPath(_path1, _fillPaint);
        canvas.DrawPath(_path1, _linePaint);

        _path2.Reset();
        _path2.MoveTo((float)screenB.X, (float)screenB.Y);
        _path2.LineTo((float)screenC.X, (float)screenC.Y);
        _path2.LineTo((float)screenD.X, (float)screenD.Y);
        _path2.Close();
        canvas.DrawPath(_path2, _fillPaint);
        canvas.DrawPath(_path2, _linePaint);
        
        // Line XC
        canvas.DrawLine((float)screenX.X, (float)screenX.Y, (float)screenC.X, (float)screenC.Y, _linePaint);
        // Line XD
        _dashedPaint.Color = skLineColor;
        canvas.DrawLine((float)screenX.X, (float)screenX.Y, (float)screenD.X, (float)screenD.Y, _dashedPaint);

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
            _przFillPaint.Color = skPrzColor.WithAlpha(50);
            canvas.DrawRect(przRect, _przFillPaint);

            // Border
            _przBorderPaint.Color = skPrzColor.WithAlpha(150);
            canvas.DrawRect(przRect, _przBorderPaint);
        }

        // 3. Draw Text (Pattern Name and Score)
        string labelText = $"{result.PatternType} ({(result.ConfidenceScore * 100):F1}%)";
        _textPaint.Color = skLineColor;
        
        // Position text below or above D point depending on direction
        float textY = result.IsBullish ? przRect.Bottom + 16 : przRect.Top - 8;
        canvas.DrawText(labelText, (float)screenD.X, textY, _textPaint);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _fillPaint.Dispose();
        _linePaint.Dispose();
        _dashEffect.Dispose();
        _dashedPaint.Dispose();
        _przFillPaint.Dispose();
        _przBorderPaint.Dispose();
        _textPaint.Dispose();
        _path1.Dispose();
        _path2.Dispose();
    }
}
