using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class HoughAutoLinesRenderer
{
    private readonly SKPath _path = new();
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _dashedPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 11f };
    private readonly SKPaint _textBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private static readonly float[] DashPattern = [5f, 5f];
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash(DashPattern, 0);

    public HoughAutoLinesRenderer()
    {
        _dashedPaint.PathEffect = DashEffect;
    }

    public void InvalidateCache()
    {
        _path.Reset();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HoughAutoLinesObject drawing || drawing.Points.Count < 2) return;

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

        // 3. Render Detected Hough Lines
        var result = drawing.CalculatedResult;
        if (result != null && result.Lines.Count > 0)
        {
            var tStart = drawing.SliceStartTime;
            var tEnd = drawing.SliceEndTime;

            // Optional channel bands
            if (drawing.ShowChannels && result.Channels.Count > 0)
            {
                _fillPaint.Color = activeColor.WithAlpha(20);
                foreach (var ch in result.Channels)
                {
                    bool chIsTrend = ch.UpperLine.LineType == HoughLineType.TrendUp || ch.UpperLine.LineType == HoughLineType.TrendDown;
                    bool chIsSupport = ch.UpperLine.LineType == HoughLineType.Support || ch.LowerLine.LineType == HoughLineType.Support;
                    bool chIsResistance = ch.UpperLine.LineType == HoughLineType.Resistance || ch.LowerLine.LineType == HoughLineType.Resistance;

                    if (chIsTrend && !drawing.ShowTrendLines) continue;
                    if (chIsSupport && !drawing.ShowSupportLines) continue;
                    if (chIsResistance && !drawing.ShowResistanceLines) continue;

                    var u1 = transform.ChartToScreen(new ChartPoint(tStart, ch.UpperLine.StartPrice));
                    var u2 = transform.ChartToScreen(new ChartPoint(tEnd, ch.UpperLine.EndPrice));
                    var l1 = transform.ChartToScreen(new ChartPoint(tStart, ch.LowerLine.StartPrice));
                    var l2 = transform.ChartToScreen(new ChartPoint(tEnd, ch.LowerLine.EndPrice));

                    _path.Reset();
                    _path.MoveTo((float)u1.X, (float)u1.Y);
                    _path.LineTo((float)u2.X, (float)u2.Y);
                    _path.LineTo((float)l2.X, (float)l2.Y);
                    _path.LineTo((float)l1.X, (float)l1.Y);
                    _path.Close();
                    canvas.DrawPath(_path, _fillPaint);
                }
            }

            foreach (var line in result.Lines)
            {
                bool isTrend = line.LineType == HoughLineType.TrendUp || line.LineType == HoughLineType.TrendDown;
                bool isSupport = line.LineType == HoughLineType.Support;
                bool isResistance = line.LineType == HoughLineType.Resistance;

                if (isTrend && !drawing.ShowTrendLines) continue;
                if (isSupport && !drawing.ShowSupportLines) continue;
                if (isResistance && !drawing.ShowResistanceLines) continue;

                var s1 = transform.ChartToScreen(new ChartPoint(tStart, line.StartPrice));
                var s2 = transform.ChartToScreen(new ChartPoint(tEnd, line.EndPrice));

                SKColor lineCol = isTrend ? drawing.SkiaTrendLineColor :
                                  isSupport ? drawing.SkiaSupportColor :
                                  isResistance ? drawing.SkiaResistanceColor :
                                  activeColor;

                _linePaint.Color = lineCol;
                _linePaint.StrokeWidth = (float)drawing.Thickness;
                canvas.DrawLine((float)s1.X, (float)s1.Y, (float)s2.X, (float)s2.Y, _linePaint);

                // Right extension
                if (drawing.ExtendLinesToRight && s2.X < transform.CanvasWidth && Math.Abs(s2.X - s1.X) > 1e-3)
                {
                    double m = (s2.Y - s1.Y) / (s2.X - s1.X);
                    float targetX = (float)transform.CanvasWidth;
                    float targetY = (float)(s2.Y + m * (targetX - s2.X));

                    _dashedPaint.Color = lineCol.WithAlpha(160);
                    _dashedPaint.StrokeWidth = (float)drawing.Thickness;
                    canvas.DrawLine((float)s2.X, (float)s2.Y, targetX, targetY, _dashedPaint);
                }

                // Label badge
                if (drawing.ShowLabels)
                {
                    string label = $"{line.LineType} T:{line.TouchCount} R²:{line.RSquared:F2}";
                    float textWidth = _textPaint.MeasureText(label);
                    float textX = (float)s2.X + 6;
                    float textY = (float)s2.Y;

                    var badgeRect = new SKRect(textX - 2, textY - 12, textX + textWidth + 4, textY + 4);
                    _textBgPaint.Color = SKColors.Black.WithAlpha(160);
                    canvas.DrawRoundRect(badgeRect, 2, 2, _textBgPaint);

                    _textPaint.Color = lineCol;
                    canvas.DrawText(label, textX, textY, _textPaint);
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
