namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Result of evaluating one pending order against one bar. When <see cref="Filled"/> is false,
/// <see cref="StopActivated"/> still communicates whether a StopLimit order's Stop leg triggered
/// on this bar (the caller must persist this onto the order's <c>StopActivated</c> field so a later
/// bar's evaluation skips the Stop re-check and scans only for the Limit leg, per
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.6: bar extremes seen before the trigger
/// point must never be reused for the StopLimit fill).
/// </summary>
public readonly record struct OrderFillOutcome(
    bool Filled,
    decimal FillPrice,
    decimal Commission,
    decimal SlippageAmount,
    bool StopActivated)
{
    public static readonly OrderFillOutcome NoFill = new(false, 0m, 0m, 0m, false);

    public static OrderFillOutcome StopTriggeredNoLimitFill() => new(false, 0m, 0m, 0m, true);
}

/// <summary>Outcome of <see cref="OrderFillEvaluator.EvaluateFrom"/>: the usual <see cref="OrderFillOutcome"/> plus the path point where the fill happened (null when not filled).</summary>
public readonly record struct ResidualFill(OrderFillOutcome Outcome, PathPoint? FillPoint)
{
    public static ResidualFill None(OrderFillOutcome outcome) => new(outcome, null);
}
