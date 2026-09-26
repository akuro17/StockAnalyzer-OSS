using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Deliberately narrow evaluation context passed to <see cref="IBacktestStrategy.Evaluate"/>, per section
/// 9's constraint: "barIndex, current bar, current/previous indicator values, current snapshot, and the
/// pending order's id (if any) — do not leak broader engine internals into the strategy interface." A
/// strategy cannot see Cash/HeldMargin directly, only the already-computed <see cref="Snapshot"/>,
/// whether it currently holds a position via <see cref="PositionSide"/>, and that position's entry
/// price via <see cref="EntryPrice"/>.
/// </summary>
public readonly struct StrategyContext
{
    public int BarIndex { get; init; }
    public CandleData Bar { get; init; }

    /// <summary>Null while Flat.</summary>
    public TradeSide? PositionSide { get; init; }

    public EquityPoint Snapshot { get; init; }

    /// <summary>
    /// Null when no order is currently resting/pending. Since Task 3 (Y:\Temp\
    /// sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.3/2.4), the engine's
    /// internal <c>RunState</c> can hold up to two independent pending orders at once (one Entry-type,
    /// one Exit-type - a same-bar reversal pair) - this field's shape stays a single nullable id
    /// unchanged, so when both are pending it reports the Exit-type order's id (the one closing the
    /// currently-held position takes priority, since it resolves first per section 2.4's Exit-before-
    /// Entry sequencing), falling back to the Entry-type order's id, then null. No current strategy reads
    /// this field.
    /// </summary>
    public long? PendingOrderId { get; init; }

    /// <summary>Id of the pending Entry-type order, or null. Additive to <see cref="PendingOrderId"/> so a strategy using <see cref="IBacktestOrderCancellationStrategy"/> can name either slot.</summary>
    public long? PendingEntryOrderId { get; init; }

    /// <summary>Id of the pending Exit-type order, or null.</summary>
    public long? PendingExitOrderId { get; init; }

    public IndicatorSeriesSet Indicators { get; init; }

    /// <summary>
    /// Read-only entry price of the currently-open position; null while <see cref="PositionSide"/> is
    /// null (flat). An approved part of the strategy contract (user decision 2026-09-20; added
    /// 2026-09-18, see Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.5):
    /// entry-price-relative Stop-Loss/Take-Profit/trailing stops cannot be expressed without it, and
    /// exposing it read-only is trading context, not a leak of engine internals. The single
    /// construction site (<c>BacktestEngine</c>'s bar loop) is the only place this is ever set.
    /// </summary>
    public decimal? EntryPrice { get; init; }
}
