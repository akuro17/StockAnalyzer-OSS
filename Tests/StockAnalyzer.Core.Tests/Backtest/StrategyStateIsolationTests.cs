#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Pins that the trailing-stop state of <see cref="ConditionBasedBacktestStrategy"/> never leaks from one run into another
/// (docs/sa_ai_context_McpPart2WebAiReview, T13): every evaluation builds a NEW strategy from the specification, and a reused instance
/// resets itself when the bar index runs backwards (<c>CheckRunBoundary</c> uses <c>&lt;=</c>, so a second run from bar 0 is a new run).
/// </summary>
public class StrategyStateIsolationTests
{
    private static readonly BacktestRiskManagementSettings TrailingRatio = new()
    {
        DynamicExits = new DynamicExitConfiguration
        {
            TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.Ratio, Ratio = 0.10m },
        },
    };

    private static BacktestConditionEntry CloseAbove(decimal threshold) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = threshold,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Long,
    };

    // Run A: entry at bar 2 (100), the price climbs to 125 and the run ends with the position open (trailing stop 0.9 x 125 = 112.5).
    private static readonly ImmutableArray<CandleData> RunA = SyntheticBars.FromTable(
        (100m, 101m, 99m, 100m),
        (100m, 106m, 99m, 105m),   // entry signal
        (100m, 110m, 99m, 108m),   // entry fill at 100
        (108m, 120m, 105m, 118m),
        (118m, 125m, 115m, 124m));

    // Run B: the same entry bar index (2) at the same price, but the price stays below 106, far under run A's stop of 112.5. Stale state would
    // make the position exit at once; a clean state (stop 0.9 x 106 = 95.4) holds the position to the end.
    private static readonly ImmutableArray<CandleData> RunB = SyntheticBars.FromTable(
        (100m, 101m, 99m, 100m),
        (100m, 106m, 99m, 105m),   // entry signal
        (100m, 106m, 99m, 103m),   // entry fill at 100
        (103m, 106m, 100m, 104m),
        (104m, 106m, 101m, 103m),
        (103m, 105m, 100m, 102m));

    private static BacktestResult Run(ImmutableArray<CandleData> bars, IBacktestStrategy strategy)
        => VerificationHarness.Run(VerificationHarness.MakeInput(bars), VerificationHarness.MakeConfig(), strategy);

    [Fact]
    public void EachCreateStrategyCall_ReturnsANewInstance()
    {
        BacktestStrategySpecification specification = BacktestStrategySpecification.ConditionBased(new[] { CloseAbove(102m) }, TrailingRatio);

        IBacktestStrategy first = specification.CreateStrategy();
        IBacktestStrategy second = specification.CreateStrategy();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void ReusingOneStrategyInstanceForASecondRunFromBarZero_EqualsAFreshInstance()
    {
        var reused = new ConditionBasedBacktestStrategy(new[] { CloseAbove(102m) }, TrailingRatio);
        BacktestResult runA = Run(RunA, reused);
        Assert.Empty(runA.Trades); // run A really ends with an open position (trailing state is set)

        BacktestResult second = Run(RunB, reused);
        BacktestResult fresh = Run(RunB, new ConditionBasedBacktestStrategy(new[] { CloseAbove(102m) }, TrailingRatio));

        Assert.Empty(fresh.Trades);
        Assert.Empty(second.Trades);
        Assert.Equal(fresh.ReproducibilityHash, second.ReproducibilityHash);
        Assert.Equal(fresh.Orders.Length, second.Orders.Length);
        Assert.Equal(fresh.Warnings.Length, second.Warnings.Length);
    }

    [Fact]
    public void ParallelRunsOfOneSpecification_EqualTheSerialRun()
    {
        BacktestStrategySpecification specification = BacktestStrategySpecification.ConditionBased(new[] { CloseAbove(102m) }, TrailingRatio);
        ImmutableArray<CandleData>[] inputs = Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? RunA : RunB).ToArray();

        byte[][] serial = inputs.Select(bars => Run(bars, specification.CreateStrategy()).ReproducibilityHash).ToArray();
        var parallel = new byte[inputs.Length][];
        Parallel.For(0, inputs.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
            parallel[index] = Run(inputs[index], specification.CreateStrategy()).ReproducibilityHash);

        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.Equal(serial[i], parallel[i]);
        }

        Assert.NotEqual(serial[0], serial[1]); // the two inputs really differ, so the comparison above is not vacuous
    }
}
