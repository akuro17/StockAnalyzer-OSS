using System;
using System.Buffers;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders multi-series stacked areas using <see cref="StackingSignMode"/>, <see cref="StackingNullMode"/>,
/// pooled decimal accumulators, and <see cref="ChartColorPalette"/> Dark/Light SSoT fallback colors.
/// </summary>
internal sealed class StackedAreaRenderer : ISeriesPrimitiveRenderer
{
    private const byte BorderStrokeAlpha = 220;

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _areaPath = new();
    private readonly SKPath _linePath = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        int n = context.VisibleCandles;
        if (n <= 0) return;

        var layers = desc.StackedSeriesNames;
        int layerCount = layers is { Count: > 0 } ? layers.Count : 1;

        bool drawFill = desc.FillAlpha > 0;
        bool drawStroke = desc.DrawBorderStroke;
        if (!drawFill && !drawStroke) return;

        decimal[] posAccum = ArrayPool<decimal>.Shared.Rent(n);
        decimal[] negAccum = ArrayPool<decimal>.Shared.Rent(n);
        bool[]? breakBars = desc.StackingNullPolicy == StackingNullMode.BreakAllLayers
            ? ArrayPool<bool>.Shared.Rent(n)
            : null;
        SKPoint[]? rentedBottom = n > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<SKPoint>.Shared.Rent(n)
            : null;

        try
        {
            decimal initialPos = desc.StackingMode == StackingSignMode.NonNegativeClamp ? desc.BaselineValue : 0m;
            for (int i = 0; i < n; i++)
            {
                posAccum[i] = initialPos;
                negAccum[i] = 0m;
                if (breakBars != null) breakBars[i] = false;
            }

            if (breakBars != null)
            {
                for (int k = 0; k < layerCount; k++)
                {
                    string layerName = layers is { Count: > 0 } ? layers[k] : desc.PrimarySeriesName;
                    if (string.IsNullOrEmpty(layerName) ||
                        IndicatorChartSuppression.IsSuppressed(context.Setting.TypeEnum, layerName, context.Setting.ParameterObject) ||
                        !SeriesRenderStyleCatalog.IsSeriesVisible(context.Setting, layerName))
                    {
                        continue;
                    }

                    var series = context.Result.GetSeries(layerName);
                    for (int i = 0; i < n; i++)
                    {
                        int valIdx = i - context.Offset;
                        if (series == null || valIdx < 0 || valIdx >= series.Count || !series[valIdx].HasValue)
                        {
                            breakBars[i] = true;
                        }
                    }
                }
            }

            Span<SKPoint> bottomPoints = rentedBottom != null ? rentedBottom.AsSpan(0, n) : stackalloc SKPoint[n];

            for (int k = 0; k < layerCount; k++)
            {
                string layerName = layers is { Count: > 0 } ? layers[k] : desc.PrimarySeriesName;
                if (string.IsNullOrEmpty(layerName) ||
                    IndicatorChartSuppression.IsSuppressed(context.Setting.TypeEnum, layerName, context.Setting.ParameterObject) ||
                    !SeriesRenderStyleCatalog.IsSeriesVisible(context.Setting, layerName))
                {
                    continue;
                }

                var series = context.Result.GetSeries(layerName);
                if (series == null || series.Count == 0) continue;

                SKColor baseColor = ResolveLayerColor(context, layerName, k);
                _fillPaint.Color = baseColor.WithAlpha(desc.FillAlpha);
                if (drawStroke)
                {
                    _strokePaint.Color = baseColor.WithAlpha(BorderStrokeAlpha);
                    _strokePaint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
                    _strokePaint.PathEffect = desc.LineStyle.ToSkPathEffect();
                }

                _areaPath.Reset();
                _linePath.Reset();
                int runLen = 0;

                for (int i = 0; i < n; i++)
                {
                    if (breakBars != null && breakBars[i])
                    {
                        FlushRun(context.Canvas, bottomPoints, runLen, drawFill, drawStroke);
                        runLen = 0;
                        continue;
                    }

                    int valIdx = i - context.Offset;
                    if (valIdx < 0 || valIdx >= series.Count)
                    {
                        FlushRun(context.Canvas, bottomPoints, runLen, drawFill, drawStroke);
                        runLen = 0;
                        continue;
                    }

                    decimal? v = series[valIdx];
                    if (!v.HasValue)
                    {
                        v = 0m;
                    }

                    decimal val = v.Value;
                    decimal lo;
                    decimal hi;
                    switch (desc.StackingMode)
                    {
                        case StackingSignMode.NonNegativeClamp:
                            decimal clamped = val > 0m ? val : 0m;
                            lo = posAccum[i];
                            hi = lo + clamped;
                            posAccum[i] = hi;
                            break;

                        case StackingSignMode.RawCumulative:
                            lo = posAccum[i];
                            hi = lo + val;
                            posAccum[i] = hi;
                            break;

                        default: // ZeroBaselineSplit
                            if (val >= 0m)
                            {
                                lo = posAccum[i];
                                hi = lo + val;
                                posAccum[i] = hi;
                            }
                            else
                            {
                                hi = negAccum[i];
                                lo = hi + val;
                                negAccum[i] = lo;
                            }
                            break;
                    }

                    if (!ChartPrimitiveMath.TryProjectY(hi, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yTop) ||
                        !ChartPrimitiveMath.TryProjectY(lo, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yBot))
                    {
                        FlushRun(context.Canvas, bottomPoints, runLen, drawFill, drawStroke);
                        runLen = 0;
                        continue;
                    }

                    float x = ChartPrimitiveMath.ComputeBarCenterX(
                        i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                        context.Candles, context.Interval, context.Transform,
                        context.Snapshot, context.Config, context.XOffset);
                    if (!float.IsFinite(x))
                    {
                        FlushRun(context.Canvas, bottomPoints, runLen, drawFill, drawStroke);
                        runLen = 0;
                        continue;
                    }

                    if (runLen == 0)
                    {
                        if (drawFill) _areaPath.MoveTo(x, yTop);
                        if (drawStroke) _linePath.MoveTo(x, yTop);
                    }
                    else
                    {
                        if (drawFill) _areaPath.LineTo(x, yTop);
                        if (drawStroke) _linePath.LineTo(x, yTop);
                    }

                    bottomPoints[runLen++] = new SKPoint(x, yBot);
                }

                FlushRun(context.Canvas, bottomPoints, runLen, drawFill, drawStroke);
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(posAccum);
            ArrayPool<decimal>.Shared.Return(negAccum);
            if (breakBars != null)
            {
                ArrayPool<bool>.Shared.Return(breakBars);
            }
            if (rentedBottom != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rentedBottom);
            }
        }
    }

    private void FlushRun(SKCanvas canvas, ReadOnlySpan<SKPoint> bottomPoints, int runLen, bool drawFill, bool drawStroke)
    {
        if (runLen >= 2)
        {
            if (drawFill)
            {
                for (int j = runLen - 1; j >= 0; j--)
                {
                    _areaPath.LineTo(bottomPoints[j].X, bottomPoints[j].Y);
                }
                _areaPath.Close();
                canvas.DrawPath(_areaPath, _fillPaint);
            }

            if (drawStroke)
            {
                canvas.DrawPath(_linePath, _strokePaint);
            }
        }

        _areaPath.Reset();
        _linePath.Reset();
    }

    private static SKColor ResolveLayerColor(in SeriesPrimitiveRenderContext context, string layerName, int layerIndex)
    {
        var colorCfg = ChartPrimitiveMath.FindSeriesColor(context.Setting.SeriesColors, layerName);
        if (colorCfg != null)
        {
            return colorCfg.Color.ToSkColor();
        }

        return ChartColorPalette.Get(layerIndex, context.Theme.IsDark).ToSkColor();
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _areaPath.Dispose();
        _linePath.Dispose();
    }
}
