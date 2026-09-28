using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// <see cref="IsForcedLiquidation"/> is a margin-account amendment addition (not in the original
/// spec doc): true when this trade was closed by the maintenance-margin intrabar loss-cut
/// (Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6) rather than a normal strategy-driven
/// exit signal. User-requested so a forced loss-cut can be distinguished in trade history even when
/// the run continues afterward (post-liquidation Equity &gt; 0 case).
/// </summary>
public readonly record struct BacktestTrade(
    long TradeId, TradeSide Side,
    int EntryBar, DateTime EntryTime, decimal EntryPrice,
    int ExitBar, DateTime ExitTime, decimal ExitPrice,
    decimal Quantity,
    decimal ClosedGross, decimal ClosedNet,
    decimal EntryFee, decimal ExitFee,
    int HoldingBars,
    bool IsForcedLiquidation);
