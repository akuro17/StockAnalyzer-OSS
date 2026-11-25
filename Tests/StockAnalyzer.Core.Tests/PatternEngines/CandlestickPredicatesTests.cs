using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Shared v2 candlestick predicate, gap, and T0..T6 trend/zone tests (plan ChartPatterns56 v2, run V2-8;
/// ledger <c>sa_pattern_literal_ledger_V2-8_CandlesPart1.md</c>, rows <c>CP_*</c>).
/// </summary>
public class CandlestickPredicatesTests
{
    private static CandleData Bar(decimal o, decimal h, decimal l, decimal c) => CandleFixtureBuilder.MakeBar(o, h, l, c);

    [Fact]
    public void CP_AtrAndAvgBody_ZeroAndInvalidGuard()
    {
        var p = new CoreCandlePatternParameter();
        CandleData candidate = Bar(100m, 110m, 95m, 108m);

        // Both avgBody <= 0 and atr <= 0 -> all predicates false.
        Assert.False(CandlestickPredicates.HasValidContext(0m, 0m));
        Assert.False(CandlestickPredicates.IsLarge(in candidate, 0m, 0m, p));
        Assert.False(CandlestickPredicates.IsSmall(in candidate, 0m, 0m, p));
        Assert.False(CandlestickPredicates.IsMarubozu(in candidate, 0m, 0m, p));
        Assert.False(CandlestickPredicates.IsDojiNormal(in candidate, 0m, 0m, p));
        Assert.False(CandlestickPredicates.IsDojiStar(in candidate, 0m, 0m, p));

        // TryAtrCtx on too-short series or corrupt bar in context returns false.
        var flat20 = CandleFixtureBuilder.BuildContextAndAppend(0);
        Assert.False(PatternMath.TryAtrCtx(PatternEngineTestBars.Context(flat20), 13, 14, out _, out _));
        var corruptCtx = PatternEngineTestBars.Corrupt(flat20.ToArray(), 25);
        Assert.False(PatternMath.TryAtrCtx(PatternEngineTestBars.Context(corruptCtx), 39, 14, out _, out _));
    }

    [Fact]
    public void CP_IsLarge_Edges()
    {
        // avgBody = 2.0, atr = 4.0 => minBody = max(1.50 * 2.0, 0.60 * 4.0) = max(3.0, 2.4) = 3.0.
        // Also requires B / R >= 0.60 and R > 0.
        var p = new CoreCandlePatternParameter();
        Assert.True(CandlestickPredicates.IsLarge(Bar(100m, 104m, 99m, 103m), 2.0m, 4.0m, p));   // B=3.0, R=5.0, B/R=0.60 exact
        Assert.False(CandlestickPredicates.IsLarge(Bar(100m, 104m, 99m, 102.99m), 2.0m, 4.0m, p)); // B=2.99 < 3.0
        Assert.False(CandlestickPredicates.IsLarge(Bar(100m, 104m, 98.99m, 103m), 2.0m, 4.0m, p)); // B/R=3.0/5.01 < 0.60

        // When 0.60 * ATR dominates (avgBody = 1.0, atr = 10.0 => minBody = max(1.5, 6.0) = 6.0):
        Assert.True(CandlestickPredicates.IsLarge(Bar(100m, 107m, 99m, 106m), 1.0m, 10.0m, p));  // B=6.0, R=8.0, B/R=0.75
        Assert.False(CandlestickPredicates.IsLarge(Bar(100m, 106m, 99m, 105.99m), 1.0m, 10.0m, p)); // B=5.99 < 6.0

        // Zero-range bar always false.
        Assert.False(CandlestickPredicates.IsLarge(Bar(100m, 100m, 100m, 100m), 2.0m, 4.0m, p));
    }

    [Fact]
    public void CP_IsSmall_Edges_ZeroRangeRejected()
    {
        // avgBody = 2.0 => maxBody = 0.80 * 2.0 = 1.60; B / R <= 0.50; R > 0 (rejects zero range unlike legacy!).
        var p = new CoreCandlePatternParameter();
        Assert.True(CandlestickPredicates.IsSmall(Bar(100m, 102.6m, 99.4m, 101.6m), 2.0m, 4.0m, p)); // B=1.60, R=3.20, B/R=0.50
        Assert.False(CandlestickPredicates.IsSmall(Bar(100m, 103m, 99m, 101.61m), 2.0m, 4.0m, p));  // B=1.61 > 1.60
        Assert.False(CandlestickPredicates.IsSmall(Bar(100m, 101.5m, 99.6m, 101m), 2.0m, 4.0m, p)); // B=1.0, R=1.9, B/R=0.526 > 0.50
        Assert.False(CandlestickPredicates.IsSmall(Bar(100m, 100m, 100m, 100m), 2.0m, 4.0m, p));   // Zero range R == 0 -> false
    }

    [Fact]
    public void CP_IsMarubozu_Edges()
    {
        // avgBody = 2.0, atr = 4.0 => minBody = max(1.50*2.0, 0.80*4.0) = 3.20; B/R >= 0.90; U, D <= 0.05*R.
        var p = new CoreCandlePatternParameter();
        Assert.True(CandlestickPredicates.IsMarubozu(Bar(100m, 110.5m, 99.5m, 110m), 2.0m, 4.0m, p));  // R=11, B=10, U=0.5<=0.55, D=0.5<=0.55, B/R=0.909
        Assert.False(CandlestickPredicates.IsMarubozu(Bar(100m, 103.19m, 100m, 103.19m), 2.0m, 4.0m, p)); // B=3.19 < 3.20
        Assert.False(CandlestickPredicates.IsMarubozu(Bar(100m, 110.56m, 100m, 110m), 2.0m, 4.0m, p));  // U=0.56 > 0.05*10.56=0.528
        Assert.False(CandlestickPredicates.IsMarubozu(Bar(100m, 110m, 99.44m, 110m), 2.0m, 4.0m, p));  // D=0.56 > 0.05*10.56=0.528
    }

    [Fact]
    public void CP_IsDoji_NormalVsStar()
    {
        var p = new CoreCandlePatternParameter();
        // avgBody = 2.0, atr = 4.0 -> normal requires R >= 0.50 * 4 = 2.0 and B <= 0.05 * R.
        CandleData zeroRange = Bar(100m, 100m, 100m, 100m);
        CandleData tinyRange = Bar(100m, 100.9m, 99m, 100m); // R = 1.9 < 2.0
        CandleData normalDoji = Bar(100m, 101m, 99m, 100.10m); // R = 2.0, B = 0.10 = 0.05 * 2.0
        CandleData tooWideBody = Bar(100m, 101m, 99m, 100.11m); // B = 0.11 > 0.10

        Assert.False(CandlestickPredicates.IsDojiNormal(in zeroRange, 2.0m, 4.0m, p));
        Assert.True(CandlestickPredicates.IsDojiStar(in zeroRange, 2.0m, 4.0m, p));

        Assert.False(CandlestickPredicates.IsDojiNormal(in tinyRange, 2.0m, 4.0m, p));
        Assert.True(CandlestickPredicates.IsDojiStar(in tinyRange, 2.0m, 4.0m, p));

        Assert.True(CandlestickPredicates.IsDojiNormal(in normalDoji, 2.0m, 4.0m, p));
        Assert.False(CandlestickPredicates.IsDojiNormal(in tooWideBody, 2.0m, 4.0m, p));
    }

    [Fact]
    public void CP_GapHelpers_And_GapParams()
    {
        // Body gap threshold = 0.10 * AvgBody(2.0) = 0.20 (strict inequality > / <).
        CandleData prevBull = Bar(100m, 106m, 99m, 105m);
        Assert.True(CandlestickPredicates.HasRealBodyGapUp(in prevBull, Bar(105.21m, 108m, 104m, 107m), 2.0m, 0.10m));
        Assert.False(CandlestickPredicates.HasRealBodyGapUp(in prevBull, Bar(105.20m, 108m, 104m, 107m), 2.0m, 0.10m));

        CandleData prevBear = Bar(105m, 106m, 99m, 100m);
        Assert.True(CandlestickPredicates.HasRealBodyGapDown(in prevBear, Bar(99.79m, 101m, 97m, 98m), 2.0m, 0.10m));
        Assert.False(CandlestickPredicates.HasRealBodyGapDown(in prevBear, Bar(99.80m, 101m, 97m, 98m), 2.0m, 0.10m));

        Assert.True(CandlestickPredicates.HasWickGapUp(in prevBull, Bar(107m, 109m, 106.01m, 108m)));
        Assert.False(CandlestickPredicates.HasWickGapUp(in prevBull, Bar(107m, 109m, 106m, 108m)));
        Assert.True(CandlestickPredicates.HasWickGapDown(in prevBear, Bar(97m, 98.99m, 95m, 96m)));
        Assert.False(CandlestickPredicates.HasWickGapDown(in prevBear, Bar(97m, 99m, 95m, 96m)));
    }

    [Fact]
    public void CP_PriorTrend_T0_To_T6_Variants()
    {
        var revParam = new CoreCandlePatternParameter();
        var contParam = new CoreCandlePatternParameter { PriorTrendAtrMultiplier = 1.0m };

        // T1 down vs T2 up vs T0 none.
        var downList = CandleFixtureBuilder.BuildContextAndAppend(-1, Bar(200m, 201m, 195m, 199m));
        var downCtx = PatternEngineTestBars.Context(downList);
        Assert.True(CandlestickPredicates.TrendAndZoneHold(downCtx, revParam, CandleTrendZoneVariant.T0None, 40, 40, 39, 4m));
        Assert.True(CandlestickPredicates.TrendAndZoneHold(downCtx, revParam, CandleTrendZoneVariant.T1ReversalBottom, 40, 40, 39, 4m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(downCtx, revParam, CandleTrendZoneVariant.T2ReversalTop, 40, 40, 39, 4m));

        var upList = CandleFixtureBuilder.BuildContextAndAppend(1, Bar(200m, 205m, 199m, 204m));
        var upCtx = PatternEngineTestBars.Context(upList);
        Assert.True(CandlestickPredicates.TrendAndZoneHold(upCtx, revParam, CandleTrendZoneVariant.T2ReversalTop, 40, 40, 39, 4m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(upCtx, revParam, CandleTrendZoneVariant.T1ReversalBottom, 40, 40, 39, 4m));

        // T3 Continuation Bull & T4 Continuation Bear.
        Assert.True(CandlestickPredicates.TrendAndZoneHold(upCtx, contParam, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(downCtx, contParam, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));
        Assert.True(CandlestickPredicates.TrendAndZoneHold(downCtx, contParam, CandleTrendZoneVariant.T4ContinuationBear, 40, 40, 39, 4m));

        // U-W05 counterexamples: sharp sell-off below EMA20 even when EMA10 > EMA20 and C_e - C_{e-10} >= ATR fails T3 (and mirror fails T4).
        var collapseList = new List<CandleData>(41);
        for (int i = 0; i < 25; i++)
        {
            collapseList.Add(PatternEngineTestBars.Bar(i, 220m, 222m, 218m, 220m, 1000L));
        }
        decimal[] preDip = { 200m, 185m, 175m, 165m, 150m }; // i=25..29 (C_29 = 150)
        decimal prevC = 220m;
        for (int j = 0; j < preDip.Length; j++)
        {
            collapseList.Add(PatternEngineTestBars.Bar(25 + j, prevC, Math.Max(prevC, preDip[j]) + 1m, Math.Min(prevC, preDip[j]) - 1m, preDip[j], 1000L));
            prevC = preDip[j];
        }
        decimal[] steepRally = { 165m, 175m, 185m, 195m, 205m, 212m, 218m, 222m, 225m }; // i=30..38
        for (int j = 0; j < steepRally.Length; j++)
        {
            collapseList.Add(PatternEngineTestBars.Bar(30 + j, prevC, Math.Max(prevC, steepRally[j]) + 1m, Math.Min(prevC, steepRally[j]) - 1m, steepRally[j], 1000L));
            prevC = steepRally[j];
        }
        collapseList.Add(PatternEngineTestBars.Bar(39, 225m, 226m, 179m, 180m, 1000L)); // C_39=180 > C_29=150 (+30 >= ATR), EMA10 > EMA20, C_39 < EMA20!
        collapseList.Add(Bar(180m, 182m, 179m, 181m));
        var collapseCtx = PatternEngineTestBars.Context(collapseList);
        Assert.True(collapseCtx.Ema(10, 39) > collapseCtx.Ema(20, 39));
        Assert.True(collapseCtx[39].Close < collapseCtx.Ema(20, 39));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(collapseCtx, new CoreCandlePatternParameter { PriorTrendAtrMultiplier = 1.0m, RequirePriceZone = false }, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));

        var spikesList = collapseList
            .Select((b, idx) => PatternEngineTestBars.Bar(idx, 400m - b.Open, 400m - b.Low, 400m - b.High, 400m - b.Close, b.Volume))
            .ToList();
        var spikesCtx = PatternEngineTestBars.Context(spikesList);
        Assert.True(spikesCtx.Ema(10, 39) < spikesCtx.Ema(20, 39));
        Assert.True(spikesCtx[39].Close > spikesCtx.Ema(20, 39));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(spikesCtx, new CoreCandlePatternParameter { PriorTrendAtrMultiplier = 1.0m, RequirePriceZone = false }, CandleTrendZoneVariant.T4ContinuationBear, 40, 40, 39, 4m));

        // T5 Cross Doji: accepts BOTH extreme low (down trend <= 0.20) AND extreme high (up trend >= 0.80), rejects flat/middle.
        Assert.True(CandlestickPredicates.TrendAndZoneHold(downCtx, revParam, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));
        Assert.True(CandlestickPredicates.TrendAndZoneHold(upCtx, revParam, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));
        var flatCtx = PatternEngineTestBars.Context(CandleFixtureBuilder.BuildContextAndAppend(0, Bar(200m, 200.5m, 199.5m, 200m)));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(flatCtx, revParam, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));

        // T6 Three White Soldiers: accepts down-trend OR 60-bar bottom zone <= 0.25 even without 10-bar momentum.
        var base60 = new List<CandleData>();
        // Drop from 300 to 200 in bars 0..29, then chop sideways 200..204 for bars 30..59 -> no 10-bar trend, YET Pos(..., 60) <= 0.25!
        for (int i = 0; i < 60; i++)
        {
            decimal c = i < 30 ? 300m - i * 3m : 202m + (i % 2 == 0 ? 1m : -1m);
            base60.Add(PatternEngineTestBars.Bar(i, c - 0.5m, c + 1m, c - 1m, c, 1000L));
        }
        base60.Add(PatternEngineTestBars.Bar(60, 200m, 204m, 199m, 203m, 1000L));
        var t6Ctx = PatternEngineTestBars.Context(base60);
        Assert.True(CandlestickPredicates.TrendAndZoneHold(t6Ctx, revParam, CandleTrendZoneVariant.T6ThreeWhiteSoldiersBottom, 60, 60, 59, 2m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(t6Ctx, revParam, CandleTrendZoneVariant.T1ReversalBottom, 60, 60, 59, 2m));
    }

    [Fact]
    public void CP_Zone_T1T2_Continuation_Doji_Long60()
    {
        var p = new CoreCandlePatternParameter();
        // Flat middle zone -> T1 fails PriceZoneThreshold (<= 0.25) even with RequirePriorTrend = false.
        var midList = CandleFixtureBuilder.BuildContextAndAppend(-1, Bar(200m, 230m, 225m, 228m)); // Pos way above 0.25
        var pNoTrend = new CoreCandlePatternParameter { RequirePriorTrend = false, RequirePriceZone = true };
        Assert.False(CandlestickPredicates.TrendAndZoneHold(PatternEngineTestBars.Context(midList), pNoTrend, CandleTrendZoneVariant.T1ReversalBottom, 40, 40, 39, 4m));

        // Continuation T3 needs Pos(C_s, s, 20) >= 0.60; placing s Close below 0.60 rejects T3 even with uptrend.
        var upMidClose = CandleFixtureBuilder.BuildContextAndAppend(1, Bar(190m, 191m, 180m, 180.5m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(PatternEngineTestBars.Context(upMidClose), new CoreCandlePatternParameter { PriorTrendAtrMultiplier = 1.0m, RequirePriorTrend = false, RequirePriceZone = true }, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));

        // Doji T5 needs PosHigh >= 0.80 or PosLow <= 0.20 (midpoint Pos = 0.50 rejected).
        var downMidDoji = CandleFixtureBuilder.BuildContextAndAppend(-1, Bar(220m, 222m, 218m, 220m));
        Assert.False(CandlestickPredicates.TrendAndZoneHold(PatternEngineTestBars.Context(downMidDoji), new CoreCandlePatternParameter { RequirePriorTrend = false, RequirePriceZone = true }, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));

        // T3 / T4 / T5 live PriceZoneThreshold wiring and specification defaults (0.40m for continuation, 0.20m for CrossDoji).
        CoreCandlePatternParameter t3Default = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleRisingThreeMethods);
        CoreCandlePatternParameter t4Default = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleFallingThreeMethods);
        CoreCandlePatternParameter t5Default = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleCrossDoji);
        Assert.Equal(0.40m, t3Default.PriceZoneThreshold);
        Assert.Equal(0.40m, t4Default.PriceZoneThreshold);
        Assert.Equal(0.20m, t5Default.PriceZoneThreshold);

        // Upward context [21..39] has L_min=161..H_max=201; bar 40 inside [161..201] with C_s=184 -> Pos=(184-161)/40 = 0.575 < 0.60 fails default T3 (0.40), passes when relaxed to 0.45 (min Pos = 0.55).
        var t3EdgeCtx = PatternEngineTestBars.Context(CandleFixtureBuilder.BuildContextAndAppend(1, Bar(184m, 185m, 183m, 184m)));
        t3Default.RequirePriorTrend = false;
        Assert.False(CandlestickPredicates.TrendAndZoneHold(t3EdgeCtx, t3Default, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));
        t3Default.PriceZoneThreshold = 0.45m;
        Assert.True(CandlestickPredicates.TrendAndZoneHold(t3EdgeCtx, t3Default, CandleTrendZoneVariant.T3ContinuationBull, 40, 40, 39, 4m));

        // Downward context [21..39] has L_min=199..H_max=239; bar 40 inside [199..239] with C_s=216 -> Pos=(216-199)/40 = 0.425 > 0.40 fails default T4 (0.40), passes when relaxed to 0.45.
        var t4EdgeCtx = PatternEngineTestBars.Context(CandleFixtureBuilder.BuildContextAndAppend(-1, Bar(216m, 217m, 215m, 216m)));
        t4Default.RequirePriorTrend = false;
        Assert.False(CandlestickPredicates.TrendAndZoneHold(t4EdgeCtx, t4Default, CandleTrendZoneVariant.T4ContinuationBear, 40, 40, 39, 4m));
        t4Default.PriceZoneThreshold = 0.45m;
        Assert.True(CandlestickPredicates.TrendAndZoneHold(t4EdgeCtx, t4Default, CandleTrendZoneVariant.T4ContinuationBear, 40, 40, 39, 4m));

        // CrossDoji at t=40 (range 199..239): L_t=208 -> PosLow=9/40=0.225 > 0.20 (and PosHigh=13/40=0.325 < 0.75) fails default (0.20), passes when PriceZoneThreshold = 0.25.
        var t5EdgeCtx = PatternEngineTestBars.Context(CandleFixtureBuilder.BuildContextAndAppend(-1, Bar(210m, 212m, 208m, 210m)));
        t5Default.RequirePriorTrend = false;
        Assert.False(CandlestickPredicates.TrendAndZoneHold(t5EdgeCtx, t5Default, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));
        t5Default.PriceZoneThreshold = 0.25m;
        Assert.True(CandlestickPredicates.TrendAndZoneHold(t5EdgeCtx, t5Default, CandleTrendZoneVariant.T5CrossDojiExtreme, 40, 40, 39, 4m));
    }
}
