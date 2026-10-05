using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>
/// Persisted configuration for Backtest Trade History and Metrics column/metric visibility and order.
/// </summary>
public sealed class BacktestColumnSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Ordered list of active column IDs for the Trade History table.
    /// </summary>
    public List<string> TradeVisibleColumns { get; init; } = new();

    /// <summary>
    /// Ordered list of active metric names for the Metrics table.
    /// </summary>
    public List<string> MetricVisibleNames { get; init; } = new();
}
