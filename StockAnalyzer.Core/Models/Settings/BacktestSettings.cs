using System;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Backtest tunables bound from the "Backtest" section of appsettings.json (via <c>IStockAnalyzerSettings</c>).
/// The default lives here only; the interface default and the configuration file both follow it.
/// </summary>
public class BacktestSettings
{
    /// <summary>
    /// Largest Offset (bars before the evaluated bar) a backtest condition may use. One value serves the Offset input boxes and the saved-configuration
    /// validation, so a value the UI cannot express is never loaded (and silently clamped) from a file. The lower bound is not a tunable:
    /// it is the causality invariant <see cref="BacktestConditionOffsetRule.MinOffset"/>.
    /// </summary>
    public int MaxConditionOffset { get; set; } = 500;

    public void Validate()
    {
        if (MaxConditionOffset < BacktestConditionOffsetRule.MinOffset)
        {
            throw new InvalidOperationException($"BacktestSettings: MaxConditionOffset must be >= {BacktestConditionOffsetRule.MinOffset}.");
        }
    }
}
