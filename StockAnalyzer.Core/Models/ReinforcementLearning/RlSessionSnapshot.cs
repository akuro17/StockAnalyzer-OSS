using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

public enum RlAction { Hold = 0, Buy = 1, Sell = 2 }
public enum RlTerminalReason { None, Horizon, CoreInsolvent, EquityNonPositive, ForcedLiquidation }
public enum RlActionOutcome { Submitted, Hold, AlreadyLong, AlreadyFlat, PendingEntry, PendingExit, Rejected, NoEntry, StopCancellationQueued, DeferredExit }

/// <summary>Immutable completed-market window plus exact decimal account, including actual intrabar terminal account.</summary>
public sealed record RlSessionSnapshot(
    int MarketAnchorBarIndex,
    DateTime MarketAnchor,
    int? TerminalEventBarIndex,
    int CompletedDecisions,
    int RemainingDecisions,
    EquityPoint EquityPoint,
    decimal Quantity,
    decimal EntryPrice,
    decimal UnrealizedPnL,
    decimal PeakEquity,
    decimal Drawdown,
    decimal MaximumDrawdown,
    int HeldBars,
    bool PendingEntry,
    bool PendingExit,
    bool Terminated,
    RlTerminalReason TerminalReason,
    RunStatus Status,
    ImmutableArray<decimal> AccountFields,
    ImmutableArray<CandleData> MarketWindow)
{
    public decimal? ActiveStopPrice { get; init; }
    public decimal RetainedStopDistance { get; init; }
    public bool DeferredMarketExit { get; init; }
    public decimal? RetainedEntrySizeRatio { get; init; }
}

public sealed record RlStepResult(
    RlSessionSnapshot Snapshot,
    decimal Reward,
    bool Terminated,
    bool Truncated,
    RlAction Action,
    RlActionOutcome Outcome,
    RejectedReason? RejectionReason)
{
    public RlContinuousAction? ActorAction { get; init; }
    public RlEntryExecution? EntryExecution { get; init; }
}
