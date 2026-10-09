using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Warning code for backtest simulation diagnostic alerts.
/// </summary>
public enum WarningCode
{
    None = 0,
    PotentialUnadjustedSplit = 1,
}

/// <summary>
/// Structured warning emitted during backtesting (e.g. unadjusted corporate actions, split anomalies).
/// Non-destructive alert that does not abort simulation execution.
/// </summary>
public readonly record struct BacktestWarning(
    WarningCode Code,
    int BarIndex,
    DateTime Timestamp,
    string Message);
