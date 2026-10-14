using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
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

    private sealed class TestLocalizationService : ILocalizationService
    {
        public string GetString(string key) => key switch
        {
            "Scatter_ColorScheme_Classic" => "Classic Colors",
            "Scatter_ColorScheme_Colorblind" => "Colorblind Safe",
            _ => key
        };
    }

    private ScatterPlotViewModel CreateViewModel(IMessenger? messenger = null, IScatterMetricProvider? metricProvider = null)
    {
        messenger ??= new WeakReferenceMessenger();
        return new ScatterPlotViewModel(
            messenger,
            new TestMarketDataProvider(),
            new TestLocalizationService(),
            NullLogger<ScatterPlotViewModel>.Instance,
            new TestDispatcherService(),
            metricProvider
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

        vm.SelectedXMetric = "RSI";
        vm.SelectedYMetric = "P/E";
        if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;

        Assert.Equal("RSI", vm.Dataset?.XAxisLabel);
        Assert.Equal("P/E", vm.Dataset?.YAxisLabel);
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
    public void GetStableHash32_ProducesDeterministicValuesWithoutOverflow()
    {
        uint hash1 = SyntheticScatterMetricProvider.GetStableHash32("AAPL");
        uint hash2 = SyntheticScatterMetricProvider.GetStableHash32("AAPL");
        Assert.Equal(hash1, hash2);

        uint hashMsft = SyntheticScatterMetricProvider.GetStableHash32("MSFT");
        Assert.NotEqual(hash1, hashMsft);

        uint hashEmpty = SyntheticScatterMetricProvider.GetStableHash32("");
        Assert.Equal(2166136261u, hashEmpty);
    }

    [Fact]
    public void ColorScheme_ToggleCommand_CyclesThemesAndUpdatesLabel()
    {
        using var vm = CreateViewModel();

        Assert.Equal(HeatmapColorScheme.Classic, vm.ColorScheme);
        Assert.Equal("Classic Colors", vm.ColorSchemeLabel);

        vm.ToggleColorSchemeCommand.Execute(null);

        Assert.Equal(HeatmapColorScheme.ColorblindSafe, vm.ColorScheme);
        Assert.Equal("Colorblind Safe", vm.ColorSchemeLabel);

        vm.ToggleColorSchemeCommand.Execute(null);

        Assert.Equal(HeatmapColorScheme.Classic, vm.ColorScheme);
        Assert.Equal("Classic Colors", vm.ColorSchemeLabel);
    }

    private sealed class TestDelayMetricProvider : IScatterMetricProvider
    {
        public string ProviderId => "TestDelay";
        public bool IsLiveMarketData => false;

        public async Task<ScatterMetricResult> ComputeMetricsAsync(
            IReadOnlyList<string> tickers,
            string xMetric,
            string yMetric,
            CancellationToken cancellationToken = default)
        {
            if (tickers.Count > 0 && tickers[0].StartsWith("DELAYED"))
            {
                await Task.Delay(100, cancellationToken);
            }

            var items = new List<ScatterMetricItem>();
            for (int i = 0; i < tickers.Count; i++)
            {
                items.Add(new ScatterMetricItem(tickers[i], i * 1.0, i * 2.0, 0.05));
            }

            return new ScatterMetricResult(items, false, ProviderId, "Test Delayed");
        }
    }

    [Fact]
    public async Task AsyncUpdate_MonotonicRevision_DiscardsSupersededOlderRequests()
    {
        var delayedProvider = new TestDelayMetricProvider();
        using var vm = CreateViewModel(metricProvider: delayedProvider);

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
}
