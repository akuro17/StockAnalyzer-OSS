using System;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// <see cref="IsForcedLiquidation"/> is a margin-account amendment addition (not in the original
/// spec doc): true when this trade was closed by the maintenance-margin intrabar loss-cut
/// (docs/sa_ai_context_BacktestEngine_P1 section 1.5.6) rather than a normal strategy-driven
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
    bool IsForcedLiquidation)
{
    [JsonIgnore]
    public decimal EntrySlippage { get; init; } = 0m;
    [JsonIgnore]
    public decimal ExitSlippage { get; init; } = 0m;
    [JsonIgnore]
    public decimal MfePrice { get; init; } = 0m;
    [JsonIgnore]
    public decimal MaePrice { get; init; } = 0m;
    [JsonIgnore]
    public string EntrySignal { get; init; } = string.Empty;
    [JsonIgnore]
    public string ExitReason { get; init; } = string.Empty;

    [JsonIgnore]
    public decimal EntryValue => checked(EntryPrice * Quantity);
    [JsonIgnore]
    public decimal ExitValue => checked(ExitPrice * Quantity);
    [JsonIgnore]
    public decimal TotalCommission => EntryFee + ExitFee;
    [JsonIgnore]
    public decimal TotalSlippage => EntrySlippage + ExitSlippage;
    [JsonIgnore]
    public decimal TotalCost => TotalCommission + TotalSlippage;
    [JsonIgnore]
    public decimal? PnLPercent => EntryValue > 0m ? (ClosedNet / EntryValue) * 100m : null;
    [JsonIgnore]
    public decimal? MfePercent => EntryPrice > 0m ? (MfePrice / EntryPrice) * 100m : null;
    [JsonIgnore]
    public decimal? MaePercent => EntryPrice > 0m ? (MaePrice / EntryPrice) * 100m : null;
    [JsonIgnore]
    public TimeSpan Duration => ExitTime >= EntryTime ? ExitTime - EntryTime : TimeSpan.Zero;
}
