using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public sealed class ScatterPlotViewModelTests
{
    private sealed class TestDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public Task PostAsync(Func<Task> action) => action();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    private sealed class TestMarketDataProvider : IMarketDataProvider
    {
        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "AAPL", "MSFT", "GOOGL" });

        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) =>
            ValueTask.FromResult(new TickerMetadata(ticker, ticker, "US", "Tech", "Software", "USD"));

        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) =>
            Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());

        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());

        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) =>
            Task.FromResult(TickerMetadata.Unknown);

        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
        public Task AddTickerAsync(string symbol) => Task.CompletedTask;
        public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public void InvalidateMetadataCache(string ticker) { }
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
    }

    /// <summary>A localization service that answers the key itself, except for the status messages that carry arguments (a short text with the placeholders).</summary>
    private sealed class TestLocalizationService : ILocalizationService
    {
        public string GetString(string key) => key switch
        {
            "Scatter_Status_OutsideFixedRange" => "{0} outside",
            "Scatter_Status_Evaluated" => "{0}/{1} valid",
            "Scatter_Status_EvaluatedWithFailures" => "{0}/{1} valid, {2} failed",
            _ => key,
        };
    }

    private sealed class TestRegistry : IScatterMetricRegistry
    {
        private List<ScatterMetricDefinition> _metrics;

        public TestRegistry(IEnumerable<ScatterMetricDefinition> metrics) => _metrics = metrics.ToList();

        public Task Loaded => Task.CompletedTask;
        public IReadOnlyList<ScatterMetricDefinition> Snapshot() => _metrics.Select(m => m.Clone()).ToList();
        public int TakeLoadDroppedCount() => 0;
        public Task<ScatterMetricRegistryResult> ReplaceAllAsync(IReadOnlyList<ScatterMetricDefinition> metrics) => throw new NotSupportedException();
        public event EventHandler? Changed;

        /// <summary>Stores <paramref name="metrics"/> as the confirmed list and raises the change event, as the real registry does after OK.</summary>
        public void Confirm(IEnumerable<ScatterMetricDefinition> metrics)
        {
            _metrics = metrics.Select(m => m.Clone()).ToList();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>An evaluator that answers X = index and Y = 2 * index, records what it was asked, and can hand out fixed ranges.</summary>
    private sealed class TestEvaluator : IScatterMetricEvaluator
    {
        private readonly object _gate = new();
        public List<(string X, string Y, IReadOnlyList<string> Tickers)> Calls { get; } = new();
        /// <summary>The time frames of the X and Y definitions the evaluator received, per call.</summary>
        public List<(TimeFrame X, TimeFrame Y)> TimeFrames { get; } = new();
        public int CallCount { get { lock (_gate) return Calls.Count; } }
        public Func<ScatterMetricDefinition, AxisRange?> FixedRangeOf { get; set; } = _ => null;
        public Func<int, double>? XValue { get; set; }

        /// <summary>When set and true for the X metric of a request, that request fails.</summary>
        public Func<ScatterMetricDefinition, bool>? FailWhen { get; set; }

        /// <summary>How many tickers the result reports as failed.</summary>
        public int FailedCount { get; set; }

        public async Task<ScatterEvaluationResult> ComputeAsync(
            IReadOnlyList<string> tickers, ScatterMetricDefinition x, ScatterMetricDefinition y, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add((x.Label, y.Label, tickers));
                TimeFrames.Add((x.Source.TimeFrame, y.Source.TimeFrame));
            }

            if (FailWhen?.Invoke(x) == true)
            {
                throw new InvalidOperationException("The evaluation failed (test).");
            }

            if (tickers.Count > 0 && tickers[0].StartsWith("DELAYED", StringComparison.Ordinal))
            {
                await Task.Delay(100, ct);
            }

            var items = new List<ScatterEvaluationItem>();
            for (int i = 0; i < tickers.Count; i++)
            {
                items.Add(new ScatterEvaluationItem(tickers[i], XValue?.Invoke(i) ?? i * 1.0, i * 2.0));
            }

            return new ScatterEvaluationResult
            {
                Items = items,
                TotalCount = items.Count,
                ValidCount = items.Count - FailedCount,
                FailedCount = FailedCount,
                XFixedRange = FixedRangeOf(x),
                YFixedRange = FixedRangeOf(y),
            };
        }
    }

    /// <summary>An <see cref="IDialogService"/> that answers only the registration window and counts how often it was opened.</summary>
    public class RecordingDialogProxy : DispatchProxy
    {
        public int OpenCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowScatterMetricRegistrationDialogAsync))
            {
                OpenCount++;
                return Task.FromResult(true);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    /// <summary>A registered metric as the registration window makes a Column: its label is <paramref name="label"/>.</summary>
    private static ScatterMetricDefinition Metric(string label, int offset = 0) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = label,
            CustomDisplayName = label,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        },
    };

    private static readonly ScatterMetricDefinition MetricA = Metric("Metric A");
    private static readonly ScatterMetricDefinition MetricB = Metric("Metric B");
    private static readonly ScatterMetricDefinition MetricC = Metric("Metric C");

    private TestEvaluator LastEvaluator { get; set; } = new();
    private TestRegistry LastRegistry { get; set; } = new(Array.Empty<ScatterMetricDefinition>());

    private ScatterPlotViewModel CreateViewModel(
        IMessenger? messenger = null,
        IScatterMetricEvaluator? evaluator = null,
        IHeatmapReturnProvider? heatmapProvider = null,
        IChartSettingsManager? chartSettingsManager = null,
        IEnumerable<ScatterMetricDefinition>? metrics = null,
        IDialogService? dialogService = null,
        ILocalizationService? localization = null)
    {
        messenger ??= new WeakReferenceMessenger();
        LastEvaluator = evaluator as TestEvaluator ?? new TestEvaluator();
        LastRegistry = new TestRegistry(metrics ?? new[] { MetricA, MetricB });
        return new ScatterPlotViewModel(
            messenger,
            new TestMarketDataProvider(),
            localization ?? new TestLocalizationService(),
            NullLogger<ScatterPlotViewModel>.Instance,
            new TestDispatcherService(),
            heatmapProvider,
            chartSettingsManager,
            LastRegistry,
            evaluator ?? LastEvaluator,
            dialogService
        );
    }

    [Fact]
    public void ModeSwitching_TogglesScatterAndHeatmapFlags()
    {
        using var vm = CreateViewModel();

        Assert.Equal(ScatterViewMode.ScatterPlot, vm.CurrentMode);
        Assert.True(vm.IsScatterMode);
        Assert.False(vm.IsHeatmapMode);

        vm.SetHeatmapModeCommand.Execute(null);

        Assert.Equal(ScatterViewMode.Heatmap, vm.CurrentMode);
        Assert.False(vm.IsScatterMode);
        Assert.True(vm.IsHeatmapMode);

        vm.SetScatterModeCommand.Execute(null);

        Assert.Equal(ScatterViewMode.ScatterPlot, vm.CurrentMode);
        Assert.True(vm.IsScatterMode);
        Assert.False(vm.IsHeatmapMode);
    }

    [Fact]
    public void DisplayToggles_FlipPropertiesCorrectly()
    {
        using var vm = CreateViewModel();

        Assert.True(vm.ShowRegressionLine);
        vm.ToggleRegressionLineCommand.Execute(null);
        Assert.False(vm.ShowRegressionLine);

        Assert.True(vm.ShowConfidenceBand);
        vm.ToggleConfidenceBandCommand.Execute(null);
        Assert.False(vm.ShowConfidenceBand);

        Assert.False(vm.ShowPredictionBand);
        vm.TogglePredictionBandCommand.Execute(null);
        Assert.True(vm.ShowPredictionBand);

        Assert.False(vm.SuppressOutliers);
        vm.ToggleSuppressOutliersCommand.Execute(null);
        Assert.True(vm.SuppressOutliers);

        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        vm.ToggleXScaleCommand.Execute(null);
        Assert.Equal(PriceScaleType.Log, vm.XScale);

        Assert.Equal(PriceScaleType.Linear, vm.YScale);
        vm.ToggleYScaleCommand.Execute(null);
        Assert.Equal(PriceScaleType.Log, vm.YScale);
    }

    [Fact]
    public void ScaleToggles_TwoWayBinding_AndNotifications_WorkCorrectly()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.IsXLog);
        Assert.False(vm.IsYLog);
        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        Assert.Equal(PriceScaleType.Linear, vm.YScale);

        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProperties.Add(e.PropertyName);
        };

        // Two-way setter from ToggleButton IsChecked
        vm.IsXLog = true;
        Assert.Equal(PriceScaleType.Log, vm.XScale);
        Assert.True(vm.IsXLog);
        Assert.Contains(nameof(vm.IsXLog), changedProperties);
        Assert.Contains(nameof(vm.XScale), changedProperties);

        changedProperties.Clear();
        vm.ToggleXScaleCommand.Execute(null);
        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        Assert.False(vm.IsXLog);
        Assert.Contains(nameof(vm.IsXLog), changedProperties);

        changedProperties.Clear();
        vm.IsYLog = true;
        Assert.Equal(PriceScaleType.Log, vm.YScale);
        Assert.True(vm.IsYLog);
        Assert.Contains(nameof(vm.IsYLog), changedProperties);
        Assert.Contains(nameof(vm.YScale), changedProperties);

        changedProperties.Clear();
        vm.ToggleYScaleCommand.Execute(null);
        Assert.Equal(PriceScaleType.Linear, vm.YScale);
        Assert.False(vm.IsYLog);
        Assert.Contains(nameof(vm.IsYLog), changedProperties);
    }

    [Fact]
    public async Task Receive_ActiveTickerGroupChangedMessage_RegeneratesDatasetAndEvaluatesAnalysis()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = CreateViewModel(messenger);
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        var tickers = new[] { "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA" };
        var message = new ActiveTickerGroupChangedMessage("Tech Leaders", tickers, true);

        messenger.Send(message);
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal("Tech Leaders", vm.GroupTitle);
        Assert.NotNull(vm.Dataset);
        Assert.Equal(5, vm.Dataset!.Points.Count);
        Assert.NotNull(vm.AnalysisResult);
        Assert.Equal(5, vm.AnalysisResult!.ValidPointCount);
        Assert.NotNull(vm.HeatmapItems);
        Assert.Equal(5, vm.HeatmapItems!.Count);
    }

    [Fact]
    public async Task MetricSelection_TriggersDatasetReevaluation()
    {
        using var vm = CreateViewModel();
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        int before = LastEvaluator.CallCount;

        vm.SelectedXMetric = vm.AvailableMetrics[1];
        vm.SelectedYMetric = vm.AvailableMetrics[0];
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal("Metric B", vm.Dataset?.XAxisLabel);
        Assert.Equal("Metric A", vm.Dataset?.YAxisLabel);
        Assert.True(LastEvaluator.CallCount > before, "the superseded first request may be cancelled before it starts, the last one always runs");
        Assert.Equal(("Metric B", "Metric A"), (LastEvaluator.Calls[^1].X, LastEvaluator.Calls[^1].Y));
        Assert.NotNull(vm.AnalysisResult);
        Assert.True(vm.AnalysisResult!.ValidPointCount > 0);
    }

    [Fact]
    public void SelectTickerCommand_DispatchesTickerSelectedMessage()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = CreateViewModel(messenger);

        string? receivedTicker = null;
        messenger.Register<TickerSelectedMessage>(this, (_, msg) =>
        {
            receivedTicker = msg.Value;
        });

        vm.SelectTickerCommand.Execute("NVDA");

        Assert.Equal("NVDA", receivedTicker);
        Assert.Equal("NVDA", vm.SelectedTicker);

        messenger.UnregisterAll(this);
    }

    [Fact]
    public void Receive_TickerSelectedMessage_SynchronizesSelection()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = CreateViewModel(messenger);

        messenger.Send(new TickerSelectedMessage("TSLA"));

        Assert.Equal("TSLA", vm.SelectedTicker);
    }

    [Fact]
    public void TabRegistry_CanRegisterAndResolveScatterPlotTab()
    {
        var logger = NullLogger<TabRegistry>.Instance;
        var registry = new TabRegistry(logger);

        var metadata = new TabMetadata("ScatterPlot", "Scatter / Heatmap", AllowMultiple: true);
        registry.Register(metadata, _ => CreateViewModel());

        var resolvedMeta = registry.GetMetadata("ScatterPlot");
        Assert.Equal("ScatterPlot", resolvedMeta.Id);
        Assert.True(resolvedMeta.AllowMultiple);

        var resolvedVm = registry.CreateViewModel("ScatterPlot", null!);
        Assert.IsType<ScatterPlotViewModel>(resolvedVm);
    }

    [Theory]
    [InlineData("ScatterPlot")]
    [InlineData("ScatterAnalysis")]
    public void PanelTabFactory_CreatesScatterAnalysisTab_ForBothIds(string tabId)
    {
        var logger = NullLogger<TabRegistry>.Instance;
        var registry = new TabRegistry(logger);

        var metadata = new TabMetadata(tabId, "Scatter Analysis", AllowMultiple: true);
        registry.Register(metadata, _ => CreateViewModel());

        var factory = new PanelTabFactory(null!, registry);
        var tab = factory.CreateTab(tabId);

        Assert.NotNull(tab);
        Assert.Equal(tabId, tab.Id);
        Assert.Equal("Scatter Analysis", tab.Title);
        Assert.IsType<ScatterPlotViewModel>(tab.ViewModel);
    }

    [Fact]
    public void HeatmapOffset_DefaultAndClamping_WorksCorrectly()
    {
        using var vm = CreateViewModel();

        Assert.Equal(0, vm.HeatmapOffset);
        Assert.Equal(0, vm.HeatmapOffsetMin);
        Assert.Equal(9999, vm.HeatmapOffsetMax);

        vm.HeatmapOffset = -5;
        Assert.Equal(0, vm.HeatmapOffset);

        vm.HeatmapOffset = 10000;
        Assert.Equal(9999, vm.HeatmapOffset);

        vm.HeatmapOffset = 42;
        Assert.Equal(42, vm.HeatmapOffset);
    }

    [Fact]
    public async Task UpdateFromTickersAsync_DecoupledPipeline_ConsumesHeatmapProviderAndFormatsDate()
    {
        var heatmapProvider = new ConfigurableHeatmapReturnProvider();
        using var vm = CreateViewModel(heatmapProvider: heatmapProvider);

        var tickers = new[] { "AAPL", "MSFT" };
        var task = vm.UpdateFromTickersAsync(tickers);
        await task;

        Assert.NotNull(vm.HeatmapItems);
        Assert.Equal(2, vm.HeatmapItems!.Count);
        Assert.Equal(0.01, vm.HeatmapItems[0].ReturnPercentage, precision: 6);
        Assert.Equal(0.02, vm.HeatmapItems[1].ReturnPercentage, precision: 6);

        Assert.Equal(new DateOnly(2026, 10, 16), vm.HeatmapTargetDate);
        Assert.Equal("2026-10-16", vm.HeatmapTargetDateText);
    }

    [Fact]
    public async Task HeatmapOffsetChange_InvokesHeatmapProviderOnly_DoesNotReevaluateScatter()
    {
        var scatterEvaluator = new TestEvaluator();
        var heatmapProvider = new ConfigurableHeatmapReturnProvider();
        using var vm = CreateViewModel(evaluator: scatterEvaluator, heatmapProvider: heatmapProvider);
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        int initialScatterCalls = scatterEvaluator.CallCount;
        int initialHeatmapCalls = heatmapProvider.Invocations.Count;

        vm.HeatmapOffset = 3;
        if (vm.PendingHeatmapTask != null) await vm.PendingHeatmapTask;

        Assert.Equal(initialScatterCalls, scatterEvaluator.CallCount);
        Assert.Equal(initialHeatmapCalls + 1, heatmapProvider.Invocations.Count);
        Assert.Equal(3, heatmapProvider.Invocations.Last().Offset);
    }

    [Fact]
    public async Task HeatmapOffsetChange_RapidSuccession_MonotonicRevisionDiscardsStaleResults()
    {
        var heatmapProvider = new ConfigurableHeatmapReturnProvider();
        heatmapProvider.Handler = async (tickers, offset, ct) =>
        {
            if (offset == 1)
            {
                await Task.Delay(100, ct);
            }
            return new HeatmapReturnResult(
                new[] { new UniformHeatmapItem("AAPL", offset * 0.1) },
                new DateOnly(2026, 10, 10 + offset),
                true,
                "Test",
                "Ok");
        };

        using var vm = CreateViewModel(heatmapProvider: heatmapProvider);

        vm.HeatmapOffset = 1; // Delayed
        vm.HeatmapOffset = 2; // Fast

        if (vm.PendingHeatmapTask != null) await vm.PendingHeatmapTask;
        await Task.Delay(150); // Ensure delayed task completes

        // Offset 2 was requested after Offset 1, so Offset 2 must win
        Assert.NotNull(vm.HeatmapItems);
        Assert.Equal(0.2, vm.HeatmapItems![0].ReturnPercentage, precision: 6);
        Assert.Equal(new DateOnly(2026, 10, 12), vm.HeatmapTargetDate);
    }

    [Fact]
    public async Task UpdateFromTickersAsync_EmptyTickers_ClearsHeatmapItemsAndTargetDate()
    {
        using var vm = CreateViewModel();
        await vm.UpdateFromTickersAsync(Array.Empty<string>());

        Assert.Empty(vm.HeatmapItems!);
        Assert.Null(vm.HeatmapTargetDate);
        Assert.Equal("—", vm.HeatmapTargetDateText);
    }

    private sealed class ConfigurableHeatmapReturnProvider : IHeatmapReturnProvider
    {
        public string ProviderId => "Configurable";
        public List<(IReadOnlyList<string> Tickers, int Offset)> Invocations { get; } = new();
        public Func<IReadOnlyList<string>, int, CancellationToken, Task<HeatmapReturnResult>>? Handler { get; set; }

        public async Task<HeatmapReturnResult> ComputeAsync(
            IReadOnlyList<string> tickers,
            int offset,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((tickers, offset));
            if (Handler != null)
            {
                return await Handler(tickers, offset, cancellationToken);
            }

            var items = new List<UniformHeatmapItem>(tickers.Count);
            for (int i = 0; i < tickers.Count; i++)
            {
                items.Add(new UniformHeatmapItem(tickers[i], 0.01 * (i + 1)));
            }

            return new HeatmapReturnResult(
                items,
                new DateOnly(2026, 10, 16),
                isLiveMarketData: true,
                ProviderId,
                "Test Heatmap");
        }
    }

    [Fact]
    public async Task AsyncUpdate_MonotonicRevision_DiscardsSupersededOlderRequests()
    {
        using var vm = CreateViewModel();

        var taskA = vm.UpdateFromTickersAsync(new[] { "DELAYED_A", "DELAYED_B" });
        var taskB = vm.UpdateFromTickersAsync(new[] { "FAST_1", "FAST_2", "FAST_3" });

        try { await taskA; } catch (OperationCanceledException) { /* Expected cancellation of older revision */ }
        await taskB;

        // Group B was requested after Group A, so Group B must win and not be overwritten by Group A
        Assert.NotNull(vm.Dataset);
        Assert.Equal(3, vm.Dataset!.Points.Count);
        Assert.Equal("FAST_1", vm.Dataset.Labels[0]);
    }

    [Fact]
    public async Task UpdateFromTickersAsync_DirectExecution_ComputesMetricsFromProvider()
    {
        using var vm = CreateViewModel();
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.NotNull(vm.Dataset);
        Assert.NotNull(vm.AnalysisResult);
        Assert.True(vm.Dataset!.Points.Count > 0);
        Assert.Contains("N =", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectedPointIndex_BiDirectionalSync_SynchronizesWithSelectedTickerAndMessenger()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = CreateViewModel(messenger: messenger);
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        string? receivedTicker = null;
        messenger.Register<TickerSelectedMessage>(this, (_, msg) => receivedTicker = msg.Value);

        // Selecting a point index in scatter plot sets SelectedTicker and sends message
        vm.SelectedPointIndex = 1;
        Assert.NotNull(vm.Dataset?.Labels);
        string expectedTicker = vm.Dataset!.Labels![1];
        Assert.Equal(expectedTicker, vm.SelectedTicker);
        Assert.Equal(expectedTicker, receivedTicker);

        // Selecting ticker externally synchronizes SelectedPointIndex
        string targetTicker = vm.Dataset.Labels[0];
        messenger.Send(new TickerSelectedMessage(targetTicker));
        Assert.Equal(targetTicker, vm.SelectedTicker);
        Assert.Equal(0, vm.SelectedPointIndex);
    }

    [Fact]
    public void HeatmapPalette_WithoutSettingsManager_DefaultsToHeatmapPaletteDefault()
    {
        using var vm = CreateViewModel();
        Assert.Equal(HeatmapPalette.Default, vm.HeatmapPalette);
    }

    [Fact]
    public void HeatmapPalette_WithSettingsManager_InitializesFromCurrentSettings()
    {
        var settingsManager = new MockChartSettingsManager();
        var customSettings = new StockAnalyzer.Core.Models.Settings.GlobalChartSettings
        {
            ScatterHeatmapUpColor = "#FF112233",
            ScatterHeatmapNeutralColor = "#FF445566",
            ScatterHeatmapDownColor = "#FF778899",
            ScatterHeatmapThresholdPercent = 8.0,
            ScatterHeatmapMultiplier = 1.5,
            ScatterHeatmapStepCount = 15
        };
        settingsManager.UpdatePreview(customSettings);

        using var vm = CreateViewModel(chartSettingsManager: settingsManager);

        Assert.Equal(15, vm.HeatmapPalette.StepCount);
        Assert.Equal(0.12, vm.HeatmapPalette.ThresholdRatio, precision: 4);
    }

    [Fact]
    public void HeatmapPalette_OnSettingsChanged_RebuildsPalette_AndDisposeUnsubscribes()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = CreateViewModel(chartSettingsManager: settingsManager);
        Assert.Equal(HeatmapPalette.Default, vm.HeatmapPalette);

        var updatedSettings = new StockAnalyzer.Core.Models.Settings.GlobalChartSettings
        {
            ScatterHeatmapThresholdPercent = 10.0,
            ScatterHeatmapMultiplier = 2.0,
            ScatterHeatmapStepCount = 25
        };
        settingsManager.UpdatePreview(updatedSettings);

        Assert.Equal(25, vm.HeatmapPalette.StepCount);
        Assert.Equal(0.20, vm.HeatmapPalette.ThresholdRatio, precision: 4);

        // After Dispose, settings changes should not affect vm
        vm.Dispose();
        var afterDisposeSettings = new StockAnalyzer.Core.Models.Settings.GlobalChartSettings
        {
            ScatterHeatmapStepCount = 5
        };
        settingsManager.UpdatePreview(afterDisposeSettings);
        Assert.Equal(25, vm.HeatmapPalette.StepCount);
    }

    // ===== Registered metrics only =====

    [Fact]
    public async Task WithoutRegisteredMetrics_NothingIsOffered_NothingIsPlotted_AndTheHeatmapStillGetsItsTickers()
    {
        var heatmapProvider = new ConfigurableHeatmapReturnProvider();
        using var vm = CreateViewModel(heatmapProvider: heatmapProvider, metrics: Array.Empty<ScatterMetricDefinition>());
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Empty(vm.AvailableMetrics);
        Assert.Null(vm.SelectedXMetric);
        Assert.Null(vm.SelectedYMetric);
        Assert.False(vm.HasRegisteredMetrics);
        Assert.True(vm.ShowNoMetricsMessage);
        Assert.Same(ScatterDataset.Empty, vm.Dataset);
        Assert.Same(ScatterAnalysisResult.Empty, vm.AnalysisResult);
        Assert.Equal("Scatter_Status_NoMetrics", vm.StatusMessage);
        Assert.False(vm.IsLoading);
        Assert.Equal(0, LastEvaluator.CallCount);

        // The heatmap does not depend on the metrics: the 20 seeded tickers still reach its provider.
        Assert.Equal(20, heatmapProvider.Invocations[0].Tickers.Count);
        Assert.Equal(20, vm.HeatmapItems!.Count);

        vm.SetHeatmapModeCommand.Execute(null);
        Assert.False(vm.ShowNoMetricsMessage);
    }

    [Fact]
    public async Task TheDefaultMetrics_AreGone_AndTheOptionsAreTheRegisteredOnesInOrder()
    {
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB, MetricC });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal(new[] { "Metric A", "Metric B", "Metric C" }, vm.AvailableMetrics.Select(o => o.Label).ToArray());
        Assert.Equal(new[] { MetricA.Id, MetricB.Id, MetricC.Id }, vm.AvailableMetrics.Select(o => o.Id).ToArray());
        Assert.DoesNotContain(vm.AvailableMetrics, o => o.Label is "Return" or "Volatility" or "RSI" or "P/E" or "Volume" or "MarketCap");
        Assert.True(vm.HasRegisteredMetrics);
        Assert.False(vm.ShowNoMetricsMessage);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(3, 0, 1)]
    public void TheInitialSelection_IsTheFirstMetricForX_AndTheSecondForY_OrTheFirstWhenThereIsOnlyOne(int count, int expectedX, int expectedY)
    {
        var metrics = new[] { MetricA, MetricB, MetricC }.Take(count).ToArray();
        using var vm = CreateViewModel(metrics: metrics);

        Assert.Equal(metrics[expectedX].Id, vm.SelectedXMetric!.Id);
        Assert.Equal(metrics[expectedY].Id, vm.SelectedYMetric!.Id);
    }

    [Fact]
    public async Task ARegistryChange_RebuildsTheOptions_KeepsSurvivingSelections_AndRepairsTheRest()
    {
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB, MetricC });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.SelectedXMetric = vm.AvailableMetrics[2]; // C
        vm.SelectedYMetric = vm.AvailableMetrics[1]; // B

        LastRegistry.Confirm(new[] { MetricC, MetricA }); // B is gone; C survives (now second)
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal(new[] { "Metric C", "Metric A" }, vm.AvailableMetrics.Select(o => o.Label).ToArray());
        Assert.Equal(MetricC.Id, vm.SelectedXMetric!.Id);          // kept
        Assert.Equal(MetricA.Id, vm.SelectedYMetric!.Id);          // B vanished: Y takes the second metric of [C, A]
        Assert.Equal("Metric C", vm.Dataset!.XAxisLabel);

        LastRegistry.Confirm(Array.Empty<ScatterMetricDefinition>());
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Empty(vm.AvailableMetrics);
        Assert.Null(vm.SelectedXMetric);
        Assert.Null(vm.SelectedYMetric);
        Assert.True(vm.ShowNoMetricsMessage);
        Assert.Same(ScatterDataset.Empty, vm.Dataset);
    }

    [Fact]
    public async Task ARegistryChangeThatKeepsTheSelectedMetrics_DoesNotEvaluateAgain()
    {
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        int before = LastEvaluator.CallCount;

        LastRegistry.Confirm(new[] { MetricA, MetricB, MetricC }); // only an unselected metric was added
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal(3, vm.AvailableMetrics.Count);
        Assert.Equal(before, LastEvaluator.CallCount);
        Assert.Equal(MetricA.Id, vm.SelectedXMetric!.Id);
        Assert.Equal(MetricB.Id, vm.SelectedYMetric!.Id);
    }

    [Fact]
    public async Task EditingTheSelectedMetric_KeepsTheSelection_RenamesTheOption_AndEvaluatesAgain()
    {
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        int before = LastEvaluator.CallCount;

        ScatterMetricDefinition edited = MetricA.Clone();
        edited.Source.Offset = 3;
        LastRegistry.Confirm(new[] { edited, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal(MetricA.Id, vm.SelectedXMetric!.Id);
        Assert.Equal("Metric A [-3]", vm.SelectedXMetric.Label);
        Assert.Equal(before + 1, LastEvaluator.CallCount);
        Assert.Equal("Metric A [-3]", vm.Dataset!.XAxisLabel);
    }

    [Fact]
    public async Task ChangingTheTimeFrameOfTheSelectedMetric_EvaluatesAgainOnThatTimeFrame_AndNamesItOnTheAxis()
    {
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        int before = LastEvaluator.CallCount;
        Assert.Equal((TimeFrame.D1, TimeFrame.D1), LastEvaluator.TimeFrames.Last());

        ScatterMetricDefinition weekly = MetricA.Clone();
        weekly.Source.TimeFrame = TimeFrame.W1;
        LastRegistry.Confirm(new[] { weekly, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        // The time frame is part of the content key, so the same Id with another time frame is a new evaluation.
        Assert.Equal(before + 1, LastEvaluator.CallCount);
        Assert.Equal((TimeFrame.W1, TimeFrame.D1), LastEvaluator.TimeFrames.Last());
        Assert.Equal("Metric A [W1]", vm.Dataset!.XAxisLabel);
        Assert.Equal("Metric B", vm.Dataset.YAxisLabel);
    }

    [Fact]
    public async Task AFailedEvaluation_ShowsNoStalePlot_AndIsTriedAgainOnTheNextChange()
    {
        var evaluator = new TestEvaluator { FixedRangeOf = m => m.Label == "Metric A" ? new AxisRange(0, 100) : null };
        using var vm = CreateViewModel(evaluator: evaluator, metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        Assert.Equal("Metric A", vm.Dataset!.XAxisLabel);
        Assert.False(vm.IsXLogEnabled);

        ScatterMetricDefinition edited = MetricA.Clone();
        edited.Source.Offset = 3;
        evaluator.FailWhen = m => m.Label == "Metric A [-3]";
        LastRegistry.Confirm(new[] { edited, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        // The selection already shows the new metric: the plot must not keep the old metric's points, axis labels or fixed range.
        Assert.Empty(vm.Dataset!.Points);
        Assert.Null(vm.Dataset.XFixedRange);
        Assert.True(vm.IsXLogEnabled);
        Assert.Equal("Scatter_Status_EvaluationFailed", vm.StatusMessage);
        Assert.False(vm.IsLoading);

        // The next change of the registry evaluates again, although the selection is the same as before.
        evaluator.FailWhen = null;
        int before = evaluator.CallCount;
        LastRegistry.Confirm(new[] { edited, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.True(evaluator.CallCount > before);
        Assert.Equal("Metric A [-3]", vm.Dataset!.XAxisLabel);
        Assert.NotEmpty(vm.Dataset.Points);
    }

    [Fact]
    public async Task TheEvaluatorReceivesTheSelectedDefinitions_AndItsResultBecomesTheDataset()
    {
        var tickers = new[] { "AAPL", "MSFT", "GOOGL" };
        using var vm = CreateViewModel(metrics: new[] { MetricA, MetricB });
        await vm.UpdateFromTickersAsync(tickers);

        var call = LastEvaluator.Calls[^1];
        Assert.Equal(("Metric A", "Metric B"), (call.X, call.Y));
        Assert.Equal(tickers, call.Tickers);
        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, vm.Dataset!.Points.Select(p => p.X).ToArray());
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, vm.Dataset.Points.Select(p => p.Y).ToArray());
        Assert.Equal(tickers, vm.Dataset.Labels);
        Assert.Equal("3/3 valid (N = 3)", vm.StatusMessage);
    }

    [Fact]
    public async Task TheStatusLine_IsBuiltFromTheCounts_WithTheFailuresWhenThereAreAny()
    {
        var evaluator = new TestEvaluator { FailedCount = 2 };
        using var vm = CreateViewModel(evaluator: evaluator, metrics: new[] { MetricA, MetricB });

        await vm.UpdateFromTickersAsync(new[] { "AAPL", "MSFT", "GOOGL" });

        Assert.Equal("1/3 valid, 2 failed (N = 3)", vm.StatusMessage);
    }

    // ===== Fixed-range axes =====

    [Fact]
    public async Task AFixedRangeMetric_ForcesALinearAxis_DisablesItsLogToggle_AndExplainsWhy()
    {
        bool rangeOn = false;
        var evaluator = new TestEvaluator { FixedRangeOf = d => rangeOn && d.Label == "Metric A" ? new AxisRange(0, 100) : null };
        using var vm = CreateViewModel(evaluator: evaluator, metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.IsXLog = true;
        vm.IsYLog = true;
        Assert.Equal(PriceScaleType.Log, vm.XScale);
        rangeOn = true;
        await vm.UpdateFromTickersAsync(new[] { "AAPL", "MSFT" });

        Assert.Equal(new AxisRange(0, 100), vm.Dataset!.XFixedRange);
        Assert.Null(vm.Dataset.YFixedRange);
        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        Assert.False(vm.IsXLog);
        Assert.False(vm.IsXLogEnabled);
        Assert.Equal("Scatter_Tip_FixedAxisLog", vm.XLogToolTip);
        Assert.True(vm.IsYLogEnabled);
        Assert.Null(vm.YLogToolTip);
        Assert.Equal(PriceScaleType.Log, vm.YScale); // the other axis keeps its own choice

        vm.IsXLog = true;
        vm.ToggleXScaleCommand.Execute(null);
        Assert.Equal(PriceScaleType.Linear, vm.XScale);

        // Another X metric without a range gives the toggle back.
        vm.SelectedXMetric = vm.AvailableMetrics[1];
        await vm.PendingUpdateTask!;
        Assert.True(vm.IsXLogEnabled);
        Assert.Null(vm.XLogToolTip);
        Assert.Null(vm.Dataset!.XFixedRange);
    }

    // ===== The Log choice of an axis outlives a fixed-range metric (owner decision: per axis, last toggle value, this tab only) =====

    private static TestEvaluator EvaluatorWithAFixedMetricA(Func<bool>? rangeOn = null)
        => new() { FixedRangeOf = d => d.Label.StartsWith("Metric A", StringComparison.Ordinal) && (rangeOn?.Invoke() ?? true) ? new AxisRange(0, 100) : null };

    [Fact]
    public async Task TheLogChoiceOfAnAxis_ComesBackWhenItsMetricIsNoLongerFixed()
    {
        using var vm = CreateViewModel(evaluator: EvaluatorWithAFixedMetricA(), metrics: new[] { MetricB, MetricC, MetricA });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.IsXLog = true;

        vm.SelectedXMetric = vm.AvailableMetrics[2]; // Metric A: fixed
        await vm.PendingUpdateTask!;
        Assert.False(vm.IsXLogEnabled);
        Assert.False(vm.IsXLog);
        Assert.Equal(PriceScaleType.Linear, vm.XScale);

        vm.SelectedXMetric = vm.AvailableMetrics[0]; // Metric B: not fixed
        await vm.PendingUpdateTask!;
        Assert.True(vm.IsXLogEnabled);
        Assert.True(vm.IsXLog);
        Assert.Equal(PriceScaleType.Log, vm.XScale);
    }

    [Fact]
    public async Task AnAxisThatTheUserNeverSetToLog_StaysLinearAfterAFixedMetric()
    {
        using var vm = CreateViewModel(evaluator: EvaluatorWithAFixedMetricA(), metrics: new[] { MetricB, MetricC, MetricA });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        vm.SelectedXMetric = vm.AvailableMetrics[2];
        await vm.PendingUpdateTask!;
        vm.SelectedXMetric = vm.AvailableMetrics[0];
        await vm.PendingUpdateTask!;

        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        Assert.False(vm.IsXLog);
    }

    [Fact]
    public async Task TheLogChoicesOfTheTwoAxesAreIndependent()
    {
        using var vm = CreateViewModel(evaluator: EvaluatorWithAFixedMetricA(), metrics: new[] { MetricB, MetricC, MetricA });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.IsYLog = true; // only Y is logarithmic

        vm.SelectedXMetric = vm.AvailableMetrics[2];
        await vm.PendingUpdateTask!;
        Assert.Equal(PriceScaleType.Log, vm.YScale);

        vm.SelectedXMetric = vm.AvailableMetrics[0];
        await vm.PendingUpdateTask!;
        Assert.Equal(PriceScaleType.Linear, vm.XScale);
        Assert.Equal(PriceScaleType.Log, vm.YScale);
    }

    [Fact]
    public async Task OnAFixedAxis_TheLogFlagReadsFalse_EvenWhenTheScaleIsAssignedDirectly()
    {
        using var vm = CreateViewModel(evaluator: EvaluatorWithAFixedMetricA(), metrics: new[] { MetricA, MetricB });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        Assert.False(vm.IsXLogEnabled); // X shows Metric A: fixed

        vm.XScale = PriceScaleType.Log; // code, not the toggle: the toggle is refused on a fixed axis

        Assert.False(vm.IsXLog);
    }

    [Fact]
    public async Task ADisabledToggle_DoesNotChangeTheRememberedChoice()
    {
        using var vm = CreateViewModel(evaluator: EvaluatorWithAFixedMetricA(), metrics: new[] { MetricB, MetricC, MetricA });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.IsXLog = true;
        vm.SelectedXMetric = vm.AvailableMetrics[2];
        await vm.PendingUpdateTask!;

        vm.IsXLog = false;                    // refused while the axis is fixed
        vm.ToggleXScaleCommand.Execute(null); // refused as well

        vm.SelectedXMetric = vm.AvailableMetrics[0];
        await vm.PendingUpdateTask!;
        Assert.Equal(PriceScaleType.Log, vm.XScale);
    }

    [Fact]
    public async Task TheLogChoiceComesBack_WhenAnEditOrARemovalEndsTheFixedRange()
    {
        bool rangeOn = false;
        var evaluator = EvaluatorWithAFixedMetricA(() => rangeOn);
        using var vm = CreateViewModel(evaluator: evaluator, metrics: new[] { MetricB, MetricA });
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
        vm.IsYLog = true; // Y shows Metric A, which has no range yet

        // The edit makes the same metric fixed ...
        rangeOn = true;
        ScatterMetricDefinition edited = MetricA.Clone();
        edited.Source.Offset = 1;
        LastRegistry.Confirm(new[] { MetricB, edited });
        await vm.PendingUpdateTask!;
        Assert.False(vm.IsYLogEnabled);
        Assert.Equal(PriceScaleType.Linear, vm.YScale);

        // ... and an edit that ends the range gives the choice back.
        rangeOn = false;
        ScatterMetricDefinition editedAgain = edited.Clone();
        editedAgain.Source.Offset = 2;
        LastRegistry.Confirm(new[] { MetricB, editedAgain });
        await vm.PendingUpdateTask!;
        Assert.True(vm.IsYLogEnabled);
        Assert.Equal(PriceScaleType.Log, vm.YScale);

        // Removing the fixed metric (Y falls back to another metric) gives it back as well.
        rangeOn = true;
        ScatterMetricDefinition fixedAgain = editedAgain.Clone();
        fixedAgain.Source.Offset = 3;
        LastRegistry.Confirm(new[] { MetricB, fixedAgain });
        await vm.PendingUpdateTask!;
        Assert.Equal(PriceScaleType.Linear, vm.YScale);
        LastRegistry.Confirm(new[] { MetricB, MetricC });
        await vm.PendingUpdateTask!;
        Assert.True(vm.IsYLogEnabled);
        Assert.Equal(PriceScaleType.Log, vm.YScale);
    }

    [Fact]
    public async Task APointOutsideAFixedRange_IsCountedInTheStatus_AndStaysInTheDataset()
    {
        var evaluator = new TestEvaluator
        {
            FixedRangeOf = d => d.Label == "Metric A" ? new AxisRange(0, 100) : null,
            XValue = i => i == 1 ? 150.0 : i * 10.0,
        };
        using var vm = CreateViewModel(evaluator: evaluator, metrics: new[] { MetricA, MetricB });
        await vm.UpdateFromTickersAsync(new[] { "AAPL", "MSFT", "GOOGL" });

        Assert.EndsWith("1 outside", vm.StatusMessage);
        Assert.Equal(3, vm.Dataset!.Points.Count);
        Assert.Equal(3, vm.AnalysisResult!.ValidPointCount);
    }

    // ===== Gear button command =====

    [Fact]
    public void OpenMetricRegistration_IsUnavailableWithoutADialogService()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.OpenMetricRegistrationCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenMetricRegistration_OpensTheDialog()
    {
        var proxy = DispatchProxy.Create<IDialogService, RecordingDialogProxy>();
        using var vm = CreateViewModel(dialogService: proxy);

        Assert.True(vm.OpenMetricRegistrationCommand.CanExecute(null));
        await vm.OpenMetricRegistrationCommand.ExecuteAsync(null);

        Assert.Equal(1, ((RecordingDialogProxy)(object)proxy).OpenCount);
    }

    [Fact]
    public void DisposingTheViewModel_StopsListeningToTheRegistry()
    {
        var vm = CreateViewModel(metrics: new[] { MetricA, MetricB });
        vm.Dispose();

        LastRegistry.Confirm(new[] { MetricC });

        Assert.Equal(new[] { "Metric A", "Metric B" }, vm.AvailableMetrics.Select(o => o.Label).ToArray());
    }
}
