using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ElliottWaveDetectorTests
{
    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    // ──────────────────────────────────────────────────────
    // 1. Boundary / Guard Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void Detect_NullInput_ReturnsEmpty()
    {
        var result = ElliottWaveDetector.Detect(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_EmptyList_ReturnsEmpty()
    {
        var result = ElliottWaveDetector.Detect(new List<CandleData>());
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_TooFewCandles_ReturnsEmpty()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 5; i++)
            candles.Add(Candle(100, 101, 99, 100, i));
        var result = ElliottWaveDetector.Detect(candles);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_FlatPrices_ReturnsEmpty()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 100; i++)
            candles.Add(Candle(100, 100, 100, 100, i));
        var result = ElliottWaveDetector.Detect(candles);
        Assert.Empty(result);
    }

    [Fact]
    public void DetectLatest_NullInput_ReturnsNull()
    {
        var result = ElliottWaveDetector.DetectLatest(null!);
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────
    // 2. IsAlternating Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void IsAlternating_ProperAlternation_ReturnsTrue()
    {
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, isHigh: false),
            new PivotPoint(5, DateTime.Today, 110, isHigh: true),
            new PivotPoint(10, DateTime.Today, 105, isHigh: false),
            new PivotPoint(15, DateTime.Today, 108, isHigh: true),
            new PivotPoint(20, DateTime.Today, 102, isHigh: false),
            new PivotPoint(25, DateTime.Today, 115, isHigh: true),
        };
        Assert.True(ElliottWaveDetector.IsAlternating(pivots));
    }

    [Fact]
    public void IsAlternating_SameDirection_ReturnsFalse()
    {
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, isHigh: false),
            new PivotPoint(5, DateTime.Today, 110, isHigh: false), // should be High
            new PivotPoint(10, DateTime.Today, 105, isHigh: false),
            new PivotPoint(15, DateTime.Today, 108, isHigh: true),
            new PivotPoint(20, DateTime.Today, 102, isHigh: false),
            new PivotPoint(25, DateTime.Today, 115, isHigh: true),
        };
        Assert.False(ElliottWaveDetector.IsAlternating(pivots));
    }

    // ──────────────────────────────────────────────────────
    // 3. Absolute Rule Validation Tests (Bullish Impulse)
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateImpulseRules_ValidBullish_ReturnsTrue()
    {
        // Valid bullish: p0=100(L), p1=110(H), p2=105(L), p3=130(H), p4=115(L), p5=140(H)
        // W1=10, W2retrace=5, W3=25, W4retrace=15, W5=25
        // Rule1: p2(105)>p0(100) OK, Rule2: W3(25)>W1(10) and W3(25)>=W5(25) OK, Rule3: p4(115)>p1(110) OK
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 105, false),
            new PivotPoint(30, DateTime.Today, 130, true),
            new PivotPoint(40, DateTime.Today, 115, false),
            new PivotPoint(50, DateTime.Today, 140, true),
        };
        Assert.True(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: true));
    }

    [Fact]
    public void ValidateImpulseRules_Rule1Violation_Wave2BelowStart_ReturnsFalse()
    {
        // Rule 1 violation: p2(99) < p0(100) - Wave 2 retraces beyond Wave 1 start
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 99, false),   // Below p0!
            new PivotPoint(30, DateTime.Today, 130, true),
            new PivotPoint(40, DateTime.Today, 115, false),
            new PivotPoint(50, DateTime.Today, 140, true),
        };
        Assert.False(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: true));
    }

    [Fact]
    public void ValidateImpulseRules_Rule2Violation_Wave3Shortest_ReturnsFalse()
    {
        // Rule 2 violation: W3 is the shortest wave
        // W1=10 (100->110), W3=5 (105->110), W5=25 (110->135)
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 105, false),
            new PivotPoint(30, DateTime.Today, 110, true),   // W3 = only 5
            new PivotPoint(40, DateTime.Today, 110, false),
            new PivotPoint(50, DateTime.Today, 135, true),   // W5 = 25
        };
        Assert.False(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: true));
    }

    [Fact]
    public void ValidateImpulseRules_Rule3Violation_Wave4OverlapsWave1_ReturnsFalse()
    {
        // Rule 3 violation: p4(105) << p1(110), overlap exceeds tolerance
        // W1 range = 10. (110-105)/10 = 0.5 >> 0.05 tolerance
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 101, false),
            new PivotPoint(30, DateTime.Today, 130, true),
            new PivotPoint(40, DateTime.Today, 105, false),  // Overlaps W1 territory
            new PivotPoint(50, DateTime.Today, 140, true),
        };
        Assert.False(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: true));
    }

    // ──────────────────────────────────────────────────────
    // 4. Bearish Impulse Rules
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateImpulseRules_ValidBearish_ReturnsTrue()
    {
        // Valid bearish: p0=140(H), p1=130(L), p2=135(H), p3=110(L), p4=120(H), p5=100(L)
        // W1=10, W3=25, W5=20. Rule2: W3(25)>W1(10) OK
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 140, true),
            new PivotPoint(10, DateTime.Today, 130, false),
            new PivotPoint(20, DateTime.Today, 135, true),
            new PivotPoint(30, DateTime.Today, 110, false),
            new PivotPoint(40, DateTime.Today, 120, true),
            new PivotPoint(50, DateTime.Today, 100, false),
        };
        Assert.True(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: false));
    }

    [Fact]
    public void ValidateImpulseRules_BearishRule1Violation_ReturnsFalse()
    {
        // Bearish: p2 >= p0 (Wave 2 retraces above Wave 1 start)
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 140, true),
            new PivotPoint(10, DateTime.Today, 130, false),
            new PivotPoint(20, DateTime.Today, 141, true),  // Above p0!
            new PivotPoint(30, DateTime.Today, 110, false),
            new PivotPoint(40, DateTime.Today, 120, true),
            new PivotPoint(50, DateTime.Today, 100, false),
        };
        Assert.False(ElliottWaveDetector.ValidateImpulseRules(pivots, isBullish: false));
    }

    // ──────────────────────────────────────────────────────
    // 5. ScoreRatio Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ScoreRatio_ExactMidpoint_ReturnsOne()
    {
        // Mid of [0.382, 0.618] = 0.500
        double score = ElliottWaveDetector.ScoreRatio(0.500, 0.382, 0.618);
        Assert.True(score > 0.8, $"Score {score} should be close to 1.0");
    }

    [Fact]
    public void ScoreRatio_OutsideTolerance_ReturnsZero()
    {
        // [0.382, 0.618] with tolerance 0.20 means [0.182, 0.818]
        // 0.10 is outside
        double score = ElliottWaveDetector.ScoreRatio(0.10, 0.382, 0.618);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreRatio_WithinTolerance_ReturnsPositive()
    {
        double score = ElliottWaveDetector.ScoreRatio(0.60, 0.382, 0.618);
        Assert.True(score > 0);
    }

    // ──────────────────────────────────────────────────────
    // 6. ScoreImpulseWave Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ScoreImpulseWave_IdealFibonacci_ReturnsHighScore()
    {
        // Construct pivots with ideal Fibonacci ratios:
        // W1=10, W2retrace=6.18 (61.8% of W1), W3=16.18 (161.8% of W1),
        // W4retrace=3.82 (23.6% of W3=16.18), W5=10 (100% of W1)
        decimal w1 = 10m;
        decimal p0 = 100m;
        decimal p1 = p0 + w1;                              // 110
        decimal p2 = p1 - w1 * 0.618m;                     // 103.82
        decimal w3 = w1 * 1.618m;                           // 16.18
        decimal p3 = p2 + w3;                               // 120.00
        decimal p4 = p3 - w3 * 0.382m;                     // 113.82
        decimal w5 = w1 * 1.0m;                             // 10
        decimal p5 = p4 + w5;                               // 123.82

        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, p0, false),
            new PivotPoint(10, DateTime.Today.AddDays(10), p1, true),
            new PivotPoint(20, DateTime.Today.AddDays(20), p2, false),
            new PivotPoint(30, DateTime.Today.AddDays(30), p3, true),
            new PivotPoint(40, DateTime.Today.AddDays(40), p4, false),
            new PivotPoint(50, DateTime.Today.AddDays(50), p5, true),
        };

        double score = ElliottWaveDetector.ScoreImpulseWave(pivots, isBullish: true);
        Assert.True(score >= ChartConstants.ElliottMinConfidence,
            $"Ideal impulse score {score} should be >= {ChartConstants.ElliottMinConfidence}");
    }

    [Fact]
    public void ScoreImpulseWave_CompletelyWrong_ReturnsLowScore()
    {
        // Very poor Fibonacci ratios
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 101, true),   // W1 = 1
            new PivotPoint(20, DateTime.Today, 100.5m, false),
            new PivotPoint(30, DateTime.Today, 200, true),   // W3 = 99.5 (way too large)
            new PivotPoint(40, DateTime.Today, 199, false),
            new PivotPoint(50, DateTime.Today, 200.5m, true),
        };
        double score = ElliottWaveDetector.ScoreImpulseWave(pivots, isBullish: true);
        // The extreme W3/W1 ratio should produce a very low score
        Assert.True(score < 0.5, $"Wrong ratios score {score} should be low");
    }

    // ──────────────────────────────────────────────────────
    // 7. Corrective Wave Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateCorrectiveRules_ValidBullish_ReturnsTrue()
    {
        // Bullish corrective: p0(L)->p1(H)->p2(L)->p3(H), p2 > p0
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 105, false),
            new PivotPoint(30, DateTime.Today, 115, true),
        };
        Assert.True(ElliottWaveDetector.ValidateCorrectiveRules(pivots, isBullish: true));
    }

    [Fact]
    public void ValidateCorrectiveRules_BullishViolation_ReturnsFalse()
    {
        // Wave B retraces below A start
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),
            new PivotPoint(20, DateTime.Today, 99, false),  // Below p0
            new PivotPoint(30, DateTime.Today, 115, true),
        };
        Assert.False(ElliottWaveDetector.ValidateCorrectiveRules(pivots, isBullish: true));
    }

    [Fact]
    public void ScoreCorrectiveWave_IdealRatios_ReturnsHighScore()
    {
        // A=10, B retraces 50% of A (5), C extends 100% of A (10)
        var pivots = new[]
        {
            new PivotPoint(0, DateTime.Today, 100, false),
            new PivotPoint(10, DateTime.Today, 110, true),   // A = 10
            new PivotPoint(20, DateTime.Today, 105, false),  // B retrace = 5, ratio=0.5
            new PivotPoint(30, DateTime.Today, 115, true),   // C = 10, ratio=1.0
        };
        double score = ElliottWaveDetector.ScoreCorrectiveWave(pivots, isBullish: true);
        Assert.True(score >= ChartConstants.ElliottMinConfidence,
            $"Ideal corrective score {score} should be >= {ChartConstants.ElliottMinConfidence}");
    }

    // ──────────────────────────────────────────────────────
    // 8. ElliottWaveResult Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ElliottWaveResult_ToString_ContainsKeyInfo()
    {
        var points = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100, false),
            new(50, DateTime.Today, 140, true),
        };
        var result = new ElliottWaveResult(true, true, points, 0.85, ElliottWavePhase.Wave3Start);

        string str = result.ToString();
        Assert.Contains("Impulse", str);
        Assert.Contains("Bullish", str);
        Assert.Contains("Wave3Start", str);
        Assert.Contains("0.85", str);
    }

    [Fact]
    public void ElliottWaveResult_Span_ReturnsCorrectValue()
    {
        var points = new List<PivotPoint>
        {
            new(5, DateTime.Today, 100, false),
            new(55, DateTime.Today, 140, true),
        };
        var result = new ElliottWaveResult(true, true, points, 0.75, ElliottWavePhase.Wave5);
        Assert.Equal(50, result.Span);
    }

    [Fact]
    public void ElliottWaveResult_ConfidenceScore_IsClamped()
    {
        var points = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100, false),
            new(50, DateTime.Today, 140, true),
        };
        var result = new ElliottWaveResult(true, true, points, 1.5, ElliottWavePhase.Wave5);
        Assert.Equal(1.0, result.ConfidenceScore);
    }

    // ──────────────────────────────────────────────────────
    // 9. Full Integration Tests with Candle Data
    // ──────────────────────────────────────────────────────

    [Fact]
    public void FindImpulseWaves_FromIdealPivots_DetectsPattern()
    {
        // Construct pivots forming an ideal bullish impulse wave
        var pivots = new List<PivotPoint>
        {
            new(0, DateTime.Today, 100m, false),                        // Start
            new(10, DateTime.Today.AddDays(10), 110m, true),            // End W1 (+10)
            new(20, DateTime.Today.AddDays(20), 103.82m, false),        // End W2 (retrace 61.8%)
            new(30, DateTime.Today.AddDays(30), 120m, true),            // End W3 (+16.18)
            new(40, DateTime.Today.AddDays(40), 114m, false),           // End W4
            new(50, DateTime.Today.AddDays(50), 124m, true),            // End W5
        };

        // Create minimal candle data to satisfy the method
        var candles = new List<CandleData>();
        for (int i = 0; i <= 50; i++)
            candles.Add(Candle(100 + i * 0.1m, 101 + i * 0.1m, 99 + i * 0.1m, 100 + i * 0.1m, i));

        var results = ElliottWaveDetector.FindImpulseWaves(pivots, candles);

        Assert.NotEmpty(results);
        Assert.True(results[0].IsImpulse);
        Assert.True(results[0].IsBullish);
    }
}
