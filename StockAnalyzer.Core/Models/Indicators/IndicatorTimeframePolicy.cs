using System.Collections.Frozen;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>How an indicator relates to the settings-level <see cref="CoreIndicatorSettings.Timeframe"/>.</summary>
public enum IndicatorTimeframeSupport
{
    /// <summary>The indicator may be calculated on a higher frame and carried onto the chart bars.</summary>
    Supported = 0,

    /// <summary>The indicator owns its time-frame choice (Pivot Points' <c>SourcePeriod</c>); the settings-level value does not apply.</summary>
    SelfHandled = 1,

    /// <summary>The settings-level value has no meaning for this indicator (it is always calculated on the chart's own bars).</summary>
    NotApplicable = 2,
}

/// <summary>
/// Single source of truth for "may this indicator be calculated on another time frame than the chart's". Consumed by the settings
/// (display-name suffix), the normalisation, the pipeline, the Indicator Manager visibility and the MCP workspace read; no other list of these indicators may exist.
/// </summary>
public static class IndicatorTimeframePolicy
{
    /// <summary>
    /// Indicators of the index-based block charts: they transform the bar series itself (Renko, Kagi, Point and Figure, Three Line Break),
    /// so a calculation on aggregated time bars has no meaning for them.
    /// </summary>
    private static readonly FrozenSet<IndicatorType> BlockIndicatorTypes = BlockChartOverlayTypes.All.Concat(new[]
    {
        // The legacy placeholders and the signal indicator; the four registered overlay types come from their one definition.
        IndicatorType.RenkoChart,
        IndicatorType.RenkoOverlay,
        IndicatorType.KagiChart,
        IndicatorType.KagiOverlay,
        IndicatorType.PointAndFigureChart,
        IndicatorType.PointAndFigureOverlay,
#pragma warning disable CS0618 // Keep legacy timeframe normalization for obsolete serialized templates
        IndicatorType.ThreeLineBreakSignal,
#pragma warning restore CS0618
    }).ToFrozenSet();

    /// <summary>
    /// True when the chart draws on time-based bars that accept indicators. The pipeline has no chart type, so the chart host asks this before it hands settings over.
    /// </summary>
    public static bool IsApplicable(ChartType chartType) => chartType.IsTimeBased() && chartType.SupportsIndicators();

    /// <summary>Classifies <paramref name="type"/> (with its parameter object, when known) for the settings-level time frame. First matching rule wins.</summary>
    public static IndicatorTimeframeSupport Resolve(IndicatorType type, CoreIndicatorParameterBase? parameterObject = null)
    {
        if (parameterObject is ISelfTimeframeParameter
            || type is IndicatorType.PivotPointsAdvanced or IndicatorType.ClassicPivotPoints)
        {
            return IndicatorTimeframeSupport.SelfHandled;
        }

        if (type is IndicatorType.Price or IndicatorType.AnchoredVWAP
            || BlockIndicatorTypes.Contains(type)
            || BacktestIndicatorEligibility.NonCausalIndicatorTypes.Contains(type)
            || ScreenerIndicatorEligibility.IsTwoTickerType(type)
            || ScreenerIndicatorEligibility.IsUniverseBreadthType(type))
        {
            return IndicatorTimeframeSupport.NotApplicable;
        }

        return IndicatorTimeframeSupport.Supported;
    }

    /// <summary>True when the settings-level time frame applies to the indicator (visible in the UI, honoured by the pipeline, kept by the normalisation).</summary>
    public static bool SupportsIndicatorTimeframe(IndicatorType type, CoreIndicatorParameterBase? parameterObject = null)
        => Resolve(type, parameterObject) == IndicatorTimeframeSupport.Supported;

    /// <summary>True when the indicator itself reads another indicator's output (Source Indicator or Dynamic Period Driver). Such an indicator is part of a chain, which is calculated on one bar series.</summary>
    public static bool HasOwnChainInput(CoreIndicatorSettings settings)
        => !string.IsNullOrEmpty(settings.SourceIndicatorId) || !string.IsNullOrEmpty(settings.DynamicPeriodIndicatorId);

    /// <summary>
    /// True when another indicator of <paramref name="all"/> reads <paramref name="settings"/>. A copy with the same Id is not "another" (the Indicator Properties dialog edits a copy
    /// of an indicator that is also in the list). For a whole list use <c>IndicatorTimeframePlan.CollectChainParticipants</c>.
    /// </summary>
    public static bool IsReferencedByOther(CoreIndicatorSettings settings, IEnumerable<CoreIndicatorSettings>? all)
    {
        if (all == null || string.IsNullOrEmpty(settings.Id))
        {
            return false;
        }

        foreach (var other in all)
        {
            if (other.Id != settings.Id && (other.SourceIndicatorId == settings.Id || other.DynamicPeriodIndicatorId == settings.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stores that must never carry a time frame (user defaults, the Source Indicator and Dynamic Period Driver registrations) reset it with this one call:
    /// a default or a chain upstream is a template of values, not a placement on a chart. A no-op (no version bump) when it is already Same as Chart.
    /// </summary>
    public static void ForceSameAsChart(CoreIndicatorSettings settings) => settings.Timeframe = IndicatorTimeframe.SameAsChart;
}

/// <summary>Fixed English texts of the time-frame refusals, shared by every host so the wording cannot drift.</summary>
public static class IndicatorTimeframeMessages
{
    /// <summary>The indicator's frame is finer than the chart's: data of a finer frame does not exist on the chart.</summary>
    public static string FinerThanChart(TimeFrame effectiveFrame, TimeFrame chartFrame)
        => $"Indicator timeframe {effectiveFrame} is finer than the chart timeframe {chartFrame}; it cannot be calculated.";

    /// <summary>After the leading, possibly incomplete period is dropped, no completed higher-frame bar is left.</summary>
    public const string InsufficientAggregatedBars = "Not enough completed higher-timeframe bars to calculate this indicator.";

    /// <summary>The chart type does not draw time-based indicator bars, so the setting is ignored.</summary>
    public const string ChartTypeNotApplicable = "The indicator timeframe setting is ignored on this chart type.";

    /// <summary>
    /// A Monthly indicator on a Weekly chart: a calendar month cannot be rebuilt exactly from weekly bars (a week can span two months), and an approximation
    /// that differs from the calendar-month OHLCV is not shown as a value.
    /// </summary>
    public const string MonthlyRequiresDailyChart = "Monthly indicator timeframe requires a Daily chart and cannot be calculated from Weekly bars.";

    /// <summary>The chart's own frame is not one of the frames the data layer serves (<c>DataServiceTimeFrames.Supported</c>): higher-frame bars cannot be built from it.</summary>
    public const string UnsupportedChartFrame = "Indicator timeframe is supported on Daily, Weekly and Monthly charts only.";
}
