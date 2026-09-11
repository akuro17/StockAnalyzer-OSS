using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

/// <summary>
/// Renderer for Auto Time Cycle Lines drawing tool.
/// Executes layered SkiaSharp rendering with quantitative parameters and early loop termination.
/// </summary>
public sealed class AutoTimeCycleRenderer
{
    private static readonly float[] CycleDashPattern = new float[] { 4.0f, 4.0f };

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not AutoTimeCycleObject cycleObj || cycleObj.Points.Count < 2) return;

        var clip = canvas.LocalClipBounds;
        var p1 = transform.ChartToScreen(cycleObj.Points[0]);
        var p2 = transform.ChartToScreen(cycleObj.Points[1]);

        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var baseColor = cycleObj.SkiaColor;
        var fillColor = cycleObj.SkiaFillColor;

        // Z0: In-sample selection range background band (Fill Band)
        byte alpha = (byte)Math.Clamp((int)Math.Round(cycleObj.FillOpacity * 2.55), 0, 255);
        using (var bgPaint = new SKPaint
        {
            Color = fillColor.WithAlpha(alpha),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(new SKRect(left, clip.Top, right, clip.Bottom), bgPaint);
        }

        // Z1: Future cycle projection vertical lines (Dashed lines)
        if (cycleObj.IsCalculated)
        {
            using var cyclePaint = new SKPaint
            {
                Color = baseColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)cycleObj.Thickness,
                PathEffect = SKPathEffect.CreateDash(CycleDashPattern, 0.0f),
                IsAntialias = false
            };

            for (int i = 0; i < cycleObj.ProjectedBarIndices.Count; i++)
            {
                float projX = (float)transform.GetXFromIndex(cycleObj.ProjectedBarIndices[i]);

                // Loop early termination when exceeding viewport right boundary
                if (projX > clip.Right) break;

                if (projX >= clip.Left)
                {
                    canvas.DrawLine(projX, clip.Top, projX, clip.Bottom, cyclePaint);
                }
            }
        }

        // Z2: Selection boundary vertical lines at start (x1) and end (x2)
        using (var borderPaint = new SKPaint
        {
            Color = isSelected ? baseColor : baseColor.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)cycleObj.Thickness + 1.0f : (float)cycleObj.Thickness,
            IsAntialias = false
        })
        {
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, borderPaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, borderPaint);
        }

        // Z3: Selection handles on the vertical boundary lines if selected
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), cycleObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), cycleObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // Z4: Dominant cycle information badge
        if (cycleObj.ShowPeriodLabel && cycleObj.IsCalculated)
        {
            DrawPeriodBadge(canvas, clip, right, cycleObj.DominantPeriod, cycleObj.PowerShare);
        }
    }

    private static void DrawPeriodBadge(SKCanvas canvas, SKRect clip, float anchorX, double period, double powerShare)
    {
        string text = $"FFT Cycle: {period:F1}b ({powerShare:F0}%)";
        float fontSize = DrawingThemeContext.DetailFontSize > 0 ? DrawingThemeContext.DetailFontSize : 11.0f;
        using var textPaint = new SKPaint
        {
            Color = DrawingThemeContext.MainTextSkColor,
            TextSize = fontSize,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        float textWidth = textPaint.MeasureText(text);
        float padX = 6.0f;
        float padY = 3.0f;
        float totalBadgeWidth = textWidth + padX * 2.0f;
        float badgeHeight = fontSize + padY * 2.0f + 2.0f;
        float badgeY = clip.Top + 8.0f;

        // Default anchor: right side of the selection boundary line (+6px)
        float badgeX = anchorX + 6.0f;

        // If badge extends beyond right viewport boundary, flip to inside (left of anchor line)
        if (badgeX + totalBadgeWidth > clip.Right)
        {
            badgeX = anchorX - totalBadgeWidth - 6.0f;
        }

        // Clamp to left viewport boundary to prevent left-side clipping
        if (badgeX < clip.Left)
        {
            badgeX = clip.Left + 6.0f;
        }

        var badgeRect = new SKRect(badgeX, badgeY, badgeX + totalBadgeWidth, badgeY + badgeHeight);

        if (badgeRect.Right <= clip.Right && badgeRect.Left >= clip.Left)
        {
            var bgSkColor = new SKColor(
                DrawingThemeContext.AppBackgroundColor.R,
                DrawingThemeContext.AppBackgroundColor.G,
                DrawingThemeContext.AppBackgroundColor.B,
                210);

            using var bgPaint = new SKPaint
            {
                Color = bgSkColor,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var borderPaint = new SKPaint
            {
                Color = DrawingThemeContext.MainTextSkColor.WithAlpha(60),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.0f,
                IsAntialias = true
            };

            canvas.DrawRoundRect(badgeRect, 3.0f, 3.0f, bgPaint);
            canvas.DrawRoundRect(badgeRect, 3.0f, 3.0f, borderPaint);
            canvas.DrawText(text, badgeX + padX, badgeY + padY + fontSize - 1.0f, textPaint);
        }
    }
}
