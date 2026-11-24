using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Indicators.Trend;

namespace StockAnalyzer.Core.Models.Rendering;

/// <summary>
/// Single source of truth for selectable single-series rendering primitives, indicator
/// render-style capability gates, per-series visibility resolution, effective primitive
/// resolution, null-gap policy, and Y-axis range exclusion.
/// All query paths are allocation-free.
/// </summary>
public static class SeriesRenderStyleCatalog
{
    private static readonly ReadOnlyCollection<SeriesPrimitiveType> SelectablePrimitiveList = Array.AsReadOnly(
    [
        SeriesPrimitiveType.Area,
        SeriesPrimitiveType.DashedLine,
        SeriesPrimitiveType.Dot,
        SeriesPrimitiveType.DynamicStyling,
        SeriesPrimitiveType.Histogram,
        SeriesPrimitiveType.HorizontalSegment,
        SeriesPrimitiveType.Line,
        SeriesPrimitiveType.Lollipop,
        SeriesPrimitiveType.MarkerSymbol,
        SeriesPrimitiveType.PositiveNegativeArea,
        SeriesPrimitiveType.Spline,
        SeriesPrimitiveType.StepLine
    ]);

    /// <summary>
    /// Ordered 12 self-contained single-series primitive modes selectable in Color Settings.
    /// Excludes auxiliary/scalar modes (<see cref="SeriesPrimitiveType.ColorStrip"/>) and multi-series modes
    /// (BandCloud, FloatingBar, StackedArea, Bubble, Vector, Waterfall).
    /// </summary>
    public static IReadOnlyList<SeriesPrimitiveType> SelectablePrimitives => SelectablePrimitiveList;

    /// <summary>
    /// Returns true if <paramref name="primitive"/> is one of the 12 user-selectable single-series modes.
    /// </summary>
    public static bool IsSelectablePrimitive(SeriesPrimitiveType primitive) => primitive switch
    {
        SeriesPrimitiveType.Line => true,
        SeriesPrimitiveType.DashedLine => true,
        SeriesPrimitiveType.Dot => true,
        SeriesPrimitiveType.Histogram => true,
        SeriesPrimitiveType.StepLine => true,
        SeriesPrimitiveType.HorizontalSegment => true,
        SeriesPrimitiveType.Spline => true,
        SeriesPrimitiveType.Area => true,
        SeriesPrimitiveType.PositiveNegativeArea => true,
        SeriesPrimitiveType.MarkerSymbol => true,
        SeriesPrimitiveType.DynamicStyling => true,
        SeriesPrimitiveType.Lollipop => true,
        _ => false
    };

    /// <summary>
    /// Returns true if the indicator supports per-series visibility toggling and render-style selection
    /// in Indicator Manager Color Settings. Non-series structural/profile custom renderers return false.
    /// </summary>
    public static bool SupportsRenderStyleSelection(IndicatorType? type)
    {
        if (!type.HasValue)
        {
            return true;
        }

        if (BlockChartOverlayTypes.Contains(type))
        {
            return false;
        }

        return type.Value switch
        {
            IndicatorType.VolumeProfile => false,
            IndicatorType.TimeAtPrice => false,
            IndicatorType.MarketStructureShift => false,
            IndicatorType.RenkoOverlay => false,
            IndicatorType.KagiOverlay => false,
            IndicatorType.PointAndFigureOverlay => false,
            _ => true
        };
    }

    /// <summary>
    /// Canonicalizes known indicator-specific series aliases so rendering, range calculation,
    /// cross detection, and hover providers resolve the exact same configuration entry.
    /// Currently maps <see cref="CoreIchimokuIndicator.TenkanSenSeriesName"/> to <see cref="IndicatorResult.MainSeriesName"/>.
    /// </summary>
    public static string? NormalizeSeriesName(IndicatorType? type, string? seriesName)
    {
        if (string.IsNullOrEmpty(seriesName))
        {
            return seriesName;
        }

        if (type == IndicatorType.Ichimoku &&
            string.Equals(seriesName, CoreIchimokuIndicator.TenkanSenSeriesName, StringComparison.OrdinalIgnoreCase))
        {
            return IndicatorResult.MainSeriesName;
        }

        return seriesName;
    }

    /// <summary>
    /// Compares two series names after canonicalizing indicator-specific aliases using case-insensitive ordinal equality.
    /// </summary>
    public static bool AreSeriesNamesEquivalent(IndicatorType? type, string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        string? normalizedLeft = NormalizeSeriesName(type, left);
        string? normalizedRight = NormalizeSeriesName(type, right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the first <see cref="SeriesColorConfig"/> whose <see cref="SeriesColorConfig.TargetSeries"/>
    /// matches <paramref name="seriesName"/> (after indicator alias normalization) using a
    /// non-allocating case-insensitive linear scan.
    /// </summary>
    public static SeriesColorConfig? FindSeriesConfig(CoreIndicatorSettings? setting, string? seriesName)
    {
        if (setting?.SeriesColors == null || setting.SeriesColors.Count == 0 || string.IsNullOrEmpty(seriesName))
        {
            return null;
        }

        var colors = setting.SeriesColors;
        for (int i = 0; i < colors.Count; i++)
        {
            var entry = colors[i];
            var targets = entry?.TargetSeries;
            if (targets == null)
            {
                continue;
            }

            for (int j = 0; j < targets.Count; j++)
            {
                if (AreSeriesNamesEquivalent(setting.TypeEnum, targets[j], seriesName))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves whether <paramref name="seriesName"/> is visible according to <paramref name="setting"/>.
    /// </summary>
    public static bool IsSeriesVisible(CoreIndicatorSettings? setting, string? seriesName)
    {
        if (setting == null)
        {
            return true;
        }

        var config = FindSeriesConfig(setting, seriesName);
        if (config != null)
        {
            return config.IsVisible;
        }

        if ((setting.SeriesColors == null || setting.SeriesColors.Count == 0) && IsMainSeriesName(setting, seriesName))
        {
            return setting.IsMainSeriesVisible;
        }

        return true;
    }

    /// <summary>
    /// Returns the explicit selectable render primitive override configured for <paramref name="seriesName"/>,
    /// or null if unset or outside <see cref="SelectablePrimitives"/>.
    /// </summary>
    public static SeriesPrimitiveType? ResolveExplicitPrimitive(CoreIndicatorSettings? setting, string? seriesName)
    {
        if (setting == null)
        {
            return null;
        }

        SeriesPrimitiveType? candidate = null;
        var config = FindSeriesConfig(setting, seriesName);
        if (config != null)
        {
            candidate = config.RenderPrimitive;
        }
        else if ((setting.SeriesColors == null || setting.SeriesColors.Count == 0) && IsMainSeriesName(setting, seriesName))
        {
            candidate = setting.MainRenderPrimitive;
        }

        return candidate.HasValue && IsSelectablePrimitive(candidate.Value)
            ? candidate.Value
            : null;
    }

    /// <summary>
    /// Resolves the legacy default primitive for <paramref name="setting"/> and <paramref name="seriesName"/>
    /// when no explicit override is configured.
    /// </summary>
    public static SeriesPrimitiveType ResolveDefaultPrimitive(CoreIndicatorSettings? setting, string? seriesName)
    {
        if (!string.IsNullOrEmpty(seriesName) &&
            seriesName.Contains("Histogram", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesPrimitiveType.Histogram;
        }

        if (setting?.TypeEnum == IndicatorType.Volume)
        {
            return SeriesPrimitiveType.Histogram;
        }

        if (setting?.TypeEnum == IndicatorType.ParabolicSAR)
        {
            return SeriesPrimitiveType.Dot;
        }

        if (setting != null)
        {
            switch (setting.Style)
            {
                case CoreLineStyle.Dash:
                case CoreLineStyle.Dot:
                case CoreLineStyle.DashDot:
                    return SeriesPrimitiveType.DashedLine;
                case CoreLineStyle.Step:
                    return SeriesPrimitiveType.StepLine;
            }
        }

        return SeriesPrimitiveType.Line;
    }

    /// <summary>
    /// Resolves the effective render primitive for <paramref name="seriesName"/>:
    /// explicit override (if valid in <see cref="SelectablePrimitives"/>) or legacy default.
    /// </summary>
    public static SeriesPrimitiveType ResolveEffectivePrimitive(CoreIndicatorSettings? setting, string? seriesName)
    {
        return ResolveExplicitPrimitive(setting, seriesName) ?? ResolveDefaultPrimitive(setting, seriesName);
    }

    /// <summary>
    /// Normalizes a user-selected primitive override back to <c>null</c> when it matches
    /// <see cref="ResolveDefaultPrimitive"/> or is outside <see cref="SelectablePrimitives"/>,
    /// preserving reversible fallback to legacy specialized renderers.
    /// </summary>
    public static SeriesPrimitiveType? NormalizePrimitiveOverride(
        CoreIndicatorSettings? setting,
        string? seriesName,
        SeriesPrimitiveType selectedPrimitive)
    {
        if (!IsSelectablePrimitive(selectedPrimitive))
        {
            return null;
        }

        return selectedPrimitive == ResolveDefaultPrimitive(setting, seriesName)
            ? null
            : selectedPrimitive;
    }

    /// <summary>
    /// Resolves null-gap handling policy for synthesized primitives.
    /// Only <see cref="IndicatorType.ZigZag"/> connects across null gaps so sparse pivots remain connected.
    /// </summary>
    public static NullGapPolicy ResolveNullGapPolicy(IndicatorType? type)
    {
        return type == IndicatorType.ZigZag
            ? NullGapPolicy.ConnectThrough
            : NullGapPolicy.BreakPath;
    }

    /// <summary>
    /// Returns true if a primitive mode anchors negative/crossed bars to <c>0m</c> on a non-overlay linear panel.
    /// </summary>
    public static bool IsZeroBaselinePrimitive(SeriesPrimitiveType primitive, bool includeZeroBaseline) =>
        primitive is SeriesPrimitiveType.Histogram or SeriesPrimitiveType.Lollipop ||
        (primitive == SeriesPrimitiveType.PositiveNegativeArea && includeZeroBaseline);

    /// <summary>
    /// Returns true if a non-overlay linear panel displaying <paramref name="seriesName"/> uses <c>0m</c>
    /// as the anchor baseline whenever values reach or cross <c>0m</c> (so negative-only bars do not clip
    /// at the panel ceiling).
    /// </summary>
    public static bool RequiresZeroBaselineInYRange(
        CoreIndicatorSettings? setting,
        string? seriesName,
        PriceScaleType priceScale)
    {
        if (setting == null || setting.IsOverlay || priceScale == PriceScaleType.Log)
        {
            return false;
        }

        if (ShouldExcludeFromYRange(setting, seriesName))
        {
            return false;
        }

        var effective = ResolveEffectivePrimitive(setting, seriesName);
        return IsZeroBaselinePrimitive(effective, setting.IncludeZeroBaseline);
    }

    /// <summary>
    /// Returns true if <paramref name="seriesName"/> must be excluded from Y-axis auto-scaling
    /// and intra-indicator cross detection: signal series, suppressed series, or hidden series.
    /// (Native <see cref="SeriesPrimitiveType.ColorStrip"/> descriptors are excluded directly in
    /// <see cref="SeriesPrimitiveValueRangeHelper.AccumulateRange"/>.)
    /// </summary>
    public static bool ShouldExcludeFromYRange(CoreIndicatorSettings? setting, string? seriesName)
    {
        if (string.IsNullOrEmpty(seriesName))
        {
            return true;
        }

        if (IndicatorSeriesNames.IsSignalSeries(seriesName))
        {
            return true;
        }

        if (IndicatorChartSuppression.IsSuppressed(setting?.TypeEnum, seriesName, setting?.ParameterObject))
        {
            return true;
        }

        return !IsSeriesVisible(setting, seriesName);
    }

    private static bool IsMainSeriesName(CoreIndicatorSettings setting, string? seriesName)
    {
        if (string.IsNullOrEmpty(seriesName) ||
            string.Equals(seriesName, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(setting.OutputSeriesName) &&
            string.Equals(seriesName, setting.OutputSeriesName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? normalized = NormalizeSeriesName(setting.TypeEnum, seriesName);
        return string.Equals(normalized, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase);
    }
}
