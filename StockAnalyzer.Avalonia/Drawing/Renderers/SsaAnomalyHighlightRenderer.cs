using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class SsaAnomalyHighlightRenderer
{
    private SKRect _highlightRect;
    private readonly SKPaint _highlightPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _boundaryUpperPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 5f, 5f }, 0)
    };
    private readonly SKPaint _boundaryLowerPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 5f, 5f }, 0)
    };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 11f, TextAlign = SKTextAlign.Center };
    private readonly SKPaint _badgeBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _structuralPath = new();
    private readonly SKPath _upperBandPath = new();
    private readonly SKPath _lowerBandPath = new();
    private readonly SKRoundRect _badgeRoundRect = new();

    public void InvalidateCache()
    {
        _structuralPath.Reset();
        _upperBandPath.Reset();
        _lowerBandPath.Reset();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not SsaAnomalyHighlightObject drawing || drawing.Points.Count < 2) return;

        var clip = canvas.LocalClipBounds;
        var p1 = transform.ChartToScreen(drawing.Points[0]);
        var p2 = transform.ChartToScreen(drawing.Points[1]);

        float selLeft = (float)Math.Min(p1.X, p2.X);
        float selRight = (float)Math.Max(p1.X, p2.X);

        var activeColor = drawing.SkiaColor;

        // 1. Vertical range boundary lines of selection
        _linePaint.Color = isSelected ? activeColor : activeColor.WithAlpha(160);
        _linePaint.StrokeWidth = isSelected ? (float)drawing.Thickness + 1f : (float)drawing.Thickness;
        canvas.DrawLine(selLeft, clip.Top, selLeft, clip.Bottom, _linePaint);
        canvas.DrawLine(selRight, clip.Top, selRight, clip.Bottom, _linePaint);

        // 2. Selection handles
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(selLeft, midY), drawing.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(selRight, midY), drawing.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        var result = drawing.CalculatedResult;
        if (result == null || result.IsEmpty) return;

        // 3. Z=1: Anomaly intervals highlight rectangles (Full vertical span)
        byte alpha = (byte)Math.Clamp((int)(255 * (drawing.HighlightOpacity / 100.0)), 10, 255);
        for (int i = 0; i < result.Intervals.Count; i++)
        {
            var interval = result.Intervals[i];
            float x1 = (float)transform.ChartToScreen(new ChartPoint(interval.StartTime, 0)).X;
            float x2 = (float)transform.ChartToScreen(new ChartPoint(interval.EndTime, 0)).X;

            float left = Math.Min(x1, x2);
            float right = Math.Max(x1, x2);
            float width = right - left;

            // Symmetrical minimum width 4px guarantee
            if (width < 4f)
            {
                float center = (left + right) * 0.5f;
                left = center - 2f;
                right = center + 2f;
            }

            var baseCol = interval.Direction == SsaAnomalyDirection.Bullish
                ? drawing.SkiaBullishColor
                : drawing.SkiaBearishColor;

            _highlightPaint.Color = baseCol.WithAlpha(alpha);
            _highlightRect = new SKRect(left, clip.Top, right, clip.Bottom);
            canvas.DrawRect(_highlightRect, _highlightPaint);
        }

        // 4. Z=2: Normal Structural Component Line
        if (drawing.ShowStructuralLine && result.ReconstructedPoints.Count > 1)
        {
            _structuralPath.Reset();
            var first = result.ReconstructedPoints[0];
            var sFirst = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            _structuralPath.MoveTo((float)sFirst.X, (float)sFirst.Y);

            for (int i = 1; i < result.ReconstructedPoints.Count; i++)
            {
                var pt = result.ReconstructedPoints[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _structuralPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _linePaint.Color = drawing.SkiaStructuralColor;
            _linePaint.StrokeWidth = (float)drawing.Thickness;
            canvas.DrawPath(_structuralPath, _linePaint);
        }

        // 5. Z=3: Boundary Bands (±k_enter * sigma_res)
        if (drawing.ShowBoundaryBands && result.UpperBandPoints.Count > 1 && result.LowerBandPoints.Count > 1)
        {
            _upperBandPath.Reset();
            var firstU = result.UpperBandPoints[0];
            var sFirstU = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstU.X), (decimal)firstU.Y));
            _upperBandPath.MoveTo((float)sFirstU.X, (float)sFirstU.Y);

            for (int i = 1; i < result.UpperBandPoints.Count; i++)
            {
                var pt = result.UpperBandPoints[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _upperBandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _boundaryUpperPaint.Color = drawing.SkiaBearishColor.WithAlpha(200);
            _boundaryUpperPaint.StrokeWidth = 1.2f;
            canvas.DrawPath(_upperBandPath, _boundaryUpperPaint);

            _lowerBandPath.Reset();
            var firstL = result.LowerBandPoints[0];
            var sFirstL = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstL.X), (decimal)firstL.Y));
            _lowerBandPath.MoveTo((float)sFirstL.X, (float)sFirstL.Y);

            for (int i = 1; i < result.LowerBandPoints.Count; i++)
            {
                var pt = result.LowerBandPoints[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _lowerBandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _boundaryLowerPaint.Color = drawing.SkiaBullishColor.WithAlpha(200);
            _boundaryLowerPaint.StrokeWidth = 1.2f;
            canvas.DrawPath(_lowerBandPath, _boundaryLowerPaint);
        }

        // 6. Z=4: Anomaly Peak Badges (Dynamic font from Settings/Fonts: DetailFontSize)
        if (drawing.ShowAnomalyBadges && result.Intervals.Count > 0)
        {
            float fontSize = DrawingThemeContext.FontSize;
            _textPaint.TextSize = fontSize;
            var fontMetrics = _textPaint.FontMetrics;
            float textHeight = fontMetrics.Descent - fontMetrics.Ascent;
            float padX = Math.Max(5f, fontSize * 0.4f);
            float padY = Math.Max(2f, fontSize * 0.15f);
            float badgeHeight = textHeight + padY * 2;
            float badgeOffsetY = badgeHeight * 0.5f + 4f;

            for (int i = 0; i < result.Intervals.Count; i++)
            {
                var interval = result.Intervals[i];
                int peakIdx = interval.PeakIndex;
                if (peakIdx >= 0 && peakIdx < result.ReconstructedPoints.Count)
                {
                    var pt = result.ReconstructedPoints[peakIdx];
                    var screenPt = transform.ChartToScreen(new ChartPoint(interval.PeakTime, (decimal)(pt.Y + interval.MaxPriceDeviation)));

                    float badgeX = (float)screenPt.X;
                    float badgeY = interval.Direction == SsaAnomalyDirection.Bullish
                        ? (float)screenPt.Y - badgeOffsetY
                        : (float)screenPt.Y + badgeOffsetY;

                    string badgeText = interval.BadgeText;
                    float textWidth = _textPaint.MeasureText(badgeText);
                    float badgeWidth = textWidth + padX * 2;

                    var badgeRect = new SKRect(badgeX - badgeWidth * 0.5f, badgeY - badgeHeight * 0.5f, badgeX + badgeWidth * 0.5f, badgeY + badgeHeight * 0.5f);
                    _badgeRoundRect.SetRect(badgeRect, 3f, 3f);

                    var badgeColor = interval.Direction == SsaAnomalyDirection.Bullish
                        ? drawing.SkiaBullishColor
                        : drawing.SkiaBearishColor;

                    _badgeBgPaint.Color = badgeColor.WithAlpha(220);
                    canvas.DrawRoundRect(_badgeRoundRect, _badgeBgPaint);

                    _textPaint.Color = SKColors.White;
                    float textBaselineY = badgeY - (fontMetrics.Ascent + fontMetrics.Descent) * 0.5f;
                    canvas.DrawText(badgeText, badgeX, textBaselineY, _textPaint);
                }
            }
        }
    }
}
