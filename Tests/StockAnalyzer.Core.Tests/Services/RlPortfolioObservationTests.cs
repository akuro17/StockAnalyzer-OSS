using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Hand table of the portfolio observation. Fixture: symbol 0 trades at 100 (open = close), symbol 1 at 50, volume 1000; bars derive High =
/// max(open, close) + 1 and Low = min(open, close) - 1. Symbol 0 has Open(63) = 110. With an identity scaler (mean 0, std 1) every value is the raw
/// log ratio, so the expected numbers below are plain logarithms (constants from an independent computation): ln 1.1 = 0.09531018, ln 1.11 =
/// 0.10436002, ln 1.01 = 0.00995033, ln 0.99 = -0.01005034, ln 1001 = 6.90875478, ln 1.02 = 0.01980263, ln 0.98 = -0.02020271.
/// </summary>
public sealed class RlPortfolioObservationTests
{
    private const float Tolerance = 1e-6f;
    private const int Slot = 322, MaxSymbols = 3, Window = 64;
    private static readonly FixedScaler Identity = RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -100d, 100d), 5).ToArray());

    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });

    private static RlPanelBarsFixture Bars() => RlPanelBarsFixture.Flat(new[] { 100m, 50m },
        new System.Collections.Generic.Dictionary<(int, int), (decimal, decimal)> { [(0, 63)] = (110m, 100m) });

    private static (RlPanelBarsFixture Bars, RlPortfolioSession Session, RlPortfolioSnapshot Start) Start()
    {
        var bars = Bars();
        var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs());
        return (bars, session, session.Reset());
    }

    private static void Near(double expected, float actual) => Assert.InRange(actual, (float)expected - Tolerance, (float)expected + Tolerance);

    [Fact]
    public void FirstDecisionLayoutMatchesTheHandTable()
    {
        var (bars, session, start) = Start();
        using var owner = session;
        float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity);
        Assert.Equal(970, obs.Length);
        // Slot 0, oldest bar (row 0): open = close = 100 -> 0, high 101 -> ln 1.01, low 99 -> ln 0.99, close 0, volume ln 1001.
        Near(0, obs[0]); Near(0.00995033085, obs[1]); Near(-0.01005033585, obs[2]); Near(0, obs[3]); Near(6.90875477931522, obs[4]);
        // Slot 0, newest bar (row 63): open 110 -> ln 1.1, high 111 -> ln 1.11, low 99 (min(110,100) - 1), close 0.
        int last = 63 * 5;
        Near(0.0953101798, obs[last]); Near(0.10436001532, obs[last + 1]); Near(-0.01005033585, obs[last + 2]); Near(0, obs[last + 3]); Near(6.90875477931522, obs[last + 4]);
        Assert.Equal((0f, 1f), (obs[320], obs[321])); // current weight 0, available
        // Slot 1 at 50: high 51 -> ln 1.02, low 49 -> ln 0.98.
        Near(0, obs[Slot]); Near(0.01980262729, obs[Slot + 1]); Near(-0.02020270731, obs[Slot + 2]);
        Assert.Equal((0f, 1f), (obs[Slot + 320], obs[Slot + 321]));
        // Padded slot 2 and every value of it is exactly zero.
        Assert.All(obs.Skip(2 * Slot).Take(Slot), v => Assert.Equal(0f, v));
        // Global: cash weight 1, drawdown 0, remaining 128/128, previous turnover 0.
        Assert.Equal(new[] { 1f, 0f, 1f, 0f }, obs.Skip(MaxSymbols * Slot).ToArray());
    }

    [Fact]
    public void AfterADecisionTheWindowSlidesAndTheAccountFieldsFollow()
    {
        var (bars, session, _) = Start();
        using var owner = session;
        var step = session.Step(ImmutableArray.Create(0.5m, 0m)); // 50 shares of symbol 0 at the open 100; E stays 10000
        float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, step.Snapshot, ImmutableArray.Create(0, 1), Identity);
        // The Open(63) = 110 bar now sits one row earlier (anchor 64: rows are indices 1..64, index 63 is row 62).
        Near(0.0953101798, obs[62 * 5]);
        Near(0, obs[63 * 5]);
        Near(0.5, obs[320]); Assert.Equal(1f, obs[321]);   // weight of symbol 0 = 5000 / 10000
        Assert.Equal(0f, obs[Slot + 320]);                   // symbol 1 is not held
        float[] global = obs.Skip(MaxSymbols * Slot).ToArray();
        Assert.Equal(new[] { 0.5f, 0f, 0.9921875f, 0.5f }, global); // cash 0.5, drawdown 0, 127/128, turnover 5000 / 10000
    }

    [Fact]
    public void AMaskedSlotIsZeroAndItsBarsAreNeverRead()
    {
        var (bars, session, start) = Start();
        using var owner = session;
        bars.Reads.Clear();
        float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0), Identity);
        Assert.All(obs.Skip(Slot).Take(Slot), v => Assert.Equal(0f, v));
        Assert.All(bars.Reads, r => Assert.Equal(0, r.Symbol));
    }

    [Fact]
    public void TheMaskUsesPastBarsOnly()
    {
        var (_, session, start) = Start();
        using var owner = session;
        // Symbol 1 lacks index 40, inside the window 0..63 of the first decision: it is unavailable (mask 0, all zeros) although tradable.
        var gap = new RlPanelBarsFixture(2, (s, t) => s == 1 && t == 40 ? null : (s == 0 ? 100m : 50m, s == 0 ? 100m : 50m));
        float[] obs = RlPortfolioObservationBuilder.Build(gap, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity);
        Assert.All(obs.Skip(Slot).Take(Slot), v => Assert.Equal(0f, v));
        Assert.Equal(1f, obs[321]);
        // A bar missing AFTER the decision index never matters: the future is not read.
        var future = new RlPanelBarsFixture(2, (s, t) => t > 63 ? null : (s == 0 ? 100m : 50m, s == 0 ? 100m : 50m));
        float[] ok = RlPortfolioObservationBuilder.Build(future, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity);
        Assert.Equal(1f, ok[Slot + 321]);
        Assert.All(future.Reads, r => Assert.InRange(r.Index, 0, 63));
    }

    [Fact]
    public void OnlyTheWindowEndingAtTheDecisionIsRead()
    {
        var (bars, session, start) = Start();
        using var owner = session;
        bars.Reads.Clear();
        _ = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity);
        Assert.All(bars.Reads, r => Assert.InRange(r.Index, 0, 63));
        Assert.Equal(2 * (Window + 1), bars.Reads.Count); // the window-end close plus the 64 window bars, per tradable symbol
    }

    [Fact]
    public void NonFiniteMarketValuesAreRejected()
    {
        var (_, session, start) = Start();
        using var owner = session;
        var bad = new NegativeVolume();
        Assert.Throws<InvalidDataException>(() => RlPortfolioObservationBuilder.Build(bad, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity));
    }

    [Fact]
    public void InconsistentInputsAreRejected()
    {
        var (bars, session, start) = Start();
        using var owner = session;
        Assert.Throws<ArgumentException>(() => RlPortfolioObservationBuilder.Build(bars, 1, start, ImmutableArray.Create(0), Identity));
        Assert.Throws<ArgumentException>(() => RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0, 2), Identity));
        Assert.Throws<ArgumentException>(() => RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, default, Identity));
        Assert.Throws<ArgumentException>(() => RlPortfolioObservationBuilder.Build(RlPanelBarsFixture.Flat(new[] { 1m, 2m, 3m }), 2, start, ImmutableArray.Create(0), Identity));
        Assert.Throws<ArgumentException>(() => RlPortfolioObservationBuilder.Build(RlPanelBarsFixture.Flat(new[] { 1m, 2m, 3m }), 3, start, ImmutableArray.Create(0), Identity));
        Assert.Throws<InvalidDataException>(() => RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0, 1), Identity with { WindowSize = 32 }));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioObservationBuilder.Build(null!, MaxSymbols, start, ImmutableArray.Create(0), Identity));
    }

    [Fact]
    public void SlotMaskMatchesTheTradableSymbolsAndLeavesPaddingFalse()
        => Assert.Equal(new[] { true, false, false }, RlPortfolioObservationBuilder.SlotMask(2, 3, ImmutableArray.Create(0)));

    [Fact]
    public void TheScalerIsAppliedPerChannel()
    {
        var (bars, session, start) = Start();
        using var owner = session;
        // Channel 4 (volume): mean 6.9, std 0.5 -> (ln 1001 - 6.9) / 0.5; channel 0 with std 0 collapses to zero.
        var stats = new[]
        {
            new FixedScalerStatistic(0d, 0d, 0d, 0d), new FixedScalerStatistic(0d, 1d, -1d, 1d), new FixedScalerStatistic(0d, 1d, -1d, 1d),
            new FixedScalerStatistic(0d, 1d, -1d, 1d), new FixedScalerStatistic(6.9d, 0.5d, 0d, 10d),
        };
        float[] obs = RlPortfolioObservationBuilder.Build(bars, MaxSymbols, start, ImmutableArray.Create(0, 1), RlPortfolioScaler.Create(stats));
        Near((6.90875477931522 - 6.9) / 0.5, obs[4]);
        Assert.Equal(0f, obs[63 * 5]); // ln 1.1 scaled by a zero-variance channel
    }

    private sealed class NegativeVolume : IRlPanelBars
    {
        public int SymbolCount => 2;
        public bool TryGetBar(int symbolIndex, int calendarIndex, out CandleData bar)
        {
            bar = new CandleData(DateTime.UnixEpoch, 100m, 101m, 99m, 100m, -5);
            return true;
        }
    }
}
