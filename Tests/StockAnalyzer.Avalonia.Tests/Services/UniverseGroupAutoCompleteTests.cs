using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.ViewModels;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

[Collection("MessengerSharedState")]
public sealed class UniverseGroupAutoCompleteTests
{
    private sealed class StubTickerStateStore : ITickerStateStore
    {
        public StubTickerStateStore(IEnumerable<TickerGroupNode> groups)
        {
            Groups = groups;
        }

        public IEnumerable<TickerGroupNode> Groups { get; }
        public IEnumerable<IFilterableSymbol> DisplayItems => Array.Empty<IFilterableSymbol>();
        public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node) => Array.Empty<string>();
    }

    private static string FakeLocalizer(string key) => key switch
    {
        "TickerList_TypeName_Watchlist" => "Watchlist",
        "TickerList_TypeName_Portfolio" => "Portfolio",
        "Prl.Source.Kind.ListFolder" => "List",
        "Note_SearchSuggestion_TypeName_Filter" => "Filter",
        _ => $"[{key}]"
    };

    private static (UniverseSuggestionProvider Provider, WatchlistNode WatchlistCore, TickerListFolderNode FolderCore, FilterNode FilterNode) BuildDuplicateNameFixture()
    {
        var wlCore = new WatchlistNode(new WatchlistProfile(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Core",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            Array.Empty<WatchlistItem>()));

        var folderCore = new TickerListFolderNode(new TickerListFolderSettings
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Core"
        });

        var filterNode = new FilterNode(new FilterSettings
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Name = "Breakout"
        });

        var watchlistsCat = new CategoryNode(new Guid("00000000-0000-0000-0000-000000000002"), "Watchlists");
        watchlistsCat.Children!.Add(wlCore);

        var customCat = new CustomCategoryNode(new TickerParentCategorySettings { Id = Guid.NewGuid(), Name = "Sector" });
        customCat.Children!.Add(folderCore);

        var allTickers = new AllTickersNode("All Tickers");
        allTickers.Children!.Add(filterNode);

        var provider = new UniverseSuggestionProvider(
            new StubTickerStateStore(new TickerGroupNode[] { allTickers, watchlistsCat, customCat }),
            FakeLocalizer);

        return (provider, wlCore, folderCore, filterNode);
    }

    private static AutoCompleteBox FindAutoCompleteBox(Control root)
    {
        if (root is StackPanel stack)
        {
            foreach (var child in stack.Children)
            {
                if (child is Border b && b.Child is Grid g)
                {
                    var box = g.Children.OfType<AutoCompleteBox>().FirstOrDefault();
                    if (box != null) return box;
                }
            }
        }
        else if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is StackPanel fieldPanel)
                {
                    var box = fieldPanel.Children.OfType<AutoCompleteBox>().FirstOrDefault();
                    if (box != null) return box;
                }
            }
        }

        throw new InvalidOperationException("AutoCompleteBox not found.");
    }

    [AvaloniaFact]
    public void Build_ForBothBreadthIndicators_CreatesAutoCompleteWithSuggestionItemsAndWatermark()
    {
        var (provider, wlCore, _, _) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);

        var breadthParam = new CoreMarketBreadthParameter { UniverseGroupId = wlCore.Id.ToString("D") };
        var mcclellanParam = new CoreMcClellanOscillatorParameter();

        var breadthBox = FindAutoCompleteBox(builder.Build(breadthParam, Array.Empty<string>(), ParameterFormLayout.InlineLabel));
        var mcclellanBox = FindAutoCompleteBox(builder.Build(mcclellanParam, Array.Empty<string>(), ParameterFormLayout.TwoColumnFields));

        Assert.Equal("List / Filter", breadthBox.Watermark);
        Assert.Equal("List / Filter", mcclellanBox.Watermark);
        Assert.Equal("Core (Watchlist)", breadthBox.Text);
        Assert.Equal(string.Empty, mcclellanBox.Text);
    }

    [AvaloniaFact]
    public void SelectionChanged_AndClear_CommitsSelectedObjectId_AndRevertsInvalidPartialText()
    {
        var (provider, _, folderCore, _) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);
        var parameter = new CoreMarketBreadthParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var autoBox = FindAutoCompleteBox(control);

        var folderSuggestion = provider.GetSuggestions().Single(s => s.GroupId == folderCore.Id);
        autoBox.SelectedItem = folderSuggestion;

        Assert.Equal(folderCore.Id.ToString("D"), parameter.UniverseGroupId);
        Assert.Equal("Core (Sector · List)", autoBox.Text);

        // Non-matching partial text must NOT corrupt UniverseGroupId, and Enter/LostFocus reverts text.
        autoBox.Text = "Cor";
        Assert.Equal(folderCore.Id.ToString("D"), parameter.UniverseGroupId);

        autoBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(folderCore.Id.ToString("D"), parameter.UniverseGroupId);
        Assert.Equal("Core (Sector · List)", autoBox.Text);

        // Clearing text immediately commits empty string.
        autoBox.Text = string.Empty;
        Assert.Equal(string.Empty, parameter.UniverseGroupId);
    }

    [AvaloniaFact]
    public void TickerRowClickedMessage_TargetsLastFocusedEmptyBox_RespectsNonEmpty_AndDetachesCleanly()
    {
        var (provider, wlCore, folderCore, filterNode) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);

        var leftParam = new CoreMarketBreadthParameter();
        var rightParam = new CoreMcClellanOscillatorParameter();

        var leftRoot = builder.Build(leftParam, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var rightRoot = builder.Build(rightParam, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var leftBox = FindAutoCompleteBox(leftRoot);
        var rightBox = FindAutoCompleteBox(rightRoot);

        var hostPanel = new StackPanel();
        hostPanel.Children.Add(leftRoot);
        hostPanel.Children.Add(rightRoot);

        var window = new Window { Content = hostPanel };
        window.Show();
        try
        {
            // Focus right box -> click message should bind ONLY right box.
            rightBox.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });

            messenger.Send(new TickerRowClickedMessage("AAPL", AllTickersNode.StaticId, new[] { wlCore.Id }));

            Assert.Equal(string.Empty, leftParam.UniverseGroupId);
            Assert.Equal(wlCore.Id.ToString("D"), rightParam.UniverseGroupId);
            Assert.Equal("Core (Watchlist)", rightBox.Text);

            // Second click while right is non-empty -> must NOT overwrite right; falls back to left (empty).
            messenger.Send(new TickerRowClickedMessage("NVDA", filterNode.Id, new[] { wlCore.Id }));
            Assert.Equal(wlCore.Id.ToString("D"), rightParam.UniverseGroupId);
            Assert.Equal(filterNode.Id.ToString("D"), leftParam.UniverseGroupId);

            // Clear left, focus left, then detach left from visual tree (Supplement 1).
            leftBox.Text = string.Empty;
            leftBox.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            hostPanel.Children.Remove(leftRoot);

            // Also clear right -> now only right is attached and empty; click must NEVER touch detached left!
            rightBox.Text = string.Empty;
            messenger.Send(new TickerRowClickedMessage("MSFT", AllTickersNode.StaticId, new[] { folderCore.Id }));

            Assert.Equal(string.Empty, leftParam.UniverseGroupId);
            Assert.Equal(folderCore.Id.ToString("D"), rightParam.UniverseGroupId);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MouseClick_OnAutoCompletePopupItem_CommitsSelectedSuggestion()
    {
        var (provider, wlCore, folderCore, _) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);
        var parameter = new CoreMarketBreadthParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var autoBox = FindAutoCompleteBox(control);

        var otherFocusable = new TextBox { Width = 100 };
        var panel = new StackPanel();
        panel.Children.Add(control);
        panel.Children.Add(otherFocusable);

        var window = new Window { Content = panel, Width = 500, Height = 400 };
        window.Show();
        try
        {
            window.Activate();
            HeadlessDispatcher.Settle();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Click the AutoCompleteBox to focus and open dropdown.
            var boxPoint = autoBox.TranslatePoint(new global::Avalonia.Point(20, 15), window) ?? default;
            window.MouseDownSettled(boxPoint, MouseButton.Left);
            window.MouseUpSettled(boxPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(autoBox.IsDropDownOpen);

            var popup = autoBox.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");
            Assert.NotNull(popup);
            Assert.True(popup!.IsOpen);

            var popupTopLevel = TopLevel.GetTopLevel(popup.Child as Visual);
            Assert.NotNull(popupTopLevel);

            var listBox = (popup.Child as Visual)?.FindDescendantOfType<ListBox>(includeSelf: true);
            Assert.NotNull(listBox);
            Assert.True(listBox!.ItemCount >= 2);

            // Click the second item (Folder Core) inside the popup via real mouse events.
            var itemContainer = listBox.ContainerFromIndex(1) as ListBoxItem;
            Assert.NotNull(itemContainer);
            var itemPoint = itemContainer!.TranslatePoint(new global::Avalonia.Point(20, itemContainer.Bounds.Height / 2), popupTopLevel!) ?? default;

            popupTopLevel!.MouseDownSettled(itemPoint, MouseButton.Left);
            popupTopLevel!.MouseUpSettled(itemPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(folderCore.Id.ToString("D"), parameter.UniverseGroupId);
            Assert.Equal("Core (Sector · List)", autoBox.Text);
            Assert.False(autoBox.IsDropDownOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MouseClick_OnAutoCompletePopupItem_AfterPartialFilterText_CommitsSelectedSuggestion()
    {
        var (provider, _, _, filterBreakout) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);
        var parameter = new CoreMarketBreadthParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var autoBox = FindAutoCompleteBox(control);

        var window = new Window { Content = control, Width = 500, Height = 400 };
        window.Show();
        try
        {
            window.Activate();
            HeadlessDispatcher.Settle();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var boxPoint = autoBox.TranslatePoint(new global::Avalonia.Point(20, 15), window) ?? default;
            window.MouseDownSettled(boxPoint, MouseButton.Left);
            window.MouseUpSettled(boxPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            window.KeyTextInput("Bre");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var popup = autoBox.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");
            Assert.NotNull(popup);
            Assert.True(popup!.IsOpen);

            var popupTopLevel = TopLevel.GetTopLevel(popup.Child as Visual);
            var listBox = (popup.Child as Visual)?.FindDescendantOfType<ListBox>(includeSelf: true);
            Assert.NotNull(popupTopLevel);
            Assert.NotNull(listBox);
            Assert.Equal(1, listBox!.ItemCount);

            var itemContainer = listBox.ContainerFromIndex(0) as ListBoxItem;
            Assert.NotNull(itemContainer);
            var itemPoint = itemContainer!.TranslatePoint(new global::Avalonia.Point(20, itemContainer.Bounds.Height / 2), popupTopLevel!) ?? default;

            popupTopLevel!.MouseDownSettled(itemPoint, MouseButton.Left);
            popupTopLevel!.MouseUpSettled(itemPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(filterBreakout.Id.ToString("D"), parameter.UniverseGroupId);
            Assert.Equal("Breakout", autoBox.Text);
            Assert.False(autoBox.IsDropDownOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MouseClick_OnTickerListViewMainGridRow_BindsEmptyUniverseGroupId()
    {
        var wlId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var wlProfile = new WatchlistProfile(
            wlId,
            "TechList",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) });

        var mockMarketDataProvider = new Mock<IMarketDataProvider>();
        mockMarketDataProvider
            .Setup(m => m.GetAvailableTickersAsync())
            .ReturnsAsync(new[] { "NVDA" });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { wlProfile });

        var messenger = new StrongReferenceMessenger();
        var vm = new TickerListViewModel(
            marketDataProvider: mockMarketDataProvider.Object,
            pythonService: null!,
            messenger: messenger,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var provider = new UniverseSuggestionProvider(vm, FakeLocalizer);
        var builder = new ParameterViewBuilder(provider, messenger);
        var parameter = new CoreMarketBreadthParameter();
        var paramControl = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var autoBox = FindAutoCompleteBox(paramControl);

        var tickerView = new TickerListView { DataContext = vm };
        var host = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(paramControl, 0);
        Grid.SetRow(tickerView, 1);
        host.Children.Add(paramControl);
        host.Children.Add(tickerView);

        var window = new Window { Content = host, Width = 800, Height = 600 };
        window.Show();
        try
        {
            // Select TechList node so AAPL row appears in MainGrid.
            var wlNode = vm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>())
                .FirstOrDefault(n => n.Id == wlId);
            Assert.NotNull(wlNode);
            vm.SelectedNode = wlNode;

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var mainGrid = tickerView.FindControl<TreeDataGrid>("MainGrid");
            Assert.NotNull(mainGrid);

            var rowControl = mainGrid!.GetVisualDescendants()
                .OfType<global::Avalonia.Controls.Primitives.TreeDataGridRow>()
                .FirstOrDefault();
            Assert.NotNull(rowControl);

            var clickPoint = rowControl!.TranslatePoint(new global::Avalonia.Point(80, rowControl.Bounds.Height / 2), window) ?? default;
            window.MouseDownSettled(clickPoint, MouseButton.Left);
            window.MouseUpSettled(clickPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(wlId.ToString("D"), parameter.UniverseGroupId);
            Assert.Equal("TechList", autoBox.Text);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void MouseClick_OnTickerListViewMainGridRow_AcrossSeparateWindowsAndIgnoringHiddenEditor_BindsVisibleEmptyBox()
    {
        var wlId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var wlProfile = new WatchlistProfile(
            wlId,
            "GrowthLeaders",
            IndicatorColor.FromRgb(0, 128, 0),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("NVDA", DateTimeOffset.UtcNow) });

        var mockMarketDataProvider = new Mock<IMarketDataProvider>();
        mockMarketDataProvider
            .Setup(m => m.GetAvailableTickersAsync())
            .ReturnsAsync(new[] { "NVDA" });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { wlProfile });

        var messenger = new StrongReferenceMessenger();
        var vm = new TickerListViewModel(
            marketDataProvider: mockMarketDataProvider.Object,
            pythonService: null!,
            messenger: messenger,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var provider = new UniverseSuggestionProvider(vm, FakeLocalizer);
        var builder = new ParameterViewBuilder(provider, messenger);

        var hiddenParam = new CoreMarketBreadthParameter();
        var hiddenControl = builder.Build(hiddenParam, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var hiddenContainer = new Border { IsVisible = false, Child = hiddenControl };

        var visibleParam = new CoreMarketBreadthParameter();
        var visibleControl = builder.Build(visibleParam, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var visibleBox = FindAutoCompleteBox(visibleControl);

        var screenerHost = new StackPanel();
        screenerHost.Children.Add(hiddenContainer);
        screenerHost.Children.Add(visibleControl);

        var screenerWindow = new Window { Content = screenerHost, Width = 500, Height = 300 };
        var tickerView = new TickerListView { DataContext = vm };
        var mainWindow = new Window { Content = tickerView, Width = 800, Height = 600 };

        screenerWindow.Show();
        mainWindow.Show();
        try
        {
            // Keep AllTickers selected so binding must fall back to ContainingGroupIds.
            var allTickersNode = vm.Groups.OfType<AllTickersNode>().FirstOrDefault();
            if (allTickersNode != null)
            {
                vm.SelectedNode = allTickersNode;
            }

            // Simulate user having focused/opened the visible AutoCompleteBox before clicking Ticker tab.
            var boxPoint = visibleBox.TranslatePoint(new global::Avalonia.Point(20, 15), screenerWindow) ?? default;
            screenerWindow.MouseDownSettled(boxPoint, MouseButton.Left);
            screenerWindow.MouseUpSettled(boxPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var mainGrid = tickerView.FindControl<TreeDataGrid>("MainGrid");
            Assert.NotNull(mainGrid);

            var rowControl = mainGrid!.GetVisualDescendants()
                .OfType<global::Avalonia.Controls.Primitives.TreeDataGridRow>()
                .FirstOrDefault();
            Assert.NotNull(rowControl);

            var clickPoint = rowControl!.TranslatePoint(new global::Avalonia.Point(80, rowControl.Bounds.Height / 2), mainWindow) ?? default;
            mainWindow.MouseDownSettled(clickPoint, MouseButton.Left);
            mainWindow.MouseUpSettled(clickPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, hiddenParam.UniverseGroupId);
            Assert.Equal(wlId.ToString("D"), visibleParam.UniverseGroupId);
            Assert.Equal("GrowthLeaders", visibleBox.Text);
            Assert.False(visibleBox.IsDropDownOpen);
        }
        finally
        {
            mainWindow.Close();
            screenerWindow.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MouseClick_OnTickerListViewMainGridRow_ReflectsClickedTickerListIntoEmptyChartMarketBreadthAndSwitchesAcrossLists()
    {
        var wl1Id = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var wl2Id = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var wl1Profile = new WatchlistProfile(
            wl1Id,
            "ListOne",
            IndicatorColor.FromRgb(0, 128, 0),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) });
        var wl2Profile = new WatchlistProfile(
            wl2Id,
            "ListTwo",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow), new("MSFT", DateTimeOffset.UtcNow) });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { wl1Profile, wl2Profile });

        var messenger = new StrongReferenceMessenger();
        var tickerVm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: messenger,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var provider = new UniverseSuggestionProvider(tickerVm, FakeLocalizer);

        var dataService = new MockDataService();
        var chartSettings = new MockChartSettingsManager();
        chartSettings.UpdatePreview(new GlobalChartSettings { IsSubWindowVisible = true });

        IReadOnlyList<string> expectedDates = Array.Empty<string>();
        var mockBreadthProvider = new Mock<IMarketBreadthSeriesProvider>();
        mockBreadthProvider
            .Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<TimeFrame>(), It.IsAny<MarketBreadthParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid groupId, TimeFrame _, MarketBreadthParameters _, CancellationToken _) =>
            {
                int adValue = groupId == wl1Id ? 111 : 222;
                var dates = expectedDates;
                var adLine = Enumerable.Repeat<int?>(adValue, dates.Count).ToList();
                var advancers = Enumerable.Repeat(1, dates.Count).ToList();
                var decliners = Enumerable.Repeat(0, dates.Count).ToList();
                var unchanged = Enumerable.Repeat(0, dates.Count).ToList();

                return new MarketBreadthResult
                {
                    DateCount = dates.Count,
                    SymbolCount = 1,
                    Series = new MarketBreadthSeries
                    {
                        Dates = dates,
                        Advancers = advancers,
                        Decliners = decliners,
                        Unchanged = unchanged,
                        AdLine = adLine
                    }
                };
            });

        var pipeline = new AnalysisPipelineService(marketBreadthProvider: mockBreadthProvider.Object);
        var chartVm = new ChartViewModel(
            dataService,
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(dataService),
            null!,
            new ThemeManager(),
            chartSettings,
            new SynchronousDispatcherService(),
            null,
            pipeline,
            null,
            null,
            null,
            messenger: messenger,
            universeSuggestionProvider: new Lazy<IUniverseSuggestionProvider>(provider));

        var breadthParam = new CoreMarketBreadthParameter
        {
            MetricMode = MarketBreadthMetricMode.AdLine,
            UniverseGroupId = string.Empty
        };
        var breadthSetting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MarketBreadth,
            DisplayName = "Market Breadth",
            IsEnabled = true,
            IsOverlay = false,
            ParameterObject = breadthParam
        };
        chartVm.Symbol = "AAPL";
        chartVm.Indicators.Add(breadthSetting);
        await ChartViewModelTestWaits.AwaitLoadsAsync(chartVm);
        expectedDates = chartVm.Candles.Select(c => c.Timestamp.ToString("yyyy-MM-dd")).ToList();

        var tickerView = new TickerListView { DataContext = tickerVm };
        var window = new Window { Content = tickerView, Width = 800, Height = 600 };
        window.Show();
        try
        {
            var wl1Node = tickerVm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>())
                .First(n => n.Id == wl1Id);
            var wl2Node = tickerVm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>())
                .First(n => n.Id == wl2Id);

            // 1. Click AAPL under ListOne -> Chart MarketBreadth reflects ListOne (111) while keeping UniverseGroupId empty.
            tickerVm.SelectedNode = wl1Node;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var mainGrid = tickerView.FindControl<TreeDataGrid>("MainGrid")!;
            var row1 = mainGrid.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.TreeDataGridRow>().First();
            var click1 = row1.TranslatePoint(new global::Avalonia.Point(80, row1.Bounds.Height / 2), window) ?? default;
            window.MouseDownSettled(click1, MouseButton.Left);
            window.MouseUpSettled(click1, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            await ChartViewModelTestWaits.EventuallyAsync(() =>
            {
                Assert.NotNull(chartVm.IndicatorResults);
                Assert.True(chartVm.IndicatorResults!.TryGetValue(breadthSetting.Id, out var res));
                Assert.True(res!.IsSuccessful);
                Assert.Equal(111m, res.MainValues.Last(v => v.HasValue)!.Value);
            });
            Assert.Equal(string.Empty, breadthParam.UniverseGroupId);

            // 2. Switch to ListTwo and click the SAME symbol (AAPL) -> Chart MarketBreadth updates to ListTwo (222).
            tickerVm.SelectedNode = wl2Node;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var row2 = mainGrid.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.TreeDataGridRow>().First();
            var click2 = row2.TranslatePoint(new global::Avalonia.Point(80, row2.Bounds.Height / 2), window) ?? default;
            window.MouseDownSettled(click2, MouseButton.Left);
            window.MouseUpSettled(click2, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            await ChartViewModelTestWaits.EventuallyAsync(() =>
            {
                Assert.NotNull(chartVm.IndicatorResults);
                Assert.True(chartVm.IndicatorResults!.TryGetValue(breadthSetting.Id, out var res));
                Assert.True(res!.IsSuccessful);
                Assert.Equal(222m, res.MainValues.Last(v => v.HasValue)!.Value);
            });
            Assert.Equal(string.Empty, breadthParam.UniverseGroupId);
        }
        finally
        {
            window.Close();
            chartVm.Dispose();
            tickerVm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MouseClick_OnTickerListViewMainGridRow_DoesNotOverrideExplicitChartUniverseOrUnsyncedChart()
    {
        var wl1Id = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var wl2Id = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var wl1Profile = new WatchlistProfile(
            wl1Id,
            "ClickedList",
            IndicatorColor.FromRgb(0, 128, 0),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) });
        var wl2Profile = new WatchlistProfile(
            wl2Id,
            "ExplicitList",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem> { new("MSFT", DateTimeOffset.UtcNow) });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { wl1Profile, wl2Profile });

        var messenger = new StrongReferenceMessenger();
        var tickerVm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: messenger,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        var provider = new UniverseSuggestionProvider(tickerVm, FakeLocalizer);

        var dataService = new MockDataService();
        var chartSettings = new MockChartSettingsManager();
        chartSettings.UpdatePreview(new GlobalChartSettings { IsSubWindowVisible = true });

        var mockBreadthProvider = new Mock<IMarketBreadthSeriesProvider>();
        mockBreadthProvider
            .Setup(p => p.GetAsync(wl2Id, It.IsAny<TimeFrame>(), It.IsAny<MarketBreadthParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarketBreadthResult
            {
                DateCount = 1,
                SymbolCount = 1,
                Series = new MarketBreadthSeries
                {
                    Dates = new[] { DateTime.UtcNow.Date.ToString("yyyy-MM-dd") },
                    Advancers = new[] { 1 },
                    Decliners = new[] { 0 },
                    Unchanged = new[] { 0 },
                    AdLine = new int?[] { 777 }
                }
            });

        var pipeline = new AnalysisPipelineService(marketBreadthProvider: mockBreadthProvider.Object);
        var chartVm = new ChartViewModel(
            dataService,
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(dataService),
            null!,
            new ThemeManager(),
            chartSettings,
            new SynchronousDispatcherService(),
            null,
            pipeline,
            null,
            null,
            null,
            messenger: messenger,
            universeSuggestionProvider: new Lazy<IUniverseSuggestionProvider>(provider));

        var explicitParam = new CoreMarketBreadthParameter
        {
            MetricMode = MarketBreadthMetricMode.AdLine,
            UniverseGroupId = wl2Id.ToString("D")
        };
        var explicitSetting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MarketBreadth,
            DisplayName = "Market Breadth",
            IsEnabled = true,
            IsOverlay = false,
            ParameterObject = explicitParam
        };
        chartVm.Symbol = "AAPL";
        chartVm.Indicators.Add(explicitSetting);
        await ChartViewModelTestWaits.AwaitLoadsAsync(chartVm);

        await ChartViewModelTestWaits.EventuallyAsync(() =>
        {
            Assert.NotNull(chartVm.IndicatorResults);
            Assert.True(chartVm.IndicatorResults!.ContainsKey(explicitSetting.Id));
        });

        var tickerView = new TickerListView { DataContext = tickerVm };
        var window = new Window { Content = tickerView, Width = 800, Height = 600 };
        window.Show();
        try
        {
            var wl1Node = tickerVm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>())
                .First(n => n.Id == wl1Id);
            tickerVm.SelectedNode = wl1Node;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var mainGrid = tickerView.FindControl<TreeDataGrid>("MainGrid")!;
            var row = mainGrid.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.TreeDataGridRow>().First();
            var clickPoint = row.TranslatePoint(new global::Avalonia.Point(80, row.Bounds.Height / 2), window) ?? default;
            window.MouseDownSettled(clickPoint, MouseButton.Left);
            window.MouseUpSettled(clickPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            await ChartViewModelTestWaits.AwaitLoadsAsync(chartVm);

            Assert.Equal(wl2Id.ToString("D"), explicitParam.UniverseGroupId);
            mockBreadthProvider.Verify(
                p => p.GetAsync(wl1Id, It.IsAny<TimeFrame>(), It.IsAny<MarketBreadthParameters>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // Unsynced chart must also ignore row clicks even with empty UniverseGroupId.
            chartVm.IsSyncEnabled = false;
            explicitParam.UniverseGroupId = string.Empty;
            window.MouseDownSettled(clickPoint, MouseButton.Left);
            window.MouseUpSettled(clickPoint, MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            mockBreadthProvider.Verify(
                p => p.GetAsync(wl1Id, It.IsAny<TimeFrame>(), It.IsAny<MarketBreadthParameters>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            window.Close();
            chartVm.Dispose();
            tickerVm.Dispose();
        }
    }

    [AvaloniaFact]
    public void UnresolvableGroupId_SetsToolTip_AndSelectingValidGroupOrClearingResetsToolTip()
    {
        var (provider, wlCore, _, _) = BuildDuplicateNameFixture();
        var messenger = new StrongReferenceMessenger();
        var builder = new ParameterViewBuilder(provider, messenger);
        var orphanId = Guid.NewGuid().ToString("D");
        var parameter = new CoreMarketBreadthParameter { UniverseGroupId = orphanId };

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var autoBox = FindAutoCompleteBox(control);
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        try
        {
            Assert.Equal(orphanId, ToolTip.GetTip(autoBox));

            parameter.UniverseGroupId = wlCore.Id.ToString("D");
            Assert.Null(ToolTip.GetTip(autoBox));

            parameter.UniverseGroupId = orphanId;
            Assert.Equal(orphanId, ToolTip.GetTip(autoBox));

            parameter.UniverseGroupId = string.Empty;
            Assert.Null(ToolTip.GetTip(autoBox));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void UniverseSuggestionProvider_ResolveTargetGroupId_EnumeratesGroupsOnceAndSkipsExcludedNodes()
    {
        var validId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var excludedNode = new CategoryNode(categoryId, "Watchlists");
        var validNode = new WatchlistNode(new WatchlistProfile(
            validId,
            "Tech",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            Array.Empty<WatchlistItem>()));

        int enumerationCount = 0;
        IEnumerable<TickerGroupNode> CountingGroups()
        {
            enumerationCount++;
            yield return excludedNode;
            yield return validNode;
        }

        var store = new CountingTickerStateStore(CountingGroups);
        var provider = new UniverseSuggestionProvider(store, FakeLocalizer);

        Guid? resolved = provider.ResolveTargetGroupId(new TickerRowClickedMessage(
            "AAPL",
            categoryId,
            new[] { categoryId, Guid.NewGuid(), validId }));

        Assert.Equal(validId, resolved);
        Assert.Equal(1, enumerationCount);
    }

    [Fact]
    public void ChartViewModel_ApplyDynamicUniverseGroupId_SupportsArbitraryIUniverseBreadthParameterOnSnapshotWithoutMutatingOriginal()
    {
        var groupId = Guid.NewGuid().ToString("D");
        var customParam = new SyntheticUniverseBreadthParameter();
        var original = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = customParam
        };
        var populatedParam = new SyntheticUniverseBreadthParameter { UniverseGroupId = Guid.NewGuid().ToString("D") };
        var populated = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = populatedParam
        };

        IReadOnlyList<CoreIndicatorSettings> applied = ChartViewModel.ApplyDynamicUniverseGroupId(
            new[] { populated, original },
            groupId);

        Assert.Equal(2, applied.Count);
        Assert.Same(populated, applied[0]);
        Assert.NotSame(original, applied[1]);
        Assert.Equal(groupId, Assert.IsType<SyntheticUniverseBreadthParameter>(applied[1].ParameterObject).UniverseGroupId);
        Assert.Equal(string.Empty, customParam.UniverseGroupId);
    }

    private sealed class SyntheticUniverseBreadthParameter : CoreIndicatorParameterBase, IUniverseBreadthParameter
    {
        public string UniverseGroupId { get; set; } = string.Empty;
        public override string GetDisplayName(string indicatorType) => indicatorType;
        public override void Validate() { }
    }

    private sealed class CountingTickerStateStore : ITickerStateStore
    {
        private readonly Func<IEnumerable<TickerGroupNode>> _groupsFactory;

        public CountingTickerStateStore(Func<IEnumerable<TickerGroupNode>> groupsFactory)
        {
            _groupsFactory = groupsFactory;
        }

        public IEnumerable<TickerGroupNode> Groups => _groupsFactory();
        public IEnumerable<IFilterableSymbol> DisplayItems => Array.Empty<IFilterableSymbol>();
        public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node) => Array.Empty<string>();
    }
}
