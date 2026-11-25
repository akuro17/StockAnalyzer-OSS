using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Spec-oracle unit and boundary tests for <see cref="HigherHighHigherLowEngine"/>
/// (<c>02_Market_Structure\03_higher_high_higher_low.md</c>, Plan R-25, U2-11 A;
/// ledger <c>docs/sa_pattern_literal_ledger_V2-10_CandlesPart3</c>, rows <c>HH_*</c>).
/// </summary>
public class HigherHighHigherLowEngineTests
{
    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static CandleData[] BuildAnchorSeries((int Index, decimal Close)[] anchors, decimal wick = 0.2m)
        => FormationFixtures.FromAnchors(anchors, wick: wick);

    private static PatternBarResult[] RunHhHl(IEnumerable<CandleData> bars, CoreStructureTrendParameter? p = null, int side = 1)
        => HigherHighHigherLowEngine.Run(PatternEngineTestBars.Context(bars.ToArray()), p ?? new CoreStructureTrendParameter(), side);

    /// <summary>
    /// Standard HH/HL rising structure after a 20-bar flat base:
    /// L1(24)=100 -> H1(30)=110 -> L2(36)=104 -> H2(42)=118 -> bounce confirms L2 at bar 40 &amp; H2 at bar 46 (when price dips 118->114).
    /// After confirmation: total span 18 in [10,120], H2-H1=12, L2-L1=12 in [4,60], C > 104 stays true until C <= 104!
    /// </summary>
    private static CandleData[] StandardRisingWithBreakdownTail() => BuildAnchorSeries(new[]
    {
        (0, 100m),
        (18, 100m),
        (24, 94m),  // deep L1 (wick Low = 93.8)
        (30, 110m), // H1 (High = 110.2)
        (36, 102m), // L2 (Low = 101.8 > L1)
        (42, 120m), // H2 (High = 120.2 > H1)
        (46, 114m), // >= 3% pullback confirms H2! Last 4 confirmed pivots = (L1=93.8, H1=110.2, L2=101.8, H2=120.2)
        (52, 115m), // still above L2=101.8
        (58, 101.8m), // closes right at L2 -> C_t <= L2 invalidates immediately!
        (60, 100m),
    });

    [Fact]
    public void HH_MinBars_Is20_NullBefore20_AndWarmupIs500()
    {
        var param = new CoreStructureTrendParameter();
        Assert.Equal(20, PatternSpecConstants.StructureTrendMinBars);
        Assert.Equal(500, param.GetRequiredWarmupBars());

        CandleData[] walk = PatternEngineTestBars.Walk(2, 40);
        IIndicatorResult res = new IndicatorFactory().Create(IndicatorType.MarketStructureHhHl)!.Calculate(ToCore(walk));
        Assert.True(res.IsSuccessful, res.ErrorMessage);
        IReadOnlyList<decimal?> main = res.GetSeries(IndicatorResult.MainSeriesName);
        for (int t = 0; t < 19; t++)
        {
            Assert.Null(main[t]);
        }

        Assert.NotNull(main[19]);
    }

    [Fact]
    public void HH_RisingConfirmedSwings_FiresUntilBreakdownOrFlatHigh()
    {
        CandleData[] rising = StandardRisingWithBreakdownTail();
        PatternBarResult[] res = RunHhHl(rising);

        // Before H2 confirms (bar < 46), L2 was only just confirmed and only 3 pivots? Or (H0,L1,H1,L2) not HH+HL -> 0m;
        // once H2 confirms and C > L2, fires; at bar 58 (close = 101.8 == L2), immediately drops to false!
        Assert.True(res.Skip(46).Take(7).All(r => r.BullishFires), "Should stay 1 while HH/HL holds and C_t > L2");
        Assert.False(res[58].BullishFires, "Close == L2 must invalidate immediately");
        Assert.False(res[60].BullishFires, "Close < L2 must stay invalid");

        // Flat or lower new confirmed high (H_new <= H2) invalidates immediately!
        CandleData[] flatHigh = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m), (24, 94m), (30, 110m), (36, 102m), (42, 120m), (46, 114m),
            (52, 110m), // L3
            (58, 119m), // H3 <= H2=120.2!
            (63, 114m), // pullback confirms H3 <= H2
        });
        PatternBarResult[] flatRes = RunHhHl(flatHigh);
        Assert.True(flatRes[47].BullishFires);
        Assert.False(flatRes[^1].BullishFires);
    }

    [Fact]
    public void HH_SpanAndIntervalBoundaries()
    {
        // High interval H2-H1 = 12, Low interval L2-L1 = 12, total span = 18.
        CandleData[] rising = StandardRisingWithBreakdownTail().Take(50).ToArray();
        Assert.True(RunHhHl(rising)[^1].BullishFires);

        // High/low interval boundaries (exact MinPivotIntervalBars = 12 passes, 13 fails).
        var min12 = new CoreStructureTrendParameter { MinPivotIntervalBars = 12, MaxPivotIntervalBars = 60 };
        var min13 = new CoreStructureTrendParameter { MinPivotIntervalBars = 13, MaxPivotIntervalBars = 60 };
        Assert.True(RunHhHl(rising, min12)[^1].BullishFires);
        Assert.False(RunHhHl(rising, min13)[^1].BullishFires);

        // U-W09 dynamic MinSpan = min(2 * MinPivotIntervalBars + 2, 2 * MaxPivotIntervalBars):
        // With default MinPivotIntervalBars=4 (MinSpan=10), span=9 fails and span=10 passes.
        // With MinPivotIntervalBars=2 (MinSpan=6), span=9 passes while span=5 fails!
        var defaultIntervalNoAtr = new CoreStructureTrendParameter { MinPivotIntervalBars = 4, MinSwingAtrMultiplier = 0m };
        var allowShortInterval = new CoreStructureTrendParameter { MinPivotIntervalBars = 2, MinSwingAtrMultiplier = 0m };
        CandleData[] span5 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 110m), (22, 102m), (23, 118m), (25, 108m), (28, 115m),
        });
        CandleData[] span9 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 110m), (23, 102m), (26, 118m), (29, 108m), (32, 115m),
        });
        CandleData[] span10 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 110m), (23, 102m), (27, 118m), (30, 108m), (33, 115m),
        });
        Assert.False(RunHhHl(span5, allowShortInterval)[^1].BullishFires);
        Assert.True(RunHhHl(span9, allowShortInterval)[^1].BullishFires);
        Assert.False(RunHhHl(span9, defaultIntervalNoAtr)[^1].BullishFires);
        Assert.True(RunHhHl(span10, defaultIntervalNoAtr)[^1].BullishFires);

        // U-W09 dynamic MaxSpan = 2 * MaxPivotIntervalBars: span=150 (q0=20, q1=65, q2=115, q3=170 -> intervals 95 & 105)
        // fails at default MaxPivotIntervalBars=60 (MaxSpan=120) and at 104, passes at MaxPivotIntervalBars=110 (MaxSpan=220).
        CandleData[] wideSpan150 = BuildAnchorSeries(new[]
        {
            (0, 100m), (15, 100m),
            (20, 94m),   // L1
            (65, 110m),  // H1
            (115, 102m), // L2 (L2-L1 = 95)
            (170, 124m), // H2 (H2-H1 = 105, span = 150)
            (176, 116m), // confirms H2
        });
        Assert.False(RunHhHl(wideSpan150, new CoreStructureTrendParameter { MaxPivotIntervalBars = 60, MaxLastPivotAgeBars = 60, MinSwingAtrMultiplier = 0m })[^1].BullishFires);
        Assert.False(RunHhHl(wideSpan150, new CoreStructureTrendParameter { MaxPivotIntervalBars = 104, MaxLastPivotAgeBars = 60, MinSwingAtrMultiplier = 0m })[^1].BullishFires);
        Assert.True(RunHhHl(wideSpan150, new CoreStructureTrendParameter { MaxPivotIntervalBars = 110, MaxLastPivotAgeBars = 60, MinSwingAtrMultiplier = 0m })[^1].BullishFires);

        // U-W09 equal/tight interval safety (even small M=4, 5 and Min=15, Max=15 must remain solvable without wipeout).
        // Case M=4, span=7: L1(20) -> H1(23) -> L2(24, int=4) -> H2(27, int=4, span=7) -> confirm(30).
        CandleData[] eq4Span7 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 94m),  // L1
            (23, 110m), // H1
            (24, 101m), // L2 (interval 4)
            (27, 120m), // H2 (interval 4, span = 7)
            (30, 112m), // confirms H2
        });
        eq4Span7[24] = PatternEngineTestBars.Bar(24, 109.5m, 109.7m, 100.8m, 101m); // keep H1 strictly at bar 23
        Assert.True(RunHhHl(eq4Span7, new CoreStructureTrendParameter { MinPivotIntervalBars = 4, MaxPivotIntervalBars = 4, MinSwingAtrMultiplier = 0m })[^1].BullishFires);

        // Case M=5, span=9: L1(20) -> H1(24) -> L2(25, int=5) -> H2(29, int=5, span=9) -> confirm(32).
        CandleData[] eq5Span9 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 94m),  // L1
            (24, 110m), // H1
            (25, 101m), // L2 (interval 5)
            (29, 120m), // H2 (interval 5, span = 9)
            (32, 112m), // confirms H2
        });
        eq5Span9[25] = PatternEngineTestBars.Bar(25, 109.5m, 109.7m, 100.8m, 101m); // keep H1 strictly at bar 24
        Assert.True(RunHhHl(eq5Span9, new CoreStructureTrendParameter { MinPivotIntervalBars = 5, MaxPivotIntervalBars = 5, MinSwingAtrMultiplier = 0m })[^1].BullishFires);

        var equalValid = new CoreStructureTrendParameter { MinPivotIntervalBars = 15, MaxPivotIntervalBars = 15, MinSwingAtrMultiplier = 0m };
        equalValid.Validate();
        Assert.False(RunHhHl(span10, equalValid)[^1].BullishFires);

        CandleData[] span22 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (20, 94m),  // L1
            (31, 110m), // H1
            (32, 102m), // L2 (interval 12)
            (42, 120m), // H2 (interval 11, span = 22)
            (46, 114m), // confirms H2
        });
        var tightRange = new CoreStructureTrendParameter { MinPivotIntervalBars = 10, MaxPivotIntervalBars = 12, MinSwingAtrMultiplier = 0m };
        Assert.True(RunHhHl(span22, tightRange)[^1].BullishFires);
        Assert.False(RunHhHl(span22, new CoreStructureTrendParameter { MinPivotIntervalBars = 10, MaxPivotIntervalBars = 10, MinSwingAtrMultiplier = 0m })[^1].BullishFires);

        // U-W09 warmup scaling: max(500, 2 * MaxPivotIntervalBars + MaxLastPivotAgeBars + 20).
        Assert.Equal(500, new CoreStructureTrendParameter().GetRequiredWarmupBars());
        Assert.Equal(530, new CoreStructureTrendParameter { MaxPivotIntervalBars = 180, MaxLastPivotAgeBars = 150 }.GetRequiredWarmupBars());
        Assert.Equal(620, new CoreStructureTrendParameter { MaxPivotIntervalBars = 200, MaxLastPivotAgeBars = 200 }.GetRequiredWarmupBars());
    }

    [Fact]
    public void HH_StaleFreshness_40_Passes_41_Fails()
    {
        // Confirmation bar of last pivot (H2): find exact latest confirmed bar, then hold sideways > L2 without triggering a 3% reversal!
        CandleData[] core = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m), (24, 94m), (30, 110m), (36, 102m), (42, 120m), (46, 114m),
            (92, 114m), // flat plateau around 114..115 (< 3% move so no new pivot!)
        });
        var param = new CoreStructureTrendParameter();
        PatternBarResult[] res = RunHhHl(core, param);
        int lastConf = CausalZigZag.Extract(PatternEngineTestBars.Context(core), new CausalZigZagOptions(param.ZigZagThresholdPercent, param.MinSwingAtrMultiplier, 0))[^1].ConfirmedAtIndex;
        Assert.InRange(lastConf, 43, 46);
        Assert.True(res[lastConf].BullishFires);
        Assert.True(res[lastConf + 40].BullishFires, "Exact MaxLastPivotAgeBars=40 must still hold");
        Assert.False(res[lastConf + 41].BullishFires, "Bar 41 after last confirmation must expire to 0m");
    }

    [Fact]
    public void HH_StepUp_PercentAndAtrEdge_U_W06()
    {
        // U-W06: StepThresholdMet requires BOTH percentage step-up (>= MinStepUpPercent%) AND volatility step (>= 0.50 * ATR(later - 1, 14)).
        // Case 1: H2-H1 = 0.90 (0.4496%) while 0.50 * ATR(~2.4) = ~1.2 -> even MinStepUpPercent = 0.40m or 0m MUST fail (ATR side blocks old OR loophole).
        CandleData[] atrBlocksSmallStep = BuildAnchorSeries(new[]
        {
            (0, 200m), (18, 200m),
            (22, 188m),
            (30, 200m),   // H1 = 200.2
            (35, 190m),   // L1 = 189.8
            (41, 200.9m), // H2 = 201.1; H2-H1 = 0.90 (0.4496%), 0.50 * ATR > 0.90
            (46, 193m),   // L2 = 192.8
            (50, 200m),   // confirms L2
        }, wick: 0.2m);

        Assert.False(RunHhHl(atrBlocksSmallStep, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.50m })[^1].BullishFires);
        Assert.False(RunHhHl(atrBlocksSmallStep, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.40m })[^1].BullishFires);
        Assert.False(RunHhHl(atrBlocksSmallStep, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.0m })[^1].BullishFires);

        // Case 2: Monotonicity + MinStepUpPercent = 0m ATR-only: H2-H1 = +2.00 (+1.00% on 200.1, > 0.50*ATR) and L2-L1 = +3.00 (+1.58%, > 0.50*ATR).
        // 0.0m (ATR only) -> True; 0.5m -> True; 1.0m -> False (2.00 / 200.1 = 0.9995% < 1.0%); 2.0m -> False!
        CandleData[] atrPasses = BuildAnchorSeries(new[]
        {
            (0, 200m), (18, 200m),
            (22, 192m),
            (30, 200m),   // H1 = 200.1
            (38, 190m),   // L1 = 189.9
            (46, 202.0m), // H2 = 202.1; H2-H1 = 2.00 (0.9995%)
            (54, 193m),   // L2 = 192.9; L2-L1 = 3.00 (1.58%)
            (62, 200m),   // confirms L2
        }, wick: 0.1m);

        bool s00 = RunHhHl(atrPasses, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.0m })[^1].BullishFires;
        bool s05 = RunHhHl(atrPasses, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.5m })[^1].BullishFires;
        bool s10 = RunHhHl(atrPasses, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 1.0m })[^1].BullishFires;
        bool s20 = RunHhHl(atrPasses, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 2.0m })[^1].BullishFires;
        Assert.True(s00, "MinStepUpPercent=0m should allow step when diff >= 0.50 * ATR");
        Assert.True(s05);
        Assert.False(s10, "ATR passes but percent 0.9995% < 1.0% must fail under AND");
        Assert.False(s20);

        // Subset monotonicity across all bars: S(2.0) subset of S(0.5) subset of S(0.0) on a walk.
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 19, count: 250);
        PatternBarResult[] r00 = RunHhHl(walk, new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.0m });
        PatternBarResult[] r05 = RunHhHl(walk, new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.5m });
        PatternBarResult[] r20 = RunHhHl(walk, new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 2.0m });
        for (int t = 0; t < walk.Length; t++)
        {
            if (r20[t].BullishFires) Assert.True(r05[t].BullishFires);
            if (r05[t].BullishFires) Assert.True(r00[t].BullishFires);
        }

        // Case 3 (R-16): later.Index < 15 (TryAtrCtx(later.Index - 1, 14) cannot compute ATR) -> must return false even at bar t >= 20.
        CandleData[] earlyPivotsBeforeBar15 = BuildAnchorSeries(new[]
        {
            (0, 100m),
            (3, 92m),  // L1
            (6, 110m), // H1
            (9, 100m), // L2 (interval 6)
            (13, 122m), // H2 (interval 7, index 13 < 15!, span = 10)
            (16, 114m), // confirms H2 at 16 (< MinBars 20), then holds flat until bar 22!
            (22, 114m),
        });
        var noAtrFilter = new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.5m };
        Assert.False(RunHhHl(earlyPivotsBeforeBar15, noAtrFilter)[19].BullishFires, "Pivots with later.Index < 15 have undefined ATR and must stay false (R-16)");

        // Case 4: Non-positive earlier pivot bar never divides by zero and never fires.
        CandleData[] nonPositivePivot = (CandleData[])atrPasses.Clone();
        nonPositivePivot[38] = PatternEngineTestBars.Bar(38, 0m, 0m, 0m, 0m);
        Assert.False(RunHhHl(nonPositivePivot, new CoreStructureTrendParameter { ZigZagThresholdPercent = 3m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.5m })[^1].BullishFires);
    }

    [Fact]
    public void HH_CloseBreakdown_InvalidatesImmediately_UnlessDisabled()
    {
        CandleData[] broken = StandardRisingWithBreakdownTail(); // bar 58 closes at L2 (101.8), bar 60 at 100
        Assert.False(RunHhHl(broken, new CoreStructureTrendParameter { RequireCloseAboveLastLow = true })[58].BullishFires);
        Assert.True(RunHhHl(broken, new CoreStructureTrendParameter { RequireCloseAboveLastLow = false })[58].BullishFires);
    }

    [Fact]
    public void HH_BothSides_MutuallyExclusive_AndOrderAgnostic4Pivots()
    {
        // Order ending on H2: L1(24)->H1(30)->L2(36)->H2(42) -> confirmed at 46 -> BullishFires = true!
        CandleData[] endingOnH2 = StandardRisingWithBreakdownTail().Take(50).ToArray();
        Assert.True(RunHhHl(endingOnH2, side: 1)[^1].BullishFires);

        // Order ending on L2: H1(24)=110 -> L1(30)=100 -> H2(36)=118 -> L2(42)=106 -> conf(46)=114 -> BullishFires = true!
        CandleData[] endingOnL2 = BuildAnchorSeries(new[]
        {
            (0, 100m), (18, 100m),
            (24, 110m), // H1
            (30, 100m), // L1
            (36, 118m), // H2 > H1
            (42, 106m), // L2 > L1
            (46, 114m), // pullback/bounce >= 3% confirms L2
        });
        Assert.True(RunHhHl(endingOnL2, side: 1)[^1].BullishFires);

        // LL/LH mirror (side = -1, Plan R-25): H1 > H2 > L2, L1 > L2, C_t < H2
        CandleData[] fallingLlLh = BuildAnchorSeries(new[]
        {
            (0, 120m), (18, 120m),
            (24, 126m), // H1
            (30, 110m), // L1
            (36, 120m), // H2 < H1
            (42, 102m), // L2 < L1
            (46, 108m), // bounce >= 3% confirms L2, C=108 < H2=120.2!
        });
        PatternBarResult[] bullOnFall = RunHhHl(fallingLlLh, side: 1);
        PatternBarResult[] bearOnFall = RunHhHl(fallingLlLh, side: -1);
        Assert.False(bullOnFall[^1].Fires);
        Assert.True(bearOnFall[^1].BearishFires);
        Assert.Equal(-1, bearOnFall[^1].Direction);

        // Mutual exclusion across a 400-bar random walk (relaxed interval/step so many bars fire).
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 12, count: 400);
        var loose = new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m };
        PatternBarResult[] plus = RunHhHl(walk, loose, side: 1);
        PatternBarResult[] minus = RunHhHl(walk, loose, side: -1);
        Assert.Contains(plus, r => r.BullishFires);
        Assert.Contains(minus, r => r.BearishFires);
        for (int t = 0; t < walk.Length; t++)
        {
            Assert.False(plus[t].Fires && minus[t].Fires, $"HH/HL (+1) and LL/LH (-1) must never fire on the same bar {t}");
        }
    }

    [Fact]
    public void HH_InvalidBar_Locality_CausalPrefix_AndCancellation()
    {
        CandleData[] baseBars = StandardRisingWithBreakdownTail().Take(50).ToArray();

        // Bar 49 invalid -> output at bar 49 must be 0m without throwing.
        CandleData[] invalidLast = (CandleData[])baseBars.Clone();
        invalidLast[49] = PatternEngineTestBars.Bar(49, 114m, 110m, 115m, 114m);
        Assert.False(RunHhHl(invalidLast)[^1].Fires);

        // Early unrelated bar 2 invalid before swing formation leaves bar 49 unaffected (local).
        CandleData[] earlyInvalid = (CandleData[])baseBars.Clone();
        earlyInvalid[2] = PatternEngineTestBars.Bar(2, 100m, 98m, 101m, 100m);
        Assert.True(RunHhHl(earlyInvalid)[^1].BullishFires);

        // Causal prefix invariance & cancellation.
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 7, count: 200);
        PatternSeriesContext fullCtx = PatternEngineTestBars.Context(walk);
        PatternSeriesContext prefCtx = PatternEngineTestBars.Context(walk.Take(115).ToArray());
        var param = new CoreStructureTrendParameter();
        PatternBarResult[] full = HigherHighHigherLowEngine.Run(fullCtx, param);
        PatternBarResult[] pref = HigherHighHigherLowEngine.Run(prefCtx, param);
        for (int t = 0; t < pref.Length; t++)
        {
            Assert.Equal(full[t], pref[t]);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => HigherHighHigherLowEngine.Run(fullCtx, param, cts.Token));
    }
}
