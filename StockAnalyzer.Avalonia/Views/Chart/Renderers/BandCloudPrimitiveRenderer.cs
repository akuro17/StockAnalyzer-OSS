using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a shaded band/cloud between two series with exact crossing-point triangle splitting
/// (preventing self-intersecting bowtie polygons and double-alpha overlap).
/// </summary>
internal sealed class BandCloudPrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _upPath = new();
    private readonly SKPath _downPath = new();
    private readonly SKPath _upperBorder = new();
    private readonly SKPath _lowerBorder = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        if (string.IsNullOrEmpty(desc.SecondarySeriesName) || context.VisibleCandles <= 0) return;

        var upperSeries = context.Result.GetSeries(desc.PrimarySeriesName);
        var lowerSeries = context.Result.GetSeries(desc.SecondarySeriesName);
        if (upperSeries == null || lowerSeries == null || upperSeries.Count == 0 || lowerSeries.Count == 0) return;

        bool drawFill = desc.FillAlpha > 0;
        bool drawStroke = desc.DrawBorderStroke;
        if (!drawFill && !drawStroke) return;

        SKColor upFill = (context.Setting.UseUpDownColors ? context.Setting.UpColor.ToSkColor() : context.SeriesColor).WithAlpha(desc.FillAlpha);
        SKColor downFill = (context.Setting.UseUpDownColors ? context.Setting.DownColor.ToSkColor() : context.SeriesColor).WithAlpha(desc.FillAlpha);

        _upPath.Reset();
        _downPath.Reset();
        _upperBorder.Reset();
        _lowerBorder.Reset();

        bool hasPrev = false;
        float prevX = 0f, prevYa = 0f, prevYb = 0f;
        decimal prevA = 0m, prevB = 0m;
        bool hasUpFill = false;
        bool hasDownFill = false;
        bool hasBorder = false;

        for (int i = 0; i < context.VisibleCandles; i++)
        {
            int valIdx = i - context.Offset;
            if (valIdx < 0 || valIdx >= upperSeries.Count || valIdx >= lowerSeries.Count)
            {
                if (desc.NullPolicy == NullGapPolicy.BreakPath) hasPrev = false;
                continue;
            }

            decimal? aOpt = upperSeries[valIdx];
            decimal? bOpt = lowerSeries[valIdx];
            if (!ChartPrimitiveMath.TryProjectY(aOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float ya) ||
                !ChartPrimitiveMath.TryProjectY(bOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yb))
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

            decimal a = aOpt!.Value;
            decimal b = bOpt!.Value;

            if (hasPrev)
            {
                if (drawStroke)
                {
                    _upperBorder.LineTo(x, ya);
                    _lowerBorder.LineTo(x, yb);
                    hasBorder = true;
                }

                if (drawFill)
                {
                    bool aTop1 = prevA >= prevB;
                    bool aTop2 = a >= b;
                    if (aTop1 == aTop2)
                    {
                        var targetPath = aTop1 ? _upPath : _downPath;
                        targetPath.MoveTo(prevX, prevYa);
                        targetPath.LineTo(x, ya);
                        targetPath.LineTo(x, yb);
                        targetPath.LineTo(prevX, prevYb);
                        targetPath.Close();
                        if (aTop1) hasUpFill = true; else hasDownFill = true;
                    }
                    else if (ChartPrimitiveMath.TryComputeCrossingT((double)(prevYa - prevYb), (double)(ya - yb), out double t))
                    {
                        float xMid = prevX + (float)((x - prevX) * t);
                        float yMid = prevYa + (float)((ya - prevYa) * t);

                        var p1 = aTop1 ? _upPath : _downPath;
                        p1.MoveTo(prevX, prevYa);
                        p1.LineTo(xMid, yMid);
                        p1.LineTo(prevX, prevYb);
                        p1.Close();
                        if (aTop1) hasUpFill = true; else hasDownFill = true;

                        var p2 = aTop2 ? _upPath : _downPath;
                        p2.MoveTo(xMid, yMid);
                        p2.LineTo(x, ya);
                        p2.LineTo(x, yb);
                        p2.Close();
                        if (aTop2) hasUpFill = true; else hasDownFill = true;
                    }
                }
            }
            else if (drawStroke)
            {
                _upperBorder.MoveTo(x, ya);
                _lowerBorder.MoveTo(x, yb);
            }

            hasPrev = true;
            prevX = x;
            prevYa = ya;
            prevYb = yb;
            prevA = a;
            prevB = b;
        }

        if (hasUpFill)
        {
            _fillPaint.Color = upFill;
            context.Canvas.DrawPath(_upPath, _fillPaint);
        }

        if (hasDownFill)
        {
            _fillPaint.Color = downFill;
            context.Canvas.DrawPath(_downPath, _fillPaint);
        }

        if (drawStroke && hasBorder)
        {
            _strokePaint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
            _strokePaint.PathEffect = desc.LineStyle.ToSkPathEffect();
            _strokePaint.Color = context.SeriesColor;
            context.Canvas.DrawPath(_upperBorder, _strokePaint);

            var lowerColorCfg = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, desc.SecondarySeriesName);
            _strokePaint.Color = lowerColorCfg != null ? lowerColorCfg.Color.ToSkColor() : context.SeriesColor;
            context.Canvas.DrawPath(_lowerBorder, _strokePaint);
        }
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _upPath.Dispose();
        _downPath.Dispose();
        _upperBorder.Dispose();
        _lowerBorder.Dispose();
    }
}
