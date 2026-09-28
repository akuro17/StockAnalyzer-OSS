namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Marker for one node of a <see cref="BacktestConditionTree"/> (Composite pattern). The only implementations are
/// <see cref="BacktestConditionLeaf"/> (one comparison) and <see cref="BacktestConditionGroup"/> (an AND/OR group of
/// child nodes); the set is closed so evaluators can dispatch exhaustively without a virtual call.
/// Named "ConditionNode" (not "Signal") so it cannot be confused with <c>SignalType</c>/<c>BacktestSignal</c>.
/// </summary>
public interface IBacktestConditionNode
{
}
