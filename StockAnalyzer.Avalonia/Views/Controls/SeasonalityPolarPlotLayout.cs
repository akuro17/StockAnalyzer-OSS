using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Pre-computed, render-loop-free geometry for one <see cref="SeasonalityChartResult"/>: the clock
/// reference frame (twelve month spokes, concentric rho grid) expressed in data-unit radii and
/// screen-independent angles. Rebuilt only when the result reference changes (mirrors
/// <c>SpiralPhasePlotLayout</c> caching); never allocated on the Skia draw path.
/// </summary>
internal readonly record struct SeasonalityPolarPlotLayout(
    double DisplayBaseRadius,
    double RadialSpan,
    double[] GridRadii,
    string[] GridLabels,
    double[] MonthAnglesRadians,
    string[] MonthLabels,
    SeasonalityLegendEntry[] LegendEntries);

internal static class SeasonalityPolarPlotLayoutConstants
{
    /// <summary>
    /// Coarse outer sanity ceiling for the trace radius, as a fraction of the shorter viewport edge.
    /// Only binds on very large viewports (shorter edge above roughly 1000px at the default font size);
    /// at normal panel sizes the font-driven <c>maxAllowedSpokeOuter</c> cap in
    /// <see cref="SeasonalityPolarPlotControl.RenderSkia"/> is tighter and determines the actual radius,
    /// so month labels are never clipped without this constant forcing extra unused margin.
    /// </summary>
    internal const double RadialFillFraction = 0.48d;

    /// <summary>
    /// Fraction the innermost drawn radius is pulled toward the centre when the radial axis origin is
    /// moved to the visible range, so the tightest trace is not flush against the frame edge.
    /// </summary>
    internal const double DisplayOriginPaddingFraction = 0.10d;
}

internal static class SeasonalityPolarPlotLayoutBuilder
{
    internal static SeasonalityPolarPlotLayout Build(
        SeasonalityChartResult result,
        decimal? manualRhoStep = null,
        bool useVisibleRangeOrigin = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        uint startMonth = result.Parameters.StartMonth;
        double[] monthAngles = BuildMonthAngles(startMonth);
        string[] monthLabels = SeasonalitySharedAxisFormatting.BuildMonthLabels(startMonth);
        SeasonalityLegendEntry[] legendEntries = SeasonalitySharedAxisFormatting.BuildYearLegend(result);

        double maxRadius = result.MaxRadius;
        if (!double.IsFinite(maxRadius) || maxRadius <= 0d)
        {
            return new SeasonalityPolarPlotLayout(
                0d,
                0d,
                Array.Empty<double>(),
                Array.Empty<string>(),
                monthAngles,
                monthLabels,
                legendEntries);
        }

        double displayBaseRadius = ResolveDisplayBaseRadius(result, useVisibleRangeOrigin, maxRadius);
        double radialSpan = maxRadius - displayBaseRadius;

        (double[] gridRadii, string[] gridLabels) = BuildRhoGrid(result, manualRhoStep);

        return new SeasonalityPolarPlotLayout(
            displayBaseRadius,
            radialSpan,
            gridRadii,
            gridLabels,
            monthAngles,
            monthLabels,
            legendEntries);
    }

    /// <summary>
    /// The data-unit radius the plot centre represents. Zero (full disc from the centre) unless
    /// <paramref name="useVisibleRangeOrigin"/> is set and the traces have a positive inner radius, in
    /// which case the axis starts just inside the tightest drawn point. Falls back to zero if the
    /// resulting span would be non-positive.
    /// </summary>
    private static double ResolveDisplayBaseRadius(SeasonalityChartResult result, bool useVisibleRangeOrigin, double maxRadius)
    {
        if (!useVisibleRangeOrigin)
        {
            return 0d;
        }

        double minRadius = result.MinRadius;
        if (!double.IsFinite(minRadius) || minRadius <= 0d)
        {
            return 0d;
        }

        double candidate = minRadius * (1d - SeasonalityPolarPlotLayoutConstants.DisplayOriginPaddingFraction);
        return double.IsFinite(candidate) && candidate > 0d && candidate < maxRadius ? candidate : 0d;
    }

    internal static double[] BuildMonthAngles(uint startMonth = 1)
    {
        double[] fractions = SeasonalitySharedAxisFormatting.BuildMonthStartFractions(startMonth);
        var angles = new double[fractions.Length];
        for (int index = 0; index < fractions.Length; index++)
        {
            angles[index] = SeasonalityCalendar.PlotAngleRadians(fractions[index]);
        }

        return angles;
    }

    /// <summary>Zero-based position of <paramref name="calendarYear"/> among the legend rows (0 when absent).</summary>
    internal static int LegendIndex(SeasonalityLegendEntry[] legendEntries, int calendarYear)
    {
        for (int index = 0; index < legendEntries.Length; index++)
        {
            if (legendEntries[index].CalendarYear == calendarYear)
            {
                return index;
            }
        }

        return 0;
    }

    private static (double[] Radii, string[] Labels) BuildRhoGrid(SeasonalityChartResult result, decimal? manualRhoStep)
    {
        double scale = result.ScaleFactor;
        double baseRadius = result.BaseRadius;
        if (!double.IsFinite(scale) || scale <= 0d || !double.IsFinite(baseRadius))
        {
            return (Array.Empty<double>(), Array.Empty<string>());
        }

        double rhoMin = (result.MinRadius - baseRadius) / scale;
        double rhoMax = (result.MaxRadius - baseRadius) / scale;

        (double[] rhoValues, string[] rhoLabels) = SeasonalitySharedAxisFormatting.BuildRhoTicks(rhoMin, rhoMax, manualRhoStep);
        if (rhoValues.Length == 0)
        {
            return (Array.Empty<double>(), Array.Empty<string>());
        }

        var radii = new List<double>(rhoValues.Length);
        var labels = new List<string>(rhoValues.Length);
        for (int index = 0; index < rhoValues.Length; index++)
        {
            double radius = baseRadius + (scale * rhoValues[index]);
            if (!double.IsFinite(radius) || radius < 0d)
            {
                continue;
            }

            radii.Add(radius);
            labels.Add(rhoLabels[index]);
        }

        return (radii.ToArray(), labels.ToArray());
    }
}
