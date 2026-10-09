using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

public enum RlPortfolioSessionState { Created, Ready, Terminated, Disposed }

/// <summary>Why a rebalance order was not executed. A buy is rejected whole (no partial fill, no clipping, no resizing from future prices).</summary>
public enum RlPortfolioRejection { InsufficientFunds }

/// <summary>
/// Immutable account after a decision (or after Reset). Money is decimal. <see cref="Quantities"/> and <see cref="Weights"/> have one entry
/// per panel symbol (zero for symbols the session does not trade); <see cref="Weights"/> are marked at the close of <see cref="AnchorIndex"/>.
/// </summary>
public sealed record RlPortfolioSnapshot(
    string BlockId,
    int CompletedDecisions,
    int RemainingDecisions,
    int AnchorIndex,
    DateTime Anchor,
    decimal Cash,
    ImmutableArray<decimal> Quantities,
    ImmutableArray<decimal> Weights,
    decimal CashWeight,
    decimal Equity,
    decimal PeakEquity,
    decimal Drawdown,
    decimal PreviousTurnover,
    bool Terminated,
    int OrderCount, // submitted orders, including rejected buys
    int RejectedBuyCount,
    decimal CommissionPaid,
    decimal SlippageCost);

/// <summary>One tradable asset at one decision. Quantities are in shares; <see cref="ExecutedQuantity"/> is signed and zero when nothing filled.</summary>
public sealed record RlPortfolioOrderTrace(
    int SymbolIndex,
    decimal PreviousQuantity,
    decimal TargetQuantity,
    decimal ExecutedQuantity,
    decimal? FillPrice,
    decimal Commission,
    RlPortfolioRejection? Rejection);

public sealed record RlPortfolioStepResult(
    RlPortfolioSnapshot Snapshot,
    decimal Reward,
    bool Terminated,
    ImmutableArray<RlPortfolioOrderTrace> Trace);
