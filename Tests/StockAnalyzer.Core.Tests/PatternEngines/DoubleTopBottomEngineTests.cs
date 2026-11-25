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
/// Double Top / Bottom engine (plan ChartPatterns56 v2, run V2-3; ledger rows DT_*): the two reported false positives, every edge of the rules of <c>02_double_top_bottom.md</c>, the life cycle and the causality
/// invariants. The fixtures are real bars with known pivot prices (<see cref="FormationFixtures"/>); the top and its mirror image (the bottom) are tested together. Each "silent" assertion has a control that fires
/// when only the tested number is moved back inside its rule, so no test is silent for another reason.
/// </summary>
public class DoubleTopBottomEngineTests
{
    private static PatternBarResult[] Run(IReadOnlyList<CandleData> bars, CoreDoubleTopBottomParameter? p = null)
        => DoubleTopBottomEngine.Run(PatternEngineTestBars.Context(bars), p ?? new CoreDoubleTopBottomParameter());

    private static int[] LitBars(PatternBarResult[] r) => Enumerable.Range(0, r.Length).Where(i => r[i].Fires).ToArray();

    private static int[] Lit(DoubleSpec spec, CoreDoubleTopBottomParameter? p = null) => LitBars(Run(Double(spec).Bars, p));

    private static void AssertFires(DoubleSpec spec, CoreDoubleTopBottomParameter? p = null, int? expectedFirstBar = null)
    {
        Built built = Double(spec);
        PatternBarResult[] r = Run(built.Bars, p);
        int first = expectedFirstBar ?? built.BreakBar;

        Assert.True(r.Length > first && r[first].Fires, $"must fire on bar {first}; lit bars: {string.Join(",", LitBars(r))}");
        Assert.Equal(first, LitBars(r).First());
        Assert.Equal(spec.Top ? -1 : 1, r[first].Direction);
        Assert.Equal(spec.Top, r[first].BearishFires);
        Assert.Equal(!spec.Top, r[first].BullishFires);
    }

    private static void AssertSilent(DoubleSpec spec, CoreDoubleTopBottomParameter? p = null)
        => Assert.Empty(Lit(spec, p));

    private static CoreDoubleTopBottomParameter Relaxed() => RelaxedDouble();

    public static IEnumerable<object[]> Sides() { yield return new object[] { true }; yield return new object[] { false }; }

    // ---------------------------------------------------------------- the reported symptoms (acceptance)

    [Theory]
    [MemberData(nameof(Sides))]
    public void Symptom_C_ThePeaksAtTheHighZone_DeepValley_CloseBreakoutWithVolume_FireOnTheBreakBar_AndForTheHoldBars(bool top)
    {
        Built built = Double(new DoubleSpec { Top = top });
        PatternBarResult[] r = Run(built.Bars);

        Assert.Equal(Enumerable.Range(built.BreakBar, 1 + new CoreDoubleTopBottomParameter().SignalPersistenceBars), LitBars(r));
        Assert.Equal(top ? -1 : 1, r[built.BreakBar].Direction);
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void Symptom_A_TwoEqualPeaksInTheMiddleOfThe60BarRange_GiveNothing(bool top)
    {
        // The same peaks as the firing fixture, but a higher high 60 bars before them puts them in the middle of the range.
        AssertSilent(new DoubleSpec { Top = top, Spike = true, FirstPeak = 80 });
        AssertFires(new DoubleSpec { Top = top, Spike = false, FirstPeak = 80 });
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void Symptom_B_TwoEqualPeaks80BarsApart_GiveNothing(bool top)
    {
        AssertSilent(new DoubleSpec { Top = top, Gap = 80, ValleyOffset = 40 });
        AssertFires(new DoubleSpec { Top = top, Gap = 50, ValleyOffset = 25 });
    }

    // ---------------------------------------------------------------- interval of the peaks [10, 65] and the legs [3, 40]

    [Theory]
    [InlineData(true, 10, 5, true)]
    [InlineData(true, 9, 4, false)]
    [InlineData(true, 65, 32, true)]
    [InlineData(true, 66, 33, false)]
    [InlineData(false, 10, 5, true)]
    [InlineData(false, 9, 4, false)]
    [InlineData(false, 65, 32, true)]
    [InlineData(false, 66, 33, false)]
    public void PeakInterval_EdgesAre10And65Bars(bool top, int gap, int valleyOffset, bool fires)
    {
        var spec = new DoubleSpec { Top = top, Gap = gap, ValleyOffset = valleyOffset };

        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void PeakInterval_FollowsTheParameters()
    {
        AssertFires(new DoubleSpec { Gap = 9, ValleyOffset = 4 }, new CoreDoubleTopBottomParameter { MinPeakIntervalBars = 9 });
        AssertFires(new DoubleSpec { Gap = 66, ValleyOffset = 33 }, new CoreDoubleTopBottomParameter { MaxPeakIntervalBars = 66 });
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void TheLegsAreAtMost40Bars(int valleyOffset, bool fires)
    {
        // Peaks 60 bars apart, so the interval is inside [10, 65] and only a leg of 41 bars is outside its rule: the valley sits 41 bars after the first peak.
        var spec = new DoubleSpec { Gap = 60, ValleyOffset = valleyOffset };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    // ---------------------------------------------------------------- peak tolerance: abs(P1 - P2) <= max(3 % of the higher peak, 0.5 ATR)

    [Theory]
    [InlineData(true, 145.5, true)]
    [InlineData(true, 145.4, false)]
    [InlineData(false, 148.5, true)]
    [InlineData(false, 148.4, false)]
    public void PeakTolerance_EdgeIsThreePercentOfTheHigherPeak(bool top, double peak2, bool fires)
    {
        // The zone of the second peak is relaxed, because a second peak 3 percent lower is in the middle of this fixture's range; the tolerance is the only rule that moves.
        // The bottom is the mirror image around 100, so its percentages differ: 1.5 points under 51.5 is the edge there (3 percent of the higher bottom).
        var p = new CoreDoubleTopBottomParameter { Peak2ZoneThreshold = 0.5m };
        var spec = new DoubleSpec { Top = top, Peak2 = (decimal)peak2 };

        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Fact]
    public void PeakTolerance_FollowsTheParameter()
    {
        var p = new CoreDoubleTopBottomParameter { Peak2ZoneThreshold = 0.5m, PeakTolerancePercent = 4.0m };

        AssertFires(new DoubleSpec { Peak2 = 144m }, p);
        AssertSilent(new DoubleSpec { Peak2 = 143.9m }, p);
    }

    // ---------------------------------------------------------------- valley depth, retracement against the prior move

    [Fact]
    public void ShallowValley_GivesNothing_DeepValleyFires()
    {
        // A valley 2 points under the peaks is neither 6 percent nor 2.5 ATR deep (and too small a swing for the ZigZag): nothing; the default valley fires (control).
        AssertSilent(new DoubleSpec { Valley = 148m, BreakClose = 142m });
        AssertFires(new DoubleSpec());
    }

    [Theory]
    [InlineData(5.99, false)]
    [InlineData(6.0, true)]
    [InlineData(14.99, true)]
    public void NecklineDepth_PercentEdge_AndTheAtrAlternativeIsOnlyUsedAboveTwoPercent(double depth, bool expected)
    {
        // ATR = 6: the alternative (2.5 ATR = 15) is out of reach below 15, so below it only the 6 percent rule decides; the alternative also needs 2 percent of the reference.
        CandleData[] bars = Enumerable.Range(0, 30).Select(i => PatternEngineTestBars.Bar(i, 100m, 103m, 97m, 100m)).ToArray();
        PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);

        Assert.Equal(expected, PatternEngineSupport.NecklineDepthMet(ctx, (decimal)depth, 100m, 6m, atrBar: 29));
        Assert.True(PatternEngineSupport.NecklineDepthMet(ctx, 15m, 100m, 30m, atrBar: 29), "2.5 ATR = 15 and 15 percent of the reference is above 2 percent");
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 14.99m, 100m, 30m, atrBar: 29));
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 15m, 1000m, 30m, atrBar: 29), "15 is 1.5 percent of 1000: below the 2 percent floor of the alternative");
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 0m, 100m, 1m, atrBar: 29));
    }

    [Fact]
    public void MaxRetrace_EdgeIsExactlyTheRatioOfTheDepthToThePriorMove()
    {
        Built built = Double(new DoubleSpec());
        PatternSeriesContext ctx = PatternEngineTestBars.Context(built.Bars);
        int firstPeak = new DoubleSpec().FirstPeak;
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, firstPeak, 150m, 30, top: true, out decimal width));
        decimal ratio = (150m - 138m) / width;

        Assert.NotEmpty(Run(built.Bars, new CoreDoubleTopBottomParameter { MaxNecklineRetraceRatio = ratio }).Where(r => r.Fires));
        Assert.Empty(Run(built.Bars, new CoreDoubleTopBottomParameter { MaxNecklineRetraceRatio = ratio - 0.001m }).Where(r => r.Fires));
    }

    [Fact]
    public void PriorMoveWidth_IsTheFirstPeakMinusTheLowestLow_AndTheMirrorForABottom()
    {
        Built top = Double(new DoubleSpec { Top = true });
        Built bottom = Double(new DoubleSpec { Top = false });
        PatternSeriesContext topCtx = PatternEngineTestBars.Context(top.Bars);
        PatternSeriesContext bottomCtx = PatternEngineTestBars.Context(bottom.Bars);

        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(topCtx, 60, 150m, 30, top: true, out decimal topWidth));
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(bottomCtx, 60, 50m, 30, top: false, out decimal bottomWidth));
        Assert.Equal(topWidth, bottomWidth);
        Assert.Equal(150m - top.Bars.Skip(30).Take(30).Min(b => b.Low), topWidth);
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(topCtx, 10, 150m, 30, top: true, out _), "the lookback leaves the series");
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(topCtx, 60, 100m, 30, top: true, out _), "no width when the peak is not above the low");
    }

    [Fact]
    public void NecklineDepth_NonPositiveReferenceOrDepth_IsNotMet()
    {
        // Owner supplement to Q-V2-2a: a non-positive P_ref (negative prices, a direct call) or depth is "not met": no division, no sign flip.
        PatternSeriesContext ctx = PatternEngineTestBars.Context(Enumerable.Range(0, 30).Select(i => PatternEngineTestBars.Bar(i, 100m, 103m, 97m, 100m)).ToArray());

        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 10m, 0m, 6m, atrBar: 29));
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 10m, -50m, 6m, atrBar: 29));
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, -10m, 100m, 6m, atrBar: 29));
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 0m, 100m, 6m, atrBar: 29));
        Assert.True(PatternEngineSupport.NecklineDepthMet(ctx, 10m, 100m, 6m, atrBar: 29));
        Assert.Equal(-5m, DoubleTopBottomEngine.DepthReferencePrice(true, -5m, 10m));
        Assert.False(PatternEngineSupport.NecklineDepthMet(ctx, 10m, DoubleTopBottomEngine.DepthReferencePrice(true, -5m, 10m), 6m, atrBar: 29));
    }

    [Fact]
    public void PriorMoveWidth_ThreeUnsafeCases_AreNotMet()
    {
        Built built = Double(new DoubleSpec());
        PatternSeriesContext ctx = PatternEngineTestBars.Context(built.Bars);

        // (1) history shorter than the window; (2) an inconsistent bar inside [P1 - 30, P1 - 1]; (3) a width <= 0.
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 29, 150m, 30, top: true, out _));
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 30, 150m, 30, top: true, out _));
        PatternSeriesContext corrupted = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(built.Bars, 45));
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(corrupted, 60, 150m, 30, top: true, out _));
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(corrupted, 60, 150m, 10, top: true, out _), "the bad bar is outside the shorter window");
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 60, 90m, 30, top: true, out _));
        Assert.False(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 60, 150m, 0, top: true, out _));

        // The same cases keep a candidate from firing: an inconsistent bar in the window.
        Assert.Empty(Run(PatternEngineTestBars.Corrupt(built.Bars, 45)).Where(r => r.Fires));
    }

    [Fact]
    public void MaxRetrace_IsSkippedWithoutThePriorTrendRequirement()
    {
        // 0.2 is the smallest allowed ratio; the valley (12) is 47 percent of the prior move (25.6), so with the prior trend required nothing fires, and without it the rule is off.
        var spec = new DoubleSpec();

        AssertSilent(spec, new CoreDoubleTopBottomParameter { MaxNecklineRetraceRatio = 0.2m });
        AssertFires(spec, new CoreDoubleTopBottomParameter { MaxNecklineRetraceRatio = 0.2m, RequirePriorTrend = false });
    }

    [Fact]
    public void MaxRetrace_WindowIsThePriorTrendLookbackSetting()
    {
        // One setting, one window: with a lookback of 10 bars the prior move is much smaller (the lowest low of [50, 59]), so the same ratio edge moves.
        Built built = Double(new DoubleSpec());
        PatternSeriesContext ctx = PatternEngineTestBars.Context(built.Bars);
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 60, 150m, 30, top: true, out decimal wide));
        Assert.True(DoubleTopBottomEngine.TryPriorMoveWidth(ctx, 60, 150m, 10, top: true, out decimal narrow));
        Assert.True(narrow < wide);

        decimal edgeNarrow = 12m / narrow;
        var p = new CoreDoubleTopBottomParameter { PriorTrendLookbackBars = 10, MaxNecklineRetraceRatio = edgeNarrow };
        Assert.NotEmpty(Run(built.Bars, p).Where(r => r.Fires));
        p.MaxNecklineRetraceRatio = edgeNarrow - 0.001m;
        Assert.Empty(Run(built.Bars, p).Where(r => r.Fires));
    }

    // ---------------------------------------------------------------- cleanliness (spec 4)

    [Fact]
    public void Cleanliness_NoCloseBeyondThePeaksBetweenThem_AndAnUnreadableWindowFails()
    {
        CandleData[] bars = Enumerable.Range(0, 20).Select(i => PatternEngineTestBars.Bar(i, 100m, 101m, 99m, 100m)).ToArray();
        PatternSeriesContext clean = PatternEngineTestBars.Context(bars);
        CandleData[] dirtyTop = (CandleData[])bars.Clone();
        dirtyTop[10] = PatternEngineTestBars.Bar(10, 100m, 106m, 99m, 105m);
        CandleData[] dirtyBottom = (CandleData[])bars.Clone();
        dirtyBottom[10] = PatternEngineTestBars.Bar(10, 100m, 101m, 94m, 95m);

        Assert.True(DoubleTopBottomEngine.HasCleanInterval(clean, 2, 18, top: true, higherPeak: 104m, lowerPeak: 96m));
        Assert.False(DoubleTopBottomEngine.HasCleanInterval(PatternEngineTestBars.Context(dirtyTop), 2, 18, top: true, higherPeak: 104m, lowerPeak: 96m));
        Assert.True(DoubleTopBottomEngine.HasCleanInterval(PatternEngineTestBars.Context(dirtyTop), 2, 18, top: false, higherPeak: 104m, lowerPeak: 96m));
        Assert.False(DoubleTopBottomEngine.HasCleanInterval(PatternEngineTestBars.Context(dirtyBottom), 2, 18, top: false, higherPeak: 104m, lowerPeak: 96m));
        Assert.False(DoubleTopBottomEngine.HasCleanInterval(PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(bars, 10)), 2, 18, top: true, higherPeak: 104m, lowerPeak: 96m));
        Assert.True(DoubleTopBottomEngine.HasCleanInterval(clean, 5, 6, top: true, higherPeak: 104m, lowerPeak: 96m), "no bar between two neighbours");
    }

    // ---------------------------------------------------------------- zone and prior trend

    [Theory]
    [MemberData(nameof(Sides))]
    public void PriorTrend_IsRequired_AndTheSwitchTurnsItOff(bool top)
    {
        // A flat run-up (100 -> 150 happens in the last 10 bars only is not what this builds): the prior move is removed by starting the series at the peak level.
        var flatStart = new DoubleSpec { Top = top, ExtraAnchors = new[] { (1, 149m) } };

        AssertSilent(flatStart);
        AssertFires(flatStart, new CoreDoubleTopBottomParameter { RequirePriorTrend = false });
    }

    [Fact]
    public void Zone_FollowsTheThresholdsAndTheLookback()
    {
        // With the spike 60 bars before the first peak the first peak is at Pos = (150 - low) / (190 - low) of its 60 bars: the thresholds decide, and a 20-bar zone no longer contains the spike.
        Built built = Double(new DoubleSpec { Spike = true, FirstPeak = 80 });
        decimal? pos = PatternMath.Pos(PatternEngineTestBars.Context(built.Bars), 150m, 80, 60);
        Assert.NotNull(pos);
        Assert.True(pos.Value < 0.85m);

        Assert.Empty(Run(built.Bars).Where(r => r.Fires));
        Assert.True(pos.Value < 0.5m);
        Assert.Empty(Run(built.Bars, new CoreDoubleTopBottomParameter { Peak1ZoneThreshold = 0.5m, Peak2ZoneThreshold = 0.5m }).Where(r => r.Fires));
        Assert.NotEmpty(Run(built.Bars, new CoreDoubleTopBottomParameter { PriceZoneLookbackBars = 20 }).Where(r => r.Fires));
    }

    // ---------------------------------------------------------------- breakout: close, margin, volume, wait, total length

    [Theory]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void BreakoutWait_IsAtMost25BarsAfterTheSecondPeak(int breakDelay, bool fires)
    {
        var spec = new DoubleSpec { BreakDelay = breakDelay };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void BreakoutWait_FollowsTheParameter()
    {
        AssertFires(new DoubleSpec { BreakDelay = 30 }, new CoreDoubleTopBottomParameter { MaxBreakoutWaitBars = 30 });
        AssertSilent(new DoubleSpec { BreakDelay = 31 }, new CoreDoubleTopBottomParameter { MaxBreakoutWaitBars = 30 });
    }

    [Theory]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void TotalLength_IsAtMost90Bars(int breakDelay, bool fires)
    {
        // Peaks 65 bars apart: 65 + 25 = 90 bars from the first peak to the break bar is the largest allowed, 91 is not (the wait is widened so that it is not the limiting rule).
        var p = new CoreDoubleTopBottomParameter { MaxBreakoutWaitBars = 40 };
        var spec = new DoubleSpec { Gap = 65, ValleyOffset = 32, BreakDelay = breakDelay };
        if (fires) AssertFires(spec, p); else AssertSilent(spec, p);
    }

    [Fact]
    public void TotalLength_AtLeast12Bars_TheFirstBarThatReachesItIsTheSignalBar()
    {
        // Peaks 10 bars apart and the break 2 bars after the second peak would be a total of 12; the fractal of 1 bar lets the second peak be confirmed early. A break bar that is too early is skipped,
        // and the first later bar that still closes beyond the valley with volume is the signal bar (interpretation I-1 of ledger V2-3).
        var p = new CoreDoubleTopBottomParameter { FractalWindowBars = 1, RequireVolumeConfirmation = false };
        Built built = Double(new DoubleSpec { Gap = 10, ValleyOffset = 5, BreakDelay = 1 });
        PatternBarResult[] r = Run(built.Bars, p);

        Assert.Equal(60 + PatternSpecConstants.DoubleTotalBarsMin, LitBars(r).First());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBreakNeedsAMargin_ZeroPercentMeansNoMarginAtAll(bool top)
    {
        // Valley 138, the pivot price is 138.0; the break close 137.9 is below the valley by 0.1 but not by the margin max(0.5 percent of 138 = 0.69, 0.25 ATR).
        var spec = new DoubleSpec { Top = top, BreakClose = 137.9m };

        AssertSilent(spec);
        AssertFires(spec, new CoreDoubleTopBottomParameter { BreakoutMarginPercent = 0m });
    }

    [Theory]
    [InlineData(137.0, true)]
    [InlineData(137.4, false)]
    public void BreakMargin_IsTheLargerOfThePercentAndTheAtrTerm(double breakClose, bool fires)
    {
        // The percent term is 0.69 (valley 138.0 - 0.69 = 137.31); the ATR term of this fixture is smaller, so a close at 137.0 passes and 137.4 does not.
        var spec = new DoubleSpec { BreakClose = (decimal)breakClose };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void BreakMargin_Function_ZeroPercentIsZero_AboveZeroIsTheLargerTerm_NoAtrGivesNull()
    {
        CandleData[] bars = Enumerable.Range(0, 30).Select(i => PatternEngineTestBars.Bar(i, 100m, 103m, 97m, 100m)).ToArray();
        PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);

        Assert.Equal(0m, PatternMath.BreakMargin(ctx, 0m, 0.25m, 100m, 20));
        Assert.Equal(1.5m, PatternMath.BreakMargin(ctx, 0.5m, 0.25m, 100m, 20));      // ATR 6: 0.25 * 6 = 1.5 > 0.5
        Assert.Equal(5m, PatternMath.BreakMargin(ctx, 5m, 0.25m, 100m, 20));          // 5 percent of 100 = 5 > 1.5
        Assert.Null(PatternMath.BreakMargin(ctx, 0.5m, 0.25m, 100m, 3));              // bar 2 has no 14 bars of true range
        Assert.Equal(0m, PatternMath.BreakMargin(ctx, 0m, 0.25m, 100m, 3));           // no ATR needed without a percent
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1199, false)]
    public void Volume_EdgeIs1p2TimesTheAverageOfThe20BarsBeforeTheBreak(long volume, bool fires)
    {
        var spec = new DoubleSpec { BreakBarVolume = volume };
        if (fires) AssertFires(spec); else AssertSilent(spec);
    }

    [Fact]
    public void Volume_OptionOffAndAnInstrumentWithoutVolumePass_ButAZeroVolumeBreakBarOfAnInstrumentWithVolumeDoesNot()
    {
        AssertFires(new DoubleSpec { BreakBarVolume = 1 }, new CoreDoubleTopBottomParameter { RequireVolumeConfirmation = false });
        AssertSilent(new DoubleSpec { BreakBarVolume = 0 });

        Built built = Double(new DoubleSpec { BreakBarVolume = 0 });
        CandleData[] noVolume = built.Bars.Select(b => b with { Volume = 0 }).ToArray();
        Assert.Contains(built.BreakBar, LitBars(Run(noVolume)));
    }

    // ---------------------------------------------------------------- life cycle: persistence, consumption, invalidation

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Persistence_LightsTheBreakBarAndTheGivenNumberOfBarsAfterIt(int persistence)
    {
        Built built = Double(new DoubleSpec());
        PatternBarResult[] r = Run(built.Bars, new CoreDoubleTopBottomParameter { SignalPersistenceBars = persistence });

        Assert.Equal(Enumerable.Range(built.BreakBar, persistence + 1), LitBars(r));
    }

    [Fact]
    public void ACandidateFiresOnce_AlthoughEveryLaterBarStaysBeyondTheValley()
    {
        // The tail of the fixture is 12 bars beyond the valley; with a persistence of 0 only the break bar is lit, never the bars after it (the tuple is consumed).
        Built built = Double(new DoubleSpec());
        Assert.Single(LitBars(Run(built.Bars, new CoreDoubleTopBottomParameter { SignalPersistenceBars = 0 })));
    }

    [Fact]
    public void InvalidationAfterTheBreak_CutsTheSignalFromThatBar_ButNotTheEarlierBars()
    {
        // A close above 1.01 * 150 = 151.5 two bars after the break (the fixture tail is lifted for one bar).
        Built plain = Double(new DoubleSpec());
        int b = plain.BreakBar;
        var spec = new DoubleSpec { ExtraAnchors = new[] { (b + 2, 132m), (b + 3, 152m), (b + 4, 132m) } };

        int[] lit = Lit(spec);

        Assert.Equal(new[] { b, b + 1, b + 2 }, lit);
    }

    [Fact]
    public void InvalidationBeforeTheBreak_EndsTheCandidate_EvenAfterTheConfirmationBar()
    {
        // A close of 152 (> 151.5) between the confirmation of the second peak and the break bar: the candidate is dead, the later close beyond the valley fires nothing.
        Built plain = Double(new DoubleSpec());
        int p2 = 100;
        var spec = new DoubleSpec { ExtraAnchors = new[] { (p2 + 3, 144m), (p2 + 6, 152m) } };

        AssertSilent(spec);
        Assert.NotEmpty(Lit(new DoubleSpec { ExtraAnchors = new[] { (p2 + 3, 144m), (p2 + 6, 151m) } }));
        Assert.Equal(109, plain.BreakBar);
    }

    // ---------------------------------------------------------------- direction mode, ties

    [Theory]
    [MemberData(nameof(Sides))]
    public void DirectionMode_AllowsOnlyItsSide(bool top)
    {
        var spec = new DoubleSpec { Top = top };
        var wanted = top ? PatternDirectionMode.BearishOnly : PatternDirectionMode.BullishOnly;
        var other = top ? PatternDirectionMode.BullishOnly : PatternDirectionMode.BearishOnly;

        AssertFires(spec, new CoreDoubleTopBottomParameter { DirectionMode = wanted });
        AssertFires(spec, new CoreDoubleTopBottomParameter { DirectionMode = PatternDirectionMode.Both });
        AssertSilent(spec, new CoreDoubleTopBottomParameter { DirectionMode = other });
    }

    // ---------------------------------------------------------------- invalid bars (plan R-14..R-16)

    [Fact]
    public void AnInconsistentBarOutsideEveryReadSet_ChangesNothing()
    {
        var spec = new DoubleSpec { FirstPeak = 80 };
        Built built = Double(spec);
        PatternBarResult[] clean = Run(built.Bars);

        PatternBarResult[] corrupted = Run(PatternEngineTestBars.Corrupt(built.Bars, 3));

        Assert.Equal(clean, corrupted);
        Assert.NotEmpty(LitBars(clean));
    }

    [Theory]
    [InlineData(30, "the zone of the first peak")]
    [InlineData(60, "the prior trend and the zone of the first peak")]
    [InlineData(90, "the interval between the peaks")]
    [InlineData(125, "the bars between the second peak and the break")]
    public void AnInconsistentBarInsideAReadSet_GivesNothingForThatCandidate(int bar, string readBy)
    {
        Built built = Double(new DoubleSpec { FirstPeak = 80 });
        Assert.NotEmpty(LitBars(Run(built.Bars)));

        Assert.True(Empty(Run(PatternEngineTestBars.Corrupt(built.Bars, bar))), $"bar {bar} is read by {readBy}");
    }

    private static bool Empty(PatternBarResult[] r) => LitBars(r).Length == 0;

    [Fact]
    public void AnInconsistentBarInTheHoldPeriod_ClearsThatBarOnly()
    {
        Built built = Double(new DoubleSpec());
        int bad = built.BreakBar + 2;
        PatternBarResult[] r = Run(PatternEngineTestBars.Corrupt(built.Bars, bad));

        Assert.Equal(new[] { built.BreakBar, built.BreakBar + 1, built.BreakBar + 3, built.BreakBar + 4, built.BreakBar + 5 }, LitBars(r));
    }

    [Fact]
    public void AnInconsistentBarOnTheBreakBar_IsNotASignal_TheNextBarThatBreaksIs()
    {
        Built built = Double(new DoubleSpec());
        PatternBarResult[] r = Run(PatternEngineTestBars.Corrupt(built.Bars, built.BreakBar));

        // The candidate read the bad bar between the second peak and the break, so it is dead (interpretation I-5): nothing fires.
        Assert.Empty(LitBars(r));
    }

    // ---------------------------------------------------------------- causality

    [Theory]
    [MemberData(nameof(Sides))]
    public void PrefixInvariance_AppendingBarsNeverChangesAnEarlierResult(bool top)
    {
        Built built = Double(new DoubleSpec { Top = top });
        PatternBarResult[] full = Run(built.Bars);

        for (int cut = 1; cut <= built.Bars.Length; cut++)
        {
            PatternBarResult[] prefix = Run(built.Bars.Take(cut).ToArray());
            Assert.Equal(full.Take(cut), prefix);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PrefixInvariance_OnRandomWalks(int seed)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(seed, 700);
        var p = Relaxed();
        PatternBarResult[] full = Run(bars, p);

        foreach (int cut in new[] { 70, 150, 301, 555, 699 })
        {
            Assert.Equal(full.Take(cut), Run(bars.Take(cut).ToArray(), p).Take(cut));
        }

        Assert.True(seed != 1 || LitBars(full).Length > 0, "the first walk must contain a signal, or the invariance is vacuous");
    }

    [Fact]
    public void RandomWalks_NeverBreakTheOutputContract()
    {
        int signals = 0;
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            CandleData[] bars = PatternEngineTestBars.Walk(seed, 1500);
            PatternBarResult[] r = Run(bars, Relaxed());

            foreach (PatternBarResult x in r)
            {
                if (x.Fires)
                {
                    signals++;
                    Assert.Contains(x.Direction, new[] { -1, 1 });
                    Assert.True((x.Direction == 1 && x.BullishFires) || (x.Direction == -1 && x.BearishFires), "the direction is a side that fires");
                }
                else
                {
                    Assert.Equal(0, x.Direction);
                }
            }
        }

        Assert.True(signals > 0, "the relaxed settings must find a pattern in six walks");
    }

    [Fact]
    public void ABarSeriesShorterThanAnyPattern_GivesNoSignalAndNoException()
    {
        Assert.Empty(LitBars(Run(Array.Empty<CandleData>())));
        Assert.Empty(LitBars(Run(PatternEngineTestBars.Walk(1, 5))));
    }
}
