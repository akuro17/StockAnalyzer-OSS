using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 determinism: identical input + configuration + strategy, executed <see cref="VerificationParameters.DeterminismRunCount"/>
/// times, must reproduce EVERY output field (orders, fills, trades, equity, signals, status), not only the hash - the engine's
/// ReproducibilityHash covers a subset of fields, so the canonical JSON snapshot is the authoritative comparison. The engine
/// itself has no random component; the seed only feeds the test-side data and strategy generators.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_DeterminismTests
{
    private static string Snapshot(BacktestResult result) => BacktestSnapshot.ToCanonicalJson(result);

    private static void AssertAllRunsIdentical(Func<BacktestResult> runOnce)
    {
        BacktestResult baseline = runOnce();
        string expected = Snapshot(baseline);
        for (int run = 1; run < VerificationParameters.DeterminismRunCount; run++)
        {
            BacktestResult again = runOnce();
            string actual = Snapshot(again);
            Assert.True(expected == actual, $"Run {run} differs from run 0. {GoldenMaster.DescribeFirstDifference(expected, actual)}");
            Assert.Equal(baseline.ReproducibilityHash, again.ReproducibilityHash);
        }
    }

    [Fact]
    public void ScriptedScenarioWithFeesAndSlippage_HundredRuns_AreIdentical()
    {
        AssertAllRunsIdentical(() => VerificationScenarios.LongRoundTrip(VerificationScenarios.CostedConfig(slippageRatio: 0.01m)).Result);
    }

    [Fact]
    public void SeededRandomStrategyOnRandomWalk_HundredRuns_AreIdentical()
    {
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestConfiguration config = VerificationHarness.MakeConfig(
            initialCapital: 10_000m, commissionFlat: 1m, commissionPerUnit: 0.01m, slippageRatio: 0.001m,
            sizingParameter: 20m, initialMarginRatio: 0.5m, maintenanceMarginRatio: 0.25m, liquidationPenaltyRatio: 0.005m);

        AssertAllRunsIdentical(() => VerificationHarness.Run(input, config, new SeededRandomStrategy(VerificationParameters.PrimarySeed)));
    }

    [Fact]
    public void RealIndicatorStrategy_HundredRuns_AreIdentical()
    {
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        BacktestInput input = VerificationHarness.MakeInput(bars, tradingStartIndex: VerificationParameters.SmaPeriod);
        BacktestConfiguration config = VerificationHarness.MakeConfig(initialCapital: 100_000m, commissionFlat: 1m, slippageRatio: 0.001m, sizingParameter: 50m);

        AssertAllRunsIdentical(() => VerificationHarness.Run(input, config, VerificationHarness.SmaTrendStrategy(VerificationParameters.SmaPeriod)));
    }

    [Fact]
    public void ConcurrentRuns_ShareNoHiddenState_AndMatchTheSequentialResult()
    {
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestConfiguration config = VerificationHarness.MakeConfig(
            initialCapital: 10_000m, commissionFlat: 1m, slippageRatio: 0.001m, sizingParameter: 20m);

        BacktestEngine engine = VerificationHarness.CreateEngine();
        string expected = Snapshot(engine.Run(input, config, new SeededRandomStrategy(VerificationParameters.PrimarySeed)));
        var snapshots = new string[VerificationParameters.DeterminismRunCount];

        Parallel.For(0, snapshots.Length, i =>
            snapshots[i] = Snapshot(engine.Run(input, config, new SeededRandomStrategy(VerificationParameters.PrimarySeed))));

        for (int i = 0; i < snapshots.Length; i++)
        {
            Assert.True(expected == snapshots[i], $"Concurrent run {i} differs. {GoldenMaster.DescribeFirstDifference(expected, snapshots[i])}");
        }
    }
}
