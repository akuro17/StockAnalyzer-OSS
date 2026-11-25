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
/// Break of Structure / Change of Character engine (plan ChartPatterns56 v2, run V2-5; ledger rows SS_*): the state table, the protected swing, the consumption of swings, the margin, the age limit, the
/// width and volume rules and the causality invariants. The fixtures are real bars with known pivot prices (<see cref="FormationFixtures.FromAnchors"/>, the wick is 0.3 so a pivot is the anchor close plus or
/// minus 0.3). Every "silent" assertion has a control that fires when only the tested number is moved back inside its rule.
/// </summary>
public class StructureShiftEngineTests
{
    // The pivots of the base scenario (anchor close, wick 0.3): H1 (bar 10), L1 (20), H2 (30), L2 (40).
    private const decimal H1 = 120.3m;
    private const decimal L1 = 107.7m;
    private const decimal H2 = 130.3m;
    private const decimal L2 = 117.7m;

    private static readonly (int Index, decimal Price)[] BaseUp = { (0, 100m), (10, 120m), (20, 108m), (30, 130m), (40, 118m) };

    private static CandleData[] Anchored(IEnumerable<(int Index, decimal Price)> anchors, IReadOnlyDictionary<int, long>? volumes = null, decimal wick = Wick)
        => FromAnchors(anchors.ToList(), volumes, wick);

    /// <summary>The up-trend is decided at the first close beyond H1, the first break of H2 is at bar 47 on the close <paramref name="x"/> (flat afterwards); bars 46 and before stay below H2.</summary>
    private static CandleData[] BosUp(decimal x, IReadOnlyDictionary<int, long>? volumes = null, int tailEnd = 60)
        => Anchored(BaseUp.Concat(tailEnd > 47 ? new[] { (46, 129m), (47, x), (tailEnd, x) } : new[] { (46, 129m), (47, x) }), volumes);

    /// <summary>Up-trend, a break of H2 on the way to 145, a fall to 125 and 120, then the close <paramref name="x"/> on bar 67 (flat afterwards).</summary>
    private static CandleData[] UpDown(decimal x, IReadOnlyDictionary<int, long>? volumes = null, int tailEnd = 80)
        => Anchored(BaseUp.Concat(tailEnd > 67 ? new[] { (50, 145m), (60, 125m), (66, 120m), (67, x), (tailEnd, x) } : new[] { (50, 145m), (60, 125m), (66, 120m), (67, x) }), volumes);

    /// <summary>Up, break, fall through the protected low, a lower bounce (128), a lower low and a rally above 145: a bearish Change of Character, a bearish BOS, then a bullish Change of Character.</summary>
    private static CandleData[] Flip()
        => Anchored(BaseUp.Concat(new[] { (50, 145m), (60, 125m), (66, 120m), (67, 116m), (75, 100m), (85, 128m), (95, 104m), (105, 152m), (115, 152m) }));

    private static StructureShiftAnalysis Analyze(IReadOnlyList<CandleData> bars, CoreStructureShiftParameter? p = null)
        => StructureShiftEngine.Analyze(PatternEngineTestBars.Context(bars), p ?? new CoreStructureShiftParameter());

    private static PatternBarResult[] Run(IReadOnlyList<CandleData> bars, StructureShiftKind kind, CoreStructureShiftParameter? p = null)
        => StructureShiftEngine.Run(PatternEngineTestBars.Context(bars), p ?? new CoreStructureShiftParameter(), kind);

    private static int[] LitBars(PatternBarResult[] r) => Enumerable.Range(0, r.Length).Where(i => r[i].Fires).ToArray();

    private static StructureShiftEvent[] EventsOf(StructureShiftAnalysis a, StructureShiftKind kind) => a.Events.Where(e => e.Kind == kind).ToArray();

    private static int FirstBarBelowOrAbove(IReadOnlyList<CandleData> bars, int from, Func<decimal, bool> test)
    {
        for (int i = from; i < bars.Count; i++)
        {
            if (test(bars[i].Close))
            {
                return i;
            }
        }

        throw new InvalidOperationException("no such bar");
    }

    // ---------------------------------------------------------------- the start (spec 2, R-22)

    [Fact]
    public void Start_TheFirstCloseBeyondTheLatestSwing_DecidesTheTrend_WithoutASignal_AndTheProtectionIsTheLatestOppositeSwing()
    {
        CandleData[] bars = BosUp(135m);
        StructureShiftAnalysis a = Analyze(bars);

        int decision = FirstBarBelowOrAbove(bars, 0, c => c > H1);
        Assert.Equal(26, decision);
        Assert.All(Enumerable.Range(0, decision), t => Assert.Equal(0, a.TrendState[t]));
        Assert.All(Enumerable.Range(decision, 21), t => Assert.Equal(1, a.TrendState[t]));
        Assert.Equal(20, a.ProtectedSwingBar[decision]);
        Assert.All(Enumerable.Range(0, decision), t => Assert.Equal(-1, a.ProtectedSwingBar[t]));
        Assert.DoesNotContain(a.Events, e => e.Bar <= decision);
    }

    [Fact]
    public void Start_AfterTheDecision_TheBrokenSwingIsConsumed_SoItNeverGivesABos()
    {
        StructureShiftAnalysis a = Analyze(BosUp(129m));

        Assert.Empty(a.Events);
        Assert.Equal(1, a.TrendState[^1]);
    }

    [Fact]
    public void Start_IsHeldUntilAnOppositeSwingIsConfirmed_R22()
    {
        // A high at 120 (bar 10, confirmed by the fall), a low at 111 (bar 15) and a gap close at 125 on bar 16: the low is confirmed on bar 16 itself, and a swing is used from the NEXT bar (R-32).
        var anchors = new List<(int, decimal)> { (0, 100m), (10, 120m), (15, 111m), (16, 125m), (30, 126m) };
        CandleData[] bars = Anchored(anchors);
        StructureShiftAnalysis a = Analyze(bars);

        Assert.True(bars[16].Close > H1);
        Assert.Equal(0, a.TrendState[16]);
        Assert.Equal(1, a.TrendState[17]);
        Assert.Equal(15, a.ProtectedSwingBar[17]);
    }

    [Fact]
    public void Start_TheDownDecision_IsTheMirror()
    {
        // 20 flat bars first: the ATR amplitude test of the first swing needs a context ATR, which exists only from bar 15 (without them the first leg of the two sides is judged on different bars).
        var anchors = new[] { (0, 100m), (20, 100m) }.Concat(BaseUp.Skip(1).Select(x => (x.Index + 20, x.Price))).Concat(new[] { (66, 129m), (67, 135m), (80, 135m) }).ToList();
        CandleData[] up = Anchored(anchors);
        StructureShiftAnalysis aUp = Analyze(up);
        StructureShiftAnalysis aDown = Analyze(Mirror(up));

        int decision = FirstBarBelowOrAbove(up, 0, c => c > H1);
        Assert.Equal(46, decision);
        Assert.Equal(0, aUp.TrendState[decision - 1]);
        Assert.Equal(1, aUp.TrendState[decision]);
        Assert.Equal(0, aDown.TrendState[decision - 1]);
        Assert.Equal(-1, aDown.TrendState[decision]);
        Assert.Equal(aUp.ProtectedSwingBar[decision], aDown.ProtectedSwingBar[decision]);
        Assert.Equal(40, aDown.ProtectedSwingBar[decision]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Start_Decision_ConsumesAllStrictlyPassedOpenSwings_S2(bool bullish)
    {
        // Two open highs accumulate in TrendState = 0 ([120.3, 110.3]); bar 69 closes at 125, breaking 110.3 and strictly passing 120.3.
        // Without Consume in Decide, the older high (120.3) would remain open and trigger a false BOS on bar 70.
        var anchors = new List<(int, decimal)>
        {
            (0, 100m), (20, 100m),
            (30, 120m), (40, 100m),
            (50, 110m), (60, 102m),
            (68, 109m), (69, 125m), (75, 125m),
        };
        CandleData[] bars = bullish ? Anchored(anchors) : Mirror(Anchored(anchors));
        StructureShiftAnalysis a = Analyze(bars);

        int expectedDir = bullish ? 1 : -1;
        Assert.Equal(0, a.TrendState[68]);
        Assert.Equal(expectedDir, a.TrendState[69]);
        Assert.Equal(60, a.ProtectedSwingBar[69]);
        Assert.Empty(a.Events);
    }

    // ---------------------------------------------------------------- Break of Structure

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bos_FiresOnTheBreakBar_AndMovesTheProtectedSwingToTheOriginOfTheBreakingWave(bool bullish)
    {
        CandleData[] bars = bullish ? BosUp(135m) : Mirror(BosUp(135m));
        StructureShiftAnalysis a = Analyze(bars);

        StructureShiftEvent e = Assert.Single(a.Events);
        Assert.Equal(StructureShiftKind.Bos, e.Kind);
        Assert.Equal(bullish ? 1 : -1, e.Direction);
        Assert.Equal(47, e.Bar);
        Assert.Equal(30, e.SwingBar);
        Assert.Equal(40, e.ProtectedSwingBar);
        Assert.Equal(bullish ? H2 : 200m - H2, e.SwingPrice);
        Assert.Equal(bullish ? L2 : 200m - L2, e.ProtectedSwingPrice);
        Assert.Equal(bullish ? 1 : -1, a.TrendState[47]);
        Assert.Equal(40, a.ProtectedSwingBar[47]);
        Assert.Equal(20, a.ProtectedSwingBar[46]);
    }

    [Fact]
    public void Bos_TheLitBarsAreTheBreakBarAndThePersistence()
    {
        CandleData[] bars = BosUp(135m);

        Assert.Equal(new[] { 47 }, LitBars(Run(bars, StructureShiftKind.Bos)));
        Assert.Equal(new[] { 47, 48, 49, 50 }, LitBars(Run(bars, StructureShiftKind.Bos, new CoreStructureShiftParameter { SignalPersistenceBars = 3 })));
        Assert.Empty(LitBars(Run(bars, StructureShiftKind.Choch)));
    }

    [Fact]
    public void Bos_TheSwingFiresOnce_ADeeperCloseBeyondItGivesNoSecondEvent()
    {
        var anchors = BaseUp.Concat(new[] { (46, 129m), (47, 131m), (52, 140m), (60, 150m) });
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        Assert.Single(EventsOf(a, StructureShiftKind.Bos));
        Assert.Equal(47, a.Events[0].Bar);
    }

    [Theory]
    [InlineData(130.30, false)]
    [InlineData(130.31, true)]
    public void Bos_Margin_Zero_IsAStrictInequality(double close, bool fires)
    {
        StructureShiftAnalysis a = Analyze(BosUp((decimal)close));

        Assert.Equal(fires, a.Events.Count == 1);
        if (fires)
        {
            Assert.Equal(47, a.Events[0].Bar);
        }
    }

    [Fact]
    public void Bos_Margin_AboveZero_IsThePercentOfTheLevel_WhenItIsTheLargerTerm()
    {
        // 1 percent of 130.3 = 1.303: the close must be above 131.603; the ATR term (0.10 ATR, about 0.3) is smaller.
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = 1m };

        Assert.Empty(Analyze(BosUp(131.60m), p).Events);
        Assert.Equal(47, Assert.Single(Analyze(BosUp(131.61m), p).Events).Bar);
    }

    [Fact]
    public void Bos_Margin_AboveZero_IsTheAtrTerm_WhenItIsTheLargerTerm()
    {
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = 0.01m };
        CandleData[] probe = BosUp(135m);
        Assert.True(PatternMath.TryAtrCtx(PatternEngineTestBars.Context(probe), 46, PatternSpecConstants.AtrPeriod, out decimal atr, out _));
        decimal margin = PatternSpecConstants.StructureShiftBreakoutAtrMultiplier * atr;
        Assert.True(margin > 0.01m / 100m * H2, "the ATR term is the larger one in this fixture");

        // The probe is the last bar (the margin follows the ATR of the bar before, so a flat tail would change it); one step on each side of level + margin.
        decimal level = H2 + margin;
        Assert.Empty(Analyze(BosUp(Math.Round(level - 0.0001m, 4), tailEnd: 47), p).Events);
        Assert.Equal(47, Assert.Single(Analyze(BosUp(Math.Round(level + 0.0001m, 4), tailEnd: 47), p).Events).Bar);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Bos_TheAgeLimit_IsMeasuredFromTheConfirmationOfTheSwing(int extraBars)
    {
        // The swing H2 is confirmed on bar c; the close beyond it on bar c + 100 is inside T_max = 100, on c + 101 outside (the swing is ignored, not consumed).
        CandleData[] probe = BosUp(135m);
        int c = CausalZigZag.Extract(PatternEngineTestBars.Context(probe), new CausalZigZagOptions(5m, 1.5m, 0)).Single(pv => pv.Index == 30).ConfirmedAtIndex;
        int fireBar = c + 100 + extraBars;
        var anchors = BaseUp.Concat(new[] { (46, 129m), (fireBar - 1, 129m), (fireBar, 135m), (fireBar + 10, 135m) });
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        if (extraBars == 0)
        {
            Assert.Equal(fireBar, Assert.Single(EventsOf(a, StructureShiftKind.Bos)).Bar);
        }
        else
        {
            Assert.Empty(EventsOf(a, StructureShiftKind.Bos));
        }
    }

    [Fact]
    public void Bos_TheAgeLimit_FollowsTheParameter()
    {
        var p = new CoreStructureShiftParameter { MaxPivotAgeBars = 10 };
        CandleData[] probe = BosUp(135m);
        int c = CausalZigZag.Extract(PatternEngineTestBars.Context(probe), new CausalZigZagOptions(5m, 1.5m, 0)).Single(pv => pv.Index == 30).ConfirmedAtIndex;

        // A slow rise from L2 (bar 40) to 129 just below H2, then the close beyond it 10 (in time) or 11 (late) bars after the confirmation.
        StructureShiftAnalysis late = Analyze(Anchored(BaseUp.Concat(new[] { (c + 10, 129m), (c + 11, 135m), (c + 20, 135m) })), p);
        StructureShiftAnalysis inTime = Analyze(Anchored(BaseUp.Concat(new[] { (c + 9, 129m), (c + 10, 135m), (c + 20, 135m) })), p);

        Assert.Empty(EventsOf(late, StructureShiftKind.Bos));
        Assert.Equal(c + 10, Assert.Single(EventsOf(inTime, StructureShiftKind.Bos)).Bar);
    }

    [Fact]
    public void Bos_ASwingIsUsedFromTheBarAfterItsConfirmation_R32()
    {
        // The protection candidate of the BOS update is a low confirmed on the break bar itself (a gap bar): it is not used (strict), the previous protected swing stays (R-23, none: keep).
        // High 130 on bar 30, a fall to 122 on bar 34, a gap close at 133 on bar 35: the low of bar 34 is confirmed on bar 35.
        var anchors = new List<(int, decimal)> { (0, 100m), (10, 120m), (20, 108m), (30, 130m), (34, 122m), (35, 133m), (45, 133m) };
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        StructureShiftEvent e = Assert.Single(a.Events);
        Assert.Equal(35, e.Bar);
        Assert.Equal(30, e.SwingBar);
        Assert.Equal(20, e.ProtectedSwingBar);
    }

    // ---------------------------------------------------------------- the protected swing (R-23)

    [Fact]
    public void Protection_IsTheLowestConfirmedLowAfterTheBrokenHigh()
    {
        // Two lows between H2 (bar 30) and the break: 112 (bar 38) and 120 (bar 50); the higher one is not the protected swing.
        var anchors = new List<(int, decimal)> { (0, 100m), (10, 120m), (20, 108m), (30, 130m), (38, 112m), (44, 126m), (50, 120m), (58, 134m), (70, 134m) };
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        StructureShiftEvent e = Assert.Single(EventsOf(a, StructureShiftKind.Bos), ev => ev.SwingBar == 30);
        Assert.Equal(38, e.ProtectedSwingBar);
        Assert.Equal(111.7m, e.ProtectedSwingPrice);
    }

    [Fact]
    public void Protection_EqualLows_TheLaterPivotWins()
    {
        var anchors = new List<(int, decimal)> { (0, 100m), (10, 120m), (20, 108m), (30, 130m), (38, 112m), (44, 126m), (50, 112m), (58, 134m), (70, 134m) };
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        StructureShiftEvent e = Assert.Single(EventsOf(a, StructureShiftKind.Bos), ev => ev.SwingBar == 30);
        Assert.Equal(50, e.ProtectedSwingBar);
        Assert.Equal(111.7m, e.ProtectedSwingPrice);
    }

    [Fact]
    public void Protection_TheBullishAndBearishBosAreMirrorImages()
    {
        var anchors = new List<(int, decimal)> { (0, 100m), (10, 120m), (20, 108m), (30, 130m), (38, 112m), (44, 126m), (50, 120m), (58, 134m), (70, 134m) };
        CandleData[] bars = Anchored(anchors);

        StructureShiftEvent up = Assert.Single(EventsOf(Analyze(bars), StructureShiftKind.Bos), ev => ev.SwingBar == 30);
        StructureShiftEvent down = Assert.Single(EventsOf(Analyze(Mirror(bars)), StructureShiftKind.Bos), ev => ev.SwingBar == 30);

        Assert.Equal((up.Bar, up.SwingBar, up.ProtectedSwingBar), (down.Bar, down.SwingBar, down.ProtectedSwingBar));
        Assert.Equal(1, up.Direction);
        Assert.Equal(-1, down.Direction);
    }

    // ---------------------------------------------------------------- Change of Character

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Choch_FiresOnTheBreakOfTheProtectedSwing_AndFlipsTheTrend(bool bearish)
    {
        CandleData[] bars = bearish ? UpDown(116m) : Mirror(UpDown(116m));
        StructureShiftAnalysis a = Analyze(bars);

        StructureShiftEvent e = Assert.Single(EventsOf(a, StructureShiftKind.Choch));
        Assert.Equal(67, e.Bar);
        Assert.Equal(bearish ? -1 : 1, e.Direction);
        Assert.Equal(40, e.SwingBar);
        Assert.Equal(50, e.ProtectedSwingBar);
        Assert.Equal(bearish ? -1 : 1, a.TrendState[67]);
        Assert.Equal(bearish ? 1 : -1, a.TrendState[66]);
        Assert.Equal(new[] { 67 }, LitBars(Run(bars, StructureShiftKind.Choch)));
    }

    [Theory]
    [InlineData(117.70, false)]
    [InlineData(117.69, true)]
    public void Choch_Margin_Zero_IsAStrictInequality(double close, bool fires)
    {
        StructureShiftAnalysis a = Analyze(UpDown((decimal)close));

        Assert.Equal(fires, EventsOf(a, StructureShiftKind.Choch).Length == 1);
        Assert.Equal(fires ? -1 : 1, a.TrendState[^1]);
    }

    [Fact]
    public void Choch_Margin_AboveZero_IsThePercentOfTheProtectedSwing()
    {
        // 1 percent of 117.7 = 1.177: the close must be below 116.523.
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = 1m };

        Assert.Empty(EventsOf(Analyze(UpDown(116.53m), p), StructureShiftKind.Choch));
        Assert.Equal(67, Assert.Single(EventsOf(Analyze(UpDown(116.52m), p), StructureShiftKind.Choch)).Bar);
    }

    [Fact]
    public void Choch_Margin_AboveZero_IsTheAtrTerm_WhenItIsTheLargerTerm()
    {
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = 0.01m };
        Assert.True(PatternMath.TryAtrCtx(PatternEngineTestBars.Context(UpDown(100m)), 66, PatternSpecConstants.AtrPeriod, out decimal atr, out _));
        decimal level = L2 - PatternSpecConstants.StructureShiftBreakoutAtrMultiplier * atr;
        Assert.True(PatternSpecConstants.StructureShiftBreakoutAtrMultiplier * atr > 0.01m / 100m * L2);

        Assert.Empty(EventsOf(Analyze(UpDown(Math.Round(level + 0.0001m, 4), tailEnd: 67), p), StructureShiftKind.Choch));
        Assert.Equal(67, Assert.Single(EventsOf(Analyze(UpDown(Math.Round(level - 0.0001m, 4), tailEnd: 67), p), StructureShiftKind.Choch)).Bar);
    }

    [Fact]
    public void Choch_IsCheckedBeforeBos_TheyNeverShareABarInOneTrendState()
    {
        // The state table at the bars of every event of a walk is in StateTable_HoldsOnRandomWalks; here the order on a constructed bar: a close that is below the protected swing is a CHoCH, never a BOS.
        StructureShiftAnalysis a = Analyze(UpDown(116m));

        Assert.Equal(new[] { StructureShiftKind.Bos, StructureShiftKind.Choch }, a.Events.Select(e => e.Kind).ToArray());
        Assert.Equal(a.Events.Count, a.Events.Select(e => e.Bar).Distinct().Count());
    }

    [Fact]
    public void Choch_TheProtectedSwingNeverExpiresByAge_R24()
    {
        // The protected low (bar 40) is 150+ bars old when the close falls below it; a BOS reference of that age would be ignored.
        var anchors = BaseUp.Concat(new[] { (50, 145m), (60, 125m), (210, 125m), (211, 116m), (225, 116m) });
        StructureShiftAnalysis a = Analyze(Anchored(anchors));

        StructureShiftEvent e = Assert.Single(EventsOf(a, StructureShiftKind.Choch));
        Assert.Equal(211, e.Bar);
        Assert.Equal(40, e.SwingBar);
    }

    [Fact]
    public void Choch_TheNewProtectedSwing_IsTheHighestHighAfterTheBrokenLow_S3()
    {
        StructureShiftAnalysis a = Analyze(Flip());

        StructureShiftEvent down = EventsOf(a, StructureShiftKind.Choch).First();
        Assert.Equal(-1, down.Direction);
        Assert.Equal(50, down.ProtectedSwingBar);
        Assert.Equal(145.3m, down.ProtectedSwingPrice);
    }

    [Fact]
    public void Choch_AfterTheFlipTheStateMachineKeepsTracking_ABearishBosThenABullishChoch()
    {
        StructureShiftAnalysis a = Analyze(Flip());

        Assert.Equal(
            new[] { (StructureShiftKind.Bos, 1), (StructureShiftKind.Choch, -1), (StructureShiftKind.Bos, -1), (StructureShiftKind.Choch, 1) },
            a.Events.Select(e => (e.Kind, e.Direction)).ToArray());
        Assert.Equal(20, a.Events[2].SwingBar);

        StructureShiftEvent up = a.Events[3];
        Assert.Equal(50, up.SwingBar);
        Assert.Equal(75, up.ProtectedSwingBar);
        Assert.Equal(99.7m, up.ProtectedSwingPrice);
    }

    [Fact]
    public void Sweep_ABreakConsumesTheSwingsThatTheCloseHasPassed_S2()
    {
        // The lower high of bar 85 (128.3) is older-than-the-break and passed by the bullish Change of Character at 147+: without the sweep it would be the "latest unconsumed high" and give a BOS on the next bar.
        StructureShiftAnalysis a = Analyze(Flip());

        int chochBar = a.Events[3].Bar;
        Assert.DoesNotContain(a.Events, e => e.Kind == StructureShiftKind.Bos && e.Bar > chochBar);
        Assert.DoesNotContain(a.Events, e => e.SwingBar == 85);
    }

    [Theory]
    [InlineData(true, 0.0, true)]
    [InlineData(true, 0.0, false)]
    [InlineData(true, 1.0, true)]
    [InlineData(true, 1.0, false)]
    [InlineData(false, 0.0, true)]
    [InlineData(false, 0.0, false)]
    [InlineData(false, 1.0, true)]
    [InlineData(false, 1.0, false)]
    public void Consume_StrictCloseBoundary_NoMargin_Q_V2_5b(bool bullish, double marginPct, bool equalToOlderSwing)
    {
        // Older open swing at bar 50 = 140.0000m (anchor 139.7 + wick 0.3), recent open swing at bar 70 = 132.0m (anchor 131.7 + wick 0.3).
        // Break bar 90 breaks 132.0m; its close is either exact 140.0000m (stays unconsumed) or 140.0001m (consumed by strict close without margin, even when marginPct = 1%).
        decimal olderSwing = 140.0000m;
        decimal bar90Close = equalToOlderSwing ? olderSwing : olderSwing + 0.0001m;
        decimal bar91Close = marginPct > 0.0 ? 145.0m : 140.0001m;
        var anchors = new List<(int, decimal)>
        {
            (0, 100m), (20, 100m),
            (30, 120m), (40, 108m), (46, 129m),
            (50, 139.7m), (60, 125m),
            (70, 131.7m), (80, 124m),
            (89, 131m), (90, bar90Close), (91, bar91Close), (95, bar91Close),
        };
        CandleData[] bars = bullish ? Anchored(anchors) : Mirror(Anchored(anchors));
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = (decimal)marginPct };
        StructureShiftEvent[] bos = EventsOf(Analyze(bars, p), StructureShiftKind.Bos);

        Assert.Contains(bos, e => e.Bar == 90 && e.SwingBar == 70);
        if (equalToOlderSwing)
        {
            Assert.Contains(bos, e => e.Bar == 91 && e.SwingBar == 50);
        }
        else
        {
            Assert.DoesNotContain(bos, e => e.SwingBar == 50);
        }
    }

    [Fact]
    public void Choch_StructureWidth_ReEvaluatedEachBar_FiresLateWhenAtrDrops_S5()
    {
        // Protected low L2 (bar 40, 117.7) and H2 (bar 30, 130.3) have width 12.6 -> width / 2 = 6.3.
        // Bars 61..74 stay flat at 125 with wide wicks (7m -> TR = 14m), inflating ATR_ctx above 6.3 without breaking L2 or H_50.
        // Bar 75 closes at 115 (< 117.7) and stays at 115 with normal 0.3m wicks: CHoCH is blocked while ATR_ctx(t-1, 14) > 6.3 and fires late on the first bar where ATR_ctx(t-1, 14) <= 6.3.
        CandleData[] bars = Anchored(BaseUp.Concat(new[] { (50, 145m), (60, 125m), (74, 125m), (75, 115m), (95, 115m) }));
        for (int i = 61; i <= 74; i++)
        {
            bars[i] = PatternEngineTestBars.Bar(i, 125m, 132m, 118m, 125m);
        }

        PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);
        StructureShiftAnalysis a = Analyze(bars);

        decimal width = H2 - L2;
        int expectedFireBar = Enumerable.Range(75, bars.Length - 75).First(t =>
            PatternMath.TryAtrCtx(ctx, t - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _) && width >= 2m * atr);

        Assert.Equal(84, expectedFireBar);
        for (int t = 75; t < expectedFireBar; t++)
        {
            Assert.Equal(1, a.TrendState[t]);
            Assert.Equal(40, a.ProtectedSwingBar[t]);
        }

        StructureShiftEvent choch = Assert.Single(EventsOf(a, StructureShiftKind.Choch));
        Assert.Equal(expectedFireBar, choch.Bar);
        Assert.Equal(40, choch.SwingBar);
        Assert.Equal(-1, a.TrendState[expectedFireBar]);
    }

    [Theory]
    [InlineData(5.99, true)]
    [InlineData(6.00, true)]
    [InlineData(6.01, false)]
    public void Choch_StructureWidth_MustBeAtLeastTwoAtr(double wick, bool fires)
    {
        // Flat bars before the probe make ATR_ctx = 2 * wick exactly. The protected low and the high before it are 12 + 2 * wick apart, so the width is >= 2 ATR (= 4 * wick) up to a wick of 6.
        var anchors = BaseUp.Concat(new[] { (50, 145m), (60, 125m), (80, 125m), (81, 100m), (95, 100m) });
        var p = new CoreStructureShiftParameter { MinSwingAtrMultiplier = 0m };
        StructureShiftAnalysis a = Analyze(Anchored(anchors, wick: (decimal)wick), p);

        Assert.Equal(1, a.TrendState[80]);
        Assert.Contains(a.Events, e => e.Kind == StructureShiftKind.Bos);
        Assert.Equal(fires, EventsOf(a, StructureShiftKind.Choch).Length == 1);
        Assert.Equal(fires ? -1 : 1, a.TrendState[^1]);
    }

    [Fact]
    public void Choch_WhenTheWidthFails_TheProtectedSwingStaysAndTheTrendDoesNotFlip_S5()
    {
        var anchors = BaseUp.Concat(new[] { (50, 145m), (60, 125m), (80, 125m), (81, 100m), (95, 100m) });
        StructureShiftAnalysis a = Analyze(Anchored(anchors, wick: 7m), new CoreStructureShiftParameter { MinSwingAtrMultiplier = 0m });

        Assert.Empty(EventsOf(a, StructureShiftKind.Choch));
        Assert.Equal(1, a.TrendState[^1]);
        Assert.Equal(40, a.ProtectedSwingBar[^1]);
    }

    // ---------------------------------------------------------------- volume (optional)

    private static Dictionary<int, long> Volume(int bar, long volume) => new() { [bar] = volume };

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1199, false)]
    public void Bos_Volume_NeedsTheMultipleOfTheAverageOfThe20BarsBefore(long volume, bool fires)
    {
        // Every bar has volume 1000; the multiple is 1.20, so the break bar needs 1200.
        var p = new CoreStructureShiftParameter { RequireVolumeConfirmation = true, BreakoutVolumeMultiplier = 1.2m };
        StructureShiftAnalysis a = Analyze(BosUp(135m, Volume(47, volume)), p);

        Assert.Equal(fires, a.Events.Count == 1);
    }

    [Fact]
    public void Bos_Volume_AFailedBreakIsNotConsumed_ALaterBarWithVolumeFires()
    {
        // The first close beyond H2 has too little volume; the next bar (still beyond) has enough: it fires there, once (known behaviour, the same as I-1 of the formations).
        var p = new CoreStructureShiftParameter { RequireVolumeConfirmation = true, BreakoutVolumeMultiplier = 1.2m };
        StructureShiftAnalysis a = Analyze(BosUp(135m, new Dictionary<int, long> { [47] = 500, [48] = 1500 }), p);

        Assert.Equal(48, Assert.Single(a.Events).Bar);
    }

    [Fact]
    public void Volume_OffByDefault_AndAnInstrumentWithoutVolumePasses()
    {
        Assert.False(new CoreStructureShiftParameter().RequireVolumeConfirmation);
        Assert.Single(Analyze(BosUp(135m, Volume(47, 1))).Events);

        CandleData[] noVolume = BosUp(135m).Select(b => b with { Volume = 0 }).ToArray();
        Assert.Single(Analyze(noVolume, new CoreStructureShiftParameter { RequireVolumeConfirmation = true }).Events);
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1199, false)]
    public void Choch_Volume_NeedsTheMultipleOfTheAverage(long volume, bool fires)
    {
        var p = new CoreStructureShiftParameter { RequireVolumeConfirmation = true, BreakoutVolumeMultiplier = 1.2m };
        // The volume rule applies to the BOS (bar 45) as well, or the protected swing would still be the first one.
        StructureShiftAnalysis a = Analyze(UpDown(116m, new Dictionary<int, long> { [45] = 1200, [67] = volume }), p);

        Assert.Single(EventsOf(a, StructureShiftKind.Bos));
        Assert.Equal(fires, EventsOf(a, StructureShiftKind.Choch).Length == 1);
    }

    // ---------------------------------------------------------------- direction mode, persistence

    [Fact]
    public void DirectionMode_NarrowsTheOutputOnly_TheStateFollowsBothSides()
    {
        CandleData[] bars = Flip();
        int[] both = LitBars(Run(bars, StructureShiftKind.Choch));
        int[] bullish = LitBars(Run(bars, StructureShiftKind.Choch, new CoreStructureShiftParameter { DirectionMode = PatternDirectionMode.BullishOnly }));
        int[] bearish = LitBars(Run(bars, StructureShiftKind.Choch, new CoreStructureShiftParameter { DirectionMode = PatternDirectionMode.BearishOnly }));
        StructureShiftAnalysis a = Analyze(bars);

        Assert.Equal(new[] { 67, a.Events[3].Bar }, both);
        Assert.Equal(new[] { a.Events[3].Bar }, bullish);
        Assert.Equal(new[] { 67 }, bearish);

        // The bullish change of character at the end exists only because the bearish one flipped the trend first: the filter did not remove it from the state.
        PatternBarResult[] r = Run(bars, StructureShiftKind.Choch, new CoreStructureShiftParameter { DirectionMode = PatternDirectionMode.BullishOnly });
        Assert.True(r[a.Events[3].Bar].BullishFires);
        Assert.False(r[67].BearishFires);
    }

    [Fact]
    public void Persistence_TheHoldIsNotCutByAnotherShift_AndBothSidesCanBeLitOnOneBar()
    {
        // Bearish BOS and a following bullish BOS... within the hold of the earlier one: the two side series are independent (output schema).
        CandleData[] bars = Flip();
        StructureShiftAnalysis a = Analyze(bars);
        StructureShiftEvent first = a.Events[0];
        var p = new CoreStructureShiftParameter { SignalPersistenceBars = 50 };
        PatternBarResult[] r = Run(bars, StructureShiftKind.Bos, p);

        Assert.True(r[first.Bar].BullishFires);
        Assert.True(r[first.Bar + 50].BullishFires || first.Bar + 50 >= r.Length);
        int bearishBos = a.Events.First(e => e.Kind == StructureShiftKind.Bos && e.Direction < 0).Bar;
        Assert.True(r[bearishBos].BearishFires);
        Assert.True(r[bearishBos].BullishFires, "the bullish BOS of bar 45 is still held (persistence 50) when the bearish BOS fires");
        Assert.Equal(-1, r[bearishBos].Direction);
    }

    // ---------------------------------------------------------------- invalid bars (R-16)

    [Fact]
    public void InvalidBar_AtTheBreakBar_IsNoBreakBar_TheNextValidBarFires()
    {
        CandleData[] bars = PatternEngineTestBars.Corrupt(BosUp(135m), 47);
        StructureShiftAnalysis a = Analyze(bars);
        PatternBarResult[] r = Run(bars, StructureShiftKind.Bos);

        Assert.Equal(48, Assert.Single(a.Events).Bar);
        Assert.False(r[47].Fires);
        Assert.Equal(new[] { 48 }, LitBars(r));
    }

    [Fact]
    public void InvalidBar_InTheAtrWindowOfTheMargin_BlocksTheBreakWhileTheWindowCoversIt()
    {
        // With a margin above zero the ATR_ctx(t-1, 14) window [t-15, t-1] must be clean: an inconsistent bar 45 blocks the breaks of bars 47..60 and lets the first bar with t - 15 > 45 fire.
        var p = new CoreStructureShiftParameter { BreakoutMarginPercent = 1m };
        CandleData[] clean = BosUp(135m, tailEnd: 75);
        CandleData[] bars = PatternEngineTestBars.Corrupt(clean, 45);

        Assert.Equal(47, Assert.Single(Analyze(clean, p).Events).Bar);
        Assert.Equal(61, Assert.Single(Analyze(bars, p).Events).Bar);
    }

    [Fact]
    public void InvalidBar_WithMarginZero_TheAtrWindowIsNotRead()
    {
        CandleData[] bars = PatternEngineTestBars.Corrupt(BosUp(135m, tailEnd: 75), 45);

        Assert.Equal(47, Assert.Single(Analyze(bars).Events).Bar);
    }

    [Fact]
    public void InvalidBar_NeverChangesTheStateOnItsOwn()
    {
        // A bad bar in a flat stretch far from every break: the events are those of the clean series.
        var anchors = BaseUp.Concat(new[] { (46, 129m), (47, 135m), (100, 135m) });
        CandleData[] clean = Anchored(anchors);
        CandleData[] bars = PatternEngineTestBars.Corrupt(clean, 80);

        Assert.Equal(Analyze(clean).Events, Analyze(bars).Events);
    }

    // ---------------------------------------------------------------- short and flat input

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void ShortSeries_GiveNoEventAndNoException(int count)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(1, Math.Max(count, 1)).Take(count).ToArray();

        StructureShiftAnalysis a = Analyze(bars);
        Assert.Empty(a.Events);
        Assert.Equal(count, a.TrendState.Length);
        Assert.Equal(count, Run(bars, StructureShiftKind.Bos).Length);
    }

    [Fact]
    public void AFlatSeries_GivesNoEvent()
    {
        CandleData[] bars = Enumerable.Range(0, 200).Select(i => PatternEngineTestBars.Bar(i, 100, 101, 99, 100)).ToArray();

        StructureShiftAnalysis a = Analyze(bars);
        Assert.Empty(a.Events);
        Assert.All(a.TrendState, s => Assert.Equal(0, s));
    }

    // ---------------------------------------------------------------- invariants on random walks

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(s => new object[] { s });

    private static CoreStructureShiftParameter[] ParameterSets() => new[]
    {
        new CoreStructureShiftParameter(),
        new CoreStructureShiftParameter { ZigZagThresholdPercent = 2m, MinSwingAtrMultiplier = 0m },
        new CoreStructureShiftParameter { ZigZagThresholdPercent = 2m, MinSwingAtrMultiplier = 0.5m, BreakoutMarginPercent = 0.2m, MaxPivotAgeBars = 30 },
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void StateTable_HoldsOnRandomWalks(int seed)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(seed, 1500);
        int total = 0;

        foreach (CoreStructureShiftParameter p in ParameterSets())
        {
            StructureShiftAnalysis a = Analyze(bars, p);
            var pivots = CausalZigZag.Extract(PatternEngineTestBars.Context(bars), new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, PatternSpecConstants.StructureShiftFractalBars));

            Assert.Equal(a.Events.Select(e => e.Bar).Distinct().Count(), a.Events.Count);
            Assert.Equal(a.Events.Select(e => (e.Kind, e.Direction, e.SwingBar)).Distinct().Count(), a.Events.Count);

            foreach (StructureShiftEvent e in a.Events)
            {
                int before = e.Bar == 0 ? 0 : a.TrendState[e.Bar - 1];
                Assert.NotEqual(0, before);
                if (e.Kind == StructureShiftKind.Bos)
                {
                    Assert.Equal(before, e.Direction);
                    Assert.Equal(e.Direction, a.TrendState[e.Bar]);
                }
                else
                {
                    Assert.Equal(-before, e.Direction);
                    Assert.Equal(e.Direction, a.TrendState[e.Bar]);
                }

                // R-32: the swing is confirmed before the break bar (strict), and a BOS is not older than T_max.
                ConfirmedPivot swing = pivots.Single(pv => pv.Index == e.SwingBar);
                Assert.True(swing.ConfirmedAtIndex < e.Bar);
                if (e.Kind == StructureShiftKind.Bos)
                {
                    Assert.True(e.Bar - swing.ConfirmedAtIndex <= p.MaxPivotAgeBars);
                }

                // The break is a close beyond the swing.
                Assert.True(e.Direction * (bars[e.Bar].Close - e.SwingPrice) > 0m);

                // The protected swing is an opposite swing that is confirmed already and lies on the right side of the price.
                ConfirmedPivot prot = pivots.Single(pv => pv.Index == e.ProtectedSwingBar);
                Assert.True(prot.ConfirmedAtIndex < e.Bar);
                Assert.Equal(e.Direction < 0, prot.IsHigh);
            }

            // The trend state changes only at a CHoCH (or the first decision from 0).
            for (int t = 1; t < bars.Length; t++)
            {
                if (a.TrendState[t] != a.TrendState[t - 1] && a.TrendState[t - 1] != 0)
                {
                    Assert.Contains(a.Events, e => e.Bar == t && e.Kind == StructureShiftKind.Choch);
                }
            }

            total += a.Events.Count;
        }

        Assert.True(total > 0, "the walks must contain shifts, or the invariants above are vacuous");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void PrefixInvariance_AppendingBarsNeverChangesAnEarlierResult(int seed)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(seed, 700);

        foreach (CoreStructureShiftParameter p in ParameterSets())
        {
            foreach (StructureShiftKind kind in new[] { StructureShiftKind.Bos, StructureShiftKind.Choch })
            {
                PatternBarResult[] full = Run(bars, kind, p);
                foreach (int cut in new[] { 20, 57, 130, 301, 520 })
                {
                    PatternBarResult[] prefix = Run(bars.Take(cut).ToArray(), kind, p);
                    for (int t = 0; t < cut; t++)
                    {
                        // The persistence is clamped at the end of the series, so a hold that reaches past the cut compares only up to it.
                        Assert.Equal((full[t].BullishFires, full[t].BearishFires, full[t].Direction), (prefix[t].BullishFires, prefix[t].BearishFires, prefix[t].Direction));
                    }
                }
            }
        }
    }

    [Fact]
    public void PrefixInvariance_AnalysisEventsAreAPrefixOfTheLongerAnalysis()
    {
        CandleData[] bars = PatternEngineTestBars.Walk(3, 900);
        var p = new CoreStructureShiftParameter { ZigZagThresholdPercent = 2m, MinSwingAtrMultiplier = 0m };
        StructureShiftAnalysis full = Analyze(bars, p);

        foreach (int cut in new[] { 100, 333, 640 })
        {
            StructureShiftAnalysis prefix = Analyze(bars.Take(cut).ToArray(), p);
            Assert.Equal(full.Events.Where(e => e.Bar < cut).ToArray(), prefix.Events.ToArray());
            Assert.Equal(full.TrendState.Take(cut), prefix.TrendState);
            Assert.Equal(full.ProtectedSwingBar.Take(cut), prefix.ProtectedSwingBar);
        }
    }

    [Fact]
    public void Convergence_TheLast100BarsAgreeWhetherTheSeriesStartsAtBar0Or1500BarsLater_PV5()
    {
        // PV-5: the state machine starts undecided; 500 bars of history must be enough for the last 100 bars to be the same as with 2,000 bars. Reported by the count of differences.
        int differences = 0;
        int compared = 0;

        foreach (int seed in Enumerable.Range(1, 12))
        {
            CandleData[] bars = PatternEngineTestBars.Walk(seed, 2000);
            CandleData[] tail = bars.Skip(1500).ToArray();

            foreach (StructureShiftKind kind in new[] { StructureShiftKind.Bos, StructureShiftKind.Choch })
            {
                PatternBarResult[] full = Run(bars, kind);
                PatternBarResult[] shorter = Run(tail, kind);
                for (int i = 400; i < 500; i++)
                {
                    compared++;
                    if ((full[1500 + i].BullishFires, full[1500 + i].BearishFires, full[1500 + i].Direction) != (shorter[i].BullishFires, shorter[i].BearishFires, shorter[i].Direction))
                    {
                        differences++;
                    }
                }
            }
        }

        Assert.Equal(2400, compared);
        Assert.Equal(0, differences);
    }

    [Fact]
    public void TheDefaultSettings_FindShiftsOnRandomWalks_SoTheInvariantTestsAreNotVacuous()
    {
        int bos = 0, choch = 0;
        foreach (int seed in Enumerable.Range(1, 12))
        {
            StructureShiftAnalysis a = Analyze(PatternEngineTestBars.Walk(seed, 1000));
            bos += EventsOf(a, StructureShiftKind.Bos).Length;
            choch += EventsOf(a, StructureShiftKind.Choch).Length;
        }

        Assert.True(bos > 12 && choch > 12, $"BOS {bos}, CHoCH {choch}");
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => StructureShiftEngine.Run(PatternEngineTestBars.Context(PatternEngineTestBars.Walk(1, 200)), new CoreStructureShiftParameter(), StructureShiftKind.Bos, cts.Token));
    }

    [Fact]
    public void Constants_AreTheSpecValues()
    {
        Assert.Equal(20, PatternSpecConstants.StructureShiftMinBars);
        Assert.Equal(0, PatternSpecConstants.StructureShiftFractalBars);
        Assert.Equal(0.10m, PatternSpecConstants.StructureShiftBreakoutAtrMultiplier);
        Assert.Equal(2.0m, PatternSpecConstants.StructureShiftChochWidthAtrMultiplier);
    }
}
