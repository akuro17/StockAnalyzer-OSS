using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class ClothoidCurveProjectionRenderer
{
    private readonly SKPath _bandPath = new();
    private readonly SKPath _upperPath = new();
    private readonly SKPath _lowerPath = new();
    private readonly SKPath _projectedPath = new();

    private readonly SKPaint _bgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = false };
    private readonly SKPaint _bandFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _boundaryPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _pathPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    private static readonly float[] BoundaryDash = [3f, 3f];
    private static readonly SKPathEffect BoundaryDashEffect = SKPathEffect.CreateDash(BoundaryDash, 0);

    private static readonly float[] PathDash = [5f, 5f];
    private static readonly SKPathEffect PathDashEffect = SKPathEffect.CreateDash(PathDash, 0);

    public ClothoidCurveProjectionRenderer()
    {
        _boundaryPaint.PathEffect = BoundaryDashEffect;
        _pathPaint.PathEffect = PathDashEffect;
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not ClothoidCurveProjectionObject clothoidObj || clothoidObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(clothoidObj.Points[0]);
        var p2 = transform.ChartToScreen(clothoidObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = clothoidObj.SkiaColor;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        _bgPaint.Color = clothoidObj.SkiaFillColor.WithAlpha((byte)(255 * clothoidObj.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _bgPaint);

        // 2. Draw Vertical Lines at Start (x1) and End (x2)
        _linePaint.Color = isSelected ? activeColor : activeColor.WithAlpha(180);
        _linePaint.StrokeWidth = isSelected ? (float)clothoidObj.Thickness + 1 : (float)clothoidObj.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _linePaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _linePaint);

        // 3. Draw Handles on the vertical lines if selected
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), clothoidObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), clothoidObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw Confidence Band (if enabled)
        if (clothoidObj.ShowConfidenceBand &&
            clothoidObj.UpperBandPath != null && clothoidObj.UpperBandPath.Count > 1 &&
            clothoidObj.LowerBandPath != null && clothoidObj.LowerBandPath.Count > 1)
        {
            _bandPath.Reset();
            var firstUpper = clothoidObj.UpperBandPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            _bandPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < clothoidObj.UpperBandPath.Count; i++)
            {
                var pt = clothoidObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = clothoidObj.LowerBandPath.Count - 1; i >= 0; i--)
            {
                var pt = clothoidObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            _bandPath.Close();

            _bandFillPaint.Color = clothoidObj.SkiaFillColor.WithAlpha((byte)(255 * clothoidObj.FillOpacity / 100.0));
            canvas.DrawPath(_bandPath, _bandFillPaint);

            // Upper & Lower boundary dashed lines
            _boundaryPaint.Color = activeColor.WithAlpha(160);
            _boundaryPaint.StrokeWidth = (float)Math.Max(1.0, clothoidObj.Thickness * 0.75);

            _upperPath.Reset();
            _upperPath.MoveTo((float)startScreen.X, (float)startScreen.Y);
            for (int i = 1; i < clothoidObj.UpperBandPath.Count; i++)
            {
                var pt = clothoidObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _upperPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(_upperPath, _boundaryPaint);

            var firstLower = clothoidObj.LowerBandPath[0];
            var lowerStartScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
            _lowerPath.Reset();
            _lowerPath.MoveTo((float)lowerStartScreen.X, (float)lowerStartScreen.Y);
            for (int i = 1; i < clothoidObj.LowerBandPath.Count; i++)
            {
                var pt = clothoidObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _lowerPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(_lowerPath, _boundaryPaint);
        }

        // 5. Draw Projected Path
        if (clothoidObj.ProjectedPath != null && clothoidObj.ProjectedPath.Count > 1)
        {
            _pathPaint.Color = clothoidObj.SkiaColor;
            _pathPaint.StrokeWidth = isSelected ? (float)clothoidObj.Thickness + 1.5f : (float)clothoidObj.Thickness + 0.5f;

            _projectedPath.Reset();
            var firstVal = clothoidObj.ProjectedPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstVal.X), (decimal)firstVal.Y));
            _projectedPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < clothoidObj.ProjectedPath.Count; i++)
            {
                var val = clothoidObj.ProjectedPath[i];
                var screenPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)val.X), (decimal)val.Y));
                _projectedPath.LineTo((float)screenPt.X, (float)screenPt.Y);
            }

            canvas.DrawPath(_projectedPath, _pathPaint);
        }
    }
}
