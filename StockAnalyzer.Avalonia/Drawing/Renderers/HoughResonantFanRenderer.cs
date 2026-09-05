using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class HoughResonantFanRenderer
{
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _originPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 10f };
    private readonly SKPaint _textBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    public void InvalidateCache()
    {
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HoughResonantFanObject drawing || drawing.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(drawing.Points[0]);
        var p2 = transform.ChartToScreen(drawing.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = drawing.SkiaColor;
        _textPaint.TextSize = DrawingThemeContext.FontSize;

        // 1. Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        _fillPaint.Color = drawing.SkiaFillColor.WithAlpha((byte)(255 * drawing.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _fillPaint);

        // 2. Vertical Bracket Lines
        _linePaint.Color = isSelected ? activeColor : activeColor.WithAlpha(180);
        _linePaint.StrokeWidth = isSelected ? (float)drawing.Thickness + 1 : (float)drawing.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _linePaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _linePaint);

        // 3. Render Resonant Rays
        if (drawing.CalculatedFanRays.Count > 0)
        {
            var originPt = transform.ChartToScreen(new ChartPoint(drawing.OriginTime, drawing.OriginPrice));
            float ox = (float)originPt.X;
            float oy = (float)originPt.Y;

            // Draw Origin Marker
            _originPaint.Color = activeColor;
            canvas.DrawCircle(ox, oy, 4f, _originPaint);

            foreach (var ray in drawing.CalculatedFanRays)
            {
                decimal endPrice = drawing.OriginPrice + (decimal)(ray.SlopePrice * (drawing.TotalSliceBars - 1));
                var endPt = transform.ChartToScreen(new ChartPoint(drawing.SliceEndTime, endPrice));
                float ex = (float)endPt.X;
                float ey = (float)endPt.Y;

                byte alpha = (byte)Math.Clamp(100 + ray.Votes * 25, 100, 255);
                _linePaint.Color = activeColor.WithAlpha(alpha);
                _linePaint.StrokeWidth = (float)drawing.Thickness;

                float finalX = ex;
                float finalY = ey;

                if (drawing.ExtendRight && ex < transform.CanvasWidth && Math.Abs(ex - ox) > 1e-3)
                {
                    double m = (ey - oy) / (ex - ox);
                    finalX = (float)transform.CanvasWidth;
                    finalY = (float)(oy + m * (finalX - ox));
                }

                canvas.DrawLine(ox, oy, finalX, finalY, _linePaint);

                if (drawing.ShowLabels)
                {
                    string label = $"{ray.AngleDegrees:F1}° ({ray.Votes})";
                    float textWidth = _textPaint.MeasureText(label);
                    float labelX = drawing.ExtendRight ? finalX - textWidth - 6 : finalX + 4;
                    float labelY = finalY;

                    var badgeRect = new SKRect(labelX - 2, labelY - 10, labelX + textWidth + 4, labelY + 4);
                    _textBgPaint.Color = SKColors.Black.WithAlpha(160);
                    canvas.DrawRoundRect(badgeRect, 2, 2, _textBgPaint);

                    _textPaint.Color = activeColor;
                    canvas.DrawText(label, labelX, labelY, _textPaint);
                }
            }
        }

        // 4. Selection Handles
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), drawing.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), drawing.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : null);
        }
    }
}
