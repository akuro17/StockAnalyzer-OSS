using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FormationFixtures;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Head and Shoulders engine (plan ChartPatterns56 v2, run V2-3; ledger rows HS_*): every rule of <c>01_head_and_shoulders.md</c> on its edge, the life cycle and the causality invariants. The fixtures are real bars
/// with known pivot prices (<see cref="FormationFixtures"/>); the top and its mirror image (the inverse) are tested together. Each "silent" assertion has a control that fires when only the tested number is
/// moved back inside its rule, so no test is silent for another reason.
/// </summary>
public class HeadAndShouldersEngineTests
{
    private static PatternBarResult[] Run(IReadOnlyList<CandleData> bars, CoreHeadAndShouldersParameter? p = null)
        => HeadAndShouldersEngine.Run(PatternEngineTestBars.Context(bars), p ?? new CoreHeadAndShouldersParameter());

    private static int[] LitBars(PatternBarResult[] r) => Enumerable.Range(0, r.Length).Where(i => r[i].Fires).ToArray();

    private static int[] Lit(HeadSpec spec, CoreHeadAndShouldersParameter? p = null) => LitBars(Run(HeadAndShoulders(spec).Bars, p));

    private static void AssertFires(HeadSpec spec, CoreHeadAndShouldersParameter? p = null, int? expectedFirstBar = null)
    {
        Built built = HeadAndShoulders(spec);
        PatternBarResult[] r = Run(built.Bars, p);
        int first = expectedFirstBar ?? built.BreakBar;

        Assert.True(r.Length > first && r[first].Fires, $"must fire on bar {first}; lit bars: {string.Join(",", LitBars(r))}");
        Assert.Equal(first, LitBars(r).First());
        Assert.Equal(spec.Top ? -1 : 1, r[first].Direction);
        Assert.Equal(spec.Top, r[first].BearishFires);
        Assert.Equal(!spec.Top, r[first].BullishFires);
    }

    private static void AssertSilent(HeadSpec spec, CoreHeadAndShouldersParameter? p = null)
        => Assert.Empty(Lit(spec, p));

    public static IEnumerable<object[]> Sides() { yield return new object[] { true }; yield return new object[] { false }; }

    // ---------------------------------------------------------------- the pattern and the head

    [Theory]
    [MemberData(nameof(Sides))]
    public void ThePattern_FiresOnTheBreakBar_AndForTheHoldBars(bool top)
    {
        Built built = HeadAndShoulders(new HeadSpec { Top = top });
        PatternBarResult[] r = Run(built.Bars);

        Assert.Equal(Enumerable.Range(built.BreakBar, 1 + new CoreHeadAndShouldersParameter().SignalPersistenceBars), LitBars(r));
        Assert.Equal(top ? -1 : 1, r[built.BreakBar].Direction);
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void TheHeadMustBeBeyondBothShoulders(bool top)
    {
        AssertSilent(new HeadSpec { Top = top, HeadPrice = 150m });
        AssertSilent(new HeadSpec { Top = top, HeadPrice = 149m });
        AssertFires(new HeadSpec { Top = top, HeadPrice = 151m, Valley1 = 140m, Valley2 = 140m }, new CoreHeadAndShouldersParameter { MinHeadProminenceRatio = 0.01m });
    }

    [Theory]
    [InlineData(true, 0.5, true)]
    [InlineData(true, 0.51, false)]
    [InlineData(false, 0.5, true)]
    [InlineData(false, 0.51, false)]
    public void HeadProminence_EdgeIsTheRatioToThePatternHeight(bool top, double minRatio, bool fires)
    {
        // Head 162, shoulders 150, necklines 138: (162 - 150) / (162 - 138) = 0.5.
        var spec = new HeadSpec { Top = top, HeadPrice = 162m };
        var p = new CoreHeadAndShouldersParameter { MinHeadProminenceRatio = (decimal)minRatio };

        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    // ---------------------------------------------------------------- shoulders

    [Theory]
    [InlineData(true, 142.5, true)]
    [InlineData(true, 142.4, false)]
    [InlineData(false, 147.4, true)]
    [InlineData(false, 147.3, false)]
    public void ShoulderTolerance_EdgeIsFivePercentOfTheHigherShoulder(bool top, double rightShoulder, bool fires)
    {
        // The top is 7.5 under 150 (5 percent of 150); the inverse is the mirror image around 100, where 5 percent of the higher bottom (50 + d) puts the edge at d = 2.6.
        var spec = new HeadSpec { Top = top, RightShoulderPrice = (decimal)rightShoulder };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void ShoulderTolerance_FollowsTheParameter()
    {
        var p = new CoreHeadAndShouldersParameter { ShoulderTolerancePercent = 2.0m };
        AssertFires(new HeadSpec { RightShoulderPrice = 147m }, p);
        AssertSilent(new HeadSpec { RightShoulderPrice = 146.9m }, p);
    }

    // ---------------------------------------------------------------- neckline

    [Theory]
    [InlineData(true, 0.35, true)]
    [InlineData(true, 0.349, false)]
    [InlineData(false, 0.35, true)]
    [InlineData(false, 0.349, false)]
    public void NecklineSlope_EdgeIsTheValleyDifferenceOverThePatternHeight(bool top, double maxSlope, bool fires)
    {
        // Valleys 138 and 131, head 154.5: the neckline at the head is 134.5, so the pattern height is 20 and |131 - 138| / 20 = 0.35.
        var spec = new HeadSpec { Top = top, HeadPrice = 154.5m, Valley2 = 131m, BreakClose = 120m };
        var p = new CoreHeadAndShouldersParameter { MaxNecklineSlopeRatio = (decimal)maxSlope };

        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Fact]
    public void TheBreakIsMeasuredAgainstTheExtrapolatedNeckline()
    {
        // The neckline falls 0.2333 per bar: at the break bar (115) it is 126.3, so a close of 130 is still above it although it is below both valleys of the flat fixture's level 137.7.
        var above = new HeadSpec { HeadPrice = 154.5m, Valley2 = 131m, BreakClose = 130m };
        var below = new HeadSpec { HeadPrice = 154.5m, Valley2 = 131m, BreakClose = 125m };

        AssertSilent(above);
        AssertFires(below);
    }

    [Fact]
    public void BothShouldersMustBeBeyondTheNeckline_AtTheirOwnBar()
    {
        // The second valley is lifted so that the neckline passes above the right shoulder: nothing.
        AssertSilent(new HeadSpec { Valley2 = 149m, HeadPrice = 175m, BreakClose = 140m });
        AssertFires(new HeadSpec { Valley2 = 138m, HeadPrice = 175m });
    }

    // ---------------------------------------------------------------- durations

    [Theory]
    [InlineData(110, 140, true)]   // left interval 60
    [InlineData(111, 141, false)]  // left interval 61
    [InlineData(80, 140, true)]    // right interval 60
    [InlineData(80, 141, false)]   // right interval 61
    public void PeakIntervals_AreAtMost60Bars(int head, int rightShoulder, bool fires)
    {
        var spec = new HeadSpec { Head = head, RightShoulder = rightShoulder };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void PeakIntervals_AtLeastTheParameter(int minInterval, bool fires)
    {
        // Left shoulder at 51, head at 80: the left interval is 29 bars.
        var spec = new HeadSpec { LeftShoulder = 51 };
        var p = new CoreHeadAndShouldersParameter { MinPeakIntervalBars = minInterval };
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Theory]
    [InlineData(130, true)]   // right 50 / left 20 = 2.5
    [InlineData(131, false)]  // 51 / 20 = 2.55
    public void TimeSymmetry_UpperEdgeIs2p5(int rightShoulder, bool fires)
    {
        var spec = new HeadSpec { LeftShoulder = 60, Head = 80, RightShoulder = rightShoulder };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Theory]
    [InlineData(134, true)]   // right 24 / left 60 = 0.4
    [InlineData(133, false)]  // 23 / 60 = 0.383
    public void TimeSymmetry_LowerEdgeIs0p4(int rightShoulder, bool fires)
    {
        var spec = new HeadSpec { LeftShoulder = 50, Head = 110, RightShoulder = rightShoulder };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void BreakoutWait_IsAtMost30BarsAfterTheRightShoulder(int breakDelay, bool fires)
    {
        var spec = new HeadSpec { BreakDelay = breakDelay };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void TotalLength_MaximumIsTheParameter()
    {
        // Left shoulder 50 to the break bar 115 is 65 bars.
        AssertFires(new HeadSpec(), new CoreHeadAndShouldersParameter { MaxTotalBars = 65 });
        AssertSilent(new HeadSpec(), new CoreHeadAndShouldersParameter { MaxTotalBars = 64 });
    }

    [Fact]
    public void TotalLength_MinimumDelaysTheSignalToTheFirstBarThatReachesIt()
    {
        var p = new CoreHeadAndShouldersParameter { MinTotalBars = 66, RequireVolumeConfirmation = false };

        AssertFires(new HeadSpec(), p, expectedFirstBar: 116);
        AssertFires(new HeadSpec(), new CoreHeadAndShouldersParameter { MinTotalBars = 65 });
    }

    // ---------------------------------------------------------------- confirmation, margin, volume

    [Fact]
    public void TheSignalIsNeverBeforeTheConfirmationOfTheRightShoulder()
    {
        // A fractal of 10 bars confirms the right shoulder (bar 110) at bar 120: the break at 115 stays below the neckline, so the first signal bar is 120, not 115.
        var p = new CoreHeadAndShouldersParameter { FractalWindowBars = 10, RequireVolumeConfirmation = false };

        AssertFires(new HeadSpec(), p, expectedFirstBar: 120);
    }

    [Fact]
    public void ABreakBeforeTheConfirmationThatIsGivenBackBeforeIt_FiresNothing()
    {
        // The break close at bar 112 is below the neckline, but the right shoulder is confirmed at 113 (fractal 3) and the close of 113 is back above the neckline; nothing ever breaks afterwards.
        var spec = new HeadSpec
        {
            BreakDelay = 2,
            ExtraAnchors = new[] { (113, 145m), (114, 145m), (115, 145m), (116, 145m), (122, 145m) },
        };

        AssertSilent(spec);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBreakNeedsAMargin_ZeroPercentMeansNoMarginAtAll(bool top)
    {
        // Necklines at 138.0; a close at 137.9 is below by 0.1 but not by max(0.5 percent of 138, 0.25 ATR).
        var spec = new HeadSpec { Top = top, BreakClose = 137.9m };

        AssertSilent(spec);
        AssertFires(spec, new CoreHeadAndShouldersParameter { BreakoutMarginPercent = 0m });
    }

    [Theory]
    [InlineData(1250, true)]
    [InlineData(1249, false)]
    public void Volume_EdgeIs1p25TimesTheAverageOfThe20BarsBeforeTheBreak(long volume, bool fires)
    {
        var spec = new HeadSpec { BreakBarVolume = volume };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void Volume_OptionOffPasses()
    {
        AssertFires(new HeadSpec { BreakBarVolume = 1 }, new CoreHeadAndShouldersParameter { RequireVolumeConfirmation = false });
    }

    // ---------------------------------------------------------------- prior trend and zone

    [Theory]
    [MemberData(nameof(Sides))]
    public void PriorTrend_IsRequired_AndTheSwitchTurnsItOff(bool top)
    {
        // The series starts high (190), falls to 140 by bar 25 and climbs to the left shoulder: the move to the head is large (more than 15 percent) but the 20-bar SMA is below the 50-bar SMA, so there is
        // no prior up-trend (the family text of Head and Shoulders). The head's zone is measured on the 60 bars before bar 80 and does not reach the early highs.
        var fallingIn = new HeadSpec { Top = top, ExtraAnchors = new[] { (0, 190m), (15, 190m), (25, 140m) } };

        AssertSilent(fallingIn);
        AssertFires(fallingIn, new CoreHeadAndShouldersParameter { RequirePriorTrend = false });
    }

    [Fact]
    public void Zone_TheHeadMustBeInTheHighZone()
    {
        // A higher high 60 bars before the head puts the head in the middle of its 60-bar range.
        var spike = new HeadSpec { ExtraAnchors = new[] { (25, 200m), (35, 120m) } };

        AssertSilent(spike, new CoreHeadAndShouldersParameter { RequirePriorTrend = false });
        AssertFires(spike, new CoreHeadAndShouldersParameter { RequirePriorTrend = false, PriceZoneLookbackBars = 40 });
    }

    [Fact]
    public void PriorTrend_UsesOnlyTheFastAgainstSlowSma()
    {
        // The family text of Head and Shoulders is "SMA20 above SMA50"; the common OR also accepts a rising SMA20. Series A: SMA20 above SMA50 and flat; series B: SMA20 rising but below SMA50.
        PatternSeriesContext a = PatternEngineTestBars.Context(Enumerable.Range(0, 80).Select(i => Flat(i, i < 50 ? 100m : 140m)).ToArray());
        PatternSeriesContext b = PatternEngineTestBars.Context(Enumerable.Range(0, 80).Select(i => Flat(i, i < 50 ? 200m : i < 70 ? 100m : 120m)).ToArray());

        Assert.True(PatternMath.MidTermUptrend(a, 80, 150m, 30, 0.10m, 3m, confirmation: MidTrendConfirmation.FastAboveSlow));
        Assert.False(PatternMath.MidTermUptrend(a, 80, 150m, 30, 0.10m, 3m, confirmation: MidTrendConfirmation.FastRising));
        Assert.True(PatternMath.MidTermUptrend(a, 80, 150m, 30, 0.10m, 3m));

        Assert.False(PatternMath.MidTermUptrend(b, 80, 150m, 30, 0.10m, 3m, confirmation: MidTrendConfirmation.FastAboveSlow));
        Assert.True(PatternMath.MidTermUptrend(b, 80, 150m, 30, 0.10m, 3m, confirmation: MidTrendConfirmation.FastRising));
        Assert.True(PatternMath.MidTermUptrend(b, 80, 150m, 30, 0.10m, 3m));
    }

    private static CandleData Flat(int index, decimal price) => PatternEngineTestBars.Bar(index, price, price + 1m, price - 1m, price);

    // ---------------------------------------------------------------- life cycle

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Persistence_LightsTheBreakBarAndTheGivenNumberOfBarsAfterIt(int persistence)
    {
        Built built = HeadAndShoulders(new HeadSpec());
        PatternBarResult[] r = Run(built.Bars, new CoreHeadAndShouldersParameter { SignalPersistenceBars = persistence });

        Assert.Equal(Enumerable.Range(built.BreakBar, persistence + 1), LitBars(r));
    }

    [Fact]
    public void ACandidateFiresOnce_AlthoughEveryLaterBarStaysBeyondTheNeckline()
    {
        Built built = HeadAndShoulders(new HeadSpec());
        Assert.Single(LitBars(Run(built.Bars, new CoreHeadAndShouldersParameter { SignalPersistenceBars = 0 })));
    }

    [Fact]
    public void ACloseBeyondTheHeadBeforeTheBreak_EndsTheCandidate()
    {
        // The right shoulder (bar 110) is confirmed at 113; a close of 166 (above the head 165) at bar 114 ends it before the break bar 115. A close of 164 does not.
        AssertSilent(new HeadSpec { ExtraAnchors = new[] { (113, 140m), (114, 166m) } });
        AssertFires(new HeadSpec { ExtraAnchors = new[] { (113, 140m), (114, 164m) } });
    }

    [Fact]
    public void ACloseBeyondTheHeadInTheHoldPeriod_CutsTheSignalFromThatBar()
    {
        Built plain = HeadAndShoulders(new HeadSpec());
        int b = plain.BreakBar;
        var spec = new HeadSpec { ExtraAnchors = new[] { (b + 2, 132m), (b + 3, 166m), (b + 4, 132m) } };

        Assert.Equal(new[] { b, b + 1, b + 2 }, Lit(spec));
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void DirectionMode_AllowsOnlyItsSide(bool top)
    {
        var spec = new HeadSpec { Top = top };
        var wanted = top ? PatternDirectionMode.BearishOnly : PatternDirectionMode.BullishOnly;
        var other = top ? PatternDirectionMode.BullishOnly : PatternDirectionMode.BearishOnly;

        AssertFires(spec, new CoreHeadAndShouldersParameter { DirectionMode = wanted });
        AssertFires(spec, new CoreHeadAndShouldersParameter { DirectionMode = PatternDirectionMode.Both });
        AssertSilent(spec, new CoreHeadAndShouldersParameter { DirectionMode = other });
    }

    [Fact]
    public void TheZigZagThreshold_ReachesTheEngine()
    {
        // Swings of 8 to 10 percent are no pivots for a threshold of 12 percent.
        AssertFires(new HeadSpec());
        AssertSilent(new HeadSpec(), new CoreHeadAndShouldersParameter { ZigZagThresholdPercent = 12m });
    }

    // ---------------------------------------------------------------- invalid bars (plan R-14..R-16)

    [Fact]
    public void AnInconsistentBarOutsideEveryReadSet_ChangesNothing()
    {
        // Left shoulder at 70: the earliest bar read is 20 (the SMA50 of bar 69), so bar 3 is outside every read set.
        Built built = HeadAndShoulders(new HeadSpec { LeftShoulder = 70, Head = 100, RightShoulder = 130 });
        PatternBarResult[] clean = Run(built.Bars);

        Assert.NotEmpty(LitBars(clean));
        Assert.Equal(clean, Run(PatternEngineTestBars.Corrupt(built.Bars, 3)));
    }

    [Theory]
    [InlineData(25, "the SMA 50 of the prior trend")]
    [InlineData(60, "the prior trend window")]
    [InlineData(85, "the zone of the head")]
    [InlineData(118, "the bars between the right shoulder and the break")]
    public void AnInconsistentBarInsideAReadSet_GivesNothingForThatCandidate(int bar, string readBy)
    {
        Built built = HeadAndShoulders(new HeadSpec { LeftShoulder = 70, Head = 100, RightShoulder = 130 });
        Assert.NotEmpty(LitBars(Run(built.Bars)));

        Assert.True(LitBars(Run(PatternEngineTestBars.Corrupt(built.Bars, bar))).Length == 0, $"bar {bar} is read by {readBy}");
    }

    [Fact]
    public void AnInconsistentBarInTheHoldPeriod_ClearsThatBarOnly()
    {
        Built built = HeadAndShoulders(new HeadSpec());
        PatternBarResult[] r = Run(PatternEngineTestBars.Corrupt(built.Bars, built.BreakBar + 2));

        Assert.Equal(new[] { built.BreakBar, built.BreakBar + 1, built.BreakBar + 3, built.BreakBar + 4, built.BreakBar + 5 }, LitBars(r));
    }

    // ---------------------------------------------------------------- causality

    [Theory]
    [MemberData(nameof(Sides))]
    public void PrefixInvariance_AppendingBarsNeverChangesAnEarlierResult(bool top)
    {
        Built built = HeadAndShoulders(new HeadSpec { Top = top });
        PatternBarResult[] full = Run(built.Bars);

        for (int cut = 1; cut <= built.Bars.Length; cut++)
        {
            Assert.Equal(full.Take(cut), Run(built.Bars.Take(cut).ToArray()));
        }
    }

    private static CoreHeadAndShouldersParameter Relaxed() => RelaxedHeadAndShoulders();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PrefixInvariance_OnRandomWalks(int seed)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(seed, 1500);
        var p = Relaxed();
        PatternBarResult[] full = Run(bars, p);

        foreach (int cut in new[] { 70, 211, 500, 901, 1499 })
        {
            Assert.Equal(full.Take(cut), Run(bars.Take(cut).ToArray(), p).Take(cut));
        }
    }

    [Fact]
    public void RandomWalks_FindPatterns_AndNeverBreakTheOutputContract()
    {
        int signals = 0;
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            foreach (PatternBarResult x in Run(PatternEngineTestBars.Walk(seed, 2000), Relaxed()))
            {
                if (x.Fires)
                {
                    signals++;
                    Assert.True((x.Direction == 1 && x.BullishFires) || (x.Direction == -1 && x.BearishFires), "the direction is a side that fires");
                }
                else
                {
                    Assert.Equal(0, x.Direction);
                }
            }
        }

        Assert.True(signals > 0, "the relaxed settings must find a pattern in eight walks, or the invariance tests are vacuous");
    }

    [Fact]
    public void ASeriesShorterThanAnyPattern_GivesNoSignalAndNoException()
    {
        Assert.Empty(LitBars(Run(Array.Empty<CandleData>())));
        Assert.Empty(LitBars(Run(PatternEngineTestBars.Walk(1, 5))));
    }
}
