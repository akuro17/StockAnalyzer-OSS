#nullable enable
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ScatterMetricEligibilityTests
{
    private readonly ITestOutputHelper _output;

    public ScatterMetricEligibilityTests(ITestOutputHelper output) => _output = output;

    private static ScreenerCatalogItem[] Catalog() => new ScreenerCatalogProvider().GetCatalogItems(IndicatorFactory.Default).ToArray();

    [Fact]
    public void IsEligible_NullIsNotEligible()
    {
        Assert.False(ScatterMetricEligibility.IsEligible(null));
    }

    [Fact]
    public void IsEligible_NeverOffersACriteria()
    {
        var criteria = Catalog().Where(item => item.CategoryType == ScreenerItemCategoryType.Criteria).ToArray();

        Assert.NotEmpty(criteria);
        Assert.All(criteria, item => Assert.False(ScatterMetricEligibility.IsEligible(item), item.ShortName));
    }

    [Fact]
    public void IsEligible_NeverOffersATextColumn()
    {
        var textColumns = Catalog()
            .Where(item => item.CategoryType == ScreenerItemCategoryType.Column
                           && ScreenerTextFields.Names.Contains(item.ColumnMemberName ?? item.ShortName))
            .ToArray();

        Assert.NotEmpty(textColumns);
        Assert.All(textColumns, item => Assert.False(ScatterMetricEligibility.IsEligible(item), item.ShortName));
    }

    [Fact]
    public void IsEligible_OffersEveryIndicatorIncludingThePriceItems()
    {
        var indicators = Catalog().Where(item => item.CategoryType == ScreenerItemCategoryType.Indicator).ToArray();

        Assert.Contains(indicators, item => item.IndicatorType == IndicatorType.Price);
        Assert.All(indicators, item => Assert.True(ScatterMetricEligibility.IsEligible(item), item.ShortName));
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("High")]
    [InlineData("Low")]
    [InlineData("Close")]
    [InlineData("Volume")]
    [InlineData("Change")]
    [InlineData("ChangePercent")]
    public void AllowsTimeFrameChoice_IsTrueForAColumnThatNamesABarValue(string memberName)
    {
        Assert.True(ScatterMetricEligibility.AllowsTimeFrameChoice(ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn(memberName)).Source));
    }

    [Theory]
    [InlineData("MarketCap")]
    [InlineData("ReturnOnEquity")]
    public void AllowsTimeFrameChoice_IsFalseForAFundamentalColumn(string memberName)
    {
        Assert.False(ScatterMetricEligibility.AllowsTimeFrameChoice(ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn(memberName)).Source));
    }

    [Fact]
    public void AllowsTimeFrameChoice_IsTrueForAnIndicatorAndForAPriceSource()
    {
        Assert.True(ScatterMetricEligibility.AllowsTimeFrameChoice(ScatterMetricTestKit.Indicator(IndicatorType.RSI).Source));
        Assert.True(ScatterMetricEligibility.AllowsTimeFrameChoice(ScatterMetricTestKit.Indicator(IndicatorType.Price, output: "Close").Source));
    }

    [Fact]
    public void AllowsTimeFrameChoice_IsFalseForNullAndForACriteria()
    {
        var criteria = ScatterMetricTestKit.Indicator(IndicatorType.RSI).Source;
        criteria.CategoryType = ScreenerItemCategoryType.Criteria;

        Assert.False(ScatterMetricEligibility.AllowsTimeFrameChoice(null));
        Assert.False(ScatterMetricEligibility.AllowsTimeFrameChoice(criteria));
    }

    [Fact]
    public void EveryRegistrationGroupOffersAtLeastOneEligibleItem()
    {
        var items = Catalog().Where(ScatterMetricEligibility.IsEligible).ToArray();

        foreach (ScreenerIndicatorGroup group in ScreenerIndicatorGroup.GetDefaultGroups().Where(g => g.CategoryType is "Indicator" or "Column"))
        {
            int count = items.Count(item => string.Equals(item.GroupName, group.Name, System.StringComparison.OrdinalIgnoreCase));
            _output.WriteLine($"{group.CategoryType,-9} {group.Name,-17} {count,4}");
            Assert.True(count > 0, $"Group '{group.Name}' offers no eligible item.");
        }
    }
}
