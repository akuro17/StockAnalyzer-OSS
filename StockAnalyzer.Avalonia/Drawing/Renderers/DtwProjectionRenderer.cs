using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class DtwProjectionRenderer
{

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not DtwProjectionObject dtwObj || dtwObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(dtwObj.Points[0]);
        var p2 = transform.ChartToScreen(dtwObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        // Choose color based on match status: only switch to unmatched color if search completed with no match
        var activeColor = dtwObj.IsUnmatched ? dtwObj.SkiaUnmatchedColor : dtwObj.SkiaColor;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        using (var bgPaint = new SKPaint
        {
            Color = dtwObj.SkiaFillColor.WithAlpha((byte)(255 * dtwObj.FillOpacity / 100.0)),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(bandRect, bgPaint);
        }

        // 2. Draw Vertical Lines at Start (x1) and End (x2)
        using (var linePaint = new SKPaint
        {
            Color = isSelected ? activeColor : activeColor.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)dtwObj.Thickness + 1 : (float)dtwObj.Thickness,
            IsAntialias = false
        })
        {
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, linePaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, linePaint);
        }

        // 3. Draw Handles on the vertical lines if selected
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), dtwObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), dtwObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw Projected Path
        if (dtwObj.ProjectedPath != null && dtwObj.ProjectedPath.Count > 1)
        {
            using var pathPaint = new SKPaint
            {
                Color = dtwObj.SkiaColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.0f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
            };

            using var path = new SKPath();
            var firstVal = dtwObj.ProjectedPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstVal.X), (decimal)firstVal.Y));
            path.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < dtwObj.ProjectedPath.Count; i++)
            {
                var val = dtwObj.ProjectedPath[i];
                var screenPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)val.X), (decimal)val.Y));
                path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }

            canvas.DrawPath(path, pathPaint);
        }

        // 5. Draw Matched Pattern Highlight
        if (dtwObj.MatchedStartTime.HasValue && dtwObj.MatchedEndTime.HasValue)
        {
            var matchX1 = (float)transform.ChartToScreen(new ChartPoint(dtwObj.MatchedStartTime.Value, 0m)).X;
            var matchX2 = (float)transform.ChartToScreen(new ChartPoint(dtwObj.MatchedEndTime.Value, 0m)).X;

            var matchRect = new SKRect(
                Math.Min(matchX1, matchX2),
                clip.Top,
                Math.Max(matchX1, matchX2),
                clip.Bottom);

            using var highlightPaint = new SKPaint
            {
                Color = new SKColor(0, 200, 100, 35), // Green with low alpha
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };
            canvas.DrawRect(matchRect, highlightPaint);

            // Draw border for the matched region
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(0, 200, 100, 100),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
            };
            canvas.DrawRect(matchRect, borderPaint);
        }
    }
}
