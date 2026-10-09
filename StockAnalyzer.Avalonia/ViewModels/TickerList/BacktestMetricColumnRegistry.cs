using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Authoritative registry mapping all 30 backtest metrics from <see cref="BacktestMetricCatalog.Definitions"/>
/// into <see cref="WatchlistColumnMetadata"/> specifically for the Ticker tab's Column Customization.
/// Isolated from Screener and other tools per architecture rules.
/// </summary>
public static class BacktestMetricColumnRegistry
{
    public const string MetricsCategoryName = "Metrics";
    public const string MetricMemberPrefix = "Metric_";
    public const string MetricHeaderPrefix = "Backtest_Metric_";
    public const string MetricTooltipPrefix = "Backtest_Metric_Tooltip_";
    public const string DefaultMetricColumnWidth = "90";

    public static IReadOnlyList<WatchlistColumnMetadata> AllColumns { get; } = BuildMetricColumns();

    private static IReadOnlyList<WatchlistColumnMetadata> BuildMetricColumns()
    {
        var list = new List<WatchlistColumnMetadata>();
        var definitions = BacktestMetricCatalog.Definitions;
        for (int i = 0; i < definitions.Length; i++)
        {
            var def = definitions[i];
            var col = new WatchlistColumnMetadata(
                HeaderKey: MetricHeaderPrefix + def.Name,
                MemberName: MetricMemberPrefix + def.Name,
                Width: DefaultMetricColumnWidth,
                Priority: 1000 + i,
                DisplayName: def.Name,
                Description: MetricTooltipPrefix + def.Name,
                Category: MetricsCategoryName,
                Formula: def.Group.ToString());
            list.Add(col);
        }
        return list;
    }
}
