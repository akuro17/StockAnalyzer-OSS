using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

/// <summary>
/// Renders detected Auto Elliott Wave patterns on the chart canvas.
/// Draws wave connection lines with numbered labels (1-2-3-4-5 or A-B-C).
/// </summary>
public class AutoElliottWaveRenderer
{
    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not AutoElliottWaveObject ewObj || ewObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(ewObj.Points[0]);
        var p2 = transform.ChartToScreen(ewObj.Points[1]);

        var rect = new SKRect(
            (float)Math.Min(p1.X, p2.X),
            (float)Math.Min(p1.Y, p2.Y),
            (float)Math.Max(p1.X, p2.X),
            (float)Math.Max(p1.Y, p2.Y));

        // 1. Draw Selection Background & Border
        RenderSelectionArea(canvas, ewObj, rect, isSelected);

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

    private void RenderSelectionArea(SKCanvas canvas, AutoElliottWaveObject ewObj, SKRect rect, bool isSelected)
    {
        // Background
        using (var bgPaint = new SKPaint
        {
            Color = new SKColor(ewObj.SkiaColor.Red, ewObj.SkiaColor.Green, ewObj.SkiaColor.Blue, 20),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, bgPaint);
        }

        // Border
        using (var borderPaint = new SKPaint
        {
            Color = isSelected ? ewObj.SkiaColor : ewObj.SkiaColor.WithAlpha(80),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)ewObj.Thickness + 1 : 1.0f,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, borderPaint);

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

    private void RenderWavePattern(SKCanvas canvas, ICoordinateTransform transform,
        AutoElliottWaveObject ewObj, ElliottWaveResult result)
    {
        if (result.WavePoints.Count < 2) return;

        // Convert pivot points to screen coordinates
        var screenPoints = new global::Avalonia.Point[result.WavePoints.Count];
        for (int i = 0; i < result.WavePoints.Count; i++)
        {
            var wp = result.WavePoints[i];
            screenPoints[i] = transform.ChartToScreen(new ChartPoint(wp.Time, wp.Price));
        }

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
        using var linePaint = new SKPaint
        {
            Color = lineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)ewObj.Thickness,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        path.MoveTo((float)screenPoints[0].X, (float)screenPoints[0].Y);
        for (int i = 1; i < screenPoints.Length; i++)
        {
            path.LineTo((float)screenPoints[i].X, (float)screenPoints[i].Y);
        }
        canvas.DrawPath(path, linePaint);

        // 2. Draw labels at each pivot point
        using var textPaint = new SKPaint
        {
            Color = labelColor,
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        using var circlePaint = new SKPaint
        {
            Color = new SKColor(labelColor.Red, labelColor.Green, labelColor.Blue, 60),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        for (int i = 0; i < screenPoints.Length && i < labels.Length; i++)
        {
            float x = (float)screenPoints[i].X;
            float y = (float)screenPoints[i].Y;

            // Draw a small circle at the pivot point
            canvas.DrawCircle(x, y, 4, circlePaint);

            // Position label above or below the pivot depending on whether it's a high or low
            bool isHigh = result.WavePoints[i].IsHigh;
            
            // Default offsets
            float labelX = x - textPaint.MeasureText(labels[i]) / 2;
            float labelY = isHigh ? y - 10 : y + 18;

            // Anti-overlap for corrective waves (A and C)
            if (!result.IsImpulse && (labels[i] == "A" || labels[i] == "C"))
            {
                // Push A to the left and C to the right slightly to avoid overlapping
                // if they are at similar price levels
                if (labels[i] == "A") labelX -= 12;
                if (labels[i] == "C") labelX += 12;
            }

            canvas.DrawText(labels[i], labelX, labelY, textPaint);
        }

        // 3. Draw confidence score text near the pattern center
        if (screenPoints.Length >= 2)
        {
            string type = result.IsImpulse ? "Impulse" : "Corrective";
            string scoreText = $"{type} ({(result.ConfidenceScore * 100):F0}%)";

            using var scorePaint = new SKPaint
            {
                Color = lineColor.WithAlpha(180),
                TextSize = 11,
                IsAntialias = true,
                Typeface = SKTypeface.Default
            };

            float centerX = (float)(screenPoints[0].X + screenPoints[screenPoints.Length - 1].X) / 2f;
            float topY = float.MaxValue;
            foreach (var sp in screenPoints)
                topY = Math.Min(topY, (float)sp.Y);

            canvas.DrawText(scoreText, centerX - scorePaint.MeasureText(scoreText) / 2, topY - 20, scorePaint);
        }
    }
}
