using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// G-1 / G1-T4b: the Screener tab's registration dialog offers the catalog of the Screener (<see cref="IScreenerCatalogProvider.GetCatalogItemsForScreener"/>): the indicators that
/// compare two symbols and that the Backtest refuses are in the group "Math" next to Correlation. The Backtest keeps its own catalog.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class IndicatorRegistrationScreenerCatalogTests
{
    private static IndicatorType[] ScreenerOnlyTypes()
        => IndicatorFactory.Default.GetRegisteredTypes()
            .Where(type => type != IndicatorType.Price && BacktestIndicatorEligibility.IsTypeBlocked(type) && !ScreenerIndicatorEligibility.IsTypeBlocked(type))
            .ToArray();

    private static IndicatorRegistrationViewModel Create(IScreenerCatalogProvider? catalogProvider = null)
        => new(IndicatorFactory.Default, catalogProvider);

    private static HashSet<IndicatorType> OfferedIn(IndicatorRegistrationViewModel vm, string groupName)
    {
        vm.SelectedGroupItem = vm.NavGroups.First(item => !item.IsHeader && !item.IsDivider && item.Group is { } group && group.Name == groupName);
        return vm.FilteredIndicators
            .Where(item => item.CategoryType == ScreenerItemCategoryType.Indicator && item.IndicatorType is not null)
            .Select(item => item.IndicatorType!.Value)
            .ToHashSet();
    }

    [Fact]
    public void TheMathGroupOffersCorrelationAndTheSevenOtherTwoSymbolIndicators()
    {
        IndicatorRegistrationViewModel vm = Create();

        HashSet<IndicatorType> math = OfferedIn(vm, "Math");

        Assert.Contains(IndicatorType.Correlation, math);
        Assert.Equal(8, ScreenerOnlyTypes().Length);
        Assert.All(ScreenerOnlyTypes(), type => Assert.Contains(type, math));
    }

    [Fact]
    public void NoOtherGroupOffersTheSevenTypes()
    {
        IndicatorRegistrationViewModel vm = Create();

        foreach (string group in new[] { "Trend", "Oscillator", "Volume", "Volatility", "Chart" })
        {
            HashSet<IndicatorType> offered = OfferedIn(vm, group);
            Assert.All(ScreenerOnlyTypes(), type => Assert.DoesNotContain(type, offered));
        }
    }

    [Fact]
    public void WhenTheTwoSymbolIndicatorsOfTheScreenerAreNotOffered_TheMathGroupKeepsItsOlderList()
    {
        IndicatorRegistrationViewModel vm = new(IndicatorFactory.Default, offersTwoSymbolIndicatorsOfTheScreener: false);

        HashSet<IndicatorType> math = OfferedIn(vm, "Math");

        Assert.Contains(IndicatorType.Correlation, math);
        Assert.All(ScreenerOnlyTypes(), type => Assert.DoesNotContain(type, math));
    }

    [Fact]
    public void TheTickerNotesDialogDoesNotOfferTheTypesItsEvaluationCannotCompute()
    {
        // The conditions of a note are evaluated without the bars of a comparison symbol (SignalEvaluationEngine): a type that needs one would never fire.
        var notes = new StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTickerNotesDialogViewModel("AAPL");

        HashSet<IndicatorType> math = OfferedIn(notes.IndicatorRegistrationViewModel, "Math");

        Assert.Equal(8, ScreenerOnlyTypes().Length);
        Assert.All(ScreenerOnlyTypes(), type => Assert.DoesNotContain(type, math));
        Assert.Contains(IndicatorType.Correlation, math);
    }

    [Fact]
    public void TheDialogAsksTheScreenerCatalog_NotTheOldOne()
    {
        var provider = new RecordingProvider(new ScreenerCatalogProvider());

        _ = Create(provider);

        Assert.True(provider.ScreenerCalls > 0);
        Assert.Equal(0, provider.OldCalls);
    }

    private sealed class RecordingProvider : IScreenerCatalogProvider
    {
        private readonly ScreenerCatalogProvider _inner;

        public RecordingProvider(ScreenerCatalogProvider inner) => _inner = inner;

        public int OldCalls { get; private set; }

        public int ScreenerCalls { get; private set; }

        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null)
        {
            OldCalls++;
            return _inner.GetCatalogItems(indicatorFactory);
        }

        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItemsForScreener(IIndicatorFactory? indicatorFactory = null)
        {
            ScreenerCalls++;
            return _inner.GetCatalogItemsForScreener(indicatorFactory);
        }

        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => _inner.GetDefaultSettings(type, indicatorFactory);

        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => _inner.GetOutputSeriesNames(type, indicatorFactory);
    }
}
