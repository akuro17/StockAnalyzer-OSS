using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Property-based symmetry and prefix-cache invariants for Candlestick and Market Structure pattern engines (Task 3-A).
/// Verifies non-vacuous vertical price reflection (<c>M(P) = K - P</c>) across all 15 mirror candlestick pairs,
/// exact parity of the O(N) <see cref="PatternSeriesContext"/> ATR/AvgBody prefix cache with <see cref="CandlePatternDetector.ComputeContext"/>,
/// and multi-seed HH/HL vs LL/LH mutual exclusion and mirror symmetry.
/// </summary>
public class PatternEngineSymmetryPropertyTests
{
    private const decimal ReflectionAxis = 4000m;

    private static CandleData[] Reflect(IReadOnlyList<CandleData> bars, decimal axis = ReflectionAxis)
    {
        var mirrored = new CandleData[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            CandleData b = bars[i];
            mirrored[i] = new CandleData(b.Timestamp, axis - b.Open, axis - b.Low, axis - b.High, axis - b.Close, b.Volume);
        }

        return mirrored;
    }

    private static CandleData[] PositiveTail(IndicatorType type)
    {
        CandleData[] fromBuilder = CandleFixtureBuilder.Part1Tail(type);
        if (fromBuilder.Length > 0)
        {
            return fromBuilder;
        }

        return type switch
        {
            IndicatorType.CandleBullishMarubozu
                => CandleFixtureBuilder.BuildContextAndAppend(0, CandleFixtureBuilder.MakeBar(200m, 210.4m, 199.6m, 210m, 1200L)).ToArray(),
            IndicatorType.CandleHammer
                => CandleFixtureBuilder.BuildContextAndAppend(-1, CandleFixtureBuilder.MakeBar(198m, 199.2m, 194.2m, 199m, 1250L)).ToArray(),
            IndicatorType.CandleShootingStar
                => Reflect(PositiveTail(IndicatorType.CandleHammer)),
            IndicatorType.CandleInvertedHammer
                => CandleFixtureBuilder.BuildContextAndAppend(-1, CandleFixtureBuilder.MakeBar(195m, 199.8m, 194.8m, 195.8m)).ToArray(),
            IndicatorType.CandleHangingMan
                => Reflect(PositiveTail(IndicatorType.CandleInvertedHammer)),
            IndicatorType.CandleBullishEngulfing
                => CandleFixtureBuilder.BuildContextAndAppend(-1, CandleFixtureBuilder.MakeBar(200m, 200.2m, 197.8m, 198m), CandleFixtureBuilder.MakeBar(197.5m, 201.2m, 197.3m, 201m, 1100L)).ToArray(),
            IndicatorType.CandleBearishEngulfing
                => Reflect(PositiveTail(IndicatorType.CandleBullishEngulfing)),
            IndicatorType.CandleBullishHarami
                => CandleFixtureBuilder.BuildContextAndAppend(-1, CandleFixtureBuilder.MakeBar(199m, 199.5m, 193.5m, 194m), CandleFixtureBuilder.MakeBar(195m, 197.5m, 194m, 196.2m)).ToArray(),
            IndicatorType.CandleBearishHarami
                => Reflect(PositiveTail(IndicatorType.CandleBullishHarami)),
            IndicatorType.CandlePiercingLine
                => CandleFixtureBuilder.BuildContextAndAppend(-1, CandleFixtureBuilder.MakeBar(200m, 200.2m, 195.8m, 196m), CandleFixtureBuilder.MakeBar(195m, 198.6m, 194.8m, 198.5m, 1250L)).ToArray(),
            IndicatorType.CandleDarkCloudCover
                => Reflect(PositiveTail(IndicatorType.CandlePiercingLine)),
            _ => Array.Empty<CandleData>(),
        };
    }

    public static IEnumerable<object[]> SymmetricCandlePairs()
    {
        yield return new object[] { IndicatorType.CandleBullishMarubozu, IndicatorType.CandleBearishMarubozu };
        yield return new object[] { IndicatorType.CandleHammer, IndicatorType.CandleShootingStar };
        yield return new object[] { IndicatorType.CandleInvertedHammer, IndicatorType.CandleHangingMan };
        yield return new object[] { IndicatorType.CandleDragonflyDoji, IndicatorType.CandleGravestoneDoji };
        yield return new object[] { IndicatorType.CandleBullishEngulfing, IndicatorType.CandleBearishEngulfing };
        yield return new object[] { IndicatorType.CandleBullishHarami, IndicatorType.CandleBearishHarami };
        yield return new object[] { IndicatorType.CandlePiercingLine, IndicatorType.CandleDarkCloudCover };
        yield return new object[] { IndicatorType.CandleBullishKicking, IndicatorType.CandleBearishKicking };
        yield return new object[] { IndicatorType.CandleMorningStar, IndicatorType.CandleEveningStar };
        yield return new object[] { IndicatorType.CandleBullishAbandonedBaby, IndicatorType.CandleBearishAbandonedBaby };
        yield return new object[] { IndicatorType.CandleBullishTasukiGap, IndicatorType.CandleBearishTasukiGap };
        yield return new object[] { IndicatorType.CandleBullishGapThreeMethods, IndicatorType.CandleBearishGapThreeMethods };
        yield return new object[] { IndicatorType.CandleBullishThreeLineStrike, IndicatorType.CandleBearishThreeLineStrike };
        yield return new object[] { IndicatorType.CandleRisingThreeMethods, IndicatorType.CandleFallingThreeMethods };
        yield return new object[] { IndicatorType.CandleBullishBreakaway, IndicatorType.CandleBearishBreakaway };
    }

    [Theory]
    [MemberData(nameof(SymmetricCandlePairs))]
    public void CP_MirrorPairs_VerticalReflectionSymmetry_IsExact_AndNonVacuous(IndicatorType bullType, IndicatorType bearType)
    {
        CoreCandlePatternParameter bullParam = CoreCandlePatternParameter.CreateFor(bullType);
        CoreCandlePatternParameter bearParam = CoreCandlePatternParameter.CreateFor(bearType);

        // 1. Positive non-vacuous fixture test: positive bull tail -> reflected fires bear; positive bear tail -> reflected fires bull.
        CandleData[] bullTail = PositiveTail(bullType);
        CandleData[] bearTail = PositiveTail(bearType);
        Assert.NotEmpty(bullTail);
        Assert.NotEmpty(bearTail);

        PatternBarResult[] bullOnBullTail = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bullTail), bullType, bullParam);
        PatternBarResult[] bearOnMirroredBullTail = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(Reflect(bullTail)), bearType, bearParam);
        Assert.True(bullOnBullTail[^1].BullishFires, $"{bullType} should fire on its fixture tail.");
        Assert.Equal(bullOnBullTail.Select(r => r.BullishFires), bearOnMirroredBullTail.Select(r => r.BullishFires));

        PatternBarResult[] bearOnBearTail = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(bearTail), bearType, bearParam);
        PatternBarResult[] bullOnMirroredBearTail = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(Reflect(bearTail)), bullType, bullParam);
        Assert.True(bearOnBearTail[^1].BullishFires, $"{bearType} should fire on its fixture tail.");
        Assert.Equal(bearOnBearTail.Select(r => r.BullishFires), bullOnMirroredBearTail.Select(r => r.BullishFires));

        // 2. Random-walk series symmetry (with identical volume multiplier so volume-gated pairs match too, and both wick/body gap modes where applicable).
        decimal sharedVolumeMult = Math.Max(bullParam.VolumeMultiplier, bearParam.VolumeMultiplier);
        foreach (bool requireVol in new[] { false, true })
        {
            bullParam.RequireVolumeConfirmation = requireVol;
            bearParam.RequireVolumeConfirmation = requireVol;
            bullParam.VolumeMultiplier = sharedVolumeMult;
            bearParam.VolumeMultiplier = sharedVolumeMult;

            bool[] gapModes = bullParam is CoreGapCandlePatternParameter ? new[] { true, false } : new[] { true };
            foreach (bool requireWick in gapModes)
            {
                if (bullParam is CoreGapCandlePatternParameter bg && bearParam is CoreGapCandlePatternParameter rg)
                {
                    bg.RequireWickToWickGap = requireWick;
                    rg.RequireWickToWickGap = requireWick;
                }

                foreach (int seed in new[] { 1, 7, 13 })
                {
                    CandleData[] walk = PatternEngineTestBars.Walk(seed, 250);
                    PatternBarResult[] bullRes = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(walk), bullType, bullParam);
                    PatternBarResult[] bearMirrorRes = CandlestickPatternEngine.Run(PatternEngineTestBars.Context(Reflect(walk)), bearType, bearParam);
                    for (int t = 0; t < walk.Length; t++)
                    {
                        Assert.Equal(bullRes[t], bearMirrorRes[t]);
                    }
                }
            }
        }
    }

    [Fact]
    public void PS_ContextPrefixCache_MatchesComputeContextBitwise_NormalExtremeAndInvalid()
    {
        // 1. Normal random walk with periods 14 and 200 across every bar.
        CandleData[] walk = PatternEngineTestBars.Walk(19, 320);
        PatternSeriesContext walkCtx = PatternEngineTestBars.Context(walk);
        foreach (int period in new[] { 14, 200 })
        {
            for (int e = 0; e < walk.Length; e++)
            {
                bool actualOk = PatternMath.TryAtrCtx(walkCtx, e, period, out decimal actualAtr, out decimal actualAvgBody);
                bool expectedOk = e >= period;
                Assert.Equal(expectedOk, actualOk);
                if (expectedOk)
                {
                    (decimal expAvgBody, decimal expAtr) = CandlePatternDetector.ComputeContext(walk, e - period + 1, e);
                    Assert.Equal(expAvgBody, actualAvgBody);
                    Assert.Equal(expAtr, actualAtr);
                }
            }
        }

        // 2. Corrupt invalid bar at index 30 -> windows [e - 14 .. e] covering 30 (30 <= e <= 44) return false; before (29) and after (45) stay exact.
        CandleData[] corrupted = PatternEngineTestBars.Corrupt(walk, 30);
        PatternSeriesContext corruptCtx = PatternEngineTestBars.Context(corrupted);
        Assert.True(PatternMath.TryAtrCtx(corruptCtx, 29, 14, out _, out _));
        for (int e = 30; e <= 44; e++)
        {
            Assert.False(PatternMath.TryAtrCtx(corruptCtx, e, 14, out _, out _));
        }

        Assert.True(PatternMath.TryAtrCtx(corruptCtx, 45, 14, out decimal afterAtr, out decimal afterAvg));
        (decimal expAfterAvg, decimal expAfterAtr) = CandlePatternDetector.ComputeContext(corrupted, 32, 45);
        Assert.Equal(expAfterAvg, afterAvg);
        Assert.Equal(expAfterAtr, afterAtr);

        // 3. Large bar magnitude (> 1e12 fallback path) stays exact and does not overflow.
        CandleData[] huge = new CandleData[30];
        DateTime start = new(2020, 1, 1);
        for (int i = 0; i < huge.Length; i++)
        {
            decimal p = 5_000_000_000_000m + (i * 1_000_000m);
            huge[i] = PatternEngineTestBars.Bar(i, p, p + 500_000m, p - 500_000m, p + 250_000m, 1_000L);
        }

        PatternSeriesContext hugeCtx = PatternEngineTestBars.Context(huge);
        Assert.True(PatternMath.TryAtrCtx(hugeCtx, 25, 14, out decimal hugeAtr, out decimal hugeAvg));
        (decimal expHugeAvg, decimal expHugeAtr) = CandlePatternDetector.ComputeContext(huge, 12, 25);
        Assert.Equal(expHugeAvg, hugeAvg);
        Assert.Equal(expHugeAtr, hugeAtr);

        // 4. Double-cast high-scale prices (scale 15..17) over 2,000 bars also stay bitwise identical.
        var dblBars = new CandleData[2_000];
        var rng = new Random(42);
        decimal dp = 1000m;
        for (int i = 0; i < dblBars.Length; i++)
        {
            decimal o = dp;
            dp += (decimal)(rng.NextDouble() - 0.5) * 10m;
            dblBars[i] = PatternEngineTestBars.Bar(i, o, Math.Max(o, dp) + 5m, Math.Min(o, dp) - 5m, dp, 1000L + i);
        }

        PatternSeriesContext dblCtx = PatternEngineTestBars.Context(dblBars);
        foreach (int idx in new[] { 200, 999, 1998 })
        {
            Assert.True(PatternMath.TryAtrCtx(dblCtx, idx, 200, out decimal dAtr, out decimal dAvg));
            (decimal expDAvg, decimal expDAtr) = CandlePatternDetector.ComputeContext(dblBars, idx - 199, idx);
            Assert.Equal(expDAvg, dAvg);
            Assert.Equal(expDAtr, dAtr);
        }

        // 5. Long series (80,000 bars >= 79,229 threshold at Scale = 18 with 1e6 prices) falls back without OverflowException.
        const int longCount = 80_000;
        var longHighScaleBars = new CandleData[longCount];
        decimal scaledBase = 900_000.000000000000000001m;
        for (int i = 0; i < longCount; i++)
        {
            longHighScaleBars[i] = PatternEngineTestBars.Bar(i, scaledBase, scaledBase + 100m, scaledBase - 100m, scaledBase + 50m, 1_000L);
        }

        PatternSeriesContext longCtx = PatternEngineTestBars.Context(longHighScaleBars);
        Assert.True(PatternMath.TryAtrCtx(longCtx, longCount - 1, 14, out decimal longAtr, out decimal longAvg));
        (decimal expLongAvg, decimal expLongAtr) = CandlePatternDetector.ComputeContext(longHighScaleBars, longCount - 14, longCount - 1);
        Assert.Equal(expLongAvg, longAvg);
        Assert.Equal(expLongAtr, longAtr);
    }

    [Fact]
    public void HH_MultiSeedMutualExclusion_And_SymmetricWaveformMirror()
    {
        var factory = new IndicatorFactory();
        var relaxed = new CoreStructureTrendParameter { ZigZagThresholdPercent = 1.5m, MinSwingAtrMultiplier = 0m };
        int totalHhHlFires = 0;
        int totalLlLhFires = 0;

        for (int seed = 1; seed <= 10; seed++)
        {
            CoreCandleData[] bars = PatternEngineTestBars.Walk(seed, 400)
                .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume))
                .ToArray();

            IReadOnlyList<decimal?> hh = factory.Create(IndicatorType.MarketStructureHhHl, relaxed)!.Calculate(bars).GetSeries(IndicatorResult.MainSeriesName);
            IReadOnlyList<decimal?> ll = factory.Create(IndicatorType.MarketStructureLlLh, relaxed)!.Calculate(bars).GetSeries(IndicatorResult.MainSeriesName);

            for (int t = 0; t < bars.Length; t++)
            {
                if (hh[t] == 1m) totalHhHlFires++;
                if (ll[t] == 1m) totalLlLhFires++;
                Assert.False(hh[t] == 1m && ll[t] == 1m, $"Seed {seed} bar {t}: HH/HL and LL/LH fired simultaneously.");
            }
        }

        Assert.True(totalHhHlFires > 0 && totalLlLhFires > 0, $"Non-vacuous check failed: hh={totalHhHlFires}, ll={totalLlLhFires}");

        // Constructed rising structure around 1000m reflected around 1000m (with MinStepUpPercent = 0m ATR-only step so level differences don't skew step%)
        CandleData[] rising = FormationFixtures.FromAnchors(new[]
        {
            (0, 1000m), (18, 1000m),
            (24, 970m),  // L1
            (30, 1030m), // H1
            (36, 985m),  // L2
            (42, 1050m), // H2
            (46, 1015m), // confirms H2
            (52, 1020m),
        });
        CandleData[] falling = Reflect(rising, axis: 2000m); // centered identically at 1000m!

        var atrOnlyStep = new CoreStructureTrendParameter { ZigZagThresholdPercent = 2.0m, MinSwingAtrMultiplier = 0m, MinStepUpPercent = 0.0m };
        PatternBarResult[] hhRes = HigherHighHigherLowEngine.Run(PatternEngineTestBars.Context(rising), atrOnlyStep, side: 1);
        PatternBarResult[] llRes = HigherHighHigherLowEngine.Run(PatternEngineTestBars.Context(falling), atrOnlyStep, side: -1);

        Assert.Contains(hhRes, r => r.BullishFires);
        Assert.Contains(llRes, r => r.BearishFires);
        for (int t = 0; t < rising.Length; t++)
        {
            Assert.Equal(hhRes[t].Fires, llRes[t].Fires);
        }
    }
}
