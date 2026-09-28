using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class NestedIndicatorWarmupTests
{
    private static List<CoreCandleData> CreateTestCandles(int count, decimal basePrice = 100m)
    {
        var startDate = new DateTime(2025, 1, 1);
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal p = basePrice + (i * 0.5m);
            list.Add(new CoreCandleData(startDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }
        return list;
    }

    [Fact]
    public void CalculateSeries_SmaOverEma_OffsetsWarmupAndDoesNotSinkToZero()
    {
        // Arrange
        // Base indicator: EMA(5) -> first 4 values are null, 5th value is non-null (~102)
        var ema = new CoreEmaIndicator { Period = 5 };
        var candles = CreateTestCandles(20, basePrice: 100m);
        var emaResult = ema.Calculate(candles);

        Assert.Equal(20, emaResult.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(emaResult[i]);
        }
        Assert.NotNull(emaResult[4]);
        Assert.InRange(emaResult[4]!.Value, 90m, 110m);

        // Act
        // Outer indicator: SMA(3) over EMA(5) series
        var sma = new CoreSmaIndicator { Period = 3 };
        var smaResult = sma.CalculateSeries(emaResult.MainValues);

        // Assert
        Assert.Equal(20, smaResult.Count);

        // Indices 0, 1, 2, 3 are null due to EMA warmup.
        // Indices 4, 5 are null due to SMA requiring 3 consecutive valid EMA values (indices 4, 5, 6).
        // First valid SMA index should be index 6.
        for (int i = 0; i < 6; i++)
        {
            Assert.Null(smaResult[i]);
        }

        // First non-null SMA value must be close to ~102, NEVER falling down towards 0!
        Assert.NotNull(smaResult[6]);
        Assert.InRange(smaResult[6]!.Value, 95m, 115m);
        Assert.True(smaResult[6]!.Value > 50m, "First nested SMA value must not sink to near-zero!");
    }

    [Fact]
    public void CalculateSeries_SmaOverRsi_OffsetsWarmupAndDoesNotSinkToZero()
    {
        // Arrange
        // Base indicator: RSI(5) -> warmup requires 5 periods
        var rsi = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(25, basePrice: 100m);
        var rsiResult = rsi.Calculate(candles);

        Assert.Equal(25, rsiResult.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(rsiResult[i]);
        }
        Assert.NotNull(rsiResult[5]);
        Assert.InRange(rsiResult[5]!.Value, 0m, 100m);

        // Act
        // Outer indicator: SMA(3) over RSI(5) series
        var sma = new CoreSmaIndicator { Period = 3 };
        var smaResult = sma.CalculateSeries(rsiResult.MainValues);

        // Assert
        Assert.Equal(25, smaResult.Count);

        // Warmup indices must be null
        for (int i = 0; i < 7; i++)
        {
            Assert.Null(smaResult[i]);
        }

        // First valid SMA(RSI) value should reflect actual RSI average, not 0
        Assert.NotNull(smaResult[7]);
        Assert.InRange(smaResult[7]!.Value, 1m, 100m);
    }
}
