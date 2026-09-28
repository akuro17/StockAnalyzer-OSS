using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Task 17 acceptance tests for BacktestIndicatorSelectionViewModel (spec §5.3): unsupported-indicator rejection and non-mutation of the shared Screener catalog.</summary>
public class BacktestIndicatorSelectionTests
{
    [Fact]
    public void UnsupportedIndicator_Rejected_NotSilentlyRemoved()
    {
        var catalogProvider = new FakeScreenerCatalogProvider(new[]
        {
            new ScreenerCatalogItem { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
        });
        // Factory that does not register SMA - the objective, mechanical "unsupported" check.
        var factory = new FakeIndicatorFactory(Array.Empty<IndicatorType>());
        var vm = new BacktestIndicatorSelectionViewModel(catalogProvider, NullLocalizationService.Instance, factory);

        vm.SelectedCatalogItem = vm.FilteredItems.Single();
        vm.AddSelectedIndicatorCommand.Execute(null);

        Assert.Empty(vm.AddedIndicators);
        Assert.Equal("Backtest_UnsupportedIndicatorMessage", vm.UnsupportedWarningMessage);
    }

    [Fact]
    public void ScreenerCatalog_Unchanged_AfterBacktestInteraction()
    {
        var sourceItems = new List<ScreenerCatalogItem>
        {
            new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
            new() { CategoryType = ScreenerItemCategoryType.Column, GroupName = "Price", ShortName = "Close", DisplayName = "Close Price", ColumnMemberName = "Close" },
        };
        var snapshot = sourceItems
            .Select(i => (i.CategoryType, i.GroupName, i.ShortName, i.DisplayName, i.IndicatorType, i.ColumnMemberName))
            .ToList();
        var catalogProvider = new FakeScreenerCatalogProvider(sourceItems);

        var vm = new BacktestIndicatorSelectionViewModel(catalogProvider, NullLocalizationService.Instance);
        vm.SelectedGroupItem = vm.NavGroups.Single(g => g.IsStandardItem && g.DisplayName == "MA");
        vm.SearchText = "SMA";
        vm.SelectedCatalogItem = vm.FilteredItems.SingleOrDefault();
        Assert.NotNull(vm.SelectedCatalogItem);
        vm.AddSelectedIndicatorCommand.Execute(null);
        Assert.Single(vm.AddedIndicators);
        vm.RemoveIndicatorCommand.Execute(vm.AddedIndicators.Single());
        Assert.Empty(vm.AddedIndicators);

        // Only the constructor's own initial load should have touched the shared provider - no
        // interaction with the backtest tab silently reloads or otherwise churns the Screener catalog.
        Assert.Equal(1, catalogProvider.GetCatalogItemsCallCount);

        IReadOnlyList<ScreenerCatalogItem> after = catalogProvider.GetCatalogItems();
        Assert.Same(sourceItems, after);
        Assert.Equal(2, after.Count);
        var afterSnapshot = after
            .Select(i => (i.CategoryType, i.GroupName, i.ShortName, i.DisplayName, i.IndicatorType, i.ColumnMemberName))
            .ToList();
        Assert.Equal(snapshot, afterSnapshot);
    }
}
