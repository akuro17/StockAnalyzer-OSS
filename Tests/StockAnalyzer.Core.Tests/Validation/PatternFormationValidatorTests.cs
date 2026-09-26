using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Validation;
using Xunit;

namespace StockAnalyzer.Core.Tests.Validation;

public class PatternFormationValidatorTests
{
    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    // ──────────────────────────────────────────────────────
    // 1. ValidateMinBars Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateMinBars_SpanBelowMin_ReturnsFalse()
    {
        Assert.False(PatternFormationValidator.ValidateMinBars(10, 15));
    }

    [Fact]
    public void ValidateMinBars_SpanEqualToMin_ReturnsTrue()
    {
        Assert.True(PatternFormationValidator.ValidateMinBars(15, 15));
    }

    [Fact]
    public void ValidateMinBars_SpanAboveMin_ReturnsTrue()
    {
        Assert.True(PatternFormationValidator.ValidateMinBars(30, 15));
    }

    [Fact]
    public void ValidateMinBars_ZeroSpan_ReturnsFalse()
    {
        Assert.False(PatternFormationValidator.ValidateMinBars(0, 15));
    }

    // ──────────────────────────────────────────────────────
    // 2. ValidateVolatility Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateVolatility_LegBelowAtr_ReturnsFalse()
    {
        // Create candles with ATR ~2, but leg move only 0.5 (below 0.5 * 2 = 1.0)
        var candles = new List<CandleData>
        {
            Candle(100, 101, 99, 100, 0),
            Candle(100, 102, 98, 100, 1),
            Candle(100, 101, 99, 100, 2),
            Candle(100, 102, 98, 100, 3),
            Candle(100, 101, 99, 100.5m, 4), // Close = 100.5, move = 0.5
        };

        var legs = new List<(int, int)> { (0, 4) };
        Assert.False(PatternFormationValidator.ValidateVolatility(
            legs, candles, ChartConstants.FormationVolatilityAtrMultiplier));
    }

    [Fact]
    public void ValidateVolatility_LegAboveAtr_ReturnsTrue()
    {
        // Create candles with ATR ~2, leg move = 10 (well above 0.5 * 2 = 1.0)
        var candles = new List<CandleData>
        {
            Candle(100, 102, 98,  100, 0),
            Candle(102, 104, 100, 103, 1),
            Candle(104, 106, 102, 105, 2),
            Candle(106, 108, 104, 107, 3),
            Candle(108, 112, 106, 110, 4), // Close = 110, move = 10
        };

        var legs = new List<(int, int)> { (0, 4) };
        Assert.True(PatternFormationValidator.ValidateVolatility(
            legs, candles, ChartConstants.FormationVolatilityAtrMultiplier));
    }

    [Fact]
    public void ValidateVolatility_MultipleLegsSomeBelow_ReturnsFalse()
    {
        // Two legs: first moves significantly, second barely moves
        var candles = new List<CandleData>
        {
            Candle(100, 102, 98,  100, 0),
            Candle(102, 104, 100, 103, 1),
            Candle(104, 106, 102, 105, 2),
            Candle(106, 108, 104, 110, 3), // End of leg1: move = 10
            Candle(110, 112, 108, 110.2m, 4), // Leg2 start
            Candle(110, 112, 108, 110.3m, 5), // Tiny move = 0.1
        };

        var legs = new List<(int, int)> { (0, 3), (3, 5) };
        Assert.False(PatternFormationValidator.ValidateVolatility(
            legs, candles, ChartConstants.FormationVolatilityAtrMultiplier));
    }

    [Fact]
    public void ValidateVolatility_NullLegs_ReturnsFalse()
    {
        Assert.False(PatternFormationValidator.ValidateVolatility(null!, null!, 0.5));
    }

    [Fact]
    public void ValidateVolatility_EmptyLegs_ReturnsFalse()
    {
        var candles = new List<CandleData> { Candle(100, 101, 99, 100) };
        Assert.False(PatternFormationValidator.ValidateVolatility(
            new List<(int, int)>(), candles, 0.5));
    }

    [Fact]
    public void ValidateVolatility_InvalidIndices_ReturnsFalse()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 101, 99, 100, 0),
            Candle(100, 101, 99, 100, 1),
        };

        // startIndex >= endIndex
        var legs = new List<(int, int)> { (1, 0) };
        Assert.False(PatternFormationValidator.ValidateVolatility(legs, candles, 0.5));
    }

    // ──────────────────────────────────────────────────────
    // 3. ValidateTimeSymmetry Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateTimeSymmetry_ExtremeRatio_ReturnsFalse()
    {
        // Leg durations: 2, 50 → ratio = 25 > 5.0
        var durations = new List<int> { 2, 50 };
        Assert.False(PatternFormationValidator.ValidateTimeSymmetry(
            durations, ChartConstants.FormationMaxTimeRatio));
    }

    [Fact]
    public void ValidateTimeSymmetry_BalancedRatio_ReturnsTrue()
    {
        // Leg durations: 5, 10, 8 → ratio = 10/5 = 2.0 <= 5.0
        var durations = new List<int> { 5, 10, 8 };
        Assert.True(PatternFormationValidator.ValidateTimeSymmetry(
            durations, ChartConstants.FormationMaxTimeRatio));
    }

    [Fact]
    public void ValidateTimeSymmetry_ExactBoundary_ReturnsTrue()
    {
        // Leg durations: 2, 10 → ratio = 5.0 == 5.0
        var durations = new List<int> { 2, 10 };
        Assert.True(PatternFormationValidator.ValidateTimeSymmetry(
            durations, ChartConstants.FormationMaxTimeRatio));
    }

    [Fact]
    public void ValidateTimeSymmetry_SingleLeg_ReturnsTrue()
    {
        // Only one leg — no symmetry to validate
        var durations = new List<int> { 10 };
        Assert.True(PatternFormationValidator.ValidateTimeSymmetry(durations, 5.0));
    }

    [Fact]
    public void ValidateTimeSymmetry_ZeroDuration_ReturnsFalse()
    {
        var durations = new List<int> { 0, 10 };
        Assert.False(PatternFormationValidator.ValidateTimeSymmetry(durations, 5.0));
    }

    [Fact]
    public void ValidateTimeSymmetry_NullInput_ReturnsTrue()
    {
        Assert.True(PatternFormationValidator.ValidateTimeSymmetry(null!, 5.0));
    }

    // ──────────────────────────────────────────────────────
    // 4. ComputeLocalATR Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeLocalATR_NormalCandles_ReturnsPositive()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 105, 95, 100, 0), // TR = 10
            Candle(100, 106, 94, 101, 1), // TR = max(12, 6, 6) = 12
            Candle(101, 104, 97, 102, 2), // TR = max(7, 3, 4) = 7
        };

        double atr = PatternFormationValidator.ComputeLocalATR(candles, 0, 2);
        Assert.True(atr > 0, $"ATR should be positive, got {atr}");
    }

    [Fact]
    public void ComputeLocalATR_FlatCandles_ReturnsZero()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 100, 100, 100, 0),
            Candle(100, 100, 100, 100, 1),
            Candle(100, 100, 100, 100, 2),
        };

        double atr = PatternFormationValidator.ComputeLocalATR(candles, 0, 2);
        Assert.Equal(0, atr);
    }

    [Fact]
    public void ComputeLocalATR_SingleCandle_ReturnsHLRange()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 105, 95, 100, 0), // H-L = 10
        };

        double atr = PatternFormationValidator.ComputeLocalATR(candles, 0, 0);
        Assert.Equal(10.0, atr, 2);
    }
}
