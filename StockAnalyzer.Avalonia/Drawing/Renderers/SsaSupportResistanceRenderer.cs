using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Renderers;

public sealed class SsaSupportResistanceRenderer
{
    private readonly SKPath _fillPath = new();
    private readonly SKPath _linePath = new();
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _dashedPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 11f };
    private readonly SKPaint _markerFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private static readonly float[] DashPattern5_5 = new float[] { 5, 5 };
    private static readonly float[] DashPattern4_4 = new float[] { 4, 4 };
    private static readonly SKPathEffect DashEffect5_5 = SKPathEffect.CreateDash(DashPattern5_5, 0);
    private static readonly SKPathEffect DashEffect4_4 = SKPathEffect.CreateDash(DashPattern4_4, 0);

    public void InvalidateCache()
    {
        _fillPath.Reset();
        _linePath.Reset();
    }

    public void Render(SKCanvas canvas, IChartObject obj, ICoordinateTransform transform, bool isSelected)
    {
        if (obj is not SsaSupportResistanceObject drawing || drawing.Points.Count < 2) return;

        var p1 = transform.ChartToScreen(drawing.Points[0]);
        var p2 = transform.ChartToScreen(drawing.Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);

        var activeColor = drawing.SkiaColor;
        _textPaint.TextSize = DrawingThemeContext.FontSize;

        // 1. Draw Selection Range Background Band
        var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
        _fillPaint.Color = drawing.SkiaFillColor.WithAlpha((byte)(255 * drawing.FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _fillPaint);

        // 2. Draw Vertical Lines at Start (x1) and End (x2)
        _linePaint.Color = isSelected ? activeColor : activeColor.WithAlpha(180);
        _linePaint.StrokeWidth = isSelected ? (float)drawing.Thickness + 1 : (float)drawing.Thickness;
        canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _linePaint);
        canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _linePaint);

        var result = drawing.CalculatedResult;
        if (result != null && !result.IsEmpty)
        {
            switch (drawing.Mode)
            {
                case SsaSupportResistanceMode.StructuralPivots:
                    RenderStructuralPivots(canvas, drawing, result, transform, left, right);
                    break;
                case SsaSupportResistanceMode.DynamicEnvelopes:
                    RenderDynamicEnvelopes(canvas, drawing, result, transform);
                    break;
                case SsaSupportResistanceMode.ProjectedTargets:
                    RenderProjectedTargets(canvas, drawing, result, transform, right);
                    break;
            }
        }

        // 3. Selection Handles
        if (isSelected)
        {
            float midY = clip.MidY;
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), drawing.AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), drawing.AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    private void RenderStructuralPivots(
        SKCanvas canvas,
        SsaSupportResistanceObject drawing,
        SsaSupportResistanceResult result,
        ICoordinateTransform transform,
        float left,
        float right)
    {
        float xStart = left;
        float xEnd = drawing.ExtendLinesToRight ? (float)transform.ViewportWidth : right;

        // Draw In-Sample Center Trend line if available
        if (result.CenterBand != null && result.CenterBand.Count > 1)
        {
            _linePath.Reset();
            var first = result.CenterBand[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            _linePath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < result.CenterBand.Count; i++)
            {
                var pt = result.CenterBand[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _linePath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _linePaint.Color = drawing.SkiaCenterColor.WithAlpha(160);
            _linePaint.StrokeWidth = 1.0f;
            canvas.DrawPath(_linePath, _linePaint);
        }

        // Configure dashed paint for horizontal levels
        _dashedPaint.PathEffect = DashEffect5_5;
        _dashedPaint.StrokeWidth = 1.5f;

        // Draw Resistance Levels
        for (int i = 0; i < result.ResistanceLevels.Count; i++)
        {
            var lvl = result.ResistanceLevels[i];
            var screenPt = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)lvl.Price));
            float y = (float)screenPt.Y;

            _dashedPaint.Color = drawing.SkiaResistanceColor;
            canvas.DrawLine(xStart, y, xEnd, y, _dashedPaint);

            string label = !string.IsNullOrEmpty(lvl.Label) ? lvl.Label : $"R{i + 1}: {lvl.Price:F2} (Hits: {lvl.Hits})";
            _textPaint.Color = drawing.SkiaResistanceColor;
            float textWidth = _textPaint.MeasureText(label);
            float maxTextX = Math.Max(xStart + 8f, (float)transform.ViewportWidth - textWidth - 14f);
            float textX = drawing.ExtendLinesToRight
                ? maxTextX
                : Math.Max(xStart + 8f, Math.Min(xEnd - textWidth - 8f, maxTextX));
            canvas.DrawText(label, textX, y - 4, _textPaint);
        }

        // Draw Support Levels
        for (int i = 0; i < result.SupportLevels.Count; i++)
        {
            var lvl = result.SupportLevels[i];
            var screenPt = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)lvl.Price));
            float y = (float)screenPt.Y;

            _dashedPaint.Color = drawing.SkiaSupportColor;
            canvas.DrawLine(xStart, y, xEnd, y, _dashedPaint);

            string label = !string.IsNullOrEmpty(lvl.Label) ? lvl.Label : $"S{i + 1}: {lvl.Price:F2} (Hits: {lvl.Hits})";
            _textPaint.Color = drawing.SkiaSupportColor;
            float textWidth = _textPaint.MeasureText(label);
            float maxTextX = Math.Max(xStart + 8f, (float)transform.ViewportWidth - textWidth - 14f);
            float textX = drawing.ExtendLinesToRight
                ? maxTextX
                : Math.Max(xStart + 8f, Math.Min(xEnd - textWidth - 8f, maxTextX));
            canvas.DrawText(label, textX, y + 13, _textPaint);
        }
    }

    private void RenderDynamicEnvelopes(
        SKCanvas canvas,
        SsaSupportResistanceObject drawing,
        SsaSupportResistanceResult result,
        ICoordinateTransform transform)
    {
        if (result.UpperBand == null || result.UpperBand.Count < 2 ||
            result.LowerBand == null || result.LowerBand.Count < 2)
        {
            return;
        }

        // 1. Channel Filled Fan
        _fillPath.Reset();
        var firstUpper = result.UpperBand[0];
        var sUpperStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstUpper.X), (decimal)firstUpper.Y));
        _fillPath.MoveTo((float)sUpperStart.X, (float)sUpperStart.Y);

        for (int i = 1; i < result.UpperBand.Count; i++)
        {
            var pt = result.UpperBand[i];
            var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
            _fillPath.LineTo((float)sPt.X, (float)sPt.Y);
        }

        for (int i = result.LowerBand.Count - 1; i >= 0; i--)
        {
            var pt = result.LowerBand[i];
            var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
            _fillPath.LineTo((float)sPt.X, (float)sPt.Y);
        }
        _fillPath.Close();

        _fillPaint.Color = drawing.SkiaColor.WithAlpha((byte)(255 * drawing.ChannelFillOpacity / 100.0));
        canvas.DrawPath(_fillPath, _fillPaint);

        // 2. Upper Band Line
        _linePath.Reset();
        _linePath.MoveTo((float)sUpperStart.X, (float)sUpperStart.Y);
        for (int i = 1; i < result.UpperBand.Count; i++)
        {
            var pt = result.UpperBand[i];
            var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
            _linePath.LineTo((float)sPt.X, (float)sPt.Y);
        }
        _linePaint.Color = drawing.SkiaResistanceColor;
        _linePaint.StrokeWidth = 1.5f;
        canvas.DrawPath(_linePath, _linePaint);

        // 3. Lower Band Line
        _linePath.Reset();
        var firstLower = result.LowerBand[0];
        var sLowerStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstLower.X), (decimal)firstLower.Y));
        _linePath.MoveTo((float)sLowerStart.X, (float)sLowerStart.Y);
        for (int i = 1; i < result.LowerBand.Count; i++)
        {
            var pt = result.LowerBand[i];
            var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
            _linePath.LineTo((float)sPt.X, (float)sPt.Y);
        }
        _linePaint.Color = drawing.SkiaSupportColor;
        _linePaint.StrokeWidth = 1.5f;
        canvas.DrawPath(_linePath, _linePaint);

        // 4. Center Equilibrium Line
        if (result.CenterBand != null && result.CenterBand.Count > 1)
        {
            _linePath.Reset();
            var firstCenter = result.CenterBand[0];
            var sCenterStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstCenter.X), (decimal)firstCenter.Y));
            _linePath.MoveTo((float)sCenterStart.X, (float)sCenterStart.Y);
            for (int i = 1; i < result.CenterBand.Count; i++)
            {
                var pt = result.CenterBand[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _linePath.LineTo((float)sPt.X, (float)sPt.Y);
            }
            _linePaint.Color = drawing.SkiaCenterColor;
            _linePaint.StrokeWidth = 2.0f;
            canvas.DrawPath(_linePath, _linePaint);
        }
    }

    private void RenderProjectedTargets(
        SKCanvas canvas,
        SsaSupportResistanceObject drawing,
        SsaSupportResistanceResult result,
        ICoordinateTransform transform,
        float right)
    {
        // 1. Draw In-Sample Reconstructed Curve
        if (result.CenterBand != null && result.CenterBand.Count > 1)
        {
            _linePath.Reset();
            var first = result.CenterBand[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)first.X), (decimal)first.Y));
            _linePath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < result.CenterBand.Count; i++)
            {
                var pt = result.CenterBand[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _linePath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _linePaint.Color = drawing.SkiaCenterColor.WithAlpha(180);
            _linePaint.StrokeWidth = 1.5f;
            canvas.DrawPath(_linePath, _linePaint);
        }

        // 2. Draw Future Projected Trajectory (Dashed)
        if (result.ProjectedPath != null && result.ProjectedPath.Count > 1)
        {
            _linePath.Reset();
            var firstProj = result.ProjectedPath[0];
            var sStart = transform.ChartToScreen(new ChartPoint(new DateTime((long)firstProj.X), (decimal)firstProj.Y));
            _linePath.MoveTo((float)sStart.X, (float)sStart.Y);

            for (int i = 1; i < result.ProjectedPath.Count; i++)
            {
                var pt = result.ProjectedPath[i];
                var sPt = transform.ChartToScreen(new ChartPoint(new DateTime((long)pt.X), (decimal)pt.Y));
                _linePath.LineTo((float)sPt.X, (float)sPt.Y);
            }

            _dashedPaint.PathEffect = DashEffect4_4;
            _dashedPaint.Color = drawing.SkiaColor;
            _dashedPaint.StrokeWidth = 1.5f;
            canvas.DrawPath(_linePath, _dashedPaint);
        }

        // 3. Draw Target Resistance Line & Marker
        if (result.ResistanceLevels.Count > 0)
        {
            var res = result.ResistanceLevels[0];
            var screenTarget = res.TargetTime.HasValue
                ? transform.ChartToScreen(new ChartPoint(res.TargetTime.Value, (decimal)res.Price))
                : transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)res.Price));

            float y = (float)screenTarget.Y;
            float targetX = res.TargetTime.HasValue ? (float)screenTarget.X : right + 50f;
            float lineEndX = drawing.ExtendLinesToRight ? (float)transform.ViewportWidth : Math.Max(targetX, right);

            _dashedPaint.PathEffect = DashEffect5_5;
            _dashedPaint.Color = drawing.SkiaResistanceColor;
            _dashedPaint.StrokeWidth = 1.5f;
            canvas.DrawLine(right, y, lineEndX, y, _dashedPaint);

            // Marker dot
            if (res.TargetTime.HasValue)
            {
                _markerFillPaint.Color = drawing.SkiaResistanceColor;
                canvas.DrawCircle((float)screenTarget.X, y, 4.5f, _markerFillPaint);
            }

            string label = !string.IsNullOrEmpty(res.Label) ? res.Label : $"Target R: {res.Price:F2}";
            _textPaint.Color = drawing.SkiaResistanceColor;
            float textWidth = _textPaint.MeasureText(label);
            float maxTextX = Math.Max(8f, (float)transform.ViewportWidth - textWidth - 14f);
            float textX;
            if (drawing.ExtendLinesToRight)
            {
                textX = maxTextX;
            }
            else
            {
                if (targetX + 8f + textWidth <= (float)transform.ViewportWidth - 14f)
                {
                    textX = Math.Max(8f, targetX + 8f);
                }
                else
                {
                    textX = Math.Max(8f, targetX - textWidth - 8f);
                }
                textX = Math.Min(textX, maxTextX);
            }
            canvas.DrawText(label, textX, y - 4, _textPaint);
        }

        // 4. Draw Target Support Line & Marker
        if (result.SupportLevels.Count > 0)
        {
            var sup = result.SupportLevels[0];
            var screenTarget = sup.TargetTime.HasValue
                ? transform.ChartToScreen(new ChartPoint(sup.TargetTime.Value, (decimal)sup.Price))
                : transform.ChartToScreen(new ChartPoint(DateTime.MinValue, (decimal)sup.Price));

            float y = (float)screenTarget.Y;
            float targetX = sup.TargetTime.HasValue ? (float)screenTarget.X : right + 50f;
            float lineEndX = drawing.ExtendLinesToRight ? (float)transform.ViewportWidth : Math.Max(targetX, right);

            _dashedPaint.PathEffect = DashEffect5_5;
            _dashedPaint.Color = drawing.SkiaSupportColor;
            _dashedPaint.StrokeWidth = 1.5f;
            canvas.DrawLine(right, y, lineEndX, y, _dashedPaint);

            // Marker dot
            if (sup.TargetTime.HasValue)
            {
                _markerFillPaint.Color = drawing.SkiaSupportColor;
                canvas.DrawCircle((float)screenTarget.X, y, 4.5f, _markerFillPaint);
            }

            string label = !string.IsNullOrEmpty(sup.Label) ? sup.Label : $"Target S: {sup.Price:F2}";
            _textPaint.Color = drawing.SkiaSupportColor;
            float textWidth = _textPaint.MeasureText(label);
            float maxTextX = Math.Max(8f, (float)transform.ViewportWidth - textWidth - 14f);
            float textX;
            if (drawing.ExtendLinesToRight)
            {
                textX = maxTextX;
            }
            else
            {
                if (targetX + 8f + textWidth <= (float)transform.ViewportWidth - 14f)
                {
                    textX = Math.Max(8f, targetX + 8f);
                }
                else
                {
                    textX = Math.Max(8f, targetX - textWidth - 8f);
                }
                textX = Math.Min(textX, maxTextX);
            }
            canvas.DrawText(label, textX, y + 13, _textPaint);
        }
    }
}
