using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders <see cref="SeriesPrimitiveType.Line"/> and <see cref="SeriesPrimitiveType.DashedLine"/>
/// with <see cref="CoreLineStyle"/> dash patterns, <see cref="NullGapPolicy"/>, and phase-continuous
/// run-batched Up/Down coloring.
/// </summary>
internal sealed class LinePrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _path = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        CoreLineStyle effectiveStyle = desc.PrimitiveType == SeriesPrimitiveType.DashedLine && desc.LineStyle == CoreLineStyle.Solid
            ? CoreLineStyle.Dash
            : desc.LineStyle;

        _paint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
        _paint.PathEffect = effectiveStyle.ToSkPathEffect();

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        SKColor singleColor = context.SeriesColor;

        _path.Reset();
        bool hasPrev = false;
        float prevX = 0f;
        float prevY = 0f;
        decimal prevVal = 0m;
        SKColor pathColor = singleColor;
        bool hasSegment = false;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count)
            {
                Flush(context.Canvas, ref hasSegment, pathColor);
                hasPrev = false;
                continue;
            }

            decimal? val = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float y))
            {
                if (desc.NullPolicy == NullGapPolicy.BreakPath)
                {
                    Flush(context.Canvas, ref hasSegment, pathColor);
                    hasPrev = false;
                }
                continue;
            }

            float x = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(x))
            {
                Flush(context.Canvas, ref hasSegment, pathColor);
                hasPrev = false;
                continue;
            }

            if (hasPrev)
            {
                SKColor segColor = twoColors
                    ? (val!.Value >= prevVal ? upColor : downColor)
                    : singleColor;

                if (hasSegment && segColor != pathColor)
                {
                    Flush(context.Canvas, ref hasSegment, pathColor);
                }

                if (!hasSegment)
                {
                    _path.MoveTo(prevX, prevY);
                    pathColor = segColor;
                    hasSegment = true;
                }

                _path.LineTo(x, y);
            }

            hasPrev = true;
            prevX = x;
            prevY = y;
            prevVal = val!.Value;
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
