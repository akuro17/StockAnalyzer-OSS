using System;
using System.Buffers;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders time-series bubbles at (bar_i, PrimarySeries[i]) with area-proportional radius scaling
/// from <c>SecondarySeriesName</c> and optional descending-radius Z-order sorting so small bubbles stay visible.
/// </summary>
internal sealed class BubbleRenderer : ISeriesPrimitiveRenderer
{
    private const int InsertionSortMaxCount = 32;

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f, IsAntialias = true };

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        int n = context.VisibleCandles;
        if (string.IsNullOrEmpty(desc.SecondarySeriesName) || n <= 0) return;

        var ySeries = context.Result.GetSeries(desc.PrimarySeriesName);
        var sizeSeries = context.Result.GetSeries(desc.SecondarySeriesName);
        if (ySeries == null || sizeSeries == null || ySeries.Count == 0 || sizeSeries.Count == 0) return;

        bool drawFill = desc.FillAlpha > 0;
        bool drawStroke = desc.DrawBorderStroke;
        if (!drawFill && !drawStroke) return;

        double sMin;
        double sMax;
        int validEstimate;

        if (ChartPrimitiveMath.TryGetFixedScaleDomain(desc, out decimal domMin, out decimal domMax))
        {
            double absMin = Math.Abs((double)domMin);
            double absMax = Math.Abs((double)domMax);
            sMin = (domMin <= 0m && domMax >= 0m) ? 0.0 : Math.Min(absMin, absMax);
            sMax = Math.Max(absMin, absMax);
            validEstimate = n;
        }
        else
        {
            // Pass 1: find visible size extrema
            sMin = double.MaxValue;
            sMax = double.MinValue;
            validEstimate = 0;

            for (int i = 0; i < n; i++)
            {
                int valIdx = i - context.Offset;
                if (valIdx < 0 || valIdx >= ySeries.Count || valIdx >= sizeSeries.Count) continue;
                decimal? sOpt = sizeSeries[valIdx];
                if (!IsSizeValid(sOpt, desc.BubbleDrawZeroSize, desc.BubbleAllowSignedSize)) continue;
                if (!ChartPrimitiveMath.TryProjectY(ySeries[valIdx], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out _)) continue;

                double mag = Math.Abs((double)sOpt!.Value);
                if (mag < sMin) sMin = mag;
                if (mag > sMax) sMax = mag;
                validEstimate++;
            }

            if (validEstimate == 0) return;
        }

        float rMin = float.IsFinite(desc.BubbleMinRadiusPx) ? Math.Clamp(desc.BubbleMinRadiusPx, 0.5f, 64.0f) : 2.0f;
        float rMax = float.IsFinite(desc.BubbleMaxRadiusPx) ? Math.Clamp(desc.BubbleMaxRadiusPx, rMin, 64.0f) : Math.Max(rMin, 16.0f);
        double sizeRange = sMax - sMin;
        float rMinSq = rMin * rMin;
        float rMaxSq = rMax * rMax;
        float flatRadius = MathF.Sqrt((rMinSq + rMaxSq) * 0.5f);

        int[]? rentedIndices = validEstimate > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<int>.Shared.Rent(validEstimate)
            : null;
        float[]? rentedRadii = validEstimate > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<float>.Shared.Rent(validEstimate)
            : null;
        float[]? rentedX = validEstimate > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<float>.Shared.Rent(validEstimate)
            : null;
        float[]? rentedY = validEstimate > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<float>.Shared.Rent(validEstimate)
            : null;
        int[]? rentedValIdx = validEstimate > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<int>.Shared.Rent(validEstimate)
            : null;
        ulong[]? rentedSortKeys = null;

        try
        {
            Span<int> order = rentedIndices != null ? rentedIndices.AsSpan(0, validEstimate) : stackalloc int[validEstimate];
            Span<float> radii = rentedRadii != null ? rentedRadii.AsSpan(0, validEstimate) : stackalloc float[validEstimate];
            Span<float> xs = rentedX != null ? rentedX.AsSpan(0, validEstimate) : stackalloc float[validEstimate];
            Span<float> ys = rentedY != null ? rentedY.AsSpan(0, validEstimate) : stackalloc float[validEstimate];
            Span<int> valIndices = rentedValIdx != null ? rentedValIdx.AsSpan(0, validEstimate) : stackalloc int[validEstimate];

            int count = 0;
            for (int i = 0; i < n && count < validEstimate; i++)
            {
                int valIdx = i - context.Offset;
                if (valIdx < 0 || valIdx >= ySeries.Count || valIdx >= sizeSeries.Count) continue;

                decimal? sOpt = sizeSeries[valIdx];
                if (!IsSizeValid(sOpt, desc.BubbleDrawZeroSize, desc.BubbleAllowSignedSize)) continue;

                if (!ChartPrimitiveMath.TryProjectY(ySeries[valIdx], context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float cy)) continue;
                float cx = ChartPrimitiveMath.ComputeBarCenterX(
                    i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset);
                if (!float.IsFinite(cx)) continue;

                double mag = Math.Abs((double)sOpt!.Value);
                float r;
                if (sizeRange <= 1e-12)
                {
                    r = flatRadius;
                }
                else
                {
                    float u = (float)Math.Clamp((mag - sMin) / sizeRange, 0.0, 1.0);
                    r = MathF.Sqrt(rMinSq + (rMaxSq - rMinSq) * u);
                }

                order[count] = count;
                radii[count] = r;
                xs[count] = cx;
                ys[count] = cy;
                valIndices[count] = valIdx;
                count++;
            }

            if (count == 0) return;

            if (desc.BubbleSortDescending && count > 1)
            {
                if (count <= InsertionSortMaxCount)
                {
                    SortIndicesByRadiusDescending(order.Slice(0, count), radii);
                }
                else
                {
                    rentedSortKeys = ArrayPool<ulong>.Shared.Rent(count);
                    Span<ulong> keys = rentedSortKeys.AsSpan(0, count);
                    for (int k = 0; k < count; k++)
                    {
                        uint bits = BitConverter.SingleToUInt32Bits(radii[k]);
                        keys[k] = ((ulong)(~bits) << 32) | (uint)k;
                    }
                    keys.Sort(order.Slice(0, count));
                }
            }

            bool twoColors = context.Setting.UseUpDownColors;
            SKColor upColor = context.Setting.UpColor.ToSkColor();
            SKColor downColor = context.Setting.DownColor.ToSkColor();
            SKColor singleColor = context.SeriesColor;

            for (int k = 0; k < count; k++)
            {
                int slot = order[k];
                float r = radii[slot];
                float cx = xs[slot];
                float cy = ys[slot];

                SKColor baseCol = singleColor;
                if (twoColors)
                {
                    decimal signedSize = sizeSeries[valIndices[slot]]!.Value;
                    baseCol = signedSize >= 0m ? upColor : downColor;
                }

                if (drawFill)
                {
                    _fillPaint.Color = baseCol.WithAlpha(desc.FillAlpha);
                    context.Canvas.DrawCircle(cx, cy, r, _fillPaint);
                }

                if (drawStroke)
                {
                    _strokePaint.Color = baseCol;
                    context.Canvas.DrawCircle(cx, cy, r, _strokePaint);
                }
            }
        }
        finally
        {
            if (rentedSortKeys != null) ArrayPool<ulong>.Shared.Return(rentedSortKeys);
            if (rentedIndices != null) ArrayPool<int>.Shared.Return(rentedIndices);
            if (rentedRadii != null) ArrayPool<float>.Shared.Return(rentedRadii);
            if (rentedX != null) ArrayPool<float>.Shared.Return(rentedX);
            if (rentedY != null) ArrayPool<float>.Shared.Return(rentedY);
            if (rentedValIdx != null) ArrayPool<int>.Shared.Return(rentedValIdx);
        }
    }

    private static bool IsSizeValid(decimal? s, bool drawZero, bool allowSigned)
    {
        if (!s.HasValue) return false;
        if (s.Value == 0m) return drawZero;
        if (s.Value < 0m) return allowSigned;
        return true;
    }

    private static void SortIndicesByRadiusDescending(Span<int> order, ReadOnlySpan<float> radii)
    {
        for (int i = 1; i < order.Length; i++)
        {
            int key = order[i];
            float keyR = radii[key];
            int j = i - 1;
            while (j >= 0 && radii[order[j]] < keyR)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
    }
}
