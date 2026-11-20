using System.Collections.Frozen;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// The one definition of "the indicator types that overlay a block (non-time-series) chart on a time-series chart": exactly Renko, Point and Figure, Kagi and Three Line Break.
/// The legacy <c>*Overlay</c> and <c>*Chart</c> enum members are NOT in this set (they are separate, unregistered placeholders).
/// Consumed by the renderer routing, the price-axis scaling guard and the tests; no other list of these four types may exist.
/// </summary>
public static class BlockChartOverlayTypes
{
    private static readonly FrozenSet<IndicatorType> Types = new[]
    {
        IndicatorType.Renko,
        IndicatorType.PointAndFigure,
        IndicatorType.Kagi,
        IndicatorType.ThreeLineBreak,
    }.ToFrozenSet();

    public static bool Contains(IndicatorType type) => Types.Contains(type);

    /// <summary>For <see cref="CoreIndicatorSettings.TypeEnum"/>, which is null for an indicator that has no enum member (a custom indicator): never one of the four.</summary>
    public static bool Contains(IndicatorType? type) => type.HasValue && Types.Contains(type.Value);

    public static IReadOnlyCollection<IndicatorType> All => Types;

    /// <summary>
    /// The one rule "the overlay has no meaning here": on a non-time-series main chart (<see cref="ChartTypeExtensions.IsIndexBased"/>) the four overlays are neither drawn nor part of the price axis.
    /// Used by the renderer routing and by both price-axis loops of the chart data snapshot.
    /// </summary>
    public static bool IsHiddenOn(ChartType chartType, IndicatorType? type) => chartType.IsIndexBased() && Contains(type);
}
