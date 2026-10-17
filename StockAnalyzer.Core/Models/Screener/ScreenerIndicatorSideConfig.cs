using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Represents single-side configuration of an indicator used in screener conditions.
/// </summary>
public class ScreenerIndicatorSideConfig
{
    /// <summary>
    /// The type of indicator (e.g., RSI, MACD, SMA).
    /// </summary>
    public IndicatorType IndicatorType { get; set; } = IndicatorType.SMA;

    /// <summary>
    /// Indicator-specific parameters (e.g., Period=14, FastPeriod=12).
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// The timeframe to evaluate this indicator on (Daily, Weekly, Monthly, etc.).
    /// </summary>
    public TimeFrame TimeFrame { get; set; } = TimeFrame.D1;

    /// <summary>
    /// The number of bars to offset from the latest bar for evaluation.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// The specific output series name selected for evaluation (e.g., "Main", "Signal", "UpperBand").
    /// </summary>
    public string OutputName { get; set; } = IndicatorResult.MainSeriesName;

    /// <summary>
    /// Short parameter description formatted for display (e.g., "(14)" or "(12, 26, 9)").
    /// Omits internal boolean configuration flags (False/True).
    /// </summary>
    public string FormattedParameters
    {
        get
        {
            if (Parameters.Count == 0) return string.Empty;
            var nonBoolValues = System.Linq.Enumerable.Where(Parameters.Values, v => v is not bool);
            if (!System.Linq.Enumerable.Any(nonBoolValues)) return string.Empty;
            return $"({string.Join(", ", nonBoolValues)})";
        }
    }

    /// <summary>
    /// Category type of the catalog item (Indicator, Column, Criteria).
    /// </summary>
    public ScreenerItemCategoryType CategoryType { get; set; } = ScreenerItemCategoryType.Indicator;

    /// <summary>
    /// Custom display title for Column or Criteria items (e.g. "High", "Volume", "Head and Shoulders").
    /// </summary>
    public string CustomDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Display label for this side configuration (upper title line).
    /// Omits parameters and timeframe brackets so details can be shown cleanly in the details sub-row.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomDisplayName))
            {
                return CustomDisplayName;
            }

            if (CategoryType == ScreenerItemCategoryType.Criteria)
            {
                return string.IsNullOrWhiteSpace(OutputName) ? IndicatorType.ToString() : OutputName;
            }

            string baseName = string.IsNullOrWhiteSpace(OutputName) || OutputName == IndicatorResult.MainSeriesName || OutputName == IndicatorType.ToString()
                ? IndicatorType.ToString()
                : $"{IndicatorType} ({OutputName})";

            return baseName;
        }
    }

    /// <summary>
    /// The name that selects the metric (Column) or the pattern (Criteria) this side reads: the first of <see cref="CustomDisplayName"/>,
    /// <see cref="OutputName"/>, <see cref="DisplayName"/> that is not blank and is not the default of an unnamed side ("SMA", "Main"); null when none is.
    /// The catalog stores the description in <see cref="CustomDisplayName"/> and the short name in <see cref="OutputName"/>, so this one order is what
    /// the evaluation, the history sizing and the result columns all use. Allocation-free: every Column extraction of a scan runs through it.
    /// </summary>
    public string? ResolveLookupName()
    {
        if (IsLookupNameCandidate(CustomDisplayName)) return CustomDisplayName;
        if (IsLookupNameCandidate(OutputName)) return OutputName;
        string displayName = DisplayName;
        return IsLookupNameCandidate(displayName) ? displayName : null;
    }

    /// <summary>
    /// The name that selects the pattern for a Criteria side: the first of <see cref="OutputName"/>,
    /// <see cref="CustomDisplayName"/>, <see cref="DisplayName"/> that is not blank and is not "SMA" or "Main"; null when none is.
    /// The catalog stores the short name (the pattern identifier) in <see cref="OutputName"/> and the description in
    /// <see cref="CustomDisplayName"/>, so OutputName is consulted first for Criteria evaluation and history reach.
    /// </summary>
    public string? ResolveCriteriaName()
    {
        if (IsLookupNameCandidate(OutputName)) return OutputName;
        if (IsLookupNameCandidate(CustomDisplayName)) return CustomDisplayName;
        string displayName = DisplayName;
        return IsLookupNameCandidate(displayName) ? displayName : null;
    }

    private static bool IsLookupNameCandidate(string? name)
        => !string.IsNullOrWhiteSpace(name) && name != nameof(Models.IndicatorType.SMA) && name != IndicatorResult.MainSeriesName;

    /// <summary>
    /// Formats a complete, deduplicatable column header title including parameters and timeframe.
    /// </summary>
    public string GetColumnHeaderTitle()
    {
        if (!string.IsNullOrWhiteSpace(CustomDisplayName)) return CustomDisplayName;
        string paramStr = FormattedParameters;
        string tfStr = TimeFrame != TimeFrame.D1 ? $" [{TimeFrame}]" : "";
        return $"{DisplayName}{paramStr}{tfStr}";
    }

    /// <summary>
    /// Creates a deep clone of this configuration.
    /// </summary>
    public ScreenerIndicatorSideConfig Clone()
    {
        return new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType,
            Parameters = Parameters != null ? new Dictionary<string, object>(Parameters) : new Dictionary<string, object>(),
            TimeFrame = TimeFrame,
            Offset = Offset,
            OutputName = OutputName,
            CategoryType = CategoryType,
            CustomDisplayName = CustomDisplayName
        };
    }
}
