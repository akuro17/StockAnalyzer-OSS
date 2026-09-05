using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class HoughKeyLevelsRenderer
{
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _dashedPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 11f };
    private readonly SKPaint _textBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private static readonly float[] DashPattern = [6f, 4f];
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash(DashPattern, 0);

    public HoughKeyLevelsRenderer()
    {
        _dashedPaint.PathEffect = DashEffect;
    }

    public void InvalidateCache()
    {
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HoughKeyLevelsObject drawing || drawing.Points.Count < 2) return;

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

        // 3. Render Key Levels
        if (drawing.CalculatedLevels.Count > 0)
        {
            float xStart = left;
            float xEnd = drawing.ExtendRight ? (float)transform.CanvasWidth : right;
            decimal atrBand = (decimal)(drawing.CalculatedAtr * (double)drawing.BandAtrMultiplier);

            foreach (var level in drawing.CalculatedLevels)
            {
                var pricePoint = new ChartPoint(drawing.SliceStartTime, level.StartPrice);
                var screenPt = transform.ChartToScreen(pricePoint);
                float y = (float)screenPt.Y;

                var upperPt = transform.ChartToScreen(new ChartPoint(drawing.SliceStartTime, level.StartPrice + atrBand));
                var lowerPt = transform.ChartToScreen(new ChartPoint(drawing.SliceStartTime, level.StartPrice - atrBand));
                float yTop = Math.Min((float)upperPt.Y, (float)lowerPt.Y);
                float yBot = Math.Max((float)upperPt.Y, (float)lowerPt.Y);

                SKColor col = level.LineType == HoughLineType.Support
                    ? drawing.SkiaSupportColor
                    : drawing.SkiaResistanceColor;

                // Subtle ATR band
                _fillPaint.Color = col.WithAlpha(25);
                canvas.DrawRect(new SKRect(xStart, yTop, xEnd, yBot), _fillPaint);

                // Horizontal dashed level line
                _dashedPaint.Color = col;
                _dashedPaint.StrokeWidth = (float)drawing.Thickness;
                canvas.DrawLine(xStart, y, xEnd, y, _dashedPaint);

                // Label badge
                if (drawing.ShowLabels)
                {
                    string label = $"{level.LineType} {level.StartPrice:F2} (T:{level.TouchCount})";
                    float textWidth = _textPaint.MeasureText(label);
                    float fontSize = _textPaint.TextSize;
                    float labelX = drawing.ExtendRight ? xEnd - textWidth - 8 : xEnd + 6;
                    float labelY = y - 4f; // Above horizontal line per common specification
                    var badgeRect = new SKRect(labelX - 4, labelY - fontSize - 2, labelX + textWidth + 4, labelY + 2);
                    _textBgPaint.Color = SKColors.Black.WithAlpha(160);
                    canvas.DrawRoundRect(badgeRect, 2, 2, _textBgPaint);

                    _textPaint.Color = col;
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
