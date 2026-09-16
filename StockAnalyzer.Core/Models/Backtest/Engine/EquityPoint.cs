using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// HeldMargin is the margin-account amendment field (see Y:\Temp\sa_ai_context_BacktestEngine_P1.md
/// section 1.5.2): Equity = Cash + HeldMargin + UnrealizedPnL. Without this field the accounting
/// identity cannot be reconciled from the output schema alone.
/// </summary>
public readonly record struct EquityPoint(
    int BarIndex, DateTime Timestamp,
    decimal Equity, decimal Cash, decimal MarketValue,
    decimal HeldMargin);
