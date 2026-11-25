using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Granville's Law Buy Signal and Sell Signal engine tests (plan ChartPatterns56 v2, run V2-7;
/// ledger <c>sa_pattern_literal_ledger_V2-7_ElliottGranville.md</c>, rows <c>GR_*</c>).
/// </summary>
public class GranvilleSignalEngineTests
{
    private static CoreGranvilleSignalParameter SmallParam(
        int maPeriod = 5,
        int slopePeriod = 2) => new()
    {
        MaPeriod = maPeriod,
        SlopePeriod = slopePeriod,
    };

    private static CandleData[] ConstantCloses(int count, decimal price = 100m, long volume = 1000)
        => Enumerable.Range(0, count)
            .Select(i => PatternEngineTestBars.Bar(i, price, price + 0.5m, price - 0.5m, price, volume))
            .ToArray();

    private static decimal Sma(CandleData[] bars, int period, int index)
    {
        decimal sum = 0m;
        for (int i = index - period + 1; i <= index; i++)
        {
            sum += bars[i].Close;
        }

        return sum / period;
    }

    [Fact]
    public void GR_Slope_FlatThresholdEdges()
    {
        // With MaPeriod = 5, SlopePeriod = 2, FlatThreshold = 0.05m:
        // Bars 0..19 flat at 100 -> SMA(t - 2) = 100. Change C_20 so slopePct hits exact boundary:
        // SMA(20) = 100.050 -> slopePct = 0.050 / 100.050 * 100 = 0.049975% <= 0.05% (FLAT).
        // SMA(20) = 100.051 -> slopePct = 0.051 / 100.051 * 100 = 0.050974% > 0.05% (RISING).
        // Test via Rule 2 (requires isMaRising strictly): C_{19} >= SMA_{19} (100), C_{20} < SMA_{20} cannot hold for rising C_20 unless we raise C_{18}=101, C_{19}=101 then C_{20}=99.
        // Simpler: fix bars 0..17 = 100, C_{18} = 100.251 (SMA_18 = 100.0502), C_{19} = 100.251 (SMA_19 = 100.1004 >= prev), C_{20} = 99 (wait: slope uses SMA(20) - SMA(18)).
        // Even cleaner: fix C[0..19] = 100 except C[19] = 101 -> SMA(18)=100, SMA(19)=100.2, C[19]=101 > 100.2, C[20] = 99.3 => sum(16..20)=400+101+99.3=600.3? No, sum = 300+101+99.3 = 500.3 -> SMA(20) = 100.06.
        // Check: slope = 100.06 - 100 = +0.06 => slopePct = 0.06 / 100.06 * 100 = 0.05996% > 0.05% -> RISING! Rule 2 fires!
        CandleData[] risingBars = ConstantCloses(21, 100m);
        risingBars[19] = PatternEngineTestBars.Bar(19, 100m, 101.2m, 99.8m, 101m);
        risingBars[20] = PatternEngineTestBars.Bar(20, 101m, 101.2m, 99m, 99.3m); // C_20 < SMA_20 (100.06) and prevClose(101) >= SMA_19(100.2)

        CoreGranvilleSignalParameter p = SmallParam();
        p.FlatThreshold = 0.05m;
        GranvilleBarResult[] risingRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(risingBars), p);
        Assert.True(risingRes[20].Rule2);
        Assert.Equal(2, risingRes[20].RuleCode);

        // Increase FlatThreshold to 0.06m (>= 0.05996%) -> SMA is now FLAT, so Rule 2 stops firing!
        p.FlatThreshold = 0.06m;
        GranvilleBarResult[] flatRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(risingBars), p);
        Assert.False(flatRes[20].Rule2);

        // Wait: on flat SMA (slopePct <= 0.05%), Rule 1 CAN still fire if crossedAbove while Rule 2 does not.
        CandleData[] flatCross = ConstantCloses(21, 100m);
        flatCross[19] = PatternEngineTestBars.Bar(19, 100m, 100m, 99.8m, 99.9m);
        flatCross[20] = PatternEngineTestBars.Bar(20, 99.9m, 100.3m, 99.8m, 100.2m);
        // SMA(18) = 100, SMA(19) = 99.98, SMA(20) = 100.02 -> slopePct = 0.02/100.02*100 = 0.02% <= 0.05% (FLAT), prevClose(99.9) <= SMA_19(99.98), close(100.2) > SMA_20(100.02) -> Buy 1!
        GranvilleBarResult[] flatCrossRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(flatCross), SmallParam());
        Assert.True(flatCrossRes[20].Rule1);
        Assert.Equal(1, flatCrossRes[20].RuleCode);
    }

    [Fact]
    public void GR_Rule1And2_CrossEdges()
    {
        // Buy Rule 1 requires strict cross above: prevClose <= SMA_{t-1} && close > SMA_t.
        // At t = 20, keep SMA rising/flat: C[0..18] = 100 (SMA_18 = 100, SMA_19 = 100 when C_19 = 100).
        // C[19] == SMA[19] (100 == 100): valid prev side. C[20] == SMA[20] must NOT cross; C[20] > SMA[20] crosses!
        CandleData[] exactEqualClose = ConstantCloses(21, 100m); // C_20 = 100 == SMA_20 = 100
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(exactEqualClose), SmallParam())[20].Rule1);

        // Mirror Sell Rule 1 & Rule 2:
        // Sell 1: (isMaFlat || isMaFalling) && crossedBelow (prevClose >= SMA_{t-1} && close < SMA_t).
        CandleData[] sell1Bars = ConstantCloses(21, 100m);
        sell1Bars[19] = PatternEngineTestBars.Bar(19, 100m, 100.2m, 99.9m, 100.0m); // prevClose == SMA_19 == 100
        sell1Bars[20] = PatternEngineTestBars.Bar(20, 100m, 100.1m, 99.7m, 99.8m); // close 99.8 < SMA_20 (99.96), slopePct 0.04% <= 0.05% (flat)
        GranvilleBarResult s1Res = GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(sell1Bars), SmallParam())[20];
        Assert.True(s1Res.Rule1);
        Assert.Equal(-1, s1Res.RuleCode);

        // Sell 2: isMaFalling && crossedAbove.
        CandleData[] sell2Bars = ConstantCloses(21, 100m);
        sell2Bars[19] = PatternEngineTestBars.Bar(19, 100m, 100.1m, 98.8m, 99.0m); // SMA_19 = 99.8, C_19 = 99.0 <= SMA_19
        sell2Bars[20] = PatternEngineTestBars.Bar(20, 99m, 100.8m, 98.9m, 100.6m); // SMA_20 = 99.92, slope = -0.08% (< -0.05% falling), C_20 = 100.6 > SMA_20
        GranvilleBarResult s2Res = GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(sell2Bars), SmallParam())[20];
        Assert.True(s2Res.Rule2);
        Assert.Equal(-2, s2Res.RuleCode);
    }

    [Fact]
    public void GR_Rule1_VolumeOption()
    {
        // AvgVol(t-1, 20) over bars 0..19 requires index >= 20, Volume = 1000 each -> threshold = 1.20 * 1000 = 1200.
        CandleData[] bars = ConstantCloses(22, 100m, volume: 1000);
        bars[20] = PatternEngineTestBars.Bar(20, 100m, 100.1m, 99.8m, 99.9m, 1000);
        bars[21] = PatternEngineTestBars.Bar(21, 99.9m, 100.4m, 99.8m, 100.3m, 1199); // below 1200

        var optOff = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, RequireVolumeOnRule1 = false };
        var optOn = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, RequireVolumeOnRule1 = true };

        Assert.True(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOff)[21].Rule1);
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOn)[21].Rule1);

        bars[21] = bars[21] with { Volume = 1200 }; // exact 1.20x passes
        Assert.True(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOn)[21].Rule1);
    }

    [Fact]
    public void GR_Rule3_BounceEdges()
    {
        // Buy Rule 3: isMaRising && prevClose > SMA_{t-1} && close > SMA_t && |Low_t - SMA_t|/SMA_t*100 <= 0.5 && close > Low_t.
        // Build smoothly rising series up to index 20 so isMaRising holds and SMA_20 is exact:
        CandleData[] bars = Enumerable.Range(0, 21)
            .Select(i =>
            {
                decimal c = 100m + i;
                return PatternEngineTestBars.Bar(i, c - 0.5m, c + 0.5m, c - 0.5m, c);
            })
            .ToArray();
        // At i = 20: closes[16..20] = 116,117,118,119,120 -> SMA(5, 20) = 118m!
        decimal sma20 = Sma(bars, 5, 20);
        Assert.Equal(118m, sma20);

        // Exact 0.50% boundary below SMA: Low = 118 * (1 - 0.005) = 117.41m -> |Low - SMA|/SMA * 100 = 0.500% (passes <= 0.5m).
        bars[20] = PatternEngineTestBars.Bar(20, 119m, 121m, 117.41m, 120m);
        CoreGranvilleSignalParameter p = SmallParam();
        GranvilleBarResult pass = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), p)[20];
        Assert.True(pass.Rule3);
        Assert.Equal(3, pass.RuleCode);

        // Low = 117.40m -> 0.508% > 0.5% -> rejected!
        CandleData[] failProx = bars.ToArray();
        failProx[20] = PatternEngineTestBars.Bar(20, 119m, 121m, 117.40m, 120m);
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(failProx), p)[20].Rule3);

        // C_t == L_t rejected (requires strict C_t > L_t)!
        CandleData[] failDojiLow = bars.ToArray();
        failDojiLow[20] = PatternEngineTestBars.Bar(20, 119m, 121m, 118.2m, 118.2m);
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(failDojiLow), p)[20].Rule3);

        // Sell Rule 3 mirror on downtrend: closes 130 - i -> at i=20, closes=114,113,112,111,110 -> SMA_20 = 112m.
        CandleData[] bearBars = Enumerable.Range(0, 21)
            .Select(i =>
            {
                decimal c = 130m - i;
                return PatternEngineTestBars.Bar(i, c + 0.5m, c + 0.5m, c - 0.5m, c);
            })
            .ToArray();
        // High = 112 * 1.005 = 112.56m -> 0.500% -> Sell 3 fires; 112.57m -> fails; Close == High -> fails.
        bearBars[20] = PatternEngineTestBars.Bar(20, 111m, 112.56m, 109m, 110m);
        GranvilleBarResult sell3Pass = GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(bearBars), p)[20];
        Assert.True(sell3Pass.Rule3);
        Assert.Equal(-3, sell3Pass.RuleCode);

        CandleData[] sell3Fail = bearBars.ToArray();
        sell3Fail[20] = PatternEngineTestBars.Bar(20, 111m, 112.57m, 109m, 110m);
        Assert.False(GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(sell3Fail), p)[20].Rule3);
    }

    [Fact]
    public void GR_Rule3_PrevBarSideToggle()
    {
        // Construct rising MA where prevClose <= SMA_{19} AND close > SMA_{20} AND Low near SMA_{20}:
        // With RequirePrevBarOnMaSideForRule3 = true (default), Rule 1 fires (NOT Rule 3 -> mutually exclusive!).
        // With RequirePrevBarOnMaSideForRule3 = false, Rule 1 AND Rule 3 can fire simultaneously!
        CandleData[] bars = Enumerable.Range(0, 21)
            .Select(i => PatternEngineTestBars.Bar(i, 100m + i, 101m + i, 99.5m + i, 100.5m + i))
            .ToArray();
        // At 19: closes 115.5, 116.5, 117.5, 118.5, 119.5 -> push 19 close BELOW SMA_19 (say 116 <= 116.8), keep slope rising via 18->20 jump.
        bars[19] = PatternEngineTestBars.Bar(19, 118m, 119m, 115.5m, 116m); // SMA_19 = (115.5+116.5+117.5+118.5+116)/5 = 116.8, prevClose 116 <= 116.8
        bars[20] = PatternEngineTestBars.Bar(20, 118.2m, 125.2m, 118m, 125m); // SMA_20 = 118.7 > SMA_18(116.5), Low 118 <= Open 118.2 (valid), |118-118.7|/118.7*100 = 0.59%

        var strictP = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, BounceTolerance = 1.0m, RequirePrevBarOnMaSideForRule3 = true };
        GranvilleBarResult strictRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), strictP)[20];
        Assert.True(strictRes.Rule1);
        Assert.False(strictRes.Rule3);

        var looseP = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, BounceTolerance = 1.0m, RequirePrevBarOnMaSideForRule3 = false };
        GranvilleBarResult looseRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), looseP)[20];
        Assert.True(looseRes.Rule1);
        Assert.True(looseRes.Rule3);
        Assert.Equal(1, looseRes.RuleCode); // Priority 4 -> 1 -> 2 -> 3
    }

    [Fact]
    public void GR_Rule4_DeviationEdges()
    {
        // Falling MA, dev = (C_t - SMA_t) / SMA_t * 100 < -DeviationThreshold (-10%).
        // Setup bars 0..19 at 100, bar 20 drops so SMA_20 = 98, wait exact: C[16..19]=100, C[20]=87.5 -> sum=487.5, SMA_20=97.5, dev = (87.5-97.5)/97.5*100 = -10.256%.
        // To test exact -10.000% vs -10.001%: set C[16..19]=100 (sum=400), solve (C - (400+C)/5)/((400+C)/5) = -0.10 -> 4C - 400 = -0.10*(400+C) -> 4.1C = 360 -> C = 3600/41 (not exact decimal),
        // OR pass DeviationThreshold equal to the exact decimal dev percentage:
        // Use C[16..19] = 100m, C[20] = 89m -> sum = 489, SMA_20 = 97.8m, dev = -8.8 / 97.8m * 100m = -8.9979...%. Even simpler: solve C_20 with C[16..19] = 105m -> sum = 420. Say SMA_20 = 100 => sum = 500 => C_20 = 80m, then dev = (80 - 100)/100*100 = -20.0000m%!
        CandleData[] bars = ConstantCloses(21, 105m);
        bars[20] = PatternEngineTestBars.Bar(20, 90m, 91m, 79m, 80m); // SMA_20 = (420 + 80)/5 = 100m, slope = -5m (falling), dev = -20.0%

        // Strict inequality: dev < -20.0% -> threshold 20.0% FAILS (-20 < -20 is false), threshold 19.99% PASSES!
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 20.0m })[20].Rule4);
        GranvilleBarResult b4 = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 19.99m })[20];
        Assert.True(b4.Rule4);
        Assert.Equal(4, b4.RuleCode);

        // Sell Rule 4 exact mirror: C[16..19] = 95m (sum=380), C[20] = 120m => SMA_20 = 100m (rising +5%), dev = +20.0%!
        CandleData[] sellBars = ConstantCloses(21, 95m);
        sellBars[20] = PatternEngineTestBars.Bar(20, 110m, 121m, 109m, 120m);
        Assert.False(GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(sellBars), new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 20.0m })[20].Rule4);
        GranvilleBarResult s4 = GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(sellBars), new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 19.99m })[20];
        Assert.True(s4.Rule4);
        Assert.Equal(-4, s4.RuleCode);
    }

    [Fact]
    public void GR_Rule4_FirstEntryAndReversalCandle_R27()
    {
        // Two consecutive bars both below -10% deviation with declining bearish close (Close <= Open):
        // bar 20 is FirstEntry (prev bar at -0% >= -10%) -> fires even with Rule4FirstEntryOrReversalOnly = true!
        // bar 21 has prev bar already < -10% AND is bearish (Close = 75 <= Open = 78) -> skipped when option is true, fires when reversal candle (Close > Open)!
        CandleData[] bars = ConstantCloses(22, 105m);
        bars[20] = PatternEngineTestBars.Bar(20, 90m, 91m, 79m, 80m); // dev_19 = 0 >= -10, dev_20 = -20 < -10 (FirstEntry!)
        bars[21] = PatternEngineTestBars.Bar(21, 78m, 79m, 74m, 75m); // dev_20 = -20 < -10 AND C_21(75) <= O_21(78) (not FirstEntry, not reversal!)

        var optOff = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 10.0m, Rule4FirstEntryOrReversalOnly = false };
        var optOn = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 10.0m, Rule4FirstEntryOrReversalOnly = true };

        GranvilleBarResult[] resOff = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOff);
        Assert.True(resOff[20].Rule4);
        Assert.True(resOff[21].Rule4);

        GranvilleBarResult[] resOn = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOn);
        Assert.True(resOn[20].Rule4);  // FirstEntry
        Assert.False(resOn[21].Rule4); // Neither FirstEntry nor reversal candle

        // Make bar 21 a bullish reversal candle (Close = 75 > Open = 74) -> now fires with optOn = true!
        bars[21] = PatternEngineTestBars.Bar(21, 74m, 79m, 73m, 75m);
        Assert.True(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), optOn)[21].Rule4);
    }

    [Fact]
    public void GR_EnableRuleToggles()
    {
        // Trigger Rule 1, then disable EnableRule1 -> all outputs become false / 0.
        CandleData[] bars = ConstantCloses(21, 100m);
        bars[19] = PatternEngineTestBars.Bar(19, 100m, 100m, 99.8m, 99.9m);
        bars[20] = PatternEngineTestBars.Bar(20, 99.9m, 100.4m, 99.8m, 100.2m);

        var enabled = SmallParam();
        Assert.True(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), enabled)[20].Rule1);

        var disabled = SmallParam();
        disabled.EnableRule1 = false;
        GranvilleBarResult r = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), disabled)[20];
        Assert.False(r.Rule1);
        Assert.False(r.Main);
        Assert.Equal(0, r.RuleCode);

        // Verify EnableRule2 / 3 / 4 flags also gate their respective rules.
        CandleData[] r2Bars = ConstantCloses(21, 100m);
        r2Bars[19] = PatternEngineTestBars.Bar(19, 100m, 101.2m, 99.8m, 101m);
        r2Bars[20] = PatternEngineTestBars.Bar(20, 101m, 101.2m, 99m, 99.3m);
        var noR2 = SmallParam();
        noR2.EnableRule2 = false;
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(r2Bars), noR2)[20].Rule2);

        CandleData[] r4Bars = ConstantCloses(21, 105m);
        r4Bars[20] = PatternEngineTestBars.Bar(20, 90m, 91m, 79m, 80m);
        var noR4 = SmallParam();
        noR4.EnableRule4 = false;
        Assert.False(GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(r4Bars), noR4)[20].Rule4);
    }

    [Fact]
    public void GR_ExclusivityProof_R12_OnRandomSeries()
    {
        // Plan R-12 / CF-V2-16: With RequirePrevBarOnMaSideForRule3 = true, B1..B4 (and S1..S4) are pairwise mutually exclusive
        // on EVERY valid bar, and RuleCode always equals the single active rule's signed code!
        var p = new CoreGranvilleSignalParameter { MaPeriod = 20, SlopePeriod = 3, BounceTolerance = 3.0m, DeviationThreshold = 3.0m };
        int buySignals = 0;
        int sellSignals = 0;

        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Walk(seed, 500));
            GranvilleBarResult[] buys = GranvilleSignalEngine.RunBuy(ctx, p);
            GranvilleBarResult[] sells = GranvilleSignalEngine.RunSell(ctx, p);

            foreach (GranvilleBarResult b in buys)
            {
                int active = (b.Rule1 ? 1 : 0) + (b.Rule2 ? 1 : 0) + (b.Rule3 ? 1 : 0) + (b.Rule4 ? 1 : 0);
                Assert.InRange(active, 0, 1);
                buySignals += active;
                int expectedCode = b.Rule1 ? 1 : b.Rule2 ? 2 : b.Rule3 ? 3 : b.Rule4 ? 4 : 0;
                Assert.Equal(expectedCode, b.RuleCode);
            }

            foreach (GranvilleBarResult s in sells)
            {
                int active = (s.Rule1 ? 1 : 0) + (s.Rule2 ? 1 : 0) + (s.Rule3 ? 1 : 0) + (s.Rule4 ? 1 : 0);
                Assert.InRange(active, 0, 1);
                sellSignals += active;
                int expectedCode = s.Rule1 ? -1 : s.Rule2 ? -2 : s.Rule3 ? -3 : s.Rule4 ? -4 : 0;
                Assert.Equal(expectedCode, s.RuleCode);
            }
        }

        Assert.True(buySignals > 0 && sellSignals > 0);
    }

    [Fact]
    public void GR_SmaNonPositiveGuard_InvalidBar_BuySellIndependent()
    {
        // 1. Corrupt bar in SMA window causes ctx.Sma(...) == null / skipped -> returns 0 without exception.
        CandleData[] bars = ConstantCloses(21, 100m);
        bars[19] = PatternEngineTestBars.Bar(19, 100m, 100m, 99.8m, 99.9m);
        bars[20] = PatternEngineTestBars.Bar(20, 99.9m, 100.4m, 99.8m, 100.2m);
        CandleData[] corrupt = PatternEngineTestBars.Corrupt(bars, 18);
        GranvilleBarResult corruptRes = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(corrupt), SmallParam())[20];
        Assert.False(corruptRes.Main);
        Assert.Equal(0, corruptRes.RuleCode);

        // 2. Non-positive price (Close = 0m makes SMA <= 0m when all closes in window are 0) -> guarded to 0 without divide-by-zero.
        CandleData[] zeroBars = Enumerable.Range(0, 21)
            .Select(i => new CandleData(new DateTime(2026, 1, 1).AddDays(i), 0m, 1m, 0m, 0m, 100))
            .ToArray();
        GranvilleBarResult zeroBuy = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(zeroBars), SmallParam())[20];
        Assert.False(zeroBuy.Main);

        // 3. Buy and Sell evaluate independently: on a flat MA bar with crossedBelow (Wait: Buy1 uses crossedAbove, Sell1 uses crossedBelow; can Buy4/Sell? Wait:
        //    on flat MA, Buy1 & Sell1 use opposite crosses; check Buy and Sell with small period where flat cross Buy 1 fires and Sell evaluates independently without being suppressed by Buy, and
        //    actually Buy Rule 3 (L near rising SMA, C > SMA) and Sell Rule 4 (rising SMA, C > SMA*(1+dev)) CAN both fire on the EXACT SAME bar!).
        // Demonstrate same-bar simultaneous Buy 3 + Sell 4!
        CandleData[] bothBars = Enumerable.Range(0, 21)
            .Select(i => PatternEngineTestBars.Bar(i, 100m + i, 101m + i, 99.5m + i, 100.5m + i))
            .ToArray();
        // At 20: previous 4 closes = 116.5, 117.5, 118.5, 119.5 (sum=472). Set bar 20: Low = 120.0 (near SMA), Close = 135.0 (> SMA*(1+0.10)) -> SMA_20 = 607/5 = 121.4, |120 - 121.4|/121.4 = 1.15% <= BounceTolerance(2%),
        // dev = (135 - 121.4)/121.4 = +11.2% > 10%, MA is rising, prevClose(119.5) > SMA_19(117.5), Close(135) > SMA_20(121.4) & > Low(120)!
        bothBars[20] = PatternEngineTestBars.Bar(20, 120m, 136m, 120m, 135m);
        var pBoth = new CoreGranvilleSignalParameter { MaPeriod = 5, SlopePeriod = 2, BounceTolerance = 2.0m, DeviationThreshold = 10.0m };
        var ctxBoth = PatternEngineTestBars.Context(bothBars);
        GranvilleBarResult buySameBar = GranvilleSignalEngine.RunBuy(ctxBoth, pBoth)[20];
        GranvilleBarResult sellSameBar = GranvilleSignalEngine.RunSell(ctxBoth, pBoth)[20];
        Assert.True(buySameBar.Rule3);
        Assert.True(sellSameBar.Rule4);
    }

    [Fact]
    public void GR_PrefixInvariance_And_Cancellation()
    {
        CandleData[] bars = PatternEngineTestBars.Walk(4, 150);
        CoreGranvilleSignalParameter p = SmallParam(maPeriod: 20, slopePeriod: 3);

        GranvilleBarResult[] full = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), p);
        GranvilleBarResult[] prefix = GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars.Take(100).ToArray()), p);
        for (int t = 0; t < 100; t++)
        {
            Assert.Equal(full[t], prefix[t]);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => GranvilleSignalEngine.RunBuy(PatternEngineTestBars.Context(bars), p, cts.Token));
        Assert.Throws<OperationCanceledException>(() => GranvilleSignalEngine.RunSell(PatternEngineTestBars.Context(bars), p, cts.Token));
    }
}
