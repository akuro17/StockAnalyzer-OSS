using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Synchronous IDispatcherService double (mirrors the established test-double pattern used
/// elsewhere in this repo): runs posted work inline instead of marshaling to a real UI thread, which
/// does not exist in a plain xUnit test process.</summary>
internal sealed class SynchronousDispatcherService : IDispatcherService
{
    public void Post(Action action) => action();
    public void Post<T>(Action<T> action, T state) => action(state);
    public Task PostAsync(Func<Task> action) => action();
    public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
    public bool CheckAccess() => true;
    public void VerifyAccess() { }
}

internal sealed class FakeDataService : IDataService
{
    private readonly IReadOnlyList<CandleData> _bars;
    private readonly IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>>? _barsByFrame;

    /// <summary><paramref name="barsByFrame"/> is a trailing, optional safe-extension (Task 5 of
    /// sa_implementation_plan_BacktestComparisonSignals.md): when supplied and it contains the
    /// requested Frame, that Frame's own bars are returned instead of <paramref name="bars"/> - lets a
    /// test simulate loading a different additional-timeframe bar series per Frame. Every existing call
    /// site that omits it keeps returning <paramref name="bars"/> regardless of Frame, unchanged.</summary>
    public FakeDataService(IReadOnlyList<CandleData>? bars = null, IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>>? barsByFrame = null)
    {
        _bars = bars ?? Array.Empty<CandleData>();
        _barsByFrame = barsByFrame;
    }

    public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100) =>
        Task.FromResult(_barsByFrame is not null && _barsByFrame.TryGetValue(timeFrame, out IReadOnlyList<CandleData>? frameBars) ? frameBars : _bars);
}

internal sealed class BlockingDataService : IDataService
{
    private readonly IReadOnlyList<CandleData> _bars;
    private readonly TaskCompletionSource<bool> _firstCallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseFirstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public BlockingDataService(IReadOnlyList<CandleData> bars) => _bars = bars;

    public Task FirstCallEntered => _firstCallEntered.Task;
    public int CallCount => Volatile.Read(ref _callCount);

    public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult(true);

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            _firstCallEntered.TrySetResult(true);
            await _releaseFirstCall.Task.ConfigureAwait(false);
        }
        return _bars;
    }
}

/// <summary>
/// Scriptable <see cref="IBacktestEngine"/> double: returns one enqueued result per call, and can hold
/// a call in-flight on <see cref="Gate"/> so a test can observe/act on the ViewModel's state while the
/// engine is still "running" (simulating a long backtest) before releasing it.
/// </summary>
internal sealed class ScriptableBacktestEngine : IBacktestEngine
{
    private readonly Queue<Func<BacktestResult>> _resultFactories = new();
    private int _runCallCount;

    public int RunCallCount => _runCallCount;

    /// <summary>When non-null, <see cref="Run"/> blocks on this until the test calls <c>Set()</c>.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>The <see cref="BacktestInput"/> passed to the most recent <see cref="Run"/> call, so a test can inspect the evaluation-window boundaries <see cref="StockAnalyzer.Avalonia.ViewModels.Backtest.BacktestWindowViewModel"/> actually computed.</summary>
    public BacktestInput? LastInput { get; private set; }

    /// <summary>The <see cref="IBacktestStrategy"/> passed to the most recent <see cref="Run"/> call (Task 5 of sa_implementation_plan_BacktestComparisonSignals.md), so a test can assert which strategy RunBacktestAsync selected (NoOp vs ConditionBased) without the engine actually executing it.</summary>
    public IBacktestStrategy? LastStrategy { get; private set; }

    public void EnqueueResult(Func<BacktestResult> factory) => _resultFactories.Enqueue(factory);

    public BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _runCallCount);
        LastInput = input;
        LastStrategy = strategy;
        // P3 Hardening Task 5: waiting with the token (instead of the parameterless overload) lets a test
        // simulate the real engine's cooperative cancellation - cancelling the token while a run is blocked
        // on Gate throws OperationCanceledException here, exactly like a real mid-run BacktestEngine.Run abort.
        Gate?.Wait(cancellationToken);
        return _resultFactories.Dequeue()();
    }
}

internal sealed class StubBacktestReportGenerator : IBacktestReportGenerator
{
    public int GenerateCallCount { get; private set; }

    /// <summary>The options of the most recent <see cref="Generate"/> call (what the ViewModel handed over).</summary>
    public BacktestReportOptions? LastOptions { get; private set; }

    public BacktestReport Generate(BacktestResult result, BacktestReportOptions options)
    {
        GenerateCallCount++;
        LastOptions = options;
        return BacktestTestFactory.CreateStubReport();
    }
}

/// <summary>Returns fixed strings per key (missing key = the key itself, like <see cref="NullLocalizationService"/>) so a test can assert that a formatted value was localized and filled.</summary>
internal sealed class FakeLocalizationService : ILocalizationService
{
    private readonly IReadOnlyDictionary<string, string> _strings;

    public FakeLocalizationService(IReadOnlyDictionary<string, string> strings) => _strings = strings;

    public string GetString(string key) => _strings.TryGetValue(key, out string? value) ? value : key;
}

internal sealed class FakeBacktestReportSettingsManager : IBacktestReportSettingsManager
{
    private BacktestReportDefaults _saved = BacktestReportDefaults.BuiltIn;
    public Exception? SaveFailure { get; set; }
    public bool UseBuiltInFallbackStatus { get; set; }
    public BacktestReportDefaults Saved => _saved;

    public Task<BacktestReportDefaults> LoadAsync() => Task.FromResult(_saved);

    public Task<BacktestReportSettingsLoadResult> LoadWithStatusAsync() => Task.FromResult(new BacktestReportSettingsLoadResult(
        _saved,
        UseBuiltInFallbackStatus
            ? BacktestReportSettingsLoadStatus.BuiltInFallback
            : BacktestReportSettingsLoadStatus.Loaded));

    public Task SaveAsync(BacktestReportDefaults defaults)
    {
        if (SaveFailure is not null) return Task.FromException(SaveFailure);
        _saved = BacktestReportDefaults.SnapshotValidated(defaults);
        return Task.CompletedTask;
    }
}

internal sealed class FakeBacktestReportExporter : IBacktestReportExporter
{
    public int ExportCallCount { get; private set; }
    public BacktestReport? LastReport { get; private set; }
    public BacktestEvaluationArtifact? LastEvaluationArtifact { get; private set; }
    public Task ExportAsync(BacktestReport report, string fileName)
    {
        ExportCallCount++;
        LastReport = report;
        return Task.CompletedTask;
    }

    public Task ExportAsync(BacktestReport report, string directoryPath, string fileName)
    {
        ExportCallCount++;
        LastReport = report;
        return Task.CompletedTask;
    }

    public Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string fileName)
    {
        ExportCallCount++;
        LastEvaluationArtifact = artifact;
        return Task.CompletedTask;
    }

    public Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string directoryPath, string fileName)
    {
        ExportCallCount++;
        LastEvaluationArtifact = artifact;
        return Task.CompletedTask;
    }
}

internal sealed class BlockingCancellableReportGenerator : IBacktestReportGenerator, ICancellableBacktestReportGenerator
{
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BacktestReport Generate(BacktestResult result, BacktestReportOptions options) =>
        throw new InvalidOperationException("The cancellable report capability should be selected.");

    public BacktestReport Generate(
        BacktestResult result,
        BacktestReportOptions options,
        CancellationToken cancellationToken)
    {
        Entered.TrySetResult(true);
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Cancellation was expected.");
    }
}

/// <summary>Minimal IDialogService test double (mirrors DrawingObjectsViewModelTests.FakeDialogService's
/// established pattern): only ShowOpenFolderDialogAsync is exercised by
/// BacktestResultsViewModel.ExportReportAsync (SAで改善, Y:\Temp\sa_improvement_plan_BacktestResultsUiPolish.md
/// Task 1); every other member is unused here.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public string? FolderToReturn { get; set; }
    public TaskCompletionSource<string?>? FolderResponse { get; set; }
    public int OpenFolderDialogCallCount { get; private set; }
    public string? LastFolderTitle { get; private set; }

    public Task<string?> ShowOpenFolderDialogAsync(string title, string? initialDirectory = null)
    {
        OpenFolderDialogCallCount++;
        LastFolderTitle = title;
        return FolderResponse?.Task ?? Task.FromResult(FolderToReturn);
    }

    public Task ShowAlertAsync(string title, string message) => throw new NotImplementedException();
    public Task<bool> ShowConfirmationAsync(string title, string message) => throw new NotImplementedException();
    public Task<string?> ShowInputAsync(string title, string message, string defaultValue = "") => throw new NotImplementedException();
    public Task<StockAnalyzer.Avalonia.Models.AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId) => throw new NotImplementedException();
    public Task<StockAnalyzer.Core.Models.Portfolio.Transaction?> ShowEditTransactionDialogAsync(StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTransactionDialogViewModel viewModel) => throw new NotImplementedException();
    public Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12) => throw new NotImplementedException();
    public Task<StockAnalyzer.Avalonia.Services.DrawingSettingsResult> ShowDrawingSettingsDialogAsync(StockAnalyzer.Avalonia.Drawing.IChartObject drawing, Action<StockAnalyzer.Avalonia.Drawing.IChartObject>? onApply = null, StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? coordinator = null, IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData>? candles = null) => throw new NotImplementedException();
    public Task<global::Avalonia.Media.Color?> ShowColorPickerAsync(global::Avalonia.Media.Color initialColor) => throw new NotImplementedException();
    public Task ShowIndicatorSettingsDialogAsync(IEnumerable<CoreIndicatorSettings> currentIndicators, Action<IEnumerable<CoreIndicatorSettings>>? onApply = null) => throw new NotImplementedException();
    public Task ShowIndicatorPropertiesDialogAsync(CoreIndicatorSettings indicator, Action<CoreIndicatorSettings>? onApply = null, IEnumerable<CoreIndicatorSettings>? allIndicators = null) => throw new NotImplementedException();
    public Task ShowSourceIndicatorRegistrationDialogAsync() => throw new NotImplementedException();
    public Task ShowDynamicPeriodDriverRegistrationDialogAsync() => throw new NotImplementedException();
    public Task ShowThemeSettingsDialogAsync() => throw new NotImplementedException();
    public Task ShowSettingsDialogAsync(string? initialCategoryKey = null) => throw new NotImplementedException();
    public Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<StockAnalyzer.Core.Models.Watchlist.WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null) => throw new NotImplementedException();
    public Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings, Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null) => throw new NotImplementedException();
    public Task ShowFilterTemplatePickerDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.FilterNode targetNode) => throw new NotImplementedException();
    public Task ShowFilterTemplatePickerForNewFilterDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode parentNode) => throw new NotImplementedException();
    public Task ShowScreenerDialogAsync() => throw new NotImplementedException();
    public Task ShowBacktestWindowAsync() => throw new NotImplementedException();
    public Task ShowTrainingWizardDialogAsync() => throw new NotImplementedException();
    public Task<StockAnalyzer.Avalonia.Models.BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags) => throw new NotImplementedException();
    public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? reminder = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null) => throw new NotImplementedException();
    [Obsolete]
    public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? reminder, Action<decimal?, decimal?, decimal?, string?>? onSave) => throw new NotImplementedException();
    public Task ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab initialTab = StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Deleted) => throw new NotImplementedException();
    public IMultiSyncProgressSession CreateMultiSyncProgressSession() => throw new NotImplementedException();
    public Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonSetupConfirmationAsync() => throw new NotImplementedException();
    public Task ShowManualSetupInstructionsAsync() => throw new NotImplementedException();
    public Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonUpdateConfirmationAsync() => throw new NotImplementedException();
    public Task ShowPythonManualUpdateInstructionsAsync() => throw new NotImplementedException();
    public Task RunWithProgressAsync(string title, Func<IProgress<string>, Task> action) => throw new NotImplementedException();
    public object? GetMainWindowOwner() => null;
    public Task ShowLogViewerAsync() => throw new NotImplementedException();
    public Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null) => throw new NotImplementedException();
    public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null, string? initialDirectory = null) => throw new NotImplementedException();
    public Task<bool> ShowExportChartImageDialogAsync(StockAnalyzer.Avalonia.ViewModels.ChartViewModel chartViewModel) => throw new NotImplementedException();
    public void Shutdown() { }
    public void ActivateMainWindow() { }
}

/// <summary>In-memory double for <see cref="IBacktestConfigurationManager"/>: no disk I/O, so tests
/// never touch the real Data/Config/backtest_configuration.json a live app would use.</summary>
internal sealed class FakeBacktestConfigurationManager : IBacktestConfigurationManager
{
    private BacktestConfigurationDto? _saved;
    public int SaveCallCount { get; private set; }
    public BacktestConfigurationDto? Saved => _saved;

    /// <summary>When set, overrides the default Save/Load round-trip behavior for a single LoadAsync call (e.g. to simulate a corrupt file or unknown schema version).</summary>
    public Func<Task<BacktestConfigurationLoadResult>>? LoadOverride { get; set; }

    public Task<BacktestConfigurationLoadResult> LoadAsync()
    {
        if (LoadOverride is not null) return LoadOverride();
        return Task.FromResult(_saved is null
            ? BacktestConfigurationLoadResult.NoFileDefaultsApplied()
            : BacktestConfigurationLoadResult.Loaded(_saved));
    }

    public Task SaveAsync(BacktestConfigurationDto configuration)
    {
        SaveCallCount++;
        _saved = configuration;
        return Task.CompletedTask;
    }
}

internal sealed class FakeScreenerCatalogProvider : IScreenerCatalogProvider
{
    private readonly IReadOnlyList<ScreenerCatalogItem> _items;
    private readonly Func<IndicatorType, IReadOnlyList<string>>? _outputSeriesNames;

    public int GetCatalogItemsCallCount { get; private set; }

    /// <summary><paramref name="outputSeriesNames"/> is a trailing, optional safe-extension (Task 6 of
    /// sa_implementation_plan_BacktestComparisonSignals.md): lets a test script a multi-series indicator's
    /// Output picker list. Every existing call site that omits it keeps the prior always-empty-array
    /// behavior unchanged.</summary>
    public FakeScreenerCatalogProvider(IReadOnlyList<ScreenerCatalogItem> items, Func<IndicatorType, IReadOnlyList<string>>? outputSeriesNames = null)
    {
        _items = items;
        _outputSeriesNames = outputSeriesNames;
    }

    public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null)
    {
        GetCatalogItemsCallCount++;
        return _items;
    }

    public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => null;

    public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) =>
        _outputSeriesNames?.Invoke(type) ?? Array.Empty<string>();
}

/// <summary>Minimal <see cref="IMarketDataProvider"/> double: only <see cref="GetAvailableTickersAsync"/>
/// (used by BacktestWindowViewModel.InitializeAsync's Symbol-suggest list) is implemented; every other
/// member throws since no test exercises them.</summary>
internal sealed class FakeMarketDataProvider : IMarketDataProvider
{
    private readonly IReadOnlyList<string> _tickers;

    public FakeMarketDataProvider(IReadOnlyList<string>? tickers = null) => _tickers = tickers ?? Array.Empty<string>();

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult(_tickers);

    public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw new NotImplementedException();
    public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => throw new NotImplementedException();
    public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw new NotImplementedException();
    public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw new NotImplementedException();
    public Task AddTickerAsync(string symbol) => throw new NotImplementedException();
    public Task AddTickersAsync(IEnumerable<string> symbols) => throw new NotImplementedException();
    public Task RemoveTickerAsync(string symbol) => throw new NotImplementedException();
    public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw new NotImplementedException();
    public void InvalidateMetadataCache(string ticker) => throw new NotImplementedException();
    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw new NotImplementedException();
    public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw new NotImplementedException();
}

/// <summary>Hand-rolled IToastNotificationService double (SAで改善 Round 2,
/// Y:\Temp\sa_improvement_plan_BacktestUiCleanupRound2.md Task 3) - records the last notification shown so
/// tests can assert on it, without needing a mocking framework.</summary>
internal sealed class FakeToastNotificationService : IToastNotificationService
{
    public string? NotificationMessage { get; private set; }
    public bool IsNotificationVisible { get; private set; }

    public void ShowNotification(string message)
    {
        NotificationMessage = message;
        IsNotificationVisible = true;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

internal sealed class FakeIndicatorFactory : IIndicatorFactory
{
    private readonly HashSet<IndicatorType> _registered;

    public FakeIndicatorFactory(IEnumerable<IndicatorType> registeredTypes) => _registered = new HashSet<IndicatorType>(registeredTypes);

    public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;

    public bool IsRegistered(IndicatorType type) => _registered.Contains(type);

    public IEnumerable<IndicatorType> GetRegisteredTypes() => _registered;
}

/// <summary>Builds a fully-wired <see cref="BacktestWindowViewModel"/> from hand-rolled test doubles (no mocking framework in this project - see AGENTS.md xUnit-only rule), with sensible overridable defaults for every dependency.</summary>
internal static class BacktestTestFactory
{
    public static BacktestWindowViewModel CreateViewModel(
        IDataService? dataService = null,
        IBacktestEngine? engine = null,
        IBacktestReportGenerator? reportGenerator = null,
        IBacktestReportSettingsManager? reportSettingsManager = null,
        IBacktestConfigurationManager? configurationManager = null,
        IDispatcherService? dispatcherService = null,
        ILocalizationService? localizationService = null,
        IScreenerCatalogProvider? catalogProvider = null,
        IMarketDataProvider? marketDataProvider = null,
        IBacktestDefaultSettingsManager? defaultSettingsManager = null,
        IBacktestEvaluationService? evaluationService = null)
    {
        ILocalizationService localization = localizationService ?? NullLocalizationService.Instance;
        var indicatorSelection = new BacktestIndicatorSelectionViewModel(
            catalogProvider ?? new FakeScreenerCatalogProvider(Array.Empty<ScreenerCatalogItem>()),
            localization,
            new FakeToastNotificationService());
        var results = new BacktestResultsViewModel(localization, new FakeBacktestReportExporter(), new FakeDialogService());
        IDataService runDataService = dataService ?? new FakeDataService();
        IBacktestReportSettingsManager runReportSettings = reportSettingsManager ?? new FakeBacktestReportSettingsManager();
        IBacktestConfigurationManager runConfigurationManager = configurationManager ?? new FakeBacktestConfigurationManager();

        return new BacktestWindowViewModel(
            new BacktestRunPreparationService(runDataService),
            engine ?? new ScriptableBacktestEngine(),
            reportGenerator ?? new StubBacktestReportGenerator(),
            new BacktestSettingsCoordinator(runConfigurationManager, runReportSettings),
            dispatcherService ?? new SynchronousDispatcherService(),
            localization,
            marketDataProvider ?? new FakeMarketDataProvider(),
            indicatorSelection,
            results,
            defaultSettingsManager,
            evaluationService);
    }

    public static BacktestResult CreateResult(RunStatus status = RunStatus.Completed, ImmutableArray<EquityPoint>? equityPoints = null,
        ExecutionModel executionModel = ExecutionModel.Legacy) =>
        new(
            orders: ImmutableArray<BacktestOrder>.Empty,
            fills: ImmutableArray<BacktestFill>.Empty,
            trades: ImmutableArray<BacktestTrade>.Empty,
            equityPoints: equityPoints ?? ImmutableArray<EquityPoint>.Empty,
            signals: ImmutableArray<BacktestSignal>.Empty,
            configuration: new BacktestConfiguration { ExecutionModel = executionModel, InitialCapital = 1_000_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m },
            status: status,
            strategyName: "NoOp",
            reproducibilityHash: Array.Empty<byte>(),
            isInsufficientData: false);

    public static BacktestReport CreateStubReport(BacktestReportRunFingerprint? runFingerprint = null,
        ExecutionModel? executionModel = null)
    {
        static MetricValue NotApplicable(MetricUnit unit) =>
            MetricValue.NonValid(MetricStatus.NotApplicable, unit, MetricReason.RiskDataMissing);
        return new BacktestReport
        {
            TotalPnL = NotApplicable(MetricUnit.Currency),
            MaxDrawdownAmount = NotApplicable(MetricUnit.Currency),
            ExpectedPayoff = NotApplicable(MetricUnit.Currency),
            TotalReturn = NotApplicable(MetricUnit.ReturnRatio),
            CAGR = NotApplicable(MetricUnit.ReturnRatio),
            WinRate = NotApplicable(MetricUnit.WinRateRatio),
            ProfitFactor = NotApplicable(MetricUnit.Dimensionless),
            MaxDrawdown = NotApplicable(MetricUnit.DrawdownRatio),
            UlcerIndex = NotApplicable(MetricUnit.PercentPoints),
            BarSharpe = NotApplicable(MetricUnit.Dimensionless),
            AnnualizedSharpe = NotApplicable(MetricUnit.Dimensionless),
            AnnualizedSharpeAutocorrelationAdjusted = NotApplicable(MetricUnit.Dimensionless),
            BarSortino = NotApplicable(MetricUnit.Dimensionless),
            AnnualizedSortino = NotApplicable(MetricUnit.Dimensionless),
            AnnualizedSortinoAutocorrelationAdjusted = NotApplicable(MetricUnit.Dimensionless),
            AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval = null,
            CalmarFullPeriod = NotApplicable(MetricUnit.Dimensionless),
            SQN = NotApplicable(MetricUnit.Dimensionless),
            RecoveryFactor = NotApplicable(MetricUnit.Dimensionless),
            TotalTrades = 0,
            WinTrades = 0,
            LossTrades = 0,
            BreakevenTrades = 0,
            SqnWarning = false,
            FormulaVersion = 1,
            AnnualPeriods = 252,
            AnnualRiskFreeRate = 0m,
            AnnualMAR = 0m,
            Frame = TimeFrame.D1,
            ExecutionModel = executionModel,
            ExecutionDisclosure = executionModel == ExecutionModel.YFinanceApproximate
                ? BacktestReport.YFinanceApproximateDisclosure
                : null,
            RunFingerprint = runFingerprint,
        };
    }
}
