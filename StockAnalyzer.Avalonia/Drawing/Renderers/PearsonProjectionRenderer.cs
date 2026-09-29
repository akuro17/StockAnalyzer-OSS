using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class PearsonProjectionRenderer
{
    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not PearsonProjectionObject pearsonObj || pearsonObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(pearsonObj.Points[0]);
        var p2 = transform.ChartToScreen(pearsonObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = pearsonObj.IsUnmatched ? pearsonObj.SkiaUnmatchedColor : pearsonObj.SkiaColor;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        using (var bgPaint = new SKPaint
        {
            Color = pearsonObj.SkiaFillColor.WithAlpha((byte)(255 * pearsonObj.FillOpacity / 100.0)),
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
            StrokeWidth = isSelected ? (float)pearsonObj.Thickness + 1 : (float)pearsonObj.Thickness,
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
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), pearsonObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), pearsonObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw Confidence Band (if enabled and present)
        if (pearsonObj.ShowConfidenceBand &&
            pearsonObj.UpperBandPath != null && pearsonObj.UpperBandPath.Count > 1 &&
            pearsonObj.LowerBandPath != null && pearsonObj.LowerBandPath.Count > 1 &&
            !pearsonObj.IsUnmatched)
        {
            using var bandPath = new SKPath();
            var firstUpper = pearsonObj.UpperBandPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            bandPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < pearsonObj.UpperBandPath.Count; i++)
            {
                var pt = pearsonObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = pearsonObj.LowerBandPath.Count - 1; i >= 0; i--)
            {
                var pt = pearsonObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            bandPath.Close();

            using (var bandFillPaint = new SKPaint
            {
                Color = pearsonObj.SkiaFillColor.WithAlpha((byte)(255 * pearsonObj.FillOpacity / 100.0)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            })
            {
                canvas.DrawPath(bandPath, bandFillPaint);
            }

            using var boundaryPaint = new SKPaint
            {
                Color = activeColor.WithAlpha(140),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, pearsonObj.Thickness * 0.75),
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
            };

            using var upperPath = new SKPath();
            upperPath.MoveTo((float)startScreen.X, (float)startScreen.Y);
            for (int i = 1; i < pearsonObj.UpperBandPath.Count; i++)
            {
                var pt = pearsonObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                upperPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(upperPath, boundaryPaint);

            var firstLower = pearsonObj.LowerBandPath[0];
            var lowerStartScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
            using var lowerPath = new SKPath();
            lowerPath.MoveTo((float)lowerStartScreen.X, (float)lowerStartScreen.Y);
            for (int i = 1; i < pearsonObj.LowerBandPath.Count; i++)
            {
                var pt = pearsonObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                lowerPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(lowerPath, boundaryPaint);
        }

        // 5. Draw Projected Path
        if (pearsonObj.ProjectedPath != null && pearsonObj.ProjectedPath.Count > 1 && !pearsonObj.IsUnmatched)
        {
            using var pathPaint = new SKPaint
            {
                Color = pearsonObj.SkiaColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = isSelected ? (float)pearsonObj.Thickness + 1.5f : (float)pearsonObj.Thickness + 0.5f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
            };

            using var path = new SKPath();
            var firstVal = pearsonObj.ProjectedPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstVal.X), (decimal)firstVal.Y));
            path.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < pearsonObj.ProjectedPath.Count; i++)
            {
                var val = pearsonObj.ProjectedPath[i];
                var screenPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)val.X), (decimal)val.Y));
                path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }

            canvas.DrawPath(path, pathPaint);
        }

        // 6. Draw Matched Pattern Highlight
        if (pearsonObj.ShowMatchHighlight &&
            pearsonObj.MatchedStartTime.HasValue &&
            pearsonObj.MatchedEndTime.HasValue &&
            !pearsonObj.IsUnmatched)
        {
            var matchX1 = (float)transform.ChartToScreen(new ChartPoint(pearsonObj.MatchedStartTime.Value, 0m)).X;
            var matchX2 = (float)transform.ChartToScreen(new ChartPoint(pearsonObj.MatchedEndTime.Value, 0m)).X;

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

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(0, 200, 100, 120),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
            };
            canvas.DrawRect(matchRect, borderPaint);
        }
    }
}
