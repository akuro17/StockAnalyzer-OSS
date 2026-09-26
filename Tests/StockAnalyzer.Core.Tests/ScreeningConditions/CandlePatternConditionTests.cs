using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using Xunit;

namespace StockAnalyzer.Core.Tests.ScreeningConditions;

public class CandlePatternConditionTests
{
    private static List<CandleData> CreateCandles(decimal open, decimal high, decimal low, decimal close, decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        
        // Add context candles to establish Average Body if requested
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
            {
                // Dummy candles summing up to exact avgBodyContext
                candles.Add(new CandleData(DateTime.Now.AddDays(-15 + i), 100m, 100m + avgBodyContext, 100m, 100m + avgBodyContext, 1000));
            }
        }

        // Add the target candle
        candles.Add(new CandleData(DateTime.Now, open, high, low, close, 1000));
        return candles;
    }

    [Fact]
    public void IsMet_WhenPatternMatches_ReturnsTrue()
    {
        // Arrange: Bullish Marubozu (Body=4, context avgBody=2)
        var candles = CreateCandles(100, 104, 100, 104, 2);
        var condition = new CandlePatternCondition(CandlePatternType.BullishMarubozu);

        // Act
        var result = condition.IsMet(candles);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsMet_WhenPatternDoesNotMatch_ReturnsFalse()
    {
        // Arrange: This is a Bearish Marubozu (104 -> 100), not Bullish
        var candles = CreateCandles(104, 104, 100, 100, 2);
        var condition = new CandlePatternCondition(CandlePatternType.BullishMarubozu);

        // Act
        var result = condition.IsMet(candles);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsMet_WhenCandlesEmpty_ReturnsFalse()
    {
        // Arrange
        var condition = new CandlePatternCondition(CandlePatternType.CrossDoji);

        // Act
        var result = condition.IsMet(new List<CandleData>());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsMet_WhenCandlesNull_ReturnsFalse()
    {
        // Arrange
        var condition = new CandlePatternCondition(CandlePatternType.BullishUmbrella);

        // Act
        var result = condition.IsMet(null!);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        // Arrange
        var condition = new CandlePatternCondition(CandlePatternType.DragonflyDoji);

        // Act
        var str = condition.ToString();

        // Assert
        Assert.Equal("Candle Pattern (DragonflyDoji)", str);
    }
}
