using System;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>One serialized, resettable finite-horizon Core accountant. Python receives snapshots, never account mutation rights.</summary>
public interface IRlTradingSession : IDisposable
{
    RlSessionSnapshot Reset(CancellationToken cancellationToken = default);
    RlStepResult Step(RlAction action, CancellationToken cancellationToken = default);
    BacktestResult GetResult();
}

public interface IRlContinuousTradingSession : IRlTradingSession
{
    RlStepResult Step(RlAction direction, float rawSize, float rawStop, CancellationToken cancellationToken = default);
    RlStepResult StepFixedControl(RlAction direction, CancellationToken cancellationToken = default);
}
