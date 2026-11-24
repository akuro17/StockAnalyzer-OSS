using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders a line whose color, stroke width, and alpha modulate per segment according to
/// <see cref="DynamicStyleMetric"/> using exact-color run-batching and rounded caps/joins.
/// Batch continuation checks <c>(widthKey == batchWidthKey) &amp;&amp; (segColor == batchColor)</c>,
/// where <c>widthKey = (int)MathF.Round(segWidth * 4.0f)</c> and <c>segColor</c> is the final
/// 32-bit ARGB <see cref="SKColor"/> (no 64-step color quantization; large-scale optimization uses
/// optional fixed domain bounds or sub-pixel LOD).
/// </summary>
internal sealed class DynamicStyleLineRenderer : ISeriesPrimitiveRenderer
{
    private enum PointStepStatus
    {
        Valid = 0,
        Break = 1,
        SkipKeepPrev = 2
    }

    private readonly SKPaint _paint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };
    private readonly SKPath _path = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        int n = context.VisibleCandles;
        if (values == null || values.Count == 0 || n <= 0) return;

        IReadOnlyList<decimal?>? auxSeries = desc.StyleMetric == DynamicStyleMetric.AuxiliarySeries && !string.IsNullOrEmpty(desc.TertiarySeriesName)
            ? context.Result.GetSeries(desc.TertiarySeriesName)
            : null;

        // Pass 1: resolve metric bounds (fixed domain skips O(V) extrema scan)
        double mMin;
        double mMax;
        bool hasMetric;

        if (ChartPrimitiveMath.TryGetFixedScaleDomain(desc, out decimal domMin, out decimal domMax))
        {
            mMin = (double)domMin;
            mMax = (double)domMax;
            hasMetric = true;
        }
        else
        {
            mMin = double.MaxValue;
            mMax = double.MinValue;
            hasMetric = false;

            bool scanHasPrev = false;
            decimal scanPrevVal = 0m;
            for (int i = 0; i < n; i++)
            {
                var status = EvaluatePoint(i, in context, values, desc.NullPolicy, out decimal val, out _, out _, out int valIdx);
                if (status == PointStepStatus.Break)
                {
                    scanHasPrev = false;
                    continue;
                }
                if (status == PointStepStatus.SkipKeepPrev)
                {
                    continue;
                }

                if (scanHasPrev && TryGetMetric(desc.StyleMetric, val, scanPrevVal, auxSeries, valIdx, out double m))
                {
                    if (m < mMin) mMin = m;
                    if (m > mMax) mMax = m;
                    hasMetric = true;
                }

                scanHasPrev = true;
                scanPrevVal = val;
            }
        }

        double mRange = hasMetric ? mMax - mMin : 0.0;

        float wMin = float.IsFinite(desc.MinStrokeWidthPx) ? Math.Clamp(desc.MinStrokeWidthPx, 0.5f, 12.0f) : 1.0f;
        float wMax = float.IsFinite(desc.MaxStrokeWidthPx) ? Math.Clamp(desc.MaxStrokeWidthPx, wMin, 12.0f) : Math.Max(wMin, 4.0f);
        byte aMin = Math.Min(desc.MinDynamicAlpha, desc.MaxDynamicAlpha);
        byte aMax = Math.Max(desc.MinDynamicAlpha, desc.MaxDynamicAlpha);

        bool interpolateColors = context.Setting.UseUpDownColors;
        OklabColor downLab = OklabColor.FromIndicatorColor(context.Setting.DownColor);
        OklabColor upLab = OklabColor.FromIndicatorColor(context.Setting.UpColor);
        SKColor baseSingleColor = context.SeriesColor;
        float baseWidth = Math.Clamp((float)context.Setting.Thickness, 0.5f, 12.0f);

        _paint.PathEffect = desc.LineStyle.ToSkPathEffect();
        _path.Reset();

        bool hasPrev = false;
        float prevX = 0f, prevY = 0f;
        decimal prevVal = 0m;
        bool hasSegment = false;
        SKColor batchColor = SKColors.Transparent;
        float batchWidth = baseWidth;
        int batchWidthKey = -1;

        for (int i = 0; i < n; i++)
        {
            var status = EvaluatePoint(i, in context, values, desc.NullPolicy, out decimal val, out float x, out float y, out int valIdx);
            if (status == PointStepStatus.Break)
            {
                Flush(context.Canvas, ref hasSegment, batchColor, batchWidth);
                hasPrev = false;
                continue;
            }
            if (status == PointStepStatus.SkipKeepPrev)
            {
                continue;
            }

            if (hasPrev)
            {
                SKColor segColor;
                float segWidth;

                if (hasMetric && TryGetMetric(desc.StyleMetric, val, prevVal, auxSeries, valIdx, out double m))
                {
                    double u = mRange > 1e-12 ? Math.Clamp((m - mMin) / mRange, 0.0, 1.0) : 0.5;
                    byte alpha = (byte)Math.Clamp(MathF.Round(aMin + (float)u * (aMax - aMin)), 0f, 255f);
                    segWidth = Math.Clamp(wMin + (float)u * (wMax - wMin), 0.5f, 12.0f);
                    segColor = interpolateColors
                        ? OklabColor.Lerp(downLab, upLab, u).ToIndicatorColorInGamut(alpha).ToSkColor()
                        : baseSingleColor.WithAlpha(alpha);
                }
                else
                {
                    segColor = baseSingleColor;
                    segWidth = baseWidth;
                }

                int widthKey = (int)MathF.Round(segWidth * 4.0f);
                if (hasSegment && (segColor != batchColor || widthKey != batchWidthKey))
                {
                    Flush(context.Canvas, ref hasSegment, batchColor, batchWidth);
                }

                if (!hasSegment)
                {
                    _path.MoveTo(prevX, prevY);
                    batchColor = segColor;
                    batchWidth = widthKey * 0.25f;
                    batchWidthKey = widthKey;
                    hasSegment = true;
                }

                _path.LineTo(x, y);
            }

            hasPrev = true;
            prevX = x;
            prevY = y;
            prevVal = val;
        }

        Flush(context.Canvas, ref hasSegment, batchColor, batchWidth);
    }

    /// <summary>
    /// Single source of truth for bar validity and gap transitions shared identically by Pass 1 (metric extrema)
    /// and Pass 2 (rendering). Non-finite X coordinates always break the run regardless of <see cref="NullGapPolicy"/>.
    /// </summary>
    private static PointStepStatus EvaluatePoint(
        int barIndex,
        in SeriesPrimitiveRenderContext context,
        IReadOnlyList<decimal?> values,
        NullGapPolicy nullPolicy,
        out decimal value,
        out float x,
        out float y,
        out int valIdx)
    {
        valIdx = barIndex - context.Offset;
        if (valIdx < 0 || valIdx >= values.Count)
        {
            value = 0m;
            x = float.NaN;
            y = float.NaN;
            return PointStepStatus.Break;
        }

        decimal? raw = values[valIdx];
        if (!ChartPrimitiveMath.TryProjectY(raw, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out y))
        {
            value = 0m;
            x = float.NaN;
            return nullPolicy == NullGapPolicy.BreakPath ? PointStepStatus.Break : PointStepStatus.SkipKeepPrev;
        }

        x = ChartPrimitiveMath.ComputeBarCenterX(
            barIndex, context.ColumnWidth, context.ChartArea, context.StartIndex,
            context.Candles, context.Interval, context.Transform,
            context.Snapshot, context.Config, context.XOffset);
        if (!float.IsFinite(x))
        {
            value = 0m;
            return PointStepStatus.Break;
        }

        value = raw!.Value;
        return PointStepStatus.Valid;
    }

    private static bool TryGetMetric(
        DynamicStyleMetric metric,
        decimal curr,
        decimal prev,
        IReadOnlyList<decimal?>? auxSeries,
        int valIdx,
        out double value)
    {
        switch (metric)
        {
            case DynamicStyleMetric.Value:
                value = (double)curr;
                return double.IsFinite(value);

            case DynamicStyleMetric.Delta:
                value = Math.Abs((double)(curr - prev));
                return double.IsFinite(value);

            case DynamicStyleMetric.AuxiliarySeries:
                if (auxSeries != null && valIdx >= 0 && valIdx < auxSeries.Count && auxSeries[valIdx].HasValue)
                {
                    value = (double)auxSeries[valIdx]!.Value;
                    return double.IsFinite(value);
                }
                value = 0.0;
                return false;

            default: // DynamicStyleMetric.Slope
                value = (double)(curr - prev);
                return double.IsFinite(value);
        }
    }

    private void Flush(SKCanvas canvas, ref bool hasSegment, SKColor color, float width)
    {
        if (!hasSegment) return;
        _paint.Color = color;
        _paint.StrokeWidth = Math.Clamp(width, 0.5f, 12.0f);
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
