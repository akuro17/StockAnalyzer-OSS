using System;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Pre-computed, render-loop-free geometry for the left-to-right line overlay of one
/// <see cref="SeasonalityChartResult"/>: the rho (y) value range with its horizontal gridlines and
/// the twelve month positions (x) as year-progress fractions. The x axis is year-progress u in
/// [0, 1) — the left edge is 1 January, the right edge 31 December (confirmation Q5). Rebuilt only
/// when the result reference changes; never allocated on the Skia draw path.
/// </summary>
internal readonly record struct SeasonalityLinearPlotLayout(
    double RhoMin,
    double RhoMax,
    double[] GridRhoValues,
    string[] GridRhoLabels,
    double[] MonthFractions,
    string[] MonthLabels,
    SeasonalityLegendEntry[] LegendEntries)
{
    /// <summary>The rho span the y axis covers; non-positive when there is nothing to draw.</summary>
    internal double RhoSpan => RhoMax - RhoMin;

    internal bool HasData => RhoSpan > 0d;
}

internal static class SeasonalityLinearPlotLayoutBuilder
{
    internal static SeasonalityLinearPlotLayout Build(SeasonalityChartResult result, decimal? manualRhoStep = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        uint startMonth = result.Parameters.StartMonth;
        double[] monthFractions = SeasonalitySharedAxisFormatting.BuildMonthStartFractions(startMonth);
        string[] monthLabels = SeasonalitySharedAxisFormatting.BuildMonthLabels(startMonth);
        SeasonalityLegendEntry[] legendEntries = SeasonalitySharedAxisFormatting.BuildYearLegend(result);

        (double rhoMin, double rhoMax) = ResolveRhoRange(result);
        if (rhoMax <= rhoMin)
        {
            return new SeasonalityLinearPlotLayout(
                0d, 0d, Array.Empty<double>(), Array.Empty<string>(), monthFractions, monthLabels, legendEntries);
        }

        (double[] gridRhoValues, string[] gridRhoLabels) =
            SeasonalitySharedAxisFormatting.BuildRhoTicks(rhoMin, rhoMax, manualRhoStep);

        return new SeasonalityLinearPlotLayout(
            rhoMin, rhoMax, gridRhoValues, gridRhoLabels, monthFractions, monthLabels, legendEntries);
    }

    /// <summary>
    /// The rho (year-start cumulative change) value range, derived from the engine's radius mapping
    /// exactly as the polar grid does: <c>rho = (radius - BaseRadius) / ScaleFactor</c>. The engine
    /// guarantees <c>rhoMin &lt;= 0 &lt;= rhoMax</c>, so the year-start baseline is always in view.
    /// </summary>
    private static (double RhoMin, double RhoMax) ResolveRhoRange(SeasonalityChartResult result)
    {
        double scale = result.ScaleFactor;
        double baseRadius = result.BaseRadius;
        if (!double.IsFinite(scale) || scale <= 0d || !double.IsFinite(baseRadius))
        {
            return (0d, 0d);
        }

        double rhoMin = (result.MinRadius - baseRadius) / scale;
        double rhoMax = (result.MaxRadius - baseRadius) / scale;
        if (!double.IsFinite(rhoMin) || !double.IsFinite(rhoMax))
        {
            return (0d, 0d);
        }

        return (rhoMin, rhoMax);
    }
}
