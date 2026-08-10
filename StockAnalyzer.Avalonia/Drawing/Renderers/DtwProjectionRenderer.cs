using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class DtwProjectionRenderer
{

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not DtwProjectionObject dtwObj || dtwObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(dtwObj.Points[0]);
        var p2 = transform.ChartToScreen(dtwObj.Points[1]);

        var rect = new SKRect(
            (float)Math.Min(p1.X, p2.X),
            (float)Math.Min(p1.Y, p2.Y),
            (float)Math.Max(p1.X, p2.X),
            (float)Math.Max(p1.Y, p2.Y));

        // Draw Selection Rectangle Background
        using (var bgPaint = new SKPaint
        {
            Color = dtwObj.SkiaColor.WithAlpha(30),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, bgPaint);
        }

        // Draw Selection Rectangle Border
        using (var borderPaint = new SKPaint
        {
            Color = dtwObj.SkiaColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)dtwObj.Thickness + 1 : (float)dtwObj.Thickness,
            IsAntialias = true
        })
        {
            canvas.DrawRect(rect, borderPaint);
            
            // Draw Handles if selected at the 4 corners
            if (isSelected)
            {
                var handlePaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
                float r = (float)dtwObj.HandleSize;
                
                canvas.DrawCircle(rect.Left, rect.Top, r, handlePaint);
                canvas.DrawCircle(rect.Right, rect.Top, r, handlePaint);
                canvas.DrawCircle(rect.Left, rect.Bottom, r, handlePaint);
                canvas.DrawCircle(rect.Right, rect.Bottom, r, handlePaint);
            }
        }

        // Draw Projected Path
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

        // Draw Matched Pattern Highlight
        if (dtwObj.MatchedStartTime.HasValue && dtwObj.MatchedEndTime.HasValue)
        {
            // Convert match timestamps to screen coordinates
            // Use min/max price from the selection to span the full chart height
            var matchP1 = transform.ChartToScreen(
                new ChartPoint(dtwObj.MatchedStartTime.Value, decimal.MaxValue / 2));
            var matchP2 = transform.ChartToScreen(
                new ChartPoint(dtwObj.MatchedEndTime.Value, decimal.MinValue / 2));

            var matchRect = new SKRect(
                (float)Math.Min(matchP1.X, matchP2.X),
                (float)Math.Min(matchP1.Y, matchP2.Y),
                (float)Math.Max(matchP1.X, matchP2.X),
                (float)Math.Max(matchP1.Y, matchP2.Y));

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
