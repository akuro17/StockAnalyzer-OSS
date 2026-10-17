using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Models.Validation;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

public class HarmonicCorrectnessTests
{
    private readonly ITestOutputHelper _output;

    public HarmonicCorrectnessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    #region Phase D1 Probes

    [Fact]
    public void Probe1_V11_Detect_ZigZagThresholdsRounding_ProducesIdenticalPivots()
    {
        // V11: Window is calculated as (int)Math.Max(1, Math.Round(threshold)).
        // Thresholds 2.0m and 2.4m both round to windowSize = 2, producing identical pivots.
        var bars = SyntheticBars.SeededRandomWalk(42, 200, 100m);

        var pivotsA = HarmonicPatternDetector.ExtractPivotsByLocalExtrema(bars, (int)Math.Max(1, Math.Round(2.0m)));
        var pivotsB = HarmonicPatternDetector.ExtractPivotsByLocalExtrema(bars, (int)Math.Max(1, Math.Round(2.4m)));

        Assert.Equal(pivotsA.Count, pivotsB.Count);
        for (int i = 0; i < pivotsA.Count; i++)
        {
            Assert.Equal(pivotsA[i].Index, pivotsB[i].Index);
            Assert.Equal(pivotsA[i].Price, pivotsB[i].Price);
            Assert.Equal(pivotsA[i].IsHigh, pivotsB[i].IsHigh);
        }
    }

    [Fact]
    public void Probe2_V12_TrailingExtreme_CannotBeConfirmedAtSeriesEnd()
    {
        // V12: In local extrema with window w, a pivot at index D requires w bars to its right.
        // Therefore, if D is within the last w bars (D > N - 1 - w), it is never confirmed at bar N - 1.
        const int window = 2;
        var bars = SyntheticBars.SeededRandomWalk(10, 50, 100m);
        var pivots = HarmonicPatternDetector.ExtractPivotsByLocalExtrema(bars, window);

        int lastBarIndex = bars.Length - 1;
        foreach (var p in pivots)
        {
            // Every extracted local extrema pivot must have at least 'window' bars to its right
            Assert.True(p.Index <= lastBarIndex - window,
                $"Local extrema pivot at index {p.Index} must be <= {lastBarIndex - window}");
        }
    }

    #endregion

    #region Phase D0 Measurement

    [Fact]
    public void PhaseD0_MeasureDetections_LegacyVsAmplitude()
    {
        // Diagnostic measurement across seeds 1..20 (400 bars each)
        // comparing legacy Detect vs DetectByLegAmplitude (2.0% vs 3.0%)
        var countsLegacy = new Dictionary<HarmonicPatternType, int>();
        var countsAmp2 = new Dictionary<HarmonicPatternType, int>();
        var countsAmp3 = new Dictionary<HarmonicPatternType, int>();

        foreach (HarmonicPatternType type in Enum.GetValues(typeof(HarmonicPatternType)))
        {
            countsLegacy[type] = 0;
            countsAmp2[type] = 0;
            countsAmp3[type] = 0;
        }

        for (int seed = 1; seed <= 20; seed++)
        {
            var bars = SyntheticBars.SeededRandomWalk(seed, 400, 100m);

            var legacyPatterns = HarmonicPatternDetector.Detect(bars);
            foreach (var p in legacyPatterns) countsLegacy[p.PatternType]++;

            var amp2Patterns = HarmonicPatternDetector.DetectByLegAmplitude(bars, 2.0m);
            foreach (var p in amp2Patterns) countsAmp2[p.PatternType]++;

            var amp3Patterns = HarmonicPatternDetector.DetectByLegAmplitude(bars, 3.0m);
            foreach (var p in amp3Patterns) countsAmp3[p.PatternType]++;
        }

        _output.WriteLine("=== Phase D0 Measurement (Seeds 1..20, 400 bars) ===");
        _output.WriteLine($"{"PatternType",-20} | {"Legacy",-10} | {"Amp 2.0%",-10} | {"Amp 3.0%",-10}");
        _output.WriteLine(new string('-', 60));

        int totalLegacy = 0, totalAmp2 = 0, totalAmp3 = 0;
        foreach (HarmonicPatternType type in Enum.GetValues(typeof(HarmonicPatternType)))
        {
            _output.WriteLine($"{type,-20} | {countsLegacy[type],-10} | {countsAmp2[type],-10} | {countsAmp3[type],-10}");
            totalLegacy += countsLegacy[type];
            totalAmp2 += countsAmp2[type];
            totalAmp3 += countsAmp3[type];
        }
        _output.WriteLine(new string('-', 60));
        _output.WriteLine($"{"Total",-20} | {totalLegacy,-10} | {totalAmp2,-10} | {totalAmp3,-10}");

        Assert.True(totalAmp2 >= 0);
    }

    #endregion

    #region Phase D2: Leg Amplitude Validation & Mode (D4 = C')

    [Fact]
    public void ValidateLegSwingPercent_ExactBoundary_DecimalPrecision()
    {
        var p0 = new PivotPoint(0, DateTime.Today, 100m, isHigh: false);
        var p1 = new PivotPoint(5, DateTime.Today.AddDays(5), 102m, isHigh: true); // Exactly 2.0% swing: (102-100)/100 = 2%

        var legsPass = new (PivotPoint, PivotPoint)[] { (p0, p1) };
        Assert.True(PatternFormationValidator.ValidateLegSwingPercent(legsPass, 2.0m));

        var p1Fail = new PivotPoint(5, DateTime.Today.AddDays(5), 101.99m, isHigh: true); // 1.99% swing
        var legsFail = new (PivotPoint, PivotPoint)[] { (p0, p1Fail) };
        Assert.False(PatternFormationValidator.ValidateLegSwingPercent(legsFail, 2.0m));

        // Guards
        Assert.False(PatternFormationValidator.ValidateLegSwingPercent(null!, 2.0m));
        Assert.False(PatternFormationValidator.ValidateLegSwingPercent(Array.Empty<(PivotPoint, PivotPoint)>(), 2.0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatternFormationValidator.ValidateLegSwingPercent(legsPass, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatternFormationValidator.ValidateLegSwingPercent(legsPass, -1m));
    }

    [Fact]
    public void HarmonicPivotMode_LegacyIsZero_LocalExtremaAmplitudeIsOne()
    {
        Assert.Equal(0, (int)HarmonicPivotMode.Legacy);
        Assert.Equal(1, (int)HarmonicPivotMode.LocalExtremaAmplitude);
    }

    #endregion

    #region Phase D3: Confirmation & Recency Window (D5 = B'', N = 5)

    [Fact]
    public void HarmonicPatternResult_ConfirmedAtIndex_Equals_DIndexPlusWindow()
    {
        var x = new PivotPoint(0, DateTime.Today, 100m, isHigh: false);
        var a = new PivotPoint(10, DateTime.Today.AddDays(10), 120m, isHigh: true);
        var b = new PivotPoint(15, DateTime.Today.AddDays(15), 108m, isHigh: false);
        var c = new PivotPoint(20, DateTime.Today.AddDays(20), 115m, isHigh: true);
        var d = new PivotPoint(25, DateTime.Today.AddDays(25), 104m, isHigh: false);

        const int window = 3;
        int expectedConfirmedAt = d.Index + window; // 28

        var result = new HarmonicPatternResult(
            HarmonicPatternType.Gartley, x, a, b, c, d, 0.85, 103m, 105m, isBullish: true, confirmedAtIndex: expectedConfirmedAt);

        Assert.Equal(expectedConfirmedAt, result.ConfirmedAtIndex);
    }

    [Fact]
    public void DetectConfirmedWithin_FiltersByMaxBarsSinceConfirmation()
    {
        var bars = SyntheticBars.SeededRandomWalk(1, 300, 100m);

        // Detect all patterns by leg amplitude
        var allPatterns = HarmonicPatternDetector.DetectByLegAmplitude(bars, 2.0m);
        Assert.NotEmpty(allPatterns);

        // Filter within 5 bars of confirmation
        var confirmedWithin5 = HarmonicPatternDetector.DetectConfirmedWithin(bars, maxBarsSinceConfirmation: 5, minLegSwingPercent: 2.0m);

        int lastBarIndex = bars.Length - 1;
        foreach (var p in confirmedWithin5)
        {
            Assert.NotNull(p.ConfirmedAtIndex);
            Assert.True(p.ConfirmedAtIndex.Value <= lastBarIndex);
            Assert.True((lastBarIndex - p.ConfirmedAtIndex.Value) <= 5,
                $"Pattern confirmed at {p.ConfirmedAtIndex.Value} must be within 5 bars of last bar {lastBarIndex}");
        }
    }

    #endregion

    #region Phase D3b: Pattern Definition Corrections (D7 = 7a + 7b)

    [Fact]
    public void WhiteSwan_IsStrictlyBullish_BlackSwan_IsStrictlyBearish()
    {
        // 7a: FindPatternsFromPivots must skip WhiteSwan when !isBullish, and BlackSwan when isBullish.
        // Fabricate bullish and bearish alternating pivots with ratios matching the Swan definition (AB/XA in [1.382, 2.0], etc.)
        var xBull = new PivotPoint(0, DateTime.Today, 100m, isHigh: false);
        var aBull = new PivotPoint(5, DateTime.Today.AddDays(5), 120m, isHigh: true); // XA = 20
        var bBull = new PivotPoint(10, DateTime.Today.AddDays(10), 90m, isHigh: false); // AB = 30 -> AB/XA = 1.5
        var cBull = new PivotPoint(15, DateTime.Today.AddDays(15), 100m, isHigh: true); // BC = 10 -> BC/AB = 0.333
        var dBull = new PivotPoint(20, DateTime.Today.AddDays(20), 85m, isHigh: false);  // CD = 15 -> CD/BC = 1.5, DXA = 35/20 = 1.75

        var bullishPivots = new List<PivotPoint> { xBull, aBull, bBull, cBull, dBull };
        var bullishResults = HarmonicPatternDetector.FindPatternsFromPivots(bullishPivots);

        // Bullish series must NOT contain BlackSwan
        Assert.DoesNotContain(bullishResults, r => r.PatternType == HarmonicPatternType.BlackSwan);

        // Mirror for bearish series (X is high, A is low)
        var xBear = new PivotPoint(0, DateTime.Today, 100m, isHigh: true);
        var aBear = new PivotPoint(5, DateTime.Today.AddDays(5), 80m, isHigh: false);  // XA = 20
        var bBear = new PivotPoint(10, DateTime.Today.AddDays(10), 110m, isHigh: true); // AB = 30 -> AB/XA = 1.5
        var cBear = new PivotPoint(15, DateTime.Today.AddDays(15), 100m, isHigh: false); // BC = 10 -> BC/AB = 0.333
        var dBear = new PivotPoint(20, DateTime.Today.AddDays(20), 115m, isHigh: true);  // CD = 15

        var bearishPivots = new List<PivotPoint> { xBear, aBear, bBear, cBear, dBear };
        var bearishResults = HarmonicPatternDetector.FindPatternsFromPivots(bearishPivots);

        // Bearish series must NOT contain WhiteSwan
        Assert.DoesNotContain(bearishResults, r => r.PatternType == HarmonicPatternType.WhiteSwan);
    }

    [Fact]
    public void SeaHorse_IsNeverSelected_InFindAbcdPatterns()
    {
        // 7b: SeaHorse is excluded from candidates, ensuring it is never returned.
        for (int seed = 1; seed <= 20; seed++)
        {
            var bars = SyntheticBars.SeededRandomWalk(seed, 200, 100m);
            var pivots = HarmonicPatternDetector.ExtractPivotsByLocalExtrema(bars, 2);
            var abcdPatterns = HarmonicPatternDetector.FindAbcdPatterns(pivots, bars);

            Assert.DoesNotContain(abcdPatterns, p => p.PatternType == HarmonicPatternType.SeaHorse);
        }
    }

    #endregion

    #region Phase D4: Direction Validation (F13)

    [Fact]
    public void IsDirectionConsistent_RejectsBullishRetracementPattern_WhenDAboveA()
    {
        // F13: Bullish retracement pattern (DXaMax <= 1.0, e.g. Gartley):
        // X=100, A=200, B=138.2, C=300, D=278.6.
        // In bullish, A must be > D (price retraces back down into XA).
        // Since D (278.6) > A (200), it must be rejected!
        var x = new PivotPoint(0, DateTime.Today, 100m, isHigh: false);
        var a = new PivotPoint(5, DateTime.Today.AddDays(5), 200m, isHigh: true);
        var b = new PivotPoint(10, DateTime.Today.AddDays(10), 138.2m, isHigh: false);
        var c = new PivotPoint(15, DateTime.Today.AddDays(15), 300m, isHigh: true);
        var d = new PivotPoint(20, DateTime.Today.AddDays(20), 278.6m, isHigh: false);

        var gartleyDef = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Gartley);
        Assert.True(gartleyDef.DXaMax <= 1.0, "Gartley is a retracement pattern with DXaMax <= 1.0");

        bool consistent = HarmonicPatternDetector.IsDirectionConsistent(x, a, b, c, d, isBullish: true, gartleyDef);
        Assert.False(consistent, "Bullish Gartley with D (278.6) > A (200) must be rejected by direction consistency.");
    }

    [Fact]
    public void IsDirectionConsistent_AcceptsValidBullishGartley()
    {
        // Valid bullish Gartley: X=100, A=200, B=138.2, C=176.4, D=121.4
        // X < A (100 < 200), A > B (200 > 138.2), B < C (138.2 < 176.4), C > D (176.4 > 121.4), D < A (121.4 < 200)
        var x = new PivotPoint(0, DateTime.Today, 100m, isHigh: false);
        var a = new PivotPoint(5, DateTime.Today.AddDays(5), 200m, isHigh: true);
        var b = new PivotPoint(10, DateTime.Today.AddDays(10), 138.2m, isHigh: false);
        var c = new PivotPoint(15, DateTime.Today.AddDays(15), 176.4m, isHigh: true);
        var d = new PivotPoint(20, DateTime.Today.AddDays(20), 121.4m, isHigh: false);

        var gartleyDef = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Gartley);
        bool consistent = HarmonicPatternDetector.IsDirectionConsistent(x, a, b, c, d, isBullish: true, gartleyDef);
        Assert.True(consistent, "Valid bullish Gartley must pass direction consistency.");
    }

    #endregion

    #region DetectConfirmedWithin Tests

    [Fact]
    public void DetectConfirmedWithin_FiltersByRecencyAndOrdersByConfidenceDescending()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var bars = SyntheticBars.SeededRandomWalk(seed, 300, 100m);
            int lastBar = bars.Length - 1;
            const int maxBars = 5;

            var results = HarmonicPatternDetector.DetectConfirmedWithin(bars, minLegSwingPercent: 2.0m, maxBarsSinceConfirmation: maxBars);
            for (int i = 0; i < results.Count; i++)
            {
                var p = results[i];
                Assert.True(p.ConfirmedAtIndex.HasValue);
                int age = lastBar - p.ConfirmedAtIndex.Value;
                Assert.True(age >= 0 && age <= maxBars, $"Pattern confirmation age {age} must be within [0, {maxBars}]");

                if (i > 0)
                {
                    Assert.True(results[i - 1].ConfidenceScore >= p.ConfidenceScore,
                        "Results must be sorted by ConfidenceScore descending.");
                }
            }
        }
    }

    [Fact]
    public void DetectConfirmedWithin_InvalidArguments_ThrowsArgumentOutOfRangeException()
    {
        var bars = SyntheticBars.SeededRandomWalk(1, 50, 100m);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HarmonicPatternDetector.DetectConfirmedWithin(bars, minLegSwingPercent: 0m, maxBarsSinceConfirmation: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HarmonicPatternDetector.DetectConfirmedWithin(bars, minLegSwingPercent: 2.0m, maxBarsSinceConfirmation: -1));
    }

    #endregion
}
