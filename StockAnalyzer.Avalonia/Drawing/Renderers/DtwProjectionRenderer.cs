using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class DtwProjectionRenderer
{
    [ThreadStatic]
    private static ProjectionRenderScratch? t_scratch;

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not DtwProjectionObject dtwObj || dtwObj.Points.Count < 2) return;

        var scratch = t_scratch ??= new ProjectionRenderScratch(ChartTheme.DtwProjectedPathDashLength);

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
        var fillPaint = scratch.FillPaint;
        fillPaint.Color = dtwObj.SkiaFillColor.WithAlpha((byte)(255 * dtwObj.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, fillPaint);

        // 2. Draw Vertical Lines at Start (x1) and End (x2)
        var linePaint = scratch.RangeLinePaint;
        linePaint.Color = isSelected ? activeColor : activeColor.WithAlpha(180);
        linePaint.StrokeWidth = isSelected ? (float)dtwObj.Thickness + 1 : (float)dtwObj.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, linePaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, linePaint);

        // 3. Draw Handles on the vertical lines if selected
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), dtwObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), dtwObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }

        // 4. Draw DBA 15%-85% Ensemble Fan Band (zero per-frame allocations via ProjectionRenderScratch)
        if (dtwObj.UseDbaEnsemble &&
            !dtwObj.IsUnmatched &&
            dtwObj.UpperBandPath != null && dtwObj.UpperBandPath.Count > 1 &&
            dtwObj.LowerBandPath != null && dtwObj.LowerBandPath.Count > 1)
        {
            var bandPath = scratch.BandPath;
            bandPath.Reset();

            var firstUpper = dtwObj.UpperBandPath[0];
            var startUpperScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            bandPath.MoveTo((float)startUpperScreen.X, (float)startUpperScreen.Y);

            for (int i = 1; i < dtwObj.UpperBandPath.Count; i++)
            {
                var pt = dtwObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = dtwObj.LowerBandPath.Count - 1; i >= 0; i--)
            {
                var pt = dtwObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            bandPath.Close();

            fillPaint.Color = dtwObj.SkiaFillColor.WithAlpha((byte)(255 * dtwObj.FillOpacity / 100.0));
            canvas.DrawPath(bandPath, fillPaint);

            var boundaryPaint = scratch.BoundaryPaint;
            boundaryPaint.Color = activeColor.WithAlpha(140);
            boundaryPaint.StrokeWidth = (float)Math.Max(1.0, dtwObj.Thickness * 0.75);

            var upperPath = scratch.UpperPath;
            upperPath.Reset();
            upperPath.MoveTo((float)startUpperScreen.X, (float)startUpperScreen.Y);
            for (int i = 1; i < dtwObj.UpperBandPath.Count; i++)
            {
                var pt = dtwObj.UpperBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                upperPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(upperPath, boundaryPaint);

            var firstLower = dtwObj.LowerBandPath[0];
            var startLowerScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
            var lowerPath = scratch.LowerPath;
            lowerPath.Reset();
            lowerPath.MoveTo((float)startLowerScreen.X, (float)startLowerScreen.Y);
            for (int i = 1; i < dtwObj.LowerBandPath.Count; i++)
            {
                var pt = dtwObj.LowerBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                lowerPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(lowerPath, boundaryPaint);
        }

        // 5. Draw Projected Path
        if (dtwObj.ProjectedPath != null && dtwObj.ProjectedPath.Count > 1)
        {
            var pathPaint = scratch.PathPaint;
            pathPaint.Color = dtwObj.SkiaColor;
            pathPaint.StrokeWidth = 2.0f;

            var path = scratch.ProjectedPath;
            path.Reset();
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

        // 6. Draw Matched Pattern Highlight
        if (dtwObj.MatchedStartTime.HasValue && dtwObj.MatchedEndTime.HasValue)
        {
            var matchX1 = (float)transform.ChartToScreen(new ChartPoint(dtwObj.MatchedStartTime.Value, 0m)).X;
            var matchX2 = (float)transform.ChartToScreen(new ChartPoint(dtwObj.MatchedEndTime.Value, 0m)).X;

            var matchRect = new SKRect(
                Math.Min(matchX1, matchX2),
                clip.Top,
                Math.Max(matchX1, matchX2),
                clip.Bottom);

            var highlightPaint = scratch.HighlightFillPaint;
            highlightPaint.Color = new SKColor(0, 200, 100, 35); // Green with low alpha
            canvas.DrawRect(matchRect, highlightPaint);

            var borderPaint = scratch.HighlightBorderPaint;
            borderPaint.Color = new SKColor(0, 200, 100, 100);
            borderPaint.StrokeWidth = 1.0f;
            canvas.DrawRect(matchRect, borderPaint);
        }
    }
}
