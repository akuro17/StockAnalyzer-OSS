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
    /// </summary>
    public string FormattedParameters
    {
        get
        {
            if (Parameters.Count == 0) return string.Empty;
            return $"({string.Join(", ", Parameters.Values)})";
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
}
