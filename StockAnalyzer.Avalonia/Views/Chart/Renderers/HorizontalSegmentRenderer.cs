using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders independent per-bar horizontal line segments without vertical connectors.
/// </summary>
internal sealed class HorizontalSegmentRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Stroke, IsAntialias = false };
    private readonly SKPath _path = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        _paint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
        _paint.PathEffect = desc.LineStyle.ToSkPathEffect();

        float ratio = float.IsFinite(desc.SegmentWidthRatio) ? Math.Clamp(desc.SegmentWidthRatio, 0.1f, 1.0f) : 0.8f;
        float halfW = MathF.Max(0.5f, context.ColumnWidth * ratio * 0.5f);

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        _path.Reset();
        bool hasSegment = false;
        SKColor pathColor = singleColor;
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
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawY))
            {
                continue;
            }

            float rawX = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(rawX)) continue;

            SKColor segColor = twoColors
                ? ((val!.Value >= (prevVal ?? desc.BaselineValue)) ? upColor : downColor)
                : singleColor;

            if (hasSegment && segColor != pathColor)
            {
                Flush(context.Canvas, ref hasSegment, pathColor);
            }

            if (!hasSegment)
            {
                pathColor = segColor;
                hasSegment = true;
            }

            float y = ChartPrimitiveMath.SnapPixel(rawY);
            float left = MathF.Floor(rawX - halfW);
            float right = MathF.Ceiling(rawX + halfW);
            if (right <= left) right = left + 1.0f;

            _path.MoveTo(left, y);
            _path.LineTo(right, y);

            prevVal = val;
        }

        Flush(context.Canvas, ref hasSegment, pathColor);
    }

    private void Flush(SKCanvas canvas, ref bool hasSegment, SKColor color)
    {
        if (!hasSegment) return;
        _paint.Color = color;
        canvas.DrawPath(_path, _paint);
        _path.Reset();
        hasSegment = false;
    }

    public void Dispose()
    {
        _paint.Dispose();
        _path.Dispose();
    }
}
