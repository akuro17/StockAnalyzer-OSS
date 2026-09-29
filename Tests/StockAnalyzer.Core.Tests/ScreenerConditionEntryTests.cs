using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerConditionEntryTests
{
    [Fact]
    public void DisplayName_ReturnsFormattedString_ForNumericComparison()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } },
                TimeFrame = TimeFrame.D1,
                Offset = 0
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m
        };

        string displayName = entry.DisplayName;
        Assert.Contains("SMA", displayName);
        Assert.Contains(">", displayName);
        Assert.EndsWith("50", displayName);
    }

    [Fact]
    public void DisplayName_ReturnsFormattedString_ForIndicatorComparison()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } },
                TimeFrame = TimeFrame.D1,
                Offset = 0
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.EMA,
                Parameters = new Dictionary<string, object> { { "Period", 50 } },
                TimeFrame = TimeFrame.W1,
                Offset = 1
            }
        };

        string displayName = entry.DisplayName;
        Assert.Contains("SMA", displayName);
        Assert.Contains(">", displayName);
        Assert.Contains("EMA", displayName);
    }

    [Fact]
    public void IsMet_EvaluatesTechnicalIndicatorsCorrectly()
    {
        var candles = new List<CandleData>();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 30; i++)
        {
            candles.Add(new CandleData(now.AddDays(i), 100m + i, 105m + i, 95m + i, 100m + i, 1000));
        }

        // Test SMA(20) > 100
        var smaEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } }
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m
        };

        Assert.True(smaEntry.IsMet(candles));

        // Test RSI(14) > 90
        var rsiEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.RSI,
                Parameters = new Dictionary<string, object> { { "Period", 14 } }
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 90m
        };

        Assert.True(rsiEntry.IsMet(candles));

        // Test BollingerBands (BB) via IndicatorFactory
        var bbEntry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.BB,
                OutputName = "Main"
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m
        };

        Assert.True(bbEntry.IsMet(candles));
    }

    [Fact]
    public void FormattedParameters_OmitsBooleanFlags()
    {
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.RSI,
            Parameters = new Dictionary<string, object>
            {
                { "Period", 14 },
                { "IsSmoothed", false },
                { "UseFilter", true }
            }
        };

        Assert.Equal("(14)", config.FormattedParameters);
        Assert.Equal("RSI(14)", config.GetColumnHeaderTitle());
    }
}

