using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.CandleFixtureBuilder;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Spec-oracle unit and boundary tests for all 9 Run V2-10 four/five-bar candlestick engines
/// (<c>06_Candlestick_Patterns</c>; ledger <c>sa_pattern_literal_ledger_V2-10_CandlesPart3.md</c>).
/// Context generator sets <c>AvgBody_ctx = 2m</c>, <c>ATR_ctx = 4m</c>, <c>AvgVol = 1000</c>.
/// </summary>
public class CandlestickPatternEnginePart3Tests
{
    private static bool FiresLast(IndicatorType type, IReadOnlyList<CandleData> bars, CoreCandlePatternParameter? p = null)
    {
        p ??= CoreCandlePatternParameter.CreateFor(type);
        PatternBarResult[] res = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bars), type, p);
        return res[^1].BullishFires;
    }

    [Fact]
    public void C45_RisingAndFallingThreeMethods_PositiveAndEdges()
    {
        // Rising Three Methods (T3, k=5): c1 large bull [199..203] (H1=203.2, L1=198.8), c2..c4 small inside [L1..H1] with body >= O1, c5 strong break > H1.
        CandleData r1 = MakeBar(199m, 203.2m, 198.8m, 203m, volume: 1000L);
        CandleData r2 = MakeBar(202.5m, 203.0m, 201.5m, 202.0m, volume: 900L);
        CandleData r3 = MakeBar(202.0m, 202.5m, 201.0m, 201.5m, volume: 900L);
        CandleData r4 = MakeBar(201.5m, 202.0m, 200.5m, 201.0m, volume: 900L);
        CandleData r5 = MakeBar(201.0m, 204.2m, 200.8m, 204.0m, volume: 1200L);
        Assert.True(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, r3, r4, r5)));

        // Alternative pullback rule: only 1 bearish bar in c2..c4 passes when C4 < C2!
        CandleData rAlt2 = MakeBar(202.0m, 203.1m, 201.8m, 202.3m, volume: 900L); // bull C2=202.3
        CandleData rAlt3 = MakeBar(201.8m, 202.6m, 201.4m, 202.1m, volume: 900L); // bull C3=202.1
        CandleData rAlt4 = MakeBar(201.6m, 202.3m, 200.8m, 201.2m, volume: 900L); // 1 bear & C4 < C2
        Assert.True(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, rAlt2, rAlt3, rAlt4, MakeBar(201.2m, 204.2m, 200.8m, 204.0m))));

        // Edges: c3 low < L1 fails; c4 real body below O1 fails; c5 <= H1 fails; O5 < C4 fails; optional volume contraction checks avg(V2..V4) < V1!
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, MakeBar(202.0m, 202.5m, 198.7m, 201.5m), r4, r5)));
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, r3, MakeBar(199.5m, 201.0m, 198.8m, 198.9m), MakeBar(199.0m, 204.2m, 198.9m, 204.0m))));
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, r3, r4, MakeBar(201.0m, 204.2m, 200.8m, 203.2m))));
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, r3, r4, MakeBar(200.99m, 204.2m, 200.8m, 204.0m))));

        CoreCandlePatternParameter rtmVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleRisingThreeMethods);
        rtmVolOn.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleRisingThreeMethods, BuildContextAndAppend(1, r1, r2, r3, r4, r5), rtmVolOn));
        Assert.True(FiresLast(IndicatorType.CandleRisingThreeMethods,
            BuildContextAndAppend(1, r1, MakeBar(202.5m, 203.0m, 201.5m, 202.0m, volume: 1100L), r3, r4, r5), rtmVolOn)); // avg(1100,900,900) = 966.7 < 1000 succeeds
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods,
            BuildContextAndAppend(1, r1, MakeBar(202.5m, 203.0m, 201.5m, 202.0m, volume: 1200L), r3, r4, r5), rtmVolOn)); // avg(1200,900,900) = 1000 == V1 fails

        // Falling Three Methods (T4, k=5) + wrong trend rejection + symmetric ThreeMethodsVolume (including R-8 zero-volume bypass).
        CandleData f1 = MakeBar(201m, 201.2m, 196.8m, 197m, volume: 1000L);
        CandleData f2 = MakeBar(197.5m, 198.5m, 197.0m, 198.0m, volume: 900L);
        CandleData f3 = MakeBar(198.0m, 199.0m, 197.5m, 198.5m, volume: 900L);
        CandleData f4 = MakeBar(198.5m, 199.5m, 198.0m, 199.0m, volume: 900L);
        CandleData f5 = MakeBar(199.0m, 199.2m, 195.8m, 196.0m, volume: 1200L);
        Assert.True(FiresLast(IndicatorType.CandleFallingThreeMethods, BuildContextAndAppend(-1, f1, f2, f3, f4, f5)));
        Assert.False(FiresLast(IndicatorType.CandleFallingThreeMethods, BuildContextAndAppend(1, f1, f2, f3, f4, f5)));
        Assert.False(FiresLast(IndicatorType.CandleFallingThreeMethods, BuildContextAndAppend(-1, f1, f2, f3, f4, MakeBar(199.0m, 199.2m, 196.8m, 196.8m))));

        // RequireVolumeConfirmation = false allows high pullback volume.
        Assert.True(FiresLast(IndicatorType.CandleFallingThreeMethods,
            BuildContextAndAppend(-1, f1, MakeBar(197.5m, 198.5m, 197.0m, 198.0m, volume: 2000L), f3, f4, f5)));

        CoreCandlePatternParameter ftmVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleFallingThreeMethods);
        ftmVolOn.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleFallingThreeMethods, BuildContextAndAppend(-1, f1, f2, f3, f4, f5), ftmVolOn));
        Assert.False(FiresLast(IndicatorType.CandleFallingThreeMethods,
            BuildContextAndAppend(-1, f1, MakeBar(197.5m, 198.5m, 197.0m, 198.0m, volume: 1200L), f3, f4, f5), ftmVolOn)); // avg(1200,900,900) == V1 fails
        Assert.False(FiresLast(IndicatorType.CandleFallingThreeMethods,
            BuildContextAndAppend(-1, f1, f2, f3, f4, MakeBar(199.0m, 199.2m, 195.8m, 196.0m, volume: 1199L)), ftmVolOn)); // V5 < 1.20*AvgVol fails

        // Zero-volume instrument (FX / Index): R-8 bypasses VolumeConfirmed & V2+V3+V4 < 3*V1 even when RequireVolumeConfirmation = true.
        var fxFalling = BuildContextAndAppend(-1, f1, f2, f3, f4, f5).Select(b => b with { Volume = 0L }).ToArray();
        Assert.True(FiresLast(IndicatorType.CandleFallingThreeMethods, fxFalling));
        Assert.True(FiresLast(IndicatorType.CandleFallingThreeMethods, fxFalling, ftmVolOn));
    }

    [Fact]
    public void C45_MatHold_PositiveGapDipAndBreakEdges()
    {
        // Mat Hold (T3, k=5): O2 > C1 + 0.10*AvgBody (202.2), >=1 bearish in c2..c4, all L_i >= O1, all C_i > mid1, C5 > max(H1..H4).
        CandleData m1 = MakeBar(198m, 202.2m, 197.8m, 202m); // mid1 = 200
        CandleData m2 = MakeBar(202.3m, 203.4m, 201.8m, 202.6m);
        CandleData m3 = MakeBar(202.4m, 202.8m, 201.0m, 201.8m); // bear
        CandleData m4 = MakeBar(201.7m, 202.2m, 200.5m, 201.1m); // bear, maxH = 203.4
        CandleData m5 = MakeBar(201.2m, 204.4m, 201.0m, 204.2m);
        Assert.True(FiresLast(IndicatorType.CandleMatHold, BuildContextAndAppend(1, m1, m2, m3, m4, m5)));

        // Edges: O2 == 202.2 (gap failed), all three c2..c4 bullish (no bearish bar), C4 == 200.0 (<= mid1), C5 == 203.4 (== maxH).
        Assert.False(FiresLast(IndicatorType.CandleMatHold, BuildContextAndAppend(1, m1, MakeBar(202.2m, 203.4m, 201.8m, 202.6m), m3, m4, m5)));
        Assert.False(FiresLast(IndicatorType.CandleMatHold, BuildContextAndAppend(1, m1, m2, MakeBar(201.4m, 202.8m, 201.0m, 201.8m), MakeBar(200.7m, 202.2m, 200.5m, 201.1m), m5)));
        Assert.False(FiresLast(IndicatorType.CandleMatHold, BuildContextAndAppend(1, m1, m2, m3, MakeBar(201.7m, 202.2m, 199.8m, 200.0m), m5)));
        Assert.False(FiresLast(IndicatorType.CandleMatHold, BuildContextAndAppend(1, m1, m2, m3, m4, MakeBar(201.2m, 204.4m, 201.0m, 203.4m))));
    }

    [Fact]
    public void C45_ThreeLineStrike_BullBear_TotalRangeNotBodySum_AndEdges()
    {
        // Bullish Three-Line Strike (T3, k=4): v2 spec uses total range C3 - O1 (3.5), NOT 0.85*(B1+B2+B3) (here sum=4.5, 0.85*4.5=3.825)!
        // Notice B4 = 3.55 passes total range (>= 3.5 && >= 3.0) while failing the old sum rule (3.55 < 3.825).
        CandleData b1 = MakeBar(199.0m, 200.7m, 198.8m, 200.5m); // B1=1.5
        CandleData b2 = MakeBar(199.8m, 201.7m, 199.6m, 201.5m); // B2=1.7
        CandleData b3 = MakeBar(200.8m, 202.7m, 200.6m, 202.5m); // B3=1.7, sum=4.9, C3-O1=3.5
        CandleData b4 = MakeBar(202.3m, 202.6m, 198.5m, 198.75m); // O4=202.3 >= 202.5-0.2, C4=198.75 < 199, B4=3.55 >= 3.5!
        Assert.True(FiresLast(IndicatorType.CandleBullishThreeLineStrike, BuildContextAndAppend(1, b1, b2, b3, b4)));

        // Edges: O4 < 202.3 fails; C4 == O1 fails; B4 < C3 - O1 fails (already enforced if C4 < O1 & O4 < C3 - ...; e.g. O4=202.3, C4=198.9 -> B4=3.4 < 3.5 fails!); B1 < 1.0 fails.
        Assert.False(FiresLast(IndicatorType.CandleBullishThreeLineStrike, BuildContextAndAppend(1, b1, b2, b3, MakeBar(202.29m, 202.6m, 198.5m, 198.7m))));
        Assert.False(FiresLast(IndicatorType.CandleBullishThreeLineStrike, BuildContextAndAppend(1, b1, b2, b3, MakeBar(202.6m, 202.8m, 198.8m, 199.0m))));
        Assert.False(FiresLast(IndicatorType.CandleBullishThreeLineStrike, BuildContextAndAppend(1, b1, b2, b3, MakeBar(202.3m, 202.6m, 198.8m, 198.9m))));

        // Bearish Three-Line Strike (T4, k=4)
        CandleData r1 = MakeBar(201.0m, 201.2m, 199.3m, 199.5m);
        CandleData r2 = MakeBar(200.2m, 200.4m, 198.3m, 198.5m);
        CandleData r3 = MakeBar(199.2m, 199.4m, 197.3m, 197.5m); // O1-C3=3.5
        CandleData r4 = MakeBar(197.7m, 201.5m, 197.4m, 201.25m); // B4=3.55 >= 3.5, C4 > O1
        Assert.True(FiresLast(IndicatorType.CandleBearishThreeLineStrike, BuildContextAndAppend(-1, r1, r2, r3, r4)));
        Assert.False(FiresLast(IndicatorType.CandleBearishThreeLineStrike, BuildContextAndAppend(1, r1, r2, r3, r4)));
    }

    [Fact]
    public void C45_LadderBottom_PositiveAndEdges()
    {
        CandleData l1 = MakeBar(201m, 201.2m, 199.2m, 199.5m);
        CandleData l2 = MakeBar(200.2m, 200.4m, 198.2m, 198.5m);
        CandleData l3 = MakeBar(199.2m, 199.4m, 197.2m, 197.5m);
        CandleData l4 = MakeBar(197.5m, 198.5m, 196.8m, 197.0m); // B4=0.5, U4=1.0 >= B4
        CandleData l5 = MakeBar(197.8m, 200.2m, 197.6m, 200.0m); // O5 > 197.5, C5 > H4=198.5, B5=2.2 >= 2.0
        Assert.True(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(-1, l1, l2, l3, l4, l5)));

        // Edges: O5 == max(O4, C4) fails (strict v2 open above c4 real body); c4 without shadow >= B4 fails; L2 == L1 fails; B5 = 1.99 fails; wrong trend fails.
        Assert.False(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(-1, l1, l2, l3, l4, MakeBar(197.5m, 200.2m, 197.3m, 200.0m))));
        Assert.False(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(-1, l1, l2, l3, MakeBar(198.0m, 198.2m, 196.9m, 197.0m), MakeBar(198.1m, 200.5m, 197.9m, 200.2m))));
        Assert.False(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(-1, l1, MakeBar(200.2m, 200.4m, 199.2m, 198.5m), l3, l4, l5)));
        Assert.False(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(-1, l1, l2, l3, l4, MakeBar(197.8m, 200.0m, 197.6m, 199.79m))));
        Assert.False(FiresLast(IndicatorType.CandleLadderBottom, BuildContextAndAppend(1, l1, l2, l3, l4, l5)));
    }

    [Fact]
    public void C45_Breakaway_BullBear_GapAndReversalReject()
    {
        // Bullish Breakaway (T1, k=5): O2 < C1 - 0.10*AvgBody (195.8), max(C3,C4) < O2, !(L4>L3 && C4>C2), C5 > max(H2..H4) & O2 < C5 < C1.
        CandleData bb1 = MakeBar(200m, 200.2m, 195.8m, 196m);
        CandleData bb2 = MakeBar(195.7m, 195.9m, 194.3m, 194.5m);
        CandleData bb3 = MakeBar(194.6m, 195.2m, 193.6m, 194.2m);
        CandleData bb4 = MakeBar(194.2m, 194.8m, 193.2m, 193.8m);
        CandleData bb5 = MakeBar(193.5m, 196.1m, 193.3m, 195.95m); // B5=2.45 >= 2.40, 195.7 < 195.95 < 196.0, > maxH=195.9
        Assert.True(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3, bb4, bb5)));

        // Edges: L4 > L3 && C4 > C2 (early reversal exclusion!) fails; C5 >= C1 (fills c1 body completely -> fails); RequireWickToWickGap=true rejects body gap.
        Assert.False(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3, MakeBar(194.5m, 195.1m, 193.9m, 194.8m), bb5)));
        Assert.False(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3, bb4, MakeBar(193.5m, 196.2m, 193.3m, 196.0m))));
        var strictWick = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishBreakaway);
        strictWick.RequireWickToWickGap = true;
        Assert.False(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3, bb4, bb5), strictWick));

        // Real-body gap mode (RequireWickToWickGap = false): middle bar upper wick reaching >= C1(196.0) still passes if c5 clears RealBodyHigh(c2..c4), while wick mode fails!
        CandleData bb3HighWick = MakeBar(194.6m, 196.1m, 193.6m, 194.2m);
        Assert.True(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3HighWick, bb4, bb5)));
        Assert.False(FiresLast(IndicatorType.CandleBullishBreakaway, BuildContextAndAppend(-1, bb1, bb2, bb3HighWick, bb4, bb5), strictWick));

        // Bearish Breakaway (T2, k=5)
        CandleData br1 = MakeBar(200m, 204.2m, 199.8m, 204m);
        CandleData br2 = MakeBar(204.3m, 205.7m, 204.1m, 205.5m);
        CandleData br3 = MakeBar(205.4m, 206.4m, 204.8m, 205.8m);
        CandleData br4 = MakeBar(205.8m, 206.8m, 205.2m, 206.2m);
        CandleData br5 = MakeBar(206.5m, 206.7m, 203.9m, 204.05m);
        Assert.True(FiresLast(IndicatorType.CandleBearishBreakaway, BuildContextAndAppend(1, br1, br2, br3, br4, br5)));
        Assert.False(FiresLast(IndicatorType.CandleBearishBreakaway, BuildContextAndAppend(1, br1, br2, br3, MakeBar(205.4m, 206.0m, 204.7m, 205.3m), br5)));

        CandleData br3LowWick = MakeBar(205.4m, 206.4m, 203.9m, 205.8m);
        var bearStrictWick = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBearishBreakaway);
        bearStrictWick.RequireWickToWickGap = true;
        Assert.True(FiresLast(IndicatorType.CandleBearishBreakaway, BuildContextAndAppend(1, br1, br2, br3LowWick, br4, br5)));
        Assert.False(FiresLast(IndicatorType.CandleBearishBreakaway, BuildContextAndAppend(1, br1, br2, br3LowWick, br4, br5), bearStrictWick));
    }

    [Fact]
    public void C45_ConcealingBabySwallow_PositiveAndEdges()
    {
        CandleData c1 = MakeBar(204m, 204.1m, 199.9m, 200m); // bear Marubozu
        CandleData c2 = MakeBar(203m, 203.1m, 198.9m, 199m); // bear Marubozu, O2<=O1 && C2<C1
        CandleData c3 = MakeBar(198.5m, 199.6m, 197.4m, 197.5m); // B3=1.0 >= 0.4, R3=2.2, O3=198.5 < 199, D3=0.1 <= 0.22, U3=1.1 >= 0.8, H3=199.6 > 199
        CandleData c4 = MakeBar(200m, 200.2m, 196.8m, 197m); // B4=3.0 >= 1.0, O4 > H3, C4 < L3
        Assert.True(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, c3, c4)));

        // Edges: D3 > 0.10*R3 fails; U3 < 0.80*B3 fails; B3 < 0.20*AvgBody fails; c4 doesn't cover c3 wick L3 fails; H3 == C2 fails.
        Assert.False(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, MakeBar(198.5m, 199.6m, 197.1m, 197.5m), MakeBar(200m, 200.2m, 196.8m, 197m))));
        Assert.False(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, MakeBar(198.3m, 199.1m, 197.1m, 197.2m), MakeBar(200m, 200.2m, 196.8m, 197m))));
        Assert.False(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, MakeBar(198.5m, 199.6m, 198.05m, 198.11m), c4))); // B3=0.39 < 0.40 fails
        Assert.False(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, c3, MakeBar(200m, 200.2m, 197.4m, 197.4m))));
        Assert.False(FiresLast(IndicatorType.CandleConcealingBabySwallow, BuildContextAndAppend(-1, c1, c2, MakeBar(198.5m, 199.0m, 197.4m, 197.5m), c4)));
    }

    [Fact]
    public void C45_InvalidBarInsideReadSetBlocks_OutsideIsLocal_CausalPrefixAndCancellation()
    {
        IndicatorType[] allPart3 =
        {
            IndicatorType.CandleBullishThreeLineStrike,
            IndicatorType.CandleBearishThreeLineStrike,
            IndicatorType.CandleConcealingBabySwallow,
            IndicatorType.CandleRisingThreeMethods,
            IndicatorType.CandleFallingThreeMethods,
            IndicatorType.CandleMatHold,
            IndicatorType.CandleLadderBottom,
            IndicatorType.CandleBullishBreakaway,
            IndicatorType.CandleBearishBreakaway,
        };

        List<CandleData> validRtm = BuildContextAndAppend(1,
            MakeBar(199m, 203.2m, 198.8m, 203m),
            MakeBar(202.5m, 203.0m, 201.5m, 202.0m),
            MakeBar(202.0m, 202.5m, 201.0m, 201.5m),
            MakeBar(201.5m, 202.0m, 200.5m, 201.0m),
            MakeBar(201.0m, 204.2m, 200.8m, 204.0m));

        List<CandleData> invalidPattern = new(validRtm);
        invalidPattern[41] = PatternEngineTestBars.Bar(41, 202.5m, 201m, 203m, 202m); // H < L in c2
        Assert.False(FiresLast(IndicatorType.CandleRisingThreeMethods, invalidPattern));

        List<CandleData> earlyInvalid = new(validRtm);
        earlyInvalid[2] = PatternEngineTestBars.Bar(2, 200m, 195m, 201m, 196m);
        Assert.True(FiresLast(IndicatorType.CandleRisingThreeMethods, earlyInvalid));

        CandleData[] walk = PatternEngineTestBars.Walk(seed: 29, count: 140);
        PatternSeriesContext fullCtx = PatternEngineTestBars.Context(walk);
        PatternSeriesContext prefixCtx = PatternEngineTestBars.Context(walk.Take(85).ToArray());

        foreach (IndicatorType type in allPart3)
        {
            CoreCandlePatternParameter p = CoreCandlePatternParameter.CreateFor(type);
            PatternBarResult[] full = CandlestickPatternEngine.Run(fullCtx, type, p);
            PatternBarResult[] pref = CandlestickPatternEngine.Run(prefixCtx, type, p);
            for (int t = 0; t < pref.Length; t++)
            {
                Assert.Equal(full[t], pref[t]);
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => CandlestickPatternEngine.Run(fullCtx, type, p, cts.Token));
        }
    }
}
