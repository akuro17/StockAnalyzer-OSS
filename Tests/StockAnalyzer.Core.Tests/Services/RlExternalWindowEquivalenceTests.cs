using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Proofs that a 192-bar window cut from a longer history behaves exactly like the same bars inside that history.</summary>
public sealed class RlExternalWindowEquivalenceTests
{
    /// <summary>Real daily bars whose close oscillates on two time scales, so the 20/60 SMA pair crosses many times.</summary>
    private static ImmutableArray<CandleData> Bars(int count)
        => Enumerable.Range(0, count).Select(i =>
        {
            decimal close = 100m + (decimal)(10 * Math.Sin(i / 9.0) + 5 * Math.Sin(i / 3.1));
            return new CandleData(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), close, close + 1m, close - 1m, close, 1_000 + i);
        }).ToImmutableArray();

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(17)] [InlineData(100)] [InlineData(208)]
    public void SmaSignalsOfACutWindowEqualTheSignalsOfTheFullHistoryAtEveryDecision(int start)
    {
        var history = Bars(400);
        var full = RlBaselinePolicies.CreateSmaSignals(history);
        var window = history.AsSpan(start, RlContract.SupportLength).ToArray().ToImmutableArray();
        var cut = RlBaselinePolicies.CreateSmaSignals(window);
        for (int decision = 0; decision < RlContract.EpisodeHorizon; decision++)
        {
            Assert.Equal(full[start + RlContract.WindowLength - 1 + decision], cut[RlContract.WindowLength - 1 + decision]);
            Assert.Equal(cut[RlContract.WindowLength - 1 + decision],
                RlBaselinePolicies.GetAction(RlContract.SmaBaselineId, cut, RlContract.WindowLength - 1, decision, RlContract.CandidateSeeds[0]));
        }
    }

    [Fact]
    public void TheFixtureSeriesReallyProducesBuyAndSellSignalsInsideTheDecisionRange()
    {
        var signals = RlBaselinePolicies.CreateSmaSignals(Bars(400));
        var decisions = signals.Skip(RlContract.WindowLength - 1).Take(RlContract.EpisodeHorizon).ToArray();
        Assert.Contains(RlAction.Buy, decisions);
        Assert.Contains(RlAction.Sell, decisions);
    }

    [Fact]
    public void TheSourceOverloadOfTheSmaSignalsStillEqualsTheBarsOverload()
    {
        var bars = Bars(400);
        string directory = RlSourceFixture.WriteSource(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sa_rl_sma_" + Guid.NewGuid().ToString("N")), 1_278);
        try
        {
            var source = RlSourceSnapshot.Load(directory, RlSourceFixture.Limits, smokeOnly: true);
            Assert.Equal(RlBaselinePolicies.CreateSmaSignals(source.Bars), RlBaselinePolicies.CreateSmaSignals(source));
            var block = source.Catalog.Train[0];
            for (int decision = 0; decision < RlContract.EpisodeHorizon; decision++)
                Assert.Equal(RlBaselinePolicies.CreateSmaSignals(source.Bars)[block.StartIndex + RlContract.WindowLength - 1 + decision],
                    RlBaselinePolicies.GetAction(RlContract.SmaBaselineId, source, block, decision, RlContract.CandidateSeeds[0]));
        }
        finally { try { System.IO.Directory.Delete(System.IO.Directory.GetParent(directory)!.FullName, true); } catch (System.IO.IOException) { } }
        Assert.Equal(RlBaselinePolicies.CreateSmaSignals(bars), RlBaselinePolicies.CreateSmaSignals(bars));
    }

    [Fact]
    public void TheBarsOverloadRejectsMissingSmaSignalsAndUnknownPolicies()
    {
        Assert.Throws<ArgumentException>(() => RlBaselinePolicies.GetAction(RlContract.SmaBaselineId, default, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => RlBaselinePolicies.GetAction("unknown", default, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlBaselinePolicies.GetAction(RlContract.HoldBaselineId, default, 0, RlContract.EpisodeHorizon, 1));
        Assert.Equal(RlAction.Hold, RlBaselinePolicies.GetAction(RlContract.HoldBaselineId, default, 0, 5, 1));
        Assert.Equal(RlAction.Buy, RlBaselinePolicies.GetAction(RlContract.BuyHoldBaselineId, default, 0, 0, 1));
        Assert.Equal(RlAction.Hold, RlBaselinePolicies.GetAction(RlContract.BuyHoldBaselineId, default, 0, 1, 1));
        Assert.Equal(RlBaselinePolicies.RandomAction(1, 3), RlBaselinePolicies.GetAction(RlContract.RandomBaselineId, default, 0, 3, 1));
    }

    /// <summary>Constant closes up to <paramref name="flatBars"/> bars, then one close that differs by <paramref name="step"/>.</summary>
    private static ImmutableArray<CandleData> FlatThenStep(int flatBars, decimal step)
        => Enumerable.Range(0, flatBars + 1).Select(i =>
        {
            decimal close = i < flatBars ? 100m : 100m + step;
            return new CandleData(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), close, close, close, close, 1_000);
        }).ToImmutableArray();

    [Fact]
    public void AnEqualAverageOnTheBarBeforeTheCrossingIsACrossingOnlyWhenTheFastAverageThenMovesPastTheSlowOne()
    {
        int step = RlContract.SmaSlowPeriod + 5; // the averages are equal (both 100) on every bar from SmaSlowPeriod - 1 up to step - 1
        var up = RlBaselinePolicies.CreateSmaSignals(FlatThenStep(step, 10m));
        Assert.Equal(RlAction.Buy, up[step]);
        Assert.All(up.Take(step), action => Assert.Equal(RlAction.Hold, action));
        var down = RlBaselinePolicies.CreateSmaSignals(FlatThenStep(step, -10m));
        Assert.Equal(RlAction.Sell, down[step]);
        Assert.All(down.Take(step), action => Assert.Equal(RlAction.Hold, action));
        // Equal averages on both bars (no move at all) is never a crossing.
        Assert.All(RlBaselinePolicies.CreateSmaSignals(FlatThenStep(step, 0m)), action => Assert.Equal(RlAction.Hold, action));
    }

    /// <summary>
    /// The 128 actions of the Random baseline of the first candidate seed, recorded once from the .NET 8 <c>System.Random(int)</c> stream
    /// (seed + <see cref="RlContract.RandomBaselineSeedOffset"/>, one <c>Next(ActionCount)</c> per decision; 0 Hold, 1 Buy, 2 Sell). A change of this
    /// string means the runtime changed the pseudo-random stream, which would silently change every saved Random baseline result.
    /// </summary>
    private const string PinnedRandomActions =
        "21021221001120021000000220112202102121002211220000221002010111212221022022101120200101002001121100202221020120021221000011101112";

    [Fact]
    public void TheRandomBaselineSequenceOfTheFirstSeedIsPinnedToTheRuntimeStream()
    {
        var actual = Enumerable.Range(0, RlContract.EpisodeHorizon)
            .Select(decision => (int)RlBaselinePolicies.RandomAction(RlContract.CandidateSeeds[0], decision)).ToArray();
        Assert.Equal(RlContract.EpisodeHorizon, PinnedRandomActions.Length);
        Assert.Equal(PinnedRandomActions, string.Concat(actual));
        for (int decision = 0; decision < RlContract.EpisodeHorizon; decision++)
            Assert.Equal((RlAction)actual[decision], RlBaselinePolicies.GetAction(RlContract.RandomBaselineId, default, 0, decision, RlContract.CandidateSeeds[0]));
    }

    private static BacktestConfiguration Costs()
        => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m });

    private static FixedScaler Scaler() => new(FixedScaler.CurrentSchemaVersion, RlFiles.FeatureMode, RlContract.WindowLength, Array.Empty<int>(), null,
        Enumerable.Range(0, RlContract.MarketChannelCount).Select(c => $"lag0:c{c}").ToArray(),
        Enumerable.Repeat(new FixedScalerStatistic(100d, 10d, 0d, 2_000d), RlContract.MarketChannelCount).ToArray(), "hash");

    /// <summary>The observation and the account after <paramref name="decisions"/> decisions of a fixed action pattern (Buy, Hold, Sell, ...).</summary>
    private static (float[] Observation, string Account) ObserveAfter(ImmutableArray<CandleData> bars, int decisions)
    {
        using var session = new BacktestEngine(new IndicatorFactory()).CreateRlSession(RlSourceSnapshot.CreateEpisodeInput("SYN", bars), Costs());
        var account = session.Reset();
        for (int i = 0; i < decisions; i++)
            account = session.Step((RlAction)(i % RlContract.ActionCount)).Snapshot;
        return (RlObservationBuilder.Build(account, Scaler()), System.Text.Json.JsonSerializer.Serialize(account, RlFiles.Json));
    }

    private static CandleData Scaled(CandleData bar, decimal factor)
        => new(bar.Timestamp, bar.Open * factor, bar.High * factor, bar.Low * factor, bar.Close * factor, bar.Volume);

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(63)] [InlineData(126)]
    public void TheObservationAndTheAccountAfterKDecisionsDoNotDependOnAnyLaterBar(int decisions)
    {
        var bars = Bars(RlContract.SupportLength);
        int lastSeen = RlContract.WindowLength - 1 + decisions; // the bar of the shown state; the decision made there is executed on the NEXT bar
        var original = ObserveAfter(bars, decisions);
        var laterRewritten = ObserveAfter(bars.Select((bar, i) => i > lastSeen ? Scaled(bar, 1.7m) : bar).ToImmutableArray(), decisions);
        Assert.True(original.Observation.SequenceEqual(laterRewritten.Observation));
        Assert.Equal(original.Account, laterRewritten.Account);

        // Control: a bar inside the observed window changes the observation, so the comparison above can fail.
        var insideRewritten = ObserveAfter(bars.Select((bar, i) => i == lastSeen - 10 ? Scaled(bar, 1.7m) : bar).ToImmutableArray(), decisions);
        Assert.False(original.Observation.SequenceEqual(insideRewritten.Observation));
    }

    [Fact]
    public void TheRawWindowIsTheScalerInputOfTheObservation()
    {
        var bars = Bars(RlContract.SupportLength);
        using var session = new BacktestEngine(new IndicatorFactory()).CreateRlSession(RlSourceSnapshot.CreateEpisodeInput("SYN", bars), Costs());
        var account = session.Reset();
        var raw = RlObservationBuilder.RawWindow(account);
        Assert.Equal(RlContract.WindowLength * RlContract.MarketChannelCount, raw.Length);
        Assert.Equal(((double)bars[0].Open, (double)bars[0].High, (double)bars[0].Low, (double)bars[0].Close, (double)bars[0].Volume),
            (raw[0], raw[1], raw[2], raw[3], raw[4]));
        int last = (RlContract.WindowLength - 1) * RlContract.MarketChannelCount;
        Assert.Equal((double)bars[RlContract.WindowLength - 1].Close, raw[last + 3]);

        var scaler = Scaler();
        var observation = RlObservationBuilder.Build(account, scaler);
        var transformed = new float[raw.Length];
        scaler.Transform(raw, transformed);
        Assert.Equal(transformed, observation.Take(raw.Length));
    }
}
