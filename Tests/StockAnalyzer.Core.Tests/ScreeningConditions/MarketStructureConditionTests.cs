using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.ScreeningConditions;
using Xunit;

namespace StockAnalyzer.Core.Tests.ScreeningConditions;

public class MarketStructureConditionTests
{
    // Use 3% threshold for test data (same as MarketStructureDetectorTests)
    private const decimal TestThreshold = 3.0m;

    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    #region Test Data

    /// <summary>
    /// Clear uptrend: 100→120→112→135→125→150 (BullishBOS)
    /// </summary>
    private static List<CandleData> CreateUptrendCandles()
    {
        return new List<CandleData>
        {
            Candle(100, 102, 98, 101, 0),
            Candle(102, 108, 101, 107, 1),
            Candle(107, 115, 106, 114, 2),
            Candle(114, 120, 113, 119, 3),
            Candle(118, 119, 114, 115, 4),
            Candle(115, 116, 112, 113, 5),
            Candle(114, 122, 113, 121, 6),
            Candle(121, 130, 120, 129, 7),
            Candle(129, 135, 128, 134, 8),
            Candle(133, 134, 128, 129, 9),
            Candle(128, 129, 125, 126, 10),
            Candle(127, 138, 126, 137, 11),
            Candle(137, 145, 136, 144, 12),
            Candle(144, 150, 143, 149, 13),
        };
    }

    /// <summary>
    /// Clear downtrend: 200→175→188→160→172→145 (BearishBOS)
    /// </summary>
    private static List<CandleData> CreateDowntrendCandles()
    {
        return new List<CandleData>
        {
            Candle(200, 202, 198, 199, 0),
            Candle(198, 200, 190, 191, 1),
            Candle(190, 192, 180, 181, 2),
            Candle(180, 182, 175, 176, 3),
            Candle(177, 184, 176, 183, 4),
            Candle(183, 188, 182, 187, 5),
            Candle(186, 187, 178, 179, 6),
            Candle(178, 180, 168, 169, 7),
            Candle(168, 170, 160, 161, 8),
            Candle(162, 168, 161, 167, 9),
            Candle(167, 172, 166, 171, 10),
            Candle(170, 171, 162, 163, 11),
            Candle(162, 164, 152, 153, 12),
            Candle(152, 154, 145, 146, 13),
        };
    }

    /// <summary>
    /// Uptrend → Downtrend reversal (BearishCHoCH)
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
    /// Downtrend → Uptrend reversal (BullishCHoCH)
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

    #region BOS Screening

    [Fact]
    public void IsMet_BullishBOS_UptrendData_ReturnsTrue()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS, TestThreshold);
        Assert.True(condition.IsMet(CreateUptrendCandles()));
    }

    [Fact]
    public void IsMet_BullishBOS_DowntrendData_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS, TestThreshold);
        Assert.False(condition.IsMet(CreateDowntrendCandles()));
    }

    [Fact]
    public void IsMet_BearishBOS_DowntrendData_ReturnsTrue()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BearishBOS, TestThreshold);
        Assert.True(condition.IsMet(CreateDowntrendCandles()));
    }

    [Fact]
    public void IsMet_BearishBOS_UptrendData_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BearishBOS, TestThreshold);
        Assert.False(condition.IsMet(CreateUptrendCandles()));
    }

    #endregion

    #region CHoCH Screening

    [Fact]
    public void IsMet_BearishCHoCH_ReversalData_ReturnsTrue()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BearishCHoCH, TestThreshold);
        Assert.True(condition.IsMet(CreateBearishReversalCandles()));
    }

    [Fact]
    public void IsMet_BullishCHoCH_ReversalData_ReturnsTrue()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishCHoCH, TestThreshold);
        Assert.True(condition.IsMet(CreateBullishReversalCandles()));
    }

    [Fact]
    public void DebugCHoCH()
    {
        var candles = CreateBullishReversalCandles();
        var pivots = MarketStructureDetector.ExtractPivots(candles, TestThreshold);
        var shifts = MarketStructureDetector.Detect(candles, TestThreshold);
        var latest = MarketStructureDetector.DetectLatest(candles, TestThreshold);

        Xunit.Abstractions.ITestOutputHelper? output = null; 
        
        Assert.True(shifts.Count > 0, "No shifts detected. Pivots: " + string.Join(", ", pivots));
        Assert.Equal(MarketStructureType.BullishCHoCH, latest?.Type);
    }

    [Fact]
    public void IsMet_BullishCHoCH_UptrendData_ReturnsFalse()
    {
        // Uptrend should produce BullishBOS, not BullishCHoCH
        var condition = new MarketStructureCondition(MarketStructureType.BullishCHoCH, TestThreshold);
        Assert.False(condition.IsMet(CreateUptrendCandles()));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void IsMet_NullCandles_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS);
        Assert.False(condition.IsMet(null!));
    }

    [Fact]
    public void IsMet_EmptyCandles_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS);
        Assert.False(condition.IsMet(new List<CandleData>()));
    }

    [Fact]
    public void IsMet_TooFewCandles_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS);
        var candles = Enumerable.Range(0, 5)
            .Select(i => Candle(100 + i, 102 + i, 98 + i, 101 + i, i))
            .ToList();
        Assert.False(condition.IsMet(candles));
    }

    [Fact]
    public void IsMet_FlatPrices_ReturnsFalse()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS);
        var candles = Enumerable.Range(0, 20)
            .Select(i => Candle(100, 100.5m, 99.5m, 100, i))
            .ToList();
        Assert.False(condition.IsMet(candles));
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_IncludesTargetType()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BullishBOS);
        var str = condition.ToString();
        Assert.Contains("BullishBOS", str);
        Assert.Contains("Market Structure", str);
    }

    [Fact]
    public void ToString_BearishCHoCH()
    {
        var condition = new MarketStructureCondition(MarketStructureType.BearishCHoCH);
        Assert.Contains("BearishCHoCH", condition.ToString());
    }

    #endregion
}
