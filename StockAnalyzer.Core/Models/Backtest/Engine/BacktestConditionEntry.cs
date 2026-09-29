using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// One comparison condition for <c>ConditionBasedBacktestStrategy</c>: Left [Operator] Right (or a
/// static numeric value), combined with the NEXT entry in a sequence via <see cref="LogicalOperator"/>
/// — the same left-to-right chained convention <c>ScreenerIndicatorEntry</c> uses, re-expressed as
/// boolean AND/OR by <see cref="StockAnalyzer.Core.Services.Backtest.Engine.BacktestConditionEvaluator"/>
/// instead of per-ticker set operations. See
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.1.
/// </summary>
public sealed class BacktestConditionEntry
{
    public BacktestConditionSide Left { get; init; } = new();

    public ComparisonOperator Operator { get; init; } = ComparisonOperator.GreaterThan;

    public RightHandTargetMode TargetMode { get; init; } = RightHandTargetMode.NumericValue;

    public decimal RightNumericValue { get; init; }

    /// <summary>Set when <see cref="TargetMode"/> is <see cref="RightHandTargetMode.Indicator"/>.</summary>
    public BacktestConditionSide? Right { get; init; }

    /// <summary>Combines this entry's result with the NEXT entry in the sequence.</summary>
    public LogicalOperator LogicalOperator { get; init; } = LogicalOperator.And;

    public BacktestConditionRole Role { get; init; } = BacktestConditionRole.Both;

    /// <summary>
    /// Safe extension (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.1):
    /// the trade direction this entry's Entry-side contribution opens. Defaults to <see cref="TradeSide.Long"/>,
    /// which combined with <see cref="Role"/> Both/EntryOnly maps to <c>SignalType.LongEntry</c> - the ONLY
    /// entry signal <c>ConditionBasedBacktestStrategy</c> could ever produce before this field existed, so
    /// every entry constructed without setting this property keeps byte-identical behavior. Per that plan's
    /// section 2.2.1, an <see cref="BacktestConditionRole.ExitOnly"/> entry's exit contribution stays
    /// side-agnostic (closes whichever side is currently held) and does not read this property.
    /// </summary>
    public TradeSide Position { get; init; } = TradeSide.Long;
}
