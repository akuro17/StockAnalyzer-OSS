using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

/// <summary>
/// Renders detected Auto Elliott Wave patterns on the chart canvas.
/// Draws wave connection lines with numbered labels (1-2-3-4-5 or A-B-C).
/// </summary>
public class AutoElliottWaveRenderer : IDisposable
{
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _linePaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _circlePaint;
    private readonly SKPaint _scorePaint;
    private readonly SKPath _path;
    private bool _disposed;

    public AutoElliottWaveRenderer()
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

        _linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        _textPaint = new SKPaint
        {
            TextSize = DrawingThemeContext.DrawingFontSize,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        _circlePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _scorePaint = new SKPaint
        {
            TextSize = DrawingThemeContext.DrawingFontSize,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        _path = new SKPath();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not AutoElliottWaveObject ewObj || ewObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(ewObj.Points[0]);
        var p2 = transform.ChartToScreen(ewObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        // 1. Draw Selection Background & Border
        RenderSelectionArea(canvas, ewObj, left, right, x1, x2, clip, isSelected);

        // 2. Draw Cached Wave Patterns (with hover focus support)
        if (ewObj.CachedResults != null && ewObj.CachedResults.Count > 0)
        {
            for (int i = 0; i < ewObj.CachedResults.Count; i++)
            {
                // When a label is hovered, only render the focused pattern
                if (ewObj.HoveredResultIndex >= 0 && i != ewObj.HoveredResultIndex)
                    continue;

                RenderWavePattern(canvas, transform, ewObj, ewObj.CachedResults[i]);
            }
        }
    }

    private void RenderSelectionArea(SKCanvas canvas, AutoElliottWaveObject ewObj, float left, float right, float x1, float x2, SKRect clip, bool isSelected)
    {
        // Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        _bgPaint.Color = new SKColor(ewObj.SkiaFillColor.Red, ewObj.SkiaFillColor.Green, ewObj.SkiaFillColor.Blue, (byte)(255 * ewObj.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _bgPaint);

        // Vertical Lines
        _borderPaint.Color = isSelected ? ewObj.SkiaColor : ewObj.SkiaColor.WithAlpha(180);
        _borderPaint.StrokeWidth = isSelected ? (float)ewObj.Thickness + 1 : (float)ewObj.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _borderPaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _borderPaint);

        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), ewObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), ewObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    private void RenderWavePattern(SKCanvas canvas, ICoordinateTransform transform,
        AutoElliottWaveObject ewObj, ElliottWaveResult result)
    {
        if (result.WavePoints.Count < 2) return;

        // Choose colors based on direction
        var lineColor = result.IsBullish ? AppTheme.GeometricSupportLine : AppTheme.GeometricResistanceLine;
        var labelColor = result.IsBullish
            ? new SKColor(0, 180, 0, 230)  // Green for bullish
            : new SKColor(220, 50, 50, 230); // Red for bearish

        // Get wave labels
        string[] labels = result.IsImpulse
            ? new[] { "0", "1", "2", "3", "4", "5" }
            : new[] { "0", "A", "B", "C" };

        // 1. Draw wave connection lines
        _linePaint.Color = lineColor;
        _linePaint.StrokeWidth = (float)ewObj.Thickness;

        _path.Reset();
        float firstX = 0f, firstY = 0f;
        float lastX = 0f;
        float topY = float.MaxValue;

        _textPaint.Color = labelColor;
        _circlePaint.Color = new SKColor(labelColor.Red, labelColor.Green, labelColor.Blue, 60);

        for (int i = 0; i < result.WavePoints.Count; i++)
        {
            var wp = result.WavePoints[i];
            var sp = transform.ChartToScreen(new ChartPoint(wp.Time, wp.Price));
            float sx = (float)sp.X;
            float sy = (float)sp.Y;

            if (i == 0)
            {
                _path.MoveTo(sx, sy);
                firstX = sx;
                firstY = sy;
            }
            else
            {
                _path.LineTo(sx, sy);
            }

            if (i == result.WavePoints.Count - 1)
            {
                lastX = sx;
            }

            if (sy < topY) topY = sy;

            // Draw label & circle if within label count
            if (i < labels.Length)
            {
                canvas.DrawCircle(sx, sy, 4, _circlePaint);

                bool isHigh = wp.IsHigh;
                float labelX = sx - _textPaint.MeasureText(labels[i]) / 2;
                float labelY = isHigh ? sy - 10 : sy + 18;

                if (!result.IsImpulse && (labels[i] == "A" || labels[i] == "C"))
                {
                    if (labels[i] == "A") labelX -= 12;
                    if (labels[i] == "C") labelX += 12;
                }

                canvas.DrawText(labels[i], labelX, labelY, _textPaint);
            }
        }

        canvas.DrawPath(_path, _linePaint);

        // 3. Draw confidence score text near the pattern center
        if (result.WavePoints.Count >= 2)
        {
            string type = result.IsImpulse ? "Impulse" : "Corrective";
            string scoreText = $"{type} ({(result.ConfidenceScore * 100):F0}%)";

            _scorePaint.Color = lineColor.WithAlpha(180);
            float centerX = (firstX + lastX) / 2f;
            canvas.DrawText(scoreText, centerX - _scorePaint.MeasureText(scoreText) / 2, topY - 20, _scorePaint);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _linePaint.Dispose();
        _textPaint.Dispose();
        _circlePaint.Dispose();
        _scorePaint.Dispose();
        _path.Dispose();
    }
}
