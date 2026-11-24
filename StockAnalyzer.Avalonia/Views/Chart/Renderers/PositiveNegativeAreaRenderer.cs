using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a baseline-split area filling above <see cref="SeriesPrimitiveDescriptor.BaselineValue"/> in Up color
/// and below in Down color with exact crossing-point triangle splitting and zero double-alpha overlap.
/// </summary>
internal sealed class PositiveNegativeAreaRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _posPath = new();
    private readonly SKPath _negPath = new();
    private readonly SKPath _borderPath = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        if (values == null || values.Count == 0 || context.VisibleCandles <= 0) return;

        decimal effectiveBaseline = context.EffectiveBaseline;
        if (!ChartPrimitiveMath.TryProjectY(effectiveBaseline, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float baselineY))
        {
            return;
        }
        baselineY = ChartPrimitiveMath.SnapPixel(baselineY);

        bool drawFill = desc.FillAlpha > 0;
        bool drawStroke = desc.DrawBorderStroke;
        if (!drawFill && !drawStroke) return;

        SKColor posFill = (context.Setting.UseUpDownColors ? context.Setting.UpColor : context.Theme.Bullish).ToSkColor().WithAlpha(desc.FillAlpha);
        SKColor negFill = (context.Setting.UseUpDownColors ? context.Setting.DownColor : context.Theme.Bearish).ToSkColor().WithAlpha(desc.FillAlpha);

        _posPath.Reset();
        _negPath.Reset();
        _borderPath.Reset();

        bool hasPrev = false;
        float prevX = 0f, prevY = 0f;
        decimal prevVal = 0m;
        bool hasPos = false;
        bool hasNeg = false;
        bool hasBorder = false;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= values.Count)
            {
                if (desc.NullPolicy == NullGapPolicy.BreakPath) hasPrev = false;
                continue;
            }

            decimal? valOpt = values[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(valOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float y))
            {
                if (desc.NullPolicy == NullGapPolicy.BreakPath) hasPrev = false;
                continue;
            }

            float x = ChartPrimitiveMath.ComputeBarCenterX(
                i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                context.Candles, context.Interval, context.Transform,
                context.Snapshot, context.Config, context.XOffset);
            if (!float.IsFinite(x))
            {
                hasPrev = false;
                continue;
            }

            decimal val = valOpt!.Value;
            if (hasPrev)
            {
                if (drawStroke)
                {
                    _borderPath.LineTo(x, y);
                }

                if (drawFill)
                {
                    bool pos1 = prevVal >= effectiveBaseline;
                    bool pos2 = val >= effectiveBaseline;
                    if (pos1 == pos2)
                    {
                        var targetPath = pos1 ? _posPath : _negPath;
                        targetPath.MoveTo(prevX, prevY);
                        targetPath.LineTo(x, y);
                        targetPath.LineTo(x, baselineY);
                        targetPath.LineTo(prevX, baselineY);
                        targetPath.Close();
                        if (pos1) hasPos = true; else hasNeg = true;
                    }
                    else if (ChartPrimitiveMath.TryComputeCrossingT(
                        (double)(prevY - baselineY),
                        (double)(y - baselineY),
                        out double t))
                    {
                        float xMid = prevX + (float)((x - prevX) * t);

                        var p1 = pos1 ? _posPath : _negPath;
                        p1.MoveTo(prevX, prevY);
                        p1.LineTo(xMid, baselineY);
                        p1.LineTo(prevX, baselineY);
                        p1.Close();
                        if (pos1) hasPos = true; else hasNeg = true;

                        var p2 = pos2 ? _posPath : _negPath;
                        p2.MoveTo(xMid, baselineY);
                        p2.LineTo(x, y);
                        p2.LineTo(x, baselineY);
                        p2.Close();
                        if (pos2) hasPos = true; else hasNeg = true;
                    }
                }
            }
            else if (drawStroke)
            {
                _borderPath.MoveTo(x, y);
                hasBorder = true;
            }

            hasPrev = true;
            prevX = x;
            prevY = y;
            prevVal = val;
        }

        if (hasPos)
        {
            _fillPaint.Color = posFill;
            context.Canvas.DrawPath(_posPath, _fillPaint);
        }

        if (hasNeg)
        {
            _fillPaint.Color = negFill;
            context.Canvas.DrawPath(_negPath, _fillPaint);
        }

        if (drawStroke && hasBorder)
        {
            _strokePaint.Color = context.SeriesColor;
            _strokePaint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
            _strokePaint.PathEffect = desc.LineStyle.ToSkPathEffect();
            context.Canvas.DrawPath(_borderPath, _strokePaint);
        }
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _posPath.Dispose();
        _negPath.Dispose();
        _borderPath.Dispose();
    }
}
