using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FX1b: the two cross-sectional channels of the v2-a observation (relative one-bar return, cross-sectional rank). The expected values are recomputed here
/// by a brute-force double implementation of the written definition (never by the production code), and a small table is worked out by hand.
/// </summary>
public sealed class RlPortfolioV2FeatureTests
{
    private const int Window = 64, Base = 5, Channels = 7, Slot = Window * Channels + 2, MaxSymbols = 4;
    private static readonly FixedScaler Identity7 = RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -100d, 100d), 7).ToArray(), RlPortfolioObservationLayout.V2A);
    private static readonly FixedScaler Identity5 = RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -100d, 100d), 5).ToArray());

    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });

    private static RlPortfolioSession Session(IRlPanelBars bars, params int[] tradable)
        => new(bars, RlPanelBarsFixture.Block(tradable), tradable.ToImmutableArray(), Costs());

    /// <summary>Symbols 0 and 1 share every price below index 80 (ties in the rank); the others differ.</summary>
    private static (decimal, decimal)? Prices(int s, int t)
    {
        decimal close = s < 2 && t < 80 ? 100m + t % 7 : 100m + s * 3 + (t * (s + 3)) % 11;
        return (close + (s + 1) * 0.5m, close);
    }

    [Fact]
    public void TheNamedChannelPositionsAndTheRankCenteringAgreeWithTheContracts()
    {
        // The production code names these positions; the brute-force reference below keeps its own literals on purpose (an independent oracle).
        Assert.Equal("Close", RlContract.MarketChannelNames[RlPortfolioObservationBuilder.CloseChannel]);
        Assert.Equal("relative_one_bar_return", RlPortfolioV2Contract.ExtraChannelNames[RlPortfolioWindowFeatures.RelativeReturnOffset]);
        Assert.Equal("cross_sectional_rank", RlPortfolioV2Contract.ExtraChannelNames[RlPortfolioWindowFeatures.RankOffset]);
        Assert.Equal(Base + RlPortfolioV2Contract.ExtraChannelNames.Length, Channels);
        Assert.Contains("(average_rank-" + RlPortfolioWindowFeatures.RankCentering.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", RlPortfolioV2Contract.FeatureConventions);
    }

    // ---- brute-force reference ----

    private static double[] ReferenceWindow(RlPanelBarsFixture bars, int symbol, int[] universe, int anchor, int blockStart)
    {
        double Close(int s, int t) { Assert.True(bars.TryGetBar(s, t, out var bar)); return (double)bar.Close; }
        var raw = new double[Window * Channels];
        for (int row = 0; row < Window; row++)
        {
            int index = anchor - Window + 1 + row;
            bars.TryGetBar(symbol, index, out var bar);
            double reference = Close(symbol, anchor);
            raw[row * Channels] = Math.Log((double)bar.Open / reference);
            raw[row * Channels + 1] = Math.Log((double)bar.High / reference);
            raw[row * Channels + 2] = Math.Log((double)bar.Low / reference);
            raw[row * Channels + 3] = Math.Log((double)bar.Close / reference);
            raw[row * Channels + 4] = Math.Log(1d + 1_000);
            double Return(int s) => index - 1 < blockStart ? 0d : Math.Log(Close(s, index) / Close(s, index - 1));
            raw[row * Channels + 5] = universe.Length == 1 ? 0d : Return(symbol) - universe.Average(Return);
            double mine = Math.Log(Close(symbol, index) / Close(symbol, anchor));
            double less = universe.Count(s => Math.Log(Close(s, index) / Close(s, anchor)) < mine);
            double equal = universe.Count(s => Math.Log(Close(s, index) / Close(s, anchor)) == mine);
            raw[row * Channels + 6] = (less + (equal + 1) / 2d - 0.5d) / universe.Length;
        }
        return raw;
    }

    private static void AssertSlot(float[] observation, int slotIndex, double[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(observation[slotIndex * Slot + i] - expected[i]) < 2e-5, $"slot {slotIndex} value {i}: {observation[slotIndex * Slot + i]} vs {expected[i]}");
    }

    // ---- hand table ----

    /// <summary>
    /// Two symbols: symbol 0 grows 1% per bar from 100, symbol 1 stays flat at 50. At the first decision (index 63 = last window bar, block start 0):
    /// row 63 returns are ln 1.01 and 0, mean ln(1.01) / 2 = 0.004975165, relative +0.004975165 and -0.004975165; the log closes over the window end are both 0 (tie),
    /// ranks (1.5 - 0.5) / 2 = 0.5 each. Row 0 has the block-start rule (previous bar before the block): relative return 0 for both; its log closes over the window
    /// end are ln(1.01^-63) = -0.626886 for symbol 0 (lowest, rank 1 -> 0.25) and 0 for symbol 1 (rank 2 -> 0.75). One decision later row 0 is index 1, whose
    /// previous bar (index 0) is inside the block: relative return is again +-0.004975165.
    /// </summary>
    [Fact]
    public void TheHandTableOfTwoSymbolsMatches()
    {
        var bars = new RlPanelBarsFixture(2, (s, t) => s == 0 ? ((decimal)Math.Pow(1.01, t) * 100m, (decimal)Math.Pow(1.01, t) * 100m) : (50m, 50m));
        using var session = Session(bars, 0, 1);
        var first = session.Reset();
        float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, first, ImmutableArray.Create(0, 1), Identity7);
        Assert.Equal(MaxSymbols * Slot + RlPortfolioContract.GlobalFeatureCount, obs.Length);
        const double half = 0.00497516542, tolerance = 2e-5;
        void Near(double expected, float actual) => Assert.InRange(actual, expected - tolerance, expected + tolerance);
        int last = 63 * Channels, firstRow = 0;
        Near(half, obs[last + 5]); Near(-half, obs[Slot + last + 5]);
        Near(0.5, obs[last + 6]); Near(0.5, obs[Slot + last + 6]);
        Near(0, obs[firstRow + 5]); Near(0, obs[Slot + firstRow + 5]);
        Near(0.25, obs[firstRow + 6]); Near(0.75, obs[Slot + firstRow + 6]);
        var second = session.Hold().Snapshot;
        float[] next = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, second, ImmutableArray.Create(0, 1), Identity7);
        Near(half, next[5]); Near(-half, next[Slot + 5]);
        // Padded slots stay exactly zero.
        Assert.All(obs.Skip(2 * Slot).Take(2 * Slot), v => Assert.Equal(0f, v));
    }

    // ---- brute-force agreement, masks, ties ----

    [Fact]
    public void TheObservationMatchesTheBruteForceDefinitionAtSeveralDecisionsWithTies()
    {
        var bars = new RlPanelBarsFixture(4, Prices);
        var all = new[] { 0, 1, 2, 3 };
        using var session = Session(bars, all);
        var snapshot = session.Reset();
        for (int step = 0; step < 50; step++)
        {
            if (step is 0 or 7 or 20 or 49)
            {
                float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, all.ToImmutableArray(), Identity7);
                foreach (int s in all) AssertSlot(obs, s, ReferenceWindow(bars, s, all, snapshot.AnchorIndex, blockStart: 0));
            }
            snapshot = session.Hold().Snapshot;
        }
    }

    [Fact]
    public void ASlotThatIsNotTradableOrHasAGapInItsWindowIsOutsideTheCrossSection()
    {
        var full = new RlPanelBarsFixture(4, Prices);
        var gap = new RlPanelBarsFixture(4, (s, t) => s == 3 && t is >= 90 and <= 95 ? null : Prices(s, t));
        using var session = Session(full, 0, 1, 2, 3);
        var snapshot = session.Reset();
        for (int i = 0; i < 40; i++) snapshot = session.Hold().Snapshot;           // anchor 103: the window 40..103 contains the gap of symbol 3
        float[] obs = RlPortfolioObservationBuilder.Build(gap, MaxSymbols, snapshot, ImmutableArray.Create(0, 1, 3), Identity7);
        var universe = new[] { 0, 1 };                                               // symbol 2 is not tradable, symbol 3 has a gap: neither enters the mean or the rank
        foreach (int s in universe) AssertSlot(obs, s, ReferenceWindow(full, s, universe, snapshot.AnchorIndex, 0));
        Assert.All(obs.Skip(2 * Slot).Take(2 * Slot), v => Assert.Equal(0f, v));    // the slots 2 and 3 are unavailable: all zero, mask 0
    }

    [Fact]
    public void AloneInTheCrossSectionTheRelativeReturnIsZeroAndTheRankIsOneHalf()
    {
        var gap = new RlPanelBarsFixture(4, (s, t) => s == 3 && t is >= 90 and <= 95 ? null : Prices(s, t));
        var full = new RlPanelBarsFixture(4, Prices);
        using var session = Session(full, 0, 1, 2, 3);
        var snapshot = session.Reset();
        for (int i = 0; i < 40; i++) snapshot = session.Hold().Snapshot;
        float[] obs = RlPortfolioObservationBuilder.Build(gap, MaxSymbols, snapshot, ImmutableArray.Create(0, 3), Identity7);
        for (int row = 0; row < Window; row++)
        {
            Assert.Equal(0f, obs[row * Channels + 5]);
            Assert.Equal(0.5f, obs[row * Channels + 6]);
        }
    }

    [Fact]
    public void TheFirstFiveChannelsOfAV2ObservationAreTheV1ObservationAndTheGlobalValuesAreUnchanged()
    {
        var bars = new RlPanelBarsFixture(4, Prices);
        var all = new[] { 0, 1, 2, 3 };
        using var session = Session(bars, all);
        var snapshot = session.Reset();
        var v1 = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, all.ToImmutableArray(), Identity5);
        var v2 = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, all.ToImmutableArray(), Identity7);
        int v1Slot = Window * Base + 2;
        foreach (int s in all)
        {
            for (int row = 0; row < Window; row++)
                for (int c = 0; c < Base; c++) Assert.Equal(v1[s * v1Slot + row * Base + c], v2[s * Slot + row * Channels + c]);
            Assert.Equal(v1[s * v1Slot + Window * Base], v2[s * Slot + Window * Channels]);            // weight
            Assert.Equal(v1[s * v1Slot + Window * Base + 1], v2[s * Slot + Window * Channels + 1]);    // mask
        }
        Assert.Equal(v1.Skip(MaxSymbols * v1Slot).ToArray(), v2.Skip(MaxSymbols * Slot).ToArray());
    }

    [Fact]
    public void NoBarAfterTheDecisionIndexIsReadAndAChangedFutureChangesNothing()
    {
        var bars = new RlPanelBarsFixture(4, Prices);
        var changedFuture = new RlPanelBarsFixture(4, (s, t) => t > 120 ? (7m + s, 9m + s) : Prices(s, t));
        var all = new[] { 0, 1, 2, 3 };
        using var session = Session(bars, all);
        var snapshot = session.Reset();
        for (int i = 0; i < 57; i++) snapshot = session.Hold().Snapshot;           // anchor 120
        bars.Reads.Clear();
        float[] one = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, all.ToImmutableArray(), Identity7);
        Assert.True(bars.Reads.Count > 0);
        Assert.All(bars.Reads, read => Assert.True(read.Index <= snapshot.AnchorIndex));
        Assert.Equal(one, RlPortfolioObservationBuilder.Build(changedFuture, MaxSymbols, snapshot, all.ToImmutableArray(), Identity7));
    }

    [Fact]
    public void TheCrossSectionOfADecisionDoesNotDependOnTheOrderOfTheTradableList()
    {
        var bars = new RlPanelBarsFixture(4, Prices);
        using var session = Session(bars, 0, 1, 2, 3);
        var snapshot = session.Reset();
        float[] ordered = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, ImmutableArray.Create(0, 1, 2, 3), Identity7);
        float[] shuffled = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, snapshot, ImmutableArray.Create(3, 1, 0, 2), Identity7);
        Assert.Equal(ordered, shuffled);
    }

    // ---- scaler fit ----

    private static RlPanelCatalog Catalog() => RlPanelCatalogBuilder.Build(1278,
        Enumerable.Range(0, 3).Select(_ => (ReadOnlyMemory<int>)Enumerable.Range(0, 1279).ToArray()).ToArray(),
        ImmutableArray.Create(false, false, false), RlPanelEvaluationMode.ShortHistory);

    [Fact]
    public void TheV2FitMatchesAnIndependentTwoPassComputationOverEveryTrainingDecision()
    {
        var bars = new RlPanelBarsFixture(3, Prices);
        var catalog = Catalog();
        var scaler = RlPortfolioScaler.Fit(bars, catalog, RlPortfolioObservationLayout.V2A);
        Assert.Equal(RlPortfolioV2Contract.ContractHash, scaler.FeatureContractHash);
        Assert.Equal(7, scaler.Statistics.Length);
        var values = new List<double>[Channels];
        for (int c = 0; c < Channels; c++) values[c] = new List<double>();
        foreach (var block in catalog.CellBlocks(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train))
        {
            var universe = catalog.EligibleIn(block, RlPanelSymbolGroup.Train).ToArray();
            for (int anchor = block.StartIndex + Window - 1; anchor < block.EndExclusive - 1; anchor++)
                foreach (int symbol in universe)
                {
                    double[] raw = ReferenceWindow(bars, symbol, universe, anchor, block.StartIndex);
                    for (int i = 0; i < raw.Length; i++) values[i % Channels].Add(raw[i]);
                }
        }
        for (int c = 0; c < Channels; c++)
        {
            double mean = values[c].Average();
            double std = Math.Sqrt(values[c].Sum(v => (v - mean) * (v - mean)) / values[c].Count);
            Assert.Equal(mean, scaler.Statistics[c].Mean, 9);
            Assert.Equal(std, scaler.Statistics[c].Std, 9);
            Assert.Equal(values[c].Min(), scaler.Statistics[c].Min, 12);
            Assert.Equal(values[c].Max(), scaler.Statistics[c].Max, 12);
        }
        Assert.InRange(scaler.Statistics[6].Mean, 0.4, 0.6);     // ranks are centred on one half
        Assert.InRange(Math.Abs(scaler.Statistics[5].Mean), 0d, 1e-9);   // a return minus its cross-sectional mean averages to zero
    }

    // ---- host and job contract ----

    [Fact]
    public void TheHostSpeaksTheContractHashOfTheScalerAndRejectsTheOtherVariant()
    {
        var run = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var catalog = RlPanelCatalogBuilder.Build(1278,
            Enumerable.Range(0, 2).Select(_ => (ReadOnlyMemory<int>)Enumerable.Range(0, 1279).ToArray()).ToArray(),
            ImmutableArray.Create(false, false), RlPanelEvaluationMode.ShortHistory);
        var bars = new RlPanelBarsFixture(2, Prices);
        long sequence = 0;
        JsonElement Request(string hash) => JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["protocol_version"] = 1, ["run_id"] = run.ToString("N"), ["contract_hash"] = hash, ["sequence"] = ++sequence,
            ["session_id"] = "", ["method"] = "Ready", ["payload"] = new { },
        })).RootElement.Clone();
        var v1Host = new RlPortfolioIpcHost(run, bars, catalog, Identity5, 3, Costs(), 100);
        Assert.Throws<InvalidDataException>(() => v1Host.Handle(Request(RlPortfolioV2Contract.ContractHash), default));
        sequence = 0;
        var v2Host = new RlPortfolioIpcHost(run, bars, catalog, Identity7, 3, Costs(), 100);
        Assert.Throws<InvalidDataException>(() => v2Host.Handle(Request(RlPortfolioContract.ContractHash), default));
        sequence = 0;
        var reply = JsonSerializer.SerializeToElement(v2Host.Handle(Request(RlPortfolioV2Contract.ContractHash), default), RlFiles.Json);
        Assert.Equal(RlPortfolioV2Contract.ContractHash, reply.GetProperty("contract_hash").GetString());
        Assert.Throws<ArgumentException>(() => new RlPortfolioIpcHost(run, bars, catalog, Identity5 with { FeatureContractHash = "00" }, 3, Costs(), 100));
    }
}
