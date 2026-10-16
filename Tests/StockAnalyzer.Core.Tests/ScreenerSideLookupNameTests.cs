using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The name that selects a metric (Column) or a pattern (Criteria) is defined once, in <see cref="ScreenerIndicatorSideConfig.ResolveLookupName"/>;
/// the evaluation, the history sizing and the Screener result columns all read it.
/// </summary>
public class ScreenerSideLookupNameTests
{
    private static ScreenerIndicatorSideConfig Side(string custom, string output, IndicatorType type = IndicatorType.SMA,
        ScreenerItemCategoryType category = ScreenerItemCategoryType.Column) => new()
    {
        CustomDisplayName = custom,
        OutputName = output,
        IndicatorType = type,
        CategoryType = category
    };

    [Theory]
    [InlineData("P/B (Live)", "PBR", "P/B (Live)")]      // the catalog description wins
    [InlineData("", "Trailing PE", "Trailing PE")]       // then the short name
    [InlineData("  ", "Trailing PE", "Trailing PE")]     // blank is not a name
    [InlineData("SMA", "Trailing PE", "Trailing PE")]    // the default of an unnamed side is not a name
    [InlineData("Main", "Trailing PE", "Trailing PE")]
    [InlineData("", "Main", null)]                       // nothing named, and SMA is the default type
    [InlineData("", "SMA", null)]
    public void ResolveLookupName_FollowsTheOrder_CustomDisplayName_OutputName_DisplayName(string custom, string output, string? expected)
    {
        Assert.Equal(expected, Side(custom, output).ResolveLookupName());
    }

    [Fact]
    public void ResolveLookupName_FallsBackToTheDisplayNameOfANonDefaultIndicator()
    {
        Assert.Equal("RSI", Side("", "Main", IndicatorType.RSI).ResolveLookupName());
    }

    // The three places that used to restate the expression: the Column extraction (fallback DisplayName), the Criteria extraction (fallback "")
    // and the Screener result column (fallback DisplayName). The single definition must give each of them the same answer as before.
    [Fact]
    public void ResolveLookupName_GivesWhatTheThreeRestatementsGave_ForEveryCombination()
    {
        string[] names = { "", " ", "SMA", "Main", "Granville's Law Buy Signal", "Trailing PE" };
        foreach (string custom in names)
        foreach (string output in names)
        foreach (var type in new[] { IndicatorType.SMA, IndicatorType.RSI })
        foreach (var category in new[] { ScreenerItemCategoryType.Indicator, ScreenerItemCategoryType.Column, ScreenerItemCategoryType.Criteria })
        {
            var side = Side(custom, output, type, category);
            string? former = new[] { side.CustomDisplayName, side.OutputName, side.DisplayName }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main");

            Assert.Equal(former ?? "", side.ResolveLookupName() ?? "");
            Assert.Equal(former ?? side.DisplayName, side.ResolveLookupName() ?? side.DisplayName);
        }
    }
}
