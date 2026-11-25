using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FormationFixtures;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Elliott Wave Impulse (1-2-3-4-5) and Corrective (A-B-C) engine tests (plan ChartPatterns56 v2, run V2-7;
/// ledger <c>sa_pattern_literal_ledger_V2-7_ElliottGranville.md</c>, rows <c>EW_*</c>).
/// </summary>
public class ElliottWaveEngineTests
{
    private const decimal WickVal = 0.2m;

    private static CandleData[] Invert(CandleData[] bars, decimal pivot = 300m)
        => bars.Select(b => b with
        {
            Open = pivot - b.Open,
            High = pivot - b.Low,
            Low = pivot - b.High,
            Close = pivot - b.Close,
        }).ToArray();

    /// <summary>
    /// Builds a clean 6-pivot Impulse fixture from explicit pivot prices and indices (bullish when <paramref name="bullish"/>, mirrored around 300m otherwise).
    /// Preamble drops from <paramref name="preHigh"/> down toward <paramref name="p0"/> so <c>Pos(p0, i0, 40) = 0</c> for bullish.
    /// </summary>
    private static (CandleData[] Bars, int ConfirmBar) BuildImpulse(
        bool bullish = true,
        decimal p0 = 100m,
        decimal p1 = 120m,
        decimal p2 = 110m,
        decimal p3 = 140m,
        decimal p4 = 129m,
        decimal p5 = 145m,
        int i0 = 45,
        int i1 = 55,
        int i2 = 63,
        int i3 = 75,
        int i4 = 83,
        int i5 = 93,
        int tailEnd = 108,
        decimal? preHigh = null,
        decimal? postLow = null)
    {
        decimal startHigh = preHigh ?? (p0 + 30m);
        decimal tailTarget = postLow ?? Math.Min(p5 * 0.88m, p5 - 15m);
        int revBar = i5 + 3;

        var anchors = new List<(int Index, decimal Price)>
        {
            (0, startHigh),
            (Math.Max(1, i0 - 15), p0 + 12m),
            (i0, p0 + 0.5m),
            (i1, p1 - 0.5m),
            (i2, p2 + 0.5m),
            (i3, p3 - 0.5m),
            (i4, p4 + 0.5m),
            (i5, p5 - 0.5m),
            (revBar, tailTarget),
        };
        if (tailEnd > revBar)
        {
            anchors.Add((tailEnd, tailTarget + 1m));
        }

        CandleData[] bars = FromAnchors(anchors, wick: WickVal);
        bars[i0] = bars[i0] with { Low = p0 };
        bars[i1] = bars[i1] with { High = p1 };
        bars[i2] = bars[i2] with { Low = p2 };
        bars[i3] = bars[i3] with { High = p3 };
        bars[i4] = bars[i4] with { Low = p4 };
        bars[i5] = bars[i5] with { High = p5 };

        if (!bullish)
        {
            bars = Invert(bars);
        }

        return (bars, revBar);
    }

    /// <summary>Builds a clean 4-pivot Corrective ABC fixture (bullish: trough p0 -> peak p1 -> trough p2 -> peak p3).</summary>
    private static (CandleData[] Bars, int ConfirmBar) BuildCorrective(
        bool bullish = true,
        decimal p0 = 100m,
        decimal p1 = 120m,
        decimal p2 = 108m,
        decimal p3 = 130m,
        int i0 = 45,
        int i1 = 55,
        int i2 = 65,
        int i3 = 77,
        int tailEnd = 92,
        decimal? preHigh = null,
        decimal? postLow = null)
    {
        decimal startHigh = preHigh ?? (p0 + 30m);
        decimal tailTarget = postLow ?? Math.Min(p3 * 0.88m, p3 - 15m);
        int revBar = i3 + 3;

        var anchors = new List<(int Index, decimal Price)>
        {
            (0, startHigh),
            (Math.Max(1, i0 - 15), p0 + 12m),
            (i0, p0 + 0.5m),
            (i1, p1 - 0.5m),
            (i2, p2 + 0.5m),
            (i3, p3 - 0.5m),
            (revBar, tailTarget),
        };
        if (tailEnd > revBar)
        {
            anchors.Add((tailEnd, tailTarget + 1m));
        }

        CandleData[] bars = FromAnchors(anchors, wick: WickVal);
        bars[i0] = bars[i0] with { Low = p0 };
        bars[i1] = bars[i1] with { High = p1 };
        bars[i2] = bars[i2] with { Low = p2 };
        bars[i3] = bars[i3] with { High = p3 };

        if (!bullish)
        {
            bars = Invert(bars);
        }

        return (bars, revBar);
    }

    private static CoreElliottImpulseParameter DefaultImpulse() => new();
    private static CoreElliottCorrectiveParameter DefaultCorrective() => new();

    private static int FirstConfirmedBar(PatternBarResult[] results, int expectedDir)
    {
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].Direction == expectedDir)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void EW_ZigZag_And_NoFractal()
    {
        // FractalBars = 0 confirms immediately on the ZigZag reversal bar (<= i5 + 3 here) without extra fractal delay.
        Assert.Equal(0, PatternSpecConstants.ElliottFractalBars);

        (CandleData[] bars, int revBar) = BuildImpulse();
        PatternBarResult[] results = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), DefaultImpulse());
        int first = FirstConfirmedBar(results, +1);
        Assert.InRange(first, 94, revBar);
        Assert.Equal(1m, results[first].Score);

        // Increase ZigZagThresholdPercent beyond the wave 2 retrace (10/120 = 8.33%) -> collapses pivots -> no impulse.
        var strictZz = new CoreElliottImpulseParameter { ZigZagThresholdPercent = 12.0m };
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), strictZz), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_PositiveBullAndBear()
    {
        (CandleData[] bullBars, _) = BuildImpulse(bullish: true);
        PatternBarResult[] bull = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bullBars), DefaultImpulse());
        Assert.Contains(bull, r => r.BullishFires && !r.BearishFires && r.Direction == 1);

        (CandleData[] bearBars, _) = BuildImpulse(bullish: false);
        PatternBarResult[] bear = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bearBars), DefaultImpulse());
        Assert.Contains(bear, r => r.BearishFires && !r.BullishFires && r.Direction == -1);
    }

    [Fact]
    public void EW_PoolIndependence()
    {
        // Same 6-pivot Impulse series also contains a trailing A-B-C prefix/segment in Corrective when enabled independently.
        (CandleData[] bars, _) = BuildImpulse();
        var ctx = PatternEngineTestBars.Context(bars);

        // Impulse fires on 6 pivots (p0..p5); changing Corrective MinSpanBars cannot affect Impulse output.
        PatternBarResult[] imp = ElliottWaveEngine.RunImpulse(ctx, DefaultImpulse());
        Assert.Contains(imp, r => r.BullishFires);

        // Build an ABC corrective where wave count is 4 pivots (does not form 6-pivot impulse) -> Corrective fires while Impulse stays 0.
        (CandleData[] abcBars, _) = BuildCorrective();
        var abcCtx = PatternEngineTestBars.Context(abcBars);
        PatternBarResult[] corr = ElliottWaveEngine.RunCorrective(abcCtx, DefaultCorrective());
        PatternBarResult[] impOnAbc = ElliottWaveEngine.RunImpulse(abcCtx, DefaultImpulse());
        Assert.Contains(corr, r => r.BullishFires);
        Assert.All(impOnAbc, r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_OriginZone_EdgesAndToggle()
    {
        // Place a deeper prior low before p0 so Pos(p0=100, i0=45, 40) > 0.25 (e.g., range [80, 130] => Pos = 20/50 = 0.40 > 0.25).
        (CandleData[] bars, _) = BuildImpulse(p0: 100m);
        bars[25] = bars[25] with { Low = 80m, High = 130m };

        PatternBarResult[] withZone = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), new CoreElliottImpulseParameter { RequireOriginPriceZone = true });
        PatternBarResult[] noZone = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), new CoreElliottImpulseParameter { RequireOriginPriceZone = false });
        Assert.All(withZone, r => Assert.Equal(0, r.Direction));
        Assert.Contains(noZone, r => r.BullishFires);

        // Exact boundary 0.25 passes (window [6..45] range [95, 115], p0=100 at bar 45 => (100-95)/20 = 0.25), whereas >0.25 fails.
        (CandleData[] boundaryPass, _) = BuildCorrective(p0: 100m, preHigh: 115m);
        boundaryPass[10] = PatternEngineTestBars.Bar(10, 105m, 106m, 95m, 105m);
        boundaryPass[15] = PatternEngineTestBars.Bar(15, 110m, 115m, 109m, 112m);
        for (int i = 16; i <= 44; i++)
        {
            decimal c = 112m - (i - 16) * 0.4m;
            boundaryPass[i] = PatternEngineTestBars.Bar(i, c + 0.2m, c + 0.4m, c - 0.2m, c);
        }

        // Make wave B/C still valid and above 115 only AFTER i0.
        PatternBarResult[] corrPass = ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(boundaryPass), DefaultCorrective());
        Assert.Contains(corrPass, r => r.BullishFires);

        CandleData[] boundaryFail = boundaryPass.ToArray();
        boundaryFail[10] = PatternEngineTestBars.Bar(10, 105m, 106m, 94.5m, 105m); // Pos = (100 - 94.5) / (115 - 94.5) = 5.5 / 20.5 = 0.2683 > 0.25
        PatternBarResult[] corrFail = ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(boundaryFail), DefaultCorrective());
        Assert.All(corrFail, r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_DurationAndSymmetryEdges()
    {
        // Impulse leg durations all valid [2..60], total span = 48 in [15, 200]. MaxSpanBars edge: 48 passes, 47 fails.
        (CandleData[] bars, _) = BuildImpulse(i0: 45, i1: 55, i2: 63, i3: 75, i4: 83, i5: 93, tailEnd: 110);
        var ctx = PatternEngineTestBars.Context(bars);

        Assert.Contains(ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { MinSpanBars = 48, MaxSpanBars = 48 }), r => r.BullishFires);
        Assert.All(ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { MinSpanBars = 49, MaxSpanBars = 200 }), r => Assert.Equal(0, r.Direction));
        Assert.All(ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { MinSpanBars = 15, MaxSpanBars = 47 }), r => Assert.Equal(0, r.Direction));

        // Symmetry edge: legs 2, 18, 2, 2, 2 -> minLeg=2 -> denom=max(3.0, 2)=3.0 -> 18 / 3.0 = 6.0 (passes <= 6.0, fails <= 5.9).
        (CandleData[] symPassBars, _) = BuildImpulse(i0: 45, i1: 47, i2: 65, i3: 67, i4: 69, i5: 71, tailEnd: 90);
        var symCtx = PatternEngineTestBars.Context(symPassBars);
        Assert.Contains(ElliottWaveEngine.RunImpulse(symCtx, new CoreElliottImpulseParameter { MaxTimeSymmetryRatio = 6.0m }), r => r.BullishFires);
        Assert.All(ElliottWaveEngine.RunImpulse(symCtx, new CoreElliottImpulseParameter { MaxTimeSymmetryRatio = 5.99m }), r => Assert.Equal(0, r.Direction));

        // Single-leg < 2 or > 60 fails.
        (CandleData[] shortLegBars, _) = BuildImpulse(i0: 45, i1: 46, i2: 55, i3: 65, i4: 75, i5: 85, tailEnd: 100);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(shortLegBars), DefaultImpulse()), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_Principle1_ViolationRejected()
    {
        // Bullish Principle 1 requires p2 > p0. p2 = 99 < p0 = 100 (or equal p2 = 100) must be rejected.
        (CandleData[] belowP0, _) = BuildImpulse(p0: 100m, p2: 99m);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(belowP0), DefaultImpulse()), r => Assert.Equal(0, r.Direction));

        (CandleData[] equalP0, _) = BuildImpulse(p0: 100m, p2: 100m);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(equalP0), DefaultImpulse()), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_Principle2_ShortestWave3Rejected()
    {
        // Make w1 = 30 (100->130), w2 = 14 (130->116), w3 = 20 (116->136 > p1=130), w4 = 8 (136->128? overlap ok with tol 0.1), w5 = 25 (128->153).
        // Now w3=20 < w1=30 AND w3=20 < w5=25 => Wave 3 is shortest => rejected!
        (CandleData[] shortestW3, _) = BuildImpulse(p0: 100m, p1: 130m, p2: 116m, p3: 136m, p4: 130.5m, p5: 155m);
        var tolParam = new CoreElliottImpulseParameter { FibonacciTolerance = 0.40m, MinConfidenceScore = 0.10m };
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(shortestW3), tolParam), r => Assert.Equal(0, r.Direction));

        // Bearish mirror also rejects shortest Wave 3.
        CandleData[] bearShortestW3 = Invert(shortestW3);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bearShortestW3), tolParam), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_Principle3_OverlapToleranceEdges()
    {
        // p0 = 100, p1 = 120 (w1 = 20), p2 = 110, p3 = 140, p5 = 145.
        // OverlapTolerance = 0m: strict p4 > p1 => p4 = 120.0m fails, p4 = 120.01m passes.
        (CandleData[] exactTouch, _) = BuildImpulse(p4: 120m);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(exactTouch), new CoreElliottImpulseParameter { OverlapTolerance = 0m }), r => Assert.Equal(0, r.Direction));

        (CandleData[] justAbove, _) = BuildImpulse(p4: 120.01m);
        Assert.Contains(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(justAbove), new CoreElliottImpulseParameter { OverlapTolerance = 0m }), r => r.BullishFires);

        // OverlapTolerance = 0.05 (max overlap = 0.05 * 20 = 1.0 => p4 = 119.0 passes with p5 = 141m so only s3 can be zero; p4 = 118.9 fails).
        (CandleData[] overlapExact, _) = BuildImpulse(p4: 119m, p5: 141m, postLow: 120m);
        Assert.Contains(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(overlapExact), new CoreElliottImpulseParameter { OverlapTolerance = 0.05m }), r => r.BullishFires);

        (CandleData[] overlapExceed, _) = BuildImpulse(p4: 118.9m, p5: 141m, postLow: 120m);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(overlapExceed), new CoreElliottImpulseParameter { OverlapTolerance = 0.05m }), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Impulse_TruncationToggle()
    {
        // Truncated Wave 5: p3 = 140, p4 = 125 (w4 = 15, w4/w3 = 0.50), p5 = 138 (< p3=140, w5=13, w5/w1=0.65).
        // Ensure ZigZag picks p4=125 -> p5=138 (13/125 = 10.4% > 5%) -> rev to 120 (18/138 = 13% > 5%).
        (CandleData[] truncated, _) = BuildImpulse(p0: 100m, p1: 120m, p2: 110m, p3: 140m, p4: 125m, p5: 138m, postLow: 118m);
        var ctx = PatternEngineTestBars.Context(truncated);

        Assert.All(ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { AllowTruncation = false }), r => Assert.Equal(0, r.Direction));
        Assert.Contains(ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { AllowTruncation = true }), r => r.BullishFires);
    }

    [Fact]
    public void EW_Impulse_FibonacciEdges_And_MaxOneZero()
    {
        // Choose p0=100, p1=120 (w1=20). p2=103.62 -> w2/w1=0.819 > 0.818 => s1 = 0; p3=136 (w3=32.38, w3/w1=1.619 -> s2=0.995 > 0);
        // p4=125 (w4=11, w4/w3=0.34 -> s3=1); p5=144 (w5=19, w5/w1=0.95 -> s4=1) => ONLY 1 zero -> ACCEPTED!
        // Make TWO ratios score zero: push p5=150 (w5=25, w5/w1=1.25 > 1.20 -> s4=0) -> zeros = 2 > 1 => REJECTED even if score >= MinConfidenceScore(0.10)!
        (CandleData[] oneZero, _) = BuildImpulse(p0: 100m, p1: 120m, p2: 103.62m, p3: 136m, p4: 125m, p5: 144m);
        Assert.Contains(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(oneZero), new CoreElliottImpulseParameter { ZigZagThresholdPercent = 5m }), r => r.BullishFires);

        (CandleData[] twoZeros, _) = BuildImpulse(p0: 100m, p1: 120m, p2: 103.62m, p3: 136m, p4: 125m, p5: 150m);
        var lowMinScore = new CoreElliottImpulseParameter { MinConfidenceScore = 0.10m };
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(twoZeros), lowMinScore), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Corrective_PositiveBullAndBear()
    {
        (CandleData[] bullBars, _) = BuildCorrective(bullish: true);
        PatternBarResult[] bull = ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(bullBars), DefaultCorrective());
        Assert.Contains(bull, r => r.BullishFires && !r.BearishFires && r.Direction == 1);

        (CandleData[] bearBars, _) = BuildCorrective(bullish: false);
        PatternBarResult[] bear = ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(bearBars), DefaultCorrective());
        Assert.Contains(bear, r => r.BearishFires && !r.BullishFires && r.Direction == -1);
    }

    [Fact]
    public void EW_Corrective_DurationAndSymmetryEdges()
    {
        // Span = 32 (45->77).
        (CandleData[] bars, _) = BuildCorrective(i0: 45, i1: 55, i2: 65, i3: 77, tailEnd: 95);
        var ctx = PatternEngineTestBars.Context(bars);

        Assert.Contains(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { MinSpanBars = 32, MaxSpanBars = 32 }), r => r.BullishFires);
        Assert.All(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { MinSpanBars = 33, MaxSpanBars = 120 }), r => Assert.Equal(0, r.Direction));
        Assert.All(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { MinSpanBars = 9, MaxSpanBars = 31 }), r => Assert.Equal(0, r.Direction));

        // Symmetry: 2, 18, 2 -> 18 / max(3, 2) = 6.0.
        (CandleData[] symBars, _) = BuildCorrective(i0: 45, i1: 47, i2: 65, i3: 67, tailEnd: 85);
        var symCtx = PatternEngineTestBars.Context(symBars);
        Assert.Contains(ElliottWaveEngine.RunCorrective(symCtx, new CoreElliottCorrectiveParameter { MaxTimeSymmetryRatio = 6.0m }), r => r.BullishFires);
        Assert.All(ElliottWaveEngine.RunCorrective(symCtx, new CoreElliottCorrectiveParameter { MaxTimeSymmetryRatio = 5.99m }), r => Assert.Equal(0, r.Direction));

        // Leg > ElliottCorrectiveWaveBarsMax (50) fails even when MaxSpanBars = 120.
        (CandleData[] longLeg, _) = BuildCorrective(i0: 45, i1: 96, i2: 106, i3: 118, tailEnd: 135);
        Assert.All(ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(longLeg), DefaultCorrective()), r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Corrective_WaveBRetrace_And_WaveCExceedToggle()
    {
        // Wave B <= p0 rejected.
        (CandleData[] bTooDeep, _) = BuildCorrective(p0: 100m, p1: 120m, p2: 99m, p3: 130m);
        Assert.All(ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(bTooDeep), DefaultCorrective()), r => Assert.Equal(0, r.Direction));

        // Flat / truncated Wave C (p3 = 119 <= p1 = 120, wC = 11, wC/wA = 0.55 in [0.418, 1.818]): rejected with default true, accepted when false.
        (CandleData[] flatC, _) = BuildCorrective(p0: 100m, p1: 120m, p2: 108m, p3: 119m, postLow: 102m);
        var ctx = PatternEngineTestBars.Context(flatC);
        Assert.All(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { RequireWaveCExceedWaveA = true }), r => Assert.Equal(0, r.Direction));
        Assert.Contains(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { RequireWaveCExceedWaveA = false }), r => r.BullishFires);
    }

    [Fact]
    public void EW_Corrective_FibonacciEdges_And_BothPositive()
    {
        // wB/wA allowed max = 0.786 + 0.20 = 0.986. If wB/wA = 0.99 -> sB = 0. Spec 02-4 requires BOTH sB > 0 AND sC > 0 -> REJECTED!
        (CandleData[] zeroB, _) = BuildCorrective(p0: 100m, p1: 120m, p2: 100.2m, p3: 130m); // wB = 19.8, wB/wA = 0.99 > 0.986
        Assert.All(ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(zeroB), new CoreElliottCorrectiveParameter { MinConfidenceScore = 0.10m }), r => Assert.Equal(0, r.Direction));

        // Check MinConfidenceScore threshold around ideal score.
        (CandleData[] ideal, _) = BuildCorrective(); // sB = 1.0, sC = 1.0 -> score = 1.0
        var ctx = PatternEngineTestBars.Context(ideal);
        Assert.Contains(ElliottWaveEngine.RunCorrective(ctx, new CoreElliottCorrectiveParameter { MinConfidenceScore = 1.00m }), r => r.BullishFires);
    }

    [Fact]
    public void EW_Persistence_Consumed_DirectionMode()
    {
        (CandleData[] bars, _) = BuildImpulse(tailEnd: 115);
        var ctx = PatternEngineTestBars.Context(bars);

        // MaxBarsSinceConfirmation = 2 holds for exactly 3 bars (confirm + 0, +1, +2) and tuple is consumed (never fires a second time).
        PatternBarResult[] r2 = ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { MaxBarsSinceConfirmation = 2 });
        int activeCount = r2.Count(r => r.BullishFires);
        Assert.Equal(3, activeCount);

        // BearishOnly suppresses bullish impulse.
        PatternBarResult[] bearOnly = ElliottWaveEngine.RunImpulse(ctx, new CoreElliottImpulseParameter { DirectionMode = PatternDirectionMode.BearishOnly });
        Assert.All(bearOnly, r => Assert.Equal(0, r.Direction));
    }

    [Fact]
    public void EW_Wave5RocDivergence_And_ZeroGuard()
    {
        (CandleData[] bars, _) = BuildImpulse(p3: 140m, p5: 145m, i3: 75, i5: 93);
        // Set valid OHLC closes so ROC(75) = (139 - 125)/125 = +0.112, ROC(93) = (144 - 140)/140 = +0.0285 (< ROC(75)).
        bars[70] = PatternEngineTestBars.Bar(70, 126m, 130m, 124m, 125m);
        bars[88] = PatternEngineTestBars.Bar(88, 138m, 141m, 136m, 140m);
        var ctx = PatternEngineTestBars.Context(bars);

        var p3 = new ConfirmedPivot(75, 140m, true, 76);
        var p5 = new ConfirmedPivot(93, 145m, true, 95);
        Assert.True(ElliottWaveEngine.HasWave5RocDivergence(ctx, bullish: true, p3, p5));

        // No divergence when ROC(93) > ROC(75).
        bars[88] = PatternEngineTestBars.Bar(88, 120m, 138m, 109m, 110m); // ROC(93) = 34/110 = 0.309 > 0.112
        Assert.False(ElliottWaveEngine.HasWave5RocDivergence(PatternEngineTestBars.Context(bars), bullish: true, p3, p5));

        // Bounds & non-positive guard: i3 < 5 or invalid bar returns false without throwing.
        Assert.False(ElliottWaveEngine.HasWave5RocDivergence(ctx, bullish: true, new ConfirmedPivot(3, 140m, true, 4), p5));
        CandleData[] corrupt = PatternEngineTestBars.Corrupt(bars, 70);
        Assert.False(ElliottWaveEngine.HasWave5RocDivergence(PatternEngineTestBars.Context(corrupt), bullish: true, p3, p5));
    }

    [Fact]
    public void EW_InvalidBars_And_ShortSeries_PrefixInvariance()
    {
        (CandleData[] bars, _) = BuildImpulse(tailEnd: 112);

        // Corrupting a bar inside [p0=45..p5=93] suppresses the impulse (plan R-16).
        CandleData[] corruptInside = PatternEngineTestBars.Corrupt(bars, 60);
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(corruptInside), DefaultImpulse()), r => Assert.Equal(0, r.Direction));

        // Corrupting a bar BEFORE p0 (e.g., index 2 -> outside OriginZone 40-bar window [6..45] and wave [45..93]) leaves the pattern unaffected.
        CandleData[] corruptOutside = PatternEngineTestBars.Corrupt(bars, 2);
        Assert.Contains(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(corruptOutside), DefaultImpulse()), r => r.BullishFires);

        // Short & flat series yield zero detections.
        Assert.All(ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(PatternEngineTestBars.Trend(10, 100m, 0m, 0.1m)), DefaultImpulse()), r => Assert.Equal(0, r.Direction));

        // Causal prefix invariance (plan R-18): output at t does not change when bars after t are added.
        int prefixLen = 100;
        PatternBarResult[] full = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), DefaultImpulse());
        PatternBarResult[] prefix = ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars.Take(prefixLen).ToArray()), DefaultImpulse());
        for (int t = 0; t < prefixLen; t++)
        {
            Assert.Equal(full[t], prefix[t]);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ElliottWaveEngine.RunImpulse(PatternEngineTestBars.Context(bars), DefaultImpulse(), cts.Token));
        Assert.Throws<OperationCanceledException>(() => ElliottWaveEngine.RunCorrective(PatternEngineTestBars.Context(bars), DefaultCorrective(), cts.Token));
    }
}
