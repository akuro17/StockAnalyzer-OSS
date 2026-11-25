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
/// Spec-oracle unit and boundary tests for all 18 Run V2-8 candlestick engines
/// (<c>06_Candlestick_Patterns</c> 1-bar 9 + 2-bar 9; ledger <c>sa_pattern_literal_ledger_V2-8_CandlesPart1.md</c>).
/// Context generator sets <c>AvgBody_ctx = 2m</c>, <c>ATR_ctx = 4m</c>, <c>AvgVol = 1000</c>.
/// </summary>
public class CandlestickPatternEnginePart1Tests
{
    private static bool FiresLast(IndicatorType type, IReadOnlyList<CandleData> bars, CoreCandlePatternParameter? p = null)
    {
        p ??= CoreCandlePatternParameter.CreateFor(type);
        PatternBarResult[] res = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bars), type, p);
        return res[^1].BullishFires;
    }

    // ---- 1-bar (9 types) ----------------------------------------------------------------------------------------

    [Fact]
    public void C1_BullishBearishMarubozu_And_DefaultVolume()
    {
        // AvgBody=2, ATR=4 -> Marubozu minBody = max(1.5*2, 0.8*4) = 3.20, B/R >= 0.90, U,D <= 0.05*R; T0 requires no trend, Marubozu defaults RequireVolumeConfirmation=true (1.20*1000=1200).
        CandleData bullBar = MakeBar(200m, 210.4m, 199.6m, 210m, volume: 1200L); // R=10.8, B=10, B/R=0.926, U=D=0.4<=0.54
        var bullBars = BuildContextAndAppend(0, bullBar);
        Assert.True(FiresLast(IndicatorType.CandleBullishMarubozu, bullBars));
        Assert.False(FiresLast(IndicatorType.CandleBearishMarubozu, bullBars));

        // Volume below 1.20x (1199) fails default, passes when RequireVolumeConfirmation = false.
        var lowVol = BuildContextAndAppend(0, MakeBar(200m, 210.4m, 199.6m, 210m, volume: 1199L));
        Assert.False(FiresLast(IndicatorType.CandleBullishMarubozu, lowVol));
        CoreCandlePatternParameter volOff = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishMarubozu);
        volOff.RequireVolumeConfirmation = false;
        Assert.True(FiresLast(IndicatorType.CandleBullishMarubozu, lowVol, volOff));

        // Shadow > 0.05*R fails even with high volume.
        var bigWick = BuildContextAndAppend(0, MakeBar(200m, 211m, 200m, 210m, volume: 1500L)); // U = 1.0 > 0.05*11 = 0.55
        Assert.False(FiresLast(IndicatorType.CandleBullishMarubozu, bigWick));

        // Bearish mirror.
        var bearBars = BuildContextAndAppend(0, MakeBar(210m, 210.4m, 199.6m, 200m, volume: 1200L));
        Assert.True(FiresLast(IndicatorType.CandleBearishMarubozu, bearBars));
        Assert.False(FiresLast(IndicatorType.CandleBullishMarubozu, bearBars));
    }

    [Fact]
    public void C1_Volume_And_ZeroAvgBypass()
    {
        // AvgVol = 1000 > 0 and V_t = 0 fails when RequireVolumeConfirmation = true (R-8).
        var zeroDayVol = BuildContextAndAppend(0, MakeBar(200m, 210.4m, 199.6m, 210m, volume: 0L));
        Assert.False(FiresLast(IndicatorType.CandleBullishMarubozu, zeroDayVol));

        // Zero AvgVol instrument (FX / Index: every context bar has Volume = 0): bypassed!
        var fxBars = BuildContextAndAppend(0, MakeBar(200m, 210.4m, 199.6m, 210m, volume: 0L))
            .Select(b => b with { Volume = 0L }).ToArray();
        Assert.True(FiresLast(IndicatorType.CandleBullishMarubozu, fxBars));
    }

    [Fact]
    public void C1_Umbrella_PositiveAndEdges_Hammer_InvHammer_HangingMan_ShootingStar()
    {
        // AvgBody=2, ATR=4: Umbrella needs R >= 0.80*4 = 3.2, 0.05*R < B <= min(0.40*R, 0.80*2=1.6),
        // long shadow >= 2.0*B, opp shadow <= min(0.10*R, 0.50*B).
        // Pick R = 5.0 (minRange 3.2), B = 1.0 (0.25 < 1.0 <= 1.60), long = 3.8 (>= 2.0), opp = 0.2 (<= min(0.5, 0.5)).
        // Bottom downtrend: C_39 = 200. Hammer at bottom: H = 199.2, C = 199.0, O = 198.0, L = 194.2 -> R=5.0, B=1.0, D=3.8, U=0.2, Pos=0.0!
        var hammerBars = BuildContextAndAppend(-1, MakeBar(198m, 199.2m, 194.2m, 199m));
        Assert.True(FiresLast(IndicatorType.CandleHammer, hammerBars));
        Assert.False(FiresLast(IndicatorType.CandleInvertedHammer, hammerBars));
        Assert.False(FiresLast(IndicatorType.CandleHangingMan, hammerBars));

        // Exact boundary checks on Hammer:
        // 1. No prior downtrend -> fails!
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(1, MakeBar(198m, 199.2m, 194.2m, 199m))));
        // 2. Wrong zone (way above bottom range) -> fails!
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(-1, MakeBar(250m, 251.2m, 246.2m, 251m))));
        // 3. Body too big (> 1.6 = 0.80*AvgBody) -> B = 1.61 fails.
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(-1, MakeBar(197.39m, 199.2m, 194.2m, 199m))));
        // 4. Primary shadow < 2.0*B (B = 1.4, long = 2.79 < 2.80, opp = 0.1) -> fails.
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(-1, MakeBar(197.6m, 199.1m, 194.81m, 199m))));
        // 5. Range < 0.80*ATR (3.19 < 3.20, B = 0.6, long = 2.50, opp = 0.09) -> fails.
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(-1, MakeBar(197.4m, 198.09m, 194.9m, 198m))));
        // 6. Doji body B == 0.05*R fails (> 0.05*R strict).
        Assert.False(FiresLast(IndicatorType.CandleHammer, BuildContextAndAppend(-1, MakeBar(198.75m, 199.2m, 194.2m, 199m)))); // B=0.25 == 0.05*5.0

        // Inverted Hammer (T1, long upper shadow): L = 194.2, O = 194.4, C = 195.4, H = 199.2.
        var invHammerBars = BuildContextAndAppend(-1, MakeBar(194.4m, 199.2m, 194.2m, 195.4m));
        Assert.True(FiresLast(IndicatorType.CandleInvertedHammer, invHammerBars));
        Assert.False(FiresLast(IndicatorType.CandleShootingStar, invHammerBars));

        // Hanging Man (T2 uptrend + top zone, long lower shadow): C_39 = 200 -> O = 206, C = 207, H = 207.2, L = 202.2 (Pos=1.0).
        var hangingManBars = BuildContextAndAppend(1, MakeBar(206m, 207.2m, 202.2m, 207m));
        Assert.True(FiresLast(IndicatorType.CandleHangingMan, hangingManBars));
        Assert.False(FiresLast(IndicatorType.CandleHammer, hangingManBars));

        // Shooting Star (T2 uptrend + top zone, long upper shadow): O = 201.4, C = 200.4, L = 200.2, H = 205.2 (Pos=1.0).
        var shootingStarBars = BuildContextAndAppend(1, MakeBar(201.4m, 205.2m, 200.2m, 200.4m));
        Assert.True(FiresLast(IndicatorType.CandleShootingStar, shootingStarBars));
        Assert.False(FiresLast(IndicatorType.CandleInvertedHammer, shootingStarBars));
    }

    [Fact]
    public void C1_Umbrella_LongOppShadows_And_StrictColor()
    {
        // Hammer with bearish body (O=199, C=198): default StrictColorMatch=false accepts, true rejects!
        var bearishBodyHammer = BuildContextAndAppend(-1, MakeBar(199m, 199.2m, 194.2m, 198m));
        var pDefault = (CoreUmbrellaPatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleHammer);
        Assert.True(FiresLast(IndicatorType.CandleHammer, bearishBodyHammer, pDefault));

        var pStrict = new CoreUmbrellaPatternParameter { StrictColorMatch = true };
        Assert.False(FiresLast(IndicatorType.CandleHammer, bearishBodyHammer, pStrict));

        // LongShadowBodyMultiplier = 3.0 accepts 3.8/1.0=3.8, rejects when set to 4.0.
        var pLong4 = new CoreUmbrellaPatternParameter { LongShadowBodyMultiplier = 4.0m };
        Assert.False(FiresLast(IndicatorType.CandleHammer, bearishBodyHammer, pLong4));
    }

    [Fact]
    public void C1_Doji_Cross_Dragonfly_Gravestone_PositiveAndEdges()
    {
        // CrossDoji (T5): R >= 0.50*4 = 2.0, B <= 0.05*R, U > 0.15*R, D > 0.15*R, trend + extreme Pos >= 0.80 or <= 0.20.
        var crossLow = BuildContextAndAppend(-1, MakeBar(197m, 199m, 195m, 197.1m)); // R=4, B=0.1<=0.2, U=1.9>0.6, D=2.0>0.6, Pos(Low)=0
        var crossHigh = BuildContextAndAppend(1, MakeBar(205m, 207m, 203m, 205.1m)); // Pos(High)=1.0
        var crossFlat = BuildContextAndAppend(0, MakeBar(200m, 202m, 198m, 200.1m)); // no trend
        var crossNoLower = BuildContextAndAppend(-1, MakeBar(195.6m, 199m, 195m, 195.6m)); // D = 0.60 == 0.15*4.0 -> fails strict >

        Assert.True(FiresLast(IndicatorType.CandleCrossDoji, crossLow));
        Assert.True(FiresLast(IndicatorType.CandleCrossDoji, crossHigh));
        Assert.False(FiresLast(IndicatorType.CandleCrossDoji, crossFlat));
        Assert.False(FiresLast(IndicatorType.CandleCrossDoji, crossNoLower));

        // Dragonfly Doji (T1): R >= 0.80*4 = 3.2, B <= 0.05*R, U <= 0.05*R, D >= 0.70*R.
        var dragonfly = BuildContextAndAppend(-1, MakeBar(199m, 199.1m, 195m, 199.1m)); // R=4.1, B=0.1<=0.205, U=0, D=4.0>=2.87
        Assert.True(FiresLast(IndicatorType.CandleDragonflyDoji, dragonfly));
        var dfShortLower = BuildContextAndAppend(-1, MakeBar(197.9m, 198m, 194m, 198m)); // R=4, D=3.9->wait want D < 0.70*4 = 2.8 => B=0.1, U=1.2... D = 2.79 -> fail opp shadow too, make Dragonfly fail via R < 3.2:
        Assert.False(FiresLast(IndicatorType.CandleDragonflyDoji, BuildContextAndAppend(-1, MakeBar(198.0m, 198.1m, 195.0m, 198.1m)))); // R=3.1 < 3.2

        // Gravestone Doji (T2): R >= 3.2, B <= 0.05*R, D <= 0.05*R, U >= 0.70*R.
        var gravestone = BuildContextAndAppend(1, MakeBar(203m, 207.1m, 203m, 203.1m));
        Assert.True(FiresLast(IndicatorType.CandleGravestoneDoji, gravestone));
        Assert.False(FiresLast(IndicatorType.CandleGravestoneDoji, dragonfly));
    }

    // ---- 2-bar (9 types) ----------------------------------------------------------------------------------------

    [Fact]
    public void C2_BullishBearishEngulfing_PositiveEdgesAndVolume()
    {
        // Bullish Engulfing (T1): e = t - 2 -> C_39 = 200 -> Wait: c1 = bar 40, c2 = bar 41, e = 39!
        // c1: bear, B1 >= 0.30 * 2.0 = 0.60; c2: bull, B2 >= 1.00 * 2.0 = 2.0, O2 <= C1 < O1 <= C2, B2 > B1.
        CandleData c1 = MakeBar(199m, 199.5m, 197.5m, 198m, volume: 1000L); // B1 = 1.0 >= 0.60
        CandleData c2 = MakeBar(197.8m, 201m, 197m, 200.5m, volume: 1200L); // B2 = 2.7 >= 2.0, Pos(197, 41, 20) = 0
        var bullBars = BuildContextAndAppend(-1, c1, c2);
        Assert.True(FiresLast(IndicatorType.CandleBullishEngulfing, bullBars));

        // c1 body < 0.30 * 2.0 = 0.60 -> B1 = 0.59 fails.
        Assert.False(FiresLast(IndicatorType.CandleBullishEngulfing,
            BuildContextAndAppend(-1, MakeBar(198.59m, 199m, 197.5m, 198m), c2)));

        // Non-engulfing O2 > C1 fails.
        Assert.False(FiresLast(IndicatorType.CandleBullishEngulfing,
            BuildContextAndAppend(-1, c1, MakeBar(198.1m, 201m, 197m, 200.5m))));

        // Volume option: requires V2 >= 1.20 * V1 (1200) AND V2 >= 1.10 * AvgVol (1100).
        CoreCandlePatternParameter volOpt = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishEngulfing);
        volOpt.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleBullishEngulfing, bullBars, volOpt));
        var lowRelVol = BuildContextAndAppend(-1, c1, MakeBar(197.8m, 201m, 197m, 200.5m, volume: 1199L));
        Assert.False(FiresLast(IndicatorType.CandleBullishEngulfing, lowRelVol, volOpt));

        // Bearish Engulfing (T2): symmetric volume check requires V2 >= 1.20*V1 AND V2 >= VolumeMultiplier*AvgVol(20) (default 1.10).
        var bearBars = BuildContextAndAppend(1,
            MakeBar(202m, 203.5m, 201.5m, 203m, volume: 1000L),
            MakeBar(203.2m, 204m, 200m, 200.5m, volume: 1200L));
        Assert.True(FiresLast(IndicatorType.CandleBearishEngulfing, bearBars));
        CoreCandlePatternParameter bearVol = CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBearishEngulfing);
        Assert.Equal(1.10m, bearVol.VolumeMultiplier);
        bearVol.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleBearishEngulfing, bearBars, bearVol));

        // Low volume relative to 20-bar average (< 1.10*1000 = 1100) must fail even when V2 >= 1.20*V1 (V1=500, V2=1099)!
        var lowBearAvgVol = BuildContextAndAppend(1,
            MakeBar(202m, 203.5m, 201.5m, 203m, volume: 500L),
            MakeBar(203.2m, 204m, 200m, 200.5m, volume: 1099L));
        Assert.False(FiresLast(IndicatorType.CandleBearishEngulfing, lowBearAvgVol, bearVol));

        // Explicit VolumeMultiplier sensitivity & R-8 zero-volume instrument bypass:
        bearVol.VolumeMultiplier = 1.50m;
        Assert.False(FiresLast(IndicatorType.CandleBearishEngulfing, bearBars, bearVol));
        var fxBearBars = bearBars.Select(b => b with { Volume = 0L }).ToArray();
        Assert.True(FiresLast(IndicatorType.CandleBearishEngulfing, fxBearBars, bearVol));
    }

    [Fact]
    public void C2_HaramiAndHomingPigeon_PositiveAndStrictEdges()
    {
        // c1: IsLarge (B1 >= 3.2? wait LargeBody min = max(3.0, 2.4) = 3.0, B1/R1 >= 0.60). Pick O1=199, C1=194 (B1=5, R1=6, B/R=0.833).
        // c2 Harami: IsSmall (B2 <= 1.6, B2/R2 <= 0.50), B2 <= 0.50 * B1 = 2.5, strict C1 < O2 < C2 < O1.
        CandleData c1Bear = MakeBar(199m, 199.5m, 193.5m, 194m);
        CandleData c2Bull = MakeBar(195m, 197.5m, 194m, 196.2m); // B2=1.2<=1.6, R2=3.5, B2/R2=0.343<=0.50, 194<195<196.2<199
        Assert.True(FiresLast(IndicatorType.CandleBullishHarami, BuildContextAndAppend(-1, c1Bear, c2Bull)));

        // Strict inequality boundary: O2 == C1 (194 == 194) MUST fail in default Phase 1 mode!
        Assert.False(FiresLast(IndicatorType.CandleBullishHarami,
            BuildContextAndAppend(-1, c1Bear, MakeBar(194m, 197m, 193.8m, 195.2m))));

        // Homing Pigeon (T1): c1 large bear, c2 small bear with strict C1 < C2 < O2 < O1.
        CandleData c2SmallBear = MakeBar(196.2m, 197.5m, 194m, 195m);
        Assert.True(FiresLast(IndicatorType.CandleHomingPigeon, BuildContextAndAppend(-1, c1Bear, c2SmallBear)));
        Assert.False(FiresLast(IndicatorType.CandleBullishHarami, BuildContextAndAppend(-1, c1Bear, c2SmallBear)));

        // Bearish Harami (T2):
        CandleData c1Bull = MakeBar(202m, 208.5m, 201.5m, 207m);
        CandleData c2Bear = MakeBar(205.5m, 207m, 203.5m, 204.3m); // 202 < 204.3 < 205.5 < 207
        Assert.True(FiresLast(IndicatorType.CandleBearishHarami, BuildContextAndAppend(1, c1Bull, c2Bear)));
    }

    [Fact]
    public void C2_PiercingLineAndDarkCloudCover_PositiveAndEdges()
    {
        // Bullish Piercing Line (T1): c1 large bear (O1=199, H1=199.5, L1=193.5, C1=194, mid=196.5),
        // c2 bull with O2 < L1 (193.0 < 193.5) and mid(196.5) < C2 < O1(199). Volume optional 1.15x.
        CandleData c1Bear = MakeBar(199m, 199.5m, 193.5m, 194m);
        CandleData c2Pierce = MakeBar(193m, 198m, 192.5m, 197m, volume: 1150L);
        var pierceBars = BuildContextAndAppend(-1, c1Bear, c2Pierce);
        Assert.True(FiresLast(IndicatorType.CandlePiercingLine, pierceBars));

        // Equal midpoint C2 == 196.5 fails strict >; O2 == L1 (193.5) fails strict <.
        Assert.False(FiresLast(IndicatorType.CandlePiercingLine, BuildContextAndAppend(-1, c1Bear, MakeBar(193m, 197m, 192.5m, 196.5m))));
        Assert.False(FiresLast(IndicatorType.CandlePiercingLine, BuildContextAndAppend(-1, c1Bear, MakeBar(193.5m, 198m, 192.5m, 197m))));

        // Volume 1.15x check:
        CoreCandlePatternParameter pVol = CoreCandlePatternParameter.CreateFor(IndicatorType.CandlePiercingLine);
        pVol.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandlePiercingLine, pierceBars, pVol));
        Assert.False(FiresLast(IndicatorType.CandlePiercingLine, BuildContextAndAppend(-1, c1Bear, MakeBar(193m, 198m, 192.5m, 197m, volume: 1149L)), pVol));

        // Bearish Dark Cloud Cover (T2): c1 large bull (O1=202, H1=208.5, L1=201.5, C1=207, mid=204.5), c2 O2=209 > 208.5, C2=204 < 204.5.
        CandleData c1Bull = MakeBar(202m, 208.5m, 201.5m, 207m);
        var darkBars = BuildContextAndAppend(1, c1Bull, MakeBar(209m, 209.5m, 203.5m, 204m));
        Assert.True(FiresLast(IndicatorType.CandleDarkCloudCover, darkBars));
    }

    [Fact]
    public void C2_Kicking_BullishBearish_PositiveAndEdges()
    {
        // AvgBody=2, ATR=4 -> Marubozu minBody = 3.20, Kicking min wick gap = max(0.20*2, 0.50*4) = 2.00m.
        // Bullish Kicking (T0): c1 bear Marubozu (O=206, H=206.2, L=200.8, C=201 -> B=5, R=5.4, B/R=0.926, U=D=0.2<=0.27),
        // c2 bull Marubozu with L2 - H1 >= 2.00 => H1 = 206.2 => L2 = 208.20 exact edge!
        CandleData c1BearMaru = MakeBar(206m, 206.2m, 200.8m, 201m);
        CandleData c2BullMaruPass = MakeBar(208.4m, 214m, 208.2m, 213.8m, volume: 1300L); // L2 - H1 = 2.00 exact, R=5.8, B=5.4, U=D=0.2<=0.29
        CandleData c2BullMaruFail = MakeBar(208.4m, 214m, 208.19m, 213.8m, volume: 1300L); // L2 - H1 = 1.99 < 2.00

        var passBars = BuildContextAndAppend(0, c1BearMaru, c2BullMaruPass);
        Assert.True(FiresLast(IndicatorType.CandleBullishKicking, passBars));
        Assert.False(FiresLast(IndicatorType.CandleBullishKicking, BuildContextAndAppend(0, c1BearMaru, c2BullMaruFail)));

        // Turning RequireWickToWickGap = false allows body-gap fallback (O2 - C1 = 7.4 > 0.10 * 2.0).
        var bodyGapParam = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishKicking);
        bodyGapParam.RequireWickToWickGap = false;
        Assert.True(FiresLast(IndicatorType.CandleBullishKicking, BuildContextAndAppend(0, c1BearMaru, c2BullMaruFail), bodyGapParam));

        // Volume multiplier 1.30 (default off -> 1000 passes; on -> 1300 passes, 1299 fails).
        var volParam = (CoreGapCandlePatternParameter)CoreCandlePatternParameter.CreateFor(IndicatorType.CandleBullishKicking);
        volParam.RequireVolumeConfirmation = true;
        Assert.True(FiresLast(IndicatorType.CandleBullishKicking, passBars, volParam));
        Assert.False(FiresLast(IndicatorType.CandleBullishKicking, BuildContextAndAppend(0, c1BearMaru, MakeBar(208.4m, 214m, 208.2m, 213.8m, volume: 1299L)), volParam));

        // Bearish Kicking (T0): c1 bull Marubozu (O=201, L=200.8, H=206.2, C=206), c2 bear Marubozu with L1 - H2 >= 2.00 => H2 = 198.8.
        CandleData c1BullMaru = MakeBar(201m, 206.2m, 200.8m, 206m);
        CandleData c2BearMaru = MakeBar(198.6m, 198.8m, 193m, 193.2m); // L1 - H2 = 200.8 - 198.8 = 2.00 exact, B=5.4, R=5.8, U=D=0.2
        Assert.True(FiresLast(IndicatorType.CandleBearishKicking, BuildContextAndAppend(0, c1BullMaru, c2BearMaru)));
    }

    [Fact]
    public void C2_Independence_NoShadowing()
    {
        // A Bullish Engulfing bar where c2 is ALSO a Bullish Marubozu with high volume MUST fire BOTH flags on bar t (no shadowing!).
        CandleData c1 = MakeBar(199m, 199.5m, 197.5m, 198m, volume: 1000L);
        CandleData c2 = MakeBar(197.5m, 203.2m, 197.3m, 203m, volume: 1300L); // B2=5.5, R2=5.9, U=D=0.2 <= 0.295, B/R=0.932 >= 0.90 (Marubozu AND Engulfing!)
        var bars = BuildContextAndAppend(-1, c1, c2);

        Assert.True(FiresLast(IndicatorType.CandleBullishEngulfing, bars));
        Assert.True(FiresLast(IndicatorType.CandleBullishMarubozu, bars));
    }

    [Fact]
    public void C1_InvalidBar_Locality_PrefixInvariance_Cancellation()
    {
        var bars = BuildContextAndAppend(-1, MakeBar(198m, 199.2m, 194.2m, 199m)).ToArray();

        // 1. Corrupt constituent bar (40) -> 0m without exception.
        Assert.False(FiresLast(IndicatorType.CandleHammer, PatternEngineTestBars.Corrupt(bars, 40)));

        // 2. Corrupt ATR context bar (35 in [25..39]) -> 0m.
        Assert.False(FiresLast(IndicatorType.CandleHammer, PatternEngineTestBars.Corrupt(bars, 35)));

        // 3. Corrupt bar BEFORE EMA10(39)/ATR(39)/Zone(40) read sets -> bar index 5 leaves bar 40 completely UNAFFECTED (plan R-16)!
        Assert.True(FiresLast(IndicatorType.CandleHammer, PatternEngineTestBars.Corrupt(bars, 5)));

        // 4. Causal prefix invariance across all 18 types: adding 10 bars never changes any bar 0..N-1.
        CandleData[] walk = PatternEngineTestBars.Walk(5, 120);
        CandleData[] prefix = walk.Take(80).ToArray();
        IndicatorType[] all18 =
        {
            IndicatorType.CandleBullishMarubozu, IndicatorType.CandleBearishMarubozu,
            IndicatorType.CandleHammer, IndicatorType.CandleInvertedHammer,
            IndicatorType.CandleHangingMan, IndicatorType.CandleShootingStar,
            IndicatorType.CandleCrossDoji, IndicatorType.CandleDragonflyDoji, IndicatorType.CandleGravestoneDoji,
            IndicatorType.CandleBullishEngulfing, IndicatorType.CandleBearishEngulfing,
            IndicatorType.CandleBullishHarami, IndicatorType.CandleBearishHarami,
            IndicatorType.CandlePiercingLine, IndicatorType.CandleDarkCloudCover,
            IndicatorType.CandleHomingPigeon,
            IndicatorType.CandleBullishKicking, IndicatorType.CandleBearishKicking,
        };

        foreach (IndicatorType t in all18)
        {
            CoreCandlePatternParameter p = CoreCandlePatternParameter.CreateFor(t);
            PatternBarResult[] full = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(walk), t, p);
            PatternBarResult[] part = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(prefix), t, p);
            for (int i = 0; i < prefix.Length; i++)
            {
                Assert.Equal(full[i], part[i]);
            }
        }

        // 5. Cancellation throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bars), IndicatorType.CandleHammer, CoreCandlePatternParameter.CreateFor(IndicatorType.CandleHammer), cts.Token));
    }
}
