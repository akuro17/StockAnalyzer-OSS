namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// A node of a Filters condition tree (<see cref="ScreenerConditionGroup"/> or <see cref="ScreenerConditionLeaf"/>).
/// Marker interface only, mirroring <c>IBacktestConditionNode</c> in
/// <c>StockAnalyzer.Core.Models.Backtest.Engine</c>; this Screener-specific family is intentionally not shared with
/// that Backtest type (single-root Filters tree vs. Backtest's fixed Entry/Exit/Reverse x Long/Short six-root tree
/// are different purposes, per Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md section 1.2).
/// </summary>
public interface IScreenerConditionNode
{
}
