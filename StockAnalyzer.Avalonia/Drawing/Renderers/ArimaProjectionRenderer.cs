using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class ArimaProjectionRenderer
{
    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not ArimaProjectionObject arimaObj || arimaObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(arimaObj.Points[0]);
        var p2 = transform.ChartToScreen(arimaObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = arimaObj.SkiaColor;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        using (var bgPaint = new SKPaint
        {
            Color = arimaObj.SkiaFillColor.WithAlpha((byte)(255 * arimaObj.FillOpacity / 100.0)),
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
            StrokeWidth = isSelected ? (float)arimaObj.Thickness + 1 : (float)arimaObj.Thickness,
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
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), arimaObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), arimaObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw Confidence Band (if enabled)
        if (arimaObj.ShowConfidenceBand &&
            arimaObj.UpperBandPath != null && arimaObj.UpperBandPath.Count > 1 &&
            arimaObj.LowerBandPath != null && arimaObj.LowerBandPath.Count > 1)
        {
            // Filled fan area
            using var bandPath = new SKPath();
            var firstUpper = arimaObj.UpperBandPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            bandPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < arimaObj.UpperBandPath.Count; i++)
            {
                var pt = arimaObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = arimaObj.LowerBandPath.Count - 1; i >= 0; i--)
            {
                var pt = arimaObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            bandPath.Close();

            using (var bandFillPaint = new SKPaint
            {
                Color = arimaObj.SkiaFillColor.WithAlpha((byte)(255 * arimaObj.FillOpacity / 100.0)),
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
                StrokeWidth = (float)Math.Max(1.0, arimaObj.Thickness * 0.75),
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
            };

            using var upperPath = new SKPath();
            upperPath.MoveTo((float)startScreen.X, (float)startScreen.Y);
            for (int i = 1; i < arimaObj.UpperBandPath.Count; i++)
            {
                var pt = arimaObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                upperPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(upperPath, boundaryPaint);

            var firstLower = arimaObj.LowerBandPath[0];
            var lowerStartScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
            using var lowerPath = new SKPath();
            lowerPath.MoveTo((float)lowerStartScreen.X, (float)lowerStartScreen.Y);
            for (int i = 1; i < arimaObj.LowerBandPath.Count; i++)
            {
                var pt = arimaObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                lowerPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(lowerPath, boundaryPaint);
        }

        // 5. Draw Projected Path
        if (arimaObj.ProjectedPath != null && arimaObj.ProjectedPath.Count > 1)
        {
            using var pathPaint = new SKPaint
            {
                Color = arimaObj.SkiaColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = isSelected ? (float)arimaObj.Thickness + 1.5f : (float)arimaObj.Thickness + 0.5f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
            };

            using var path = new SKPath();
            var firstVal = arimaObj.ProjectedPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstVal.X), (decimal)firstVal.Y));
            path.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < arimaObj.ProjectedPath.Count; i++)
            {
                var val = arimaObj.ProjectedPath[i];
                var screenPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)val.X), (decimal)val.Y));
                path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }

            canvas.DrawPath(path, pathPaint);
        }
    }
}
