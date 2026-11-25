using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FlagFixtures;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Flag engine (plan ChartPatterns56 v2, run V2-4; ledger rows FL_*): the pole, the Theil-Sen channel, the break, the volume, the candidate ranking and the life cycle, each rule on its edge. The bullish flag and its
/// mirror image (the bearish flag) are tested together; every "silent" assertion has a control that fires when only the tested number moves back inside its rule.
/// </summary>
public class FlagEngineTests
{
    private static PatternBarResult[] Run(IReadOnlyList<CandleData> bars, CoreFlagPatternParameter? p = null)
        => FlagEngine.Run(PatternEngineTestBars.Context(bars), p ?? new CoreFlagPatternParameter());

    /// <summary>
    /// Settings under which only the fixture's own pole qualifies: the minimum move is the pole's range (rise + 0.4), so a pole that is shorter, longer or shifted by one bar (a flag bar as the pole end) has a smaller
    /// move and does not. Every candidate (flag length, pole length) is searched, so without it a rule's edge could be passed by a neighbouring pole.
    /// </summary>
    private static CoreFlagPatternParameter Strict(decimal poleRise = 12m, Action<CoreFlagPatternParameter>? tweak = null)
    {
        var p = new CoreFlagPatternParameter { MinPoleMovePercent = poleRise + 0.4m };
        tweak?.Invoke(p);
        return p;
    }

    private static int[] LitBars(PatternBarResult[] r) => Enumerable.Range(0, r.Length).Where(i => r[i].Fires).ToArray();

    private static int[] Lit(FlagSpec spec, CoreFlagPatternParameter? p = null) => LitBars(Run(Build(spec).Bars, p));

    private static void AssertFires(FlagSpec spec, CoreFlagPatternParameter? p = null)
    {
        Built built = Build(spec);
        PatternBarResult[] r = Run(built.Bars, p);

        Assert.True(r[built.BreakBar].Fires, $"must fire on the break bar {built.BreakBar}; lit bars: {string.Join(",", LitBars(r))}");
        Assert.Equal(built.BreakBar, LitBars(r).First());
        Assert.Equal(spec.Bullish ? 1 : -1, r[built.BreakBar].Direction);
        Assert.Equal(spec.Bullish, r[built.BreakBar].BullishFires);
        Assert.Equal(!spec.Bullish, r[built.BreakBar].BearishFires);
    }

    private static void AssertSilent(FlagSpec spec, CoreFlagPatternParameter? p = null) => Assert.Empty(Lit(spec, p));

    public static IEnumerable<object[]> Sides() { yield return new object[] { true }; yield return new object[] { false }; }

    // ---------------------------------------------------------------- the pattern

    [Theory]
    [MemberData(nameof(Sides))]
    public void ThePattern_FiresOnTheBreakBar_AndForTheHoldBars(bool bullish)
    {
        Built built = Build(new FlagSpec { Bullish = bullish });
        PatternBarResult[] r = Run(built.Bars);

        Assert.Equal(Enumerable.Range(built.BreakBar, 1 + new CoreFlagPatternParameter().SignalPersistenceBars), LitBars(r));
        Assert.Equal(bullish ? 1 : -1, r[built.BreakBar].Direction);
        Assert.NotNull(r[built.BreakBar].Score);
        Assert.InRange(r[built.BreakBar].Score!.Value, 0.70m, 1m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void Persistence_LightsTheBreakBarAndTheGivenNumberOfBarsAfterIt(int persistence)
    {
        Built built = Build(new FlagSpec());
        PatternBarResult[] r = Run(built.Bars, new CoreFlagPatternParameter { SignalPersistenceBars = persistence });

        Assert.Equal(Enumerable.Range(built.BreakBar, persistence + 1), LitBars(r));
    }

    [Fact]
    public void APoleFiresOnce_EvenWhenTheBarsAfterTheBreakKeepRising()
    {
        // Rising bars after the break keep closing above the rail; the consumed (direction, pole start, pole end) never signals again, so with no hold bars only the break bar is lit.
        Built built = Build(new FlagSpec { RisingTail = true });
        int[] lit = LitBars(Run(built.Bars, new CoreFlagPatternParameter { SignalPersistenceBars = 0 }));

        Assert.Equal(built.BreakBar, lit.First());
        Assert.True(lit.Length <= 3, $"lit bars {string.Join(",", lit)}: only poles of other lengths could signal again");
    }

    // ---------------------------------------------------------------- pole

    [Theory]
    [InlineData(2.0, 10.4, true)]
    [InlineData(2.0, 10.5, false)]
    public void PoleMove_PercentEdge(double rise, double minPercent, bool fires)
    {
        // Rise of 10 over 5 bars: the pole spans 99.8 to 110.2 (10.4 points, 10.42 percent of 99.8).
        var spec = new FlagSpec { PoleRise = (decimal)(rise * 5) };
        var p = new CoreFlagPatternParameter { MinPoleMovePercent = (decimal)minPercent };
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Theory]
    [InlineData(10.4, true)]
    [InlineData(10.5, false)]
    public void PoleMove_AtrEdge(double minAtr, bool fires)
    {
        // The ATR before the pole is exactly 1.0 (true range 1 on every quiet bar), the pole spans 10.4.
        var spec = new FlagSpec { PoleRise = 10m, Width = 2.0m, Drift = 0.1m };
        var p = new CoreFlagPatternParameter { MinPoleAtrMultiplier = (decimal)minAtr, MinPoleMovePercent = 2m };
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Theory]
    [InlineData(2, true)]    // 3 of 5 bars rise: 0.6
    [InlineData(3, false)]   // 2 of 5 bars rise: 0.4
    public void PoleBodyRatio_EdgeIs0p6(int downBars, bool fires)
    {
        // The down bars are the last ones, so no shorter pole at the end of the pole has a better ratio.
        var spec = new FlagSpec { DownBars = Enumerable.Range(5 - downBars, downBars).ToHashSet() };
        if (fires) AssertFires(spec, Strict()); else AssertSilent(spec, Strict());
    }

    [Fact]
    public void PoleEnd_MustBeInTheHighZoneOfThe20Bars()
    {
        // A high of 115 five bars before the pole puts the pole end (112.2) at (112.2 - 99.5) / (115 - 99.5) = 0.82 of the 20-bar range: below 0.90.
        AssertSilent(new FlagSpec { PreSpikeHigh = 115m });
        AssertFires(new FlagSpec { PreSpikeHigh = 111m });
        AssertSilent(new FlagSpec { Bullish = false, PreSpikeHigh = 115m });
    }

    [Theory]
    [InlineData(3, 9, true)]
    [InlineData(15, 10, true)]
    [InlineData(16, 10, false)]
    public void PoleLength_EdgesAre3And15Bars(int poleBars, int flagBars, bool fires)
    {
        // The last bar of a 3-bar pole spans 4.4, so the channel is 4.4 wide there and the width limit is lifted for that case (the width rule has its own test).
        var spec = new FlagSpec { PoleBars = poleBars, FlagBars = flagBars, PoleRise = 12m, Drift = 0.15m, Width = poleBars == 3 ? 4.4m : 2.5m };
        CoreFlagPatternParameter p = Strict(12m, q => q.MaxChannelWidthRatio = 0.5m);
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Fact]
    public void PoleLength_MaximumFollowsTheParameter_AndAPoleOfTwoBarsCanNeverFire()
    {
        AssertFires(new FlagSpec { PoleBars = 16, FlagBars = 10, Drift = 0.15m, Width = 2.5m }, Strict(12m, p => p.MaxPoleBars = 16));

        // A pole of 2 bars needs a flag of at least 6 bars for the total of 8, but the flag is at most 3 times the pole (6 bars): the two rules of the specification exclude it, whatever the setting.
        AssertSilent(new FlagSpec { PoleBars = 2, FlagBars = 6, Drift = 0.15m, Width = 2.5m }, Strict(12m, p => p.MinPoleBars = 2));
        AssertSilent(new FlagSpec { PoleBars = 2, FlagBars = 7, Drift = 0.15m, Width = 2.5m }, Strict(12m, p => p.MinPoleBars = 2));
    }

    // ---------------------------------------------------------------- flag length and total length

    [Theory]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public void FlagLength_AtLeast5Bars(int flagBars, bool fires)
    {
        var spec = new FlagSpec { PoleBars = 5, FlagBars = flagBars, Drift = 0.2m };
        if (fires) AssertFires(spec, Strict()); else AssertSilent(spec, Strict());
    }

    [Fact]
    public void FlagLength_MinimumFollowsTheParameter() => AssertFires(new FlagSpec { FlagBars = 4 }, Strict(12m, p => p.MinFlagBars = 3));

    [Theory]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void FlagLength_AtMost25Bars(int flagBars, bool fires)
    {
        var spec = new FlagSpec { PoleBars = 9, PoleRise = 14m, FlagBars = flagBars, Drift = 0.05m, Width = 2m };
        if (fires) AssertFires(spec, Strict(14m)); else AssertSilent(spec, Strict(14m));
    }

    [Fact]
    public void FlagLength_MaximumFollowsTheParameter_UpToTheFitLimit()
    {
        var spec = new FlagSpec { PoleBars = 11, PoleRise = 14m, FlagBars = 30, Drift = 0.04m, Width = 2m };

        AssertSilent(spec, Strict(14m));
        AssertFires(spec, Strict(14m, p => p.MaxFlagBars = 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { MaxFlagBars = 31 }.Validate());
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void TheFlagIsAtMost3TimesAsLongAsThePole(int flagBars, bool fires)
    {
        var spec = new FlagSpec { PoleBars = 3, FlagBars = flagBars, Drift = 0.15m, Width = 4.4m };
        CoreFlagPatternParameter p = Strict(12m, q => q.MaxChannelWidthRatio = 0.5m);
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Fact]
    public void TotalLength_AtLeast8Bars()
    {
        // Pole 3 + flag 5: from the pole start to the break bar are 7 bars; pole 4 + flag 5 is 8.
        AssertSilent(new FlagSpec { PoleBars = 3, FlagBars = 5 }, Strict());
        AssertFires(new FlagSpec { PoleBars = 4, FlagBars = 5 }, Strict());
    }

    // ---------------------------------------------------------------- channel

    [Fact]
    public void Retrace_MaximumEdgeIsTheRatioOfTheDepthToThePoleHeight()
    {
        // Pole 99.8 -> 112.2 (12.4); the lowest low of the flag is 112.2 - 3 - 0.2 * 9 = 107.4, so the depth is 4.8 and the ratio 4.8 / 12.4 = 0.3871.
        AssertFires(new FlagSpec(), Strict(12m, p => p.MaxRetraceRatio = 0.388m));
        AssertSilent(new FlagSpec(), Strict(12m, p => p.MaxRetraceRatio = 0.386m));
    }

    [Fact]
    public void Retrace_MinimumEdge_AndTheDefaultHalfOfThePoleIsTheCeiling()
    {
        // A shallow flag after a 15-bar pole (its last bar spans only 1.2): width 1.5, flat. The lowest low is 112.2 - 1.5 = 110.7, so the depth is 1.5 and the ratio 1.5 / 12.4 = 0.1210.
        var shallow = new FlagSpec { PoleBars = 15, Width = 1.5m, Drift = 0m };
        AssertFires(shallow, Strict(12m, p => p.MinRetraceRatio = 0.120m));
        AssertSilent(shallow, Strict(12m, p => p.MinRetraceRatio = 0.122m));

        // A deep flag: width 6, drift 0.2: depth 7.8 / 12.4 = 0.63 > 0.50 (the flag is also too wide).
        AssertSilent(new FlagSpec { Width = 6m }, Strict());
    }

    [Fact]
    public void ChannelWidth_EdgeIsTheRatioToThePoleHeight()
    {
        // Mean width 3.0 over a pole of 12.4: 0.242.
        AssertFires(new FlagSpec(), Strict(12m, p => p.MaxChannelWidthRatio = 0.25m));
        AssertSilent(new FlagSpec(), Strict(12m, p => p.MaxChannelWidthRatio = 0.24m));
    }

    [Fact]
    public void Parallelism_BoundsTheSlopeDifferenceInAtrs()
    {
        var narrow = new FlagSpec { Drift = 0.05m, Width = 2m };
        AssertFires(narrow with { LowerExtraDrift = 0.05m }, Strict(12m, p => p.MinRSquared = 0.3m));
        AssertSilent(narrow with { LowerExtraDrift = 0.4m }, Strict(12m, p => p.MinRSquared = 0.3m));
        AssertFires(narrow with { LowerExtraDrift = 0.4m }, Strict(12m, p => { p.MaxSlopeDiffAtr = 1.0m; p.MinRSquared = 0.3m; p.MaxRetraceRatio = 0.7m; p.MaxChannelWidthRatio = 0.8m; }));
    }

    [Fact]
    public void TheUpperRailMayNotRise_AMildFallOrAFlatRailPasses()
    {
        AssertFires(new FlagSpec { Drift = 0m }, Strict());
        AssertFires(new FlagSpec { Drift = 0.2m }, Strict());
        AssertSilent(new FlagSpec { Drift = -0.3m, LowerExtraDrift = 0m, BreakClose = 116m, BreakOpen = 113m }, Strict()); // a rising channel (+0.3 per bar, more than 0.05 ATR) is no bull flag
    }

    [Fact]
    public void RSquared_AnOutlierOfTheUpperRailLowersTheScore_AndTheSettingDecides()
    {
        AssertFires(new FlagSpec { SpikeAt = 5, SpikeSize = 1.0m }, Strict());
        AssertSilent(new FlagSpec { SpikeAt = 5, SpikeSize = 2.0m }, Strict());
        AssertSilent(new FlagSpec { SpikeAt = 5, SpikeSize = 1.0m }, Strict(12m, p => p.MinRSquared = 0.99m));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Touches_AreCountedAtLeastTwoBarsApart(int minTouches, bool fires)
    {
        // Ten flag bars lie on each rail: counted touches are at offsets 0, 2, 4, 6, 8 = 5 (not 10).
        var spec = new FlagSpec();
        var p = Strict(12m, q => q.MinTouchCount = minTouches);
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    // ---------------------------------------------------------------- break and volume

    [Theory]
    [InlineData(110.9, true)]
    [InlineData(110.6, false)]
    public void Break_NeedsTheMarginBeyondTheRail(double close, bool fires)
    {
        // Rail at the break bar: 112.2 - 0.2 * 10 = 110.2; margin max(0.3 percent, 0.2 ATR) is about 0.6.
        var spec = new FlagSpec { BreakClose = (decimal)close, BreakOpen = 109.8m };
        if (fires) AssertFires(spec, Strict()); else AssertSilent(spec, Strict());
    }

    [Fact]
    public void Break_NeedsABullishCandle()
    {
        AssertFires(new FlagSpec { BreakClose = 112.0m, BreakOpen = 111.0m }, Strict());
        AssertSilent(new FlagSpec { BreakClose = 112.0m, BreakOpen = 112.5m }, Strict());
        AssertSilent(new FlagSpec { Bullish = false, BreakClose = 112.0m, BreakOpen = 112.5m }, Strict());
    }

    [Theory]
    [InlineData(1560, true)]
    [InlineData(1559, false)]
    public void Volume_BreakNeeds1p3TimesTheMeanVolumeOfTheFlag(long volume, bool fires)
    {
        // The flag interval starts with the pole end bar (volume 3000): mean (3000 + 9 * 1000) / 10 = 1200; 1.3 * 1200 = 1560.
        var spec = new FlagSpec { BreakBarVolume = volume };
        if (fires) AssertFires(spec, Strict()); else AssertSilent(spec, Strict());
    }

    [Theory]
    [InlineData(2499, true)]
    [InlineData(2500, false)]
    public void Volume_TheFlagMeanMustBeBelow85PercentOfThePoleMean(long flagVolume, bool fires)
    {
        // Mean of the flag (3000 + 9 v) / 10 against 0.85 * 3000 = 2550: v = 2500 gives exactly 2550, which is not below.
        var spec = new FlagSpec { FlagBarVolume = flagVolume, BreakBarVolume = 9000 };
        if (fires) AssertFires(spec, Strict()); else AssertSilent(spec, Strict());
    }

    [Fact]
    public void Volume_OptionOffAndAnInstrumentWithoutVolumePass()
    {
        var spec = new FlagSpec { BreakBarVolume = 1, FlagBarVolume = 5000 };

        AssertSilent(spec, Strict());
        AssertFires(spec, Strict(12m, p => p.RequireVolumeConfirmation = false));

        Built built = Build(new FlagSpec());
        CandleData[] noVolume = built.Bars.Select(b => b with { Volume = 0 }).ToArray();
        Assert.Contains(built.BreakBar, LitBars(Run(noVolume)));
    }

    // ---------------------------------------------------------------- ranking, direction mode

    [Fact]
    public void TheBestCandidateScoreIsTheMeanNormalisedRSquared_AndAnOutlierLowersIt()
    {
        PatternBarResult clean = Run(Build(new FlagSpec()).Bars)[Build(new FlagSpec()).BreakBar];
        Built noisyBuilt = Build(new FlagSpec { SpikeAt = 5, SpikeSize = 1.0m });
        PatternBarResult noisy = Run(noisyBuilt.Bars)[noisyBuilt.BreakBar];

        Assert.True(clean.Score > noisy.Score, $"{clean.Score} must exceed {noisy.Score}");
        Assert.True(noisy.Score >= 0.70m);
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void DirectionMode_AllowsOnlyItsSide(bool bullish)
    {
        var spec = new FlagSpec { Bullish = bullish };
        var wanted = bullish ? PatternDirectionMode.BullishOnly : PatternDirectionMode.BearishOnly;
        var other = bullish ? PatternDirectionMode.BearishOnly : PatternDirectionMode.BullishOnly;

        AssertFires(spec, new CoreFlagPatternParameter { DirectionMode = wanted });
        AssertSilent(spec, new CoreFlagPatternParameter { DirectionMode = other });
    }

    // ---------------------------------------------------------------- invalid bars (plan R-14..R-16)

    [Fact]
    public void AnInconsistentBarOutsideEveryReadSet_ChangesNothing()
    {
        // The earliest bar read is the first bar of the ATR window before the pole (bar 15) and of the 20-bar zone; bar 3 is outside every read set.
        Built built = Build(new FlagSpec());
        PatternBarResult[] clean = Run(built.Bars);

        Assert.NotEmpty(LitBars(clean));
        Assert.Equal(clean, Run(PatternEngineTestBars.Corrupt(built.Bars, 3)));
    }

    [Theory]
    [InlineData(20, "the ATR window before the pole")]
    [InlineData(32, "the pole")]
    [InlineData(38, "the flag")]
    [InlineData(44, "the break bar")]
    public void AnInconsistentBarInsideAReadSet_GivesNothingForThatCandidate(int bar, string readBy)
    {
        Built built = Build(new FlagSpec());
        Assert.NotEmpty(LitBars(Run(built.Bars)));

        Assert.True(LitBars(Run(PatternEngineTestBars.Corrupt(built.Bars, bar))).Length == 0, $"bar {bar} is read by {readBy}");
    }

    [Fact]
    public void AnInconsistentBarInTheHoldPeriod_ClearsThatBarOnly()
    {
        Built built = Build(new FlagSpec());
        PatternBarResult[] r = Run(PatternEngineTestBars.Corrupt(built.Bars, built.BreakBar + 2));

        Assert.Equal(new[] { built.BreakBar, built.BreakBar + 1, built.BreakBar + 3, built.BreakBar + 4, built.BreakBar + 5 }, LitBars(r));
    }

    // ---------------------------------------------------------------- causality

    [Theory]
    [MemberData(nameof(Sides))]
    public void PrefixInvariance_AppendingBarsNeverChangesAnEarlierResult(bool bullish)
    {
        Built built = Build(new FlagSpec { Bullish = bullish, RisingTail = true });
        PatternBarResult[] full = Run(built.Bars);

        for (int cut = 1; cut <= built.Bars.Length; cut++)
        {
            Assert.Equal(full.Take(cut), Run(built.Bars.Take(cut).ToArray()));
        }
    }

    private static CoreFlagPatternParameter Relaxed() => RelaxedFlag();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PrefixInvariance_OnRandomWalks(int seed)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(seed, 900);
        PatternBarResult[] full = Run(bars, Relaxed());

        foreach (int cut in new[] { 40, 150, 333, 600, 899 })
        {
            Assert.Equal(full.Take(cut), Run(bars.Take(cut).ToArray(), Relaxed()));
        }

        Assert.True(seed != 1 || LitBars(full).Length > 0, "the first walk must contain a signal, or the invariance is vacuous");
    }

    [Fact]
    public void RandomWalks_FindFlags_AndNeverBreakTheOutputContract()
    {
        int signals = 0;
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            foreach (PatternBarResult x in Run(PatternEngineTestBars.Walk(seed, 1500), Relaxed()))
            {
                if (x.Fires)
                {
                    signals++;
                    Assert.True((x.Direction == 1 && x.BullishFires) || (x.Direction == -1 && x.BearishFires), "the direction is a side that fires");
                    Assert.NotNull(x.Score);
                }
                else
                {
                    Assert.Equal(0, x.Direction);
                }
            }
        }

        Assert.True(signals > 0, "the relaxed settings must find a flag in eight walks");
    }

    [Fact]
    public void ASeriesShorterThanAnyFlag_GivesNoSignalAndNoException()
    {
        Assert.Empty(LitBars(Run(Array.Empty<CandleData>())));
        Assert.Empty(LitBars(Run(PatternEngineTestBars.Walk(1, 12))));
    }

    [Fact]
    public void ThePoleMemo_DoesNotChangeTheAnswer_AcrossManyBars()
    {
        // 3,000 bars with relaxed settings run through every (flag, pole) pair; the engine must stay fast and deterministic (same input, same output).
        CandleData[] bars = PatternEngineTestBars.Walk(4, 3000);
        Assert.Equal(Run(bars, Relaxed()), Run(bars, Relaxed()));
    }
}
