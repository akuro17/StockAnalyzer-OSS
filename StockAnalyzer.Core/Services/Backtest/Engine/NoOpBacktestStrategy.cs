using System.Collections.Generic;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// P3 Gate G7 (user-confirmed 2026-09-17, see Y:\Temp\sa_ai_context_BacktestUiWindow_P3.md section 1):
/// the source spec never defines how BacktestWindow's indicator-selection tab turns into buy/sell
/// signals, and inventing that convention would violate AGENTS.md's Specification Authority rule
/// (no independent selection among unspecified conventions). This strategy only requests the
/// caller's selected indicators be precomputed and displayed (<see cref="GetRequiredIndicators"/>);
/// it never opens or closes a position (<see cref="Evaluate"/> always returns null), so every run
/// using it completes with zero trades. Real signal-generation strategies are out of scope for P3
/// and left for a future phase.
/// </summary>
public sealed class NoOpBacktestStrategy : IBacktestStrategy
{
    private readonly IReadOnlyList<StrategyIndicatorRequest> _requiredIndicators;

    public NoOpBacktestStrategy(IReadOnlyList<StrategyIndicatorRequest> requiredIndicators)
    {
        _requiredIndicators = requiredIndicators;
    }

    public string Name => "NoOp";

    /// <summary>The requested indicators as plain data, so the run fingerprint can read them without a strategy call (the engine makes the one declared call per run).</summary>
    public IReadOnlyList<StrategyIndicatorRequest> RequiredIndicators => _requiredIndicators;

    public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _requiredIndicators;

    public StrategyOrderRequest? Evaluate(StrategyContext context) => null;
}
