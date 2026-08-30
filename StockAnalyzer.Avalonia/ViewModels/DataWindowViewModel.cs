using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Services;
using System.Collections.Immutable;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class DataWindowViewModel : ViewModelBase,
    IDisposable,
    IRecipient<CrosshairPositionChangedMessage>,
    IRecipient<IndicatorSettingsChangedMessage>,
    IRecipient<SingleIndicatorSettingsChangedMessage>,
    IRecipient<IndicatorResultsUpdatedMessage>
{
    private bool _isDisposed;
    private ChartViewModel _chartViewModel;

    public string Symbol => string.IsNullOrWhiteSpace(_chartViewModel?.Symbol) ? "-" : _chartViewModel.Symbol;

    public void SetChartViewModel(ChartViewModel chartViewModel)
    {
        if (_chartViewModel != null)
        {
            _chartViewModel.PropertyChanged -= ChartViewModel_PropertyChanged;
            if (_chartViewModel.ObjectManager != null)
            {
                _chartViewModel.ObjectManager.Synced -= OnObjectManagerSynced;
            }
        }
        _chartViewModel = chartViewModel;
        if (_chartViewModel != null)
        {
            _chartViewModel.PropertyChanged += ChartViewModel_PropertyChanged;
            if (_chartViewModel.ObjectManager != null)
            {
                _chartViewModel.ObjectManager.Synced += OnObjectManagerSynced;
            }
            UpdateComparisonColors(_chartViewModel.ThemeManager?.CurrentTheme?.IsDark ?? true);
            UpdatePropertiesInternal();
        }
        OnPropertyChanged(nameof(Symbol));
    }

    private void OnObjectManagerSynced()
    {
        _dispatcherService.Post(static vm => vm.UpdatePropertiesInternal(), this);
    }

    private void ChartViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartViewModel.Symbol))
        {
            _dispatcherService.Post(static vm =>
            {
                vm.OnPropertyChanged(nameof(Symbol));
                vm.UpdatePropertiesInternal();
            }, this);
        }
        else if (e.PropertyName == nameof(ChartViewModel.ChartType))
        {
            _dispatcherService.Post(static vm => vm.UpdatePropertiesInternal(), this);
        }
    }

    private System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData>? _cachedCandlesRef = null;
    private int _cachedLineCount = -1;
    private ImmutableArray<StockAnalyzer.Core.Models.ThreeLineBreakBlock> _cachedBlocks = default;
    private readonly IndicatorColor[] _comparisonColors;

    // Cache fields for ZeroAllocation optimization
    private int _lastCrosshairCandleIndex = int.MinValue;
    private CoreCandleData? _lastCrosshairCandle = null;
    private ChartType? _lastChartType = null;
    private StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint? _lastRwPoint = null;
    private int _lastIndicatorCount = -1;
    private bool? _lastIsDark = null;
    private object? _lastResultsRef = null;
    private StockAnalyzer.Core.Models.Confluence.ConfluenceResult? _lastConfluence = null;
    private CrosshairPositionData? _lastCrosshairData = null;


    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    private string _openText = string.Empty;

    [ObservableProperty]
    private string _highText = string.Empty;

    [ObservableProperty]
    private string _lowText = string.Empty;

    [ObservableProperty]
    private string _closeText = string.Empty;

    [ObservableProperty]
    private string _volumeText = string.Empty;

    [ObservableProperty]
    private string _yesterdayChangeText = string.Empty;

    [ObservableProperty]
    private IndicatorColor _yesterdayChangeColor;

    [ObservableProperty]
    private string _yesterdayChangeRatioText = string.Empty;

    [ObservableProperty]
    private IndicatorColor _yesterdayChangeRatioColor;

    [ObservableProperty]
    private string _kagiReversalText = string.Empty;

    [ObservableProperty]
    private string _threeLineBreakReversalText = string.Empty;

    [ObservableProperty]
    private string _threeLineBreakCountText = string.Empty;

    [ObservableProperty]
    private string _renkoCountText = string.Empty;

    [ObservableProperty]
    private string _renkoReversalText = string.Empty;

    [ObservableProperty]
    private string _renkoUpdateText = string.Empty;

    [ObservableProperty]
    private string _pointAndFigureCountText = string.Empty;

    [ObservableProperty]
    private string _reverseWatchValueText = string.Empty;

    [ObservableProperty]
    private string _reverseWatchVolumeText = string.Empty;

    [ObservableProperty]
    private string _confluenceScoreText = string.Empty;

    [ObservableProperty]
    private IndicatorColor _confluenceColor = new IndicatorColor(255, 128, 128, 128); // Gray

    [ObservableProperty]
    private bool _isConfluenceVisible = false;

    [ObservableProperty]
    private bool _isOpenVisible = true;

    [ObservableProperty]
    private bool _isHighVisible = true;

    [ObservableProperty]
    private bool _isLowVisible = true;

    [ObservableProperty]
    private bool _isCloseVisible = true;

    [ObservableProperty]
    private bool _isVolumeVisible = true;

    [ObservableProperty]
    private string _indicatorSectionTitle = "Indicators";

    [ObservableProperty]
    private string _drawingSectionTitle = "Drawing Tools";

    public ObservableCollection<DataWindowItemViewModel> IndicatorItems { get; } = new();

    public ObservableCollection<DataWindowItemViewModel> DrawingItems { get; } = new();

    public bool HasItems => IndicatorItems.Count > 0 || DrawingItems.Count > 0;

    // Comparison Floating Tooltip (FR-39-15)
    [ObservableProperty]
    private bool _isComparisonTooltipVisible = false;

    [ObservableProperty]
    private double _tooltipLeft = 0;

    [ObservableProperty]
    private double _tooltipTop = 0;

    [ObservableProperty]
    private string _tooltipDateText = string.Empty;

    public ObservableCollection<ComparisonTooltipItemViewModel> ComparisonTooltipItems { get; } = new();

    private readonly IMessenger _messenger;
    private bool _isSyncing = false;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILocalizationService _localizationService;

    private class DesignTimeLocalizationService : ILocalizationService
    {
        public string GetString(string key)
        {
            try
            {
                var localized = LocalizationManager.Instance[key];
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            catch {}
            return key;
        }
    }

    public DataWindowViewModel(ChartViewModel chartViewModel) : this(chartViewModel, chartViewModel.Messenger, chartViewModel.DispatcherService, null)
    {
    }

    public DataWindowViewModel(ChartViewModel chartViewModel, IMessenger messenger, IDispatcherService dispatcherService) : this(chartViewModel, messenger, dispatcherService, null)
    {
    }

    public DataWindowViewModel(ChartViewModel chartViewModel, IMessenger messenger, IDispatcherService dispatcherService, ILocalizationService? localizationService)
    {
        _chartViewModel = chartViewModel;
        _messenger = messenger;
        _dispatcherService = dispatcherService;
        _localizationService = localizationService ?? new DesignTimeLocalizationService();

        _indicatorSectionTitle = _localizationService.GetString("DataWindow_Section_Indicators");
        var localizedDrawingTitle = _localizationService.GetString("DataWindow_Section_Drawings");
        _drawingSectionTitle = (!string.IsNullOrEmpty(localizedDrawingTitle) && localizedDrawingTitle != "DataWindow_Section_Drawings")
            ? localizedDrawingTitle
            : "Drawing Tools";

        if (_chartViewModel != null)
        {
            _chartViewModel.PropertyChanged += ChartViewModel_PropertyChanged;
            if (_chartViewModel.ObjectManager != null)
            {
                _chartViewModel.ObjectManager.Synced += OnObjectManagerSynced;
            }
        }

        // Pre-allocate comparison colors (12 colors)
        _comparisonColors = new IndicatorColor[ChartColorPalette.PaletteSize];
        for (int i = 0; i < _comparisonColors.Length; i++)
        {
             _comparisonColors[i] = new IndicatorColor(0, 0, 0, 0); // Transparent placeholder
        }
        UpdateComparisonColors(_chartViewModel!.ThemeManager?.CurrentTheme?.IsDark ?? true);

        _messenger.Register<DataWindowViewModel, CrosshairPositionChangedMessage>(this, (r, m) => r.Receive(m));
        _messenger.Register<DataWindowViewModel, IndicatorSettingsChangedMessage>(this, (r, m) => r.Receive(m));
        _messenger.Register<DataWindowViewModel, SingleIndicatorSettingsChangedMessage>(this, (r, m) => r.Receive(m));
        _messenger.Register<DataWindowViewModel, IndicatorResultsUpdatedMessage>(this, (r, m) => r.UpdatePropertiesInternal());
        
        // Initial update
        UpdatePropertiesInternal();
    }
    
    // For design time support
    public DataWindowViewModel() 
    {
        _chartViewModel = new ChartViewModel();
        _messenger = WeakReferenceMessenger.Default;
        _dispatcherService = new NullDispatcherService(); // Fix CS8618
        _localizationService = new DesignTimeLocalizationService();

        _comparisonColors = new IndicatorColor[ChartColorPalette.PaletteSize];
        for (int i = 0; i < _comparisonColors.Length; i++)
        {
             _comparisonColors[i] = new IndicatorColor(0, 0, 0, 0);
        }
    }

    private class NullDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public Task PostAsync(Func<Task> action) => action();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }


    private void UpdatePropertiesInternal()
    {
        var candles = _chartViewModel.Candles;
        if (candles == null) return;

        // Preferred: Use the last crosshair candle if it's still in the current collection
        CoreCandleData? candle = _lastCrosshairCandle;
        int index = _lastCrosshairCandleIndex;
        
        // Fallback: If no crosshair or collection changed, use latest candle
        if (candle == null || index < 0 || index >= candles.Count)
        {
            if (candles.Count > 0)
            {
                index = candles.Count - 1;
                candle = candles[index];
            }
            else
            {
                candle = null;
                index = -1;
            }
        }
            
        UpdateBasicProperties(candle);
        UpdateSpecializedProperties(index, candle);
        UpdateIndicatorItems(_chartViewModel.Indicators, _chartViewModel.IndicatorResults ?? new Dictionary<string, IIndicatorResult>());
        UpdateDrawingItems(candle);
        OnPropertyChanged(nameof(HasItems));
        
        if (_chartViewModel.ChartType == ChartType.RelativePerformance && index >= 0)
        {
             UpdateRelativePerformanceItems(index);
        }
    }

    public void Receive(IndicatorSettingsChangedMessage message) => InvalidateCache();
    public void Receive(SingleIndicatorSettingsChangedMessage message)
    {
        InvalidateCache();
        // Update items immediately to reflect metadata/color changes without waiting for calculation
        UpdateIndicatorItems(_chartViewModel.Indicators, _chartViewModel.IndicatorResults ?? new Dictionary<string, IIndicatorResult>());
    }

    [RelayCommand]
    private void ToggleVisibility(DataWindowItemViewModel? item)
    {
        if (item?.AssociateId == null) return;
        
        var indicator = _chartViewModel.Indicators.FirstOrDefault(i => i.Id == item.AssociateId);
        if (indicator == null)
        {
            ToggleGroupVisibility(item);
            return;
        }

        bool newState = item.IsEnabled;
        if (indicator.IsEnabled != newState)
        {
            indicator.IsEnabled = newState;
            // Clone with MathematicalVersion = 0 to ensure visual-only fast-path.
            // Sending the live indicator directly would cause MainWindowVM to enter
            // the math-change branch if MathematicalVersion > 0 from prior edits,
            // leading to destructive recalculation and permanent hiding.
            var clone = indicator.Clone();
            clone.Id = indicator.Id; // Preserve original ID for matching
            clone.MathematicalVersion = 0; // Force visual-only path
            clone.IsEnabled = newState;
            _messenger.Send(new SingleIndicatorSettingsChangedMessage(clone));
        }
    }

    [RelayCommand]
    private void ToggleGroupVisibility(DataWindowItemViewModel? item)
    {
        if (item?.AssociateId == null) return;
        string typeName = item.AssociateId;
        
        bool isGuid = Guid.TryParse(typeName, out _);
        var indicatorsInGroup = _chartViewModel.Indicators
            .Where(i => (isGuid && i.Id == typeName) || (!isGuid && (i.TypeEnum?.ToString() == typeName || i.TypeEnum?.GetDescription() == typeName)))
            .ToList();

        if (indicatorsInGroup.Count == 0) return;

        bool targetState = item.IsEnabled;
        bool changed = false;

        foreach (var indicator in indicatorsInGroup)
        {
            if (indicator.IsEnabled != targetState)
            {
                indicator.IsEnabled = targetState;
                changed = true;
            }
        }
        
        // Sync ViewModel state for immediate feedback
        if (changed)
        {
            _isSyncing = true;
            try
            {
                SyncViewModelsToIndicators(indicatorsInGroup);
            }
            finally
            {
                _isSyncing = false;
            }

            // Send individual visual-only messages to avoid destructive Clear/Add in MainWindowVM
            foreach (var indicator in indicatorsInGroup)
            {
                var clone = indicator.Clone();
                clone.Id = indicator.Id;
                clone.MathematicalVersion = 0; // Force visual-only path
                _messenger.Send(new SingleIndicatorSettingsChangedMessage(clone));
            }
        }
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isSyncing) return;

        if (e.PropertyName == nameof(DataWindowItemViewModel.IsEnabled))
        {
            if (sender is DataWindowItemViewModel item)
            {
                if (item.AssociateId != null)
                {
                    var indicator = _chartViewModel.Indicators.FirstOrDefault(i => i.Id == item.AssociateId);
                    if (indicator != null) ToggleVisibility(item);
                    else ToggleGroupVisibility(item);
                }
            }
        }
    }

    [RelayCommand]
    private void ToggleGroupAllOn(DataWindowItemViewModel? item) => ToggleGroupAll(item, true);

    [RelayCommand]
    private void ToggleGroupAllOff(DataWindowItemViewModel? item) => ToggleGroupAll(item, false);

    private void ToggleGroupAll(DataWindowItemViewModel? item, bool targetState)
    {
        if (item?.AssociateId == null) return;
        string typeName = item.AssociateId;
        
        bool isGuid = Guid.TryParse(typeName, out _);
        var indicatorsInGroup = _chartViewModel.Indicators
            .Where(i => (isGuid && i.Id == typeName) || (!isGuid && (i.TypeEnum?.ToString() == typeName || i.TypeEnum?.GetDescription() == typeName)))
            .ToList();

        if (indicatorsInGroup.Count == 0) return;

        bool changed = false;
        foreach (var indicator in indicatorsInGroup)
        {
            if (indicator.IsEnabled != targetState)
            {
                indicator.IsEnabled = targetState;
                changed = true;
            }
        }

        if (changed)
        {
            SyncViewModelsToIndicators(indicatorsInGroup);
            
            // Send individual visual-only messages to avoid destructive Clear/Add in MainWindowVM
            foreach (var indicator in indicatorsInGroup)
            {
                var clone = indicator.Clone();
                clone.Id = indicator.Id;
                clone.MathematicalVersion = 0; // Force visual-only path
                _messenger.Send(new SingleIndicatorSettingsChangedMessage(clone));
            }
        }
    }

    [RelayCommand]
    private void OpenSettings(DataWindowItemViewModel? item)
    {
        if (item?.AssociateId == null) return;

        // Specification: Don't open settings for parent headers of a single-indicator group
        if (item.HasChildren && item.Children.Count <= 1) return;

        var indicator = _chartViewModel.Indicators.FirstOrDefault(i => i.Id == item.AssociateId);
        if (indicator != null)
        {
            _messenger.Send(new StockAnalyzer.Avalonia.Common.OpenIndicatorPropertiesMessage(indicator));
        }
    }

    private void SyncViewModelsToIndicators(System.Collections.Generic.List<CoreIndicatorSettings> indicators)
    {
        if (indicators == null || indicators.Count == 0) return;
        var ids = new System.Collections.Generic.HashSet<string>(indicators.Select(i => i.Id));
        
        foreach (var item in IndicatorItems)
        {
            if (item.AssociateId != null && ids.Contains(item.AssociateId))
            {
                item.IsEnabled = indicators.First(ind => ind.Id == item.AssociateId).IsEnabled;
            }
            foreach (var child in item.Children)
            {
                if (child.AssociateId != null && ids.Contains(child.AssociateId))
                {
                    child.IsEnabled = indicators.First(ind => ind.Id == child.AssociateId).IsEnabled;
                }
            }
        }
    }

    private void UpdateComparisonColors(bool isDark)
    {
        if (_lastIsDark == isDark) return;
        _lastIsDark = isDark;

        for (int i = 0; i < _comparisonColors.Length; i++)
        {
            _comparisonColors[i] = ChartColorPalette.Get(i, isDark);
        }
    }

    private void InvalidateCache()
    {
        _lastCrosshairCandleIndex = int.MinValue;
        _lastCrosshairCandle = null;
        _lastChartType = null;
        _lastRwPoint = null;
        _lastIndicatorCount = -1;
        IsConfluenceVisible = false;
        _lastResultsRef = null;
        _lastConfluence = null;
    }

    public void Receive(CrosshairPositionChangedMessage message)
    {
        _dispatcherService.Post(static state => {
            var vm = state.Vm;
            var data = state.Msg.Value;
            if (data == null) return;
            
            // Update Floating Tooltip (FR-39-15)
            vm.UpdateFloatingTooltip(data);

            // ZeroAllocation Optimization: Prevent continuous string allocation on mouse hover
            vm._lastCrosshairData = data;
            bool isReverseWatch = vm._chartViewModel.ChartType == ChartType.ReverseWatch;
            int currentIndicatorCount = vm._chartViewModel.Indicators?.Count ?? 0;
            var currentResults = vm._chartViewModel.IndicatorResults;
            var currentConfluence = data.Confluence;
            
            if (isReverseWatch)
            {
                if (vm._lastChartType == ChartType.ReverseWatch &&
                    Equals(vm._lastRwPoint, data.ReverseWatchPoint) &&
                    vm._lastIndicatorCount == currentIndicatorCount &&
                    ReferenceEquals(vm._lastResultsRef, currentResults))
                {
                    return; // State unchanged, skip allocations
                }
                vm._lastChartType = ChartType.ReverseWatch;
                vm._lastRwPoint = data.ReverseWatchPoint;
                vm._lastIndicatorCount = currentIndicatorCount;
                vm._lastResultsRef = currentResults;
            }
            else
            {
                if (vm._lastChartType == vm._chartViewModel.ChartType &&
                    vm._lastCrosshairCandleIndex == data.CandleIndex &&
                    Equals(vm._lastCrosshairCandle, data.HoveredCandle) &&
                    vm._lastIndicatorCount == currentIndicatorCount &&
                    ReferenceEquals(vm._lastResultsRef, currentResults) &&
                    Equals(vm._lastConfluence, currentConfluence))
                {
                    return; // State unchanged, skip allocations
                }
                vm._lastChartType = vm._chartViewModel.ChartType;
                vm._lastCrosshairCandleIndex = data.CandleIndex;
                vm._lastCrosshairCandle = data.HoveredCandle;
                vm._lastIndicatorCount = currentIndicatorCount;
                vm._lastResultsRef = currentResults;
                vm._lastConfluence = currentConfluence;
            }
            
            if (data.ReverseWatchPoint != null)
            {
                vm._lastRwPoint = data.ReverseWatchPoint;
            }
            else if (vm._chartViewModel.ChartType == ChartType.ReverseWatch)
            {
                // FALLBACK to latest calculated point if not hovering
                vm._lastRwPoint = vm._chartViewModel.LatestReverseWatchPoint;
            }
            else
            {
                vm._lastRwPoint = null;
            }
            
            vm.UpdateBasicProperties(data.HoveredCandle);

            if (vm._chartViewModel.ChartType == ChartType.RelativePerformance)
            {
                vm.IsOpenVisible = false;
                vm.IsHighVisible = false;
                vm.IsLowVisible = false;
                vm.IsCloseVisible = false;
                vm.IsVolumeVisible = false;
                
                // Ensure colors are correct for current theme even if floating tooltip is disabled
                vm.UpdateComparisonColors(vm._chartViewModel.ThemeManager?.CurrentTheme?.IsDark ?? true);
                vm.UpdateRelativePerformanceItems(data.CandleIndex);
                return;
            }

            if (vm._chartViewModel.ChartType == ChartType.ReverseWatch)
            {
                if (data.ReverseWatchPoint != null)
                {
                    vm.DateText = data.ReverseWatchPoint.Date.ToString("yyyy/MM/dd");
                    vm.ReverseWatchValueText = data.ReverseWatchPoint.PriceAverage.ToString("0.00");
                    vm.ReverseWatchVolumeText = data.ReverseWatchPoint.VolumeAverage.ToString("N0");
                }
                else
                {
                    vm.DateText = string.Empty;
                    vm.ReverseWatchValueText = string.Empty;
                    vm.ReverseWatchVolumeText = string.Empty;
                }
                
                vm.IsHighVisible = false;
                vm.IsLowVisible = false;
                vm.IsOpenVisible = false;
                vm.IsCloseVisible = false;
                vm.IsVolumeVisible = false;
                
                vm.OpenText = string.Empty;
                vm.HighText = string.Empty;
                vm.LowText = string.Empty;
                vm.CloseText = string.Empty;
                vm.VolumeText = string.Empty;
                vm.KagiReversalText = string.Empty;
                vm.ThreeLineBreakReversalText = string.Empty;
                vm.ThreeLineBreakCountText = string.Empty;
                vm.RenkoCountText = string.Empty;
                vm.RenkoReversalText = string.Empty;
                vm.RenkoUpdateText = string.Empty;
                vm.PointAndFigureCountText = string.Empty;
                vm.IndicatorSectionTitle = vm._localizationService.GetString("DataWindow_Section_ReverseWatch");
                vm.IsConfluenceVisible = false;
                return;
            }

            // If we reach here, data.HoveredCandle is not null (handled by UpdateBasicProperties)
            // and it's not ReverseWatch or RelativePerformance.
            // Basic properties are already set by UpdateBasicProperties.

            int globalIndex = vm._chartViewModel.VisibleStartIndex + data.CandleIndex;
            vm.UpdateSpecializedProperties(globalIndex, data.HoveredCandle);

            vm.UpdateIndicatorItems(vm._chartViewModel.Indicators, vm._chartViewModel.IndicatorResults ?? new Dictionary<string, IIndicatorResult>());
            vm.UpdateDrawingItems(data.HoveredCandle);
            vm.OnPropertyChanged(nameof(HasItems));
        }, (Vm: this, Msg: message));
    }


    private void UpdateBasicProperties(CoreCandleData? candle)
    {
        var candles = _chartViewModel.Candles;
        int globalIndex = -1;
        
        if (candle == null)
        {
            // Feature: Use latest candle if not hovering
            if (candles != null && candles.Count > 0)
            {
                globalIndex = candles.Count - 1;
                candle = candles[globalIndex];
            }
            else
            {
                ClearPanelProperties();
                return;
            }
        }
        else
        {
            // Try to find the exact index if possible
            if (_lastCrosshairData != null && _lastCrosshairData.CandleIndex >= 0)
            {
                globalIndex = _chartViewModel.VisibleStartIndex + _lastCrosshairData.CandleIndex;
                if (globalIndex >= (candles?.Count ?? 0))
                {
                    globalIndex = (candles?.Count ?? 1) - 1;
                }
            }
            else if (candles != null)
            {
                 // Fallback: finding the index
                 globalIndex = -1;
                 for (int i = 0; i < candles.Count; i++)
                 {
                     if (candles[i].Timestamp == candle.Timestamp)
                     {
                         globalIndex = i;
                         break;
                     }
                 }
            }
        }

        IsOpenVisible = true;
        IsHighVisible = true;
        IsLowVisible = true;
        IsCloseVisible = true;
        IsVolumeVisible = true;
        
        if (candle.Timestamp.Hour == 0 && candle.Timestamp.Minute == 0)
        {
             DateText = candle.Timestamp.ToString("yyyy/MM/dd");
        }
        else
        {
             DateText = candle.Timestamp.ToString("yyyy/MM/dd HH:mm");
        }

        OpenText = candle.Open.ToString("0.000");
        HighText = candle.High.ToString("0.000");
        LowText = candle.Low.ToString("0.000");
        CloseText = candle.Close.ToString("0.000");
        VolumeText = candle.Volume.ToString("N0");

        if (candles != null && globalIndex > 0 && globalIndex < candles.Count)
        {
             var prevCandle = candles[globalIndex - 1];
             var diff = candle.Close - prevCandle.Close;
             var ratio = prevCandle.Close > 0 ? (diff / prevCandle.Close) * 100m : 0m;
             YesterdayChangeText = (diff > 0 ? "+" : "") + diff.ToString("0.000");
             YesterdayChangeRatioText = (ratio > 0 ? "+" : "") + ratio.ToString("0.00") + "%";
             var theme = _chartViewModel.ThemeManager.CurrentTheme;
             YesterdayChangeColor = diff > 0 ? theme.SemanticPlus : (diff < 0 ? theme.SemanticMinus : theme.SemanticNeutral);
             YesterdayChangeRatioColor = YesterdayChangeColor;
        }
        else
        {
             YesterdayChangeText = string.Empty;
             YesterdayChangeRatioText = string.Empty;
        }

        // WebAI: Use the latest/hovered point for Reverse Watch texts
        if (_lastRwPoint != null)
        {
            ReverseWatchValueText = _lastRwPoint.PriceAverage.ToString("0.000");
            ReverseWatchVolumeText = _lastRwPoint.VolumeAverage.ToString("N0");
            
            // If in Reverse Watch mode, the OHLCV relative to the point should be shown
            DateText = _lastRwPoint.Date.ToString("yyyy/MM/dd");
            OpenText = _lastRwPoint.Open.ToString("0.000");
            HighText = _lastRwPoint.High.ToString("0.000");
            LowText = _lastRwPoint.Low.ToString("0.000");
            CloseText = _lastRwPoint.Close.ToString("0.000");
            VolumeText = _lastRwPoint.Volume.ToString("N0");
            YesterdayChangeText = string.Empty;
            YesterdayChangeRatioText = string.Empty;
        }
    }

    private void ClearPanelProperties()
    {
        DateText = string.Empty;
        OpenText = string.Empty;
        HighText = string.Empty;
        LowText = string.Empty;
        CloseText = string.Empty;
        VolumeText = string.Empty;
        YesterdayChangeText = string.Empty;
        YesterdayChangeRatioText = string.Empty;
        KagiReversalText = string.Empty;
        ThreeLineBreakReversalText = string.Empty;
        ThreeLineBreakCountText = string.Empty;
        RenkoCountText = string.Empty;
        RenkoReversalText = string.Empty;
        RenkoUpdateText = string.Empty;
        PointAndFigureCountText = string.Empty;
        ReverseWatchValueText = string.Empty;
        ReverseWatchVolumeText = string.Empty;
        IsConfluenceVisible = false;

        IsOpenVisible = true;
        IsHighVisible = true;
        IsLowVisible = true;
        IsCloseVisible = true;
        IsVolumeVisible = true;
    }

    public void Receive(IndicatorResultsUpdatedMessage message)
    {
        _dispatcherService.Post(static vm =>
        {
            vm.UpdateIndicatorItems(vm._chartViewModel.Indicators, vm._chartViewModel.IndicatorResults);
        }, this);
    }

    private void UpdateIndicatorItems(IEnumerable<CoreIndicatorSettings>? settings, IReadOnlyDictionary<string, IIndicatorResult>? results)
    {
        if (_isSyncing || _chartViewModel == null || settings == null || results == null) return;
        
        // Guard against flickering in specialized modes where right panel is managed by mouse-move
        // Specification: Do not show indicators in Comparison or Reverse Watch modes
        if (_chartViewModel.ChartType == ChartType.ReverseWatch || _chartViewModel.ChartType == ChartType.RelativePerformance)
        {
            IndicatorItems.Clear();
            return;
        }

        int itemIndex = 0;
        var indicators = settings as IReadOnlyList<CoreIndicatorSettings> ?? settings.ToList();

        _isSyncing = true;
        try
        {
            var typeOrder = new System.Collections.Generic.List<string>();

            // Lookup index logic
            var snapshot = _chartViewModel.CurrentSnapshot;
            bool useSnapshotResults = snapshot?.ChartType.IsIndexBased() == true 
                                      && snapshot.IndicatorResults != null;
            
            int lookupIndex = -1;
            if (_lastCrosshairData != null && _lastCrosshairData.CandleIndex >= 0)
            {
                lookupIndex = useSnapshotResults 
                    ? _lastCrosshairData.CandleIndex 
                    : _lastCrosshairData.CandleIndex + _chartViewModel.VisibleStartIndex;
            }
            else
            {
                var candles = _chartViewModel.Candles;
                lookupIndex = (candles != null && candles.Count > 0) ? candles.Count - 1 : -1;
            }

            // Categorization: Separate indicators into Type A (Independent) and Type B (Grouped by Type)
            var independentIndicators = new System.Collections.Generic.List<(CoreIndicatorSettings Settings, IIndicatorResult Result, System.Collections.Generic.List<string> ValidSeries, string BaseName)>();
            var groupedIndicatorsByType = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(CoreIndicatorSettings Settings, IIndicatorResult Result, System.Collections.Generic.List<string> ValidSeries)>>();

            foreach (var indicator in indicators)
            {
                string indicatorName = indicator.ShortDisplayName;
                if (string.IsNullOrEmpty(indicatorName)) indicatorName = indicator.DisplayName;
                
                string indicatorBaseName = indicatorName;
                int parenIndex = indicatorBaseName.IndexOf('(');
                if (parenIndex > 0) indicatorBaseName = indicatorBaseName.Substring(0, parenIndex).Trim();

                IIndicatorResult? result = null;
                bool hasResult = results != null && results.TryGetValue(indicator.Id, out result);
                var validSeries = new System.Collections.Generic.List<string>();
                if (hasResult && result != null)
                {
                    var seriesNames = result.SeriesNamesList;
                    if (seriesNames != null)
                    {
                        foreach (var n in seriesNames)
                        {
                            if (n == "BullishSignals" || n == "BearishSignals" || n == "BuySignals" || n == "SellSignals")
                                continue;
                            
                            bool isMainExplicitlyMapped = indicator.SeriesColors != null && indicator.SeriesColors.Any(c => c.TargetSeries.Contains("Main"));
                            if (n == "Main" && !isMainExplicitlyMapped && seriesNames.Any(other => other != "Main" && !other.Contains("Signal") && !other.Contains("Histogram") && !other.Contains("Trend")))
                                continue;

                            validSeries.Add(n);
                        }
                    }
                }

                // 1. Indicators with multiple series defined in metadata (BB, MACD) or produced in results (multi-line)
                //    are always Independent (Type A).
                // 2. Everything else is Grouped (Type B), ensuring stability even when disabled (validSeries.Count == 0).
                bool isMultiSeriesInternal = validSeries.Count > 1 || indicator.SeriesColors.Count > 1;
                bool shouldBeGrouped = !isMultiSeriesInternal;

                if (validSeries.Count > 1 || !shouldBeGrouped)
                {
                    independentIndicators.Add((indicator, result!, validSeries, indicatorBaseName));
                }
                else
                {
                    if (!groupedIndicatorsByType.TryGetValue(indicatorBaseName, out var list))
                    {
                        list = new System.Collections.Generic.List<(CoreIndicatorSettings, IIndicatorResult, System.Collections.Generic.List<string>)>();
                        groupedIndicatorsByType[indicatorBaseName] = list;
                        typeOrder.Add(indicatorBaseName);
                    }
                    list.Add((indicator, result!, validSeries));
                }
            }

            // Render Independent Groups (Type A)
            foreach (var (indicator, result, validSeries, indicatorBaseName) in independentIndicators)
            {
                string indicatorName = indicator.ShortDisplayName;
                if (string.IsNullOrEmpty(indicatorName)) indicatorName = indicator.DisplayName;

                bool isFolder = indicator.SeriesColors.Count > 1 || validSeries.Count > 1;

                if (isFolder)
                {
                    var parentColor = indicator.Color;
                    var parentItem = UpdateOrAddItem(itemIndex++, indicatorName, string.Empty, parentColor, true, false); // useCustomColor = false (Standard policy)
                    
                    parentItem.IsEnabled = indicator.IsEnabled;
                    parentItem.AssociateId = indicator.Id;

                    int childIndex = 0;
                    foreach (var seriesName in validSeries)
                    {
                        decimal? val = null;
                        string label = string.Empty;
                        var childColor = parentColor;

                        var colorConfig = indicator.SeriesColors?.FirstOrDefault(c => c.TargetSeries.Contains(seriesName));
                        if (colorConfig != null)
                        {
                            childColor = colorConfig.Color;
                            if (!string.IsNullOrEmpty(colorConfig.DisplayName))
                            {
                                label = colorConfig.DisplayName;
                            }
                        }

                        if (result != null)
                        {
                            var series = result.GetSeries(seriesName);
                            val = (series != null && lookupIndex >= 0 && lookupIndex < series.Count) ? series[lookupIndex] : null;

                            if (string.IsNullOrEmpty(label))
                            {
                                label = result.SeriesLabels.TryGetValue(seriesName, out var cachedLabel)
                                    ? cachedLabel
                                    : (seriesName == "Main" ? indicatorBaseName : seriesName);
                            }
                        }
                        else if (string.IsNullOrEmpty(label))
                        {
                            label = seriesName == "Main" ? indicatorBaseName : seriesName;
                        }

                        if (label == "FFT Cycle Period")
                        {
                            label = "FFT Cycle";
                        }

                        var childItem = UpdateOrAddChildItem(parentItem, childIndex++, label, val, childColor, false); // useCustomColor = false
                        childItem.IsEnabled = indicator.IsEnabled;
                        childItem.AssociateId = indicator.Id; // Keep ID for OpenSettings
                        childItem.ShowCheckbox = false; // Hide checkbox for child entries
                        childItem.OpenSettingsCommand = OpenSettingsCommand;
                    }
                    while (parentItem.Children.Count > childIndex) 
                    {
                        var removed = parentItem.Children[parentItem.Children.Count - 1];
                        removed.PropertyChanged -= Item_PropertyChanged;
                        parentItem.Children.RemoveAt(parentItem.Children.Count - 1);
                    }
                }
                else
                {
                    decimal? val = null;
                    if (result != null && validSeries.Count == 1)
                    {
                        var series = result.GetSeries(validSeries[0]);
                        val = (series != null && lookupIndex >= 0 && lookupIndex < series.Count) ? series[lookupIndex] : null;
                    }
                    else if (result != null)
                    {
                        var series = result.MainValues;
                        val = (series != null && lookupIndex >= 0 && lookupIndex < series.Count) ? series[lookupIndex] : null;
                    }

                    var c = indicator.Color;
                    var item = UpdateOrAddItem(itemIndex++, indicatorName, val.HasValue ? val.Value.ToString("0.000") : "N/A", c, false, false); // useCustomColor = false
                    item.OpenSettingsCommand = OpenSettingsCommand;
                    
                    item.IsEnabled = indicator.IsEnabled;
                    item.AssociateId = indicator.Id;
                }
            }

            // Render Grouped Indicators (Type B)
            foreach (var typeName in typeOrder)
            {
                var group = groupedIndicatorsByType[typeName];
                if (group.Count == 0) continue;

                var firstInd = group[0].Settings;
                var parentColor = firstInd.Color;
                var parentItem = UpdateOrAddItem(itemIndex++, typeName, string.Empty, parentColor, true, false); // useCustomColor = false (standard)

                parentItem.IsEnabled = group.Any(g => g.Settings.IsEnabled);
                parentItem.AssociateId = typeName; // Type name for group toggle

                int childIndex = 0;
                foreach (var (indicator, result, validSeries) in group)
                {
                    decimal? val = null;
                    if (result != null && validSeries != null && validSeries.Count > 0)
                    {
                        var series = result.GetSeries(validSeries[0]);
                        val = (series != null && lookupIndex >= 0 && lookupIndex < series.Count) ? series[lookupIndex] : null;
                    }

                    indicator.UpdateDisplayName();
                    string displayLabel = indicator.ShortDisplayName; 
                    if (string.IsNullOrEmpty(displayLabel)) displayLabel = indicator.DisplayName;
                    if (string.IsNullOrEmpty(displayLabel)) displayLabel = indicator.TypeEnum?.ToString() ?? "Indicator";

                    var c = indicator.Color;
                    var childItem = UpdateOrAddChildItem(parentItem, childIndex++, displayLabel, val, c, false); // useCustomColor = false

                    childItem.IsEnabled = indicator.IsEnabled;
                    childItem.AssociateId = indicator.Id;
                    childItem.ShowCheckbox = true; // Type B children are individual indicators, need toggles
                }
                while (parentItem.Children.Count > childIndex) 
                {
                    var removed = parentItem.Children[parentItem.Children.Count - 1];
                    if (removed != null)
                    {
                        removed.PropertyChanged -= Item_PropertyChanged;
                    }
                    parentItem.Children.RemoveAt(parentItem.Children.Count - 1);
                }
            }

            while (IndicatorItems.Count > itemIndex)
            {
                var removed = IndicatorItems[IndicatorItems.Count - 1];
                if (removed != null)
                {
                    removed.PropertyChanged -= Item_PropertyChanged;
                }
                IndicatorItems.RemoveAt(IndicatorItems.Count - 1);
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private DataWindowItemViewModel UpdateOrAddChildItem(DataWindowItemViewModel parent, int index, string displayName, decimal? val, IndicatorColor color, bool useCustomColor = false)
    {
        string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";
        DataWindowItemViewModel item;
        if (index < parent.Children.Count)
        {
            item = parent.Children[index];
            if (item.Name != displayName) item.Name = displayName;
            if (item.Value != valStr) item.Value = valStr;
            if (item.Color != color) item.Color = color;
            if (item.UseCustomColor != useCustomColor) item.UseCustomColor = useCustomColor;
            item.ShowCheckbox = false;
            item.OpenSettingsCommand = OpenSettingsCommand;
        }
        else
        {
            item = new DataWindowItemViewModel
            {
                Name = displayName,
                Value = valStr,
                Color = color,
                UseCustomColor = useCustomColor,
                HasChildren = false,
                ShowCheckbox = false,
                OpenSettingsCommand = OpenSettingsCommand
            };
            item.PropertyChanged += Item_PropertyChanged;
            parent.Children.Add(item);
        }
        return item;
    }

    private DataWindowItemViewModel UpdateOrAddItem(int index, string displayName, string value, IndicatorColor color, bool hasChildren = false, bool useCustomColor = false, bool showCheckbox = true)
    {
        DataWindowItemViewModel item;
        if (index < IndicatorItems.Count)
        {
            item = IndicatorItems[index];
            if (item.Name != displayName) item.Name = displayName;
            if (item.Value != value) item.Value = value;
            if (item.Color != color) item.Color = color;
            if (item.HasChildren != hasChildren) item.HasChildren = hasChildren;
            if (item.UseCustomColor != useCustomColor) item.UseCustomColor = useCustomColor;
            if (item.ShowCheckbox != showCheckbox) item.ShowCheckbox = showCheckbox;
            item.OpenSettingsCommand = OpenSettingsCommand;
        }
        else
        {
            item = new DataWindowItemViewModel
            {
                Name = displayName,
                Value = value,
                Color = color,
                HasChildren = hasChildren,
                UseCustomColor = useCustomColor,
                ShowCheckbox = showCheckbox,
                IsEnabled = true,
                OpenSettingsCommand = OpenSettingsCommand,
                ToggleVisibilityCommand = ToggleVisibilityCommand,
                ToggleGroupVisibilityCommand = ToggleGroupVisibilityCommand,
                ToggleGroupAllOnCommand = ToggleGroupAllOnCommand,
                ToggleGroupAllOffCommand = ToggleGroupAllOffCommand
            };
            item.PropertyChanged += Item_PropertyChanged;
            IndicatorItems.Add(item);
        }
        return item;
    }

    private void UpdateRelativePerformanceItems(int localIndex)
    {
        var snapshot = _chartViewModel.CurrentSnapshot;
        if (snapshot?.ComparisonSeries == null)
        {
            if (IndicatorItems.Count > 0) IndicatorItems.Clear();
            return;
        }

        int itemIndex = 0;
        var mode = _chartViewModel.ComparisonMode;

        // Implementation Step 1.3: Set Dynamic Unit Header
        IndicatorSectionTitle = mode switch
        {
            ComparisonMode.Performance => _localizationService.GetString("DataWindow_Unit_Performance"),
            ComparisonMode.Ratio => _localizationService.GetString("DataWindow_Unit_Ratio"),
            ComparisonMode.ZScore => _localizationService.GetString("DataWindow_Unit_ZScore"),
            ComparisonMode.Spread => _localizationService.GetString("DataWindow_Unit_Spread"),
            _ => "Comparison"
        };

        foreach (var kvp in snapshot.ComparisonSeries)
        {
            var symbol = kvp.Key;
            var values = kvp.Value;

            if (localIndex >= 0 && localIndex < values.Length)
            {
                var val = values[localIndex];
                string valStr;
                
                if (val.HasValue)
                {
                    valStr = mode switch
                    {
                        ComparisonMode.Performance => val.Value.ToString(ChartConstants.DefaultRelativePerformanceFormat) + ChartConstants.DefaultRelativePerformanceSuffix,
                        ComparisonMode.Ratio => val.Value.ToString(ChartConstants.DefaultRatioFormat),
                        ComparisonMode.ZScore => $"{ChartConstants.ZScorePrefix}{val.Value.ToString(ChartConstants.DefaultZScoreFormat)}",
                        ComparisonMode.Spread => val.Value.ToString(ChartConstants.DefaultSpreadFormat),
                        _ => val.Value.ToString("F2")
                    };
                }
                else
                {
                    valStr = "N/A";
                }

                int colorIndex = _chartViewModel.SeriesColorIndex.GetOrAdd(symbol);
                var color = _comparisonColors[colorIndex % _comparisonColors.Length];
                
                string displayName = symbol;
                if (symbol == _chartViewModel.ComparisonData?.PrimarySymbol && (mode == ComparisonMode.Ratio || mode == ComparisonMode.Spread))
                {
                    displayName += ChartConstants.BaseLabelSuffix;
                    // Apply visual "demotion" to base series by reducing alpha
                    color = color.WithAlpha(ChartConstants.BaseAlpha);
                }

                UpdateOrAddItem(itemIndex++, displayName, valStr, color, false, true, false); // showCheckbox = false
            }
        }

        // Cleanup trailing items if comparison count decreased
        int specializedEnd = itemIndex;
        while (IndicatorItems.Count > specializedEnd && (IndicatorItems[specializedEnd].AssociateId == null || !Guid.TryParse(IndicatorItems[specializedEnd].AssociateId, out _)))
        {
             // Remove items that are not indicators (e.g. leftover comparison items from a larger list)
             // In Comparison mode, specialized items don't have GUID AssociateIds.
             IndicatorItems.RemoveAt(specializedEnd);
        }
    }

    private int GetSpecializedOffset()
    {
        if (_chartViewModel == null) return 0;
        if (_chartViewModel.ChartType == ChartType.RelativePerformance)
        {
            return _chartViewModel.CurrentSnapshot?.ComparisonSeries?.Count ?? 0;
        }
        return 0;
    }

    private void UpdateOrAddItem(int index, string displayName, decimal? val, CoreIndicatorSettings indicator)
    {
        string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";
        var c = indicator.Color;
        var color = c;
        UpdateOrAddItem(index, displayName, valStr, color, false, false, true); // useCustomColor = false
    }

    private void UpdateFloatingTooltip(CrosshairPositionData data)
    {
        if (_chartViewModel.ChartType != ChartType.RelativePerformance || 
            !_chartViewModel.ShowFloatingTooltip || 
            !data.ScreenX.HasValue || !data.ScreenY.HasValue || 
            data.CandleIndex < 0)
        {
            IsComparisonTooltipVisible = false;
            return;
        }

        // 1. Set Position with Offset
        TooltipLeft = data.ScreenX.Value + 15;
        TooltipTop = data.ScreenY.Value + 15;

        // 2. Populate Tooltip Data
        var comparisonData = _chartViewModel.ComparisonData;
        if (comparisonData == null)
        {
            IsComparisonTooltipVisible = false;
            return;
        }

        int globalIndex = _chartViewModel.VisibleStartIndex + data.CandleIndex;
        if (globalIndex < 0 || globalIndex >= comparisonData.Timestamps.Length)
        {
            IsComparisonTooltipVisible = false;
            return;
        }

        var snapshot = _chartViewModel.CurrentSnapshot;
        if (snapshot?.ComparisonSeries == null)
        {
            IsComparisonTooltipVisible = false;
            return;
        }

        IsComparisonTooltipVisible = true;
        TooltipDateText = comparisonData.Timestamps[globalIndex].ToString("yyyy/MM/dd HH:mm");

        // Ensure colors are correct for current theme
        UpdateComparisonColors(_chartViewModel.ThemeManager.CurrentTheme.IsDark);

        int itemIndex = 0;
        var mode = _chartViewModel.ComparisonMode;
        
        foreach (var kvp in snapshot.ComparisonSeries)
        {
            var symbol = kvp.Key;
            var values = kvp.Value;
            if (values == null || data.CandleIndex >= values.Length) continue;
            
            var val = values[data.CandleIndex];
            if (val.HasValue)
            {
                // Current absolute value (Price)
                string actStr = "N/A";
                if (comparisonData.Series.TryGetValue(symbol, out var candleSeries) && globalIndex < candleSeries.Length)
                {
                    var candle = candleSeries[globalIndex];
                    if (candle.HasValue) actStr = candle.Value.Close.ToString("N3");
                }

                // Relative value (Mode-specific)
                string relStr = mode switch
                {
                    ComparisonMode.Performance => val.Value.ToString(ChartConstants.DefaultRelativePerformanceFormat) + ChartConstants.DefaultRelativePerformanceSuffix,
                    ComparisonMode.Ratio => val.Value.ToString(ChartConstants.DefaultRatioFormat),
                    ComparisonMode.ZScore => $"{ChartConstants.ZScorePrefix}{val.Value.ToString(ChartConstants.DefaultZScoreFormat)}",
                    ComparisonMode.Spread => val.Value.ToString(ChartConstants.DefaultSpreadFormat),
                    _ => val.Value.ToString("F2")
                };

                int colorIndex = _chartViewModel.SeriesColorIndex.GetOrAdd(symbol);
                var color = _comparisonColors[colorIndex % _comparisonColors.Length];
                
                string displayName = symbol;
                if (symbol == comparisonData.PrimarySymbol && (mode == ComparisonMode.Ratio || mode == ComparisonMode.Spread))
                {
                    displayName += ChartConstants.BaseLabelSuffix;
                }

                UpdateOrAddTooltipItem(itemIndex++, displayName, actStr, relStr, color);
            }
        }

        // Cleanup trailing items
        while (ComparisonTooltipItems.Count > itemIndex) ComparisonTooltipItems.RemoveAt(ComparisonTooltipItems.Count - 1);
    }

    private void UpdateOrAddTooltipItem(int index, string name, string actual, string percent, IndicatorColor color)
    {
        if (index < ComparisonTooltipItems.Count)
        {
            var item = ComparisonTooltipItems[index];
            if (item.StockName != name) item.StockName = name;
            if (item.ActualValueText != actual) item.ActualValueText = actual;
            if (item.IndexPercentText != percent) item.IndexPercentText = percent;
            if (item.IconColor != color) item.IconColor = color;
        }
        else
        {
            ComparisonTooltipItems.Add(new ComparisonTooltipItemViewModel
            {
                StockName = name,
                ActualValueText = actual,
                IndexPercentText = percent,
                IconColor = color
            });
        }
    }

    private void UpdateSpecializedProperties(int globalIndex, CoreCandleData? hoveredCandle)
    {
        KagiReversalText = string.Empty;
        ThreeLineBreakReversalText = string.Empty;
        ThreeLineBreakCountText = string.Empty;
        RenkoCountText = string.Empty;
        RenkoReversalText = string.Empty;
        RenkoUpdateText = string.Empty;
        PointAndFigureCountText = string.Empty;
        ReverseWatchValueText = string.Empty;
        ReverseWatchVolumeText = string.Empty;

        if (_chartViewModel.ChartType == ChartType.Kagi && _chartViewModel.ConvertedKagiCandles != null)
        {
            var candles = _chartViewModel.ConvertedKagiCandles;
            if (globalIndex >= 0 && globalIndex < candles.Count)
            {
                bool isUp = candles[globalIndex].Close >= candles[globalIndex].Open;
                
                int colStart = globalIndex;
                while (colStart > 0 && (candles[colStart - 1].Close >= candles[colStart - 1].Open) == isUp)
                    colStart--;

                int colEnd = globalIndex;
                while (colEnd < (candles.Count - 1) && (candles[colEnd + 1].Close >= candles[colEnd + 1].Open) == isUp)
                    colEnd++;

                decimal startPrice = candles[colStart].Open;
                decimal endPrice = candles[colEnd].Close;
                KagiReversalText = System.Math.Abs(endPrice - startPrice).ToString("0.000");
            }
            IsHighVisible = false;
            IsLowVisible = false;
            IsVolumeVisible = false;
        }
        else if (_chartViewModel.ChartType == ChartType.Renko && _chartViewModel.RenkoDataAdapter != null)
        {
            var adapter = _chartViewModel.RenkoDataAdapter;
            if (globalIndex >= 0 && globalIndex < adapter.Count)
            {
                var closes = adapter.Closes.Span;
                var opens = adapter.Opens.Span;
                
                bool isUp = closes[globalIndex] >= opens[globalIndex];
                
                int currentSequenceStart = globalIndex;
                while (currentSequenceStart > 0 && (closes[currentSequenceStart - 1] >= opens[currentSequenceStart - 1]) == isUp)
                {
                    currentSequenceStart--;
                }
                
                int count = globalIndex - currentSequenceStart + 1;
                RenkoCountText = count.ToString();
                
                decimal brickHigh = System.Math.Max(opens[globalIndex], closes[globalIndex]);
                decimal brickLow = System.Math.Min(opens[globalIndex], closes[globalIndex]);
                decimal brickSize = _chartViewModel.EffectiveRenkoBrickSize;
                decimal reversalRequirement = _chartViewModel.RenkoReversal; 
                
                decimal reversalPrice = isUp ? brickLow - (reversalRequirement * brickSize) : brickHigh + (reversalRequirement * brickSize);
                decimal nextBrickPrice = isUp ? brickHigh + brickSize : brickLow - brickSize;
                
                RenkoReversalText = reversalPrice.ToString("0.000");
                RenkoUpdateText = nextBrickPrice.ToString("0.000");
            }
            IsHighVisible = false;
            IsLowVisible = false;
            IsVolumeVisible = false;
        }
        else if (_chartViewModel.ChartType == ChartType.PointAndFigure && _chartViewModel.PnfDataAdapter != null)
        {
            var adapter = _chartViewModel.PnfDataAdapter;
            if (globalIndex >= 0 && globalIndex < adapter.Count)
            {
                var highs = adapter.Highs.Span;
                var lows = adapter.Lows.Span;
                var boxSize = _chartViewModel.EffectivePnfBoxSize;
                
                if (boxSize > 0)
                {
                    int trendCount = System.Math.Max(1, (int)System.Math.Round((double)((highs[globalIndex] - lows[globalIndex]) / boxSize)) + 1);
                    PointAndFigureCountText = trendCount.ToString();
                }
            }
            IsVolumeVisible = false;
        }
        else if (_chartViewModel.ChartType == ChartType.ThreeLineBreak && _chartViewModel.Candles != null)
        {
            var candles = _chartViewModel.Candles;
            if (globalIndex >= 0 && hoveredCandle != null)
            {
                int lineCount = _chartViewModel.ThreeLineBreakLineCount;
                if (!ReferenceEquals(candles, _cachedCandlesRef) || lineCount != _cachedLineCount || _cachedBlocks.IsDefault)
                {
                    _cachedBlocks = StockAnalyzer.Core.Utilities.ThreeLineBreakConverter.Convert(candles, lineCount);
                    _cachedCandlesRef = candles;
                    _cachedLineCount = lineCount;
                }
                
                var blocks = _cachedBlocks;
                int targetBlockIndex = -1;
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i].EndDate == hoveredCandle.Timestamp)
                    {
                        targetBlockIndex = i;
                        break;
                    }
                }
                
                if (targetBlockIndex >= 0 && targetBlockIndex < blocks.Length)
                {
                    var targetBlock = blocks[targetBlockIndex];
                    bool currentIsUp = targetBlock.IsUp;
                    
                    int count = 1;
                    for (int i = targetBlockIndex - 1; i >= 0; i--)
                    {
                        if (blocks[i].IsUp == currentIsUp) count++; else break;
                    }
                    ThreeLineBreakCountText = count.ToString();
                    
                    decimal reversalPrice = 0;
                    if (targetBlockIndex >= lineCount)
                    {
                        int lookback = lineCount;
                        if (currentIsUp)
                        {
                            decimal revLow = decimal.MaxValue;
                            int startIdx = targetBlockIndex - lookback;
                            for (int b = startIdx; b < targetBlockIndex; b++)
                                if (blocks[b].Low < revLow) revLow = blocks[b].Low;
                            reversalPrice = revLow;
                        }
                        else
                        {
                            decimal revHigh = decimal.MinValue;
                            int startIdx = targetBlockIndex - lookback;
                            for (int b = startIdx; b < targetBlockIndex; b++)
                                if (blocks[b].High > revHigh) revHigh = blocks[b].High;
                            reversalPrice = revHigh;
                        }
                    }
                    else
                    {
                        reversalPrice = currentIsUp ? targetBlock.Low : targetBlock.High;
                    }
                    
                    ThreeLineBreakReversalText = reversalPrice.ToString("F2");
                }
            }
            IsHighVisible = false;
            IsLowVisible = false;
            IsVolumeVisible = false;
        }
    }

    private void UpdateDrawingItems(CoreCandleData? candle)
    {
        if (_chartViewModel == null) return;
        var manager = _chartViewModel.ObjectManager;
        if (manager == null || manager.Objects.Count == 0)
        {
            if (DrawingItems.Count > 0) DrawingItems.Clear();
            return;
        }

        DateTime timestamp = candle?.Timestamp ?? (_chartViewModel.Candles != null && _chartViewModel.Candles.Count > 0 ? _chartViewModel.Candles[^1].Timestamp : DateTime.MinValue);
        decimal? currentPrice = candle?.Close;

        int itemIndex = 0;
        foreach (var obj in manager.Objects)
        {
            if (!obj.IsVisible) continue;
            if (obj is not IDrawingCalculatedValuesProvider provider) continue;

            var values = provider.GetCalculatedValues(timestamp, currentPrice);
            if (values == null || values.Count == 0) continue;

            string displayName = DrawingObjectDisplayNameHelper.GetDisplayName(obj);
            var objColor = new IndicatorColor(obj.Color.A, obj.Color.R, obj.Color.G, obj.Color.B);

            if (values.Count == 1)
            {
                var val = values[0];
                var item = UpdateOrAddDrawingItem(itemIndex++, $"{displayName} - {val.Label}", val.FormattedText, val.Color, false);
                item.IsEnabled = obj.IsVisible;
                item.AssociateId = obj.Id.ToString();
                item.ShowCheckbox = false;
            }
            else
            {
                var parentItem = UpdateOrAddDrawingItem(itemIndex++, displayName, string.Empty, objColor, true);
                parentItem.IsEnabled = obj.IsVisible;
                parentItem.AssociateId = obj.Id.ToString();
                parentItem.ShowCheckbox = false;

                int childIndex = 0;
                foreach (var val in values)
                {
                    var child = UpdateOrAddDrawingChildItem(parentItem, childIndex++, val.Label, val.FormattedText, val.Color);
                    child.IsEnabled = obj.IsVisible;
                    child.AssociateId = obj.Id.ToString();
                    child.ShowCheckbox = false;
                }
                while (parentItem.Children.Count > childIndex)
                {
                    parentItem.Children.RemoveAt(parentItem.Children.Count - 1);
                }
            }
        }

        while (DrawingItems.Count > itemIndex)
        {
            DrawingItems.RemoveAt(DrawingItems.Count - 1);
        }
    }

    private DataWindowItemViewModel UpdateOrAddDrawingChildItem(DataWindowItemViewModel parent, int index, string displayName, string valStr, IndicatorColor color)
    {
        DataWindowItemViewModel item;
        if (index < parent.Children.Count)
        {
            item = parent.Children[index];
            if (item.Name != displayName) item.Name = displayName;
            if (item.Value != valStr) item.Value = valStr;
            if (item.Color != color) item.Color = color;
            item.ShowCheckbox = false;
        }
        else
        {
            item = new DataWindowItemViewModel
            {
                Name = displayName,
                Value = valStr,
                Color = color,
                HasChildren = false,
                ShowCheckbox = false
            };
            parent.Children.Add(item);
        }
        return item;
    }

    private DataWindowItemViewModel UpdateOrAddDrawingItem(int index, string displayName, string value, IndicatorColor color, bool hasChildren = false)
    {
        DataWindowItemViewModel item;
        if (index < DrawingItems.Count)
        {
            item = DrawingItems[index];
            if (item.Name != displayName) item.Name = displayName;
            if (item.Value != value) item.Value = value;
            if (item.Color != color) item.Color = color;
            if (item.HasChildren != hasChildren) item.HasChildren = hasChildren;
            item.ShowCheckbox = false;
        }
        else
        {
            item = new DataWindowItemViewModel
            {
                Name = displayName,
                Value = value,
                Color = color,
                HasChildren = hasChildren,
                ShowCheckbox = false,
                IsEnabled = true
            };
            DrawingItems.Add(item);
        }
        return item;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_chartViewModel != null)
        {
            _chartViewModel.PropertyChanged -= ChartViewModel_PropertyChanged;
            if (_chartViewModel.ObjectManager != null)
            {
                _chartViewModel.ObjectManager.Synced -= OnObjectManagerSynced;
            }
        }

        _messenger?.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
