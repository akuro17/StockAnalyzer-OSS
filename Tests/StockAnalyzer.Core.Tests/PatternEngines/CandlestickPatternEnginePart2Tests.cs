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
/// Spec-oracle unit and boundary tests for all 16 Run V2-9 three-bar candlestick engines
/// (<c>06_Candlestick_Patterns</c>; ledger <c>sa_pattern_literal_ledger_V2-9_CandlesPart2.md</c>).
/// Context generator sets <c>AvgBody_ctx = 2m</c>, <c>ATR_ctx = 4m</c>, <c>AvgVol = 1000</c>.
/// </summary>
public class CandlestickPatternEnginePart2Tests
{
    private static bool FiresLast(IndicatorType type, IReadOnlyList<CandleData> bars, CoreCandlePatternParameter? p = null)
    {
        p ??= CoreCandlePatternParameter.CreateFor(type);
        PatternBarResult[] res = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bars), type, p);
        return res[^1].BullishFires;
    }

    private static CandleData[] WithSixtyBarLowZoneNoDownTrend(params CandleData[] patternBars)
    {
        // Bars 0..34 high range [240..250], bars 35..64 steady tiny rise [199..201.9] (no 10-bar down-trend at e=64; Pos_60 <= 0.25).
        var list = new List<CandleData>(65 + patternBars.Length);
        for (int i = 0; i < 35; i++)
        {
            list.Add(PatternEngineTestBars.Bar(i, 244m, 250m, 240m, 246m, DefaultVolume));
        }

        decimal c = 199m;
        for (int i = 35; i < 65; i++)
        {
            decimal open = c;
            c = open + 0.1m;
            list.Add(PatternEngineTestBars.Bar(i, open, c + 1m, open - 1m, c, DefaultVolume));
        }

        foreach (CandleData pb in patternBars)
        {
            list.Add(pb with { Timestamp = PatternEngineTestBars.Bar(list.Count, pb.Open, pb.High, pb.Low, pb.Close, pb.Volume).Timestamp });
        }

        return list.ToArray();
    }

    [Fact]
    public void C3_MorningAndEveningStar_PositiveEdgesZeroRangeStar_Volume()
    {
        // Morning Star (T1, Vol=1.20): c1 bear large, c2 small body below C1, c3 bull B3 >= 1.20*2=2.4 & C3 > mid1=198.
        CandleData ms1 = MakeBar(200m, 200.2m, 195.8m, 196m);
        CandleData ms2 = MakeBar(195.2m, 196.2m, 194.2m, 195.6m);
        CandleData ms3 = MakeBar(196m, 199.1m, 195.9m, 199m, volume: 1200L);
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, ms2, ms3)));

        // Four-price doji star (R2 == 0) is allowed on Morning Star (spec 06/07 §5).
        CandleData fourPriceStar = MakeBar(195.5m, 195.5m, 195.5m, 195.5m);
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, fourPriceStar, ms3)));

        // Edges: V = 1199 fails when RequireVolumeConfirmation = true; B3 = 2.39 (< 2.40) fails; C3 = 198.0 (<= mid1) fails; max(O2,C2) == C1 fails; Wrong trend (+1) fails.
        CoreCandlePatternParameter msVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleMorningStar);
        msVolOn.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, ms2, ms3), msVolOn));
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, ms2, MakeBar(196m, 199.1m, 195.9m, 199m, volume: 1199L)), msVolOn));
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, ms2, MakeBar(196m, 198.4m, 195.9m, 198.39m, volume: 1200L))));
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, ms2, MakeBar(195m, 198.1m, 194.9m, 198.0m, volume: 1200L))));
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(-1, ms1, MakeBar(195.6m, 196.2m, 194.2m, 196.0m), ms3)));
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, BuildContextAndAppend(1, ms1, ms2, ms3)));

        // Evening Star (T2, optional Vol=1.20) + 4-price doji star.
        CandleData es1 = MakeBar(200m, 204.2m, 199.8m, 204m);
        CandleData es2 = MakeBar(204.4m, 205.4m, 203.4m, 204.8m);
        CandleData es3 = MakeBar(204m, 204.1m, 200.9m, 201m, volume: 1200L);
        CoreCandlePatternParameter esVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleEveningStar);
        esVolOn.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleEveningStar, BuildContextAndAppend(1, es1, es2, es3), esVolOn));
        Assert.True(FiresLast(IndicatorType.CandleEveningStar, BuildContextAndAppend(1, es1, MakeBar(204.5m, 204.5m, 204.5m, 204.5m), es3)));
        Assert.False(FiresLast(IndicatorType.CandleEveningStar, BuildContextAndAppend(1, es1, es2, MakeBar(204m, 204.1m, 200.9m, 201m, volume: 1199L)), esVolOn));
        Assert.False(FiresLast(IndicatorType.CandleEveningStar, BuildContextAndAppend(-1, es1, es2, es3)));
    }

    [Fact]
    public void C3_ThreeWhiteSoldiers_T6ZoneAndEdges()
    {
        CandleData c1 = MakeBar(198m, 200.2m, 197.8m, 200m);
        CandleData c2 = MakeBar(199m, 201.7m, 198.8m, 201.5m);
        CandleData c3 = MakeBar(200m, 203.2m, 199.8m, 203m);

        // Build >= 60 downtrend bars so default ComputeMinBars(3) == 63 is satisfied.
        var longDown = new List<CandleData>(63);
        decimal downClose = 320m;
        for (int i = 0; i < 60; i++)
        {
            decimal o = downClose;
            downClose = o - 2m;
            longDown.Add(PatternEngineTestBars.Bar(i, o, o + 1m, downClose - 1m, downClose, DefaultVolume));
        }
        foreach (CandleData pb in new[] { c1, c2, c3 })
        {
            longDown.Add(pb with { Timestamp = PatternEngineTestBars.Bar(longDown.Count, pb.Open, pb.High, pb.Low, pb.Close, pb.Volume).Timestamp });
        }

        // Down-trend fires at >= 63 bars; 62-bar prefix yields all null (MinBars = 63); flat >= 63 bars without low zone fails.
        Assert.True(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, longDown));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, longDown.Take(62).ToArray()));
        var longFlat = new List<CandleData>(63);
        for (int i = 0; i < 60; i++)
        {
            // Keep bars 3..34 lower (183..187) and bars 35..59 flat (197..201) so ShortTermTrend == 0 and c1..c3 minLow sits above the 60-bar bottom 25%.
            bool up = (i % 2) != 0;
            decimal loBase = i < 35 ? 184m : 198m;
            decimal o = up ? loBase : loBase + 2m;
            decimal close = up ? loBase + 2m : loBase;
            longFlat.Add(PatternEngineTestBars.Bar(i, o, Math.Max(o, close) + 1m, Math.Min(o, close) - 1m, close, DefaultVolume));
        }
        foreach (CandleData pb in new[] { c1, c2, c3 })
        {
            longFlat.Add(pb with { Timestamp = PatternEngineTestBars.Bar(longFlat.Count, pb.Open, pb.High, pb.Low, pb.Close, pb.Volume).Timestamp });
        }
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, longFlat));

        // Custom PriceZoneLookbackBars = 20 adjusts MinBars to 23 and evaluates 20-bar zone cleanly on 43 bars.
        CoreCandlePatternParameter zone20Param = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleThreeWhiteSoldiers);
        Assert.Equal(60, zone20Param.PriceZoneLookbackBars);
        zone20Param.PriceZoneLookbackBars = 20;
        Assert.Equal(23, zone20Param.ComputeMinBars(3));
        Assert.True(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, BuildContextAndAppend(-1, c1, c2, c3), zone20Param));

        // T6 60-bar bottom-25% branch: 68 bars (>= 63 MinBars) fires with NO down-trend (even with RequirePriorTrend = false);
        // slicing 59 or 62 bars returns null -> false without down-trend.
        CandleData[] zoneBars = WithSixtyBarLowZoneNoDownTrend(
            MakeBar(201m, 203.2m, 200.8m, 203m),
            MakeBar(202m, 204.7m, 201.8m, 204.5m),
            MakeBar(203m, 206.2m, 202.8m, 206m));
        Assert.True(zoneBars.Length >= 63);
        Assert.True(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, zoneBars));
        CoreCandlePatternParameter zoneOnlyParam = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleThreeWhiteSoldiers);
        zoneOnlyParam.RequirePriorTrend = false;
        zoneOnlyParam.RequirePriceZone = true;
        Assert.True(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, zoneBars, zoneOnlyParam));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, zoneBars[^62..], zoneOnlyParam));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, zoneBars[^59..]));

        // Edges: O3 gap-up (> C2), U2 > 0.30*B2, H2 == H1, B3 < 0.70*AvgBody.
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, BuildContextAndAppend(-1, c1, c2, MakeBar(201.51m, 203.5m, 201.3m, 203m)), zone20Param));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, BuildContextAndAppend(-1, c1, MakeBar(199m, 202.26m, 198.8m, 201.5m), c3), zone20Param));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, BuildContextAndAppend(-1, MakeBar(198m, 201.7m, 197.8m, 200m), c2, c3), zone20Param));
        Assert.False(FiresLast(IndicatorType.CandleThreeWhiteSoldiers, BuildContextAndAppend(-1, MakeBar(198m, 200.2m, 197.8m, 199.39m), MakeBar(199m, 201.7m, 198.8m, 201.5m), c3), zone20Param));
    }

    [Fact]
    public void C3_ThreeBlackCrows_PositiveAndEdges()
    {
        CandleData c1 = MakeBar(202m, 202.2m, 199.8m, 200m);
        CandleData c2 = MakeBar(201m, 201.2m, 198.3m, 198.5m);
        CandleData c3 = MakeBar(200m, 200.2m, 196.8m, 197m);
        Assert.True(FiresLast(IndicatorType.CandleThreeBlackCrows, BuildContextAndAppend(1, c1, c2, c3)));
        Assert.False(FiresLast(IndicatorType.CandleThreeBlackCrows, BuildContextAndAppend(0, c1, c2, c3)));

        // Edges: O2 > O1, L2 == L1, D2 > 0.30*B2.
        Assert.False(FiresLast(IndicatorType.CandleThreeBlackCrows, BuildContextAndAppend(1, c1, MakeBar(202.01m, 202.2m, 198.3m, 198.5m), c3)));
        Assert.False(FiresLast(IndicatorType.CandleThreeBlackCrows, BuildContextAndAppend(1, MakeBar(202m, 202.2m, 198.3m, 200m), c2, c3)));
        Assert.False(FiresLast(IndicatorType.CandleThreeBlackCrows, BuildContextAndAppend(1, c1, MakeBar(201m, 201.2m, 197.74m, 198.5m), c3)));
    }

    [Fact]
    public void C3_SideBySideWhiteLines_BullBear_T3T4AndGap()
    {
        // Bullish (T3: up-trend + Pos_20 >= 0.60): body gap passes default, RequireWickToWickGap=true requires L2 > H1.
        CandleData b1 = MakeBar(199m, 201.2m, 198.8m, 201m);
        CandleData b2 = MakeBar(201.5m, 203.7m, 201.1m, 203.5m);
        CandleData b3 = MakeBar(201.8m, 204.2m, 201.6m, 204m);
        Assert.True(FiresLast(IndicatorType.CandleBullishSideBySideWhiteLines, BuildContextAndAppend(1, b1, b2, b3)));

        var strictWick = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishSideBySideWhiteLines);
        strictWick.RequireWickToWickGap = true;
        Assert.False(FiresLast(IndicatorType.CandleBullishSideBySideWhiteLines, BuildContextAndAppend(1, b1, b2, b3), strictWick));
        Assert.True(FiresLast(IndicatorType.CandleBullishSideBySideWhiteLines, BuildContextAndAppend(1, b1, MakeBar(201.5m, 203.7m, 201.25m, 203.5m), b3), strictWick));

        // Open tolerance boundary: |O3 - O2| <= 0.30*max(2,2) = 0.60 -> 0.60 passes, 0.61 fails.
        Assert.True(FiresLast(IndicatorType.CandleBullishSideBySideWhiteLines, BuildContextAndAppend(1, b1, b2, MakeBar(202.1m, 204.3m, 202.0m, 204.1m))));
        Assert.False(FiresLast(IndicatorType.CandleBullishSideBySideWhiteLines, BuildContextAndAppend(1, b1, b2, MakeBar(202.11m, 204.3m, 202.0m, 204.11m))));

        // Bearish (T4: down-trend + Pos_20 <= 0.40): BOTH c2 and c3 must be white (bullish) with real-body gap below C1!
        CandleData d1 = MakeBar(201m, 201.2m, 198.8m, 199m);
        CandleData d2 = MakeBar(196.5m, 198.7m, 196.3m, 198.5m);
        CandleData d3 = MakeBar(196.8m, 198.9m, 196.6m, 198.9m);
        Assert.True(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, d2, d3)));
        // Exact body-gap boundary (C1=199, 0.10*AvgBody=0.20 -> threshold 198.80 strict <): C2=198.80 or 198.90 (overlap gap < 0.20 even with O2=196.5) MUST fail; C2=198.79 passes!
        Assert.False(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, MakeBar(196.5m, 199.0m, 196.3m, 198.80m), MakeBar(196.8m, 198.9m, 196.6m, 198.9m))));
        Assert.False(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, MakeBar(196.5m, 199.0m, 196.3m, 198.90m), MakeBar(196.8m, 198.9m, 196.6m, 198.9m))));
        Assert.True(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, MakeBar(196.5m, 198.9m, 196.3m, 198.79m), MakeBar(196.8m, 198.9m, 196.6m, 198.9m))));
        Assert.False(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, d2, MakeBar(196.8m, 199.1m, 196.6m, 199.0m)))); // C3 == C1 fails
        Assert.False(FiresLast(IndicatorType.CandleBearishSideBySideWhiteLines, BuildContextAndAppend(-1, d1, d2, MakeBar(198.5m, 198.7m, 196.3m, 196.5m)))); // Bearish c3 fails
    }

    [Fact]
    public void C3_AbandonedBaby_BullBear_WickGapsAndStarDoji_IndependenceFromStars()
    {
        // Bullish (T1, RequireWickToWickGap=true, Vol=1.25): both CandleBullishAbandonedBaby and CandleMorningStar fire (no shadowing).
        CandleData ab1 = MakeBar(200m, 200.2m, 196.8m, 197m);
        CandleData ab2 = MakeBar(195.5m, 196.5m, 194.5m, 195.5m);
        CandleData ab3 = MakeBar(197m, 200.2m, 196.8m, 200m, volume: 1250L);
        var abBars = BuildContextAndAppend(-1, ab1, ab2, ab3);
        Assert.True(FiresLast(IndicatorType.CandleBullishAbandonedBaby, abBars));
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, abBars)); // independence: no shadowing

        // When RequireVolumeConfirmation = true, V=1249 fails AbandonedBaby (1.25) while MorningStar (1.20) still passes on V=1200!
        CoreCandlePatternParameter abVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishAbandonedBaby);
        abVolOn.RequireVolumeConfirmation = true;
        CoreCandlePatternParameter msVolOn = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleMorningStar);
        msVolOn.RequireVolumeConfirmation = true;
        var vol1200Bars = BuildContextAndAppend(-1, ab1, ab2, MakeBar(197m, 200.2m, 196.8m, 200m, volume: 1200L));
        Assert.False(FiresLast(IndicatorType.CandleBullishAbandonedBaby, vol1200Bars, abVolOn));
        Assert.True(FiresLast(IndicatorType.CandleBullishAbandonedBaby, abBars, abVolOn));
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, vol1200Bars, msVolOn));

        // Touching wicks (H2 == L1) fails strict AbandonedBaby, passes only with RequireWickToWickGap = false (body gap remains).
        var touchWick = BuildContextAndAppend(-1, ab1, MakeBar(195.5m, 196.8m, 194.5m, 195.5m), MakeBar(197m, 200.2m, 196.9m, 200m, volume: 1250L));
        Assert.False(FiresLast(IndicatorType.CandleBullishAbandonedBaby, touchWick));
        var relaxedGap = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishAbandonedBaby);
        relaxedGap.RequireWickToWickGap = false;
        Assert.True(FiresLast(IndicatorType.CandleBullishAbandonedBaby, touchWick, relaxedGap));

        // Non-doji c2 (B2 / R2 > 0.05) fails AbandonedBaby even with full wick gap.
        Assert.False(FiresLast(IndicatorType.CandleBullishAbandonedBaby, BuildContextAndAppend(-1, ab1, MakeBar(195.4m, 196.5m, 194.5m, 195.6m), ab3)));

        // Bearish Abandoned Baby (T2, Vol=1.25 symmetric default).
        var bearBars = BuildContextAndAppend(1,
            MakeBar(200m, 203.2m, 199.8m, 203m),
            MakeBar(204.5m, 205.5m, 203.5m, 204.5m),
            MakeBar(203m, 203.2m, 199.8m, 200m, volume: 1250L));
        Assert.True(FiresLast(IndicatorType.CandleBearishAbandonedBaby, bearBars));
        Assert.True(FiresLast(IndicatorType.CandleEveningStar, bearBars));
        CoreCandlePatternParameter bearBabyVol = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBearishAbandonedBaby);
        Assert.Equal(1.25m, bearBabyVol.VolumeMultiplier);
        bearBabyVol.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleBearishAbandonedBaby, bearBars, bearBabyVol));
        Assert.False(FiresLast(IndicatorType.CandleBearishAbandonedBaby, BuildContextAndAppend(1,
            MakeBar(200m, 203.2m, 199.8m, 203m),
            MakeBar(204.5m, 205.5m, 203.5m, 204.5m),
            MakeBar(203m, 203.2m, 199.8m, 200m, volume: 1249L)), bearBabyVol));
    }

    [Fact]
    public void C3_AdvanceBlock_PositiveAndEdges()
    {
        CandleData c1 = MakeBar(198m, 202.2m, 197.8m, 202m); // B1=4.0, U1=0.2
        CandleData c2 = MakeBar(200m, 203.8m, 199.8m, 203m); // B2=3.0, U2=0.8
        CandleData c3 = MakeBar(202m, 205.2m, 201.8m, 204m); // B3=2.0 <= 0.85*4=3.4, U3=1.2 >= 0.5*2 & U3 > U1
        Assert.True(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(1, c1, c2, c3)));
        Assert.False(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(-1, c1, c2, c3)));

        // Edges: B3 > 0.85*B1, B1 == B2, U3 == U1 (even when U2 >= 0.5*B2), short upper shadows on both c2 & c3.
        Assert.False(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(1,
            c1,
            MakeBar(200m, 204.5m, 199.8m, 203.6m),
            MakeBar(202m, 207.2m, 201.8m, 205.41m)))); // B3 = 3.41 > 3.40 fails
        Assert.False(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(1,
            c1,
            MakeBar(200m, 204.5m, 199.8m, 204.0m), // B2 == B1 fails
            MakeBar(202m, 207.2m, 201.8m, 204.5m))));
        Assert.False(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(1,
            MakeBar(198m, 203.2m, 197.8m, 202m), // U1=1.2
            MakeBar(200m, 204.6m, 199.8m, 203m), // U2=1.6 >= 1.5
            MakeBar(202m, 205.2m, 201.8m, 204m)))); // U3=1.2 <= U1 fails
        Assert.False(FiresLast(IndicatorType.CandleAdvanceBlock, BuildContextAndAppend(1,
            c1,
            c2,
            MakeBar(202m, 204.99m, 201.8m, 204m)))); // U3=0.99 < 0.5*2 fails
    }

    [Fact]
    public void C3_Deliberation_PositiveDojiC3AndEdges()
    {
        CandleData c1 = MakeBar(199m, 201.2m, 198.8m, 201m);   // B1=2.0
        CandleData c2 = MakeBar(200m, 202.7m, 199.8m, 202.5m); // B2=2.5
        CandleData c3 = MakeBar(202.2m, 203.2m, 202.0m, 203.0m); // B3=0.8 <= min(0.4*2.5=1.0, 0.5*2=1.0), O3=202.2 >= 202.5-0.3
        Assert.True(FiresLast(IndicatorType.CandleDeliberation, BuildContextAndAppend(1, c1, c2, c3)));

        // Slightly bearish doji c3 allowed (B3 <= DojiBodyRangeRatio * R3).
        Assert.True(FiresLast(IndicatorType.CandleDeliberation, BuildContextAndAppend(1, c1, c2, MakeBar(202.6m, 203.6m, 201.6m, 202.5m))));

        // Edges: bearish non-doji c3 fails; B3 = 1.01 fails; O3 = 202.19 fails.
        Assert.False(FiresLast(IndicatorType.CandleDeliberation, BuildContextAndAppend(1, c1, c2, MakeBar(202.8m, 203.0m, 202.1m, 202.3m))));
        Assert.False(FiresLast(IndicatorType.CandleDeliberation, BuildContextAndAppend(1, c1, c2, MakeBar(202.3m, 203.5m, 202.1m, 203.31m))));
        Assert.False(FiresLast(IndicatorType.CandleDeliberation, BuildContextAndAppend(1, c1, c2, MakeBar(202.19m, 203.2m, 202.0m, 202.9m))));
    }

    [Fact]
    public void C3_StickSandwich_PositiveAndCloseTolEdges()
    {
        // closeTol = max(0.05*2=0.10, 0.002*197=0.394) -> 0.394!
        CandleData s1 = MakeBar(200m, 200.2m, 196.8m, 197m);
        CandleData s2 = MakeBar(197.5m, 200.7m, 197.3m, 200.5m);
        CandleData s3Ok = MakeBar(200.3m, 200.5m, 196.9m, 197.39m);
        Assert.True(FiresLast(IndicatorType.CandleStickSandwich, BuildContextAndAppend(-1, s1, s2, s3Ok)));

        // Edges: |C3 - C1| = 0.40 > 0.394 fails; O3 = 200.29 < 200.3 fails; C3 = 197.51 > O2 fails; Wrong trend (+1) fails.
        Assert.False(FiresLast(IndicatorType.CandleStickSandwich, BuildContextAndAppend(-1, s1, s2, MakeBar(200.3m, 200.5m, 196.9m, 197.40m))));
        Assert.False(FiresLast(IndicatorType.CandleStickSandwich, BuildContextAndAppend(-1, s1, s2, MakeBar(200.29m, 200.5m, 196.9m, 197.2m))));
        Assert.False(FiresLast(IndicatorType.CandleStickSandwich, BuildContextAndAppend(-1, s1, s2, MakeBar(200.3m, 200.5m, 196.9m, 197.51m))));
        Assert.False(FiresLast(IndicatorType.CandleStickSandwich, BuildContextAndAppend(1, s1, s2, s3Ok)));
    }

    [Fact]
    public void C3_TasukiGap_And_GapThreeMethods_BullBear()
    {
        // Bullish (T3): c1 [199..201], c2 [201.5..203.5]
        CandleData b1 = MakeBar(199m, 201.2m, 198.8m, 201m);
        CandleData b2 = MakeBar(201.5m, 203.7m, 201.3m, 203.5m);
        var bullTasukiBars = BuildContextAndAppend(1, b1, b2, MakeBar(203.9m, 204.0m, 201.1m, 201.2m)); // O3 = 203.5+0.4 exact boundary; closes inside gap
        Assert.True(FiresLast(IndicatorType.CandleBullishTasukiGap, bullTasukiBars));
        Assert.False(FiresLast(IndicatorType.CandleBullishGapThreeMethods, bullTasukiBars));

        var bullFilledBars = BuildContextAndAppend(1, b1, b2, MakeBar(203.5m, 203.7m, 199.8m, 200m)); // closes inside c1 [199..201]
        Assert.True(FiresLast(IndicatorType.CandleBullishGapThreeMethods, bullFilledBars));
        Assert.False(FiresLast(IndicatorType.CandleBullishTasukiGap, bullFilledBars));

        // C3 closes below O1 -> both fail; O3 > 203.91 -> both fail; strict wick gap rejects body-only gap.
        Assert.False(FiresLast(IndicatorType.CandleBullishGapThreeMethods, BuildContextAndAppend(1, b1, b2, MakeBar(203.5m, 203.7m, 198.8m, 198.99m))));
        Assert.False(FiresLast(IndicatorType.CandleBullishTasukiGap, BuildContextAndAppend(1, b1, b2, MakeBar(203.91m, 204.0m, 201.1m, 201.2m))));

        // Bearish (T4): c1 [201->199], c2 [198.5->196.5]
        CandleData r1 = MakeBar(201m, 201.2m, 198.8m, 199m);
        CandleData r2 = MakeBar(198.5m, 198.7m, 196.3m, 196.5m);
        var bearTasukiBars = BuildContextAndAppend(-1, r1, r2, MakeBar(196.1m, 198.9m, 196.0m, 198.8m)); // O3 = 196.5-0.4 exact
        Assert.True(FiresLast(IndicatorType.CandleBearishTasukiGap, bearTasukiBars));
        Assert.False(FiresLast(IndicatorType.CandleBearishGapThreeMethods, bearTasukiBars));

        var bearFilledBars = BuildContextAndAppend(-1, r1, r2, MakeBar(196.5m, 200.2m, 196.3m, 200m));
        Assert.True(FiresLast(IndicatorType.CandleBearishGapThreeMethods, bearFilledBars));
        Assert.False(FiresLast(IndicatorType.CandleBearishTasukiGap, bearFilledBars));
    }

    [Fact]
    public void C3_IdenticalThreeCrows_PositiveEdges_IndependenceFromThreeBlackCrows()
    {
        CandleData i1 = MakeBar(202m, 202.2m, 199.7m, 200m);     // B1=2.0, D1=0.3 <= 0.15*2=0.30
        CandleData i2 = MakeBar(200.39m, 200.6m, 197.7m, 198m);  // B2=2.39, D2=0.3, |O2-C1|=0.39 <= 0.40
        CandleData i3 = MakeBar(198.35m, 198.6m, 195.8m, 196m);  // B3=2.35, D3=0.2, |O3-C2|=0.35 <= 0.396
        var sharedBars = BuildContextAndAppend(1, i1, i2, i3);
        Assert.True(FiresLast(IndicatorType.CandleIdenticalThreeCrows, sharedBars));
        Assert.True(FiresLast(IndicatorType.CandleThreeBlackCrows, sharedBars)); // independent flags: both fire

        // B=1.20 (>= 0.50*AvgBody=1.0, < 0.70*AvgBody=1.4): Identical fires, ThreeBlackCrows does NOT!
        var smallBodyCrows = BuildContextAndAppend(1,
            MakeBar(201.2m, 201.4m, 199.9m, 200m),
            MakeBar(200.0m, 200.2m, 198.7m, 198.8m),
            MakeBar(198.8m, 199.0m, 197.5m, 197.6m));
        Assert.True(FiresLast(IndicatorType.CandleIdenticalThreeCrows, smallBodyCrows));
        Assert.False(FiresLast(IndicatorType.CandleThreeBlackCrows, smallBodyCrows));

        // Lower shadow D1 = 0.31 > 0.15*2=0.30: Identical fails, ThreeBlackCrows (0.30*2=0.60) still fires!
        var bigLowerShadow = BuildContextAndAppend(1, MakeBar(202m, 202.2m, 199.69m, 200m), i2, i3);
        Assert.False(FiresLast(IndicatorType.CandleIdenticalThreeCrows, bigLowerShadow));
        Assert.True(FiresLast(IndicatorType.CandleThreeBlackCrows, bigLowerShadow));

        // Open deviation > max(0.05*AvgBody, 0.002*C1) = 0.40 fails Identical.
        Assert.False(FiresLast(IndicatorType.CandleIdenticalThreeCrows, BuildContextAndAppend(1, i1, MakeBar(200.41m, 200.6m, 197.7m, 198m), i3)));
    }

    [Fact]
    public void C3_InvalidBarInsideReadSetBlocks_OutsideIsLocal_CausalPrefixAndCancellation()
    {
        IndicatorType[] allPart2 =
        {
            IndicatorType.CandleMorningStar,
            IndicatorType.CandleEveningStar,
            IndicatorType.CandleThreeWhiteSoldiers,
            IndicatorType.CandleThreeBlackCrows,
            IndicatorType.CandleBullishSideBySideWhiteLines,
            IndicatorType.CandleBearishSideBySideWhiteLines,
            IndicatorType.CandleBullishAbandonedBaby,
            IndicatorType.CandleBearishAbandonedBaby,
            IndicatorType.CandleAdvanceBlock,
            IndicatorType.CandleDeliberation,
            IndicatorType.CandleStickSandwich,
            IndicatorType.CandleBullishTasukiGap,
            IndicatorType.CandleBearishTasukiGap,
            IndicatorType.CandleBullishGapThreeMethods,
            IndicatorType.CandleBearishGapThreeMethods,
            IndicatorType.CandleIdenticalThreeCrows,
        };

        // Invalid bar in c1 (pattern bar s = t-2) suppresses PatternBarResult; unrelated invalid inside first 5 context bars stays local.
        List<CandleData> validMs = BuildContextAndAppend(-1,
            MakeBar(200m, 200.2m, 195.8m, 196m),
            MakeBar(195.2m, 196.2m, 194.2m, 195.6m),
            MakeBar(196m, 199.1m, 195.9m, 199m, volume: 1200L));
        List<CandleData> invalidPattern = new(validMs);
        invalidPattern[40] = PatternEngineTestBars.Bar(40, 200m, 195m, 201m, 196m); // inverted H < L
        Assert.False(FiresLast(IndicatorType.CandleMorningStar, invalidPattern));

        List<CandleData> earlyInvalid = new(validMs);
        earlyInvalid[2] = PatternEngineTestBars.Bar(2, 200m, 195m, 201m, 196m);
        Assert.True(FiresLast(IndicatorType.CandleMorningStar, earlyInvalid));

        // Causality & cancellation across all 16 three-bar engines.
        CandleData[] walk = PatternEngineTestBars.Walk(seed: 19, count: 140);
        PatternSeriesContext fullCtx = PatternEngineTestBars.Context(walk);
        PatternSeriesContext prefixCtx = PatternEngineTestBars.Context(walk.Take(85).ToArray());

        foreach (IndicatorType type in allPart2)
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
