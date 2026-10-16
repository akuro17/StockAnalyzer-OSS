using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Warning code for backtest simulation diagnostic alerts.
/// </summary>
public enum WarningCode
{
    None = 0,
    PotentialUnadjustedSplit = 1,

    /// <summary>A bar whose outcome depends on the unknown intra-bar arrival order of two prices (see API_SPECIFICATION "Order-dependent bars"). Append-only.</summary>
    AmbiguousIntrabarOrder = 2,

    /// <summary>
    /// An <see cref="AmbiguousIntrabarOrder"/> bar whose result was decided by the configured convention (the other convention would have produced a different result).
    /// One warning per bar carries either this code or <see cref="AmbiguousIntrabarOrder"/>, never both. Append-only.
    /// </summary>
    AmbiguousIntrabarOrderAffectsOutcome = 3,
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
