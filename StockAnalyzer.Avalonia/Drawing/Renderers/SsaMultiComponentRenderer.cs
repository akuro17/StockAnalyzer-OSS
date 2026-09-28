using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public class SsaMultiComponentRenderer
{
    private static readonly float[] DashEffect = new float[] { 4, 4 };

    public void InvalidateCache()
    {
        // Reserved for SKPath cache invalidation if cached paths are retained
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not SsaMultiComponentObject ssaObj || ssaObj.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(ssaObj.Points[0]);
        var p2 = transform.ChartToScreen(ssaObj.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = ssaObj.SkiaColor;

        // 1. Z=1 (Bottom): Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        using (var bgPaint = new SKPaint
        {
            Color = ssaObj.SkiaFillColor.WithAlpha((byte)(255 * ssaObj.FillOpacity / 100.0)),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawRect(bandRect, bgPaint);
        }

        // 2. Z=1 (Bottom): Layer 4 Residual Noise Envelope (Filled Fan)
        if (ssaObj.ShowNoiseBand &&
            ssaObj.UpperNoiseBandPath != null && ssaObj.UpperNoiseBandPath.Count > 1 &&
            ssaObj.LowerNoiseBandPath != null && ssaObj.LowerNoiseBandPath.Count > 1)
        {
            using var bandPath = new SKPath();
            var firstUpper = ssaObj.UpperNoiseBandPath[0];
            var startScreen = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
            bandPath.MoveTo((float)startScreen.X, (float)startScreen.Y);

            for (int i = 1; i < ssaObj.UpperNoiseBandPath.Count; i++)
            {
                var pt = ssaObj.UpperNoiseBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            for (int i = ssaObj.LowerNoiseBandPath.Count - 1; i >= 0; i--)
            {
                var pt = ssaObj.LowerNoiseBandPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                bandPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            bandPath.Close();

            var noiseColor = new SKColor(ssaObj.NoiseBandColor.R, ssaObj.NoiseBandColor.G, ssaObj.NoiseBandColor.B);
            using var fanPaint = new SKPaint
            {
                Color = noiseColor.WithAlpha((byte)(255 * 0.25)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawPath(bandPath, fanPaint);
        }

        // 3. Z=2: Layer 3 Composite Reconstructed (Dashed Line)
        if (ssaObj.ShowCompositeLayer && ssaObj.CompositePath != null && ssaObj.CompositePath.Count > 1)
        {
            var compColor = new SKColor(ssaObj.CompositeColor.R, ssaObj.CompositeColor.G, ssaObj.CompositeColor.B);
            using var pathEffect = SKPathEffect.CreateDash(DashEffect, 0);
            using var compPaint = new SKPaint
            {
                Color = compColor.WithAlpha(220),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                PathEffect = pathEffect,
                IsAntialias = true
            };

            using var compPath = new SKPath();
            var first = ssaObj.CompositePath[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            compPath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < ssaObj.CompositePath.Count; i++)
            {
                var pt = ssaObj.CompositePath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                compPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(compPath, compPaint);
        }

        // 4. Z=3: Layer 2 Primary Cycle Oscillation (Solid Line)
        if (ssaObj.ShowPrimaryCycleLayer && ssaObj.PrimaryCyclePath != null && ssaObj.PrimaryCyclePath.Count > 1)
        {
            var cycleColor = new SKColor(ssaObj.PrimaryCycleColor.R, ssaObj.PrimaryCycleColor.G, ssaObj.PrimaryCycleColor.B);
            using var cyclePaint = new SKPaint
            {
                Color = cycleColor.WithAlpha(230),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.8f,
                IsAntialias = true
            };

            using var cyclePath = new SKPath();
            var first = ssaObj.PrimaryCyclePath[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            cyclePath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < ssaObj.PrimaryCyclePath.Count; i++)
            {
                var pt = ssaObj.PrimaryCyclePath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                cyclePath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(cyclePath, cyclePaint);
        }

        // 5. Z=4: Layer 1 Trend Baseline (Bold Solid Line)
        if (ssaObj.ShowTrendLayer && ssaObj.TrendPath != null && ssaObj.TrendPath.Count > 1)
        {
            var trendColor = new SKColor(ssaObj.TrendColor.R, ssaObj.TrendColor.G, ssaObj.TrendColor.B);
            using var trendPaint = new SKPaint
            {
                Color = trendColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f,
                IsAntialias = true
            };

            using var trendPath = new SKPath();
            var first = ssaObj.TrendPath[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            trendPath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < ssaObj.TrendPath.Count; i++)
            {
                var pt = ssaObj.TrendPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                trendPath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            canvas.DrawPath(trendPath, trendPaint);
        }

        // 6. Z=5 (Top): Boundary Lines & Selection Handles
        using (var linePaint = new SKPaint
        {
            Color = isSelected ? activeColor : activeColor.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isSelected ? (float)ssaObj.Thickness + 1 : (float)ssaObj.Thickness,
            IsAntialias = false
        })
        {
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, linePaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, linePaint);
        }

        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), ssaObj.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), ssaObj.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }
}
