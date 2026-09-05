#if DEBUG && DESIGN_TIME
using System;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Parameterless constructor strictly for the Avalonia designer and testing contexts.
    /// Excluded from runtime execution and Release builds.
    /// </summary>
    public MainWindowViewModel()
    {


        _detachedTabManager = new DesignTimeDetachedTabManager();
        _localizationService = new DesignTimeLocalizationService();
        _stateStore = new LayoutStateStore();
        _layoutSaveScheduler = new LayoutSaveScheduler(_stateStore, null);
        _windowFactory = null!;
        _tearOffService = null!;
        _boundaryService = new WindowBoundaryService();
        _dialogService = new DialogService();
        _serializationService = new WorkspaceSerializationService();
        _themeManager = new ThemeManager();
        _settings = new Services.MockStockAnalyzerSettings();
        _pythonService = new PythonService(_settings, null!);
        _dispatcherService = new Services.DispatcherService(); 
        _watchlistManager = new StockAnalyzer.Core.Services.WatchlistManager();
        _panelTabFactory = new PanelTabFactory(null!, new TabRegistry());
        
        // During designer execution, completely block external dependencies (DuckDB/Parquet) and use Mocks for stability
        _marketDataProvider = new Services.MockMarketDataProvider();
        
        _logger = NullLogger<MainWindowViewModel>.Instance;
        _chartViewModelFactory = () => new ChartViewModel { DialogService = _dialogService };
        ChartViewModel = new ChartViewModel { DialogService = _dialogService };
        SidebarViewModel = new DrawingToolSidebarViewModel(ChartViewModel);
        DataWindowViewModel = new DataWindowViewModel(ChartViewModel);
        DrawingObjectsViewModel = new DrawingObjectsViewModel(ChartViewModel, _dispatcherService);
        TickerListViewModel = new TickerListViewModel(
            _marketDataProvider, 
            _pythonService, 
            WeakReferenceMessenger.Default, 
            _dispatcherService, 
            _watchlistManager, 
            new StockAnalyzer.Core.Services.PortfolioManager(), 
            _dialogService, 
            new StockAnalyzer.Core.Services.TickerImportService(NullLogger<StockAnalyzer.Core.Services.TickerImportService>.Instance),
            new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
            NullLogger<TickerListViewModel>.Instance);
        
        _workspaceCoordinator = new WorkspaceCoordinator(
            _stateStore, 
            _serializationService, 
            _watchlistManager, 
            Microsoft.Extensions.Options.Options.Create(new WorkspaceCoordinatorOptions()), 
            _dispatcherService, 
            NullLogger<WorkspaceCoordinator>.Instance);
        _workspaceCoordinator.Bind(this);

        // Propagate state store and panel dimension changes to ViewModel properties
        _stateStore.PropertyChanged += OnStateStorePropertyChanged;

        InitializePanelTabs();
    }

    private class DesignTimeDetachedTabManager : IDetachedTabManager
    {
        public System.Collections.Generic.IReadOnlyList<WorkspaceViewItem> DetachedTabs => System.Array.Empty<WorkspaceViewItem>();
        public bool RegisterActiveDetachedTab(WorkspaceViewItem item) => false;
        public bool RemoveActiveDetachedTab(WorkspaceViewItem item) => false;
        public void Restore(StockAnalyzer.Core.Models.Settings.WorkspaceSettings settings) {}
        public void Capture(System.Collections.Generic.List<StockAnalyzer.Core.Models.Settings.DetachedTabInfo> destination, System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreIndicatorSettings>? fallbackIndicators = null) {}
        public void Dispose() {}
    }

    private class DesignTimeLocalizationService : ILocalizationService
    {
        public string GetString(string key) => key;
    }
}
#endif
