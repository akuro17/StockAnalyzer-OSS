using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class HoughParabolicCurveRenderer
{
    private readonly SKPath _path = new();
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 11f };
    private readonly SKPaint _textBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    public void InvalidateCache()
    {
        _path.Reset();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not HoughParabolicCurveObject drawing || drawing.Points.Count < 2) return;

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

        // 3. Render Parabolic Curves via exact quadratic NURBS
        var result = drawing.CalculatedResult;
        if (result != null && !result.IsEmpty)
        {
            _linePaint.Color = activeColor;
            _linePaint.StrokeWidth = (float)drawing.Thickness;
            _linePaint.StrokeJoin = SKStrokeJoin.Round;
            _linePaint.StrokeCap = SKStrokeCap.Round;

            int count = Math.Max(1, drawing.TotalSliceBars);
            int midBar = count / 2;

            foreach (var parabola in result.Parabolas)
            {
                decimal startPrice = parabola.StartPrice;
                decimal endPrice = parabola.EndPrice;
                decimal midPrice = parabola.GetPriceAt(midBar);
                decimal apexPrice = 2m * midPrice - (startPrice + endPrice) / 2m;

                var s0 = transform.ChartToScreen(new ChartPoint(drawing.SliceStartTime, startPrice));
                var s1 = transform.ChartToScreen(new ChartPoint(drawing.SliceMidTime, apexPrice));
                var s2 = transform.ChartToScreen(new ChartPoint(drawing.SliceEndTime, endPrice));

                Span<SKPoint> pts = stackalloc SKPoint[3]
                {
                    new((float)s0.X, (float)s0.Y),
                    new((float)s1.X, (float)s1.Y),
                    new((float)s2.X, (float)s2.Y)
                };

                _path.Reset();
                NurbsConicFactory.BuildParabolaPath(_path, pts);
                canvas.DrawPath(_path, _linePaint);

                if (drawing.ShowLabels)
                {
                    string label = $"{parabola.CurvatureSign} (R²={parabola.RSquared:F2}, {parabola.Votes}v)";
                    float textWidth = _textPaint.MeasureText(label);
                    float labelX = (float)s2.X + 6;
                    float labelY = (float)s2.Y;

                    var badgeRect = new SKRect(labelX - 2, labelY - 12, labelX + textWidth + 4, labelY + 4);
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
