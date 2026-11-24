using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Rendering;

/// <summary>
/// Single source of truth for Y-axis value range contribution of primitive-aware indicator results.
/// Shared by panel auto-scaling (<c>PanelValueRangeCalculator</c>) and main-chart overlay scaling (<c>ChartDataSnapshot</c>).
/// Allocation-free: uses plain loops and O(1) scalar accumulators.
/// </summary>
public static class SeriesPrimitiveValueRangeHelper
{
    /// <summary>
    /// Accumulates min/max Y-axis bounds for all non-suppressed <paramref name="descriptors"/>.
    /// Non-Y auxiliary series (Bubble size, Vector polar angle/length, DynamicStyling driver,
    /// Waterfall bar kind, ColorStrip scalars) are excluded from Y-axis scaling.
    /// </summary>
    public static void AccumulateRange(
        IIndicatorResult result,
        IReadOnlyList<SeriesPrimitiveDescriptor> descriptors,
        CoreIndicatorSettings setting,
        int maxSampleCount,
        PriceScaleType priceScale,
        ref decimal minVal,
        ref decimal maxVal,
        ref bool hasValue,
        int offset = 0)
    {
        if (result == null || descriptors == null || descriptors.Count == 0 || maxSampleCount <= 0)
        {
            return;
        }

        for (int d = 0; d < descriptors.Count; d++)
        {
            var desc = descriptors[d];
            if (desc.PrimitiveType == SeriesPrimitiveType.ColorStrip)
            {
                // ColorStrip renders at a fixed anchor strip height and maps values to Oklab colors, not Y coordinates.
                continue;
            }

            if (desc.PrimitiveType == SeriesPrimitiveType.StackedArea)
            {
                AccumulateStackedArea(result, desc, setting, maxSampleCount, priceScale, offset, ref minVal, ref maxVal, ref hasValue);
                continue;
            }

            bool includeSecondary = desc.PrimitiveType switch
            {
                SeriesPrimitiveType.BandCloud => true,
                SeriesPrimitiveType.FloatingBar => true,
                SeriesPrimitiveType.Waterfall => true,
                SeriesPrimitiveType.Area => desc.BaselineMode == AreaBaselineMode.ReferenceSeries,
                SeriesPrimitiveType.Vector => desc.VectorMode == VectorInputMode.PriceSpaceEndpoints,
                _ => false
            };

            if (includeSecondary &&
                (!SeriesRenderStyleCatalog.IsSeriesVisible(setting, desc.PrimarySeriesName) ||
                 (!string.IsNullOrEmpty(desc.SecondarySeriesName) && !SeriesRenderStyleCatalog.IsSeriesVisible(setting, desc.SecondarySeriesName))))
            {
                // Multi-series primitive descriptors (BandCloud, FloatingBar, etc.) are hidden as a unit when any bound endpoint series is hidden.
                continue;
            }

            bool primaryChartable = IsChartableSeries(setting, desc.PrimarySeriesName);
            if (primaryChartable)
            {
                ScanSeries(result.GetSeries(desc.PrimarySeriesName), maxSampleCount, priceScale, offset, ref minVal, ref maxVal, ref hasValue);
            }

            if (includeSecondary && !string.IsNullOrEmpty(desc.SecondarySeriesName) && IsChartableSeries(setting, desc.SecondarySeriesName))
            {
                ScanSeries(result.GetSeries(desc.SecondarySeriesName), maxSampleCount, priceScale, offset, ref minVal, ref maxVal, ref hasValue);
            }

            var effectivePrimitive = SeriesRenderStyleCatalog.ResolveEffectivePrimitive(setting, desc.PrimarySeriesName);
            if (primaryChartable &&
                desc.PrimitiveType == SeriesPrimitiveType.PositiveNegativeArea &&
                effectivePrimitive == SeriesPrimitiveType.PositiveNegativeArea &&
                IsPriceValid(desc.BaselineValue, priceScale))
            {
                IncludePoint(desc.BaselineValue, ref minVal, ref maxVal, ref hasValue);
            }
            else if (primaryChartable &&
                     hasValue &&
                     (minVal <= 0m || setting.IncludeZeroBaseline) &&
                     SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(setting, desc.PrimarySeriesName, priceScale) &&
                     IsPriceValid(0m, priceScale))
            {
                IncludePoint(0m, ref minVal, ref maxVal, ref hasValue);
            }
        }
    }

    private static void AccumulateStackedArea(
        IIndicatorResult result,
        SeriesPrimitiveDescriptor desc,
        CoreIndicatorSettings setting,
        int maxSampleCount,
        PriceScaleType priceScale,
        int offset,
        ref decimal minVal,
        ref decimal maxVal,
        ref bool hasValue)
    {
        var layers = desc.StackedSeriesNames;
        int layerCount = layers is { Count: > 0 } ? layers.Count : 1;

        int barCount = 0;
        for (int k = 0; k < layerCount; k++)
        {
            string layerName = layers is { Count: > 0 } ? layers[k] : desc.PrimarySeriesName;
            if (!IsChartableSeries(setting, layerName)) continue;
            var s = result.GetSeries(layerName);
            if (s != null && s.Count > barCount)
            {
                barCount = s.Count;
            }
        }

        int startI = Math.Max(0, offset);
        long rawEnd = (long)barCount + offset;
        int endI = rawEnd <= 0 ? 0 : (int)Math.Min((long)maxSampleCount, rawEnd);
        for (int i = startI; i < endI; i++)
        {
            int valIdx = i - offset;

            if (desc.StackingNullPolicy == StackingNullMode.BreakAllLayers &&
                BarHasNullChartableLayer(result, setting, layers, desc.PrimarySeriesName, layerCount, valIdx))
            {
                continue;
            }

            decimal posSum = desc.StackingMode == StackingSignMode.NonNegativeClamp ? desc.BaselineValue : 0m;
            decimal negSum = 0m;
            decimal rawSum = 0m;
            bool anyValid = false;

            for (int k = 0; k < layerCount; k++)
            {
                string layerName = layers is { Count: > 0 } ? layers[k] : desc.PrimarySeriesName;
                if (!IsChartableSeries(setting, layerName)) continue;
                var s = result.GetSeries(layerName);
                decimal? v = (s != null && valIdx < s.Count) ? s[valIdx] : null;

                if (!v.HasValue)
                {
                    continue;
                }

                anyValid = true;
                decimal val = v.Value;

                switch (desc.StackingMode)
                {
                    case StackingSignMode.NonNegativeClamp:
                        if (val > 0m) posSum += val;
                        break;

                    case StackingSignMode.RawCumulative:
                        rawSum += val;
                        if (IsPriceValid(rawSum, priceScale))
                        {
                            IncludePoint(rawSum, ref minVal, ref maxVal, ref hasValue);
                        }
                        break;

                    default: // ZeroBaselineSplit
                        if (val >= 0m) posSum += val;
                        else negSum += val;
                        break;
                }
            }

            if (!anyValid) continue;

            if (desc.StackingMode == StackingSignMode.NonNegativeClamp)
            {
                if (IsPriceValid(desc.BaselineValue, priceScale)) IncludePoint(desc.BaselineValue, ref minVal, ref maxVal, ref hasValue);
                if (IsPriceValid(posSum, priceScale)) IncludePoint(posSum, ref minVal, ref maxVal, ref hasValue);
            }
            else if (desc.StackingMode == StackingSignMode.RawCumulative)
            {
                if (IsPriceValid(0m, priceScale)) IncludePoint(0m, ref minVal, ref maxVal, ref hasValue);
            }
            else if (desc.StackingMode == StackingSignMode.ZeroBaselineSplit)
            {
                if (IsPriceValid(posSum, priceScale)) IncludePoint(posSum, ref minVal, ref maxVal, ref hasValue);
                if (IsPriceValid(negSum, priceScale)) IncludePoint(negSum, ref minVal, ref maxVal, ref hasValue);
            }
        }
    }

    private static bool BarHasNullChartableLayer(
        IIndicatorResult result,
        CoreIndicatorSettings setting,
        IReadOnlyList<string>? layers,
        string fallbackPrimary,
        int layerCount,
        int valIdx)
    {
        for (int k = 0; k < layerCount; k++)
        {
            string layerName = layers is { Count: > 0 } ? layers[k] : fallbackPrimary;
            if (!IsChartableSeries(setting, layerName)) continue;
            var s = result.GetSeries(layerName);
            if (s == null || valIdx >= s.Count || !s[valIdx].HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private static void ScanSeries(
        IReadOnlyList<decimal?>? series,
        int maxSampleCount,
        PriceScaleType priceScale,
        int offset,
        ref decimal minVal,
        ref decimal maxVal,
        ref bool hasValue)
    {
        if (series == null || series.Count == 0) return;

        int startI = Math.Max(0, offset);
        long rawEnd = (long)series.Count + offset;
        int endI = rawEnd <= 0 ? 0 : (int)Math.Min((long)maxSampleCount, rawEnd);
        for (int i = startI; i < endI; i++)
        {
            int valIdx = i - offset;
            var v = series[valIdx];
            if (v.HasValue && IsPriceValid(v.Value, priceScale))
            {
                IncludePoint(v.Value, ref minVal, ref maxVal, ref hasValue);
            }
        }
    }

    private static bool IsChartableSeries(CoreIndicatorSettings setting, string seriesName)
    {
        if (string.IsNullOrEmpty(seriesName)) return false;
        if (string.Equals(seriesName, IndicatorSeriesNames.UpTrendFlag, StringComparison.Ordinal)) return false;
        return !SeriesRenderStyleCatalog.ShouldExcludeFromYRange(setting, seriesName);
    }

    private static bool IsPriceValid(decimal value, PriceScaleType priceScale) =>
        priceScale != PriceScaleType.Log || value > 0m;

    private static void IncludePoint(decimal value, ref decimal minVal, ref decimal maxVal, ref bool hasValue)
    {
        if (value < minVal) minVal = value;
        if (value > maxVal) maxVal = value;
        hasValue = true;
    }
}
