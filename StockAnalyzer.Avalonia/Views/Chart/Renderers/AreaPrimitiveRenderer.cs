using System;
using System.Buffers;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders an indicator area fill against <see cref="AreaBaselineMode"/> (PanelBottom, ScalarValue, ReferenceSeries)
/// with contiguous null-gap polygon splitting and cached luminance-scaled vertical gradient shader.
/// </summary>
internal sealed class AreaPrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private const int ShaderCacheCapacity = 4;

    private struct ShaderCacheEntry
    {
        public SKShader? Shader;
        public SKColor BaseColor;
        public SKColor BgColor;
        public float Top;
        public float Bottom;
        public byte FillAlpha;
        public int LastUseTick;
    }

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _areaPath = new();
    private readonly SKPath _linePath = new();
    private readonly SKColor[] _gradientColors = new SKColor[2];
    private readonly ShaderCacheEntry[] _shaderCache = new ShaderCacheEntry[ShaderCacheCapacity];
    private int _shaderUseCounter;

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        int n = context.VisibleCandles;
        if (values == null || values.Count == 0 || n <= 0) return;

        IReadOnlyList<decimal?>? refValues = null;
        float scalarBaselineY = float.NaN;

        switch (desc.BaselineMode)
        {
            case AreaBaselineMode.PanelBottom:
                scalarBaselineY = ChartPrimitiveMath.SnapPixel((float)context.ChartArea.Bottom);
                break;

            case AreaBaselineMode.ReferenceSeries:
                if (string.IsNullOrEmpty(desc.SecondarySeriesName)) return;
                refValues = context.Result.GetSeries(desc.SecondarySeriesName);
                if (refValues == null || refValues.Count == 0) return;
                break;

            default: // AreaBaselineMode.ScalarValue
                if (!ChartPrimitiveMath.TryProjectY(desc.BaselineValue, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out scalarBaselineY))
                {
                    return;
                }
                scalarBaselineY = ChartPrimitiveMath.SnapPixel(scalarBaselineY);
                break;
        }

        bool drawFill = desc.FillAlpha > 0;
        bool drawStroke = desc.DrawBorderStroke;
        if (!drawFill && !drawStroke) return;

        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();

        if (drawFill && !twoColors)
        {
            _fillPaint.Shader = EnsureShader(context, context.SeriesColor, desc.FillAlpha);
        }

        if (drawStroke)
        {
            _linePaint.Color = context.SeriesColor;
            _linePaint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
            _linePaint.PathEffect = desc.LineStyle.ToSkPathEffect();
        }

        SKPoint[]? rented = n > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<SKPoint>.Shared.Rent(n)
            : null;
        try
        {
            Span<SKPoint> baselinePoints = rented != null ? rented.AsSpan(0, n) : stackalloc SKPoint[n];
            int runLen = 0;
            _areaPath.Reset();
            _linePath.Reset();

            bool hasPrev = false;
            float prevX = 0f;
            float prevY = 0f;
            float prevBaseY = 0f;
            decimal prevVal = 0m;
            SKColor activeColor = context.SeriesColor;

            for (int i = 0; i < n; i++)
            {
                int valIdx = i - context.Offset;
                bool outOfRange = valIdx < 0 || valIdx >= values.Count || (refValues != null && valIdx >= refValues.Count);
                if (outOfRange)
                {
                    if (desc.NullPolicy == NullGapPolicy.BreakPath)
                    {
                        FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors, activeColor, desc.FillAlpha);
                        runLen = 0;
                        hasPrev = false;
                    }
                    continue;
                }

                decimal? valOpt = values[valIdx];
                if (!ChartPrimitiveMath.TryProjectY(valOpt, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float yTop))
                {
                    if (desc.NullPolicy == NullGapPolicy.BreakPath)
                    {
                        FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors, activeColor, desc.FillAlpha);
                        runLen = 0;
                        hasPrev = false;
                    }
                    continue;
                }

                float yBase;
                if (refValues != null)
                {
                    if (!ChartPrimitiveMath.TryProjectY(refValues[valIdx], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out yBase))
                    {
                        if (desc.NullPolicy == NullGapPolicy.BreakPath)
                        {
                            FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors, activeColor, desc.FillAlpha);
                            runLen = 0;
                            hasPrev = false;
                        }
                        continue;
                    }
                }
                else
                {
                    yBase = scalarBaselineY;
                }

                float x = ChartPrimitiveMath.ComputeBarCenterX(
                    i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset);
                if (!float.IsFinite(x))
                {
                    FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors, activeColor, desc.FillAlpha);
                    runLen = 0;
                    hasPrev = false;
                    continue;
                }

                decimal val = valOpt!.Value;
                if (!twoColors)
                {
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

                    baselinePoints[runLen++] = new SKPoint(x, yBase);
                }
                else
                {
                    if (hasPrev)
                    {
                        SKColor segColor = val >= prevVal ? upColor : downColor;
                        if (runLen >= 2 && segColor != activeColor)
                        {
                            FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors: true, activeColor, desc.FillAlpha);
                            runLen = 0;
                        }

                        if (runLen == 0)
                        {
                            if (drawFill) _areaPath.MoveTo(prevX, prevY);
                            if (drawStroke) _linePath.MoveTo(prevX, prevY);
                            baselinePoints[runLen++] = new SKPoint(prevX, prevBaseY);
                            activeColor = segColor;
                        }

                        if (drawFill) _areaPath.LineTo(x, yTop);
                        if (drawStroke) _linePath.LineTo(x, yTop);
                        baselinePoints[runLen++] = new SKPoint(x, yBase);
                    }

                    hasPrev = true;
                    prevX = x;
                    prevY = yTop;
                    prevBaseY = yBase;
                    prevVal = val;
                }
            }

            FlushRun(context, baselinePoints, runLen, drawFill, drawStroke, twoColors, activeColor, desc.FillAlpha);
        }
        finally
        {
            _fillPaint.Shader = null;
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
    }

    private void FlushRun(
        in SeriesPrimitiveRenderContext context,
        ReadOnlySpan<SKPoint> baselinePoints,
        int runLen,
        bool drawFill,
        bool drawStroke,
        bool twoColors,
        SKColor runColor,
        byte fillAlpha)
    {
        if (runLen >= 2)
        {
            if (drawFill)
            {
                if (twoColors)
                {
                    _fillPaint.Shader = EnsureShader(context, runColor, fillAlpha);
                }

                for (int k = runLen - 1; k >= 0; k--)
                {
                    _areaPath.LineTo(baselinePoints[k].X, baselinePoints[k].Y);
                }
                _areaPath.Close();
                context.Canvas.DrawPath(_areaPath, _fillPaint);
            }

            if (drawStroke)
            {
                if (twoColors)
                {
                    _linePaint.Color = runColor;
                }

                context.Canvas.DrawPath(_linePath, _linePaint);
            }
        }

        _areaPath.Reset();
        _linePath.Reset();
    }

    private SKShader EnsureShader(in SeriesPrimitiveRenderContext context, SKColor baseColor, byte fillAlpha)
    {
        SKColor bgColor = context.Theme.ChartBackground.ToSkColor();
        float top = (float)context.ChartArea.Top;
        float bottom = (float)context.ChartArea.Bottom;

        int emptyIdx = -1;
        int lruIdx = 0;
        int oldestTick = int.MaxValue;
        for (int i = 0; i < ShaderCacheCapacity; i++)
        {
            ref var entry = ref _shaderCache[i];
            if (entry.Shader != null &&
                entry.BaseColor == baseColor &&
                entry.BgColor == bgColor &&
                entry.Top == top &&
                entry.Bottom == bottom &&
                entry.FillAlpha == fillAlpha)
            {
                entry.LastUseTick = ++_shaderUseCounter;
                return entry.Shader;
            }

            if (entry.Shader == null)
            {
                if (emptyIdx < 0) emptyIdx = i;
                continue;
            }

            if (entry.LastUseTick < oldestTick)
            {
                oldestTick = entry.LastUseTick;
                lruIdx = i;
            }
        }

        int victimIdx = emptyIdx >= 0 ? emptyIdx : lruIdx;
        _shaderCache[victimIdx].Shader?.Dispose();

        float luminance = IndicatorColor.Luma(bgColor.Red, bgColor.Green, bgColor.Blue);
        float alphaScale = ChartTheme.AreaChartAlphaBaseScale + ChartTheme.AreaChartAlphaLuminanceScale * luminance;
        byte alphaTop = (byte)Math.Clamp(MathF.Round(fillAlpha * alphaScale), 0f, 255f);
        byte alphaBottom = (byte)Math.Clamp(MathF.Round(fillAlpha * 0.2f * alphaScale), 0f, 255f);

        _gradientColors[0] = baseColor.WithAlpha(alphaTop);
        _gradientColors[1] = baseColor.WithAlpha(alphaBottom);

        float safeBottom = bottom > top ? bottom : top + 1.0f;
        var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, top),
            new SKPoint(0f, safeBottom),
            _gradientColors,
            null,
            SKShaderTileMode.Clamp);

        _shaderCache[victimIdx] = new ShaderCacheEntry
        {
            Shader = shader,
            BaseColor = baseColor,
            BgColor = bgColor,
            Top = top,
            Bottom = bottom,
            FillAlpha = fillAlpha,
            LastUseTick = ++_shaderUseCounter
        };
        return shader;
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _linePaint.Dispose();
        _areaPath.Dispose();
        _linePath.Dispose();
        for (int i = 0; i < ShaderCacheCapacity; i++)
        {
            _shaderCache[i].Shader?.Dispose();
            _shaderCache[i] = default;
        }
    }
}
