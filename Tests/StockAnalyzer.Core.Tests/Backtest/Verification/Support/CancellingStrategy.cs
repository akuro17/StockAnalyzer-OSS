#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// A <see cref="ScriptedStrategy"/> that also has the optional order-cancellation capability (owner decision G4): the entry/exit scripts are delegated to the
/// shared scripted strategy, so the two differ ONLY by the capability (same <see cref="Name"/>, same requests). Counts every call so invocation counts can be asserted.
/// </summary>
internal sealed class CancellingStrategy : IBacktestStrategy, IBacktestOrderCancellationStrategy
{
    private readonly ScriptedStrategy _scripted;
    private readonly Func<StrategyContext, OrderCancellationIntent> _cancel;

    public int EvaluateCalls;
    public int EvaluateExitCalls;
    public int CancelCalls;

    public CancellingStrategy(
        IReadOnlyDictionary<int, StrategyOrderRequest> entryScript,
        Func<StrategyContext, OrderCancellationIntent> cancel,
        IReadOnlyDictionary<int, StrategyOrderRequest>? exitScript = null)
    {
        _scripted = new ScriptedStrategy(entryScript, exitScript);
        _cancel = cancel;
    }

    public string Name => _scripted.Name;

    public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _scripted.GetRequiredIndicators();

    public StrategyOrderRequest? Evaluate(StrategyContext context)
    {
        EvaluateCalls++;
        return _scripted.Evaluate(context);
    }

    public StrategyOrderRequest? EvaluateExit(StrategyContext context)
    {
        EvaluateExitCalls++;
        return _scripted.EvaluateExit(context);
    }

    public OrderCancellationIntent EvaluateCancellations(StrategyContext context)
    {
        CancelCalls++;
        return _cancel(context);
    }
}
