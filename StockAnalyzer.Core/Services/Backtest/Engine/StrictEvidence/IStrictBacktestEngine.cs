using StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;

namespace StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence;

public interface IStrictBacktestEngine
{
    StrictBacktestResult Run(
        StrictBacktestInput input,
        StrictBacktestConfiguration configuration,
        IStrictBacktestStrategy strategy,
        CancellationToken cancellationToken = default);
}

public interface IStrictBacktestStrategy
{
    string Name { get; }

    /// <summary>
    /// Called once at each evidence-availability boundary at or after TradingStartUtc. Returning null
    /// is an explicit no-op. Any returned order must name every event used by the decision.
    /// </summary>
    StrictOrderRequest? Evaluate(StrictStrategyContext context);
}
