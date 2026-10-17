using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ScreenerIndicatorSideConfig.ResolveCriteriaName"/> (Phase A2).
/// Verifies candidate priority order: OutputName, CustomDisplayName, DisplayName.
/// </summary>
public class ScreenerCriteriaNameTests
{
    private static ScreenerIndicatorSideConfig Side(
        string output,
        string custom,
        IndicatorType type = IndicatorType.SMA,
        ScreenerItemCategoryType category = ScreenerItemCategoryType.Criteria) => new()
    {
        OutputName = output,
        CustomDisplayName = custom,
        IndicatorType = type,
        CategoryType = category
    };

    [Theory]
    [InlineData("Morning Star", "3-Candle Bullish Reversal", "Morning Star")]
    [InlineData("Main", "Mat Hold", "Mat Hold")]
    [InlineData("SMA", "SMA", null)]
    [InlineData("  ", "", null)]
    public void ResolveCriteriaName_FollowsPriorityOrder(string output, string custom, string? expected)
    {
        Assert.Equal(expected, Side(output, custom).ResolveCriteriaName());
    }

    [Fact]
    public void ResolveCriteriaName_FallsBackToDisplayName_WhenOutputAndCustomAreEmpty()
    {
        // When output and custom are empty, falls back to DisplayName (e.g. non-default IndicatorType like RSI)
        var side = Side("", "", IndicatorType.RSI);
        Assert.Equal("RSI", side.ResolveCriteriaName());
    }

    [Fact]
    public void ResolveCriteriaName_IgnoresDefaultSMAAndMain()
    {
        var side = Side("Main", "SMA", IndicatorType.SMA);
        Assert.Null(side.ResolveCriteriaName());
    }
}
