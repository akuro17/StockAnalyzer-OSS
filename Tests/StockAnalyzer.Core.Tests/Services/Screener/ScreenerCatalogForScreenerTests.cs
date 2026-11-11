using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// G-1 / G1-T4b: <see cref="IScreenerCatalogProvider.GetCatalogItemsForScreener"/> is the catalog of the Screener (the tab and the MCP screener): the one of
/// <see cref="IScreenerCatalogProvider.GetCatalogItems"/> plus the two-ticker indicators the Backtest refuses but the Screener evaluates
/// (<see cref="ScreenerIndicatorEligibility"/>). <c>GetCatalogItems</c>, which the Backtest selection and the Scatter registration use, is unchanged.
/// The types come from the registry and the two eligibility rules, not from a list in this file.
/// </summary>
public sealed class ScreenerCatalogForScreenerTests
{
    private static readonly IIndicatorFactory Factory = IndicatorFactory.Default;
    private static readonly ScreenerCatalogProvider Provider = new();

    /// <summary>The types the Screener offers but the Backtest does not: registered, blocked for the Backtest, not blocked for the Screener.</summary>
    private static IndicatorType[] ScreenerOnlyTypes()
        => Factory.GetRegisteredTypes()
            .Where(type => type != IndicatorType.Price && BacktestIndicatorEligibility.IsTypeBlocked(type) && !ScreenerIndicatorEligibility.IsTypeBlocked(type))
            .OrderBy(type => type.ToString(), StringComparer.Ordinal)
            .ToArray();

    private static string Key(ScreenerCatalogItem item)
        => $"{item.CategoryType}|{item.GroupName}|{item.IndicatorType}|{item.ShortName}|{item.DisplayName}|{item.ColumnMemberName}";

    [Fact]
    public void TheScreenerOnlyTypes_AreTheEightTwoTickerTypesTheBacktestRefuses()
    {
        IndicatorType[] types = ScreenerOnlyTypes();

        Assert.Equal(
            new[]
            {
                IndicatorType.CointegrationSpread, IndicatorType.CrossTickerDtw, IndicatorType.PairRatio, IndicatorType.PairSpread, IndicatorType.Ratiocator,
                IndicatorType.RollingAlpha, IndicatorType.RollingBeta, IndicatorType.RollingRSquared,
            }.OrderBy(type => type.ToString(), StringComparer.Ordinal),
            types);
        Assert.All(types, type => Assert.True(Factory.Create(type) is ICrossTickerIndicator, type.ToString()));
    }

    [Fact]
    public void GetCatalogItems_StillLeavesOutEveryTypeTheBacktestRefuses_Eight_Included()
    {
        var offered = Provider.GetCatalogItems(Factory).Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.IndicatorType != IndicatorType.Price)
            .Select(i => i.IndicatorType!.Value).ToHashSet();

        Assert.All(Factory.GetRegisteredTypes().Where(type => type != IndicatorType.Price), type =>
            Assert.Equal(!BacktestIndicatorEligibility.IsTypeBlocked(type), offered.Contains(type)));
        Assert.All(ScreenerOnlyTypes(), type => Assert.DoesNotContain(type, offered));
    }

    [Fact]
    public void GetCatalogItemsForScreener_IsTheOldCatalogPlusTheScreenerOnlyTypes_InGroupMath_AndNothingElseChanges()
    {
        IReadOnlyList<ScreenerCatalogItem> old = Provider.GetCatalogItems(Factory);
        IReadOnlyList<ScreenerCatalogItem> forScreener = Provider.GetCatalogItemsForScreener(Factory);
        IndicatorType[] added = ScreenerOnlyTypes();

        ScreenerCatalogItem[] extra = forScreener.Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.IndicatorType is { } t && added.Contains(t)).ToArray();
        Assert.Equal(added.Length, extra.Length);
        Assert.All(extra, item => Assert.Equal("Math", item.GroupName));
        Assert.All(extra, item => Assert.Equal(item.IndicatorType.ToString(), item.ShortName));

        // Without the added items the two lists are the same, item for item and in the same order.
        Assert.Equal(old.Select(Key), forScreener.Except(extra).Select(Key));
        Assert.Equal(old.Count + added.Length, forScreener.Count);
    }

    [Fact]
    public void TheAddedItems_AreSortedIntoTheirGroupLikeEveryOtherIndicator()
    {
        IReadOnlyList<ScreenerCatalogItem> forScreener = Provider.GetCatalogItemsForScreener(Factory);
        var math = forScreener.Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.GroupName == "Math").ToList();

        Assert.Equal(
            math.Select(i => i.DisplayName ?? i.ShortName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            math.Select(i => i.DisplayName ?? i.ShortName));
        Assert.True(math.Count > ScreenerOnlyTypes().Length);
    }

    [Fact]
    public void TheMathGroupNamesTheScreenerOnlyTypes_AndNoTypeIsInTwoIndicatorGroups()
    {
        List<ScreenerIndicatorGroup> groups = ScreenerIndicatorGroup.GetDefaultGroups().Where(g => g.CategoryType == "Indicator").ToList();
        ScreenerIndicatorGroup math = groups.Single(g => g.Name == "Math");

        Assert.All(ScreenerOnlyTypes(), type => Assert.Contains(type, math.IndicatorTypes));

        IndicatorType[] duplicated = groups.SelectMany(g => g.IndicatorTypes).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.Empty(duplicated.Where(type => ScreenerOnlyTypes().Contains(type)));
    }

    [Fact]
    public void ADefaultCatalogProviderThatKnowsOnlyTheOldMember_GivesTheScreenerTheSameItems()
    {
        IScreenerCatalogProvider legacy = new OldMemberOnlyProvider(Provider);

        Assert.Equal(legacy.GetCatalogItems(Factory).Select(Key), legacy.GetCatalogItemsForScreener(Factory).Select(Key));
    }

    [Fact]
    public void TheScatterRegistrationOffersNoneOfTheScreenerOnlyTypes()
    {
        // The Scatter registration reads GetCatalogItems and the Scatter rules keep the Backtest's eligibility.
        var offered = Provider.GetCatalogItems(Factory).Where(ScatterMetricEligibility.IsEligible)
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator).Select(i => i.IndicatorType!.Value).ToHashSet();

        Assert.All(ScreenerOnlyTypes(), type => Assert.DoesNotContain(type, offered));
    }

    [Fact]
    public void EveryItemOfTheScreenerCatalog_CanBeEvaluatedByTheScreener()
    {
        IEnumerable<IndicatorType> indicators = Provider.GetCatalogItemsForScreener(Factory)
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.IndicatorType is not null && i.IndicatorType != IndicatorType.Price)
            .Select(i => i.IndicatorType!.Value);

        Assert.All(indicators, type => Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(type), type.ToString()));
    }

    /// <summary>A provider written before the Screener member existed: it implements the old members only.</summary>
    private sealed class OldMemberOnlyProvider : IScreenerCatalogProvider
    {
        private readonly ScreenerCatalogProvider _inner;

        public OldMemberOnlyProvider(ScreenerCatalogProvider inner) => _inner = inner;

        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null) => _inner.GetCatalogItems(indicatorFactory);

        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => _inner.GetDefaultSettings(type, indicatorFactory);

        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => _inner.GetOutputSeriesNames(type, indicatorFactory);
    }
}
