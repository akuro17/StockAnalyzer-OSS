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
        Assert.Contains("SMA(20)", displayName);
        Assert.Contains("[Day]", displayName);
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
        Assert.Contains("SMA(20)", displayName);
        Assert.Contains(">", displayName);
        Assert.Contains("EMA(50)", displayName);
        Assert.Contains("[Week, Off:1]", displayName);
    }
}
