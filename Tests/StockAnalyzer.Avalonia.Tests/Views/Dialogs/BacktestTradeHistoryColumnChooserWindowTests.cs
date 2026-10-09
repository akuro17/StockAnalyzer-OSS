using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Interfaces;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Tests BacktestTradeHistoryColumnChooserWindow and BacktestTradeHistoryColumnChooserViewModel
/// for compliance with SA_UI_SUBWINDOW_STANDARD.md, boundary invariants, and category filtering.
/// </summary>
public class BacktestTradeHistoryColumnChooserWindowTests
{
    private static BacktestTradeHistoryColumnChooserWindow ShowWindow(BacktestTradeHistoryColumnChooserViewModel vm)
    {
        var window = new BacktestTradeHistoryColumnChooserWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Window_AdheresToSubWindowDecorationsAndResizeStandard()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.False(window.CanResize);
            Assert.Equal(700, window.Width);
            Assert.Equal(520, window.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollBars_ComplyWithSubWindowStandard()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            var listBoxes = window.GetVisualDescendants().OfType<ListBox>().ToList();
            Assert.True(listBoxes.Count >= 2, "Expected at least Category and Columns ListBoxes");

            foreach (var lb in listBoxes)
            {
                Assert.False(ScrollViewer.GetAllowAutoHide(lb), $"ListBox {lb.Name ?? "unnamed"} must set AllowAutoHide='False'");
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(lb));
                Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(lb));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Footer_AdheresToButtonStandard()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var okBtn = buttons.FirstOrDefault(b => b.IsDefault);
            Assert.NotNull(okBtn);

            var cancelBtn = buttons.FirstOrDefault(b => b.IsCancel);
            Assert.NotNull(cancelBtn);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The number of trade-history columns: the production list is the only owner, so the tests follow it instead of repeating a number.</summary>
    private static int KnownCount => BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Count;

    [Fact]
    public void ViewModel_InitializesWithAllKnownColumnsByDefault()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        Assert.Equal(KnownCount, vm.AllItems.Count);
        Assert.All(vm.AllItems, item => Assert.True(item.IsActive));
        Assert.Equal(KnownCount, vm.GetActiveColumnIds().Count);
    }

    [Fact]
    public void ViewModel_CategoryFiltering_ActiveVsAll()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel(new[] { "TradeId", "Side", "ClosedNet" });
        Assert.Equal(KnownCount, vm.AllItems.Count);

        // SelectedCategory defaults to Active: only 3 active items shown
        Assert.Equal(BacktestTradeColumnCategory.Active, vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredItems.Count);
        Assert.Contains(vm.FilteredItems, x => x.ColumnId == "TradeId");
        Assert.Contains(vm.FilteredItems, x => x.ColumnId == "Side");
        Assert.Contains(vm.FilteredItems, x => x.ColumnId == "ClosedNet");

        // Switch to All: every known item is shown
        vm.SelectedCategory = BacktestTradeColumnCategory.All;
        Assert.Equal(KnownCount, vm.FilteredItems.Count);
    }

    [Fact]
    public void ViewModel_SearchQuery_FiltersCorrectly()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        vm.SelectedCategory = BacktestTradeColumnCategory.All;

        vm.SearchQuery = "Price";
        var expected = BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Where(c => c.Id.Contains("Price", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToArray();
        Assert.Contains("EntryPrice", expected);
        Assert.Contains("ExitPrice", expected);
        Assert.Equal(expected, vm.FilteredItems.Select(x => x.ColumnId).OrderBy(id => Array.IndexOf(expected, id)).ToArray());
        Assert.All(vm.FilteredItems, x => Assert.Contains("Price", x.ColumnId, StringComparison.OrdinalIgnoreCase));

        vm.SearchQuery = string.Empty;
        Assert.Equal(KnownCount, vm.FilteredItems.Count);
    }

    [Fact]
    public void ViewModel_EnforcesAtLeastOneActiveColumnInvariant()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel(new[] { "TradeId" });
        Assert.Single(vm.AllItems.Where(x => x.IsActive));

        var activeItem = vm.AllItems.First(x => x.IsActive);
        Assert.Equal("TradeId", activeItem.ColumnId);

        // Attempt to uncheck the sole remaining active column
        activeItem.IsActive = false;

        // Invariant check: must reject and stay true
        Assert.True(activeItem.IsActive);
        Assert.Single(vm.GetActiveColumnIds());
    }

    [Fact]
    public void KnownColumns_GetColumnHeaderKey_ReturnsExpectedValues()
    {
        Assert.Equal("Backtest_TradeList_Header_TradeId", BacktestTradeHistoryColumnChooserViewModel.GetColumnHeaderKey("TradeId"));
        Assert.Equal("Backtest_TradeList_Header_Side", BacktestTradeHistoryColumnChooserViewModel.GetColumnHeaderKey("Side"));
        Assert.Equal("Backtest_TradeList_Header_ClosedNet", BacktestTradeHistoryColumnChooserViewModel.GetColumnHeaderKey("ClosedNet"));
        Assert.Equal("UnknownCol", BacktestTradeHistoryColumnChooserViewModel.GetColumnHeaderKey("UnknownCol"));
    }

    [Fact]
    public void XamlSource_UsesListScrollGutter_AndNoHardcodedFontSizes()
    {
        string xamlPath = Path.Combine(StockAnalyzer.Avalonia.Tests.TestHelpers.TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "Dialogs", "BacktestTradeHistoryColumnChooserWindow.axaml");
        Assert.True(File.Exists(xamlPath), $"XAML file must exist at {xamlPath}");

        string xaml = File.ReadAllText(xamlPath);

        // Standard requirement: scrollable lists must use shared ListScrollGutter
        Assert.Contains("Padding=\"{DynamicResource ListScrollGutter}\"", xaml);

        // Standard requirement: hardcoded font sizes in XAML view files are prohibited
        var matches = System.Text.RegularExpressions.Regex.Matches(xaml, @"FontSize=""(\d+)""");
        Assert.Empty(matches);

        // Standard requirement: Theme buttons
        Assert.Contains("Theme=\"{DynamicResource PrimaryButtonStyle}\"", xaml);
        Assert.Contains("Theme=\"{DynamicResource SecondaryButtonStyle}\"", xaml);
    }

    [Fact]
    public void ResultsViewModel_SetActiveTradeColumns_UpdatesVisibilityFlags()
    {
        var exporter = new Moq.Mock<StockAnalyzer.Core.Services.Backtest.Reporting.IBacktestReportExporter>();
        var dialog = new Moq.Mock<StockAnalyzer.Avalonia.Services.IDialogService>();
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            exporter.Object,
            dialog.Object);

        // Default: every known column is visible
        Assert.True(vm.IsTradeIdVisible);
        Assert.True(vm.IsSideVisible);
        Assert.True(vm.IsClosedNetVisible);
        Assert.Equal(KnownCount, vm.ActiveTradeColumns.Count);

        // Hide Side and EntryPrice
        vm.SetActiveTradeColumns(new[] { "TradeId", "EntryTime", "ClosedNet" });

        Assert.True(vm.IsTradeIdVisible);
        Assert.False(vm.IsSideVisible);
        Assert.True(vm.IsEntryTimeVisible);
        Assert.False(vm.IsEntryPriceVisible);
        Assert.True(vm.IsClosedNetVisible);
        Assert.Equal(3, vm.ActiveTradeColumns.Count);

        // Attempt to pass empty collection: must be ignored due to invariant
        vm.SetActiveTradeColumns(Array.Empty<string>());
        Assert.Equal(3, vm.ActiveTradeColumns.Count);
    }

    [Fact]
    public void ViewModel_InitializesWithActiveOrder_PreservingSequence()
    {
        var customOrder = new[] { "ClosedNet", "TradeId", "Quantity" };
        var vm = new BacktestTradeHistoryColumnChooserViewModel(customOrder);

        var activeIds = vm.GetActiveColumnIds();
        Assert.Equal(customOrder, activeIds);

        // FilteredItems under Active category also reflects customOrder
        Assert.Equal(BacktestTradeColumnCategory.Active, vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredItems.Count);
        Assert.Equal("ClosedNet", vm.FilteredItems[0].ColumnId);
        Assert.Equal("TradeId", vm.FilteredItems[1].ColumnId);
        Assert.Equal("Quantity", vm.FilteredItems[2].ColumnId);
    }

    [Fact]
    public void ViewModel_ReorderActiveItems_UpdatesGetActiveColumnIds()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel(new[] { "ClosedNet", "TradeId", "Quantity" });

        // Swap first two items in AllItems
        var first = vm.AllItems[0];
        vm.AllItems.RemoveAt(0);
        vm.AllItems.Insert(1, first);

        var activeIds = vm.GetActiveColumnIds();
        Assert.Equal(new[] { "TradeId", "ClosedNet", "Quantity" }, activeIds);
    }

    [Fact]
    public void ResultsViewModel_WithInjectedSettingsManager_LoadsCustomColumnsAndOrder()
    {
        var settingsMock = new Moq.Mock<StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager>();
        settingsMock.Setup(m => m.Load()).Returns(new StockAnalyzer.Core.Models.Backtest.Configuration.BacktestColumnSettings
        {
            TradeVisibleColumns = new List<string> { "ClosedNet", "TradeId" },
            MetricVisibleNames = new List<string> { "CAGR", "TotalReturn" }
        });

        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Moq.Mock.Of<StockAnalyzer.Core.Services.Backtest.Reporting.IBacktestReportExporter>(),
            Moq.Mock.Of<StockAnalyzer.Avalonia.Services.IDialogService>(),
            settingsMock.Object);

        Assert.Equal(new[] { "ClosedNet", "TradeId" }, vm.ActiveTradeColumns);
        Assert.True(vm.IsClosedNetVisible);
        Assert.True(vm.IsTradeIdVisible);
        Assert.False(vm.IsSideVisible);
        Assert.Equal(new[] { "CAGR", "TotalReturn" }, vm.ActiveMetricNames);
    }

    [Fact]
    public void ResultsViewModel_SetActiveTradeColumns_SavesToInjectedSettingsManager()
    {
        var settingsMock = new Moq.Mock<StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager>();
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Moq.Mock.Of<StockAnalyzer.Core.Services.Backtest.Reporting.IBacktestReportExporter>(),
            Moq.Mock.Of<StockAnalyzer.Avalonia.Services.IDialogService>(),
            settingsMock.Object);

        vm.SetActiveTradeColumns(new[] { "ClosedNet", "TradeId" });

        settingsMock.Verify(m => m.SaveColumnsAsync(
            Moq.It.Is<IReadOnlyList<string>>(cols => cols.SequenceEqual(new[] { "ClosedNet", "TradeId" })),
            Moq.It.IsAny<IReadOnlyList<string>>()), Moq.Times.Once);
    }

    [Fact]
    public void ViewModel_DuplicateActiveColumns_FilteredDefensively()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel(new[] { "TradeId", "TradeId", "Side" });
        Assert.Equal(KnownCount, vm.AllItems.Count);
        Assert.Equal(2, vm.GetActiveColumnIds().Count);
        Assert.Equal(new[] { "TradeId", "Side" }, vm.GetActiveColumnIds());
    }

    [Fact]
    public void KnownColumns_GetColumnMinWidth_ReturnsExpectedValues()
    {
        Assert.Equal(50, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("TradeId"));
        Assert.Equal(60, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("Side"));
        Assert.Equal(150, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("EntryTime"));
        Assert.Equal(90, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("EntryPrice"));
        Assert.Equal(110, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("ClosedNet"));
        Assert.Equal(50, BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth("UnknownColumn"));
    }

    [Fact]
    public void ViewModel_Dispose_UnsubscribesHandlersGracefully()
    {
        var vm = new BacktestTradeHistoryColumnChooserViewModel();
        vm.Dispose(); // Must execute without throwing
    }
}

