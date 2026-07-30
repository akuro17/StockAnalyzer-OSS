using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class MarketStructureDetectorTests
{
    // Use a smaller threshold (3%) to ensure test data generates enough pivots
    private const decimal TestThreshold = 3.0m;

    #region Test Data Helpers

    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    /// <summary>
    /// Creates a clear uptrend with dramatic swings (>3% reversal each time).
    /// Pattern: 100→120, dip→112, rally→135, dip→125, rally→150
    /// This produces HH+HL structure = BullishBOS.
    /// </summary>
    private static List<CandleData> CreateUptrendCandles()
    {
        return new List<CandleData>
        {
            // Wave 1 up: 100 → 120
            Candle(100, 102, 98, 101, 0),
            Candle(102, 108, 101, 107, 1),
            Candle(107, 115, 106, 114, 2),
            Candle(114, 120, 113, 119, 3),
            // Pullback 1: 120 → 112 (6.7% dip)
            Candle(118, 119, 114, 115, 4),
            Candle(115, 116, 112, 113, 5),
            // Wave 2 up: 112 → 135 (HH > 120)
            Candle(114, 122, 113, 121, 6),
            Candle(121, 130, 120, 129, 7),
            Candle(129, 135, 128, 134, 8),
            // Pullback 2: 135 → 125 (7.4% dip, HL > 112)
            Candle(133, 134, 128, 129, 9),
            Candle(128, 129, 125, 126, 10),
            // Wave 3 up: 125 → 150 (HH > 135)
            Candle(127, 138, 126, 137, 11),
            Candle(137, 145, 136, 144, 12),
            Candle(144, 150, 143, 149, 13),
        };
    }

    /// <summary>
    /// Creates a clear downtrend with dramatic swings.
    /// Pattern: 200→175, bounce→188, drop→160, bounce→172, drop→145
    /// This produces LL+LH structure = BearishBOS.
    /// </summary>
    private static List<CandleData> CreateDowntrendCandles()
    {
        return new List<CandleData>
        {
            // Wave 1 down: 200 → 175
            Candle(200, 202, 198, 199, 0),
            Candle(198, 200, 190, 191, 1),
            Candle(190, 192, 180, 181, 2),
            Candle(180, 182, 175, 176, 3),
            // Bounce 1: 175 → 188 (7.4% bounce, LH < 200)
            Candle(177, 184, 176, 183, 4),
            Candle(183, 188, 182, 187, 5),
            // Wave 2 down: 188 → 160 (LL < 175)
            Candle(186, 187, 178, 179, 6),
            Candle(178, 180, 168, 169, 7),
            Candle(168, 170, 160, 161, 8),
            // Bounce 2: 160 → 172 (7.5% bounce, LH < 188)
            Candle(162, 168, 161, 167, 9),
            Candle(167, 172, 166, 171, 10),
            // Wave 3 down: 172 → 145 (LL < 160)
            Candle(170, 171, 162, 163, 11),
            Candle(162, 164, 152, 153, 12),
            Candle(152, 154, 145, 146, 13),
        };
    }

    /// <summary>
    /// Creates a transition from uptrend to downtrend (Bearish CHoCH).
    /// Uptrend: 100→120, dip→112, rally→135 (BullishBOS)
    /// Then fails to make HH and breaks HL → BearishCHoCH
    /// </summary>
    private static List<CandleData> CreateBearishReversalCandles()
    {
        return new List<CandleData>
        {
            // Swing 1 Up (100 -> 110)
            Candle(100, 105, 100, 105, 0),
            Candle(105, 110, 105, 110, 1), // SH1 = 110
            // Swing 1 Down (110 -> 105)
            Candle(110, 110, 107, 107, 2),
            Candle(107, 107, 105, 105, 3), // SL1 = 105
            // Swing 2 Up (105 -> 120)
            Candle(105, 112, 105, 112, 4),
            Candle(112, 120, 112, 120, 5), // SH2 = 120
            // Swing 2 Down (120 -> 114)
            Candle(120, 120, 117, 117, 6),
            Candle(117, 117, 114, 114, 7), // SL2 = 114
            // Swing 3 Up (weak to 119 - enough to be > 3% from 114)
            Candle(114, 119, 114, 119, 8), // SH3 = 119
            // Swing 3 Down (reversal breaking SL2=114)
            Candle(119, 119, 110, 110, 9), // SL3 = 110 -> BearishCHoCH
        };
    }

    /// <summary>
    /// Creates a transition from downtrend to uptrend (Bullish CHoCH).
    /// </summary>
    private static List<CandleData> CreateBullishReversalCandles()
    {
        return new List<CandleData>
        {
            // Swing 1 Down (200 -> 180)
            Candle(200, 201, 199, 200, 0),
            Candle(190, 191, 189, 190, 1),
            Candle(180, 181, 179, 180, 2), // SL1 = 179 (idx 2)
            // Swing 1 Up (180 -> 195)
            Candle(185, 186, 184, 185, 3),
            Candle(190, 191, 189, 190, 4),
            Candle(195, 196, 194, 195, 5), // SH1 = 196 (idx 5)
            // Swing 2 Down (195 -> 170)
            Candle(185, 186, 184, 185, 6),
            Candle(175, 176, 174, 175, 7),
            Candle(170, 171, 169, 170, 8), // SL2 = 169 (idx 8)
            // Swing 2 Up (170 -> 185)
            Candle(175, 176, 174, 175, 9),
            Candle(180, 181, 179, 180, 10),
            Candle(185, 186, 184, 185, 11), // SH2 = 186 (idx 11)
            // Swing 3 Down (weak to 180)
            Candle(180, 181, 179, 180, 12), // SL3 = 179 (idx 12)
            // Swing 3 Up (reversal breaking SH2=186 to 195)
            Candle(185, 186, 184, 185, 13), 
            Candle(195, 196, 194, 195, 14), // SH3 = 196 -> BullishCHoCH!
        };
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Detect_NullInput_ReturnsEmpty()
    {
        var result = MarketStructureDetector.Detect(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_EmptyCandles_ReturnsEmpty()
    {
        var result = MarketStructureDetector.Detect(new List<CandleData>());
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_TooFewCandles_ReturnsEmpty()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 105, 95, 100, 0),
            Candle(101, 106, 96, 101, 1),
            Candle(102, 107, 97, 102, 2),
        };
        var result = MarketStructureDetector.Detect(candles);
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_FlatPrices_ReturnsEmpty()
    {
        var candles = Enumerable.Range(0, 20)
            .Select(i => Candle(100, 100.5m, 99.5m, 100, i))
            .ToList();
        var result = MarketStructureDetector.Detect(candles);
        Assert.Empty(result);
    }

    #endregion

    #region Pivot Extraction

    [Fact]
    public void ExtractPivots_Uptrend_ProducesAlternatingHighLow()
    {
        var candles = CreateUptrendCandles();
        var pivots = MarketStructureDetector.ExtractPivots(candles, TestThreshold);

        Assert.True(pivots.Count >= 4,
            $"Expected at least 4 pivots, got {pivots.Count}: [{string.Join(", ", pivots.Select(p => p.ToString()))}]");

        for (int i = 1; i < pivots.Count; i++)
        {
            Assert.NotEqual(pivots[i].IsHigh, pivots[i - 1].IsHigh);
        }
    }

    [Fact]
    public void ExtractPivots_Downtrend_ProducesAlternatingHighLow()
    {
        var candles = CreateDowntrendCandles();
        var pivots = MarketStructureDetector.ExtractPivots(candles, TestThreshold);

        Assert.True(pivots.Count >= 4,
            $"Expected at least 4 pivots, got {pivots.Count}: [{string.Join(", ", pivots.Select(p => p.ToString()))}]");

        for (int i = 1; i < pivots.Count; i++)
        {
            Assert.NotEqual(pivots[i].IsHigh, pivots[i - 1].IsHigh);
        }
    }

    #endregion

    #region BOS Detection

    [Fact]
    public void Detect_Uptrend_ContainsBullishBOS()
    {
        var candles = CreateUptrendCandles();
        var shifts = MarketStructureDetector.Detect(candles, TestThreshold);

        Assert.True(shifts.Count > 0,
            $"Expected at least one structure shift in uptrend data. " +
            $"Pivots: [{string.Join(", ", MarketStructureDetector.ExtractPivots(candles, TestThreshold).Select(p => p.ToString()))}]");

        var bullishBos = shifts.Where(s => s.Type == MarketStructureType.BullishBOS).ToList();
        Assert.True(bullishBos.Count > 0,
            $"Expected BullishBOS in uptrend. Got: [{string.Join(", ", shifts.Select(s => s.ToString()))}]");
    }

    [Fact]
    public void Detect_Downtrend_ContainsBearishBOS()
    {
        var candles = CreateDowntrendCandles();
        var shifts = MarketStructureDetector.Detect(candles, TestThreshold);

        Assert.True(shifts.Count > 0,
            $"Expected at least one structure shift in downtrend data. " +
            $"Pivots: [{string.Join(", ", MarketStructureDetector.ExtractPivots(candles, TestThreshold).Select(p => p.ToString()))}]");

        var bearishBos = shifts.Where(s => s.Type == MarketStructureType.BearishBOS).ToList();
        Assert.True(bearishBos.Count > 0,
            $"Expected BearishBOS in downtrend. Got: [{string.Join(", ", shifts.Select(s => s.ToString()))}]");
    }

    #endregion

    #region CHoCH Detection

    [Fact]
    public void Detect_BearishReversal_ContainsBearishCHoCH()
    {
        var candles = CreateBearishReversalCandles();
        var shifts = MarketStructureDetector.Detect(candles, TestThreshold);

        Assert.True(shifts.Count > 0,
            $"Expected structure shifts. " +
            $"Pivots: [{string.Join(", ", MarketStructureDetector.ExtractPivots(candles, TestThreshold).Select(p => p.ToString()))}]");

        var chochShifts = shifts.Where(s => s.Type == MarketStructureType.BearishCHoCH).ToList();
        Assert.True(chochShifts.Count > 0,
            $"Expected BearishCHoCH. Got: [{string.Join(", ", shifts.Select(s => s.ToString()))}]");
    }

    [Fact]
    public void Detect_BullishReversal_ContainsBullishCHoCH()
    {
        var candles = CreateBullishReversalCandles();
        var shifts = MarketStructureDetector.Detect(candles, TestThreshold);

        Assert.True(shifts.Count > 0,
            $"Expected structure shifts. " +
            $"Pivots: [{string.Join(", ", MarketStructureDetector.ExtractPivots(candles, TestThreshold).Select(p => p.ToString()))}]");

        var chochShifts = shifts.Where(s => s.Type == MarketStructureType.BullishCHoCH).ToList();
        Assert.True(chochShifts.Count > 0,
            $"Expected BullishCHoCH. Got: [{string.Join(", ", shifts.Select(s => s.ToString()))}]");
    }

    #endregion

    #region DetectLatest

    [Fact]
    public void DetectLatest_Uptrend_ReturnsBullish()
    {
        var candles = CreateUptrendCandles();
        var latest = MarketStructureDetector.DetectLatest(candles, TestThreshold);

        Assert.NotNull(latest);
        Assert.Equal(MarketStructureType.BullishBOS, latest.Type);
    }

    [Fact]
    public void DetectLatest_Downtrend_ReturnsBearish()
    {
        var candles = CreateDowntrendCandles();
        var latest = MarketStructureDetector.DetectLatest(candles, TestThreshold);

        Assert.NotNull(latest);
        Assert.Equal(MarketStructureType.BearishBOS, latest.Type);
    }

    [Fact]
    public void DetectLatest_TooFewData_ReturnsNull()
    {
        var candles = new List<CandleData>
        {
            Candle(100, 105, 95, 100, 0),
            Candle(101, 106, 96, 101, 1),
        };
        var latest = MarketStructureDetector.DetectLatest(candles);
        Assert.Null(latest);
    }

    #endregion

    #region Output Validation

    [Fact]
    public void MarketStructureShift_ToString_ContainsRelevantInfo()
    {
        var shift = new MarketStructureShift(
            MarketStructureType.BullishBOS,
            index: 10,
            time: System.DateTime.Today,
            price: 125.0m,
            previousPivotHigh: 120.0m,
            previousPivotHighIndex: 5,
            previousPivotHighTime: System.DateTime.Today.AddDays(-5),
            previousPivotLow: 110.0m,
            previousPivotLowIndex: 2,
            previousPivotLowTime: System.DateTime.Today.AddDays(-8));

        var str = shift.ToString();
        Assert.Contains("BullishBOS", str);
        Assert.Contains("10", str);
        Assert.Contains("125", str);
    }

    [Fact]
    public void MarketStructureShift_Properties_AreCorrect()
    {
        var shift = new MarketStructureShift(
            MarketStructureType.BearishCHoCH,
            index: 15,
            time: System.DateTime.Today,
            price: 95.0m,
            previousPivotHigh: 120.0m,
            previousPivotHighIndex: 8,
            previousPivotHighTime: System.DateTime.Today.AddDays(-7),
            previousPivotLow: 100.0m,
            previousPivotLowIndex: 12,
            previousPivotLowTime: System.DateTime.Today.AddDays(-3));

        Assert.Equal(MarketStructureType.BearishCHoCH, shift.Type);
        Assert.Equal(15, shift.Index);
        Assert.Equal(95.0m, shift.Price);
        Assert.Equal(120.0m, shift.PreviousPivotHigh);
        Assert.Equal(8, shift.PreviousPivotHighIndex);
        Assert.Equal(100.0m, shift.PreviousPivotLow);
        Assert.Equal(12, shift.PreviousPivotLowIndex);
    }

    #endregion
}
