using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>A constructed <see cref="MainWindowViewModel"/> together with the mocks tests assert on.</summary>
internal sealed record MainWindowViewModelParts(
    MainWindowViewModel Vm,
    Mock<IDialogService> Dialog,
    Mock<ILocalizationService> Localization,
    Mock<IDetachedTabManager> TabManager,
    Mock<ILayoutSaveScheduler> Scheduler,
    Mock<ITearOffService> TearOff,
    LayoutStateStore Store);

/// <summary>
/// The single place that wires a <see cref="MainWindowViewModel"/> with mocked collaborators, so a change to its
/// constructor is made here once instead of in every test that hosts the view model.
/// </summary>
internal static class MainWindowViewModelFactory
{
    /// <param name="dispatcher">Defaults to <see cref="SynchronousDispatcherService"/>.</param>
    /// <param name="panelTabFactory">Defaults to <see cref="MockPanelTabFactory"/>.</param>
    /// <param name="containerRegistry">Defaults to a new <see cref="ContainerRegistry"/>.</param>
    /// <param name="layoutSettings">Layout limits of the created <see cref="LayoutStateStore"/>; the defaults when omitted.</param>
    internal static MainWindowViewModelParts Create(
        IDispatcherService? dispatcher = null,
        IPanelTabFactory? panelTabFactory = null,
        IContainerRegistry? containerRegistry = null,
        LayoutSettings? layoutSettings = null)
    {
        dispatcher ??= new SynchronousDispatcherService();
        panelTabFactory ??= new MockPanelTabFactory();
        containerRegistry ??= new ContainerRegistry();

        var dialog = new Mock<IDialogService>();
        var workspaceFacade = new Mock<IWorkspaceViewModelFacade>();
        var themeManager = new Mock<Core.Theme.IThemeManager>();
        var localization = new Mock<ILocalizationService>();
        var coreServices = new Mock<ICoreServicesFacade>();
        var watchlist = new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        coreServices.Setup(c => c.WatchlistManager).Returns(watchlist.Object);
        coreServices.Setup(c => c.Settings).Returns(new Mock<IStockAnalyzerSettings>().Object);
        coreServices.Setup(c => c.MarketDataProvider).Returns(new Mock<IMarketDataProvider>().Object);
        coreServices.Setup(c => c.PythonService).Returns(new Mock<IPythonService>().Object);

        var chartVm = new ChartViewModel();
        var tickerListVm = new TickerListViewModel(
            new Mock<IMarketDataProvider>().Object,
            new Mock<IPythonService>().Object,
            WeakReferenceMessenger.Default,
            dispatcher,
            watchlist.Object,
            new PortfolioManager(),
            dialog.Object,
            new TickerImportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerImportService>.Instance),
            new Mock<IChartSettingsManager>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerListViewModel>.Instance);

        workspaceFacade.Setup(w => w.TickerList).Returns(tickerListVm);
        workspaceFacade.Setup(w => w.DataWindow).Returns(new DataWindowViewModel(chartVm));
        workspaceFacade.Setup(w => w.Sidebar).Returns(new DrawingToolSidebarViewModel(chartVm));
        workspaceFacade.Setup(w => w.DrawingObjects).Returns(new DrawingObjectsViewModel(chartVm, dispatcher));

        var windowManagement = new Mock<IWindowManagementService>();
        windowManagement.SetupGet(x => x.BoundaryService).Returns(new Mock<IWindowBoundaryService>().Object);
        windowManagement.SetupGet(x => x.TabFactory).Returns(panelTabFactory);
        var tearOff = new Mock<ITearOffService>();
        // A default interface member is mocked like any other member (unset = false); a re-host succeeds unless a test says otherwise.
        tearOff.Setup(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Returns(true);
        windowManagement.SetupGet(x => x.TearOff).Returns(tearOff.Object);
        windowManagement.SetupGet(x => x.WindowFactory).Returns(new Mock<IDetachedWindowFactory>().Object);

        var tabManager = new Mock<IDetachedTabManager>();
        var scheduler = new Mock<ILayoutSaveScheduler>();

        var store = new LayoutStateStore(null, layoutSettings);

        var vm = new MainWindowViewModel(
            dialog.Object,
            workspaceFacade.Object,
            themeManager.Object,
            dispatcher,
            localization.Object,
            coreServices.Object,
            windowManagement.Object,
            tabManager.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance,
            store,
            scheduler.Object,
            new Mock<IWorkspaceCoordinator>().Object,
            new Mock<IWorkspaceSerializationService>().Object,
            () => new ChartViewModel(),
            containerRegistry: containerRegistry);

        return new MainWindowViewModelParts(vm, dialog, localization, tabManager, scheduler, tearOff, store);
    }
}
