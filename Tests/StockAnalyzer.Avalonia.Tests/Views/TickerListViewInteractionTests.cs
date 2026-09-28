using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

// Shares the static WeakReferenceMessenger.Default with other TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class TickerListViewInteractionTests
{
    [AvaloniaFact]
    public void TickerTagFilter_TextStaysSynchronizedAcrossViewModelAndView()
    {
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        vm.TickerTagFilter.FilterText = "AAPL";

        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 600 };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var filterBox = view.FindControl<AutoCompleteBox>("TickerTagFilterBox");
            Assert.NotNull(filterBox);

            // A newly-created View must display an already-active filter from its ViewModel.
            Assert.Equal("AAPL", filterBox!.Text);

            // User input must continue to drive the live filter in the opposite direction.
            filterBox.Text = "MSFT";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("MSFT", vm.TickerTagFilter.FilterText);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void RapidDoubleClick_OnCategoryHeader_TogglesExpandedExactlyTwice()
    {
        // Arrange: one non-portfolio watchlist so the "Watchlists" CategoryNode has a child
        // to expand/collapse.
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>
        {
            new(Guid.NewGuid(), "Tech", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) })
        });

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 400, Height = 600 };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var treeView = view.FindControl<TreeView>("TickersTreeView");
            Assert.NotNull(treeView);

            var watchlistsCategory = vm.Groups.First(n => n.Id == TickerListViewModel.WatchlistsCategoryId);
            var container = treeView!.TreeContainerFromItem(watchlistsCategory) as TreeViewItem;
            Assert.NotNull(container);

            var point = container!.TranslatePoint(new global::Avalonia.Point(50, 10), window) ?? default;
            var initialExpanded = container.IsExpanded;

            // Act: two rapid clicks on the same point. Each helper call waits for the dispatcher to go
            // idle by wall-clock time, and the whole sequence stays well inside Avalonia's double-click
            // recognition window, so its real gesture-recognition pipeline should treat this as a
            // double-tap - exactly the scenario that triggers TreeViewItem's built-in
            // OnHeaderDoubleTapped toggle.
            // (MouseDownSettled/MouseUpSettled, not Avalonia's MouseDown/MouseUp: those throw "Dispatcher
            // job loop detected" when a press's wall-clock-bound transitions outlast 10 fast rounds.)
            window.MouseDownSettled(point, MouseButton.Left);
            window.MouseUpSettled(point, MouseButton.Left);
            window.MouseDownSettled(point, MouseButton.Left);
            window.MouseUpSettled(point, MouseButton.Left);

            // Assert: exactly 2 toggles occurred (1 per click), landing back on the initial
            // state. Without the DoubleTappedEvent compensation handler (TickerListView.axaml.cs
            // OnTreeViewDoubleTapped), the built-in double-tap toggle adds an uncancelled 3rd
            // flip here, leaving IsExpanded inverted relative to the start state.
            Assert.Equal(initialExpanded, container.IsExpanded);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>sa_improve: real end-to-end proof that the 2-level nested "Add To Custom Category"
    /// submenu actually resolves through two Avalonia Popup boundaries - the existing single-level
    /// submenus (OtherWatchlists/AllPortfolios) only prove one Popup level works, so nesting a
    /// second level was a genuine, previously-untested risk in this codebase. Opens the real
    /// ContextMenu on the real TreeDataGrid, drills into both submenu levels, and checks the leaf
    /// MenuItem's Command/CommandParameter bindings resolved to the real ViewModel instances -
    /// not just that some text rendered.</summary>
    [AvaloniaFact]
    public void AddToCustomCategorySubmenu_NestsCategoryThenFolder_WithWorkingCommandBinding()
    {
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var folderA = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "Folder A" };
        var folderB = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "Folder B" };
        var category = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
        {
            Name = "My Sector",
            Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folderA, folderB },
        };
        vm.ImportCustomTickerCategories(new[] { category });

        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 600 };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var mainGrid = view.FindControl<TreeDataGrid>("MainGrid");
            Assert.NotNull(mainGrid);
            var contextMenu = mainGrid!.ContextMenu;
            Assert.NotNull(contextMenu);

            contextMenu!.Open(mainGrid);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var assignHeader = LocalizationManager.Instance["TickerList_AssignToCustomCategory"];
            var assignMenuItem = contextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, assignHeader));

            // Level 1: opening it must realize exactly one row - the one non-empty category.
            // (Items holds the bound TickerAssignableCategoryGroup data objects, not the generated
            // MenuItem controls - ContainerFromIndex fetches the actual realized container so its
            // Style-set Header/ItemsSource can be inspected.)
            assignMenuItem.IsSubMenuOpen = true;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, assignMenuItem.ItemCount);
            var categoryMenuItem = Assert.IsType<MenuItem>(assignMenuItem.ContainerFromIndex(0));
            Assert.Equal("My Sector", categoryMenuItem.Header);

            // Level 2: opening the category row must realize both its folders, each a real
            // assignment target with the command correctly bound through the second Popup.
            categoryMenuItem.IsSubMenuOpen = true;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, categoryMenuItem.ItemCount);
            var folderMenuItems = Enumerable.Range(0, categoryMenuItem.ItemCount)
                .Select(i => Assert.IsType<MenuItem>(categoryMenuItem.ContainerFromIndex(i)))
                .ToList();
            Assert.Equal(2, folderMenuItems.Count);
            Assert.Contains(folderMenuItems, m => Equals(m.Header, "Folder A"));
            Assert.Contains(folderMenuItems, m => Equals(m.Header, "Folder B"));

            foreach (var folderMenuItem in folderMenuItems)
            {
                Assert.Same(vm.AssignSelectedTickersToCustomCategoryCommand, folderMenuItem.Command);
                var parameter = Assert.IsType<TickerListFolderAssignmentEntry>(folderMenuItem.CommandParameter);
                Assert.Contains(parameter.FolderId, new[] { folderA.Id, folderB.Id });
            }

            // Close the nested Popups before the window teardown below, innermost first with a
            // dispatcher pump after each step - leaving them open (or closing them out of order)
            // trips an unrelated Avalonia.Headless logical-tree-detachment ordering issue on
            // Window.Close().
            categoryMenuItem.IsSubMenuOpen = false;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            assignMenuItem.IsSubMenuOpen = false;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            contextMenu.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            // Avalonia 11.3.10 / Avalonia.Headless: once a Window has ever opened a ContextMenu
            // Popup, closing that Window throws ArgumentOutOfRangeException from
            // VisualLayerManager.OnDetachedFromLogicalTree (indexing past the end of its own
            // overlay list) - reproduced identically regardless of teardown order (submenus closed
            // innermost-first with dispatcher pumps between each step, ContextMenu.Close() called
            // explicitly, or the View detached from Content first). All assertions above already
            // completed by this point; this is a headless-test-only teardown artifact of the
            // framework/version combination, not a defect in the feature under test - narrowly
            // swallowed here rather than left unexplained.
            try { window.Close(); } catch (ArgumentOutOfRangeException) { }
            vm.Dispose();
        }
    }
}
