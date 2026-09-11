using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class SignalOrchestratorTests
{
    private readonly SignalOrchestrator _orchestrator = new();

    [Fact]
    public void Orchestrate_EmptySignals_ReturnsNeutralScore()
    {
        var result = _orchestrator.Orchestrate(100, new List<ConfluenceSignal>());
        Assert.Equal(50, result.Score);
        Assert.Equal(SignalDirection.Neutral, result.FinalDirection);
    }

    [Fact]
    public void Orchestrate_AllBullish_ReturnsHighPositiveScore()
    {
        var signals = new List<ConfluenceSignal>
        {
            new(100, SignalType.GoldenCross, SignalDirection.Bullish, DecorrelationGroup.Trend),
            new(100, SignalType.RegularBullishDivergence, SignalDirection.Bullish, DecorrelationGroup.Momentum)
        };

        var result = _orchestrator.Orchestrate(100, signals);
        Assert.Equal(100, result.Score); // Since there is no disagreement, the normalized net direction is 1.0 (100%)
        Assert.Equal(SignalDirection.Bullish, result.FinalDirection);
        Assert.Equal(2, result.ConfluenceCount);
    }

    [Fact]
    public void Orchestrate_AllBearish_ReturnsLowScore()
    {
        var signals = new List<ConfluenceSignal>
        {
            new(100, SignalType.DeadCross, SignalDirection.Bearish, DecorrelationGroup.Trend)
        };

        var result = _orchestrator.Orchestrate(100, signals);
        Assert.Equal(0, result.Score); // Net direction -1.0 -> 0%
        Assert.Equal(SignalDirection.Bearish, result.FinalDirection);
    }

    [Fact]
    public void Orchestrate_MixedSignals_ReturnsIntermediateScore()
    {
        var signals = new List<ConfluenceSignal>
        {
            new(100, SignalType.GoldenCross, SignalDirection.Bullish, DecorrelationGroup.Trend, Weight: 2.0),
            new(100, SignalType.DeadCross, SignalDirection.Bearish, DecorrelationGroup.Trend, Weight: 1.0)
        };

        var result = _orchestrator.Orchestrate(100, signals);
        
        // S_bull = 2.0, S_bear = 1.0
        // Raw = (2-1)/(2+1) = 1/3 = 0.3333...
        // Normalized = (0.3333 + 1.0) / 2 * 100 = 66.66... -> Round to 67
        Assert.Equal(67, result.Score);
        Assert.Equal(SignalDirection.Bullish, result.FinalDirection);
    }

    [Fact]
    public void Orchestrate_Deduplication_HandlesProximity()
    {
        var signals = new List<ConfluenceSignal>
        {
            new(99, SignalType.GoldenCross, SignalDirection.Bullish, DecorrelationGroup.Trend, Strength: 0.5),
            new(100, SignalType.GoldenCross, SignalDirection.Bullish, DecorrelationGroup.Trend, Strength: 0.9) // Stronger
        };

        var result = _orchestrator.Orchestrate(100, signals);
        
        // Should ignore index 99 and keep index 100 because they are ±1 bar
        Assert.Equal(1, result.ConfluenceCount);
    }

    [Fact]
    public void Orchestrate_Decorrelation_AppliesGroupWeighting()
    {
        // 2 Momentum signals vs 1 independent Trend signal
        // Each momentum signal gets weight 1.0 * 1/2 = 0.5. Total momentum weight = 1.0.
        // The independent Trend signal gets 1.0. Total trend weight = 1.0.
        // Resulting score should reflect equal influence despite signal count difference.
        
        var signals = new List<ConfluenceSignal>
        {
            new(100, SignalType.RegularBullishDivergence, SignalDirection.Bullish, DecorrelationGroup.Momentum),
            new(100, SignalType.HiddenBullishDivergence, SignalDirection.Bullish, DecorrelationGroup.Momentum),
            new(100, SignalType.DeadCross, SignalDirection.Bearish, DecorrelationGroup.Trend)
        };

        var result = _orchestrator.Orchestrate(100, signals);

        // S_bull = (1.0 * 0.5) + (1.0 * 0.5) = 1.0
        // S_bear = (1.0 * 1.0) = 1.0
        // Result should be 50% (Neutral) even though there are 2 bullish signals and only 1 bearish.
        Assert.Equal(50, result.Score);
        Assert.Equal(SignalDirection.Neutral, result.FinalDirection);
    }
}
