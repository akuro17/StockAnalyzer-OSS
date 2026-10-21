using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>Why a <see cref="ScatterMetricDefinition"/> cannot be used as a scatter metric.</summary>
public enum ScatterMetricInvalidReason
{
    None = 0,
    /// <summary>The definition or its <see cref="ScatterMetricDefinition.Source"/> is null, or the <see cref="ScatterMetricDefinition.Id"/> is empty.</summary>
    Missing,
    /// <summary>Only Indicator and Column sources can be plotted; a Criteria (pattern) is not a number.</summary>
    UnsupportedCategory,
    /// <summary>
    /// The time frame is not one of <see cref="ScatterMetricTimeFrames.Supported"/> (Day, Week, Month). Legacy name, kept because renaming a public member
    /// is outside this change: it originally meant "not daily", when Day was the only time frame.
    /// </summary>
    TimeFrameNotDaily,
    /// <summary>The Offset is negative (it would read a future bar) or above the configured limit.</summary>
    OffsetOutOfRange,
    /// <summary>A text Column (Sector, Industry, Region, Name, Tag) has no number to plot.</summary>
    TextColumn,
    /// <summary>A Column that names no metric.</summary>
    ColumnNameMissing,
    /// <summary>A Column that never yields a number (<see cref="ScatterMetricEligibility.ColumnsWithoutAValue"/>); a saved definition of one is refused on load.</summary>
    ColumnWithoutValue,
    /// <summary>A Column that is not a bar value (a fundamental such as the market cap) on another time frame than Day (<see cref="ScatterMetricEligibility.AllowsTimeFrameChoice"/>).</summary>
    TimeFrameNotApplicable,
    /// <summary>The indicator type is not registered in the indicator factory.</summary>
    UnknownIndicator,
    /// <summary>The indicator or the chosen output series carries data of later bars (look-ahead), or has no synchronous output.</summary>
    NotAllowedIndicator,
}

/// <summary>
/// The one validation of a scatter metric, shared by the registration screen's draft, the registry and the evaluator, so all three
/// accept and refuse exactly the same definitions.
/// </summary>
public static class ScatterMetricRules
{
    /// <summary>
    /// <see cref="ScatterMetricInvalidReason.None"/> when <paramref name="definition"/> can be plotted. The checks run in the order of the enum members;
    /// the first failing one is returned.
    /// </summary>
    /// <param name="definition">The definition to judge.</param>
    /// <param name="maxOffset">The configured limit of the Offset (<c>ScatterMetrics:MaxOffset</c>), in bars.</param>
    /// <param name="indicatorFactory">Decides whether an indicator type exists.</param>
    public static ScatterMetricInvalidReason Validate(ScatterMetricDefinition? definition, int maxOffset, IIndicatorFactory indicatorFactory)
    {
        ArgumentNullException.ThrowIfNull(indicatorFactory);

        if (definition is null || definition.Source is null || definition.Id == Guid.Empty)
        {
            return ScatterMetricInvalidReason.Missing;
        }

        ScreenerIndicatorSideConfig source = definition.Source;

        if (source.CategoryType is not (ScreenerItemCategoryType.Indicator or ScreenerItemCategoryType.Column))
        {
            return ScatterMetricInvalidReason.UnsupportedCategory;
        }

        if (!ScatterMetricTimeFrames.IsSupported(source.TimeFrame))
        {
            return ScatterMetricInvalidReason.TimeFrameNotDaily;
        }

        if (!BacktestConditionOffsetRule.IsValid(source.Offset) || !BacktestConditionOffsetRule.IsWithinLimit(source.Offset, maxOffset))
        {
            return ScatterMetricInvalidReason.OffsetOutOfRange;
        }

        if (source.CategoryType == ScreenerItemCategoryType.Column)
        {
            if (ScreenerTextFields.ResolveFieldName(source) != null)
            {
                return ScatterMetricInvalidReason.TextColumn;
            }

            if (source.ResolveLookupName() is null)
            {
                return ScatterMetricInvalidReason.ColumnNameMissing;
            }

            if (ScatterMetricEligibility.IsExcludedColumn(source))
            {
                return ScatterMetricInvalidReason.ColumnWithoutValue;
            }

            // A fundamental belongs to the ticker, not to a bar: only Day reads it.
            return source.TimeFrame != TimeFrame.D1 && !ScatterMetricEligibility.AllowsTimeFrameChoice(source)
                ? ScatterMetricInvalidReason.TimeFrameNotApplicable
                : ScatterMetricInvalidReason.None;
        }

        // The Price source is read straight from the bars (the extractor does not run the eligibility check for it either).
        if (source.IndicatorType == IndicatorType.Price)
        {
            return ScatterMetricInvalidReason.None;
        }

        if (!indicatorFactory.IsRegistered(source.IndicatorType))
        {
            return ScatterMetricInvalidReason.UnknownIndicator;
        }

        return BacktestIndicatorEligibility.IsAllowed(source.IndicatorType, source.OutputName)
            ? ScatterMetricInvalidReason.None
            : ScatterMetricInvalidReason.NotAllowedIndicator;
    }

    /// <summary>
    /// True when the sequence of <paramref name="first"/> and <paramref name="second"/> differs: another count, another Id at some position, or
    /// another <see cref="ScatterMetricDefinition.ContentKey"/> at some position. This is "modified" for the registry's change event and the
    /// registration screen's unsaved-changes check, defined once.
    /// </summary>
    public static bool DiffersInIdOrContent(
        IReadOnlyList<ScatterMetricDefinition> first,
        IReadOnlyList<ScatterMetricDefinition> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Count != second.Count)
        {
            return true;
        }

        for (int i = 0; i < first.Count; i++)
        {
            if (first[i].Id != second[i].Id || !string.Equals(first[i].ContentKey, second[i].ContentKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
