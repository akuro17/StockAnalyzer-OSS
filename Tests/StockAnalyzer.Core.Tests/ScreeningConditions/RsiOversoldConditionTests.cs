using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using Xunit;

namespace StockAnalyzer.Core.Tests.ScreeningConditions;

public class RsiOversoldConditionTests
{
    private static List<CandleData> CreateSequence(params decimal[] closes)
    {
        var list = new List<CandleData>(closes.Length);
        var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < closes.Length; i++)
        {
            decimal c = closes[i];
            list.Add(new CandleData(baseTime.AddDays(i), c, c, c, c, 1000));
        }
        return list;
    }

    [Theory]
    [InlineData(-5, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(14, 14)]
    [InlineData(250, 250)]
    [InlineData(300, 250)]
    public void Constructor_PeriodClamped(int inputPeriod, int expectedPeriod)
    {
        var condition = new RsiOversoldCondition(period: inputPeriod);
        Assert.Equal(expectedPeriod, condition.Period);
    }

    [Theory]
    [InlineData(-10.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(30.0, 30.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(150.0, 100.0)]
    public void Constructor_ThresholdClamped(double inputThreshold, double expectedThreshold)
    {
        var condition = new RsiOversoldCondition(threshold: (decimal)inputThreshold);
        Assert.Equal((decimal)expectedThreshold, condition.Threshold);
    }

    [Fact]
    public void IsMet_InsufficientCandles_ReturnsFalse()
    {
        var condition = new RsiOversoldCondition(period: 14);

        Assert.False(condition.IsMet(null!));

        var shortList = CreateSequence(100m, 101m, 102m);
        Assert.False(condition.IsMet(shortList));
    }

    [Fact]
    public void IsMet_FlatPrices_EvaluatesAtRsi50()
    {
        // 15 identical prices -> period = 14
        var closes = new decimal[15];
        Array.Fill(closes, 100m);
        var candles = CreateSequence(closes);

        var condThreshold60 = new RsiOversoldCondition(period: 14, threshold: 60m);
        Assert.True(condThreshold60.IsMet(candles));

        var condThreshold40 = new RsiOversoldCondition(period: 14, threshold: 40m);
        Assert.False(condThreshold40.IsMet(candles));
    }

    [Fact]
    public void IsMet_StrictlyDecliningPrices_EvaluatesAtRsiZero()
    {
        // 15 bars: 114 down to 100
        var closes = new decimal[15];
        for (int i = 0; i < 15; i++)
        {
            closes[i] = 114m - i;
        }
        var candles = CreateSequence(closes);

        var condition = new RsiOversoldCondition(period: 14, threshold: 30m);
        Assert.True(condition.IsMet(candles));
    }

    [Fact]
    public void IsMet_StrictlyRisingPrices_EvaluatesAtRsi100()
    {
        // 15 bars: 100 up to 114
        var closes = new decimal[15];
        for (int i = 0; i < 15; i++)
        {
            closes[i] = 100m + i;
        }
        var candles = CreateSequence(closes);

        var condition = new RsiOversoldCondition(period: 14, threshold: 30m);
        Assert.False(condition.IsMet(candles));
    }
}
