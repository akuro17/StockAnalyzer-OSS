using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.ViewModels;

public enum ScatterViewMode
{
    ScatterPlot,
    Heatmap
}

/// <summary>
/// ViewModel managing the Scatter Plot and Uniform Grid Heatmap dual-view tab.
/// Provides seamless view mode switching, statistical analysis evaluation via <see cref="ScatterAnalysisEngine"/>,
/// freeze-eliminated in-memory dataset synchronization, and decoupled messenger communication.
/// </summary>
public partial class ScatterPlotViewModel : ViewModelBase,
    IDisposable,
    IRecipient<ActiveTickerGroupChangedMessage>,
    IRecipient<TickerSelectedMessage>
{
    private readonly IMessenger _messenger;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ScatterPlotViewModel> _logger;
    private readonly IDispatcherService _dispatcherService;
    private readonly IScatterMetricRegistry? _metricRegistry;
    private readonly IScatterMetricEvaluator? _metricEvaluator;
    private readonly IDialogService? _dialogService;
    private readonly IHeatmapReturnProvider _heatmapProvider;
    private readonly IChartSettingsManager? _chartSettingsManager;

    private long _currentRevision;
    private CancellationTokenSource? _currentCts;
    private long _heatmapRevision;
    private CancellationTokenSource? _heatmapCts;
    private IReadOnlyList<string>? _currentTickers;

    // The registered metrics by Id (the confirmed list), and the (Id, content) pair each axis was last evaluated with.
    private IReadOnlyDictionary<Guid, ScatterMetricDefinition> _metricsById = new Dictionary<Guid, ScatterMetricDefinition>();
    private (Guid Id, string Key)? _evaluatedX;
    private (Guid Id, string Key)? _evaluatedY;
    private bool _isSyncingMetrics;
    private AxisRange? _xFixedRange;
    private AxisRange? _yFixedRange;

    // The scale each axis had when the user last chose it (this tab only; not saved). A fixed-range metric draws its axis linear whatever this says,
    // and the choice returns as soon as the axis has a metric without a fixed range. Only a change that does not come from ApplyFixedAxes is a choice.
    private bool _userXLog;
    private bool _userYLog;
    private bool _isApplyingFixedAxes;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScatterMode))]
    [NotifyPropertyChangedFor(nameof(IsHeatmapMode))]
    [NotifyPropertyChangedFor(nameof(ShowNoMetricsMessage))]
    private ScatterViewMode _currentMode = ScatterViewMode.ScatterPlot;

    public bool IsScatterMode => CurrentMode == ScatterViewMode.ScatterPlot;
    public bool IsHeatmapMode => CurrentMode == ScatterViewMode.Heatmap;

    [ObservableProperty] private ScatterDataset? _dataset;
    [ObservableProperty] private ScatterAnalysisResult? _analysisResult;
    [ObservableProperty] private IReadOnlyList<UniformHeatmapItem>? _heatmapItems;

    [ObservableProperty] private bool _showRegressionLine = true;
    [ObservableProperty] private bool _showConfidenceBand = true;
    [ObservableProperty] private bool _showPredictionBand = false;
    [ObservableProperty] private bool _suppressOutliers = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXLog))]
    private PriceScaleType _xScale = PriceScaleType.Linear;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYLog))]
    private PriceScaleType _yScale = PriceScaleType.Linear;

    partial void OnXScaleChanged(PriceScaleType value)
    {
        if (!_isApplyingFixedAxes) _userXLog = value == PriceScaleType.Log;
    }

    partial void OnYScaleChanged(PriceScaleType value)
    {
        if (!_isApplyingFixedAxes) _userYLog = value == PriceScaleType.Log;
    }

    public bool IsXLog
    {
        get => XScale == PriceScaleType.Log && IsXLogEnabled;
        set
        {
            if (!IsXLogEnabled) return;
            var target = value ? PriceScaleType.Log : PriceScaleType.Linear;
            if (XScale != target)
            {
                XScale = target;
            }
        }
    }

    public bool IsYLog
    {
        get => YScale == PriceScaleType.Log && IsYLogEnabled;
        set
        {
            if (!IsYLogEnabled) return;
            var target = value ? PriceScaleType.Log : PriceScaleType.Linear;
            if (YScale != target)
            {
                YScale = target;
            }
        }
    }

    /// <summary>False while the X metric has a fixed value range: that axis is linear and the log toggle is disabled.</summary>
    public bool IsXLogEnabled => !_xFixedRange.HasValue;

    /// <summary>False while the Y metric has a fixed value range: that axis is linear and the log toggle is disabled.</summary>
    public bool IsYLogEnabled => !_yFixedRange.HasValue;

    /// <summary>The reason the X log toggle is disabled; null while it is enabled (a tooltip of a disabled control must come from the view model).</summary>
    public string? XLogToolTip => IsXLogEnabled ? null : _localizationService.GetString("Scatter_Tip_FixedAxisLog");

    /// <summary>The reason the Y log toggle is disabled; null while it is enabled.</summary>
    public string? YLogToolTip => IsYLogEnabled ? null : _localizationService.GetString("Scatter_Tip_FixedAxisLog");

    [ObservableProperty]
    private HeatmapPalette _heatmapPalette = HeatmapPalette.Default;

    public int HeatmapOffsetMin => HeatmapOffsetLimits.Min;
    public int HeatmapOffsetMax => HeatmapOffsetLimits.Max;

    [ObservableProperty]
    private int _heatmapOffset;

    partial void OnHeatmapOffsetChanged(int value)
    {
        if (value < HeatmapOffsetLimits.Min || value > HeatmapOffsetLimits.Max)
        {
            HeatmapOffset = HeatmapOffsetLimits.Clamp(value);
            return;
        }

        _ = RefreshHeatmapAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeatmapTargetDateText))]
    private DateOnly? _heatmapTargetDate;

    private const string HeatmapDateFormat = "yyyy-MM-dd";
    private const string HeatmapNoDateText = "—";

    public string HeatmapTargetDateText => HeatmapTargetDate?.ToString(HeatmapDateFormat, CultureInfo.InvariantCulture) ?? HeatmapNoDateText;

    [ObservableProperty] private ScatterMetricOption? _selectedXMetric;
    [ObservableProperty] private ScatterMetricOption? _selectedYMetric;
    [ObservableProperty] private string _groupTitle = "All Tickers";
    [ObservableProperty] private string? _selectedTicker;
    [ObservableProperty] private int? _selectedPointIndex;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>The registered scatter metrics, in registration order; empty until the user registers one.</summary>
    public ObservableCollection<ScatterMetricOption> AvailableMetrics { get; } = new BulkObservableCollection<ScatterMetricOption>();

    public bool HasRegisteredMetrics => AvailableMetrics.Count > 0;

    /// <summary>True in scatter mode while nothing is registered: the screen shows the guidance text instead of a plot.</summary>
    public bool ShowNoMetricsMessage => IsScatterMode && !HasRegisteredMetrics;

    public ScatterPlotViewModel(
        IMessenger messenger,
        IMarketDataProvider? marketDataProvider,
        ILocalizationService localizationService,
        ILogger<ScatterPlotViewModel> logger,
        IDispatcherService dispatcherService,
        IHeatmapReturnProvider? heatmapProvider = null,
        IChartSettingsManager? chartSettingsManager = null,
        IScatterMetricRegistry? registry = null,
        IScatterMetricEvaluator? evaluator = null,
        IDialogService? dialogService = null)
    {
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _heatmapProvider = heatmapProvider ?? SyntheticHeatmapReturnProvider.Instance;
        _chartSettingsManager = chartSettingsManager;
        _metricRegistry = registry;
        _metricEvaluator = evaluator;
        _dialogService = dialogService;

        if (_chartSettingsManager != null)
        {
            _heatmapPalette = HeatmapPalette.FromSettings(_chartSettingsManager.Current);
            _chartSettingsManager.SettingsChanged += OnChartSettingsChanged;
        }
        else
        {
            _heatmapPalette = HeatmapPalette.Default;
        }

        _messenger.Register<ActiveTickerGroupChangedMessage>(this);
        _messenger.Register<TickerSelectedMessage>(this);

        // The registry is listened to first and read second, so a load that finishes in between is not missed (the handler just reads it again).
        if (_metricRegistry != null)
        {
            _metricRegistry.Changed += OnMetricRegistryChanged;
            SyncAvailableMetrics();
        }

        // Initialize with default sample population
        PopulateDefaultDataset();
    }

    private void OnChartSettingsChanged()
    {
        _dispatcherService.Post(() =>
        {
            if (_chartSettingsManager != null)
            {
                HeatmapPalette = HeatmapPalette.FromSettings(_chartSettingsManager.Current);
            }
        });
    }

    [RelayCommand]
    private void SetScatterMode()
    {
        CurrentMode = ScatterViewMode.ScatterPlot;
    }

    [RelayCommand]
    private void SetHeatmapMode()
    {
        CurrentMode = ScatterViewMode.Heatmap;
    }

    [RelayCommand]
    private void ToggleXScale()
    {
        if (!IsXLogEnabled) return;
        XScale = XScale == PriceScaleType.Linear ? PriceScaleType.Log : PriceScaleType.Linear;
    }

    [RelayCommand]
    private void ToggleYScale()
    {
        if (!IsYLogEnabled) return;
        YScale = YScale == PriceScaleType.Linear ? PriceScaleType.Log : PriceScaleType.Linear;
    }

    [RelayCommand]
    private void ToggleRegressionLine()
    {
        ShowRegressionLine = !ShowRegressionLine;
    }

    [RelayCommand]
    private void ToggleConfidenceBand()
    {
        ShowConfidenceBand = !ShowConfidenceBand;
    }

    [RelayCommand]
    private void TogglePredictionBand()
    {
        ShowPredictionBand = !ShowPredictionBand;
    }

    [RelayCommand]
    private void ToggleSuppressOutliers()
    {
        SuppressOutliers = !SuppressOutliers;
    }

    private bool _isHandlingExternalSelection;

    [RelayCommand]
    private void SelectTicker(string? ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        SelectedTicker = ticker;
    }

    partial void OnSelectedPointIndexChanged(int? value)
    {
        if (value.HasValue && Dataset?.Labels != null && value.Value >= 0 && value.Value < Dataset.Labels.Count)
        {
            string ticker = Dataset.Labels[value.Value];
            if (!string.Equals(SelectedTicker, ticker, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTicker = ticker;
            }
        }
    }

    partial void OnSelectedTickerChanged(string? value)
    {
        if (_isHandlingExternalSelection) return;

        if (!string.IsNullOrWhiteSpace(value))
        {
            _messenger.Send(new TickerSelectedMessage(value));

            if (Dataset?.Labels != null)
            {
                int idx = -1;
                for (int i = 0; i < Dataset.Labels.Count; i++)
                {
                    if (string.Equals(Dataset.Labels[i], value, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                int? targetIdx = idx >= 0 ? idx : null;
                if (SelectedPointIndex != targetIdx)
                {
                    SelectedPointIndex = targetIdx;
                }
            }
        }
    }

    partial void OnSelectedXMetricChanged(ScatterMetricOption? value)
    {
        if (!_isSyncingMetrics) ReevaluateDataset();
    }

    partial void OnSelectedYMetricChanged(ScatterMetricOption? value)
    {
        if (!_isSyncingMetrics) ReevaluateDataset();
    }

    // ===== Registered metrics =====

    private void OnMetricRegistryChanged(object? sender, EventArgs e) => _dispatcherService.Post(SyncAvailableMetrics);

    private bool CanOpenMetricRegistration() => _dialogService != null;

    /// <summary>Opens the metric registration window; what the user confirms there reaches this screen through the registry's change event.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenMetricRegistration))]
    private async Task OpenMetricRegistrationAsync()
    {
        if (_dialogService != null)
        {
            await _dialogService.ShowScatterMetricRegistrationDialogAsync();
        }
    }

    /// <summary>
    /// Rebuilds the list of metrics from the registry and repairs the two selections: a selection whose metric still exists stays (even when its content
    /// changed), otherwise X takes the first metric and Y the second (the first when only one is registered, none when nothing is). The axes are evaluated
    /// again only when a selected metric is another one or has other content than the one last evaluated.
    /// </summary>
    private void SyncAvailableMetrics()
    {
        if (_metricRegistry == null) return;

        IReadOnlyList<ScatterMetricDefinition> snapshot = _metricRegistry.Snapshot();
        Guid? previousX = SelectedXMetric?.Id;
        Guid? previousY = SelectedYMetric?.Id;
        var options = snapshot.Select(m => new ScatterMetricOption(m.Id, m.Label)).ToList();

        _isSyncingMetrics = true;
        try
        {
            _metricsById = snapshot.ToDictionary(m => m.Id);
            ((BulkObservableCollection<ScatterMetricOption>)AvailableMetrics).ReplaceRange(options);

            SelectedXMetric = options.FirstOrDefault(o => o.Id == previousX) ?? options.FirstOrDefault();
            SelectedYMetric = options.FirstOrDefault(o => o.Id == previousY)
                ?? (options.Count >= 2 ? options[1] : options.FirstOrDefault());
        }
        finally
        {
            _isSyncingMetrics = false;
        }

        OnPropertyChanged(nameof(HasRegisteredMetrics));
        OnPropertyChanged(nameof(ShowNoMetricsMessage));

        if (!SameAsEvaluated(SelectedXMetric, _evaluatedX) || !SameAsEvaluated(SelectedYMetric, _evaluatedY))
        {
            ReevaluateDataset();
        }
    }

    private bool SameAsEvaluated(ScatterMetricOption? option, (Guid Id, string Key)? evaluated)
    {
        if (option == null || evaluated == null) return option == null && evaluated == null;
        return option.Id == evaluated.Value.Id
               && _metricsById.TryGetValue(option.Id, out ScatterMetricDefinition? definition)
               && string.Equals(definition.ContentKey, evaluated.Value.Key, StringComparison.Ordinal);
    }

    private (ScatterMetricDefinition? Definition, (Guid Id, string Key)? Mark) Resolve(ScatterMetricOption? option)
        => option != null && _metricsById.TryGetValue(option.Id, out ScatterMetricDefinition? definition)
            ? (definition.Clone(), (definition.Id, definition.ContentKey))
            : (null, null);

    private void ApplyFixedAxes(AxisRange? xRange, AxisRange? yRange)
    {
        _xFixedRange = xRange;
        _yFixedRange = yRange;

        // A fixed axis is linear; an axis without a fixed range has the scale the user chose last.
        _isApplyingFixedAxes = true;
        try
        {
            XScale = xRange.HasValue || !_userXLog ? PriceScaleType.Linear : PriceScaleType.Log;
            YScale = yRange.HasValue || !_userYLog ? PriceScaleType.Linear : PriceScaleType.Log;
        }
        finally
        {
            _isApplyingFixedAxes = false;
        }

        OnPropertyChanged(nameof(IsXLogEnabled));
        OnPropertyChanged(nameof(IsYLogEnabled));
        OnPropertyChanged(nameof(XLogToolTip));
        OnPropertyChanged(nameof(YLogToolTip));
        OnPropertyChanged(nameof(IsXLog));
        OnPropertyChanged(nameof(IsYLog));
    }

    private string FormatStatus(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, _localizationService.GetString(key), arguments);

    private static int CountOutsideFixedRange(IReadOnlyList<ScatterPoint> points, AxisRange? xRange, AxisRange? yRange)
    {
        if (!xRange.HasValue && !yRange.HasValue) return 0;

        int outside = 0;
        foreach (ScatterPoint point in points)
        {
            if (!point.IsFinite) continue;
            if ((xRange.HasValue && (point.X < xRange.Value.Min || point.X > xRange.Value.Max))
                || (yRange.HasValue && (point.Y < yRange.Value.Min || point.Y > yRange.Value.Max)))
            {
                outside++;
            }
        }

        return outside;
    }

    public void Receive(ActiveTickerGroupChangedMessage message)
    {
        GroupTitle = message.GroupName;
        UpdateFromTickers(message.Tickers);
    }

    public void Receive(TickerSelectedMessage message)
    {
        if (string.Equals(SelectedTicker, message.Value, StringComparison.OrdinalIgnoreCase)) return;

        _isHandlingExternalSelection = true;
        try
        {
            SelectedTicker = message.Value;
            if (Dataset?.Labels != null)
            {
                int idx = -1;
                for (int i = 0; i < Dataset.Labels.Count; i++)
                {
                    if (string.Equals(Dataset.Labels[i], message.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                SelectedPointIndex = idx >= 0 ? idx : null;
            }
        }
        finally
        {
            _isHandlingExternalSelection = false;
        }
    }

    public Task? PendingUpdateTask { get; private set; }
    public Task? PendingHeatmapTask { get; private set; }

    public Task RefreshHeatmapAsync()
    {
        var tickers = _currentTickers;
        if (tickers == null || tickers.Count == 0)
        {
            _heatmapCts?.Cancel();
            _heatmapCts?.Dispose();
            _heatmapCts = null;
            Interlocked.Increment(ref _heatmapRevision);

            HeatmapItems = Array.Empty<UniformHeatmapItem>();
            HeatmapTargetDate = null;
            PendingHeatmapTask = Task.CompletedTask;
            return Task.CompletedTask;
        }

        _heatmapCts?.Cancel();
        _heatmapCts?.Dispose();
        _heatmapCts = new CancellationTokenSource();
        var token = _heatmapCts.Token;
        long revision = Interlocked.Increment(ref _heatmapRevision);

        int offsetSnapshot = HeatmapOffset;
        var tickersSnapshot = tickers;

        var task = Task.Run(async () =>
        {
            try
            {
                var result = await _heatmapProvider.ComputeAsync(tickersSnapshot, offsetSnapshot, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                _dispatcherService.Post(() =>
                {
                    if (token.IsCancellationRequested || revision != Volatile.Read(ref _heatmapRevision)) return;

                    HeatmapItems = result.Items;
                    HeatmapTargetDate = result.TargetDate;
                });
            }
            catch (OperationCanceledException)
            {
                // Discard canceled request
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate heatmap returns asynchronously");
            }
        }, token);

        PendingHeatmapTask = task;
        return task;
    }

    public Task UpdateFromTickersAsync(IReadOnlyList<string> tickers)
    {
        _currentTickers = tickers;

        if (tickers == null || tickers.Count == 0)
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = null;

            Dataset = ScatterDataset.Empty;
            AnalysisResult = ScatterAnalysisResult.Empty;
            StatusMessage = _localizationService.GetString("Scatter_Status_NoTickers");
            IsLoading = false;
            RefreshHeatmapAsync();
            PendingUpdateTask = Task.CompletedTask;
            return Task.CompletedTask;
        }

        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;
        long revision = Interlocked.Increment(ref _currentRevision);

        // Without both metrics there is nothing to plot: the screen shows the "register metrics" guidance, the heatmap goes on with the tickers.
        var (xMetric, xMark) = Resolve(SelectedXMetric);
        var (yMetric, yMark) = Resolve(SelectedYMetric);
        if (xMetric == null || yMetric == null || _metricEvaluator == null)
        {
            _currentCts?.Dispose();
            _currentCts = null;
            Interlocked.Increment(ref _currentRevision);

            _evaluatedX = null;
            _evaluatedY = null;
            ApplyFixedAxes(null, null);
            Dataset = ScatterDataset.Empty;
            AnalysisResult = ScatterAnalysisResult.Empty;
            StatusMessage = _localizationService.GetString("Scatter_Status_NoMetrics");
            IsLoading = false;
            PendingUpdateTask = RefreshHeatmapAsync();
            return PendingUpdateTask;
        }

        _evaluatedX = xMark;
        _evaluatedY = yMark;
        IsLoading = true;
        StatusMessage = _localizationService.GetString("Scatter_Status_Evaluating");

        var updateTask = Task.Run(async () =>
        {
            try
            {
                var metricResult = await _metricEvaluator.ComputeAsync(tickers, xMetric, yMetric, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                var points = new List<ScatterPoint>(metricResult.Items.Count);

                for (int i = 0; i < metricResult.Items.Count; i++)
                {
                    var it = metricResult.Items[i];
                    points.Add(new ScatterPoint(it.X, it.Y));
                }

                var newDataset = new ScatterDataset(
                    title: GroupTitle,
                    xAxisLabel: xMetric.Label,
                    yAxisLabel: yMetric.Label,
                    points: points,
                    labels: tickers
                )
                {
                    XFixedRange = metricResult.XFixedRange,
                    YFixedRange = metricResult.YFixedRange,
                };

                var newAnalysis = ScatterAnalysisEngine.Analyze(newDataset);
                int outsideFixedRange = CountOutsideFixedRange(points, metricResult.XFixedRange, metricResult.YFixedRange);

                _dispatcherService.Post(() =>
                {
                    if (token.IsCancellationRequested || revision != Volatile.Read(ref _currentRevision)) return;

                    ApplyFixedAxes(metricResult.XFixedRange, metricResult.YFixedRange);
                    Dataset = newDataset;
                    AnalysisResult = newAnalysis;
                    string status = metricResult.FailedCount > 0
                        ? FormatStatus("Scatter_Status_EvaluatedWithFailures", metricResult.ValidCount, metricResult.TotalCount, metricResult.FailedCount)
                        : FormatStatus("Scatter_Status_Evaluated", metricResult.ValidCount, metricResult.TotalCount);
                    status += $" (N = {newAnalysis.ValidPointCount})";
                    if (outsideFixedRange > 0)
                    {
                        status += " / " + FormatStatus("Scatter_Status_OutsideFixedRange", outsideFixedRange);
                    }

                    StatusMessage = status;
                    IsLoading = false;
                });
            }
            catch (OperationCanceledException)
            {
                // Discard canceled request
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate scatter metrics asynchronously");
                _dispatcherService.Post(() =>
                {
                    if (revision != Volatile.Read(ref _currentRevision)) return;

                    // The selection already shows the new metrics: nothing of the previous plot may stay, and the next registry change evaluates again.
                    _evaluatedX = null;
                    _evaluatedY = null;
                    ApplyFixedAxes(null, null);
                    Dataset = ScatterDataset.Empty;
                    AnalysisResult = ScatterAnalysisResult.Empty;
                    IsLoading = false;
                    StatusMessage = _localizationService.GetString("Scatter_Status_EvaluationFailed");
                });
            }
        }, token);

        var heatmapTask = RefreshHeatmapAsync();
        PendingUpdateTask = Task.WhenAll(updateTask, heatmapTask);
        return PendingUpdateTask;
    }

    public void UpdateFromTickers(IReadOnlyList<string> tickers)
    {
        _ = UpdateFromTickersAsync(tickers);
    }

    private void PopulateDefaultDataset()
    {
        string[] defaultTickers =
        [
            "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA", "META", "TSLA", "BRK-B", "JPM", "V",
            "JNJ", "WMT", "PG", "MA", "UNH", "HD", "DIS", "BAC", "VZ", "ADBE"
        ];
        UpdateFromTickers(defaultTickers);
    }

    private void ReevaluateDataset()
    {
        if (_currentTickers != null && _currentTickers.Count > 0)
        {
            UpdateFromTickers(_currentTickers);
        }
        else if (Dataset?.Labels != null)
        {
            UpdateFromTickers(Dataset.Labels);
        }
    }

    public void Dispose()
    {
        if (_chartSettingsManager != null)
        {
            _chartSettingsManager.SettingsChanged -= OnChartSettingsChanged;
        }

        if (_metricRegistry != null)
        {
            _metricRegistry.Changed -= OnMetricRegistryChanged;
        }

        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _heatmapCts?.Cancel();
        _heatmapCts?.Dispose();
        _messenger.UnregisterAll(this);
    }
}
