namespace StockAnalyzer.Core.Services.Backtest.Engine;

public enum ExitWinner { None, PendingOrder, MarginLiquidation }

/// <summary>
/// User-confirmed rule (Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6): when a pending
/// exit order (Limit/Stop/StopLimit) and the maintenance-margin liquidation threshold both apply to the same
/// open position on the same bar, whichever ACTUALLY HAPPENS first (the order's fill, not its activation) along the fixed
/// Open-&gt;High-&gt;Low-&gt;Close path wins; the loser is superseded/cancelled for that bar rather than
/// evaluated normally.
/// </summary>
public static class ExitPrecedenceResolver
{
    /// <param name="pendingOrderPosition">Path position where the resting exit order would actually fill (<see cref="OrderFillEvaluator.TryFindFillPosition"/>), or null if it does not fill this bar.</param>
    /// <param name="marginLiquidationPosition">Result of <see cref="MarginLiquidationCalculator.TryFindBreachPosition"/>, or null if the maintenance margin is not breached this bar.</param>
    public static ExitWinner Resolve(decimal? pendingOrderPosition, decimal? marginLiquidationPosition)
    {
        if (pendingOrderPosition is null && marginLiquidationPosition is null) return ExitWinner.None;
        if (pendingOrderPosition is null) return ExitWinner.MarginLiquidation;
        if (marginLiquidationPosition is null) return ExitWinner.PendingOrder;

        if (pendingOrderPosition.Value < marginLiquidationPosition.Value) return ExitWinner.PendingOrder;
        if (marginLiquidationPosition.Value < pendingOrderPosition.Value) return ExitWinner.MarginLiquidation;

        // Exact tie (e.g. both conditions are already true at Open). USER-CONFIRMED (2026-09-16):
        // margin liquidation is treated as the broker-mandated compliance action and wins the tie.
        return ExitWinner.MarginLiquidation;
    }
}
