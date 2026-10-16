using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// The outcome of a bar that reached BOTH the Stop-Loss and the Take-Profit of the held position: the side, which threshold the strategy's
/// <c>PricePathConflictMode</c> resolved it to, whether the Open already decided the order (a gap through one threshold), and whether the other
/// <c>PricePathConflictMode</c> would have resolved the same bar to the other threshold.
/// </summary>
public readonly record struct RiskTie(TradeSide Side, SlTpWinner Outcome, bool OpenDecided, bool OutcomeDiffersUnderOtherMode);

/// <summary>
/// Optional capability of an <see cref="IBacktestStrategy"/> (same pattern as <see cref="IBacktestOrderCancellationStrategy"/>): reporting that the bar it just
/// evaluated reached both of its entry-relative risk thresholds, so the engine can record an order-dependent bar. A strategy without it runs exactly as before.
/// </summary>
public interface IBacktestRiskTieDetector
{
    /// <summary>
    /// A pure function of <paramref name="context"/> and the strategy's own settings (it changes no state and allocates nothing), called by the engine
    /// right after <see cref="IBacktestStrategy.Evaluate"/> and <see cref="IBacktestStrategy.EvaluateExit"/> of the same bar.
    /// Returns true only while a position is held, both thresholds exist and the bar reached both.
    /// </summary>
    bool TryDetectRiskTie(in StrategyContext context, out RiskTie tie);
}
