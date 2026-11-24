using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Single source of truth for coordinate projection, pixel snapping, crossing-point interpolation,
/// and normalized rectangle geometry shared across chart rendering primitives.
/// </summary>
internal static class ChartPrimitiveMath
{
    /// <summary>
    /// Spacing margin factor to avoid adjacent dot collision: (1 - 0.10) / 2 = 0.45.
    /// Shared with <see cref="DotChartRenderer"/>.
    /// </summary>
    internal const float DotSpacingMarginFactor = 0.45f;

    /// <summary>
    /// Minimum effective dot rendering radius (0.5 px). Shared with <see cref="DotChartRenderer"/>.
    /// </summary>
    internal const float DotMinEffectiveRadius = 0.5f;

    /// <summary>
    /// Equal-area diamond scaling factor sqrt(pi / 2) ~ 1.2533. Shared with <see cref="DotChartRenderer"/>.
    /// </summary>
    internal const float DotDiamondScaleFactor = 1.2533f;

    /// <summary>
    /// Cosine of 30 degrees (sqrt(3) / 2 ~ 0.8660254), shared by equilateral triangles and arrow trims.
    /// </summary>
    internal const float Cos30 = 0.8660254f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float SnapPixel(float coord) => MathF.Floor(coord) + 0.5f;

    internal static TimeSpan ComputeCandleInterval(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles != null && candles.Count > 1)
        {
            var interval = candles[candles.Count - 1].Timestamp - candles[candles.Count - 2].Timestamp;
            if (interval > TimeSpan.Zero) return interval;
        }
        return TimeSpan.FromDays(1);
    }

    internal static DateTime GetCandleTime(int index, IReadOnlyList<CoreCandleData> candles, TimeSpan interval)
    {
        if (candles.Count == 0) return DateTime.MinValue;
        if (index >= 0 && index < candles.Count) return candles[index].Timestamp;
        if (index < 0)
        {
            return candles[0].Timestamp.AddTicks(interval.Ticks * index);
        }

        var lastCandle = candles[candles.Count - 1];
        int diff = index - (candles.Count - 1);
        return lastCandle.Timestamp.AddTicks(interval.Ticks * diff);
    }

    internal static float ComputeColumnWidth(
        Rect chartArea,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        int visibleCandles,
        ICoordinateTransform? transform,
        bool isIndexBased,
        int startIndex)
    {
        if (transform != null && candles != null && candles.Count > 0)
        {
            double x0;
            double x1;
            if (isIndexBased)
            {
                x0 = transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(startIndex, 0m)).X;
                x1 = transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(startIndex + 1, 0m)).X;
            }
            else if (candles.Count > 1)
            {
                x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0m)).X;
                x1 = transform.ChartToScreen(new ChartPoint(candles[1].Timestamp, 0m)).X;
            }
            else
            {
                x0 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0m)).X;
                x1 = transform.ChartToScreen(new ChartPoint(candles[0].Timestamp + interval, 0m)).X;
            }

            float w = (float)(x1 - x0);
            if (float.IsFinite(w) && w > 0f) return w;
        }

        return visibleCandles > 0 ? (float)(chartArea.Width / visibleCandles) : 0f;
    }

    internal static float ComputeBarCenterX(
        int index,
        float columnWidth,
        Rect chartArea,
        int startIndex,
        IReadOnlyList<CoreCandleData> candles,
        TimeSpan interval,
        ICoordinateTransform? transform,
        ChartDataSnapshot? snapshot,
        IChartRenderConfig? config,
        float xOffset)
    {
        if (transform == null)
        {
            return (float)chartArea.Left + index * columnWidth + xOffset;
        }

        // SubWindowCoordinateTransform wraps _mainTransform and is NOT a GenericCoordinateTransform,
        // so ChartType.IsIndexBased() must also be checked explicitly.
        bool isIndexMode = (config?.ChartType.IsIndexBased() ?? false) ||
                           (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index);

        if (isIndexMode)
        {
            if (snapshot != null && config != null && config.ChartType.IsIndexBased() && index >= 0 && index < candles.Count)
            {
                double logicalIdx = snapshot.GetLogicalXIndex(index, config);
                float x = (float)transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(logicalIdx, 0m)).X;
                return config.ChartType == ChartType.PointAndFigure ? x : x + columnWidth * 0.5f;
            }

            return (float)transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(startIndex + index, 0m)).X + xOffset;
        }

        var t = GetCandleTime(index, candles, interval);
        return (float)transform.ChartToScreen(new ChartPoint(t, 0m)).X + xOffset;
    }

    internal static bool TryProjectY(
        decimal? value,
        Rect chartArea,
        ICoordinateTransform? transform,
        PriceScaleType priceScale,
        out float screenY)
    {
        if (!value.HasValue || transform == null)
        {
            screenY = float.NaN;
            return false;
        }

        if (priceScale == PriceScaleType.Log && value.Value <= 0m)
        {
            screenY = float.NaN;
            return false;
        }

        double rawY = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, value.Value)).Y;
        float y = (float)(rawY + chartArea.Top);
        if (!float.IsFinite(y))
        {
            screenY = float.NaN;
            return false;
        }

        screenY = y;
        return true;
    }

    internal static bool TryComputeCrossingT(double d1, double d2, out double t)
    {
        double denominator = d1 - d2;
        double tolerance = Math.Max(Math.Abs(d1) + Math.Abs(d2), 1.0) * 1e-9;
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < tolerance)
        {
            t = 0.0;
            return false;
        }

        t = Math.Clamp(d1 / denominator, 0.0, 1.0);
        return double.IsFinite(t);
    }

    internal static SKRect NormalizeRectWithMinHeight(float left, float width, float y1, float y2, float minHeight = 1.0f)
    {
        float top = MathF.Min(y1, y2);
        float bottom = MathF.Max(y1, y2);
        if (bottom - top < minHeight)
        {
            float mid = (top + bottom) * 0.5f;
            float half = minHeight * 0.5f;
            top = mid - half;
            bottom = mid + half;
        }

        return new SKRect(left, top, left + MathF.Max(1.0f, width), bottom);
    }

    internal static bool TryGetFixedScaleDomain(
        Core.Models.Rendering.SeriesPrimitiveDescriptor desc,
        out decimal domainMin,
        out decimal domainMax)
    {
        if (desc.ScaleDomainMin.HasValue &&
            desc.ScaleDomainMax.HasValue &&
            desc.ScaleDomainMax.Value > desc.ScaleDomainMin.Value)
        {
            domainMin = desc.ScaleDomainMin.Value;
            domainMax = desc.ScaleDomainMax.Value;
            return true;
        }

        domainMin = 0m;
        domainMax = 0m;
        return false;
    }

    internal static SeriesColorConfig? FindSeriesColor(
        ObservableCollection<SeriesColorConfig>? colors,
        string? targetName,
        bool matchByNameFallback = false)
    {
        if (colors == null || string.IsNullOrEmpty(targetName)) return null;
        for (int i = 0; i < colors.Count; i++)
        {
            var entry = colors[i];
            if (entry == null) continue;
            if ((entry.TargetSeries != null && entry.TargetSeries.Contains(targetName)) ||
                (matchByNameFallback && string.Equals(entry.Name, targetName, StringComparison.Ordinal)))
            {
                return entry;
            }
        }
        return null;
    }
}
