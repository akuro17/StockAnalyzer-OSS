using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a lollipop chart: snapped stem from <see cref="SeriesPrimitiveDescriptor.BaselineValue"/>
/// terminating cleanly at the circle head boundary, plus a filled or stroked circle head.
/// </summary>
internal sealed class LollipopRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _stemPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = false };
    private readonly SKPaint _headFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _headStrokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        decimal effectiveBaseline = context.EffectiveBaseline;
        if (!ChartPrimitiveMath.TryProjectY(effectiveBaseline, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawBaselineY))
        {
            return;
        }
        float baselineY = ChartPrimitiveMath.SnapPixel(rawBaselineY);

        float baseR = float.IsFinite(desc.MarkerRadiusPx) ? Math.Clamp(desc.MarkerRadiusPx, 1.5f, 32.0f) : 4.0f;
        float r = MathF.Max(1.5f, MathF.Min(baseR, context.ColumnWidth * ChartPrimitiveMath.DotSpacingMarginFactor));
        _stemPaint.StrokeWidth = float.IsFinite(desc.LollipopStemWidthPx) ? Math.Clamp(desc.LollipopStemWidthPx, 0.5f, 12.0f) : 1.0f;
        _headStrokePaint.StrokeWidth = MathF.Max(1.0f, (float)context.Setting.Thickness);

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;
        SKPaint headPaint = desc.MarkerFilled ? _headFillPaint : _headStrokePaint;
        decimal? prevVal = null;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count)
            {
                if (desc.NullPolicy == Core.Models.Rendering.NullGapPolicy.BreakPath) prevVal = null;
                continue;
            }

            decimal? val = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float y))
            {
                if (desc.NullPolicy == Core.Models.Rendering.NullGapPolicy.BreakPath) prevVal = null;
                continue;
            }

            if (twoColors && !prevVal.HasValue && valIdx > 0 && valIdx - 1 < values.Count && values[valIdx - 1].HasValue)
            {
                prevVal = values[valIdx - 1];
            }

            float rawX = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(rawX)) continue;

            float x = ChartPrimitiveMath.SnapPixel(rawX);
            SKColor color = twoColors
                ? (val!.Value >= (prevVal ?? effectiveBaseline) ? upColor : downColor)
                : singleColor;

            float deltaY = baselineY - y;
            if (MathF.Abs(deltaY) > r)
            {
                float stemEndY = y + MathF.Sign(deltaY) * r;
                _stemPaint.Color = color;
                context.Canvas.DrawLine(x, baselineY, x, stemEndY, _stemPaint);
            }

            headPaint.Color = color;
            context.Canvas.DrawCircle(x, y, r, headPaint);
            prevVal = val;
        }
    }

    public void Dispose()
    {
        _stemPaint.Dispose();
        _headFillPaint.Dispose();
        _headStrokePaint.Dispose();
    }
}
