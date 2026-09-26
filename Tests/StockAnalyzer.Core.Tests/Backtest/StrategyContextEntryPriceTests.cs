using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task 2c regression/enabling proof (plan section 4.5 of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md): <see cref="StrategyContext.EntryPrice"/>
/// is a trailing, optional safe extension — null while flat, the open position's entry price while
/// holding one — and every existing strategy that never reads it is unaffected.
/// </summary>
public class StrategyContextEntryPriceTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    private static StrategyOrderRequest LongEntryMarket() => new(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "enter");
    private static StrategyOrderRequest LongExitMarket() => new(SignalType.LongExit, OrderType.Market, null, null, Gtc, "exit");

    /// <summary>Fires pre-scripted order requests on specific bar indices and records each bar's
    /// <see cref="StrategyContext.EntryPrice"/> as observed by <see cref="Evaluate"/>.</summary>
    private sealed class RecordingScriptedStrategy : IBacktestStrategy
    {
        private readonly Dictionary<int, StrategyOrderRequest> _script;
        public string Name => "RecordingScripted";
        public List<decimal?> RecordedEntryPrices { get; } = new();

        public RecordingScriptedStrategy(Dictionary<int, StrategyOrderRequest> script) => _script = script;

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            RecordedEntryPrices.Add(context.EntryPrice);
            return _script.TryGetValue(context.BarIndex, out StrategyOrderRequest request) ? request : null;
        }
    }

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    [Fact]
    public void EntryPrice_NullWhileFlat_SetWhileHolding_NullAgainAfterExit()
    {
        // Same fill timing as BacktestEngineTests.GoldenLong_MarketEntryThenExit: bar0 signal fills at
        // bar1 Open (101), bar1 exit signal fills at bar2 Open (110).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 101m, 108m),
            Bar(2, 110m, 115m, 109m, 111m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            Bar0, Bar0.AddDays(10), 0, 0);
        var strategy = new RecordingScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = LongEntryMarket(),
            [1] = LongExitMarket(),
        });

        VerificationHarness.CreateEngine().Run(input, MakeConfig(), strategy);

        Assert.Equal(3, strategy.RecordedEntryPrices.Count);
        Assert.Null(strategy.RecordedEntryPrices[0]);   // bar0: still flat before the entry fills
        Assert.Equal(101m, strategy.RecordedEntryPrices[1]); // bar1: entry filled at this bar's Open
        Assert.Null(strategy.RecordedEntryPrices[2]);   // bar2: exit already filled at this bar's Open
    }

    [Fact]
    public void ExistingStrategyThatNeverReadsEntryPrice_StillCompilesAndRuns()
    {
        // Safe-extension regression guard: a strategy that never touches the new property (like
        // NoOpBacktestStrategy) is completely unaffected.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 102m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            Bar0, Bar0.AddDays(10), 0, 0);
        var strategy = new NoOpBacktestStrategy(Array.Empty<StrategyIndicatorRequest>());

        BacktestResult result = VerificationHarness.CreateEngine().Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(result.Trades);
    }
}
