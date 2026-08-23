using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class KalmanFilterProjectionRenderer
{
    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not KalmanFilterProjectionObject kalmanObj || kalmanObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(kalmanObj.Points[0]);
        var p2 = transform.ChartToScreen(kalmanObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = kalmanObj.SkiaColor;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        using (var bgPaint = new SKPaint
        {
            Color = kalmanObj.SkiaFillColor.WithAlpha((byte)(255 * kalmanObj.FillOpacity / 100.0)),
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
            StrokeWidth = isSelected ? (float)kalmanObj.Thickness + 1 : (float)kalmanObj.Thickness,
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
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), kalmanObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), kalmanObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw Confidence Band (if enabled)
        if (kalmanObj.ShowConfidenceBand &&
            kalmanObj.UpperBandPath != null && kalmanObj.UpperBandPath.Count > 1 &&
            kalmanObj.LowerBandPath != null && kalmanObj.LowerBandPath.Count > 1)
        {
            // Filled fan area
            using var bandPath = new SKPath();
            var firstUpper = kalmanObj.UpperBandPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            bandPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < kalmanObj.UpperBandPath.Count; i++)
            {
                var pt = kalmanObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = kalmanObj.LowerBandPath.Count - 1; i >= 0; i--)
            {
                var pt = kalmanObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            bandPath.Close();

            using (var bandFillPaint = new SKPaint
            {
                Color = kalmanObj.SkiaFillColor.WithAlpha((byte)(255 * kalmanObj.FillOpacity / 100.0)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            })
            {
                canvas.DrawPath(bandPath, bandFillPaint);
            }

            // Upper & Lower boundary dashed lines
            using var boundaryPaint = new SKPaint
            {
                Color = activeColor.WithAlpha(160),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, kalmanObj.Thickness * 0.75),
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
            };

            using var upperPath = new SKPath();
            upperPath.MoveTo((float)startScreen.X, (float)startScreen.Y);
            for (int i = 1; i < kalmanObj.UpperBandPath.Count; i++)
            {
                var pt = kalmanObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                upperPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(upperPath, boundaryPaint);

            var firstLower = kalmanObj.LowerBandPath[0];
            var lowerStartScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
            using var lowerPath = new SKPath();
            lowerPath.MoveTo((float)lowerStartScreen.X, (float)lowerStartScreen.Y);
            for (int i = 1; i < kalmanObj.LowerBandPath.Count; i++)
            {
                var pt = kalmanObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                lowerPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(lowerPath, boundaryPaint);
        }

        // 5. Draw Projected Path
        if (kalmanObj.ProjectedPath != null && kalmanObj.ProjectedPath.Count > 1)
        {
            using var pathPaint = new SKPaint
            {
                Color = kalmanObj.SkiaColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = isSelected ? (float)kalmanObj.Thickness + 1.5f : (float)kalmanObj.Thickness + 0.5f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
            };

            using var path = new SKPath();
            var firstVal = kalmanObj.ProjectedPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstVal.X), (decimal)firstVal.Y));
            path.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < kalmanObj.ProjectedPath.Count; i++)
            {
                var val = kalmanObj.ProjectedPath[i];
                var screenPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)val.X), (decimal)val.Y));
                path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }

            canvas.DrawPath(path, pathPaint);
        }
    }
}
