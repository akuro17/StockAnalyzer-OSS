using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Pluggable strategy shape referenced by the P1 completion bar
/// (<c>BacktestEngine.Run(BacktestInput, BacktestConfiguration, IBacktestStrategy)</c>). Its member shape
/// was not specified verbatim by the source spec doc beyond section 9's context field list — authored as
/// part of task #6 per Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 12 ("will need to be authored
/// as part of the P1 implementation ... an implementation-detail choice, not a spec gate").
/// </summary>
public interface IBacktestStrategy
{
    string Name { get; }

    /// <summary>
    /// Indicators this strategy needs precomputed by the engine's Gate G2 batch-prep pass, before the bar
    /// loop starts. Called once per run.
    /// </summary>
    IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators();

    /// <summary>
    /// Called at most once per bar (section 5.5 Step 7), only when <c>barIndex &gt;= TradingStartIndex</c>
    /// and Equity &gt; 0. Return null for "no action this bar". The engine enforces the v1 max-1-pending-
    /// order / max-1-open-position / no-partial-exit rules itself — this method only reports intent.
    /// </summary>
    StrategyOrderRequest? Evaluate(StrategyContext context);
}
