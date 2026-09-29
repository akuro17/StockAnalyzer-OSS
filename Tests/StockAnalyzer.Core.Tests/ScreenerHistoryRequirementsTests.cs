using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerHistoryRequirementsTests
{
    [Fact]
    public void GetRequiredCandleCount_ForStandardIndicator_ReturnsOptimizedDefault()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 20 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        Assert.Equal(500, count);
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ForAnchoredVWAP_ReturnsZeroForFullHistory()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.AnchoredVWAP,
                Parameters = new Dictionary<string, object> { { "AnchorIndex", 0 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        Assert.Equal(0, count);
        Assert.True(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }

    [Fact]
    public void GetRequiredCandleCount_ForLargePeriodIndicator_ReturnsCalculatedWarmupCount()
    {
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 250 } }
            }
        };

        int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);

        // 250 * 3 + 0 + 100 = 850
        Assert.Equal(850, count);
        Assert.False(ScreenerHistoryRequirements.RequiresFullHistory(entry));
    }
}
