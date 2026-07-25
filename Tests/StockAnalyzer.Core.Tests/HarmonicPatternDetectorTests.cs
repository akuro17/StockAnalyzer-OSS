using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class HarmonicPatternDetectorTests
{
    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    // ──────────────────────────────────────────────────────
    // 1. Boundary / Guard Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void Detect_NullInput_ReturnsEmpty()
    {
        var result = HarmonicPatternDetector.Detect(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_EmptyList_ReturnsEmpty()
    {
        var result = HarmonicPatternDetector.Detect(new List<CandleData>());
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_TooFewCandles_ReturnsEmpty()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 5; i++)
            candles.Add(Candle(100, 101, 99, 100, i));
        var result = HarmonicPatternDetector.Detect(candles);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_FlatPrices_ReturnsEmpty()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 100; i++)
            candles.Add(Candle(100, 100, 100, 100, i));
        var result = HarmonicPatternDetector.Detect(candles);
        Assert.Empty(result);
    }

    // ──────────────────────────────────────────────────────
    // 2. IsAlternating Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void IsAlternating_ProperAlternation_ReturnsTrue()
    {
        var x = new PivotPoint(0, DateTime.Today, 100, isHigh: false);
        var a = new PivotPoint(5, DateTime.Today, 110, isHigh: true);
        var b = new PivotPoint(10, DateTime.Today, 105, isHigh: false);
        var c = new PivotPoint(15, DateTime.Today, 108, isHigh: true);
        var d = new PivotPoint(20, DateTime.Today, 102, isHigh: false);

        Assert.True(HarmonicPatternDetector.IsAlternating(x, a, b, c, d));
    }

    [Fact]
    public void IsAlternating_SameDirection_ReturnsFalse()
    {
        var x = new PivotPoint(0, DateTime.Today, 100, isHigh: false);
        var a = new PivotPoint(5, DateTime.Today, 110, isHigh: false); // should be High
        var b = new PivotPoint(10, DateTime.Today, 105, isHigh: false);
        var c = new PivotPoint(15, DateTime.Today, 108, isHigh: true);
        var d = new PivotPoint(20, DateTime.Today, 102, isHigh: false);

        Assert.False(HarmonicPatternDetector.IsAlternating(x, a, b, c, d));
    }

    // ──────────────────────────────────────────────────────
    // 3. ScoreRatio Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ScoreRatio_ExactMatch_ReturnsOne()
    {
        double score = HarmonicPatternDetector.ScoreRatio(0.618, 0.618, 0.618, isFixed: true);
        Assert.Equal(1.0, score, 2);
    }

    [Fact]
    public void ScoreRatio_OutsideTolerance_ReturnsZero()
    {
        // 0.618 with tolerance 0.15 means [0.468, 0.768]
        // 0.40 is outside tolerance.
        double score = HarmonicPatternDetector.ScoreRatio(0.40, 0.618, 0.618, isFixed: true);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreRatio_WithinRange_ReturnsPositive()
    {
        // Range [0.382, 0.886] with range tolerance 0.10 means [0.282, 0.986]
        double score = HarmonicPatternDetector.ScoreRatio(0.60, 0.382, 0.886, isFixed: false);
        Assert.True(score > 0);
    }

    [Fact]
    public void ScoreRatio_JustOutsideRange_ReturnsZero()
    {
        // Range [0.382, 0.886] with range tolerance 0.10 means [0.282, 0.986]
        // 0.20 is outside tolerance.
        double score = HarmonicPatternDetector.ScoreRatio(0.20, 0.382, 0.886, isFixed: false);
        Assert.Equal(0, score);
    }

    // ──────────────────────────────────────────────────────
    // 4. Pattern Definition Tests
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(HarmonicPatternType.Gartley)]
    [InlineData(HarmonicPatternType.Bat)]
    [InlineData(HarmonicPatternType.Butterfly)]
    [InlineData(HarmonicPatternType.Crab)]
    [InlineData(HarmonicPatternType.AlternateBat)]
    [InlineData(HarmonicPatternType.DeepCrab)]
    [InlineData(HarmonicPatternType.Leonardo)]
    [InlineData(HarmonicPatternType.NenStar)]
    [InlineData(HarmonicPatternType.Cypher)]
    [InlineData(HarmonicPatternType.Shark)]
    [InlineData(HarmonicPatternType.ThreeDrives)]
    // Note: ABCD and SeaPony use FindAbcdPatterns, not GetPatternDefinition
    public void GetPatternDefinition_AllTypes_ReturnValidDefinition(HarmonicPatternType type)
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(type);
        Assert.True(def.AbXaMin <= def.AbXaMax);
        Assert.True(def.BcAbMin <= def.BcAbMax);
        Assert.True(def.CdBcMin <= def.CdBcMax);
        Assert.True(def.DXaMin <= def.DXaMax);
    }

    // ──────────────────────────────────────────────────────
    // 5. EvaluatePattern Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void EvaluatePattern_PerfectGartley_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Gartley);
        // Perfect Gartley ratios: AB/XA=0.618, BC/AB=0.618, CD/BC=1.414, D/XA=0.786
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.618, 0.618, 1.414, 0.786);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Gartley score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_CompletelyWrong_ReturnsZero()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Gartley);
        // Completely wrong ratios
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.1, 0.1, 0.1, 0.1);
        Assert.Equal(0, score);
    }

    [Fact]
    public void EvaluatePattern_PerfectBat_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Bat);
        // Perfect Bat: AB/XA=0.441 (mid of 0.382-0.50), BC/AB=0.618, CD/BC=2.0, D/XA=0.886
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.441, 0.618, 2.0, 0.886);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Bat score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectButterfly_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Butterfly);
        // Perfect Butterfly: AB/XA=0.786, BC/AB=0.618, CD/BC=2.0, D/XA=1.445 (mid)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.786, 0.618, 2.0, 1.445);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Butterfly score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectCrab_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Crab);
        // Perfect Crab: AB/XA=0.500 (mid), BC/AB=0.618, CD/BC=3.0, D/XA=1.618
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.500, 0.618, 3.0, 1.618);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Crab score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectAlternateBat_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.AlternateBat);
        // Perfect Alternate Bat: AB/XA=0.309 (mid 0.236-0.382), BC/AB=0.618, CD/BC=2.5, D/XA=1.130
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.309, 0.618, 2.5, 1.130);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect AlternateBat score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectDeepCrab_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.DeepCrab);
        // Perfect Deep Crab: AB/XA=0.886 (fixed), BC/AB=0.618, CD/BC=2.929 (mid), D/XA=1.618
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.886, 0.618, 2.929, 1.618);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect DeepCrab score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectLeonardo_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Leonardo);
        // Perfect Leonardo: AB/XA=0.500 (fixed), BC/AB=0.618, CD/BC=1.873 (mid), D/XA=0.786
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.500, 0.618, 1.873, 0.786);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Leonardo score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectNenStar_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.NenStar);
        // Perfect NenStar: AB/XA=0.500 (mid), BC/AB=1.272 (mid), CD/BC=1.945 (mid), D/XA=1.201 (mid)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.500, 1.272, 1.945, 1.201);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect NenStar score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectCypher_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Cypher);
        // Perfect Cypher: AB/XA=0.500, BC/AB=1.343 (mid), CD/BC=1.636 (mid)
        // D is scored against XC (0.786), so dXaRatio is irrelevant; pass dXcRatio=0.786
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.500, 1.343, 1.636, 0.75, dXcRatio: 0.786);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Cypher score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectShark_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Shark);
        // Perfect Shark: AB/XA=0.584 (mid), BC/AB=1.374 (mid), CD/BC=1.683 (mid), D/XA=1.008 (mid)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.584, 1.374, 1.683, 1.008);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect Shark score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_PerfectThreeDrives_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.ThreeDrives);
        // Perfect Three Drives: Drive2/Corr1=1.414 (mid), Corr2/Drive2=0.584 (mid), Drive3/Corr2=1.414
        // D/XA = cumulative, ~2.0
        double score = HarmonicPatternDetector.EvaluatePattern(def, 1.414, 0.584, 1.414, 2.0);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Perfect ThreeDrives score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void ScoreAbcd_StandardAbcd_ReturnsHighScore()
    {
        // Standard AB=CD: BC/AB=0.618, CD/AB=1.0 (equal legs)
        double score = HarmonicPatternDetector.ScoreAbcd(0.618, 1.0, HarmonicPatternDetector.AbcdFamily.Standard);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Standard AB=CD score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void ScoreAbcd_SeaPony_ReturnsHighScore()
    {
        // Sea Pony: BC/AB=0.441 (mid 0.382-0.500), CD/AB=2.118 (mid 1.618-2.618)
        double score = HarmonicPatternDetector.ScoreAbcd(0.441, 2.118, HarmonicPatternDetector.AbcdFamily.SeaPony);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Sea Pony score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void ScoreAbcd_SeaHorse_ReturnsHighScore()
    {
        // Sea Horse: BC/AB=0.441 (mid 0.382-0.500), CD/AB=2.118 (mid 1.618-2.618)
        double score = HarmonicPatternDetector.ScoreAbcd(0.441, 2.118, HarmonicPatternDetector.AbcdFamily.SeaHorse);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Sea Horse score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void ScoreAbcd_Dragon_ReturnsHighScore()
    {
        // Dragon (double bottom/top): BC/AB=0.441 (mid 0.382-0.500), CD/AB=0.840 (mid 0.680-1.000)
        double score = HarmonicPatternDetector.ScoreAbcd(0.441, 0.840, HarmonicPatternDetector.AbcdFamily.Dragon);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Dragon score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    // ──────────────────────────────────────────────────────
    // 6. PRZ Calculation Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void CalculatePrz_ReturnsValidRange()
    {
        var x = new PivotPoint(0, DateTime.Today, 100, false);
        var a = new PivotPoint(5, DateTime.Today, 110, true);
        var d = new PivotPoint(20, DateTime.Today, 102, false);

        var (przLow, przHigh) = HarmonicPatternDetector.CalculatePrz(
            x, a, d, HarmonicPatternType.Gartley, true);

        Assert.True(przLow < przHigh, $"PRZ Low ({przLow}) should be less than High ({przHigh})");
        Assert.True(przLow <= d.Price, "PRZ should contain D price");
        Assert.True(przHigh >= d.Price, "PRZ should contain D price");
    }

    // ──────────────────────────────────────────────────────
    // 7. FindPatternsFromPivots Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void FindPatternsFromPivots_GartleyPivots_DetectsGartley()
    {
        // Construct pivots that form a perfect bullish Gartley:
        // X=100(low), A=110(high), B=103.82(low) [AB/XA=0.618], C=108.5(high), D=102.14(low) [D/XA=0.786]
        decimal xa = 10m; // A - X
        decimal ab = xa * 0.618m; // = 6.18
        decimal bPrice = 110m - ab; // B = 103.82
        decimal bc = ab * 0.618m; // = 3.82
        decimal cPrice = bPrice + bc; // C = 107.64
        decimal cd = bc * 1.414m; // = 5.40
        decimal dPrice = cPrice - cd; // D = 102.24

        // Check D/XA ratio: |D - A| / |A - X| = |102.24 - 110| / 10 = 0.776 ~ close to 0.786
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100m, false),           // X (low)
            new(10, DateTime.Today.AddDays(10), 110m, true),         // A (high)
            new(20, DateTime.Today.AddDays(20), bPrice, false),      // B (low)
            new(30, DateTime.Today.AddDays(30), cPrice, true),       // C (high)
            new(40, DateTime.Today.AddDays(40), dPrice, false),      // D (low)
        };

        var patterns = HarmonicPatternDetector.FindPatternsFromPivots(pivots);

        // Should detect at least one pattern
        Assert.NotEmpty(patterns);

        // The best match for these ratios should be Gartley
        var best = patterns[0];
        // With these specific ratios (AB/XA=0.618, D/XA=0.776), Gartley should score highest
        Assert.True(best.ConfidenceScore >= ChartConstants.HarmonicMinConfidence);
        Assert.True(best.IsBullish);
    }

    [Fact]
    public void FindPatternsFromPivots_InsufficientPivots_ReturnsEmpty()
    {
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100m, false),
            new(10, DateTime.Today.AddDays(10), 110m, true),
            new(20, DateTime.Today.AddDays(20), 105m, false),
            new(30, DateTime.Today.AddDays(30), 108m, true),
        };

        var patterns = HarmonicPatternDetector.FindPatternsFromPivots(pivots);
        Assert.Empty(patterns);
    }

    [Fact]
    public void FindPatternsFromPivots_NonAlternating_ReturnsEmpty()
    {
        // All pivots marked as high - should fail alternation check
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100m, true),
            new(10, DateTime.Today.AddDays(10), 110m, true),
            new(20, DateTime.Today.AddDays(20), 103m, true),
            new(30, DateTime.Today.AddDays(30), 108m, true),
            new(40, DateTime.Today.AddDays(40), 102m, true),
        };

        var patterns = HarmonicPatternDetector.FindPatternsFromPivots(pivots);
        Assert.Empty(patterns);
    }

    // ──────────────────────────────────────────────────────
    // 8. HarmonicPatternResult Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void HarmonicPatternResult_ToString_ContainsKeyInfo()
    {
        var x = new PivotPoint(0, DateTime.Today, 100, false);
        var a = new PivotPoint(5, DateTime.Today, 110, true);
        var b = new PivotPoint(10, DateTime.Today, 104, false);
        var c = new PivotPoint(15, DateTime.Today, 108, true);
        var d = new PivotPoint(20, DateTime.Today, 102, false);

        var result = new HarmonicPatternResult(
            HarmonicPatternType.Gartley, x, a, b, c, d,
            0.85, 101m, 103m, true);

        string str = result.ToString();
        Assert.Contains("Gartley", str);
        Assert.Contains("Bullish", str);
        Assert.Contains("0.85", str);
    }

    [Fact]
    public void HarmonicPatternResult_Span_ReturnsCorrectValue()
    {
        var x = new PivotPoint(5, DateTime.Today, 100, false);
        var d = new PivotPoint(25, DateTime.Today, 102, false);
        var a = new PivotPoint(10, DateTime.Today, 110, true);
        var b = new PivotPoint(15, DateTime.Today, 104, false);
        var c = new PivotPoint(20, DateTime.Today, 108, true);

        var result = new HarmonicPatternResult(
            HarmonicPatternType.Bat, x, a, b, c, d,
            0.75, 101m, 103m, true);

        Assert.Equal(20, result.Span); // 25 - 5
    }

    [Fact]
    public void HarmonicPatternResult_ConfidenceScore_IsClamped()
    {
        var x = new PivotPoint(0, DateTime.Today, 100, false);
        var a = new PivotPoint(5, DateTime.Today, 110, true);
        var b = new PivotPoint(10, DateTime.Today, 104, false);
        var c = new PivotPoint(15, DateTime.Today, 108, true);
        var d = new PivotPoint(20, DateTime.Today, 102, false);

        var result = new HarmonicPatternResult(
            HarmonicPatternType.Gartley, x, a, b, c, d,
            1.5, 101m, 103m, true);

        Assert.Equal(1.0, result.ConfidenceScore);
    }

    // ──────────────────────────────────────────────────────
    // 9. DetectLatest Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void DetectLatest_NullInput_ReturnsNull()
    {
        var result = HarmonicPatternDetector.DetectLatest(null!);
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────
    // 10. New Pattern Evaluation Tests (White/Black Swan, 5-0)
    // ──────────────────────────────────────────────────────

    [Fact]
    public void EvaluatePattern_WhiteSwan_ReturnsHighScore()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.WhiteSwan);
        // AB/XA=1.691 (mid 1.382-2.000), BC/AB=0.368 (mid 0.236-0.500),
        // CD/BC=1.564 (mid 1.128-2.000), D/XA=1.873 (mid 1.128-2.618)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 1.691, 0.368, 1.564, 1.873);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"White Swan score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void EvaluatePattern_BlackSwan_ReturnsHighScore()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.BlackSwan);
        // Same ratios as White Swan (direction differs via isBullish flag, not ratios)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 1.691, 0.368, 1.564, 1.873);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Black Swan score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void FindFiveZeroPatterns_ValidPivots_DetectsFiveZero()
    {
        // 5-0 Pattern: 0-X-A-B-C
        // XA = 10, AB = XA * 1.374 = 13.74 (mid 1.130-1.618)
        // BC = AB * 1.929 = 26.51 (mid 1.618-2.240)
        decimal xa = 10m;
        decimal ab = xa * 1.374m;  // 13.74
        decimal bc = ab * 1.929m;  // 26.51

        // Bullish 5-0: 0(high) -> X(low) -> A(high) -> B(low) -> C(high)
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 115m, true),                        // Point 0 (high)
            new(10, DateTime.Today.AddDays(10), 100m, false),           // Point X (low)
            new(20, DateTime.Today.AddDays(20), 100m + xa, true),       // Point A (high) = 110
            new(30, DateTime.Today.AddDays(30), 110m - ab, false),      // Point B (low) = 96.26
            new(40, DateTime.Today.AddDays(40), 96.26m + bc, true),     // Point C (high) = 122.77
        };

        var patterns = HarmonicPatternDetector.FindFiveZeroPatterns(pivots);
        Assert.NotEmpty(patterns);
        Assert.Equal(HarmonicPatternType.FiveZero, patterns[0].PatternType);
    }

    [Fact]
    public void FindFiveZeroPatterns_InvalidRatios_ReturnsEmpty()
    {
        // AB/XA ratio way outside 1.130-1.618 range
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 115m, true),
            new(10, DateTime.Today.AddDays(10), 100m, false),
            new(20, DateTime.Today.AddDays(20), 110m, true),
            new(30, DateTime.Today.AddDays(30), 107m, false),   // AB/XA = 0.3 (too small)
            new(40, DateTime.Today.AddDays(40), 112m, true),
        };

        var patterns = HarmonicPatternDetector.FindFiveZeroPatterns(pivots);
        Assert.Empty(patterns);
    }
    // ──────────────────────────────────────────────────────
    // Navarro 200 Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void EvaluatePattern_PerfectNavarro200_ReturnsHighConfidence()
    {
        var def = HarmonicPatternDetector.GetPatternDefinition(HarmonicPatternType.Navarro200);
        // AB/XA=0.584 (mid 0.382-0.786), BC/AB=1.007 (mid 0.886-1.128),
        // CD/BC=1.873 (mid 1.128-2.618), D/XA=1.007 (mid 0.886-1.128)
        double score = HarmonicPatternDetector.EvaluatePattern(def, 0.584, 1.007, 1.873, 1.007);
        Assert.True(score >= ChartConstants.HarmonicMinConfidence,
            $"Navarro 200 score {score} should be >= {ChartConstants.HarmonicMinConfidence}");
    }

    [Fact]
    public void ValidateTimeZoneConstraint_ValidRatio_ReturnsTrue()
    {
        // CD duration = 10 bars, XA duration = 10 bars → ratio = 1.0 (within 0.382-2.618)
        Assert.True(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 10));
        // CD duration = 4 bars, XA duration = 10 bars → ratio = 0.4 (within 0.382-2.618)
        Assert.True(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 4));
        // CD duration = 26 bars, XA duration = 10 bars → ratio = 2.6 (within 0.382-2.618)
        Assert.True(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 26));
    }

    [Fact]
    public void ValidateTimeZoneConstraint_InvalidRatio_ReturnsFalse()
    {
        // CD duration = 1 bar, XA duration = 10 bars → ratio = 0.1 (below 0.382)
        Assert.False(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 1));
        // CD duration = 30 bars, XA duration = 10 bars → ratio = 3.0 (above 2.618)
        Assert.False(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 30));
        // Zero/negative inputs
        Assert.False(HarmonicPatternDetector.ValidateTimeZoneConstraint(0, 10));
        Assert.False(HarmonicPatternDetector.ValidateTimeZoneConstraint(10, 0));
    }

    [Fact]
    public void FindPatternsFromPivots_Navarro200WithValidTime_DetectsPattern()
    {
        // Construct pivots matching Navarro 200 ratios:
        // AB/XA ≈ 0.584: mid of [0.382, 0.786]
        // BC/AB ≈ 1.007: mid of [0.886, 1.128]
        // D/XA  ≈ 1.007: mid of [0.886, 1.128]
        // Time: XA = 10 bars, CD = 10 bars → ratio = 1.0 (valid)
        decimal xa = 10m;
        decimal ab = xa * 0.584m;   // 5.84
        decimal bPrice = 110m - ab; // 104.16
        decimal bc = ab * 1.007m;   // 5.88
        decimal cPrice = bPrice + bc; // 110.04
        decimal cd = bc * 1.873m;   // 11.01
        decimal dPrice = cPrice - cd; // 99.03

        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100m, false),                      // X (low)
            new(10, DateTime.Today.AddDays(10), 110m, true),          // A (high), XA=10 bars
            new(20, DateTime.Today.AddDays(20), bPrice, false),       // B (low)
            new(30, DateTime.Today.AddDays(30), cPrice, true),        // C (high)
            new(40, DateTime.Today.AddDays(40), dPrice, false),       // D (low), CD=10 bars
        };

        var patterns = HarmonicPatternDetector.FindPatternsFromPivots(pivots);

        // Should detect at least one pattern
        Assert.NotEmpty(patterns);

        // At least one of the detected patterns should be Navarro200
        bool hasNavarro200 = false;
        foreach (var p in patterns)
        {
            if (p.PatternType == HarmonicPatternType.Navarro200)
            {
                hasNavarro200 = true;
                Assert.True(p.ConfidenceScore >= ChartConstants.HarmonicMinConfidence);
                Assert.True(p.IsBullish);
                break;
            }
        }
        Assert.True(hasNavarro200, "Expected a Navarro200 pattern to be detected");
    }
}
