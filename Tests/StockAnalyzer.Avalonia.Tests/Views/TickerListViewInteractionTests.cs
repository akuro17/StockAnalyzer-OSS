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
using StockAnalyzer.Avalonia.ViewModels;
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

            // Act: two rapid clicks on the same point. The real wall-clock gap between these
            // calls (microseconds, since this runs synchronously in-process) is well inside
            // Avalonia's double-click recognition window, so its real gesture-recognition
            // pipeline should treat this as a double-tap - exactly the scenario that triggers
            // TreeViewItem's built-in OnHeaderDoubleTapped toggle.
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

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
}
