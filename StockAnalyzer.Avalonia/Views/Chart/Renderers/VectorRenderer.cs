using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders directional vector arrows in either <see cref="VectorInputMode.PriceSpaceEndpoints"/>
/// or <see cref="VectorInputMode.BarAnchoredPolar"/> mode, delegating arrowheads to
/// <see cref="DrawingArrowHeadRenderer.DrawFilledHead"/> with shaft shortening and zero-length guards.
/// </summary>
internal sealed class VectorRenderer : ISeriesPrimitiveRenderer
{
    private const float ShaftTrimFactor = 0.85f;
    private const float MinVectorLengthSquared = 1e-4f;
    private const float DefaultVectorHeadSizePx = 8.0f;
    private const double MaxBarDeltaMagnitude = 100_000.0;

    private readonly SKPaint _shaftPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Butt };
    private readonly SKPaint _headPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _headPath = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        if (string.IsNullOrEmpty(desc.SecondarySeriesName) || context.VisibleCandles <= 0) return;

        var startYSeries = context.Result.GetSeries(desc.PrimarySeriesName);
        var secSeries = context.Result.GetSeries(desc.SecondarySeriesName);
        if (startYSeries == null || secSeries == null || startYSeries.Count == 0 || secSeries.Count == 0) return;

        IReadOnlyList<decimal?>? tertSeries = !string.IsNullOrEmpty(desc.TertiarySeriesName)
            ? context.Result.GetSeries(desc.TertiarySeriesName)
            : null;
        if (desc.VectorMode == VectorInputMode.BarAnchoredPolar && (tertSeries == null || tertSeries.Count == 0)) return;

        _shaftPaint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
        _shaftPaint.PathEffect = desc.LineStyle.ToSkPathEffect();

        float reqHead = float.IsFinite(desc.VectorHeadSizePx) && desc.VectorHeadSizePx > 0f
            ? Math.Clamp(desc.VectorHeadSizePx, 1.0f, 64.0f)
            : DefaultVectorHeadSizePx;
        float maxLenPx = float.IsFinite(desc.VectorMaxLengthPx) && desc.VectorMaxLengthPx > 0f
            ? desc.VectorMaxLengthPx
            : 200.0f;

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= startYSeries.Count || valIdx >= secSeries.Count) continue;

            if (!ChartPrimitiveMath.TryProjectY(startYSeries[valIdx], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float tailY))
            {
                continue;
            }

            float tailX = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(tailX)) continue;

            float tipX;
            float tipY;

            if (desc.VectorMode == VectorInputMode.BarAnchoredPolar)
            {
                decimal? angleOpt = secSeries[valIdx];
                decimal? lenOpt = (tertSeries != null && valIdx < tertSeries.Count) ? tertSeries[valIdx] : null;
                if (!angleOpt.HasValue || !lenOpt.HasValue) continue;

                float theta = (float)angleOpt.Value;
                float lenPx = (float)lenOpt.Value;
                if (!float.IsFinite(theta) || !float.IsFinite(lenPx) || lenPx <= 0f) continue;
                lenPx = MathF.Min(lenPx, maxLenPx);

                tipX = tailX + lenPx * MathF.Cos(theta);
                tipY = tailY - lenPx * MathF.Sin(theta);
            }
            else
            {
                if (!ChartPrimitiveMath.TryProjectY(secSeries[valIdx], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out tipY))
                {
                    continue;
                }

                int deltaBars = 0;
                if (tertSeries != null && valIdx < tertSeries.Count && tertSeries[valIdx].HasValue)
                {
                    double roundedDelta = Math.Round((double)tertSeries[valIdx]!.Value, MidpointRounding.AwayFromZero);
                    if (!double.IsFinite(roundedDelta) || Math.Abs(roundedDelta) > MaxBarDeltaMagnitude)
                    {
                        continue;
                    }
                    deltaBars = (int)roundedDelta;
                }

                tipX = deltaBars == 0
                    ? tailX
                    : ChartPrimitiveMath.ComputeBarCenterX(
                        i + deltaBars, context.ColumnWidth, context.ChartArea, context.StartIndex,
                        context.Candles, context.Interval, context.Transform,
                        context.Snapshot, context.Config, context.XOffset);
            }

            if (!float.IsFinite(tipX) || !float.IsFinite(tipY)) continue;

            float dx = tipX - tailX;
            float dy = tipY - tailY;
            float distSq = dx * dx + dy * dy;
            if (!float.IsFinite(distSq) || distSq < MinVectorLengthSquared) continue;

            float dist = MathF.Sqrt(distSq);
            float effHead = MathF.Min(reqHead, dist * 0.6f);
            float trim = effHead * ChartPrimitiveMath.Cos30 * ShaftTrimFactor;

            SKColor color = twoColors ? (dy <= 0f ? upColor : downColor) : singleColor;
            _shaftPaint.Color = color;
            _headPaint.Color = color;

            var tail = new SKPoint(tailX, tailY);
            var tip = new SKPoint(tipX, tipY);

            if (dist > trim)
            {
                var shaftEnd = new SKPoint(tipX - (dx / dist) * trim, tipY - (dy / dist) * trim);
                context.Canvas.DrawLine(tail, shaftEnd, _shaftPaint);
            }

            DrawingArrowHeadRenderer.DrawFilledHead(context.Canvas, tail, tip, _headPath, _headPaint, effHead);
        }
    }

    public void Dispose()
    {
        _shaftPaint.Dispose();
        _headPaint.Dispose();
        _headPath.Dispose();
    }
}
