using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 metamorphic tests: change ONE knob of an otherwise identical run and assert the exact relation that must follow.
/// They catch sign errors and rounding issues that a single golden number can hide.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_MetamorphicTests
{
    private const decimal FeePerFillFlat = 15m;

    [Fact]
    public void AddingFees_NeverImprovesEquity_AndCostsExactlyTheCommissionsPaidSoFar()
    {
        ScenarioRun free = VerificationScenarios.MultiTrade(VerificationScenarios.FreeConfig(), leaveFourthOpen: true);
        ScenarioRun costed = VerificationScenarios.MultiTrade(VerificationHarness.MakeConfig(commissionFlat: FeePerFillFlat), leaveFourthOpen: true);

        Assert.Equal(free.Result.Fills.Length, costed.Result.Fills.Length);
        for (int k = 0; k < free.Result.EquityPoints.Length; k++)
        {
            decimal commissionsSoFar = costed.Result.Fills.Where(f => f.BarIndex <= k).Sum(f => f.Commission);
            Assert.True(costed.Result.EquityPoints[k].Equity <= free.Result.EquityPoints[k].Equity, $"Fees improved equity at bar {k}");
            Eq(free.Result.EquityPoints[k].Equity - commissionsSoFar, costed.Result.EquityPoints[k].Equity, $"Equity@{k}");
        }
    }

    [Fact]
    public void AddingSlippage_OnMarketOrders_NeverImprovesEquity_AndCostsExactlyTheSlippageAmounts()
    {
        ScenarioRun none = VerificationScenarios.MultiTrade(VerificationScenarios.FreeConfig(), leaveFourthOpen: false);
        ScenarioRun slipped = VerificationScenarios.MultiTrade(VerificationHarness.MakeConfig(slippageRatio: 0.005m), leaveFourthOpen: false);

        decimal noneFinal = none.Result.EquityPoints[^1].Equity;
        decimal slippedFinal = slipped.Result.EquityPoints[^1].Equity;
        Assert.True(slippedFinal <= noneFinal);
        Eq(slipped.Result.Fills.Sum(f => f.SlippageAmount), noneFinal - slippedFinal, "equity lost == sum of SlippageAmount");
    }

    [Fact]
    public void LongOnOriginalPrices_And_ShortOnMirroredPrices_ProduceIdenticalTradePnL()
    {
        // Additive mirror p' = 2*pivot - p (High/Low swapped): a Long gain of x becomes a Short gain of x. Fees 0, slippage 0.
        ImmutableArray<CandleData> original = VerificationScenarios.MultiTradeBars;
        ImmutableArray<CandleData> mirrored = SyntheticBars.Mirror(original, VerificationParameters.MirrorPivot);
        Assert.All(mirrored, b => Assert.True(b.Low > 0m));

        BacktestConfiguration config = VerificationScenarios.FreeConfig();
        var longScript = new System.Collections.Generic.Dictionary<int, StrategyOrderRequest>
        {
            [0] = VerificationHarness.Req(SignalType.LongEntry), [1] = VerificationHarness.Req(SignalType.LongExit),
            [3] = VerificationHarness.Req(SignalType.LongEntry), [4] = VerificationHarness.Req(SignalType.LongExit),
            [6] = VerificationHarness.Req(SignalType.LongEntry), [7] = VerificationHarness.Req(SignalType.LongExit),
        };
        var shortScript = new System.Collections.Generic.Dictionary<int, StrategyOrderRequest>
        {
            [0] = VerificationHarness.Req(SignalType.ShortEntry), [1] = VerificationHarness.Req(SignalType.ShortExit),
            [3] = VerificationHarness.Req(SignalType.ShortEntry), [4] = VerificationHarness.Req(SignalType.ShortExit),
            [6] = VerificationHarness.Req(SignalType.ShortEntry), [7] = VerificationHarness.Req(SignalType.ShortExit),
        };

        BacktestResult longResult = VerificationHarness.Run(VerificationHarness.MakeInput(original), config, new ScriptedStrategy(longScript));
        BacktestResult shortResult = VerificationHarness.Run(VerificationHarness.MakeInput(mirrored), config, new ScriptedStrategy(shortScript));

        Assert.Equal(longResult.Trades.Length, shortResult.Trades.Length);
        for (int i = 0; i < longResult.Trades.Length; i++)
        {
            Assert.Equal(TradeSide.Long, longResult.Trades[i].Side);
            Assert.Equal(TradeSide.Short, shortResult.Trades[i].Side);
            Eq(longResult.Trades[i].ClosedNet, shortResult.Trades[i].ClosedNet, $"trade {i} net");
        }
        Eq(longResult.EquityPoints[^1].Equity, shortResult.EquityPoints[^1].Equity, "final equity");
    }

    [Fact]
    public void ScalingAllPricesByTen_ScalesEveryTradePnLByTen_WithIdenticalSignals()
    {
        // Same fixed quantity, prices x10, slippage is a ratio (scale-free), no fees: P&L must scale by exactly 10.
        ImmutableArray<CandleData> baseBars = VerificationScenarios.MultiTradeBars;
        ImmutableArray<CandleData> scaledBars = SyntheticBars.Scale(baseBars, VerificationParameters.ScaleFactor);
        BacktestConfiguration config = VerificationHarness.MakeConfig(slippageRatio: 0.01m, initialCapital: 100_000_000m);
        var script = new System.Collections.Generic.Dictionary<int, StrategyOrderRequest>
        {
            [0] = VerificationHarness.Req(SignalType.LongEntry), [1] = VerificationHarness.Req(SignalType.LongExit),
            [3] = VerificationHarness.Req(SignalType.LongEntry), [4] = VerificationHarness.Req(SignalType.LongExit),
        };

        BacktestResult a = VerificationHarness.Run(VerificationHarness.MakeInput(baseBars), config, new ScriptedStrategy(script));
        BacktestResult b = VerificationHarness.Run(VerificationHarness.MakeInput(scaledBars), config, new ScriptedStrategy(script));

        Assert.Equal(a.Trades.Length, b.Trades.Length);
        for (int i = 0; i < a.Trades.Length; i++)
        {
            Eq(a.Trades[i].ClosedNet * VerificationParameters.ScaleFactor, b.Trades[i].ClosedNet, $"trade {i} net x scale");
            Eq(a.Trades[i].EntryPrice * VerificationParameters.ScaleFactor, b.Trades[i].EntryPrice, $"trade {i} entry x scale");
        }
        Eq((a.EquityPoints[^1].Equity - config.InitialCapital) * VerificationParameters.ScaleFactor,
            b.EquityPoints[^1].Equity - config.InitialCapital, "equity change x scale");
    }

    [Fact]
    public void ScalingAllPricesByTen_RealIndicatorStrategy_ProducesTheSameSignalSequence()
    {
        ImmutableArray<CandleData> baseBars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        ImmutableArray<CandleData> scaledBars = SyntheticBars.Scale(baseBars, VerificationParameters.ScaleFactor);
        BacktestConfiguration config = VerificationHarness.MakeConfig(initialCapital: 100_000_000m, sizingParameter: 50m);

        BacktestResult a = VerificationHarness.Run(VerificationHarness.MakeInput(baseBars, VerificationParameters.SmaPeriod), config, VerificationHarness.SmaTrendStrategy(VerificationParameters.SmaPeriod));
        BacktestResult b = VerificationHarness.Run(VerificationHarness.MakeInput(scaledBars, VerificationParameters.SmaPeriod), config, VerificationHarness.SmaTrendStrategy(VerificationParameters.SmaPeriod));

        Assert.NotEmpty(a.Signals);
        Assert.Equal(a.Signals.Select(s => (s.Type, s.BarIndex)), b.Signals.Select(s => (s.Type, s.BarIndex)));
    }
}
