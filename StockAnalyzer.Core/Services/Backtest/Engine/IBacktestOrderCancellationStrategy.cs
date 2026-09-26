namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Optional capability of an <see cref="IBacktestStrategy"/> (owner decision G4, A1 (Order cancellation)): asking the
/// engine to cancel its own still-pending orders. A strategy that does not implement it runs exactly as before. It is a separate interface, not a
/// member of <see cref="IBacktestStrategy"/>, so no existing implementer or call signature changes.
/// </summary>
public interface IBacktestOrderCancellationStrategy
{
    /// <summary>
    /// Called at most once per bar at that bar's Close, before <see cref="IBacktestStrategy.Evaluate"/> and under the same conditions (trading active and
    /// Equity &gt; 0). The returned intent is QUEUED and applied at Step 1 of the NEXT bar, before that bar's GTD expiry and fills: an order that is pending
    /// at this Close and whose id is named becomes Cancelled and never fills (an id that is unknown, already terminal, or not yet submitted at this Close is
    /// ignored). The slot is not freed during this Close's own submissions, so a replacement can only be
    /// submitted at the next Close (and fills from the bar after). A cancellation queued at the final bar's Close is never applied.
    /// The pending order ids are exposed by <see cref="StrategyContext.PendingEntryOrderId"/> and <see cref="StrategyContext.PendingExitOrderId"/>.
    /// </summary>
    OrderCancellationIntent EvaluateCancellations(StrategyContext context);
}
