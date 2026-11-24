using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders <see cref="Core.Models.Rendering.SeriesPrimitiveType.Dot"/> as discrete filled circles
/// with spacing-clamped radius and viewport culling.
/// </summary>
internal sealed class DotPrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        float baseR = float.IsFinite(desc.MarkerRadiusPx) && desc.MarkerRadiusPx > 0f
            ? Math.Clamp(desc.MarkerRadiusPx, ChartPrimitiveMath.DotMinEffectiveRadius, 32.0f)
            : (float)Math.Max(1.0, context.Setting.Thickness * 1.5);
        float r = MathF.Max(ChartPrimitiveMath.DotMinEffectiveRadius, MathF.Min(baseR, context.ColumnWidth * ChartPrimitiveMath.DotSpacingMarginFactor));

        float leftClip = (float)context.ChartArea.Left;
        float rightClip = (float)context.ChartArea.Right;
        float topClip = (float)context.ChartArea.Top;
        float botClip = (float)context.ChartArea.Bottom;

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;
        decimal? prevVal = null;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count)
            {
                prevVal = null;
                continue;
            }

            decimal? val = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float cy))
            {
                continue;
            }

            float cx = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(cx)) continue;

            if (cx + r >= leftClip && cx - r <= rightClip && cy + r >= topClip && cy - r <= botClip)
            {
                _paint.Color = twoColors && prevVal.HasValue
                    ? (val!.Value >= prevVal.Value ? upColor : downColor)
                    : singleColor;
                context.Canvas.DrawCircle(cx, cy, r, _paint);
            }

            prevVal = val;
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
    }
}
