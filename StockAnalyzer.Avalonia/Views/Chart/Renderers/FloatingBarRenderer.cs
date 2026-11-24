using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders independent per-bar floating range bars spanning [<c>PrimarySeriesName</c>, <c>SecondarySeriesName</c>]
/// with normalized <see cref="SKRect"/> bounds and a 1px minimum height guard.
/// </summary>
internal sealed class FloatingBarRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = false };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        if (string.IsNullOrEmpty(desc.SecondarySeriesName) || context.VisibleCandles <= 0) return;

        var lowSeries = context.Result.GetSeries(desc.PrimarySeriesName);
        var highSeries = context.Result.GetSeries(desc.SecondarySeriesName);
        if (lowSeries == null || highSeries == null || lowSeries.Count == 0 || highSeries.Count == 0) return;

        float ratio = float.IsFinite(desc.SegmentWidthRatio) ? Math.Clamp(desc.SegmentWidthRatio, 0.1f, 1.0f) : 0.8f;
        float barW = MathF.Max(1.0f, context.ColumnWidth * ratio);

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= lowSeries.Count || valIdx >= highSeries.Count) continue;

            decimal? lowOpt = lowSeries[valIdx];
            decimal? highOpt = highSeries[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(lowOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yLow) ||
                !ChartPrimitiveMath.TryProjectY(highOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yHigh))
            {
                continue;
            }

            float rawX = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(rawX)) continue;

            _paint.Color = twoColors
                ? (highOpt!.Value >= lowOpt!.Value ? upColor : downColor)
                : singleColor;

            SKRect rect = ChartPrimitiveMath.NormalizeRectWithMinHeight(rawX - barW * 0.5f, barW, yLow, yHigh, 1.0f);
            context.Canvas.DrawRect(rect, _paint);
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
    }
}
