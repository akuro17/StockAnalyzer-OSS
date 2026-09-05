using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.UI;
using System.IO;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Avalonia.ViewModels.Notes;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, 
    IWorkspaceLayoutTarget,
    IRecipient<IndicatorSettingsChangedMessage>,
    IRecipient<SingleIndicatorSettingsChangedMessage>,
    IRecipient<TickerSelectedMessage>,
    IRecipient<OpenIndicatorPropertiesMessage>,
    IRecipient<LayoutChangedMessage>,
    IRecipient<ChartSymbolChangedMessage>,
    IRecipient<TearOffRequestMessage>,
    IRecipient<RestoreRequestMessage>,
    IRecipient<CurrentTickerRequestMessage>,
    IRecipient<NoteChartJumpRequestedMessage>,
    IRecipient<NavigateToNoteTimelineRequestedMessage>,
    IRecipient<OrphanedAttachmentsDetectedMessage>,
    IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IThemeManager _themeManager;
    private readonly IPythonService _pythonService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILocalizationService _localizationService;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly IStockAnalyzerSettings _settings;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Func<ChartViewModel> _chartViewModelFactory;
    private readonly IWatchlistManager _watchlistManager;
    private readonly ITearOffService _tearOffService;
    private readonly IDetachedWindowFactory _windowFactory;
    private readonly IWindowBoundaryService _boundaryService;
    private readonly IDetachedTabManager _detachedTabManager;
    private bool _isTearingOff;
    private bool _isExiting;
    private bool _isDisposed;
    private static int _chartInstanceCounter = 0;
    private static int _isCounterInitialized = 0; // 0: 未初期化, 1: 初期化済
    private readonly ILayoutSaveScheduler _layoutSaveScheduler;
    private readonly IWorkspaceCoordinator _workspaceCoordinator;
    private readonly IWorkspaceSerializationService _serializationService;
    private readonly LayoutStateStore _stateStore;
    private readonly ISourceIndicatorService? _sourceIndicatorService;
    private readonly IDynamicPeriodDriverService? _dynamicPeriodDriverService;

    // Child VM
    public ChartViewModel ChartViewModel { get; }
    public DataWindowViewModel DataWindowViewModel { get; }
    public TickerListViewModel TickerListViewModel { get; }
    public DrawingToolSidebarViewModel SidebarViewModel { get; }
    public DrawingObjectsViewModel DrawingObjectsViewModel { get; }
    private readonly IPanelTabFactory _panelTabFactory;
    private int _autoSavePauseRefCount = 0;
    private bool _isApplyingSettings => Volatile.Read(ref _autoSavePauseRefCount) > 0;
    private bool _isLoaded;

    [ObservableProperty]
    private ObservableCollection<WorkspaceViewItem> _leftPanelTabs = new();

    [ObservableProperty]
    private ObservableCollection<WorkspaceViewItem> _rightPanelTabs = new();

    [ObservableProperty]
    private ObservableCollection<WorkspaceViewItem> _topPanelTabs = new();

    [ObservableProperty]
    private ObservableCollection<WorkspaceViewItem> _bottomPanelTabs = new();

    public int LeftSelectedTabIndex
    {
        get => _stateStore.SelectedTabIndices[PanelRegion.Left];
        set
        {
            if (_stateStore.SelectedTabIndices[PanelRegion.Left] != value)
            {
                _stateStore.SetTabIndex(PanelRegion.Left, value);
                OnPropertyChanged(nameof(LeftSelectedTabIndex));
                if (!_isApplyingSettings)
                {
                    _logger.LogInformation("UI Interaction: Left panel selected tab changed to index {Index}.", value);
                    _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
                }
            }
        }
    }

    public int RightSelectedTabIndex
    {
        get => _stateStore.SelectedTabIndices[PanelRegion.Right];
        set
        {
            if (_stateStore.SelectedTabIndices[PanelRegion.Right] != value)
            {
                _stateStore.SetTabIndex(PanelRegion.Right, value);
                OnPropertyChanged(nameof(RightSelectedTabIndex));
                if (!_isApplyingSettings)
                {
                    _logger.LogInformation("UI Interaction: Right panel selected tab changed to index {Index}.", value);
                    _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
                }
            }
        }
    }

    public int TopSelectedTabIndex
    {
        get => _stateStore.SelectedTabIndices[PanelRegion.Top];
        set
        {
            if (_stateStore.SelectedTabIndices[PanelRegion.Top] != value)
            {
                _stateStore.SetTabIndex(PanelRegion.Top, value);
                OnPropertyChanged(nameof(TopSelectedTabIndex));
                if (!_isApplyingSettings)
                {
                    _logger.LogInformation("UI Interaction: Top panel selected tab changed to index {Index}.", value);
                    _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
                }
            }
        }
    }

    public int BottomSelectedTabIndex
    {
        get => _stateStore.SelectedTabIndices[PanelRegion.Bottom];
        set
        {
            if (_stateStore.SelectedTabIndices[PanelRegion.Bottom] != value)
            {
                _stateStore.SetTabIndex(PanelRegion.Bottom, value);
                OnPropertyChanged(nameof(BottomSelectedTabIndex));
                if (!_isApplyingSettings)
                {
                    _logger.LogInformation("UI Interaction: Bottom panel selected tab changed to index {Index}.", value);
                    _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
                }
            }
        }
    }

    [ObservableProperty]
    private ObservableCollection<string> _availableTickers = new();

    /// <summary>Dismissible status-bar notice for the app-startup orphaned-attachment scan (spec
    /// section 4.5). Null/empty hides the notice; set from Receive(OrphanedAttachmentsDetectedMessage)
    /// and auto-cleared after a few seconds.</summary>
    [ObservableProperty]
    private string? _orphanedAttachmentNoticeText;

    private string? _selectedTicker;
    public string? SelectedTicker
    {
        get => _selectedTicker;
        set
        {
            if (SetProperty(ref _selectedTicker, value) && value != null)
            {
                ChartViewModel.Symbol = value;
                _ = ChartViewModel.LoadDataCommand.ExecuteAsync(null);
                if (!_isApplyingSettings)
                {
                    _logger.LogInformation("UI Interaction: Selected ticker changed to {Ticker}.", value);
                    WeakReferenceMessenger.Default.Send(new TickerSelectedMessage(value));
                }
                _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
            }
        }
    }

    public MainWindowViewModel(
        IDialogService dialogService,
        IWorkspaceViewModelFacade workspaceViewModels,
        IThemeManager themeManager,
        IDispatcherService dispatcherService,
        ILocalizationService localizationService,
        ICoreServicesFacade coreServices,
        IWindowManagementService windowManagement,
        IDetachedTabManager detachedTabManager,
        ILogger<MainWindowViewModel> logger,
        LayoutStateStore stateStore,
        ILayoutSaveScheduler layoutSaveScheduler,
        IWorkspaceCoordinator workspaceCoordinator,
        IWorkspaceSerializationService serializationService,
        Func<ChartViewModel> chartViewModelFactory,
        ISourceIndicatorService? sourceIndicatorService = null,
        IDynamicPeriodDriverService? dynamicPeriodDriverService = null)
    {
        _sourceIndicatorService = sourceIndicatorService;
        _dynamicPeriodDriverService = dynamicPeriodDriverService;
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _detachedTabManager = detachedTabManager ?? throw new ArgumentNullException(nameof(detachedTabManager));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _workspaceCoordinator = workspaceCoordinator ?? throw new ArgumentNullException(nameof(workspaceCoordinator));
        _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
        _workspaceCoordinator.Bind(this);

        _layoutSaveScheduler = layoutSaveScheduler ?? throw new ArgumentNullException(nameof(layoutSaveScheduler));
        _layoutSaveScheduler.RegisterSaveAction(() => _workspaceCoordinator.SaveActiveWorkspaceAsync());

        // Propagate state store and panel dimension changes to ViewModel properties
        _stateStore.PropertyChanged += OnStateStorePropertyChanged;

        ArgumentNullException.ThrowIfNull(windowManagement);
        _boundaryService = windowManagement.BoundaryService ?? throw new ArgumentNullException(nameof(windowManagement.BoundaryService));
        _panelTabFactory = windowManagement.TabFactory ?? throw new ArgumentNullException(nameof(windowManagement.TabFactory));
        _tearOffService = windowManagement.TearOff ?? throw new ArgumentNullException(nameof(windowManagement.TearOff));
        _windowFactory = windowManagement.WindowFactory ?? throw new ArgumentNullException(nameof(windowManagement.WindowFactory));

        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        
        ArgumentNullException.ThrowIfNull(coreServices);
        _pythonService = coreServices.PythonService;
        _settings = coreServices.Settings;
        _watchlistManager = coreServices.WatchlistManager;
        _marketDataProvider = coreServices.MarketDataProvider;
        
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _chartViewModelFactory = chartViewModelFactory ?? throw new ArgumentNullException(nameof(chartViewModelFactory));
        
        ChartViewModel = _chartViewModelFactory() ?? throw new InvalidOperationException("Failed to resolve ChartViewModel from factory.");
        ChartViewModel.DialogService = _dialogService;
        
        ArgumentNullException.ThrowIfNull(workspaceViewModels);
        DataWindowViewModel = workspaceViewModels.DataWindow;
        TickerListViewModel = workspaceViewModels.TickerList;
        SidebarViewModel = workspaceViewModels.Sidebar;
        DrawingObjectsViewModel = workspaceViewModels.DrawingObjects;
        workspaceViewModels.BindChart(ChartViewModel);

        // Register for messages
        WeakReferenceMessenger.Default.RegisterAll(this);

        // Auto-save when watchlists change
        _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;

        // Auto-save when Column visibility or SORT changes
        TickerListViewModel.PropertyChanged += OnTickerListPropertyChanged;

        // Auto-save when Column visibility or SORT changes
        TickerListViewModel.ActiveColumns.CollectionChanged += OnTickerListActiveColumnsChanged;
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e)
    {
        if (!_isApplyingSettings)
        {
            _logger.LogInformation("UI Interaction: Watchlist updated.");
            _layoutSaveScheduler.RequestSave(LayoutChangeReason.WatchlistUpdated);
        }
    }

    private void OnTickerListPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isApplyingSettings && (e.PropertyName == nameof(TickerListViewModel.SidebarGridWidth) || 
                                     e.PropertyName == nameof(TickerListViewModel.SelectedWatchlist) ||
                                     e.PropertyName == "ColumnSortChanged"))
        {
            _logger.LogInformation("UI Interaction: Ticker list property '{PropertyName}' changed. Selected Watchlist: {WatchlistName}.", 
                e.PropertyName, TickerListViewModel.SelectedWatchlist?.Name ?? "(none)");
            _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
        }
    }

    private void OnTickerListActiveColumnsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            _logger.LogInformation("UI Interaction: Ticker list active columns collection changed (Action: {Action}).", e.Action);
            _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
        }
    }


    [RelayCommand]
    private void AddPanelChart()
    {
        AddPanelChartInternal();
    }

    private void AddPanelChartInternal()
    {
        // FR-70-11-c: Open as an independent (detached) empty window container
        _dispatcherService.Post(() => 
        {
            var owner = _dialogService.GetMainWindowOwner();
            if (owner != null)
            {
                var window = _windowFactory.CreateWindow(owner);
                _windowFactory.ShowWindow(window); // No owner => not always-on-top
                
                _logger.LogInformation("Spawned new empty independent Tab Window.");
            }
        });
    }



    private void InitializePanelTabs()
    {
        // Initial setup for default tabs is now handled by PanelTabFactory in ApplyPanelTabs fallback
         if (LeftPanelTabs.Count == 0)
         {
             var item = _panelTabFactory.CreateTab("TickerList");
             if (item != null)
             {
                 BindTabToActiveChart(item);
                 LeftPanelTabs.Add(item);
             }
             _stateStore.LeftPanel.IsVisible = true;
             _stateStore.LeftPanel.WidthOrHeight = LayoutConstants.DefaultLeftWidth;
         }
 
         if (RightPanelTabs.Count == 0)
         {
             var item = _panelTabFactory.CreateTab("DataWindow");
             if (item != null)
             {
                 BindTabToActiveChart(item);
                 RightPanelTabs.Add(item);
             }
             _stateStore.RightPanel.IsVisible = true;
             _stateStore.RightPanel.WidthOrHeight = LayoutConstants.DefaultRightWidth;
         }

         if (BottomPanelTabs.Count == 0)
         {
             var item = _panelTabFactory.CreateTab("PortfolioSummary");
             if (item != null)
             {
                 BindTabToActiveChart(item);
                 BottomPanelTabs.Add(item);
             }
             _stateStore.BottomPanel.IsVisible = true;
             _stateStore.BottomPanel.WidthOrHeight = LayoutConstants.DefaultBottomHeight;
         }
    }


    [ObservableProperty]
    private TimeframeType _selectedTimeframe = TimeframeType.Daily;

    [ObservableProperty]
    private bool _isSyncing;



    [ObservableProperty]
    private bool _isRulerChecked;

    partial void OnIsRulerCheckedChanged(bool value)
    {
        ChartViewModel.CurrentTool = value ? DrawingTool.Ruler : DrawingTool.Pointer;
    }

    [RelayCommand]
    private void SetTool(string toolName)
    {
        if (toolName == "Pointer")
        {
            IsRulerChecked = false; // This triggers OnIsRulerCheckedChanged -> Pointer
        }
    }

    // --- SSoT 直接公開用プロパティ ---
    public LayoutStateStore Layout => _stateStore;

    // --- IWorkspaceLayoutTarget の明示的インターフェース実装 (全8プロパティ) ---
    double IWorkspaceLayoutTarget.LeftPanelWidth
    {
        get => _stateStore.LeftPanel.WidthOrHeight;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (_stateStore.LeftPanel.IsVisible)
            {
                double minSize = _stateStore.LeftPanel.IsPinned ? LayoutConstants.MinPanelHeight : 32.0;
                double clamped = Math.Max(minSize, value);
                _stateStore.LeftPanel.WidthOrHeight = clamped;
            }
            else
            {
                if (value > 0.0)
                {
                    _stateStore.LeftPanel.LastSize = value;
                }
                _stateStore.LeftPanel.WidthOrHeight = 0.0;
            }
        }
    }
    bool IWorkspaceLayoutTarget.IsLeftPanelVisible
    {
        get => _stateStore.LeftPanel.IsVisible;
        set => _stateStore.LeftPanel.IsVisible = value;
    }
    bool IWorkspaceLayoutTarget.IsLeftPanelPinned
    {
        get => _stateStore.LeftPanel.IsPinned;
        set => _stateStore.LeftPanel.IsPinned = value;
    }
    double IWorkspaceLayoutTarget.RightPanelWidth
    {
        get => _stateStore.RightPanel.WidthOrHeight;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (_stateStore.RightPanel.IsVisible)
            {
                double minSize = _stateStore.RightPanel.IsPinned ? LayoutConstants.MinPanelHeight : 32.0;
                double clamped = Math.Max(minSize, value);
                _stateStore.RightPanel.WidthOrHeight = clamped;
            }
            else
            {
                if (value > 0.0)
                {
                    _stateStore.RightPanel.LastSize = value;
                }
                _stateStore.RightPanel.WidthOrHeight = 0.0;
            }
        }
    }
    bool IWorkspaceLayoutTarget.IsRightPanelVisible
    {
        get => _stateStore.RightPanel.IsVisible;
        set => _stateStore.RightPanel.IsVisible = value;
    }
    bool IWorkspaceLayoutTarget.IsRightPanelPinned
    {
        get => _stateStore.RightPanel.IsPinned;
        set => _stateStore.RightPanel.IsPinned = value;
    }
    double IWorkspaceLayoutTarget.TopPanelHeight
    {
        get => _stateStore.TopPanel.WidthOrHeight;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (_stateStore.TopPanel.IsVisible)
            {
                double minSize = _stateStore.TopPanel.IsPinned ? LayoutConstants.MinPanelHeight : 32.0;
                double clamped = Math.Max(minSize, value);
                _stateStore.TopPanel.WidthOrHeight = clamped;
            }
            else
            {
                if (value > 0.0)
                {
                    _stateStore.TopPanel.LastSize = value;
                }
                _stateStore.TopPanel.WidthOrHeight = 0.0;
            }
        }
    }
    bool IWorkspaceLayoutTarget.IsTopPanelVisible
    {
        get => _stateStore.TopPanel.IsVisible;
        set => _stateStore.TopPanel.IsVisible = value;
    }
    bool IWorkspaceLayoutTarget.IsTopPanelPinned
    {
        get => _stateStore.TopPanel.IsPinned;
        set => _stateStore.TopPanel.IsPinned = value;
    }
    double IWorkspaceLayoutTarget.BottomPanelHeight
    {
        get => _stateStore.BottomPanel.WidthOrHeight;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (_stateStore.BottomPanel.IsVisible)
            {
                double minSize = _stateStore.BottomPanel.IsPinned ? LayoutConstants.MinPanelHeight : 32.0;
                double clamped = Math.Max(minSize, value);
                _stateStore.BottomPanel.WidthOrHeight = clamped;
            }
            else
            {
                if (value > 0.0)
                {
                    _stateStore.BottomPanel.LastSize = value;
                }
                _stateStore.BottomPanel.WidthOrHeight = 0.0;
            }
        }
    }
    bool IWorkspaceLayoutTarget.IsBottomPanelVisible
    {
        get => _stateStore.BottomPanel.IsVisible;
        set => _stateStore.BottomPanel.IsVisible = value;
    }
    bool IWorkspaceLayoutTarget.IsBottomPanelPinned
    {
        get => _stateStore.BottomPanel.IsPinned;
        set => _stateStore.BottomPanel.IsPinned = value;
    }

    // --- IWorkspaceLayoutTarget Decoupled Implementations ---
    System.Collections.Generic.IReadOnlyList<CoreIndicatorSettings> IWorkspaceLayoutTarget.GetIndicators()
    {
        return ChartViewModel?.Indicators != null ? System.Linq.Enumerable.ToList(ChartViewModel.Indicators) : new System.Collections.Generic.List<CoreIndicatorSettings>();
    }

    void IWorkspaceLayoutTarget.ApplyIndicators(System.Collections.Generic.IEnumerable<CoreIndicatorSettings> indicators)
    {
        if (ChartViewModel != null)
        {
            ChartViewModel.Indicators.Clear();
            foreach (var indicator in indicators)
            {
                ChartViewModel.Indicators.Add(indicator);
            }
        }
    }

    void IWorkspaceLayoutTarget.RefreshTickerListNodes()
    {
        TickerListViewModel?.RefreshNodes();
    }

    void IWorkspaceLayoutTarget.SelectTickerListNodeById(System.Guid id)
    {
        TickerListViewModel?.SelectNodeById(id);
    }

    void IWorkspaceLayoutTarget.ImportFilterSettings(System.Collections.Generic.IEnumerable<StockAnalyzer.Core.Models.Settings.FilterSettings> filters)
    {
        TickerListViewModel?.ImportFilterSettings(filters.ToList());
    }

    void IWorkspaceLayoutTarget.SetActiveColumns(System.Collections.Generic.IEnumerable<string> columnNames)
    {
        TickerListViewModel?.SetActiveColumns(columnNames);
    }

    void IWorkspaceLayoutTarget.ApplyColumnWidths(System.Collections.Generic.Dictionary<string, string>? widths)
    {
        TickerListViewModel?.ApplyColumnWidths(widths);
    }

    void IWorkspaceLayoutTarget.ApplySortState(string? columnName, int direction)
    {
        if (columnName != null) TickerListViewModel?.ApplySortState(columnName, direction);
    }

    [RelayCommand]
    private void TogglePanel(PanelRegion region)
    {
        if (!Enum.IsDefined(typeof(PanelRegion), region)) return; // 境界外入力の破棄
        _stateStore.TogglePanelVisibility(region);
        _layoutSaveScheduler.RequestSave(LayoutChangeReason.PanelResized);
    }

    [RelayCommand]
    private void AddTab(string panelAndId)
    {
        if (string.IsNullOrWhiteSpace(panelAndId))
        {
            _logger.LogWarning("AddTab received null or empty parameter. Ignoring.");
            return;
        }

        ReadOnlySpan<char> input = panelAndId.AsSpan();
        int colonIndex = input.IndexOf(':');

        if (colonIndex <= 0 || colonIndex == input.Length - 1)
        {
            _logger.LogWarning("AddTab parameter format invalid. Expected '{{Region}}:{{TabId}}', received '{Parameter}'.", panelAndId);
            return;
        }

        ReadOnlySpan<char> regionPart = input[..colonIndex];
        ReadOnlySpan<char> tabIdPart = input[(colonIndex + 1)..];

        if (!Enum.TryParse<PanelRegion>(regionPart, ignoreCase: true, out var region) || !Enum.IsDefined(typeof(PanelRegion), region))
        {
            _logger.LogWarning("AddTab received invalid region '{Region}'. Expected Left/Right/Top/Bottom.", regionPart.ToString());
            return;
        }

        if (tabIdPart.IsEmpty)
        {
            _logger.LogWarning("AddTab received empty TabId. Region={Region}.", region);
            return;
        }

        string tabId;
        if (tabIdPart.Equals("Chart".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "Chart";
        else if (tabIdPart.Equals("TickerList".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "TickerList";
        else if (tabIdPart.Equals("DataWindow".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "DataWindow";
        else if (tabIdPart.Equals("DrawingTools".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "DrawingTools";
        else if (tabIdPart.Equals("PortfolioSummary".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "PortfolioSummary";
        else if (tabIdPart.Equals("Allocation".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "Allocation";
        else if (tabIdPart.Equals("DrawingObjects".AsSpan(), StringComparison.OrdinalIgnoreCase))
            tabId = "DrawingObjects";
        else
            tabId = tabIdPart.ToString();

        var tab = _panelTabFactory.CreateTab(tabId);
        if (tab == null) return;
        BindTabToActiveChart(tab);

        switch (region)
        {
            case PanelRegion.Left:
                if (LeftPanelTabs.Count >= LayoutConstants.MaxPanelTabs) return;
                LeftPanelTabs.Add(tab); LeftSelectedTabIndex = LeftPanelTabs.Count - 1; break;
            case PanelRegion.Right:
                if (RightPanelTabs.Count >= LayoutConstants.MaxPanelTabs) return;
                RightPanelTabs.Add(tab); RightSelectedTabIndex = RightPanelTabs.Count - 1; break;
            case PanelRegion.Top:
                if (TopPanelTabs.Count >= LayoutConstants.MaxPanelTabs) return;
                TopPanelTabs.Add(tab); TopSelectedTabIndex = TopPanelTabs.Count - 1; break;
            case PanelRegion.Bottom:
                if (BottomPanelTabs.Count >= LayoutConstants.MaxPanelTabs) return;
                BottomPanelTabs.Add(tab); BottomSelectedTabIndex = BottomPanelTabs.Count - 1; break;
        }
        _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
    }


    [RelayCommand]
    private void RemoveTab(WorkspaceViewItem? item)
    {
        if (item == null) return;

        bool removed = false;
        if (LeftPanelTabs.Remove(item)) removed = true;
        else if (RightPanelTabs.Remove(item)) removed = true;
        else if (TopPanelTabs.Remove(item)) removed = true;
        else if (BottomPanelTabs.Remove(item)) removed = true;

        if (removed)
        {
            // FR-70-11-03: Strict Memory Management (Dispose on Close)
            // Guard: Do not dispose if we are just tearing off the tab to a new window.
            if (!_isTearingOff)
            {
                // Evict the matching cached view from ViewSwitcher's Zero-Object-Recreation
                // cache before disposing the ViewModel, so the disposed instance and its view
                // are not held for the lifetime of the panel.
                WeakReferenceMessenger.Default.Send(new WorkspaceViewItemRemovedMessage(item.ViewModel));

                if (item.ViewModel is IDisposable disposable)
                {
                    disposable.Dispose();
                    _logger.LogInformation("Tab [{TabId}] removed and disposed.", item.Id);
                }
            }
            
            _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDetachTab))]
    private void DetachTab(WorkspaceViewItem? item)
    {
        if (item == null) return;

        _isTearingOff = true;
        DetachTabCommand.NotifyCanExecuteChanged();

        try
        {
            _tearOffService.TearOff(item);
        }
        finally
        {
            _isTearingOff = false;
            DetachTabCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDetachTab(WorkspaceViewItem? item) => item != null && !_isTearingOff;

    /// <summary>
    /// Reorders a tab within its panel.
    /// Format: "PanelName:FromIndex:ToIndex"
    /// </summary>
    [RelayCommand]
    private void ReorderTab(string panelAndIndices)
    {
        if (string.IsNullOrWhiteSpace(panelAndIndices))
        {
            _logger.LogWarning("ReorderTab received null or empty parameter. Ignoring.");
            return;
        }

        ReadOnlySpan<char> input = panelAndIndices.AsSpan();
        
        int firstColon = input.IndexOf(':');
        if (firstColon <= 0 || firstColon == input.Length - 1)
        {
            _logger.LogWarning("ReorderTab parameter format invalid. Expected '{{Region}}:{{From}}:{{To}}', received '{Parameter}'.", panelAndIndices);
            return;
        }

        ReadOnlySpan<char> regionPart = input[..firstColon];
        ReadOnlySpan<char> indicesPart = input[(firstColon + 1)..];

        int secondColon = indicesPart.IndexOf(':');
        if (secondColon <= 0 || secondColon == indicesPart.Length - 1)
        {
            _logger.LogWarning("ReorderTab parameter format invalid. Expected '{{Region}}:{{From}}:{{To}}', received '{Parameter}'.", panelAndIndices);
            return;
        }

        ReadOnlySpan<char> fromPart = indicesPart[..secondColon];
        ReadOnlySpan<char> toPart = indicesPart[(secondColon + 1)..];

        if (!Enum.TryParse<PanelRegion>(regionPart, ignoreCase: true, out var region) || !Enum.IsDefined(typeof(PanelRegion), region))
        {
            _logger.LogWarning("ReorderTab received invalid region '{Region}'. Expected Left/Right/Top/Bottom.", regionPart.ToString());
            return;
        }

        if (!int.TryParse(fromPart, out int fromIndex) || !int.TryParse(toPart, out int toIndex))
        {
            _logger.LogWarning("ReorderTab failed to parse indices. From='{From}', To='{To}'.", fromPart.ToString(), toPart.ToString());
            return;
        }

        if (fromIndex == toIndex) return;

        ObservableCollection<WorkspaceViewItem>? tabs = region switch
        {
            PanelRegion.Left => LeftPanelTabs,
            PanelRegion.Right => RightPanelTabs,
            PanelRegion.Top => TopPanelTabs,
            PanelRegion.Bottom => BottomPanelTabs,
            _ => null
        };

        if (tabs == null) return;

        if (fromIndex < 0 || fromIndex >= tabs.Count)
        {
            _logger.LogWarning("ReorderTab fromIndex '{FromIndex}' is out of bounds. Tabs count = {Count}.", fromIndex, tabs.Count);
            return;
        }

        int distance = Math.Abs(fromIndex - toIndex);
        if (distance > LayoutConstants.MAX_TAB_REORDER_DISTANCE)
        {
            _logger.LogWarning("ReorderTab requested reorder distance '{Distance}' exceeds MAX_TAB_REORDER_DISTANCE '{Limit}'. Clamping.", distance, LayoutConstants.MAX_TAB_REORDER_DISTANCE);
            int direction = toIndex > fromIndex ? 1 : -1;
            toIndex = fromIndex + (LayoutConstants.MAX_TAB_REORDER_DISTANCE * direction);
        }

        if (toIndex < 0)
        {
            _logger.LogWarning("ReorderTab clamped toIndex '{ToIndex}' was negative. Clamping to 0.", toIndex);
            toIndex = 0;
        }
        else if (toIndex >= tabs.Count)
        {
            _logger.LogWarning("ReorderTab clamped toIndex '{ToIndex}' exceeded tabs count. Clamping to '{MaxIndex}'.", toIndex, tabs.Count - 1);
            toIndex = tabs.Count - 1;
        }

        if (fromIndex == toIndex) return;

        tabs.Move(fromIndex, toIndex);

        switch (region)
        {
            case PanelRegion.Left: LeftSelectedTabIndex = toIndex; break;
            case PanelRegion.Right: RightSelectedTabIndex = toIndex; break;
            case PanelRegion.Top: TopSelectedTabIndex = toIndex; break;
            case PanelRegion.Bottom: BottomSelectedTabIndex = toIndex; break;
        }

        _layoutSaveScheduler.RequestSave(LayoutChangeReason.TabMoved);
    }

    [RelayCommand]
    private async Task SaveLayoutAsync()
    {
        await _layoutSaveScheduler.ForceSaveImmediateAsync();
    }

    private async Task SaveLayoutForceAsync(string? path = null)
    {
        try
        {
            await _workspaceCoordinator.SaveActiveWorkspaceAsync(path);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to save layout to {Path}", path ?? "default");
        }
    }

    [RelayCommand]
    private async Task Exit()
    {
        PrepareExit();
        await SaveLayoutForceAsync();

        _dialogService.Shutdown();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await _dialogService.ShowIndicatorSettingsDialogAsync(ChartViewModel.Indicators);
    }

    [RelayCommand]
    private async Task OpenThemeSettingsAsync()
    {
        await _dialogService.ShowSettingsDialogAsync(SettingsConstants.Keys.Theme);
    }

    [RelayCommand]
    private async Task OpenScreenerAsync()
    {
        await _dialogService.ShowScreenerDialogAsync();
    }

    [RelayCommand]
    private async Task OpenTrainingWizardAsync()
    {
        await _dialogService.ShowTrainingWizardDialogAsync();
    }

    [RelayCommand]
    private async Task OpenLogViewerAsync()
    {
        await _dialogService.ShowLogViewerAsync();
    }

    [RelayCommand]
    private async Task LoadWorkspaceAsync()
    {
        var filePath = await _dialogService.ShowOpenFileDialogAsync(
            _localizationService.GetString("Dialog_LoadWorkspaceTitle") ?? "Load Workspace",
            new[] { "json" }
        );

        if (!string.IsNullOrEmpty(filePath))
        {
            try
            {
                var settings = await _serializationService.ImportPortableAsync(filePath);
                if (settings == null)
                {
                    string emptyTemplate = _localizationService.GetString("Dialog_WorkspaceLoadEmpty") ?? "Failed to load workspace file from '{0}'. File was empty or not found.";
                    await _dialogService.ShowAlertAsync(
                        _localizationService.GetString("Dialog_Error") ?? "Error",
                        string.Format(emptyTemplate, filePath)
                    );
                    return;
                }

                await _workspaceCoordinator.ApplyLoadedWorkspaceAsync(settings);

                // Make this loaded workspace the active default for next startup
                await _workspaceCoordinator.SaveActiveWorkspaceAsync();

                string successTemplate = _localizationService.GetString("Dialog_WorkspaceLoadSuccess") ?? "Workspace loaded from {0}";
                await _dialogService.ShowAlertAsync(
                    _localizationService.GetString("Dialog_Success") ?? "Success",
                    string.Format(successTemplate, filePath)
                );
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to load workspace from {Path}", filePath);
                string errorTemplate = _localizationService.GetString("Dialog_WorkspaceLoadError") ?? "Failed to load workspace: {0}";
                await _dialogService.ShowAlertAsync(
                    _localizationService.GetString("Dialog_Error") ?? "Error",
                    string.Format(errorTemplate, ex.Message)
                );
            }
        }
    }

    [RelayCommand]
    private async Task SaveWorkspaceAsync()
    {
        var filePath = await _dialogService.ShowSaveFileDialogAsync(
            _localizationService.GetString("Dialog_SaveWorkspaceTitle") ?? "Save Workspace",
            "json",
            "workspace.json",
            new[] { "json" }
        );

        if (!string.IsNullOrEmpty(filePath))
        {
            try
            {
                var settings = await _workspaceCoordinator.CaptureCurrentWorkspaceAsync();
                await _serializationService.ExportPortableAsync(settings, filePath);

                // Make this the new default workspace for next startup
                await _workspaceCoordinator.SaveActiveWorkspaceAsync();

                string successTemplate = _localizationService.GetString("Dialog_WorkspaceSaveSuccess") ?? "Workspace saved to {0}";
                await _dialogService.ShowAlertAsync(
                    _localizationService.GetString("Dialog_Success") ?? "Success",
                    string.Format(successTemplate, filePath)
                );
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to save workspace to {Path}", filePath);
                string errorTemplate = _localizationService.GetString("Dialog_WorkspaceSaveError") ?? "Failed to save workspace: {0}";
                await _dialogService.ShowAlertAsync(
                    _localizationService.GetString("Dialog_Error") ?? "Error",
                    string.Format(errorTemplate, ex.Message)
                );
            }
        }
    }

    [RelayCommand]
    private async Task ExportChartImageAsync()
    {
        if (ChartViewModel != null)
        {
            await _dialogService.ShowExportChartImageDialogAsync(ChartViewModel);
        }
    }

    /// <summary>
    /// Attempts to silently load the default workspace if it exists.
    /// Called during application startup.
    /// </summary>
    public async Task TryLoadDefaultWorkspaceAsync()
    {
        using (PauseAutoSave())
        {
            try
            {
                await _workspaceCoordinator.InitializeWorkspaceAsync();

                // Trigger initial data load if a symbol is present
                if (!string.IsNullOrEmpty(ChartViewModel.Symbol))
                {
                    await ChartViewModel.LoadDataCommand.ExecuteAsync(null);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed during default workspace load");
            }
            finally
            {
                _isLoaded = true;
            }
        }
    }

    public void SetTimeframe(TimeframeType timeframe)
    {
        SelectedTimeframe = timeframe;
        ChartViewModel.SelectedTimeFrame = timeframe;
        _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
    }

    [RelayCommand]
    private void SetTimeframe(string timeframeName)
    {
        if (System.Enum.TryParse<TimeframeType>(timeframeName, out var timeframe))
        {
            SetTimeframe(timeframe);
        }
    }

    public void Receive(IndicatorSettingsChangedMessage message)
    {
        if (message.Value != null)
        {
            _dispatcherService.Post(() =>
            {
                ChartViewModel.Indicators.Clear();
                foreach (var indicator in message.Value)
                {
                    ChartViewModel.Indicators.Add(indicator);
                }

                _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
            });
        }
    }

    public void Receive(SingleIndicatorSettingsChangedMessage message)
    {
        if (message.Value != null)
        {
            _dispatcherService.Post(() =>
            {
                var existing = ChartViewModel.Indicators.FirstOrDefault(i => i.Id == message.Value.Id);
                if (existing != null)
                {
                    bool isMathChange = message.Value.MathematicalVersion > 0 
                        || message.Value.PriceSource != existing.PriceSource 
                        || message.Value.SourceIndicatorId != existing.SourceIndicatorId 
                        || message.Value.DynamicPeriodIndicatorId != existing.DynamicPeriodIndicatorId;
                    
                    if (isMathChange)
                    {
                        // MATH PARAMETER CHANGE: Instance replacement triggers full recalculation
                        int index = ChartViewModel.Indicators.IndexOf(existing);
                        if (index >= 0)
                        {
                            message.Value.MathematicalVersion = Math.Max(existing.MathematicalVersion + 1, existing.MathematicalVersion + message.Value.MathematicalVersion);
                            ChartViewModel.Indicators[index] = message.Value;
                        }
                    }
                    else
                    {
                        // VISUAL-ONLY CHANGE: Update properties in-place, skip CalculateIndicatorsAsync entirely.
                        // Direct RenderTrigger++ for zero-latency redraw.
                        var src = message.Value;
                        existing.Color = src.Color;
                        existing.Thickness = src.Thickness;
                        existing.Style = src.Style;
                        existing.IsEnabled = src.IsEnabled;
                        existing.IsOverlay = src.IsOverlay;
                        existing.UseUpDownColors = src.UseUpDownColors;
                        existing.UpColor = src.UpColor;
                        existing.DownColor = src.DownColor;
                        existing.OverlayPanelId = src.OverlayPanelId;
                        existing.ShowCrossMarkers = src.ShowCrossMarkers;
                        existing.ShowAxisLabel = src.ShowAxisLabel;
                        
                        // Copy series colors - NON-DESTRUCTIVE SYNC (FR-18-02)
                        foreach (var srcColor in src.SeriesColors)
                        {
                            var targetColor = existing.SeriesColors.FirstOrDefault(c => c.Name == srcColor.Name);
                            if (targetColor != null)
                            {
                                targetColor.Color = srcColor.Color;
                                // Synchronize other properties if added in the future (e.g., Thickness)
                            }
                            else
                            {
                                existing.SeriesColors.Add(srcColor.Duplicate());
                            }
                        }

                        // Remove orphaned colors
                        var orphans = existing.SeriesColors.Where(c => !src.SeriesColors.Any(s => s.Name == c.Name)).ToList();
                        foreach (var o in orphans) existing.SeriesColors.Remove(o);
                        
                        // Directly trigger redraw without going through CalculateIndicatorsAsync
                        ChartViewModel.RenderTrigger++;
                    }
                }
                else
                {
                    // The changed indicator is not directly in ChartViewModel.Indicators (e.g. it is a Source Indicator).
                    // Gather this ID and any transitive source indicator IDs depending on it.
                    var changedIds = new HashSet<string> { message.Value.Id };
                    var registeredEntries = new List<CoreIndicatorSettings>();
                    if (_sourceIndicatorService != null) registeredEntries.AddRange(_sourceIndicatorService.GetSourceIndicators());
                    if (_dynamicPeriodDriverService != null) registeredEntries.AddRange(_dynamicPeriodDriverService.GetDynamicPeriodDrivers());
                    if (registeredEntries.Count > 0)
                    {
                        bool added;
                        do
                        {
                            added = false;
                            foreach (var s in registeredEntries)
                            {
                                if (!changedIds.Contains(s.Id) &&
                                    ((!string.IsNullOrEmpty(s.SourceIndicatorId) && changedIds.Contains(s.SourceIndicatorId)) ||
                                     (!string.IsNullOrEmpty(s.DynamicPeriodIndicatorId) && changedIds.Contains(s.DynamicPeriodIndicatorId))))
                                {
                                    changedIds.Add(s.Id);
                                    added = true;
                                }
                            }
                        } while (added);
                    }

                    // Find all active chart indicators depending on the changed source indicators
                    var dependents = ChartViewModel.Indicators
                        .Where(i => (!string.IsNullOrEmpty(i.SourceIndicatorId) && changedIds.Contains(i.SourceIndicatorId)) ||
                                    (!string.IsNullOrEmpty(i.DynamicPeriodIndicatorId) && changedIds.Contains(i.DynamicPeriodIndicatorId)))
                        .ToList();

                    if (dependents.Count > 0)
                    {
                        foreach (var dep in dependents)
                        {
                            int index = ChartViewModel.Indicators.IndexOf(dep);
                            if (index >= 0)
                            {
                                var clone = dep.Snapshot();
                                clone.MathematicalVersion++;
                                ChartViewModel.Indicators[index] = clone;
                            }
                        }
                    }
                }

                _layoutSaveScheduler.RequestSave(LayoutChangeReason.SelectionChanged);
            });
        }
    }

    public void RestoreDetachedTabs(WorkspaceSettings settings)
    {
        _detachedTabManager.Restore(settings);
    }

    /// <summary>
    /// Captures the current layout state into a WorkspaceSettings instance.
    /// </summary>
    public void CaptureWorkspaceSettings(WorkspaceSettings settings)
    {
        if (settings == null) return;

        // Capture layout
        settings.IsLeftPanelVisible = _stateStore.LeftPanel.IsVisible;
        settings.LeftPanelWidth = _stateStore.LeftPanel.IsVisible 
            ? (_stateStore.LeftPanel.WidthOrHeight > 0 ? _stateStore.LeftPanel.WidthOrHeight : LayoutConstants.DefaultLeftWidth) 
            : _stateStore.LeftPanel.LastSize;
        settings.IsLeftPanelPinned = _stateStore.LeftPanel.IsPinned;

        settings.IsRightPanelVisible = _stateStore.RightPanel.IsVisible;
        settings.RightPanelWidth = _stateStore.RightPanel.IsVisible 
            ? (_stateStore.RightPanel.WidthOrHeight > 0 ? _stateStore.RightPanel.WidthOrHeight : LayoutConstants.DefaultRightWidth) 
            : _stateStore.RightPanel.LastSize;
        settings.IsRightPanelPinned = _stateStore.RightPanel.IsPinned;

        settings.IsTopPanelVisible = _stateStore.TopPanel.IsVisible;
        settings.TopPanelHeight = _stateStore.TopPanel.IsVisible 
            ? (_stateStore.TopPanel.WidthOrHeight > 0 ? _stateStore.TopPanel.WidthOrHeight : LayoutConstants.DefaultTopHeight) 
            : _stateStore.TopPanel.LastSize;
        settings.IsTopPanelPinned = _stateStore.TopPanel.IsPinned;

        settings.IsBottomPanelVisible = _stateStore.BottomPanel.IsVisible;
        settings.BottomPanelHeight = _stateStore.BottomPanel.IsVisible 
            ? (_stateStore.BottomPanel.WidthOrHeight > 0 ? _stateStore.BottomPanel.WidthOrHeight : LayoutConstants.DefaultBottomHeight) 
            : _stateStore.BottomPanel.LastSize;
        settings.IsBottomPanelPinned = _stateStore.BottomPanel.IsPinned;

        // Capture tab indices
        settings.PanelSelectedTabIndices[nameof(PanelRegion.Left)] = LeftSelectedTabIndex;
        settings.PanelSelectedTabIndices[nameof(PanelRegion.Right)] = RightSelectedTabIndex;
        settings.PanelSelectedTabIndices[nameof(PanelRegion.Top)] = TopSelectedTabIndex;
        settings.PanelSelectedTabIndices[nameof(PanelRegion.Bottom)] = BottomSelectedTabIndex;

        // Capture active selection (Ticker & Timeframe & Period)
        settings.SelectedTicker = SelectedTicker;
        settings.SelectedTimeframe = SelectedTimeframe.ToString();
        settings.MaxCandleCount = ChartViewModel.MaxCandleCount;
        settings.ChartType = ChartViewModel.ChartType.ToString();
        settings.IsMainWindowVisible = ChartViewModel.IsMainWindowVisible;
        settings.IsSubWindowVisible = ChartViewModel.IsSubWindowVisible;
        settings.TickerListSidebarWidth = TickerListViewModel.SidebarGridWidth;
        settings.IsMetadataSyncEnabled = TickerListViewModel.IsMetadataSyncEnabled;
        settings.IsImputeMissingMetadataEnabled = TickerListViewModel.IsImputeMissingMetadataEnabled;
        settings.IsTimeSeriesSyncEnabled = TickerListViewModel.IsTimeSeriesSyncEnabled;
        settings.IsAutoSyncEnabled = TickerListViewModel.IsAutoSyncEnabled;
        settings.IsFullHistoryEnabled = TickerListViewModel.IsFullHistoryEnabled;
        settings.IsForcePeriodDownloadEnabled = TickerListViewModel.IsForcePeriodDownloadEnabled;
        settings.SyncDelayMinSeconds = TickerListViewModel.SyncDelayMinSeconds;
        settings.SyncDelayMaxSeconds = TickerListViewModel.SyncDelayMaxSeconds;
        settings.SyncDelaySeconds = TickerListViewModel.SyncDelayMaxSeconds; // Keep SyncDelaySeconds updated for backward compatibility
        settings.StartSyncPeriodYears = TickerListViewModel.StartSyncPeriodYears;

        // Capture collection configuration (Tab IDs)
        CapturePanelTabs(settings.PanelTabIds);

        // Capture watchlists
        settings.WatchlistProfiles = _watchlistManager.GetAllProfiles().ToList();

        // Capture tag filters
        if (TickerListViewModel != null)
        {
            settings.TagFilters = TickerListViewModel.ExportFilterSettings();
        }

        // Capture TickerList column visibility and widths
        settings.TickerListVisibleColumns = TickerListViewModel.ActiveColumns.Select(c => c.MemberName).ToList();
        settings.TickerListColumnWidths = TickerListViewModel.GetColumnWidths();

        // Capture selected watchlist
        settings.SelectedWatchlistId = TickerListViewModel.SelectedWatchlist?.Id;

        // Capture sort state
        var sort = TickerListViewModel.GetSortState();
        settings.TickerListSortColumn = sort.ColumnName;
        settings.TickerListSortDirection = sort.SortDirection;

        // Capture detached tabs
        _detachedTabManager.Capture(settings.DetachedTabs, ChartViewModel.Indicators);

        // Capture settings for panel charts (such as sync status, local configurations)
        settings.PanelCharts.Clear();
        var allPanelCharts = LeftPanelTabs.Concat(RightPanelTabs)
                                         .Concat(TopPanelTabs)
                                         .Concat(BottomPanelTabs)
                                         .ToList();
                                         
        foreach (var item in allPanelCharts)
        {
            if (item != null && item.ViewModel is ChartViewModel cvm)
            {
                var chartSettings = new StockAnalyzer.Core.Models.Settings.PanelChartSettings
                {
                    TabId = item.Id,
                    IsSyncEnabled = cvm.IsSyncEnabled,
                    Symbol = cvm.Symbol,
                    Timeframe = cvm.SelectedTimeFrame.ToString(),
                    MaxCandleCount = cvm.MaxCandleCount,
                    ChartType = cvm.ChartType.ToString(),
                    IsMainWindowVisible = cvm.IsMainWindowVisible,
                    IsSubWindowVisible = cvm.IsSubWindowVisible,
                    // Apply indicator fallback for internal panel charts too
                    Indicators = (cvm.Indicators.Count > 0 || !cvm.IsSyncEnabled) 
                        ? cvm.Indicators.Select(i => i.Clone()).ToList() 
                        : ChartViewModel.Indicators.Select(i => i.Clone()).ToList()
                };
                settings.PanelCharts.Add(chartSettings);
            }
        }
    }

    public void RestoreChartSettings(WorkspaceSettings settings)
    {
        if (settings == null) return;

        if (!string.IsNullOrEmpty(settings.ChartType) && 
            Enum.TryParse<ChartType>(settings.ChartType, out var type))
        {
            ChartViewModel.ChartType = type;
        }

        if (settings.MaxCandleCount > 0)
        {
            ChartViewModel.MaxCandleCount = settings.MaxCandleCount;
        }

        TickerListViewModel.IsMetadataSyncEnabled = settings.IsMetadataSyncEnabled;
        TickerListViewModel.IsImputeMissingMetadataEnabled = settings.IsImputeMissingMetadataEnabled;
        TickerListViewModel.IsTimeSeriesSyncEnabled = settings.IsTimeSeriesSyncEnabled;
        TickerListViewModel.IsAutoSyncEnabled = settings.IsAutoSyncEnabled;
        TickerListViewModel.IsFullHistoryEnabled = settings.IsFullHistoryEnabled;
        TickerListViewModel.IsForcePeriodDownloadEnabled = settings.IsForcePeriodDownloadEnabled;
        
        if (settings.SyncDelayMinSeconds >= 3.0m && settings.SyncDelayMinSeconds <= 60.0m)
        {
            TickerListViewModel.SyncDelayMinSeconds = settings.SyncDelayMinSeconds;
        }
        else
        {
            TickerListViewModel.SyncDelayMinSeconds = 3.0m;
        }

        if (settings.SyncDelayMaxSeconds >= 3.0m && settings.SyncDelayMaxSeconds <= 60.0m)
        {
            TickerListViewModel.SyncDelayMaxSeconds = settings.SyncDelayMaxSeconds;
        }
        else if (settings.SyncDelaySeconds >= 3.0m && settings.SyncDelaySeconds <= 60.0m)
        {
            TickerListViewModel.SyncDelayMaxSeconds = settings.SyncDelaySeconds;
        }
        else
        {
            TickerListViewModel.SyncDelayMaxSeconds = 5.0m;
        }

        if (settings.StartSyncPeriodYears >= 1 && settings.StartSyncPeriodYears <= 50)
        {
            TickerListViewModel.StartSyncPeriodYears = settings.StartSyncPeriodYears;
        }
        else
        {
            TickerListViewModel.StartSyncPeriodYears = 5;
        }
    }


    private void CapturePanelTabs(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> panelTabIds)
    {
        panelTabIds.Clear();
        panelTabIds[nameof(PanelRegion.Left)] = LeftPanelTabs.Select(t => t.Id).ToList();
        panelTabIds[nameof(PanelRegion.Right)] = RightPanelTabs.Select(t => t.Id).ToList();
        panelTabIds[nameof(PanelRegion.Top)] = TopPanelTabs.Select(t => t.Id).ToList();
        panelTabIds[nameof(PanelRegion.Bottom)] = BottomPanelTabs.Select(t => t.Id).ToList();
    }

    public void ApplyPanelTabs(WorkspaceSettings settings)
    {
        var panelTabIds = settings.PanelTabIds;
        if (panelTabIds.Count == 0)
        {
            // Fallback to default setup if no configuration is stored
            InitializePanelTabs();
            return;
        }

        RestoreTabsForPanel(LeftPanelTabs, panelTabIds, nameof(PanelRegion.Left), settings);
        RestoreTabsForPanel(RightPanelTabs, panelTabIds, nameof(PanelRegion.Right), settings);
        RestoreTabsForPanel(TopPanelTabs, panelTabIds, nameof(PanelRegion.Top), settings);
        RestoreTabsForPanel(BottomPanelTabs, panelTabIds, nameof(PanelRegion.Bottom), settings);

        /* Comment out force migration: This prevents the closed Allocation tab from forcefully reappearing upon every app launch
        bool isAllocationDetached = settings.DetachedTabs?.Any(d => d.TabId == "Allocation") == true;
        if (!isAllocationDetached && !BottomPanelTabs.Any(t => t.Id == "Allocation"))
        {
            var allocationTab = _panelTabFactory.CreateTab("Allocation");
            if (allocationTab != null)
            {
                var portfolioIdx = BottomPanelTabs.Select((t, i) => new { t.Id, i })
                                                 .FirstOrDefault(x => x.Id == "PortfolioSummary")?.i ?? -1;
                
                if (portfolioIdx != -1)
                    BottomPanelTabs.Insert(portfolioIdx + 1, allocationTab);
                else
                    BottomPanelTabs.Add(allocationTab);
            }
        }
        */
    }

    private void RestoreTabsForPanel(ObservableCollection<WorkspaceViewItem> collection, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> panelTabIds, string panelKey, WorkspaceSettings settings)
    {
        collection.Clear();
        if (panelTabIds.TryGetValue(panelKey, out var ids))
        {
            foreach (var id in ids)
            {
                var item = GetWorkspaceViewItemById(id);
                if (item != null)
                {
                    if (item.ViewModel is ChartViewModel cvm && settings != null && settings.PanelCharts != null)
                    {
                        var chartSettings = settings.PanelCharts.FirstOrDefault(p => p.TabId == id);
                        if (chartSettings != null)
                        {
                            cvm.IsSyncEnabled = chartSettings.IsSyncEnabled;
                            if (!cvm.IsSyncEnabled)
                            {
                                cvm.Symbol = chartSettings.Symbol ?? ChartViewModel.Symbol;
                                if (!string.IsNullOrEmpty(chartSettings.Timeframe) && Enum.TryParse<StockAnalyzer.Core.Models.TimeframeType>(chartSettings.Timeframe, out var tf)) 
                                    cvm.SelectedTimeFrame = tf;
                                if (chartSettings.MaxCandleCount > 0) 
                                    cvm.MaxCandleCount = chartSettings.MaxCandleCount;

                                if (!string.IsNullOrEmpty(chartSettings.ChartType) && Enum.TryParse<ChartType>(chartSettings.ChartType, out var ct))
                                    cvm.ChartType = ct;
                                cvm.IsMainWindowVisible = chartSettings.IsMainWindowVisible;
                                cvm.IsSubWindowVisible = chartSettings.IsSubWindowVisible;
                                    
                                _ = cvm.LoadDataCommand.ExecuteAsync(null);
                            }

                            // Restore indicators regardless of sync state
                            cvm.Indicators.Clear();
                            foreach (var indicator in chartSettings.Indicators)
                            {
                                StockAnalyzer.Core.Models.DefaultCoreIndicatorSettings.AutoHeal(indicator);
                                cvm.Indicators.Add(indicator);
                            }
                        }
                    }
                    collection.Add(item);
                }
            }
        }
    }

    private void BindTabToActiveChart(WorkspaceViewItem? item)
    {
        if (item?.ViewModel is DataWindowViewModel dw)
        {
            dw.SetChartViewModel(ChartViewModel);
        }
        else if (item?.ViewModel is DrawingToolSidebarViewModel sidebar)
        {
            sidebar.SetChartViewModel(ChartViewModel);
        }
        else if (item?.ViewModel is DrawingObjectsViewModel objects)
        {
            objects.SetChartViewModel(ChartViewModel);
        }
    }

    private WorkspaceViewItem? GetWorkspaceViewItemById(string id)
    {
        var item = _panelTabFactory.CreateTab(id);
        BindTabToActiveChart(item);
        return item;
    }

    public void Receive(OpenIndicatorPropertiesMessage message)
    {
        if (message.Value != null)
        {
            _ = _dialogService.ShowIndicatorPropertiesDialogAsync(message.Value, null, ChartViewModel.Indicators);
        }
    }

    public void Receive(TickerSelectedMessage message)
    {
        if (_isApplyingSettings) return; // Guard during startup/reloading
        
        _dispatcherService.Post(() => 
        {
            // Sync the local property so CaptureWorkspaceSettings picks it up
            _selectedTicker = message.Value;
            OnPropertyChanged(nameof(SelectedTicker));

            ChartViewModel.Symbol = message.Value;
            _ = ChartViewModel.LoadDataCommand.ExecuteAsync(null);
            _ = SaveLayoutAsync();
        });
    }

    /// <summary>
    /// Handles a Note card's "チャートを見る" request (spec section 6.2): switches the main chart
    /// to the Note's ticker/timeframe, waits for data to load, then centers the view on
    /// <see cref="NoteChartJumpRequestedMessage.AnchorDate"/> via
    /// <see cref="ViewModels.ChartViewModel.JumpToAnchorDate"/>.
    /// </summary>
    /// <summary>Auto-clear delay for the orphaned-attachment status-bar notice (spec section 4.5:
    /// this is a passive heads-up, not something that needs to stay until dismissed - the file list
    /// itself remains reachable any time via the trash dialog's "Orphaned Files" tab).</summary>
    private static readonly TimeSpan OrphanedAttachmentNoticeDuration = TimeSpan.FromSeconds(10);

    public void Receive(OrphanedAttachmentsDetectedMessage message)
    {
        var template = _localizationService.GetString("NoteTrash_OrphanedNotice") ?? "{0} orphaned attachment file(s) found. Click to review.";
        var text = string.Format(template, message.Count);
        OrphanedAttachmentNoticeText = text;

        _ = Task.Delay(OrphanedAttachmentNoticeDuration).ContinueWith(_ =>
        {
            _dispatcherService.Post(() =>
            {
                if (OrphanedAttachmentNoticeText == text)
                {
                    OrphanedAttachmentNoticeText = null;
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Clicking the status-bar notice opens the trash dialog directly to its "Orphaned
    /// Files" tab content (Step 90-1-20) rather than requiring a separate menu hunt.</summary>
    [RelayCommand]
    private async Task OpenOrphanedAttachmentNoticeAsync()
    {
        OrphanedAttachmentNoticeText = null;
        await _dialogService.ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Orphaned);
    }

    public void Receive(NoteChartJumpRequestedMessage message)
    {
        if (_isApplyingSettings) return;

        _dispatcherService.Post(() =>
        {
            _selectedTicker = message.Ticker;
            OnPropertyChanged(nameof(SelectedTicker));

            ChartViewModel.Symbol = message.Ticker;
            ChartViewModel.SelectedTimeFrame = message.Timeframe.ToTimeframeType();

            _ = ChartViewModel.LoadDataCommand.ExecuteAsync(null).ContinueWith(_ =>
            {
                // A second dispatcher hop guarantees this runs after LoadDataInternalAsync's own
                // internal dispatcher.Post that writes the freshly-fetched Candles (spec section
                // 6.2's "未ロード期間への対応": jump only once loading has actually finished).
                _dispatcherService.Post(() => ChartViewModel.JumpToAnchorDate(message.AnchorDate, message.Timeframe));
            }, TaskScheduler.Default);
        });
    }

    public void Receive(CurrentTickerRequestMessage message)
    {
        message.Reply(SelectedTicker ?? ChartViewModel.Symbol);
    }

    /// <summary>
    /// Handles a Tickers-tab Notes cell click: brings the Notes tab (NoteTimeline) to the front -
    /// activating it wherever it's already open, or opening a new one (defaulting to the Bottom
    /// panel, falling back to Right/Top/Left if Bottom is full) when it isn't open anywhere - and
    /// filters it to the clicked ticker.
    /// </summary>
    public void Receive(NavigateToNoteTimelineRequestedMessage message)
    {
        if (_isApplyingSettings) return;

        _dispatcherService.Post(() =>
        {
            var tab = FindOrOpenNoteTimelineTab();
            if (tab?.ViewModel is NoteTimelineViewModel noteTimeline)
            {
                // Replaces whatever filter was previously active with exactly this ticker (fix
                // request: multi-Ticker chip UI, Task F) - routes through SelectSuggestion so this
                // entry point also clears any active Watchlist/Tag/Hashtag/NoTicker dimension,
                // same mutual-exclusivity contract as picking a Ticker suggestion by hand.
                noteTimeline.SelectedTickerCodes.Clear();
                noteTimeline.SelectSuggestion(NoteScopeSuggestion.ForTicker(message.Ticker));
            }
        });
    }

    private WorkspaceViewItem? FindOrOpenNoteTimelineTab()
    {
        var panels = new (PanelRegion Region, ObservableCollection<WorkspaceViewItem> Tabs)[]
        {
            (PanelRegion.Left, LeftPanelTabs),
            (PanelRegion.Right, RightPanelTabs),
            (PanelRegion.Top, TopPanelTabs),
            (PanelRegion.Bottom, BottomPanelTabs),
        };

        foreach (var (region, tabs) in panels)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].ViewModel is NoteTimelineViewModel)
                {
                    ActivateTab(region, i);
                    return tabs[i];
                }
            }
        }

        // Not open anywhere: try to add it, preferring Bottom (Notes' conventional home alongside
        // DataWindow), then the other panels in order if Bottom is already at MaxPanelTabs capacity.
        var preferredOrder = new[] { PanelRegion.Bottom, PanelRegion.Right, PanelRegion.Top, PanelRegion.Left };
        foreach (var region in preferredOrder)
        {
            var tabs = region switch
            {
                PanelRegion.Left => LeftPanelTabs,
                PanelRegion.Right => RightPanelTabs,
                PanelRegion.Top => TopPanelTabs,
                _ => BottomPanelTabs,
            };
            if (tabs.Count >= LayoutConstants.MaxPanelTabs) continue;

            var newTab = _panelTabFactory.CreateTab("NoteTimeline");
            if (newTab == null) return null;

            tabs.Add(newTab);
            ActivateTab(region, tabs.Count - 1);
            return newTab;
        }

        return null;
    }

    private void ActivateTab(PanelRegion region, int index)
    {
        switch (region)
        {
            case PanelRegion.Left: LeftSelectedTabIndex = index; break;
            case PanelRegion.Right: RightSelectedTabIndex = index; break;
            case PanelRegion.Top: TopSelectedTabIndex = index; break;
            case PanelRegion.Bottom: BottomSelectedTabIndex = index; break;
        }
    }


    public void Receive(LayoutChangedMessage message)
    {
        if (!_isApplyingSettings && _isLoaded)
        {
            _ = SaveLayoutAsync();
        }
    }

    public void Receive(TearOffRequestMessage message)
    {
        if (message.Item == null) 
        {
            message.Reply(false);
            return;
        }

        if (_dispatcherService.CheckAccess())
        {
            ExecuteTearOff(message);
        }
        else
        {
            _dispatcherService.Post(static state => state.VM.ExecuteTearOff(state.Msg), (VM: this, Msg: message));
        }
    }

    private void ExecuteTearOff(TearOffRequestMessage message)
    {
        string? panelName = null;
        bool removed = false;
        if (LeftPanelTabs.Remove(message.Item)) { removed = true; panelName = "Left"; }
        else if (RightPanelTabs.Remove(message.Item)) { removed = true; panelName = "Right"; }
        else if (TopPanelTabs.Remove(message.Item)) { removed = true; panelName = "Top"; }
        else if (BottomPanelTabs.Remove(message.Item)) { removed = true; panelName = "Bottom"; }

        if (removed)
        {
            message.Item.IsDetached = true;
            message.Item.OriginalPanelName = panelName;
            _detachedTabManager.RegisterActiveDetachedTab(message.Item);
            
            if (_isLoaded && !_isApplyingSettings)
            {
                _logger.LogInformation("[TabTearOff] Removed from panel {Panel}: Tab={TabId}", panelName, message.Item.Id);
                _ = SaveLayoutAsync();
            }
        }
        
        message.Reply(removed);
    }

    public void Receive(RestoreRequestMessage message)
    {
        if (message.Value == null || _isExiting) return;

        _dispatcherService.Post(() =>
        {
            if (_isExiting) return;

            if (message.Value.IsIndependent)
            {
                // FR-70-12 Fix: Only remove/dispose if this is an explicit Redock (ContainerId is cleared by TearOffService).
                // If ContainerId is still set, it means the window was closed via the 'X' button or system close.
                // We keep it in DetachedTabManager so its state (Indicators, etc.) can be persisted during app shutdown.
                if (message.Value.ContainerId != null)
                {
                    _logger.LogInformation("[TabTearOff] Independent window closed for {TabId}. Keeping in workspace for persistence.", message.Value.Id);
                    return;
                }

                // Independent windows are just removed from the collection and disposed
                _detachedTabManager.RemoveActiveDetachedTab(message.Value);
                (message.Value.ViewModel as IDisposable)?.Dispose();
                
                if (_isLoaded && !_isApplyingSettings)
                {
                    _ = SaveLayoutAsync();
                }
                return;
            }

            message.Value.IsDetached = false;
            string panelName = message.Value.OriginalPanelName ?? "Bottom";
            
            ObservableCollection<WorkspaceViewItem> targetCollection = panelName switch
            {
                "Left" => LeftPanelTabs,
                "Right" => RightPanelTabs,
                "Top" => TopPanelTabs,
                _ => BottomPanelTabs
            };

            targetCollection.Add(message.Value);
            _detachedTabManager.RemoveActiveDetachedTab(message.Value);
            
            // Update selection
            if (panelName == "Left") LeftSelectedTabIndex = LeftPanelTabs.Count - 1;
            else if (panelName == "Right") RightSelectedTabIndex = RightPanelTabs.Count - 1;
            else if (panelName == "Top") TopSelectedTabIndex = TopPanelTabs.Count - 1;
            else BottomSelectedTabIndex = BottomPanelTabs.Count - 1;

            // Ensure panel is visible
            if (panelName == "Left") _stateStore.LeftPanel.IsVisible = true;
            else if (panelName == "Right") _stateStore.RightPanel.IsVisible = true;
            else if (panelName == "Top") _stateStore.TopPanel.IsVisible = true;
            else _stateStore.BottomPanel.IsVisible = true;
            
            _logger.LogInformation("[TabTearOff] Restored to {Panel} panel: Tab={TabId}", panelName, message.Value.Id);
            if (_isLoaded && !_isApplyingSettings)
            {
                _ = SaveLayoutAsync();
            }
        });
    }
    
    public void Receive(ChartSymbolChangedMessage message)
    {
        if (message.Sender == null || _isExiting) return;

        // Sync main window panel tab titles
        var panelItem = LeftPanelTabs.Concat(RightPanelTabs)
                                   .Concat(TopPanelTabs)
                                   .Concat(BottomPanelTabs)
                                   .FirstOrDefault(x => x.ViewModel == message.Sender);
        if (panelItem != null)
        {
            panelItem.Title = message.Value;
        }
    }

    public void PrepareExit()
    {
        _isExiting = true;
        
        // Cancel any pending debounced saves to ensure the final Flush is atomic
        _layoutSaveScheduler.Cancel();
    }

    /// <summary>
    /// Synchronous force-save for the shutdown path (X button / system close).
    /// Must be called AFTER PrepareExit() so that no further saves can sneak in.
    /// </summary>
    public void ForceSaveOnShutdown()
    {
        try
        {
            // Flush any pending drawing-object saves synchronously first. ChartViewModel.Dispose()
            // has an equivalent flush, but it depends on the DI container's cascading disposal
            // during desktop.ShutdownRequested, which is not always reached (or completed) before
            // the process exits. Running it here, during Window.OnClosing, is deterministic.
            // CommitPendingRename() must run first: closing the window while the Layers Panel's
            // inline rename TextBox is still focused never fires its LostFocus commit, so without
            // this the typed name would be silently discarded from the snapshot FlushPendingDrawings
            // is about to persist.
            DrawingObjectsViewModel?.CommitPendingRename();
            ChartViewModel?.FlushPendingDrawings();
            foreach (var item in LeftPanelTabs.Concat(RightPanelTabs).Concat(TopPanelTabs).Concat(BottomPanelTabs))
            {
                if (item?.ViewModel is ChartViewModel panelChartViewModel)
                {
                    panelChartViewModel.FlushPendingDrawings();
                }
                else if (item?.ViewModel is DrawingToolSidebarViewModel sidebarVm)
                {
                    sidebarVm.DrawingObjectsViewModel.CommitPendingRename();
                }
                else if (item?.ViewModel is DrawingObjectsViewModel objectsVm)
                {
                    objectsVm.CommitPendingRename();
                }
            }

            var settings = new WorkspaceSettings();
            CaptureWorkspaceSettings(settings);

            // Thread-safe / race-condition protected snapshot copy of indicators
            var indicators = ChartViewModel?.Indicators;
            if (indicators != null)
            {
                foreach (var indicator in indicators)
                {
                    var cloned = indicator.Clone();
                    if (cloned != null)
                    {
                        settings.Indicators.Add(cloned);
                    }
                }
            }

            _logger.LogInformation("Delegating synchronous force save to WorkspaceCoordinator during shutdown.");
            _workspaceCoordinator.ForceSaveSync(settings);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "ForceSaveOnShutdown failed");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"PanelChart_(\d+)")]
    private static partial System.Text.RegularExpressions.Regex PanelChartIdRegex();

    private void SyncPanelChartTitle(WorkspaceViewItem item, ChartViewModel vm, long id)
    {
        _dispatcherService.Post(() => 
        {
            if (item == null) return;
            string symbol = string.IsNullOrEmpty(vm.Symbol) ? "Chart" : vm.Symbol;
            string timeframe = vm.SelectedTimeFrame.ToString();
            item.Title = $"{symbol} ({timeframe}) [{id}]";
        });
    }

    static MainWindowViewModel()
    {
        // Initialize to thread-safe by default during startup
        InitializeChartInstanceCounter(0);
    }

    /// <summary>
    /// Global counter for chart instance IDs.
    /// Protected by atomic operations, completely eliminating ID duplication across multiple windows.
    /// </summary>
    public static void InitializeChartInstanceCounter(int maxExistingId)
    {
        if (maxExistingId < 0) throw new ArgumentOutOfRangeException(nameof(maxExistingId), "maxExistingId must be non-negative.");
        
        if (Interlocked.CompareExchange(ref _isCounterInitialized, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _chartInstanceCounter, maxExistingId);
        }
    }

    /// <summary>
    /// When a new chart VM is created, a unique instance ID is issued atomically.
    /// </summary>
    public static int GenerateNextChartId()
    {
        if (Volatile.Read(ref _isCounterInitialized) == 0)
        {
            throw new InvalidOperationException("Chart instance counter has not been initialized.");
        }

        int nextId = Interlocked.Increment(ref _chartInstanceCounter);
        if (nextId >= int.MaxValue - 1)
        {
            throw new OverflowException("Chart ID generation exceeded safe integer limits. Potential memory / logic leak.");
        }
        return nextId;
    }

    /// <summary>
    /// Legacy API compatibility helper.
    /// </summary>
    public static int GetNextChartInstanceId() => GenerateNextChartId();

    /// <summary>
    /// Scans all active and detached tabs to find the highest PanelChart ID
    /// and initializes the static counter to prevent collisions after restart.
    /// </summary>
    public void ScanAndInitializeChartInstanceCounter()
    {
        int maxId = 0;
        var regex = PanelChartIdRegex();

        var allItems = LeftPanelTabs.Concat(RightPanelTabs)
                                   .Concat(TopPanelTabs)
                                   .Concat(BottomPanelTabs)
                                   .Concat(_detachedTabManager.DetachedTabs);

        foreach (var item in allItems)
        {
            if (item.Id == null) continue;
            var match = regex.Match(item.Id);
            if (match.Success && match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out int id))
            {
                if (id > maxId) maxId = id;
            }
        }

        if (Volatile.Read(ref _isCounterInitialized) == 0)
        {
            InitializeChartInstanceCounter(maxId);
        }
        else
        {
            Interlocked.Exchange(ref _chartInstanceCounter, maxId);
        }
        _logger.LogInformation("Scanned and initialized ChartInstanceCounter to {MaxId}", maxId);
    }

    /// <summary>
    /// Legacy instance API compatibility helper.
    /// </summary>
    public void InitializeChartInstanceCounter() => ScanAndInitializeChartInstanceCounter();

    private void OnStateStorePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutStateStore.SelectedTabIndices))
        {
            OnPropertyChanged(nameof(LeftSelectedTabIndex));
            OnPropertyChanged(nameof(RightSelectedTabIndex));
            OnPropertyChanged(nameof(TopSelectedTabIndex));
            OnPropertyChanged(nameof(BottomSelectedTabIndex));
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_stateStore != null)
        {
            _stateStore.PropertyChanged -= OnStateStorePropertyChanged;
        }

        _watchlistManager.WatchlistsChanged -= OnWatchlistsChanged;
        
        if (TickerListViewModel != null)
        {
            TickerListViewModel.PropertyChanged -= OnTickerListPropertyChanged;
            TickerListViewModel.ActiveColumns.CollectionChanged -= OnTickerListActiveColumnsChanged;
        }

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }

    public struct AutoSaveSuspensionScope : IDisposable
    {
        private readonly MainWindowViewModel _viewModel;
        private bool _disposed;

        public AutoSaveSuspensionScope(MainWindowViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _disposed = false;
            Interlocked.Increment(ref _viewModel._autoSavePauseRefCount);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Interlocked.Decrement(ref _viewModel._autoSavePauseRefCount);
            }
        }
    }

    public AutoSaveSuspensionScope PauseAutoSave()
    {
        return new AutoSaveSuspensionScope(this);
    }
}

