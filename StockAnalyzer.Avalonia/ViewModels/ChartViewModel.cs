using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;

using System.Reactive.Subjects;
using StockAnalyzer.Avalonia.Common;

using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Strategies;
using StockAnalyzer.ZeroAllocation;
using System.Collections.ObjectModel;
using System.Threading;
// using Avalonia; // Removed for UI-Agnosticism
// using Avalonia.Threading; // Removed for UI-Agnosticism
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Theme;
using Microsoft.Extensions.DependencyInjection;


namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel : ViewModelBase, IChartRenderSettings, IRecipient<TickerSelectedMessage>, IRecipient<TickerDataRefreshedMessage>, IDisposable
{
    private static int _dtwRequestIdCounter = 0;
    private readonly IDataService _dataService;
    private readonly ITimeFrameManager? _timeFrameManager;
    private readonly IChartStrategyFactory? _strategyFactory; // Injected Factory
    private readonly IStockAnalyzerSettings? _settings; // Added settings
    private readonly Core.Services.MarketStructureService? _marketStructureService; // Used for DTW projection
    private readonly Core.Services.IDispatcherService _dispatcherService;
    private readonly Core.Services.IPredictionService? _predictionService;
    private readonly Core.Services.IPythonService? _pythonService;
    private readonly Core.Services.IMarketDataProvider? _marketDataProvider;
    private readonly Core.Services.IComparisonDataAligner? _comparisonDataAligner;
    private readonly Core.Services.IChartSettingsManager _chartSettingsManager;
    private readonly Core.Services.Analysis.IAnalysisPipelineService _analysisPipelineService;
    private readonly IMessenger _messenger;
    private CancellationTokenSource? _loadCts; // Cancellation for timeframe switching
    private CancellationTokenSource? _chartUpdateCts;
    private int _chartUpdateId = 0;

    private readonly Subject<ChartType> _displayModeSubject = new();
    
    /// <summary>
    /// Observable that emits the chart type whenever it changes.
    /// Used by IndicatorMediator to trigger recalculations.
    /// </summary>
    public IObservable<ChartType> DisplayModeChanges => _displayModeSubject;

    /// <summary>
    /// Controls whether the UI (e.g. indicator settings) is enabled.
    /// Disabled during long-running recalculations to prevent race conditions.
    /// </summary>
    [ObservableProperty]
    private bool _isUiEnabled = true;


    public IDialogService DialogService { get; set; }

    [ObservableProperty]
    private Core.Theme.IThemeManager _themeManager = default!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeaderText))]
    private string _symbol = StockAnalyzer.Core.ChartConstants.DefaultSymbol;


    [ObservableProperty]
    private TimeframeType _selectedTimeFrame = TimeframeType.Daily;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeaderText))]
    private ChartType _chartType = ChartType.Candlestick;

    public string TabHeaderText => ComputeTabHeaderLabel();

    private string ComputeTabHeaderLabel()
    {
        string symbol = string.IsNullOrEmpty(Symbol) ? "---" : Symbol;
        int maxLength = StockAnalyzer.Avalonia.Common.ChartConstants.TabHeaderMaxSymbolLength;
        
        bool needsPrefix = !IsSyncEnabled;
        int prefixLength = needsPrefix ? 1 : 0;
        
        int totalLogicalLength = prefixLength + symbol.Length;
        bool needsTruncation = totalLogicalLength > maxLength;
        int finalLength = needsTruncation ? maxLength : totalLogicalLength;

        return string.Create(finalLength, (symbol, needsPrefix, needsTruncation, prefixLength, finalLength), 
            static (span, state) =>
        {
            int currentIndex = 0;
            if (state.needsPrefix)
            {
                span[currentIndex++] = '★';
            }

            int charsToCopy = state.needsTruncation 
                ? state.finalLength - state.prefixLength - 1
                : state.symbol.Length;

            state.symbol.AsSpan(0, charsToCopy).CopyTo(span.Slice(currentIndex));
            currentIndex += charsToCopy;

            if (state.needsTruncation)
            {
                span[currentIndex] = '…';
            }
        });
    }

    [ObservableProperty]
    private int _renderTrigger;

    private int _accumulatedRenderReason;

    /// <summary>
    /// Requests a UI redraw by incrementing the RenderTrigger with a specific reason.
    /// Centralized point for all rendering requests.
    /// </summary>
    public void RequestRender(RenderReason reason)
    {
        Interlocked.Or(ref _accumulatedRenderReason, (int)reason);
        unchecked { Interlocked.Increment(ref _renderTrigger); }
        
        if (_dispatcherService.CheckAccess())
        {
            OnPropertyChanged(nameof(RenderTrigger));
        }
        else
        {
            _dispatcherService.Post(() => OnPropertyChanged(nameof(RenderTrigger)));
        }
    }


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDataCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private CoreCandleData? _currentHoveredCandle;

    private bool _isDisposed;

    [ObservableProperty]
    private IReadOnlyList<CoreCandleData> _candles = Array.Empty<CoreCandleData>();

    [ObservableProperty]
    private IReadOnlyList<CoreCandleData>? _convertedKagiCandles;

    [ObservableProperty]
    private PredictionResult? _currentPrediction;

    [ObservableProperty]
    private decimal _currentPrice;

    public bool HasData => Candles?.Count > 0;

    // Viewport State (Managed by Coordinator, exposed for Renderers)
    [ObservableProperty] private int _visibleStartIndex;
    [ObservableProperty] private int _visibleCandleCount = StockAnalyzer.Core.ChartConstants.DefaultVisibleCandleCount;

    [ObservableProperty] private StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint? _latestReverseWatchPoint;

    public ObservableCollection<CoreIndicatorSettings> Indicators { get; } = new();

    [ObservableProperty]
    private ObservableCollection<string> _comparisonTargets = new();

    [ObservableProperty]
    private SeriesColorIndex _seriesColorIndex = new();

    [ObservableProperty] private bool _showVolume = true;
    [ObservableProperty] private bool _showPriceLabels = true;
    [ObservableProperty] private bool _showCrosshair = true;
    [ObservableProperty] private bool _showLegend = true;
    [ObservableProperty] private bool _showHeaderInfo = true;
    
    // Step 68-1-4: Visibility Control (User preference, Default OFF as per policy)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveIsSubWindowVisible))]
    [NotifyPropertyChangedFor(nameof(IsDisplayOff))]
    private bool _isSubWindowVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayOff))]
    private bool _isMainWindowVisible = true;

    public bool IsDisplayOff => !IsMainWindowVisible && !EffectiveIsSubWindowVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveIsSubWindowVisible))]
    [NotifyPropertyChangedFor(nameof(IsDisplayOff))]
    private bool _canToggleIndicators = false; // Initialized for Candlestick

    private void VerifyStateInvariants()
    {
        bool expectedEffective = CanToggleIndicators && IsSubWindowVisible;
        if (EffectiveIsSubWindowVisible != expectedEffective)
        {
            throw new InvalidOperationException(
                $"Invariant violation detected in chart rendering state. " +
                $"EffectiveIsSubWindowVisible ({EffectiveIsSubWindowVisible}) " +
                $"does not match expected mathematical state ({expectedEffective}).");
        }

        bool expectedDisplayOff = !IsMainWindowVisible && !EffectiveIsSubWindowVisible;
        if (IsDisplayOff != expectedDisplayOff)
        {
            throw new InvalidOperationException(
                $"Invariant violation: Display status state corrupted. " +
                $"Mismatch between core logic and rendering engine signals.");
        }
    }

    partial void OnIsMainWindowVisibleChanged(bool value)
    {
        VerifyStateInvariants();
        RequestRender(RenderReason.ForceRedraw);
    }

    partial void OnIsSubWindowVisibleChanged(bool value)
    {
        VerifyStateInvariants();
        RequestRender(RenderReason.ForceRedraw);
    }

    partial void OnCanToggleIndicatorsChanged(bool value)
    {
        VerifyStateInvariants();
        RequestRender(RenderReason.ForceRedraw);
    }


    public IMessenger Messenger => _messenger;
    public IDispatcherService DispatcherService => _dispatcherService;

    // Effective visibility determines if the indicator sub-window should be rendered.
    // If the chart type supports indicators, we honor the user preference.
    public bool EffectiveIsSubWindowVisible => CanToggleIndicators ? IsSubWindowVisible : false;

    IReadOnlyList<string> IChartRenderSettings.ComparisonTargets => ComparisonTargets;
    SeriesColorIndex IChartRenderSettings.SeriesColorIndex => SeriesColorIndex;
    bool IChartRenderSettings.IsSubWindowVisible => EffectiveIsSubWindowVisible;
    bool IChartRenderSettings.InvertOscillator => InvertOscillator;
    double IChartRenderSettings.DefaultDrawingThickness => _chartSettingsManager?.Current.DefaultStrokeThickness ?? 1.0;
    bool IChartRenderSettings.CrosshairLabelVisible => _chartSettingsManager?.Current.CrosshairLabelVisible ?? true;
    bool IChartRenderSettings.ShowTickerInsteadOfValue => ShowTickerInsteadOfValue;
    public event Action? SettingsChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeaderText))]
    private bool _isSyncEnabled = true;

    partial void OnIsSyncEnabledChanged(bool value)
    {
        if (value)
        {
            // Sync was turned ON: Immediately fetch current state from Main Chart
            PerformInitialSync();
        }
    }

    private void PerformInitialSync()
    {
        var request = new CurrentTickerRequestMessage();
        _messenger.Send(request);
        if (request.HasReceivedResponse)
        {
            var newSymbol = request.Response;
            if (!string.IsNullOrEmpty(newSymbol) && !string.Equals(Symbol, newSymbol, StringComparison.OrdinalIgnoreCase))
            {
                Symbol = newSymbol;
                _ = LoadDataCommand.ExecuteAsync(null);
            }
        }
        else
        {
            // Fallback for Main Chart at startup (since MainWindowViewModel may not have registered yet)
            // Or if no one responds, we still need to load data for our default symbol
            if (CanLoadData())
            {
                _ = LoadDataCommand.ExecuteAsync(null);
            }
        }
    }

    // Zero-Allocation collections for ComboBoxes
    public static IReadOnlyList<TimeframeType> TimeframeOptions { get; } = new[] 
    { 
        TimeframeType.Daily, 
        TimeframeType.Weekly, 
        TimeframeType.Monthly 
    };
    public static IReadOnlyList<ChartType> ChartTypeOptions { get; } = Enum.GetValues<ChartType>();
    
    // Tickers will be populated from data provider
    [ObservableProperty]
    private ObservableCollection<string> _availableTickers = new();



    public void Receive(TickerSelectedMessage message)
    {
        if (IsSyncEnabled && !string.IsNullOrEmpty(message.Value) && !string.Equals(Symbol, message.Value, StringComparison.OrdinalIgnoreCase))
        {
            Symbol = message.Value;
        }
    }

    /// <summary>
    /// Handles bulk sync completion notifications from TickerListViewModel.
    /// When the currently displayed symbol's data is refreshed via MultiSync,
    /// the chart is reloaded to display the latest candles.
    /// </summary>
    public void Receive(TickerDataRefreshedMessage message)
    {
        if (!string.IsNullOrEmpty(message.Value)
            && string.Equals(Symbol, message.Value, StringComparison.OrdinalIgnoreCase))
        {
            _dispatcherService.Post(async () => await LoadDataAsync());
        }
    }

    [RelayCommand]
    private async Task OpenIndicatorSettingsAsync()
    {
        await DialogService.ShowIndicatorSettingsDialogAsync(Indicators, ApplyIndicators);
    }

    [RelayCommand]
    private void SetPriceScale(PriceScaleType scaleType)
    {
        PriceScale = scaleType;
    }

    [RelayCommand]
    private void ToggleInvertOscillator()
    {
        InvertOscillator = !InvertOscillator;
    }

    [RelayCommand]
    private Task SyncCurrentSymbolAsync()
    {
        var symbol = Symbol;
        if (string.IsNullOrEmpty(symbol) || _pythonService == null || _settings == null) return Task.CompletedTask;

        var syncService = App.Current?.Services?.GetService(typeof(ITickerSyncService)) as ITickerSyncService;
        if (syncService == null) return Task.CompletedTask;

        return syncService.SyncSingleTickerAsync(symbol, () => LoadDataAsync());
    }

    private void ApplyIndicators(IEnumerable<CoreIndicatorSettings> newIndicators)
    {
        if (!_dispatcherService.CheckAccess())
        {
            _dispatcherService.Post(() => ApplyIndicators(newIndicators));
            return;
        }
        Indicators.Clear();
        foreach (var indicator in newIndicators)
        {
            Indicators.Add(indicator.Clone());
        }
        RequestRender(RenderReason.DataChanged);
    }

    [RelayCommand]
    private void ToggleSubWindow()
    {
        IsSubWindowVisible = !IsSubWindowVisible;
    }

    [RelayCommand]
    private async Task ShowIndicatorPropertiesAsync(CoreIndicatorSettings? indicator)
    {
        if (indicator == null) return;
        await DialogService.ShowIndicatorPropertiesDialogAsync(indicator, ApplySingleIndicator);
    }

    private void ApplySingleIndicator(CoreIndicatorSettings changedIndicator)
    {
        if (changedIndicator == null) throw new ArgumentNullException(nameof(changedIndicator));
        if (Indicators == null) return;

        if (!_dispatcherService.CheckAccess())
        {
            _dispatcherService.Post(() => ApplySingleIndicator(changedIndicator));
            return;
        }

        var existing = Indicators.FirstOrDefault(i => i != null && i.Id == changedIndicator.Id);
        if (existing != null)
        {
            bool isMathChange = changedIndicator.MathematicalVersion > 0;
            
            if (isMathChange)
            {
                int index = Indicators.IndexOf(existing);
                if (index >= 0)
                {
                    changedIndicator.MathematicalVersion = existing.MathematicalVersion + changedIndicator.MathematicalVersion;
                    Indicators[index] = changedIndicator;
                }
            }
            else
            {
                existing.Color = changedIndicator.Color;
                existing.Thickness = changedIndicator.Thickness;
                existing.Style = changedIndicator.Style;
                existing.IsEnabled = changedIndicator.IsEnabled;
                existing.IsOverlay = changedIndicator.IsOverlay;
                existing.OverlayPanelId = changedIndicator.OverlayPanelId;

                if (existing.SeriesColors != null && changedIndicator.SeriesColors != null)
                {
                    for (int i = 0; i < existing.SeriesColors.Count && i < changedIndicator.SeriesColors.Count; i++)
                    {
                        existing.SeriesColors[i].Color = changedIndicator.SeriesColors[i].Color;
                    }
                }
                RequestRender(RenderReason.VisualChanged);
            }
        }
    }

    public void Dispose()
    {
        _isDisposed = true;

        _messenger?.UnregisterAll(this);

        if (_chartSettingsManager != null)
        {
            _chartSettingsManager.SettingsChanged -= OnGlobalSettingsChanged;
        }

        if (_themeManager != null)
        {
            _themeManager.PropertyChanged -= OnThemeManagerPropertyChanged;
        }

        if (Indicators != null)
        {
            Indicators.CollectionChanged -= OnIndicatorsCollectionChanged;
            
            // Critical Fix: Unsubscribe from individual indicator settings to prevent reference leaks
            foreach (var item in _subscribedIndicators)
            {
                item.PropertyChanged -= IndicatorSetting_PropertyChanged;
            }
            _subscribedIndicators.Clear();
        }

        if (ComparisonTargets != null)
        {
            ComparisonTargets.CollectionChanged -= OnComparisonTargetsChanged;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _chartUpdateCts?.Cancel();
        _chartUpdateCts?.Dispose();
        _chartUpdateCts = null;

        _indicatorCalcCts?.Cancel();
        _indicatorCalcCts?.Dispose();
        _indicatorCalcCts = null;

        _displayModeSubject.OnCompleted();
        _displayModeSubject.Dispose();

        // Clear collections to release references
        Candles = Array.Empty<CoreCandleData>();
        
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    private ComparisonMode _comparisonMode = ComparisonMode.Performance;

    partial void OnComparisonModeChanged(ComparisonMode value) => RequestRender(RenderReason.DataChanged);

    [ObservableProperty]
    private int _comparisonZScorePeriod = 20;

    partial void OnComparisonZScorePeriodChanged(int value) => RequestRender(RenderReason.DataChanged);

    partial void OnComparisonTargetsChanged(ObservableCollection<string> value)
    {
        if (value != null)
        {
            // Ensure no duplicate or self-comparison on initial assignment
            var toRemove = value.FirstOrDefault(t => string.Equals(t, Symbol, StringComparison.OrdinalIgnoreCase));
            if (toRemove != null) value.Remove(toRemove);

            value.CollectionChanged += OnComparisonTargetsChanged;
            CalculateComparisonDataAsync();
        }
    }

    /// <summary>
    /// Triggered by CommunityToolkit.Mvvm when the Symbol property changes.
    /// Ensures that the new base symbol is not present in the comparison targets.
    /// </summary>
    partial void OnSymbolChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // Security validation to prevent Directory Traversal or injection
        var decodedSymbol = System.Web.HttpUtility.UrlDecode(value);
        var regex = new System.Text.RegularExpressions.Regex(@"^[A-Z0-9\-\.]{1,15}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!regex.IsMatch(decodedSymbol))
        {
            Symbol = _previousSymbol ?? StockAnalyzer.Core.ChartConstants.DefaultSymbol;
            throw new System.Security.SecurityException("Invalid symbol format detected.");
        }

        // Broadcast change for title synchronization (Phase 3 remediation).
        // Deferred via dispatcher: sending this synchronously here re-enters the messenger
        // (tab title sync in MainWindowViewModel/DetachedTabManager) inside the Symbol
        // setter's own call stack, which can race with ViewSwitcher's cached-view
        // reattachment and trigger Avalonia's "Visual already has a parent" crash.
        // Symbol itself stays synchronous so startup code (TryLoadDefaultWorkspaceAsync)
        // that reads Symbol right after workspace restoration still sees the correct value.
        _dispatcherService.Post(() =>
        {
            if (_isDisposed) return;
            _messenger.Send(new ChartSymbolChangedMessage(this, value));
        });

        _previousSymbol = value;

        _ = LoadDataCommand.ExecuteAsync(null);
    }

    [ObservableProperty]
    private IReadOnlyDictionary<string, IIndicatorResult>? _indicatorResults;

    private int _indicatorCalcId = 0;
    private Dictionary<string, long> _lastMathVersions = new();
    private int _lastCandleCount = 0;
    private string? _lastSymbol;
    private string? _previousSymbol;
    private TimeframeType? _lastTimeFrame;
    private ChartType? _lastChartType;
    private decimal _lastEffectiveSize = 0m;
    private int _lastAdapterCount = 0;
    private CancellationTokenSource? _indicatorCalcCts;

    // Buffer reuse to prevent heap allocations in hot-path (ZeroAllocation)
    private readonly List<CoreIndicatorSettings> _subscribedIndicators = new();

    private void CalculateIndicatorsAsync()
    {
        var currentId = Interlocked.Increment(ref _indicatorCalcId);
        
        // Debounce: Cancel previous pending task if any
        _indicatorCalcCts?.Cancel();
        _indicatorCalcCts = new CancellationTokenSource();
        var cts = _indicatorCalcCts;

        var snapshotCandles = Candles;
        var snapshotSettings = Indicators.ToList(); // Still need a stable list for BG calculation
        
        var chartType = ChartType;
        var symbol = Symbol;
        var timeFrame = SelectedTimeFrame;

        var effectiveSize = chartType switch
        {
            ChartType.Renko => EffectiveRenkoBrickSize,
            ChartType.PointAndFigure => EffectivePnfBoxSize,
            ChartType.Kagi => EffectiveKagiReversalAmount,
            _ => 0m
        };
        
        // Capture active adapter references and dispatcher before passing into Task
        var renkoAdapter = _renkoDataAdapter;
        var pnfAdapter = _pnfDataAdapter;
        var kagiAdapter = _kagiDataAdapter;
        var dispatcher = _dispatcherService;

        if (snapshotCandles.Count == 0 || snapshotSettings.Count == 0)
        {
            dispatcher.Post<(ChartViewModel vm, int id)>(static (state) => 
            {
                if (state.id != state.vm._indicatorCalcId || state.vm._isDisposed) return;
                
                state.vm.IndicatorResults = new Dictionary<string, IIndicatorResult>();
                state.vm.RequestRender(RenderReason.DataChanged);
                state.vm._messenger.Send(new StockAnalyzer.Avalonia.Common.IndicatorResultsUpdatedMessage());
            }, (this, currentId));
            return;
        }

        _ = Task.Run(async () => 
        {
            // Debounce delay to batch rapid property changes (e.g. slider drag)
            try { await Task.Delay(StockAnalyzer.Avalonia.Common.ChartConstants.IndicatorCalculationDebounceDelay, cts.Token); }
            catch (OperationCanceledException) { return; }

            IReadOnlyList<CoreCandleData> targetCandles = snapshotCandles;
            CoreCandleData[]? pooledArray = null;

            try
            {
                // [Data Adapter Extraction Logic]
                StockAnalyzer.ZeroAllocation.IZeroAllocChartDataAdapter? activeAdapter = chartType switch
                {
                    ChartType.Renko => renkoAdapter,
                    ChartType.PointAndFigure => pnfAdapter,
                    ChartType.Kagi => kagiAdapter,
                    _ => null
                };

                if (activeAdapter != null && activeAdapter.Count > 0)
                {
                    var count = activeAdapter.Count;
                    pooledArray = System.Buffers.ArrayPool<CoreCandleData>.Shared.Rent(count);
                    
                    var tsMem = activeAdapter.Timestamps;
                    var opensMem = activeAdapter.Opens;
                    var highsMem = activeAdapter.Highs;
                    var lowsMem = activeAdapter.Lows;
                    var closesMem = activeAdapter.Closes;
                    var volsMem = activeAdapter.Volumes;
                    
                    for (int i = 0; i < count; i++)
                    {
                        pooledArray[i] = new CoreCandleData(
                            tsMem.Span[i], 
                            opensMem.Span[i], 
                            highsMem.Span[i], 
                            lowsMem.Span[i], 
                            closesMem.Span[i], 
                            volsMem.Span[i]);
                    }
                    targetCandles = new ArraySegment<CoreCandleData>(pooledArray, 0, count);
                }
                
                // 1. PERFORMANCE OPTIMIZATION: Skip math if only visual properties (Color, Thickness) changed.
                bool needsFullRecalc = true;
                int adapterCount = activeAdapter?.Count ?? 0;
                if (snapshotCandles.Count == _lastCandleCount && 
                    symbol == _lastSymbol && 
                    timeFrame == _lastTimeFrame &&
                    chartType == _lastChartType &&
                    effectiveSize == _lastEffectiveSize &&
                    adapterCount == _lastAdapterCount &&
                    snapshotSettings.Count == _lastMathVersions.Count)
                {
                    bool mathChanged = false;
                    foreach (var s in snapshotSettings)
                    {
                        if (!_lastMathVersions.TryGetValue(s.Id, out var lastVersion) || s.MathematicalVersion != lastVersion)
                        {
                            mathChanged = true;
                            break;
                        }
                    }
                    if (!mathChanged) needsFullRecalc = false;
                }

                IReadOnlyDictionary<string, IIndicatorResult> results;
                if (!needsFullRecalc && IndicatorResults != null)
                {
                    // OPTIMIZED: Re-use previous math results, only visual properties changed.
                    results = IndicatorResults;
                }
                else
                {
                    results = await _analysisPipelineService.CalculateIndicatorsAsync(targetCandles, snapshotSettings);
                    
                    // Update cache on successful full recalc
                    var newVersions = new Dictionary<string, long>();
                    foreach (var s in snapshotSettings) newVersions[s.Id] = s.MathematicalVersion;
                    _lastMathVersions = newVersions;
                    _lastCandleCount = snapshotCandles.Count;
                    _lastSymbol = symbol;
                    _lastTimeFrame = timeFrame;
                    _lastChartType = chartType;
                    _lastEffectiveSize = effectiveSize;
                    _lastAdapterCount = adapterCount;
                }
                
                // ID check moved to dispatcher callback to avoid 'this' capture in Task.Run closure
                dispatcher.Post<(ChartViewModel vm, int id, IReadOnlyDictionary<string, IIndicatorResult> results, CancellationTokenSource cts)>(static (state) => 
                {
                    if (state.id != state.vm._indicatorCalcId || state.cts.IsCancellationRequested || state.vm._isDisposed) return;
                    
                    state.vm.IndicatorResults = state.results;
                    state.vm.RequestRender(RenderReason.DataChanged); // Trigger redraw
                    state.vm._messenger.Send(new StockAnalyzer.Avalonia.Common.IndicatorResultsUpdatedMessage());
                }, (this, currentId, results, cts));
            }
            catch (OperationCanceledException)
            {
                // Expected behavior during rapid updates or timeframe switching
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Indicator calculation pipeline error: {ex}");
            }
            finally
            {
                if (pooledArray != null)
                {
                    System.Buffers.ArrayPool<CoreCandleData>.Shared.Return(pooledArray);
                }
            }
        });
    }

    [ObservableProperty]
    private ComparisonAlignedData? _comparisonData;

    /// <summary>
    /// The latest data snapshot used for rendering.
    /// Updated by ChartDataCoordinator.
    /// </summary>
    public ChartDataSnapshot? CurrentSnapshot { get; set; }

    private int _comparisonCalcId = 0;

    private void CalculateComparisonDataAsync()
    {
        var currentId = Interlocked.Increment(ref _comparisonCalcId);
        
        var primarySymbol = Symbol;
        var targets = ComparisonTargets?.ToArray() ?? Array.Empty<string>();
        var timeFrame = SelectedTimeFrame;
        var candleCount = MaxCandleCount; 

        // Capture services to avoid 'this' capture in Task.Run closure
        var alignerService = _comparisonDataAligner;
        var dataService = _dataService;
        var dispatcher = _dispatcherService;
        
        if (string.IsNullOrEmpty(primarySymbol) || ChartType != ChartType.RelativePerformance)
        {
             dispatcher.Post<(ChartViewModel vm, int id)>(static (state) => 
             {
                 if (state.id != state.vm._comparisonCalcId) return;
                 state.vm.ComparisonData = null;
             }, (this, currentId));
             return;
        }

        _ = Task.Run(async () => 
        {
            try
            {
                var aligner = alignerService ?? new ComparisonDataAligner(dataService);
                
                // Map TimeframeType to TimeFrame (Using Extension Method)
                TimeFrame coreTimeFrame = timeFrame.ToCoreTimeFrame();

                var data = await aligner.AlignAsync(primarySymbol, targets, coreTimeFrame, candleCount);
                
                // ID check moved to dispatcher callback to avoid 'this' capture in Task.Run closure
                dispatcher.Post<(ChartViewModel vm, int id, ComparisonAlignedData data)>(static (state) => 
                {
                    if (state.id != state.vm._comparisonCalcId || state.vm._isDisposed) return;
                    state.vm.ComparisonData = state.data;
                    state.vm.RequestRender(RenderReason.DataChanged);
                }, (this, currentId, data));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Comparison data alignment error: {ex}");
                dispatcher.Post<(ChartViewModel vm, int id)>(static (state) => 
                {
                    if (state.id != state.vm._comparisonCalcId) return;
                    state.vm.ComparisonData = null;
                }, (this, currentId));
            }
        });
    }



    private void OnIndicatorsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _subscribedIndicators)
            {
                item.PropertyChanged -= IndicatorSetting_PropertyChanged;
            }
            _subscribedIndicators.Clear();
        }
        else
        {
            // For Replace action, if the instance is the same, avoid re-subscribing 
            // to prevent Add then Remove from the same instance which leaves it without a handler.
            bool isSameInstanceReplace = e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace 
                                       && e.NewItems != null && e.OldItems != null 
                                       && e.NewItems.Count > 0 && e.OldItems.Count > 0
                                       && ReferenceEquals(e.NewItems[0], e.OldItems[0]);

            if (!isSameInstanceReplace)
            {
                if (e.OldItems != null)
                {
                    foreach (CoreIndicatorSettings item in e.OldItems)
                    {
                        item.PropertyChanged -= IndicatorSetting_PropertyChanged;
                        _subscribedIndicators.Remove(item);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (CoreIndicatorSettings item in e.NewItems)
                    {
                        item.PropertyChanged += IndicatorSetting_PropertyChanged;
                        _subscribedIndicators.Add(item);
                    }
                }
            }
        }
        
        CalculateIndicatorsAsync();
        RequestRender(RenderReason.DataChanged); // Force immediate layout sync
    }

    private void IndicatorSetting_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only trigger full recalculation for mathematical parameter changes.
        // Visual-only property changes (Color, Thickness, Style) do NOT need action here.
        // Rendering for visual changes is handled by:
        //   - MainWindowViewModel.Receive → direct RenderTrigger++ (dialog edits)
        //   - OnIndicatorsCollectionChanged → CalculateIndicatorsAsync → RenderTrigger++ (init/add/remove)
        if (e.PropertyName == nameof(CoreIndicatorSettings.MathematicalVersion) ||
            e.PropertyName == nameof(CoreIndicatorSettings.IsEnabled))
        {
            CalculateIndicatorsAsync();
            RequestRender(RenderReason.DataChanged); // SYNC: Ensure UI layout and panels update immediately
        }
    }


    // Chart Settings
    
    private readonly Dictionary<ChartType, (float Top, float Bottom, float Right)> _chartMargins = new();
    private bool _isUpdatingMargins;

    [ObservableProperty]
    private float _chartMarginTop = StockAnalyzer.Core.ChartConstants.MarginTop;

    partial void OnChartMarginTopChanged(float value)
    {
        SaveCurrentMargins();
        UpdateChartData();
    }

    [ObservableProperty]
    private float _chartMarginBottom = StockAnalyzer.Core.ChartConstants.MarginBottom;

    partial void OnChartMarginBottomChanged(float value)
    {
        SaveCurrentMargins();
        UpdateChartData();
    }

    [ObservableProperty]
    private float _chartMarginRight = StockAnalyzer.Core.ChartConstants.MarginRight;

    partial void OnChartMarginRightChanged(float value)
    {
        SaveCurrentMargins();
        UpdateChartData();
        RecalculateFutureOffset();
    }

    private void SaveCurrentMargins()
    {
        if (!_isUpdatingMargins && ChartType != ChartType.ReverseWatch)
        {
            _chartMargins[ChartType] = (ChartMarginTop, ChartMarginBottom, ChartMarginRight);
        }
    }

    // Chart Type Specific Color Settings

    // --- Chart Color Customization (Relocated from Theme Settings) ---
    [ObservableProperty] private HsvData _bullishColor = new(1, 120, 1, 1); // Default Green
    [ObservableProperty] private HsvData _bearishColor = new(1, 0, 1, 1);   // Default Red
    [ObservableProperty] private HsvData _neutralColor = new(1, 0, 0, 0.5); // Default Gray

    [ObservableProperty] private HsvData _heikinBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _heikinBearishColor = new(1, 0, 1, 1);

    [ObservableProperty] private HsvData _ohlcBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _ohlcBearishColor = new(1, 0, 1, 1);

    [ObservableProperty] private HsvData _lineChartColor = new(1, 210, 1, 1); // DodgerBlue approx
    [ObservableProperty] private HsvData _areaChartColor = new(1, 210, 1, 1);

    [ObservableProperty] private bool _showLineMarkers = false;
    partial void OnShowLineMarkersChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty] private bool _showAreaMarkers = false;
    partial void OnShowAreaMarkersChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty] private HsvData _renkoBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _renkoBearishColor = new(1, 0, 1, 1);

    [ObservableProperty] private HsvData _pnfBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _pnfBearishColor = new(1, 0, 1, 1);

    [ObservableProperty] private HsvData _kagiBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _kagiBearishColor = new(1, 0, 1, 1);

    [ObservableProperty] private HsvData _threeLineBreakBullishColor = new(1, 120, 1, 1);
    [ObservableProperty] private HsvData _threeLineBreakBearishColor = new(1, 0, 1, 1);
    [ObservableProperty] private bool _showThreeLineBreakReversalLine = true;
    [ObservableProperty] private bool _showThreeLineBreakReversalPrice = true;
    [ObservableProperty] private HsvData _threeLineBreakReversalLineColor = new(1, 15, 1, 1); // Default OrangeRed approx

    // Chart Type Specific Settings
    [ObservableProperty]
    private decimal _effectiveRenkoBrickSize = StockAnalyzer.Core.ChartConstants.DefaultRenkoBrickSize;

    [ObservableProperty]
    private decimal _effectivePnfBoxSize = StockAnalyzer.Core.ChartConstants.DefaultPnfBoxSize;

    [ObservableProperty]
    private decimal _effectiveKagiReversalAmount = StockAnalyzer.Core.ChartConstants.DefaultKagiReversalAmount;
    
    [ObservableProperty]
    private ChartSizingMode _kagiReversalMode = ChartSizingMode.AutoAtr;

    [ObservableProperty]
    private decimal _kagiReversalPercent = StockAnalyzer.Core.ChartConstants.DefaultKagiReversalPercent; // 1% (user requested default)

    // Renko
    [ObservableProperty] private ChartSizingMode _renkoSizingMode = ChartSizingMode.AutoAtr;
    [ObservableProperty] private decimal _renkoBrickSize = StockAnalyzer.Core.ChartConstants.DefaultRenkoBrickSize;
    [ObservableProperty] private decimal _renkoBrickPercent = StockAnalyzer.Core.ChartConstants.DefaultRenkoBrickPercent;
    [ObservableProperty] private decimal _renkoAtrMultiplier = StockAnalyzer.Core.ChartConstants.DefaultAtrMultiplier;
    [ObservableProperty] private int _renkoAtrPeriod = StockAnalyzer.Core.ChartConstants.DefaultAtrPeriod;
    [ObservableProperty] private int _renkoReversal = 2;
    [ObservableProperty] private int _renkoRoundingMode = 0;
    [ObservableProperty] private int _renkoFallbackMode = 0;
    [ObservableProperty] private float _renkoGhostProjectionFontSize = 10.0f;
    [ObservableProperty] private bool _renkoShowGhostLabelsOnHoverOnly = false;
    
    // Renko Breakout Pattern Toggles
    [ObservableProperty] private bool _renkoShowMultiWavePatterns = false;
    [ObservableProperty] private bool _renkoShowGhostProjections = false;
    
    // P&F
    [ObservableProperty] private ChartSizingMode _pnfSizingMode = ChartSizingMode.AutoAtr;
    [ObservableProperty] private decimal _pnfBoxSize = StockAnalyzer.Core.ChartConstants.DefaultPnfBoxSize; // User setting
    [ObservableProperty] private decimal _pnfAtrMultiplier = StockAnalyzer.Core.ChartConstants.DefaultAtrMultiplier;
    [ObservableProperty] private int _pnfAtrPeriod = StockAnalyzer.Core.ChartConstants.DefaultAtrPeriod;
    [ObservableProperty] private int _pnfRoundingMode = 0;
    [ObservableProperty] private int _pnfFallbackMode = 0;
    [ObservableProperty] private int _pnfReversal = 3;
    [ObservableProperty] private decimal _pnfBoxPercent = 1.0m;
    [ObservableProperty] private float _pnfGhostProjectionFontSize = 10.0f;
    [ObservableProperty] private bool _pnfShowGhostLabelsOnHoverOnly = false;
    
    // P&F Breakout Pattern Toggles
    [ObservableProperty] private bool _pnfShowDoubleBreakout = false;
    [ObservableProperty] private bool _pnfShowTripleBreakout = false;
    [ObservableProperty] private bool _pnfShowTrendlineBreakout = false;
    [ObservableProperty] private bool _pnfShowTriangleBreakout = false;
    [ObservableProperty] private bool _pnfShowCatapultBreakout = false;
    
    // P&F Multi-Wave Toggles
    [ObservableProperty] private bool _pnfShowMultiWavePatterns = false;
    [ObservableProperty] private bool _pnfShowGhostProjections = false;
    
    // Kagi
    [ObservableProperty] private decimal _kagiReversalAmount = StockAnalyzer.Core.ChartConstants.DefaultKagiReversalAmount; // User setting
    [ObservableProperty] private decimal _kagiAtrMultiplier = StockAnalyzer.Core.ChartConstants.DefaultAtrMultiplier;
    [ObservableProperty] private int _kagiAtrPeriod = StockAnalyzer.Core.ChartConstants.DefaultAtrPeriod;
    [ObservableProperty] private int _kagiRoundingMode = 0;
    [ObservableProperty] private int _kagiInitialColumn = 0;
    [ObservableProperty] private int _kagiFallbackMode = 0;
    [ObservableProperty] private float _kagiLineThickness = 1.0f;
    [ObservableProperty] private float _kagiGhostProjectionFontSize = 10.0f;
    [ObservableProperty] private bool _kagiShowGhostLabelsOnHoverOnly = false;
    
    // Kagi Multi-Wave Toggles
    [ObservableProperty] private bool _kagiShowMultiWavePatterns = false;
    [ObservableProperty] private bool _kagiShowGhostProjections = false;

    [ObservableProperty]
    private int _threeLineBreakLineCount = StockAnalyzer.Core.ChartConstants.DefaultThreeLineBreakLineCount;

    [ObservableProperty]
    private decimal _threeLineBreakMinMove = 0m;

    // TLB Multi-Wave Toggles
    [ObservableProperty] private bool _tlbShowMultiWavePatterns = false;
    partial void OnTlbShowMultiWavePatternsChanged(bool value) => UpdateChartData();
    
    [ObservableProperty] private bool _tlbShowGhostProjections = false;
    partial void OnTlbShowGhostProjectionsChanged(bool value) => UpdateChartData();

    [ObservableProperty] private float _tlbGhostProjectionFontSize = 10.0f;
    partial void OnTlbGhostProjectionFontSizeChanged(float value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty] private bool _tlbShowGhostLabelsOnHoverOnly = false;
    partial void OnTlbShowGhostLabelsOnHoverOnlyChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    // Color Options
    public IReadOnlyList<string> AvailableColors => SettingsConstants.AvailableColors;

    public IReadOnlyList<NamedColor> ReverseWatchColorOptions => SettingsConstants.ReverseWatchColorOptions;

    // Reverse Watch
    [ObservableProperty]
    private int _reverseWatchPeriod = StockAnalyzer.Core.ChartConstants.DefaultReverseWatchPeriod;
    partial void OnReverseWatchPeriodChanged(int value) => RequestRender(RenderReason.DataChanged);

    [ObservableProperty]
    private int _reverseWatchDataCount = StockAnalyzer.Core.ChartConstants.DefaultReverseWatchDataCount;
    partial void OnReverseWatchDataCountChanged(int value) => RequestRender(RenderReason.DataChanged);

    [ObservableProperty]
    private bool _showReverseWatchGrid = true;
    partial void OnShowReverseWatchGridChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty]
    private bool _reverseWatchIsMaBased = true;
    partial void OnReverseWatchIsMaBasedChanged(bool value) => RequestRender(RenderReason.DataChanged);

    [ObservableProperty]
    private bool _reverseWatchIsLogScaleVolume = false;
    partial void OnReverseWatchIsLogScaleVolumeChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty]
    private float _reverseWatchLineThickness = 1.5f;
    partial void OnReverseWatchLineThicknessChanged(float value) => RequestRender(RenderReason.VisualChanged);

    // Ghost Projection Settings
    [ObservableProperty] private float _ghostProjectionFontSize = 11.0f;
    partial void OnGhostProjectionFontSizeChanged(float value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty] private bool _showGhostLabelsOnHoverOnly = true;
    partial void OnShowGhostLabelsOnHoverOnlyChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    // Comparison Chart Floating Tooltip (FR-39-15)
    [ObservableProperty] private bool _showFloatingTooltip = false;
    partial void OnShowFloatingTooltipChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    // Show Ticker instead of numeric value on Comparison Chart Y-Axis
    [ObservableProperty] private bool _showTickerInsteadOfValue = false;
    partial void OnShowTickerInsteadOfValueChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    [ObservableProperty]
    private PriceScaleType _priceScale = PriceScaleType.Linear;

    [ObservableProperty]
    private bool _invertOscillator = false;
    partial void OnInvertOscillatorChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    public string PriceScaleCode => PriceScale switch
    {
        PriceScaleType.Linear => "L",
        PriceScaleType.Log => "G",
        PriceScaleType.Percent => "%",
        PriceScaleType.Inverted => "I",
        _ => "L"
    };

    partial void OnPriceScaleChanged(PriceScaleType value)
    {
        OnPropertyChanged(nameof(PriceScaleCode));
        RequestRender(RenderReason.VisualChanged);
    }

    // Phase Colors mapping to UI labels:
    // UI "1: Bullish Reversal" (陽転) binds to Phase8Color -> Lime Green
    // UI "2: Buy Signal" (買い) binds to Phase1Color -> Green
    // UI "3: Buy Continuation" (買い乗せ) binds to Phase2Color -> Dark Green
    // UI "4: Caution" (天井警戒) binds to Phase3Color -> Yellow
    // UI "5: Bearish Reversal" (陰転) binds to Phase4Color -> Salmon
    // UI "6: Sell Signal" (売り) binds to Phase5Color -> Red
    // UI "7: Sell Continuation" (売り乗せ) binds to Phase6Color -> Dark Red
    // UI "8: Bottoming Out" (底入れ) binds to Phase7Color -> Blue Gray
    [ObservableProperty] private HsvData _reverseWatchPhase1Color = new(120, 0.8, 0.8, 1); // Green (UI label 2)
    [ObservableProperty] private HsvData _reverseWatchPhase2Color = new(120, 1, 0.4, 1);   // Dark Green (UI label 3)
    [ObservableProperty] private HsvData _reverseWatchPhase3Color = new(50, 1, 1, 1);     // Yellow (UI label 4)
    [ObservableProperty] private HsvData _reverseWatchPhase4Color = new(0, 0.4, 1, 1);    // Salmon (UI label 5)
    [ObservableProperty] private HsvData _reverseWatchPhase5Color = new(0, 1, 1, 1);      // Red (UI label 6)
    [ObservableProperty] private HsvData _reverseWatchPhase6Color = new(0, 1, 0.5, 1);    // Dark Red (UI label 7)
    [ObservableProperty] private HsvData _reverseWatchPhase7Color = new(200, 0.3, 0.5, 1); // Blue Gray (UI label 8)
    [ObservableProperty] private HsvData _reverseWatchPhase8Color = new(90, 0.4, 1, 1);    // Lime Green (UI label 1)


    // Drawing State
    [ObservableProperty]
    private DrawingTool _currentTool = DrawingTool.Pointer;

    [ObservableProperty]
    private DrawingTool _lastDrawingTool = DrawingTool.Pointer;

    public ChartObjectManager ObjectManager { get; } = new();

    // Legacy support for ChartBaseControl
    public System.Collections.Generic.IReadOnlyList<IChartObject> Drawings => ObjectManager.Objects;





    public ChartViewModel(IDataService dataService, IDialogService dialogService, IChartStrategyFactory strategyFactory, IStockAnalyzerSettings settings, ITimeFrameManager timeFrameManager, Core.Services.MarketStructureService marketStructureService, Core.Theme.IThemeManager themeManager, Core.Services.IChartSettingsManager chartSettingsManager, Core.Services.IDispatcherService dispatcherService, Core.Services.IPredictionService? predictionService, Core.Services.Analysis.IAnalysisPipelineService analysisPipelineService, Core.Services.IMarketDataProvider? marketDataProvider = null, Core.Services.IPythonService? pythonService = null, Core.Services.IComparisonDataAligner? comparisonDataAligner = null, IMessenger? messenger = null)
    {
        _dataService = dataService;
        _marketDataProvider = marketDataProvider;
        _timeFrameManager = timeFrameManager;
        DialogService = dialogService;
        _strategyFactory = strategyFactory;
        _settings = settings;
        _marketStructureService = marketStructureService;
        _themeManager = themeManager;
        _chartSettingsManager = chartSettingsManager;
        _dispatcherService = dispatcherService;
        _predictionService = predictionService;
        _pythonService = pythonService;
        _analysisPipelineService = analysisPipelineService;
        _comparisonDataAligner = comparisonDataAligner;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        
        _chartSettingsManager.SettingsChanged += OnGlobalSettingsChanged;

        // Register for messages
        _messenger.RegisterAll(this);

        _comparisonTargets = new();

        // Initialize symbol from externalized settings
        _symbol = _settings.DefaultSymbol;

        // Reverse Watch Phase Colors
        _reverseWatchPhase1Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(2)); // UI 2 -> Phase 2 (Green)
        _reverseWatchPhase2Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(3)); // UI 3 -> Phase 3 (Dark Green)
        _reverseWatchPhase3Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(4)); // UI 4 -> Phase 4 (Yellow)
        _reverseWatchPhase4Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(5)); // UI 5 -> Phase 5 (Salmon)
        _reverseWatchPhase5Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(6)); // UI 6 -> Phase 6 (Red)
        _reverseWatchPhase6Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(7)); // UI 7 -> Phase 7 (Dark Red)
        _reverseWatchPhase7Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(8)); // UI 8 -> Phase 8 (Blue Gray)
        _reverseWatchPhase8Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(1)); // UI 1 -> Phase 1 (Lime Green)

        Indicators.CollectionChanged += OnIndicatorsCollectionChanged;
        ComparisonTargets.CollectionChanged += OnComparisonTargetsChanged;
        _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;

        UpdateColorsFromTheme();
        UpdateColorsFromSettings(); // Settings override theme defaults
        UpdateParametersFromSettings();
        OnChartTypeChanged(ChartType); // Initial capability sync

        // Populate available tickers for in-tab selection
        if (_marketDataProvider != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var tickers = await _marketDataProvider.GetAvailableTickersAsync();
                    if (tickers == null) return;

                    _dispatcherService.Post<(ChartViewModel vm, IReadOnlyList<string> tickers)>(static (state) => 
                    {
                        if (state.vm._isDisposed) return;
                        state.vm.AvailableTickers.Clear();
                        foreach (var ticker in state.tickers)
                        {
                            state.vm.AvailableTickers.Add(ticker);
                        }
                    }, (this, tickers));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error fetching available tickers: {ex.Message}");
                }
            });
        }

        // Fetch initial state for synced tabs
        if (IsSyncEnabled)
        {
            PerformInitialSync();
        }
    }

    private void OnGlobalSettingsChanged()
    {
        _dispatcherService.Post<ChartViewModel>(static (vm) =>
        {
            if (vm._isDisposed) return;
            vm.UpdateColorsFromSettings();
            vm.UpdateParametersFromSettings();
            vm.RequestRender(RenderReason.ForceRedraw);
            vm.SettingsChanged?.Invoke();
        }, this);
    }

    private void UpdateParametersFromSettings()
    {
        var s = _chartSettingsManager.Current;

        // --- Renko ---
        RenkoSizingMode = s.RenkoSizingMode;
        RenkoBrickSize = s.RenkoBrickSize;
        RenkoBrickPercent = s.RenkoBrickPercent;
        RenkoAtrPeriod = s.RenkoAtrPeriod;
        RenkoAtrMultiplier = s.RenkoAtrMultiplier;
        RenkoReversal = s.RenkoReversal;
        RenkoRoundingMode = (int)s.RenkoRoundingMode;
        RenkoFallbackMode = (int)s.RenkoFallbackMode;
        RenkoShowMultiWavePatterns = s.RenkoShowMultiWavePatterns;
        RenkoShowGhostProjections = s.RenkoShowGhostProjections;
        RenkoGhostProjectionFontSize = s.RenkoGhostProjectionFontSize;
        RenkoShowGhostLabelsOnHoverOnly = s.RenkoShowGhostLabelsOnHoverOnly;

        // --- Kagi ---
        KagiReversalMode = s.KagiReversalMode;
        KagiReversalAmount = s.KagiReversalAmount;
        KagiReversalPercent = s.KagiReversalPercent;
        KagiAtrPeriod = s.KagiAtrPeriod;
        KagiAtrMultiplier = s.KagiAtrMultiplier;
        KagiRoundingMode = (int)s.KagiRoundingMode;
        KagiFallbackMode = (int)s.KagiFallbackMode;
        KagiLineThickness = s.KagiLineThickness;
        KagiInitialColumn = s.KagiInitialColumn;
        KagiShowMultiWavePatterns = s.KagiShowMultiWavePatterns;
        KagiShowGhostProjections = s.KagiShowGhostProjections;
        KagiGhostProjectionFontSize = s.KagiGhostProjectionFontSize;
        KagiShowGhostLabelsOnHoverOnly = s.KagiShowGhostLabelsOnHoverOnly;

        // --- P&F ---
        PnfSizingMode = s.PnfSizingMode;
        PnfBoxSize = s.PnfBoxSize;
        PnfBoxPercent = s.PnfBoxPercent;
        PnfAtrPeriod = s.PnfAtrPeriod;
        PnfAtrMultiplier = s.PnfAtrMultiplier;
        PnfRoundingMode = (int)s.PnfRoundingMode;
        PnfFallbackMode = (int)s.PnfFallbackMode;
        PnfReversal = s.PnfReversal;
        PnfShowDoubleBreakout = s.PnfShowDoubleBreakout;
        PnfShowTripleBreakout = s.PnfShowTripleBreakout;
        PnfShowTrendlineBreakout = s.PnfShowTrendlineBreakout;
        PnfShowTriangleBreakout = s.PnfShowTriangleBreakout;
        PnfShowCatapultBreakout = s.PnfShowCatapultBreakout;
        PnfShowMultiWavePatterns = s.PnfShowMultiWavePatterns;
        PnfShowGhostProjections = s.PnfShowGhostProjections;
        PnfGhostProjectionFontSize = s.PnfGhostProjectionFontSize;
        PnfShowGhostLabelsOnHoverOnly = s.PnfShowGhostLabelsOnHoverOnly;

        // --- Line / Area ---
        ShowLineMarkers = s.ShowLineMarkers;
        ShowAreaMarkers = s.ShowAreaMarkers;

        // --- Reverse Watch ---
        ShowReverseWatchGrid = s.ShowReverseWatchGrid;
        ReverseWatchIsMaBased = s.ReverseWatchIsMaBased;
        ReverseWatchIsLogScaleVolume = s.ReverseWatchIsLogScaleVolume;
        ReverseWatchDataCount = s.ReverseWatchDataCount;
        ReverseWatchLineThickness = s.ReverseWatchLineThickness;

        // --- Three Line Break ---
        TlbShowMultiWavePatterns = s.ThreeLineBreakShowMultiWavePatterns;
        TlbShowGhostProjections = s.ThreeLineBreakShowGhostProjections;
        TlbGhostProjectionFontSize = s.ThreeLineBreakGhostProjectionFontSize;
        TlbShowGhostLabelsOnHoverOnly = s.ThreeLineBreakShowGhostLabelsOnHoverOnly;
        
        // --- Relative Performance ---
        if (!Enumerable.SequenceEqual(ComparisonTargets, s.ComparisonTargets))
        {
            ComparisonTargets.Clear();
            foreach (var t in s.ComparisonTargets) ComparisonTargets.Add(t);
        }
        ComparisonMode = s.RelativePerformanceMode;
        ComparisonZScorePeriod = s.ComparisonZScorePeriod;
        ShowTickerInsteadOfValue = s.ShowTickerInsteadOfValue;

        // --- Interaction ---
        ShowFloatingTooltip = s.ShowFloatingTooltip;

        // --- Layout & Visuals ---
        ChartMarginTop = s.TopMargin;
        ChartMarginBottom = s.BottomMargin;
        ChartMarginRight = s.RightMargin;
        IsSubWindowVisible = s.IsSubWindowVisible;

        // --- Generic / Other ---
        GhostProjectionFontSize = s.GhostProjectionFontSize;
        ShowGhostLabelsOnHoverOnly = s.ShowGhostLabelsOnHoverOnly;
    }

    private void UpdateColorsFromSettings()
    {
        var s = _chartSettingsManager.Current;
        
        // --- Candlestick ---
        BullishColor = HsvData.FromHtmlSafe(s.CandlestickBullishColor);
        BearishColor = HsvData.FromHtmlSafe(s.CandlestickBearishColor);
        NeutralColor = HsvData.FromHtmlSafe(s.CandlestickNeutralColor);

        // --- OHLC Bar ---
        OhlcBullishColor = HsvData.FromHtmlSafe(s.OhlcBullishColor);
        OhlcBearishColor = HsvData.FromHtmlSafe(s.OhlcBearishColor);

        // --- Heikin Ashi ---
        HeikinBullishColor = HsvData.FromHtmlSafe(s.HeikinBullishColor);
        HeikinBearishColor = HsvData.FromHtmlSafe(s.HeikinBearishColor);

        // --- Renko ---
        RenkoBullishColor = HsvData.FromHtmlSafe(s.RenkoBullishColor);
        RenkoBearishColor = HsvData.FromHtmlSafe(s.RenkoBearishColor);

        // --- P&F ---
        PnfBullishColor = HsvData.FromHtmlSafe(s.PnfBullishColor);
        PnfBearishColor = HsvData.FromHtmlSafe(s.PnfBearishColor);

        // --- Kagi ---
        KagiBullishColor = HsvData.FromHtmlSafe(s.KagiBullishColor);
        KagiBearishColor = HsvData.FromHtmlSafe(s.KagiBearishColor);

        // --- Three Line Break ---
        ThreeLineBreakBullishColor = HsvData.FromHtmlSafe(s.ThreeLineBreakBullishColor);
        ThreeLineBreakBearishColor = HsvData.FromHtmlSafe(s.ThreeLineBreakBearishColor);
        
        // --- Line / Area ---
        LineChartColor = HsvData.FromHtmlSafe(s.LineColor);
        AreaChartColor = HsvData.FromHtmlSafe(s.AreaBaseColor);

        // --- Reverse Watch ---
        ReverseWatchPhase1Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase1Color);
        ReverseWatchPhase2Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase2Color);
        ReverseWatchPhase3Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase3Color);
        ReverseWatchPhase4Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase4Color);
        ReverseWatchPhase5Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase5Color);
        ReverseWatchPhase6Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase6Color);
        ReverseWatchPhase7Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase7Color);
        ReverseWatchPhase8Color = HsvData.FromHtmlSafe(s.ReverseWatchPhase8Color);
    }

    private void OnThemeManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Core.Theme.IThemeManager.CurrentTheme))
        {
            UpdateColorsFromTheme();
            UpdateColorsFromSettings(); // Settings override theme defaults
            RequestRender(RenderReason.ForceRedraw);
        }
    }

    private void OnComparisonTargetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        CalculateComparisonDataAsync();
    }

    private void UpdateColorsFromTheme()
    {
        var theme = _themeManager.CurrentTheme;
        
        // Use dispatcher to ensure properties are updated on UI thread if needed, 
        // but since these are observable properties, staying on current thread (usually UI) is best.
        BullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        BearishColor = HsvData.FromIndicatorColor(theme.Bearish);
        
        HeikinBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        HeikinBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        OhlcBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        OhlcBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        LineChartColor = HsvData.FromIndicatorColor(theme.LineChartLine);
        AreaChartColor = HsvData.FromIndicatorColor(theme.AreaChartFillBase);

        RenkoBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        RenkoBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        PnfBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        PnfBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        KagiBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        KagiBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        ThreeLineBreakBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        ThreeLineBreakBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        // Update RW colors if they are not explicitly customized 
        // (For now, just reset them to match theme-specific logic if desired, 
        // but RW usually has fixed phase colors. Let's stick to base colors for now).
    }



    public ChartViewModel() 
    {
        _messenger = WeakReferenceMessenger.Default;
        _dataService = new StockAnalyzer.Avalonia.Services.MockDataService();
        _dispatcherService = new StockAnalyzer.Avalonia.Services.Mocks.DesignTimeDispatcher();
        _timeFrameManager = new StockAnalyzer.Avalonia.Services.TimeFrameManager(_dataService);
        DialogService = new StockAnalyzer.Avalonia.Services.DialogService();
        _themeManager = new Core.Theme.ThemeManager();
        _chartSettingsManager = new Services.MockChartSettingsManager();
        _analysisPipelineService = new Core.Services.Analysis.AnalysisPipelineService();

        _settings = new Services.MockStockAnalyzerSettings();
        _symbol = _settings.DefaultSymbol;
        
        Indicators.CollectionChanged += OnIndicatorsCollectionChanged;
        ComparisonTargets.CollectionChanged += OnComparisonTargetsChanged;
        _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;
        _chartSettingsManager.SettingsChanged += OnGlobalSettingsChanged;
        OnChartTypeChanged(ChartType);
    }

    [ObservableProperty]
    private int _maxCandleCount = 200;

    [ObservableProperty]
    private int _maxCandleCountStep = 60; // Initial step based on 200 count

    [ObservableProperty]
    private string _newComparisonSymbol = string.Empty;

    // Calendar-based snap targets for Hybrid step calculation
    private static int[] SnapTargets => StockAnalyzer.Avalonia.Common.ChartConstants.ViewportSnapTargets;

    partial void OnMaxCandleCountChanged(int value)
    {
        // Hybrid step calculation based on webai.txt
        // 1. Calculate 25% of current value as base step
        double baseStep = value * 0.25;

        // Minimum step is 20
        if (baseStep < 20)
        {
            MaxCandleCountStep = 20;
        }
        else
        {
            // 3. Find closest snap target (ZeroAllocation: no LINQ or new arrays)
            int closestStep = SnapTargets[0];
            double minDiff = Math.Abs(SnapTargets[0] - baseStep);

            for (int i = 1; i < SnapTargets.Length; i++)
            {
                double diff = Math.Abs(SnapTargets[i] - baseStep);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closestStep = SnapTargets[i];
                }
            }

            MaxCandleCountStep = closestStep;
        }

        // On Spinner Up/Down, reload data automatically with cache invalidation
        if (ChartType == ChartType.RelativePerformance)
        {
            _ = LoadRelativePerformanceDataAsync();
        }
        else
        {
            _ = LoadDataInternalAsync(true);
        }
    }
    

    [RelayCommand]
    private void SetCandleCount(string countStr)
    {
        if (int.TryParse(countStr, out int count))
        {
            if (MaxCandleCount == count)
            {
                // If it's the exact same count, manually trigger since OnMaxCandleCountChanged won't fire
                _ = LoadDataInternalAsync(true);
            }
            else
            {
                // This triggers OnMaxCandleCountChanged, which now has the load trigger
                MaxCandleCount = count;
            }
        }
    }

    public bool IsKagiPercentageMode => KagiReversalMode == ChartSizingMode.Percentage;
    public bool IsRenkoPercentageMode => RenkoSizingMode == ChartSizingMode.Percentage;

    private async Task LoadRelativePerformanceDataAsync()
    {
        await LoadDataInternalAsync(true);
        // Only trigger comparison alignment after the primary data (Candles) is fully updated
        CalculateComparisonDataAsync();
    }

    private bool CanLoadData() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadData))]
    public Task LoadDataAsync() => LoadDataInternalAsync(true);

    private async Task LoadDataInternalAsync(bool forceRefresh)
    {
        // Cancel any previous load operation
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        // Debounce: Wait to ensure no newer requests are queued from rapid UI interactions (e.g. holding Increment)
        try
        {
            await Task.Delay(StockAnalyzer.Avalonia.Common.ChartConstants.DataLoadDebounceDelay, token);
        }
        catch (OperationCanceledException)
        {
            // A newer request superseded this one
            return;
        }

        if (IsLoading) return;
        IsLoading = true;
        
        // --- LIFECYCLE MANAGEMENT: CLEAR DRAWINGS ON LOAD ---
        // When loading new data (symbol change or refresh), existing drawings are invalid.
        // We must clear ALL drawing contexts to prevent stale objects.
        ObjectManager.Clear();
        // ----------------------------------------------------

        // Optimization: Start AI model loading in background as soon as we know we are loading data
        _ = _predictionService?.InitializeAsync();

        try
        {
             if (forceRefresh)
             {
                 _timeFrameManager?.InvalidateCache(Symbol);
             }

             var tf = SelectedTimeFrame.ToCoreTimeFrame();
             var data = _timeFrameManager != null
                 ? await Task.Run(() => _timeFrameManager.GetCandlesAsync(Symbol, tf, MaxCandleCount, token), token)
                 : await Task.Run(() => _dataService.LoadCandlesAsync(Symbol, tf, MaxCandleCount), token);

             token.ThrowIfCancellationRequested();

             // Zero Allocation / Batch Update Optimization:
             // Pre-allocate a fixed array and perform an atomic swap on the UI thread.
             var candleCount = data.Count;
             var newCandles = new CoreCandleData[candleCount];
             for (int i = 0; i < candleCount; i++)
             {
                 var c = data[i];
                 newCandles[i] = new CoreCandleData(c.Time, c.Open, c.High, c.Low, c.Close, (long)c.Volume);
             }
             
             var dispatcher = _dispatcherService;
             dispatcher.Post<(ChartViewModel vm, CoreCandleData[] candles)>(static (state) => 
             {
                 var (vm, candles) = state;
                 if (vm._isDisposed) return;

                 vm.Candles = candles;
                 vm.IndicatorResults = null; // Clear stale indicators immediately to prevent ghosting
                 
                 if (vm.Candles.Count > 0)
                 {
                     vm.CurrentPrice = vm.Candles[vm.Candles.Count - 1].Close;
                 }
                 
                 if (vm.IsKagiChart || vm.ChartType == ChartType.Renko || vm.ChartType == ChartType.PointAndFigure)
                 {
                     vm.UpdateChartData();
                 }
                 else
                 {
                     // Recalculate indicators after data is fully updated
                     vm.CalculateIndicatorsAsync();
                 }

                 // Recalculate comparison data if in RelativePerformance mode
                 // This is critical for startup: comparison targets are restored before candle data loads,
                 // so earlier CalculateComparisonDataAsync calls fail with empty data.
                 if (vm.ChartType == ChartType.RelativePerformance)
                 {
                     vm.CalculateComparisonDataAsync();
                 }
             }, (this, newCandles));

              // AI Trend Prediction
              if (_predictionService != null && _settings != null && Candles.Count >= _settings.PredictionWindowSize)
              {
                  // Zero-Allocation extraction for AI Prediction window
                  var windowSize = _settings.PredictionWindowSize;
                  var startIndex = Math.Max(0, Candles.Count - windowSize);
                  var mlCandles = new List<CandleData>(windowSize);
                  for (int i = startIndex; i < Candles.Count; i++)
                  {
                      var c = Candles[i];
                      mlCandles.Add(CandleData.CreateUnchecked(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
                  }
                  
                  var prediction = await _predictionService.PredictAsync(mlCandles);
                  dispatcher.Post<(ChartViewModel vm, PredictionResult? res)>(static (state) => 
                  { 
                      if (state.vm._isDisposed) return;
                      state.vm.CurrentPrediction = state.res; 
                  }, (this, prediction));
              }
              else
              {
                  CurrentPrediction = null;
              }
        }
        catch (OperationCanceledException)
        {
            // Timeframe switch or rapid input cancelled this load - expected behavior
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Reloads data when the selected timeframe changes.
    /// Cancels any in-progress load to prevent stale data.
    /// </summary>
    partial void OnSelectedTimeFrameChanged(TimeframeType value)
    {
        _ = LoadDataInternalAsync(true);
        if (ChartType == ChartType.RelativePerformance)
        {
            CalculateComparisonDataAsync();
        }
    }


    [RelayCommand]
    private void SetChartType(string typeName)
    {
        if (System.Enum.TryParse<ChartType>(typeName, out var type))
        {
            ChartType = type;
        }
    }


    // Dynamic Future Offset
    [ObservableProperty]
    private int _requiredFutureOffset = 0; // No default buffer; expanded dynamically by RecalculateFutureOffset

    public void RecalculateFutureOffset()
    {
        // Allow scrolling into future by default (approx 20 candles or user preference)
        // P&F Fixed: Add default right margin for P&F to allow 45-degree trendline projections
        // and prevent overlap with Y-axis labels.
        // TLB Fixed: Add default right margin so the latest block isn't hidden by the right edge.
        int maxOffset = 0;
        
        foreach (var indicator in Indicators)
        {
            if (indicator.TypeEnum == StockAnalyzer.Core.Models.IndicatorType.Ichimoku)
            {
                 maxOffset = System.Math.Max(maxOffset, StockAnalyzer.Core.ChartConstants.IchimokuStandardFutureOffset);
            }
            
            // Allow scrolling based on generic Offset property
            if (indicator.Offset > 0)
            {
                 maxOffset = System.Math.Max(maxOffset, indicator.Offset + 1); 
            }
        }
        RequiredFutureOffset = maxOffset; 
    }

    // ZeroAllocation Kagi Integration
    [ObservableProperty]
    private StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? _kagiDataAdapter;

    [ObservableProperty]
    private StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? _renkoDataAdapter;

    [ObservableProperty]
    private StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? _pnfDataAdapter;

    partial void OnKagiDataAdapterChanged(StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? value) => CalculateIndicatorsAsync();
    partial void OnRenkoDataAdapterChanged(StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? value) => CalculateIndicatorsAsync();
    partial void OnPnfDataAdapterChanged(StockAnalyzer.ZeroAllocation.ChartSegmentAdapter? value) => CalculateIndicatorsAsync();

    [ObservableProperty]
    private decimal _maxPrice = 1000m;

    [ObservableProperty]
    private decimal _minPrice = 0m;

    [ObservableProperty]
    private int? _threeLineBreakCount;

    public bool IsKagiChart => ChartType == ChartType.Kagi;

    partial void OnChartTypeChanged(ChartType value)
    {
        var caps = Core.Models.ChartTypeCapabilitiesRegistry.Get(value);
        CanToggleIndicators = caps.CanToggleIndicators;

        _displayModeSubject?.OnNext(value);

        // Switch drawing object context based on the new chart type.
        var context = GetContextType(value);
        ObjectManager.SwitchContext(context);

        if (value == ChartType.RelativePerformance)
        {
            CalculateComparisonDataAsync();
        }
        
        if (value == ChartType.Renko || value == ChartType.Kagi || value == ChartType.PointAndFigure || value == ChartType.ThreeLineBreak)
        {
            UpdateChartData();
        }
        
        if (value != ChartType.Renko && value != ChartType.Kagi && value != ChartType.PointAndFigure)
        {
            // Recalculate indicators on chart type change if not deferred by adapter updates.
            // Index-based charts (Kagi, Renko, etc.) compute indicators against their converted blocks (~30 items),
            // while time-series charts use raw candles (~200+ items). Without recalculation on every transition,
            // switching Kagi->Candlestick leaves stale 30-point results, causing indicator lines to stop early.
            CalculateIndicatorsAsync();
        }
        
        RequestRender(RenderReason.DataChanged);
    }

    private void RestoreMargins(ChartType type)
    {
        if (_chartMargins.TryGetValue(type, out var margins))
        {
            _isUpdatingMargins = true;
            try
            {
                ChartMarginTop = margins.Top;
                ChartMarginBottom = margins.Bottom;
                ChartMarginRight = margins.Right;
            }
            finally
            {
                _isUpdatingMargins = false;
            }
        }
    }

    // Trigger update when candles change
    partial void OnCandlesChanged(IReadOnlyList<CoreCandleData> value)
    {
        if (_isDisposed) return;

        OnPropertyChanged(nameof(HasData));

        if (value == null || value.Count == 0)
        {
            // Clear indicators and state when data is empty
            IndicatorResults = null;
            CurrentPrice = 0;
            return;
        }

        UpdateChartData();
        if (ChartType != ChartType.Renko && ChartType != ChartType.Kagi && ChartType != ChartType.PointAndFigure)
        {
            CalculateIndicatorsAsync();
        }

        if (ChartType == ChartType.RelativePerformance)
        {
            CalculateComparisonDataAsync();
        }
    }

    private void UpdateChartData()
    {
        var currentId = Interlocked.Increment(ref _chartUpdateId);
        _chartUpdateCts?.Cancel();
        _chartUpdateCts?.Dispose();
        _chartUpdateCts = new CancellationTokenSource();
        var token = _chartUpdateCts.Token;

        // Capture necessary state for background thread
        var chartType = ChartType;
        var candles = Candles;
        var strategyFactory = _strategyFactory;
        var dispatcher = _dispatcherService;

        if (candles == null || candles.Count == 0 || strategyFactory == null)
        {
            KagiDataAdapter = null;
            RenkoDataAdapter = null;
            PnfDataAdapter = null;
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                // Debounce to prevent flickering during rapid parameter changes
                await Task.Delay(100, token);

                var strategy = strategyFactory.GetStrategy(chartType);
                if (strategy == null) return;

                // Build parameters (This must be thread-safe or captured)
                // Note: Build() might access ViewModel properties. For safety, we should ideally capture them or run Build on UI thread.
                // Since this is a Minimal Fix, we'll assume Build is safe or handle it if it fails.
                var parameters = StockAnalyzer.Avalonia.Services.ChartParameterBuilder.Build(chartType, this);

                var result = strategy.Calculate(candles, parameters);

                dispatcher.Post<(ChartViewModel vm, int id, ChartStrategyResult result)>(static (state) =>
                {
                    var (vm, id, res) = state;
                    if (vm._isDisposed || id != vm._chartUpdateId) return;

                    // Update type-specific adapters
                    if (vm.ChartType == ChartType.Kagi)
                    {
                        vm.KagiDataAdapter = res.Adapter as ChartSegmentAdapter;
                        vm.EffectiveKagiReversalAmount = res.EffectiveSize;
                    }
                    else if (vm.ChartType == ChartType.Renko)
                    {
                        vm.RenkoDataAdapter = res.Adapter as ChartSegmentAdapter;
                        vm.EffectiveRenkoBrickSize = res.EffectiveSize;
                    }
                    else if (vm.ChartType == ChartType.PointAndFigure)
                    {
                        vm.PnfDataAdapter = res.Adapter as ChartSegmentAdapter;
                        vm.EffectivePnfBoxSize = res.EffectiveSize;
                    }

                    if (res.Adapter != null)
                    {
                        var count = res.Adapter.Count;
                        if (vm.VisibleStartIndex >= count)
                        {
                            vm.VisibleStartIndex = System.Math.Max(0, count - vm.VisibleCandleCount);
                        }
                        
                        var (max, min) = res.Adapter.GetPriceRange();
                        vm.MaxPrice = max;
                        vm.MinPrice = min;
                    }
                    
                    vm.RequestRender(RenderReason.DataChanged);
                }, (this, currentId, result));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Async Chart Update Error: {ex.Message}");
            }
        }, token);
    }

    // Trigger update when parameters change
    partial void OnKagiReversalModeChanged(ChartSizingMode value) => UpdateChartData();
    partial void OnKagiReversalAmountChanged(decimal value) => UpdateChartData();
    partial void OnKagiAtrMultiplierChanged(decimal value) => UpdateChartData();
    partial void OnKagiAtrPeriodChanged(int value) => UpdateChartData();
    partial void OnKagiReversalPercentChanged(decimal value) => UpdateChartData();
    partial void OnKagiRoundingModeChanged(int value) => UpdateChartData();
    partial void OnKagiFallbackModeChanged(int value) => UpdateChartData();
    partial void OnKagiShowMultiWavePatternsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnKagiShowGhostProjectionsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnKagiLineThicknessChanged(float value) => RequestRender(RenderReason.VisualChanged);
    partial void OnKagiGhostProjectionFontSizeChanged(float value) => RequestRender(RenderReason.VisualChanged);
    partial void OnKagiShowGhostLabelsOnHoverOnlyChanged(bool value) => RequestRender(RenderReason.VisualChanged);
    
    // Trigger update for Renko/P&F parameters
    partial void OnRenkoSizingModeChanged(ChartSizingMode value)
    {
        OnPropertyChanged(nameof(IsRenkoPercentageMode));
        UpdateChartData();
    }
    partial void OnRenkoBrickSizeChanged(decimal value) => UpdateChartData();
    partial void OnRenkoBrickPercentChanged(decimal value) => UpdateChartData();
    partial void OnRenkoAtrMultiplierChanged(decimal value) => UpdateChartData();
    partial void OnRenkoAtrPeriodChanged(int value) => UpdateChartData();
    partial void OnRenkoReversalChanged(int value) => UpdateChartData();
    partial void OnRenkoRoundingModeChanged(int value) => UpdateChartData();
    partial void OnRenkoFallbackModeChanged(int value) => UpdateChartData();
    partial void OnRenkoShowMultiWavePatternsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnRenkoShowGhostProjectionsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnRenkoGhostProjectionFontSizeChanged(float value) => RequestRender(RenderReason.VisualChanged);
    partial void OnRenkoShowGhostLabelsOnHoverOnlyChanged(bool value) => RequestRender(RenderReason.VisualChanged);

    partial void OnPnfSizingModeChanged(ChartSizingMode value) => UpdateChartData();
    partial void OnPnfBoxSizeChanged(decimal value) => UpdateChartData();
    partial void OnPnfAtrMultiplierChanged(decimal value) => UpdateChartData();
    partial void OnPnfAtrPeriodChanged(int value) => UpdateChartData();
    partial void OnPnfReversalChanged(int value) => UpdateChartData();
    partial void OnPnfShowDoubleBreakoutChanged(bool value) => UpdateChartData();
    partial void OnPnfShowTripleBreakoutChanged(bool value) => UpdateChartData();
    partial void OnPnfShowTrendlineBreakoutChanged(bool value) => UpdateChartData();
    partial void OnPnfShowTriangleBreakoutChanged(bool value) => UpdateChartData();
    partial void OnPnfShowCatapultBreakoutChanged(bool value) => UpdateChartData();
    partial void OnPnfShowMultiWavePatternsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnPnfShowGhostProjectionsChanged(bool value) { UpdateChartData(); RequestRender(RenderReason.VisualChanged); }
    partial void OnPnfBoxPercentChanged(decimal value) => UpdateChartData();
    partial void OnPnfRoundingModeChanged(int value) => UpdateChartData();
    partial void OnPnfFallbackModeChanged(int value) => UpdateChartData();
    partial void OnPnfGhostProjectionFontSizeChanged(float value) => RequestRender(RenderReason.VisualChanged);
    partial void OnPnfShowGhostLabelsOnHoverOnlyChanged(bool value) => RequestRender(RenderReason.VisualChanged);
    
    public void OnDrawingFinished(IChartObject obj)
    {
        if (obj is Drawing.Objects.DtwProjectionObject dtwObj)
        {
            // The user finished drawing the DTW projection box.
            // Increment the request ID so that previous queries are ignored if they haven't finished
            var currentRequestId = Interlocked.Increment(ref _dtwRequestIdCounter);

            // Start the ML search using the selected timeframe. Use Task.Run to prevent UI deadlock/freeze during Python initialization.
            Task.Run(() => ExecuteDtwSearchAsync(dtwObj, currentRequestId));
        }
        else if (obj is Drawing.Objects.HarmonicPatternObject harmonicObj)
        {
            // Run pattern detection within the selected region
            if (_candles != null && _candles.Count > 0)
            {
                var coreCandles = _candles.Select(c =>
                    CandleData.CreateUnchecked(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
                harmonicObj.Recalculate(coreCandles);
                RequestRender(RenderReason.DataChanged);
            }
        }
    }

    private async Task ExecuteDtwSearchAsync(Drawing.Objects.DtwProjectionObject dtwObj, int requestId)
    {
        var dispatcher = _dispatcherService;

        // Debounce: Wait 250ms to let subsequent drag events (if any) override the request ID
        await Task.Delay(StockAnalyzer.Avalonia.Common.ChartConstants.DtwSearchDebounceDelay);

        if (requestId != _dtwRequestIdCounter)
        {
            return; // Ignore obsolete request before hitting the ML pipeline
        }

        if (_marketStructureService == null)
        {
            return;
        }

        if (_candles == null || _candles.Count == 0 || dtwObj.Points.Count < 2)
        {
            return;
        }

        try
        {
            // Determine the start and end indices for the selected region
            var t1 = dtwObj.Points[0].Time;
            var t2 = dtwObj.Points[1].Time;

            var startTime = t1 < t2 ? t1 : t2;
            var endTime = t1 > t2 ? t1 : t2;

            var candlesList = _candles.ToList();
            var startIndex = candlesList.FindIndex(c => c.Timestamp >= startTime);
            var endIndex = candlesList.FindLastIndex(c => c.Timestamp <= endTime);

            if (startIndex < 0 || endIndex < 0 || startIndex >= endIndex)
            {
                return; // Invalid selection range
            }

            var queryLength = endIndex - startIndex + 1;
            
            // Note: We are currently passing the whole candle history and giving the length of the query.
            // SearchSimilarPatternsAsync currently takes the whole history and considers the *last* queryLength candles
            // as the query. If the framework expects arbitrary historical queries, the service logic might need adapting.
            // For now, we'll try sending the whole dataset and using queryLength.
            // Convert visible candles to ML-compatible CandleData
            var mlCandles = _candles.Select(c => CandleData.CreateUnchecked(
                c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

            var result = await _marketStructureService.SearchSimilarPatternsAsync(
                candles: mlCandles,
                lookback: 0,
                topK: 5,
                futureSteps: 20,
                threshold: 0.3, // We can lower this if it still doesn't hit
                queryLength: queryLength,
                queryStartIndex: startIndex,
                useStructural: true);

            if (requestId != _dtwRequestIdCounter)
            {
                return;
            }

            if (result.IsSuccessful && result.Patterns != null && result.Patterns.Count > 0)
            {
                // Take the most similar pattern (top 1) and project it
                var topOverlay = result.Patterns.FirstOrDefault();
                if (topOverlay != null && topOverlay.FuturePercentChange != null)
                {
                    // Map the relative FuturePercentChange to absolute chart coordinates starting from the end of the selection
                    var projectedPoints = new List<StockAnalyzer.Core.Models.Point>();
                    var lastCandle = _candles[endIndex];
                    var basePrice = (double)lastCandle.Close;

                    var timeframeSpan = SelectedTimeFrame.ToTimeSpan();

                    for (int i = 0; i < topOverlay.FuturePercentChange.Count; i++)
                    {
                        var targetIndex = endIndex + i + 1; // +1 to start after the selection
                        DateTime targetTime;
                        if (targetIndex < _candles.Count)
                        {
                            targetTime = _candles[targetIndex].Timestamp;
                        }
                        else
                        {
                            // Extrapolate future timestamps if we run out of real candle data
                            int extendedSteps = targetIndex - _candles.Count + 1;
                            targetTime = _candles[^1].Timestamp + (timeframeSpan * extendedSteps);
                        }

                        // Values in FuturePercentChange are relative changes. Add to base price.
                        var projectedPrice = basePrice * (1.0 + topOverlay.FuturePercentChange[i] / 100.0);
                        // Encode time as ticks in X, and price as Y
                        projectedPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, projectedPrice));
                    }

                    // Resolve matched pattern timestamps for chart highlight
                    DateTime? matchStart = null;
                    DateTime? matchEnd = null;
                    if (topOverlay.StartIndex >= 0 && topOverlay.StartIndex < _candles.Count)
                        matchStart = _candles[topOverlay.StartIndex].Timestamp;
                    if (topOverlay.EndIndex >= 0 && topOverlay.EndIndex < _candles.Count)
                        matchEnd = _candles[topOverlay.EndIndex].Timestamp;

                    // Dispatch to UI thread to update the object and request redraw
                    dispatcher.Post<(ChartViewModel vm, Drawing.Objects.DtwProjectionObject dtwObj, List<StockAnalyzer.Core.Models.Point> points, DateTime? start, DateTime? end)>(static (state) =>
                    {
                        var (vm, dtwObj, points, start, end) = state;
                        if (vm._isDisposed) return;

                        dtwObj.ProjectedPath = points;
                        dtwObj.MatchedStartTime = start;
                        dtwObj.MatchedEndTime = end;
                        vm.RequestRender(RenderReason.DataChanged);
                    }, (this, dtwObj, projectedPoints, matchStart, matchEnd));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during DTW search: {ex.Message}");
        }
    }


    [RelayCommand]
    public void ResetSettingsToDefault()
    {
        var theme = ThemeManager.CurrentTheme;
        BullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        BearishColor = HsvData.FromIndicatorColor(theme.Bearish);
        
        HeikinBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        HeikinBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        // Match OHLC Bar colors to Candlestick colors as requested
        OhlcBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        OhlcBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        LineChartColor = HsvData.FromIndicatorColor(theme.LineChartLine);
        AreaChartColor = HsvData.FromIndicatorColor(theme.AreaChartFillBase);

        RenkoBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        RenkoBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        PnfBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        PnfBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        KagiBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        KagiBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        ThreeLineBreakBullishColor = HsvData.FromIndicatorColor(theme.Bullish);
        ThreeLineBreakBearishColor = HsvData.FromIndicatorColor(theme.Bearish);

        if (_settings != null)
        {
            ReverseWatchPhase1Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(2)); // UI 2
            ReverseWatchPhase2Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(3)); // UI 3
            ReverseWatchPhase3Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(4)); // UI 4
            ReverseWatchPhase4Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(5)); // UI 5
            ReverseWatchPhase5Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(6)); // UI 6
            ReverseWatchPhase6Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(7)); // UI 7
            ReverseWatchPhase7Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(8)); // UI 8
            ReverseWatchPhase8Color = HsvData.FromHtmlSafe(_settings.GetReverseWatchPhaseColor(1)); // UI 1
        }
        
        RequestRender(RenderReason.ForceRedraw);
    }



    [RelayCommand]
    public async Task AddComparisonTargetAsync(string ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return;
        
        // Don't add if it's the primary symbol
        if (string.Equals(ticker, Symbol, StringComparison.OrdinalIgnoreCase)) return;

        if (_marketDataProvider != null)
        {
            var available = await _marketDataProvider.GetAvailableTickersAsync();
            if (!available.Any(t => string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase)))
            {
                await DialogService.ShowAlertAsync("Warning", $"Ticker '{ticker}' not found in available data.");
                return;
            }
        }

        if (!ComparisonTargets.Any(t => string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase)))
        {
            ComparisonTargets.Add(ticker.ToUpperInvariant());
        }
        NewComparisonSymbol = string.Empty;
    }

    [RelayCommand]
    public void RemoveComparisonTarget(string ticker)
    {
        if (ComparisonTargets.Contains(ticker))
        {
            ComparisonTargets.Remove(ticker);
        }
    }

    /// <summary>
    /// Maps ChartType to ChartDrawingContextType.
    /// Defines which chart types share the same drawing objects.
    /// </summary>
    private ChartDrawingContextType GetContextType(ChartType type)
    {
        return type switch
        {
            ChartType.Candlestick => ChartDrawingContextType.Standard,
            ChartType.HeikinAshi => ChartDrawingContextType.Standard,
            ChartType.OHLCBar => ChartDrawingContextType.Standard, // Added OHLCBar to Standard group
            
            ChartType.Line => ChartDrawingContextType.Linear,
            ChartType.Area => ChartDrawingContextType.Linear,
            
            ChartType.Kagi => ChartDrawingContextType.Kagi,
            ChartType.Renko => ChartDrawingContextType.Renko,
            ChartType.PointAndFigure => ChartDrawingContextType.PointAndFigure,
            ChartType.ThreeLineBreak => ChartDrawingContextType.ThreeLineBreak,
            ChartType.ReverseWatch => ChartDrawingContextType.ReverseWatch,
            
            _ => ChartDrawingContextType.Standard // Default fallback
        };
    }


    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (IsDisplaySetting(e.PropertyName))
        {
            _messenger.Send(
                new Common.ChartSettingsChangedMessage(
                    new Common.ChartSettingChange(e.PropertyName ?? string.Empty, null)));
        }
    }

    private static bool IsDisplaySetting(string? propertyName)
    {
        return propertyName is
            nameof(ShowHeaderInfo) or
            nameof(ShowVolume) or
            nameof(ShowPriceLabels) or
            nameof(ShowCrosshair) or
            nameof(ShowLegend) or
            nameof(IsSubWindowVisible) or
            nameof(ChartType);
    }

    /// <summary>
    /// Consumes (reads and resets) the accumulated render reasons.
    /// </summary>
    public RenderReason ConsumeRenderReason()
    {
        return (RenderReason)Interlocked.Exchange(ref _accumulatedRenderReason, 0);
    }
}
