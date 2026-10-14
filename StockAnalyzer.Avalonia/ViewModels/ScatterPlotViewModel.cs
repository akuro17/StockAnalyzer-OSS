using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Common;
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
    private readonly IScatterMetricProvider _metricProvider;

    private long _currentRevision;
    private CancellationTokenSource? _currentCts;
    private IReadOnlyList<string>? _currentTickers;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScatterMode))]
    [NotifyPropertyChangedFor(nameof(IsHeatmapMode))]
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

    [ObservableProperty] private PriceScaleType _xScale = PriceScaleType.Linear;
    [ObservableProperty] private PriceScaleType _yScale = PriceScaleType.Linear;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorSchemeLabel))]
    private HeatmapColorScheme _colorScheme = HeatmapColorScheme.Classic;

    public string ColorSchemeLabel => ColorScheme == HeatmapColorScheme.Classic
        ? _localizationService.GetString("Scatter_ColorScheme_Classic")
        : _localizationService.GetString("Scatter_ColorScheme_Colorblind");

    [ObservableProperty] private string _selectedXMetric = "Return";
    [ObservableProperty] private string _selectedYMetric = "Volatility";
    [ObservableProperty] private string _groupTitle = "All Tickers";
    [ObservableProperty] private string? _selectedTicker;
    [ObservableProperty] private int? _selectedPointIndex;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<string> AvailableMetrics { get; } = new()
    {
        "Return",
        "Volatility",
        "RSI",
        "P/E",
        "Volume",
        "MarketCap"
    };

    public ScatterPlotViewModel(
        IMessenger messenger,
        IMarketDataProvider? marketDataProvider,
        ILocalizationService localizationService,
        ILogger<ScatterPlotViewModel> logger,
        IDispatcherService dispatcherService,
        IScatterMetricProvider? metricProvider = null)
    {
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _metricProvider = metricProvider ?? SyntheticScatterMetricProvider.Instance;

        _messenger.Register<ActiveTickerGroupChangedMessage>(this);
        _messenger.Register<TickerSelectedMessage>(this);

        // Initialize with default sample population
        PopulateDefaultDataset();
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
        XScale = XScale == PriceScaleType.Linear ? PriceScaleType.Log : PriceScaleType.Linear;
    }

    [RelayCommand]
    private void ToggleYScale()
    {
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

    [RelayCommand]
    private void ToggleColorScheme()
    {
        ColorScheme = ColorScheme == HeatmapColorScheme.Classic
            ? HeatmapColorScheme.ColorblindSafe
            : HeatmapColorScheme.Classic;
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

    partial void OnSelectedXMetricChanged(string value) => ReevaluateDataset();
    partial void OnSelectedYMetricChanged(string value) => ReevaluateDataset();

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
            HeatmapItems = Array.Empty<UniformHeatmapItem>();
            StatusMessage = "No active tickers";
            IsLoading = false;
            PendingUpdateTask = Task.CompletedTask;
            return Task.CompletedTask;
        }

        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;
        long revision = Interlocked.Increment(ref _currentRevision);

        IsLoading = true;
        StatusMessage = "Evaluating metrics...";

        var updateTask = Task.Run(async () =>
        {
            try
            {
                var metricResult = await _metricProvider.ComputeMetricsAsync(tickers, SelectedXMetric, SelectedYMetric, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                var points = new List<ScatterPoint>(metricResult.Items.Count);
                var heatmapList = new List<UniformHeatmapItem>(metricResult.Items.Count);

                for (int i = 0; i < metricResult.Items.Count; i++)
                {
                    var it = metricResult.Items[i];
                    points.Add(new ScatterPoint(it.XValue, it.YValue));
                    heatmapList.Add(new UniformHeatmapItem(it.Ticker, it.ReturnPercentage));
                }

                var newDataset = new ScatterDataset(
                    title: GroupTitle,
                    xAxisLabel: SelectedXMetric,
                    yAxisLabel: SelectedYMetric,
                    points: points,
                    labels: tickers
                );

                var newAnalysis = ScatterAnalysisEngine.Analyze(newDataset);

                _dispatcherService.Post(() =>
                {
                    if (token.IsCancellationRequested || revision != Volatile.Read(ref _currentRevision)) return;

                    Dataset = newDataset;
                    AnalysisResult = newAnalysis;
                    HeatmapItems = heatmapList;
                    StatusMessage = $"{metricResult.StatusMessage} (N = {newAnalysis.ValidPointCount})";
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
                    IsLoading = false;
                    StatusMessage = "Metric evaluation failed";
                });
            }
        }, token);

        PendingUpdateTask = updateTask;
        return updateTask;
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
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _messenger.UnregisterAll(this);
    }
}
