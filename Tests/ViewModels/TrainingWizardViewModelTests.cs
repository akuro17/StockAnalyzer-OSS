using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests.ViewModels;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class TrainingWizardViewModelTests : IDisposable
{
    private readonly SynchronizationContext? _previousSyncContext;

    public TrainingWizardViewModelTests()
    {
        LocalizationManager.Instance.Initialize("ja");

        // The VM builds a Progress<TrainingProgress> that captures SynchronizationContext.Current;
        // in the app that is the UI context, which serializes OnTrainingProgress onto one thread.
        // Without a context here, Progress<T> dispatches callbacks concurrently on the thread pool
        // and corrupts the ObservableCollections it mutates. Install a synchronous context so the
        // tests exercise the same single-threaded delivery the app has.
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronousSynchronizationContext());
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previousSyncContext);

    private sealed class SynchronousSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public async Task Constructor_SplitsProfilesIntoWatchlistAndPortfolioCollections()
    {
        var watchlist = MakeProfile("Tech Watch", isPortfolio: false, "AAPL", "MSFT");
        var portfolio = MakeProfile("Core Holdings", isPortfolio: true, "GOOGL");
        var watchlistManager = new DummyWatchlistManager(new[] { watchlist, portfolio });
        var market = new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN");

        var vm = new TrainingWizardViewModel(watchlistManager: watchlistManager, marketDataProvider: market);
        await WaitForInitializationAsync(vm);

        Assert.Single(vm.WatchlistProfiles);
        Assert.Equal("Tech Watch", vm.WatchlistProfiles[0].Name);
        Assert.Single(vm.PortfolioProfiles);
        Assert.Equal("Core Holdings", vm.PortfolioProfiles[0].Name);
    }

    [Fact]
    public void StartTrainingCommand_DefaultState_CannotExecute()
    {
        // Horizon defaults to 0 (no SSoT default exists to seed it with; see the VM's doc
        // comment), so the command must stay disabled until the user sets it explicitly.
        var vm = new TrainingWizardViewModel();

        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public void WindowUnitNote_DefaultHorizonZero_OmitsMinBarsClause()
    {
        // Horizon defaults to 0 (invalid); the min-bars clause needs both Window/Horizon > 0.
        var vm = new TrainingWizardViewModel();

        Assert.DoesNotContain("534", vm.WindowUnitNote); // WalkForwardDataRequirement.MinimumRawBars(75, 5)
    }

    [Fact]
    public void WindowUnitNote_ValidWindowAndHorizon_IncludesMinBarsCount()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };

        Assert.Contains(WalkForwardDataRequirement.MinimumRawBars(25, 5).ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_RecomputesWhenHorizonChanges()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var before = WalkForwardDataRequirement.MinimumRawBars(25, 5);
        Assert.Contains(before.ToString(), vm.WindowUnitNote);

        vm.Horizon = 30;

        var after = WalkForwardDataRequirement.MinimumRawBars(25, 30);
        Assert.NotEqual(before, after);
        Assert.Contains(after.ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_RecomputesWhenGapChanges()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var noteAtDefaultGap = vm.WindowUnitNote;
        Assert.Contains(WalkForwardDataRequirement.MinimumRawBars(25, 5).ToString(), noteAtDefaultGap);

        vm.Gap = 0;

        var expected = WalkForwardDataRequirement.MinimumRawBars(
            25, 5, WalkForwardDataRequirement.DefaultSplitCount, gap: 0);
        Assert.NotEqual(noteAtDefaultGap, vm.WindowUnitNote);
        Assert.Contains(expected.ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_NegativeGap_DoesNotThrow_FallsBackToDefaultGap()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };

        vm.Gap = -5; // transient invalid input; Start stays disabled, but the note must still render.

        Assert.Contains(WalkForwardDataRequirement.MinimumRawBars(25, 5).ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void Gap_Change_RaisesWindowUnitNotePropertyChanged()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Gap = 7;

        Assert.Contains(nameof(TrainingWizardViewModel.WindowUnitNote), raised);
    }

    [Theory]
    [InlineData("AllTickers", TrainingScopeKind.AllTickers)]
    [InlineData("Watchlist", TrainingScopeKind.Watchlist)]
    [InlineData("Portfolio", TrainingScopeKind.Portfolio)]
    public void SetScopeCommand_ValidMemberName_UpdatesSelectedScope(string scopeName, TrainingScopeKind expected)
    {
        // Bound from the view's scope RadioButtons via CommandParameter (string), so the
        // command must round-trip the same Enum.TryParse conversion MainWindowViewModel.SetTimeframe(string) uses.
        var vm = new TrainingWizardViewModel();

        vm.SetScopeCommand.Execute(scopeName);

        Assert.Equal(expected, vm.SelectedScope);
    }

    [Fact]
    public void SetScopeCommand_UnknownMemberName_LeavesSelectedScopeUnchanged()
    {
        var vm = new TrainingWizardViewModel { SelectedScope = TrainingScopeKind.Watchlist };

        vm.SetScopeCommand.Execute("NotARealScope");

        Assert.Equal(TrainingScopeKind.Watchlist, vm.SelectedScope);
    }

    [Fact]
    public async Task StartTrainingAsync_AllTickersScope_ResolvesFullMasterListIntoConfig()
    {
        var market = new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(
            new[] { "AAPL", "MSFT", "GOOGL" }.OrderBy(s => s),
            orchestrator.CapturedConfig!.Symbols.OrderBy(s => s));
        Assert.Equal(TrainingFeatureModeForTest, orchestrator.CapturedConfig.FeatureMode);
    }

    [Fact]
    public async Task StartTrainingAsync_DefaultValidationSettings_ConfigUsesWalkForwardDefaults()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, vm.NSplits);
        Assert.Null(vm.Gap);
        Assert.Null(vm.OosTailDays);

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, orchestrator.CapturedConfig!.NSplits);
        Assert.Null(orchestrator.CapturedConfig.Gap);
        Assert.Null(orchestrator.CapturedConfig.OosTailDays);
    }

    [Fact]
    public async Task StartTrainingAsync_CustomValidationSettings_FlowIntoConfig()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.NSplits = 8;
        vm.Gap = 12;
        vm.OosTailDays = 90;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(8, orchestrator.CapturedConfig!.NSplits);
        Assert.Equal(12, orchestrator.CapturedConfig.Gap);
        Assert.Equal(90, orchestrator.CapturedConfig.OosTailDays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void StartTrainingCommand_NSplitsBelowTwo_CannotExecute(int nSplits)
    {
        var vm = new TrainingWizardViewModel(
            orchestrator: new FakeTrainingOrchestrator(),
            marketDataProvider: new DummyMarketDataProvider("AAPL"))
        {
            Horizon = 5,
            NSplits = nSplits,
        };

        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnTrainingProgress_FoldMetricLines_PopulateFoldResultsTable()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            MetricLines =
            {
                FoldMetric(fold: 0, splits: 3, n: 120, accuracy: 0.55, baseline: 0.40, macroF1: 0.52, logloss: 0.98),
                FoldMetric(fold: 1, splits: 3, n: 130, accuracy: 0.58, baseline: 0.41, macroF1: 0.54, logloss: 0.95),
                FoldMetric(fold: 2, splits: 3, n: 140, accuracy: 0.60, baseline: 0.42, macroF1: 0.57, logloss: 0.90),
                // out-of-sample line: not a fold row, must be ignored by the table
                new Dictionary<string, double> { ["oos_tail_days"] = 90, ["oos_n"] = 88, ["oos_accuracy"] = 0.53 },
                // final aggregate line: also not a fold row
                new Dictionary<string, double> { ["accuracy"] = 0.59, ["macro_f1"] = 0.55 },
            },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm); // pump the Progress<T> callbacks

        Assert.True(vm.HasFoldResults);
        Assert.Equal(3, vm.FoldResults.Count);
        Assert.Equal(new[] { "1/3", "2/3", "3/3" }, vm.FoldResults.Select(r => r.FoldLabel));
        Assert.Equal(120, vm.FoldResults[0].SampleCount);
        Assert.Equal(0.55, vm.FoldResults[0].Accuracy, precision: 6);
        Assert.Equal(0.60 - 0.42, vm.FoldResults[2].AccuracyOverBaseline, precision: 6);
    }

    [Fact]
    public async Task StartTrainingAsync_SecondRun_ClearsPreviousFoldResults()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            MetricLines = { FoldMetric(fold: 0, splits: 2, n: 100, accuracy: 0.5, baseline: 0.4, macroF1: 0.5, logloss: 1.0) },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm);
        Assert.Single(vm.FoldResults);

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm);
        Assert.Single(vm.FoldResults); // cleared then repopulated, not appended
    }

    private static Dictionary<string, double> FoldMetric(
        int fold, int splits, int n, double accuracy, double baseline, double macroF1, double logloss) => new()
    {
        ["fold"] = fold,
        ["n_splits"] = splits,
        ["fold_n"] = n,
        ["fold_accuracy"] = accuracy,
        ["fold_baseline_accuracy"] = baseline,
        ["fold_macro_f1"] = macroF1,
        ["fold_multi_logloss"] = logloss,
    };

    [Fact]
    public async Task StartTrainingAsync_WatchlistScope_ResolvesOnlySelectedProfilesTickers()
    {
        var selected = MakeProfile("Selected", isPortfolio: false, "AAPL", "MSFT");
        var notSelected = MakeProfile("NotSelected", isPortfolio: false, "GOOGL");
        var watchlistManager = new DummyWatchlistManager(new[] { selected, notSelected });
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, watchlistManager: watchlistManager);
        await WaitForInitializationAsync(vm);

        vm.SelectedScope = TrainingScopeKind.Watchlist;
        vm.WatchlistProfiles.Single(p => p.Name == "Selected").IsSelected = true;
        vm.Horizon = 5;

        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(new[] { "AAPL", "MSFT" }.OrderBy(s => s), orchestrator.CapturedConfig!.Symbols.OrderBy(s => s));
    }

    [Fact]
    public async Task StartTrainingAsync_Success_SetsLastResultAndRecordsExperimentLog()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "20260829-000000",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = @"I:\artifacts\model.onnx",
            },
        };
        var experimentLog = new FakeExperimentLogService();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, experimentLogService: experimentLog, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Same(orchestrator.ResultToReturn, vm.LastResult);
        Assert.False(vm.IsTraining);
        Assert.NotNull(experimentLog.Captured);
        Assert.Equal("20260829-000000", experimentLog.Captured!.Value.result.RunId);
        Assert.Same(orchestrator.CapturedConfig, experimentLog.Captured.Value.config);
    }

    [Fact]
    public async Task StartTrainingAsync_TrainerFailure_SetsStatusMessageFromResult()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-fail",
                Success = false,
                ExitCode = 3,
                Message = "trainer backend not available",
            },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Equal("trainer backend not available", vm.StatusMessage);
        Assert.False(vm.DeployToModelsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelTrainingCommand_WhileRunning_CancelsTheOrchestratorToken()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var gate = new TaskCompletionSource();
        var orchestrator = new FakeTrainingOrchestrator { Gate = gate };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        var runTask = vm.StartTrainingCommand.ExecuteAsync(null);
        await orchestrator.EnteredAsync; // fake orchestrator signals it has started and is waiting on the gate

        Assert.True(vm.IsTraining);
        Assert.True(vm.CancelTrainingCommand.CanExecute(null));

        // Cancelling ct fires the fake's registration, which cancels the gate itself -- no
        // separate release needed (and calling gate.SetResult() here would race with that
        // registration and risk an InvalidOperationException on an already-completed TCS).
        vm.CancelTrainingCommand.Execute(null);

        await runTask;

        Assert.True(orchestrator.WasCancelled);
        Assert.False(vm.IsTraining);
    }

    [Fact]
    public async Task DeployToModelsAsync_AfterSuccessfulRun_CallsModelDeploymentServiceWithArtifactPaths()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-ok",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = @"I:\artifacts\model.onnx",
                MetricsArtifactPath = @"I:\artifacts\model.onnx.metrics.json",
            },
        };
        var deployment = new FakeModelDeploymentService();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, modelDeploymentService: deployment, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.True(vm.DeployToModelsCommand.CanExecute(null));
        await vm.DeployToModelsCommand.ExecuteAsync(null);

        Assert.Equal((@"I:\artifacts\model.onnx", (string?)@"I:\artifacts\model.onnx.metrics.json"), deployment.Captured);
    }

    // --- fixtures --------------------------------------------------------

    private const PredictionFeatureMode TrainingFeatureModeForTest = PredictionFeatureMode.OhlcvMinMax;

    private static async Task WaitForInitializationAsync(TrainingWizardViewModel vm)
    {
        // The VM's constructor fires an unawaited InitializeAsync(); yield a few times so its
        // continuations (which only ever touch in-memory dummies, no real I/O) have run before
        // the test inspects the populated collections.
        for (var i = 0; i < 5; i++)
        {
            await Task.Yield();
        }
    }

    private static WatchlistProfile MakeProfile(string name, bool isPortfolio, params string[] tickers) =>
        new(Guid.NewGuid(), name, IndicatorColor.Gray, isPortfolio,
            tickers.Select(t => new WatchlistItem(t, DateTimeOffset.UtcNow)).ToList());

    private sealed class FakeTrainingOrchestrator : ITrainingOrchestrator
    {
        public TrainingJobConfig? CapturedConfig { get; private set; }
        public TrainingRunResult ResultToReturn { get; set; } = new() { RunId = "fake-run", Success = true, ExitCode = 0 };
        public TaskCompletionSource? Gate { get; set; }
        public bool WasCancelled { get; private set; }

        /// <summary>Metric payloads reported (in order) as <c>METRIC:</c>-equivalent progress updates before completion.</summary>
        public List<IReadOnlyDictionary<string, double>> MetricLines { get; } = new();

        private readonly TaskCompletionSource _entered = new();
        public Task EnteredAsync => _entered.Task;

        public async Task<TrainingRunResult> StartTrainingAsync(
            TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
        {
            CapturedConfig = config;
            progress?.Report(new TrainingProgress { Stage = "load", Percent = 0 });

            foreach (var metric in MetricLines)
            {
                progress?.Report(new TrainingProgress { Stage = "evaluate", Percent = 92, Metric = metric });
            }

            if (Gate is not null)
            {
                _entered.TrySetResult();
                using var registration = ct.Register(() => { WasCancelled = true; Gate.TrySetCanceled(); });
                await Gate.Task;
                ct.ThrowIfCancellationRequested();
            }

            progress?.Report(new TrainingProgress { Stage = "done", Percent = 100 });
            return ResultToReturn;
        }
    }

    private sealed class FakeModelDeploymentService : IModelDeploymentService
    {
        public (string onnx, string? metrics)? Captured { get; private set; }

        public Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
        {
            Captured = (onnxSourcePath, metricsSourcePath);
            return Task.FromResult(@"I:\Data\Models\model.onnx");
        }
    }

    private sealed class FakeExperimentLogService : IExperimentLogService
    {
        public (TrainingJobConfig config, TrainingRunResult result)? Captured { get; private set; }

        public Task RecordAsync(TrainingJobConfig config, TrainingRunResult result, CancellationToken ct = default)
        {
            Captured = (config, result);
            return Task.CompletedTask;
        }
    }

    private sealed class DummyMarketDataProvider : IMarketDataProvider
    {
        private readonly List<string> _tickers;

        public DummyMarketDataProvider(params string[] tickers) => _tickers = tickers.ToList();

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult<IReadOnlyList<string>>(_tickers);
        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(_tickers);
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());
        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => ValueTask.FromResult(TickerMetadata.Unknown);
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Task.FromResult(TickerMetadata.Unknown);
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
        public Task AddTickerAsync(string symbol) => Task.CompletedTask;
        public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public void InvalidateMetadataCache(string ticker) { }
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
    }

    private sealed class DummyWatchlistManager : IWatchlistManager
    {
        private readonly List<WatchlistProfile> _profiles;

        public DummyWatchlistManager(IEnumerable<WatchlistProfile> profiles) => _profiles = profiles.ToList();

        public event EventHandler? WatchlistsChanged;

        public IReadOnlyList<WatchlistProfile> GetAllProfiles() => _profiles;
        public WatchlistProfile? GetProfileById(Guid profileId) => _profiles.FirstOrDefault(p => p.Id == profileId);
        public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false) => throw new NotImplementedException();
        public void UpdateProfileName(Guid profileId, string name) { }
        public void DeleteProfile(Guid profileId) { }
        public void AddTickerToProfile(Guid profileId, string ticker) { }
        public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickerFromProfile(Guid profileId, string ticker) { }
        public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers) { }
        public void Initialize(IEnumerable<WatchlistProfile> profiles) { }
    }
}
