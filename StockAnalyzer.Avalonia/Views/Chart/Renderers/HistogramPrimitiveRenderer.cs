using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders <see cref="Core.Models.Rendering.SeriesPrimitiveType.Histogram"/> bars relative to
/// <see cref="Core.Models.Rendering.SeriesPrimitiveDescriptor.BaselineValue"/> with normalized bounds and 1px minimum height guard.
/// </summary>
internal sealed class HistogramPrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        decimal effectiveBaseline = context.EffectiveBaseline;
        if (!ChartPrimitiveMath.TryProjectY(effectiveBaseline, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawBaseY))
        {
            return;
        }
        float baseY = ChartPrimitiveMath.SnapPixel(rawBaseY);

        float ratio = float.IsFinite(desc.SegmentWidthRatio) ? Math.Clamp(desc.SegmentWidthRatio, 0.1f, 1.0f) : 0.8f;
        float barW = MathF.Max(1.0f, context.ColumnWidth * ratio);

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count) continue;

            decimal? val = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float y))
            {
                continue;
            }

            float cx = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(cx)) continue;

            _paint.Color = twoColors
                ? (val!.Value >= effectiveBaseline ? upColor : downColor)
                : singleColor;

            SKRect rect = ChartPrimitiveMath.NormalizeRectWithMinHeight(cx - barW * 0.5f, barW, y, baseY, 1.0f);
            context.Canvas.DrawRect(rect, _paint);
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
    }
}
