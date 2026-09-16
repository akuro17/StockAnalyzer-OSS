using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Deliberately narrow evaluation context passed to <see cref="IBacktestStrategy.Evaluate"/>, per section
/// 9's constraint: "barIndex, current bar, current/previous indicator values, current snapshot, and the
/// pending order's id (if any) — do not leak broader engine internals into the strategy interface." A
/// strategy cannot see Cash/HeldMargin/EntryPrice directly, only the already-computed <see cref="Snapshot"/>
/// and whether it currently holds a position via <see cref="PositionSide"/>.
/// </summary>
public readonly struct StrategyContext
{
    public int BarIndex { get; init; }
    public CandleData Bar { get; init; }

    /// <summary>Null while Flat.</summary>
    public TradeSide? PositionSide { get; init; }

    public EquityPoint Snapshot { get; init; }

    /// <summary>Null when no order is currently resting/pending.</summary>
    public long? PendingOrderId { get; init; }

    public IndicatorSeriesSet Indicators { get; init; }
}
