using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>One editable row of Group D's per-frame annualization-period list (spec §9.5), backed by <see cref="BacktestReportDefaults.AnnualPeriodsByFrame"/>.</summary>
public partial class AnnualPeriodEntry : ObservableObject
{
    public TimeFrame Frame { get; }

    [ObservableProperty]
    private int _periods;

    public AnnualPeriodEntry(TimeFrame frame, int periods)
    {
        Frame = frame;
        _periods = periods;
    }
}

/// <summary>
/// BacktestWindow's run-lifecycle state (spec §5.4, see Y:\Temp\sa_ai_context_BacktestUiWindow_P3.md
/// section 6 for the transition table this enum and <see cref="BacktestWindowViewModel"/> implement).
/// </summary>
public enum BacktestRunState
{
    Idle,
    Validating,
    Running,
    Cancelling,
    Completed,
    Cancelled,
    Failed,
    Insolvent,
    Unsupported,
}

/// <summary>
/// Central orchestrator for BacktestWindow (spec §5.4/§5.1): owns the Configuration-tab (Group A-D)
/// bindable fields, composes the Tab-2 <see cref="BacktestIndicatorSelectionViewModel"/> (task 7), and
/// drives the Run/Cancel state machine against <see cref="IBacktestEngine"/> + <see cref="NoOpBacktestStrategy"/>
/// (Gate G7) + <see cref="IBacktestReportGenerator"/>. Every later tab ViewModel/View (tasks 9-14)
/// composes into this instance rather than re-implementing run orchestration.
///
/// Cancellation model (P3 Hardening Task 5): <see cref="CancelBacktestCommand"/> both flips <see cref="State"/>
/// to <see cref="BacktestRunState.Cancelling"/> and requests real, cooperative abortion of the in-flight
/// <see cref="IBacktestEngine.Run"/> call via a per-run <see cref="CancellationTokenSource"/> - the engine's
/// bar loop and indicator prep observe the token and throw <see cref="OperationCanceledException"/> rather
/// than running to completion. The generation counter (<see cref="_generation"/>) is retained alongside this
/// and serves a different purpose: it discards a stale run's outcome (including a cancellation that arrives
/// too late) so it can never overwrite a newer run's result, independent of whether the abort itself was
/// timely. Commands run on the UI thread (CommunityToolkit.Mvvm's synchronous RelayCommand execution), so
/// the "cancel lands after the result already committed" race in spec §5.4 resolves for free via
/// <see cref="CancelBacktestCommand"/>'s CanExecute gate: once a commit has already flipped <see cref="State"/>
/// away from Running/Cancelling, Cancel can no longer execute.
/// </summary>
public partial class BacktestWindowViewModel : ViewModelBase
{
    private readonly IBacktestRunPreparationService _runPreparationService;
    private readonly IBacktestEngine _engine;
    private readonly IBacktestReportGenerator _reportGenerator;
    private readonly IBacktestEvaluationService? _evaluationService;
    private readonly IBacktestSettingsCoordinator _settingsCoordinator;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILocalizationService _localizationService;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IBacktestDefaultSettingsManager? _defaultSettingsManager;

    /// <summary>Defaults for Groups B/C/D adopted by construction, Reset to Defaults and no-file Restore. Factory values until <see cref="InitializeAsync"/> loads the user's Settings > Backtest values (snapshot at open: later Settings edits do not change an open window).</summary>
    private BacktestDefaultSettings _defaultSettings = BacktestDefaultSettings.BuiltIn;

    private long _generation;
    private readonly object _ctsGate = new();

    /// <summary>The current run's cancellation source. The owning run disposes it in its finalization path
    /// and clears this field only while it still refers to the same source.</summary>
    private CancellationTokenSource? _cts;

    public BacktestIndicatorSelectionViewModel IndicatorSelection { get; }

    public BacktestResultsViewModel Results { get; }

    /// <summary>Tab 1's Frame ComboBox source: restricted to the frames the backtest data loader
    /// actually supports (<see cref="StockAnalyzer.Core.Services.ParquetDataService.LoadCandlesAsync"/>
    /// only reads Daily/Weekly/Monthly Parquet files) - selecting any other TimeFrame would already
    /// fail on Run with a NotSupportedException, so it is not offered here.</summary>
    public IReadOnlyList<TimeFrame> AvailableFrames { get; } = new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

    public IReadOnlyList<PositionSizingModel> AvailableSizingModels { get; } = Enum.GetValues<PositionSizingModel>();

    public IReadOnlyList<ExecutionModel> AvailableExecutionModels { get; } = Enum.GetValues<ExecutionModel>();

    public decimal SlippageRatioMinimum => BacktestConfigurationBounds.InclusiveRatioMinimum;
    public decimal SlippageRatioMaximum => BacktestConfigurationBounds.BelowUnitRatioMaximum;
    public decimal InitialMarginRatioMinimum => BacktestConfigurationBounds.PositiveRatioMinimum;
    public decimal InitialMarginRatioMaximum => BacktestConfigurationBounds.InclusiveRatioMaximum;
    public decimal MaintenanceMarginRatioMinimum => BacktestConfigurationBounds.PositiveRatioMinimum;
    public decimal MaintenanceMarginRatioMaximum => BacktestConfigurationBounds.BelowUnitRatioMaximum;
    public decimal LiquidationPenaltyRatioMinimum => BacktestConfigurationBounds.InclusiveRatioMinimum;
    public decimal LiquidationPenaltyRatioMaximum => BacktestConfigurationBounds.BelowUnitRatioMaximum;

    /// <summary>Symbol field's ticker-suggest source (spec-external UX request: mirror MainWindow's
    /// Symbol Selector AutoCompleteBox), populated once in <see cref="InitializeAsync"/> from the same
    /// DI-registered <see cref="IMarketDataProvider"/> MainWindow itself uses - no new service.</summary>
    public ObservableCollection<string> AvailableSymbols { get; } = new();

    // Group A: target data and evaluation period.
    [ObservableProperty]
    private ExecutionModel _executionModel;

    [ObservableProperty]
    private string _symbol = string.Empty;

    [ObservableProperty]
    private TimeFrame _frame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvaluationStartUtcTooltip))]
    private DateTime _evaluationStartUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvaluationEndUtcTooltip))]
    private DateTime _evaluationEndUtc;

    public string EvaluationStartUtcTooltip => NormalizeUiDate(EvaluationStartUtc).ToString("O", CultureInfo.InvariantCulture);
    public string EvaluationEndUtcTooltip => NormalizeUiDate(EvaluationEndUtc).ToString("O", CultureInfo.InvariantCulture);

    // Group B: capital and cost model.
    [ObservableProperty]
    private decimal _initialCapital;

    [ObservableProperty]
    private decimal _commissionFlat;

    [ObservableProperty]
    private decimal _commissionPerUnit;

    [ObservableProperty]
    private decimal _slippageRatio;

    [ObservableProperty]
    private PositionSizingModel _sizingModel;

    [ObservableProperty]
    private decimal _sizingParameter;

    // Group C: margin model.
    [ObservableProperty]
    private decimal _initialMarginRatio;

    [ObservableProperty]
    private decimal _maintenanceMarginRatio;

    [ObservableProperty]
    private decimal _liquidationPenaltyRatio;

    // Group D: report/bootstrap parameters (AnnualRiskFreeRate/AnnualMAR/AnnualPeriodsByFrame reuse
    // IBacktestReportSettingsManager per spec - populated in InitializeAsync; BootstrapSeed/Iterations
    // are this feature's own new persisted fields, see BacktestConfigurationDto).
    [ObservableProperty]
    private decimal _annualRiskFreeRate;

    [ObservableProperty]
    private decimal _annualMar;

    public ObservableCollection<AnnualPeriodEntry> AnnualPeriodsByFrame { get; } = new();

    [ObservableProperty]
    private int _bootstrapSeed;

    [ObservableProperty]
    private int _bootstrapIterations;

    // Run-lifecycle state.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBacktestCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelBacktestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetToDefaultsCommand))]
    private BacktestRunState _state = BacktestRunState.Idle;

    /// <summary>Localized status-bar label for <see cref="State"/> (spec §9.8's 8-state text list) - kept as a VM-owned string so the View never switches on the enum itself (DT-1/DT-4).</summary>
    [ObservableProperty]
    private string _stateDisplayText = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    public BacktestResult? LastResult => Results.Presentation?.Result;

    public BacktestReport? LastReport => Results.Presentation?.Report;

    public BacktestEvaluationArtifact? LastEvaluationArtifact => Results.Presentation?.Artifact;

    public BacktestWindowViewModel(
        IBacktestRunPreparationService runPreparationService,
        IBacktestEngine engine,
        IBacktestReportGenerator reportGenerator,
        IBacktestSettingsCoordinator settingsCoordinator,
        IDispatcherService dispatcherService,
        ILocalizationService localizationService,
        IMarketDataProvider marketDataProvider,
        BacktestIndicatorSelectionViewModel indicatorSelection,
        BacktestResultsViewModel results,
        IBacktestDefaultSettingsManager? defaultSettingsManager = null,
        IBacktestEvaluationService? evaluationService = null)
    {
        _defaultSettingsManager = defaultSettingsManager;
        _runPreparationService = runPreparationService ?? throw new ArgumentNullException(nameof(runPreparationService));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _evaluationService = evaluationService;
        _settingsCoordinator = settingsCoordinator ?? throw new ArgumentNullException(nameof(settingsCoordinator));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        IndicatorSelection = indicatorSelection ?? throw new ArgumentNullException(nameof(indicatorSelection));
        Results = results ?? throw new ArgumentNullException(nameof(results));
        Results.ConditionSelection = IndicatorSelection;

        ApplyBuiltInDefaults();
        StateDisplayText = string.Empty;
    }

    /// <summary>Loads Group D's shared report defaults (spec §9.5; reused as-is from P2's <see cref="IBacktestReportSettingsManager"/>) into the bindable Group D properties. Cheap local-file IO; safe to await before showing the window.</summary>
    public async Task InitializeAsync()
    {
        if (_defaultSettingsManager is not null)
        {
            _defaultSettings = (await _defaultSettingsManager.LoadAsync().ConfigureAwait(true)).Settings;
            ApplyCapitalMarginAndBootstrap(BuildBuiltInConfigurationDto());
        }

        // A saved report-settings file (window's Save Settings) wins; only the built-in fallback is replaced by the Settings > Backtest values.
        BacktestReportSettingsLoadResult reportSettings = await _settingsCoordinator.LoadReportSettingsAsync().ConfigureAwait(true);
        ApplyReportDefaults(ResolveReportDefaults(reportSettings));

        IReadOnlyList<string> tickers = await _marketDataProvider.GetAvailableTickersAsync().ConfigureAwait(true);
        AvailableSymbols.Clear();
        foreach (string ticker in tickers)
        {
            AvailableSymbols.Add(ticker);
        }
    }

    private bool CanRunBacktest() => State is BacktestRunState.Idle or BacktestRunState.Completed
        or BacktestRunState.Cancelled or BacktestRunState.Failed or BacktestRunState.Insolvent
        or BacktestRunState.Unsupported;

    /// <summary>Raised when Cancel is pressed while no run is in flight (any state where Run is allowed): the window closes (explicit user request, overrides the earlier Idle-only scope).</summary>
    public event Action? RequestClose;

    private bool CanCancelBacktest() => State == BacktestRunState.Running || CanRunBacktest();

    [RelayCommand(CanExecute = nameof(CanRunBacktest))]
    private async Task RunBacktestAsync()
    {
        long myGeneration = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        lock (_ctsGate)
        {
            _cts = cts;
        }

        StatusMessage = null;
        State = BacktestRunState.Validating;

        PreparedBacktestRun? prepared = null;
        BacktestRunSnapshot? runSnapshot = null;
        BacktestResult? result = null;
        BacktestReport? report = null;
        BacktestEvaluationArtifact? artifact = null;
        Exception? failure = null;
        bool enteredRunning = false;
        try
        {
            runSnapshot = BuildRunSnapshot();
            prepared = await _runPreparationService.PrepareAsync(runSnapshot, cts.Token).ConfigureAwait(true);
            if (!IsCurrent(myGeneration)) return;
            cts.Token.ThrowIfCancellationRequested();

            State = BacktestRunState.Running;
            enteredRunning = true;

            if (_evaluationService is not null)
            {
                artifact = await Task.Run(
                    () => _evaluationService.Evaluate(
                        prepared.Input,
                        prepared.Configuration,
                        prepared.StrategySpecification,
                        prepared.ReportOptions,
                        samplingEvidence: null,
                        cts.Token),
                    cts.Token).ConfigureAwait(false);
                result = artifact.Result;
                report = artifact.Report;
            }
            else
            {
                result = await Task.Run(
                    () => _engine.Run(prepared.Input, prepared.Configuration, prepared.Strategy, cts.Token),
                    cts.Token).ConfigureAwait(false);
                if (result.Status == RunStatus.Completed)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    report = _reportGenerator is ICancellableBacktestReportGenerator cancellable
                        ? cancellable.Generate(result, prepared.ReportOptions, cts.Token)
                        : GenerateWithBoundaryCancellation(result, prepared.ReportOptions, cts.Token);
                }
            }
            if (!IsCurrent(myGeneration)) return;
            cts.Token.ThrowIfCancellationRequested();

            if (result.Status == RunStatus.Completed)
            {
                cts.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (IsCurrent(myGeneration))
            {
                await _dispatcherService.PostAsync(() =>
                {
                    if (enteredRunning)
                    {
                        CommitRunOutcome(
                            myGeneration,
                            artifact,
                            result,
                            report,
                            failure,
                            prepared!.EvaluationStartIndex,
                            runSnapshot!.Conditions,
                            prepared.Strategy is NoOpBacktestStrategy,
                            prepared.Strategy is ConditionBasedBacktestStrategy conditionStrategy
                                ? conditionStrategy.RiskManagement
                                : null,
                            cts.Token);
                    }
                    else if (failure is not null)
                    {
                        CommitPreparationFailure(myGeneration, failure);
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            lock (_ctsGate)
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
                cts.Dispose();
            }
        }
    }

    private BacktestReport GenerateWithBoundaryCancellation(
        BacktestResult result,
        BacktestReportOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BacktestReport report = _reportGenerator.Generate(result, options);
        cancellationToken.ThrowIfCancellationRequested();
        return report;
    }

    [RelayCommand(CanExecute = nameof(CanCancelBacktest))]
    private void CancelBacktest()
    {
        if (CanRunBacktest())
        {
            RequestClose?.Invoke();
            return;
        }

        if (State != BacktestRunState.Running) return;
        State = BacktestRunState.Cancelling;
        lock (_ctsGate)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>Status-bar Save Settings action (spec §9.8) - persists Tab 1/2's editable fields via <see cref="IBacktestConfigurationManager"/> (task 6). Left enabled in every <see cref="State"/> (spec §9.8 only calls out Run/Restore as disabled while Running) since it only reads the current bindable fields, never the run's own execution snapshot.</summary>
    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        try
        {
            BacktestConfigurationDto configuration = BuildConfigurationDto();
            BacktestReportDefaults reportDefaults = BuildReportDefaults();
            BacktestSettingsSave result = await _settingsCoordinator.SaveAsync(configuration, reportDefaults).ConfigureAwait(true);
            StatusMessage = result.IsComplete
                ? _localizationService.GetString("Backtest_Config_SaveSuccess")
                : result.ConfigurationSaved
                    ? _localizationService.GetFormattedString(
                        "Backtest_Config_SavePartialError",
                        "Configuration was saved, but report defaults were not: {0}",
                        result.Failure?.Message ?? string.Empty)
                    : _localizationService.GetFormattedString(
                        "Backtest_Config_SaveError",
                        "Failed to save backtest configuration: {0}",
                        result.Failure?.Message ?? string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = _localizationService.GetFormattedString("Backtest_Config_SaveError", "Failed to save backtest configuration: {0}", ex.Message);
        }
    }

    /// <summary>Status-bar Restore Settings action (spec §9.8/§5.5): Loaded applies the saved DTO; NoFileDefaultsApplied resets to the same built-in defaults the window opens with; Error leaves every bindable field and the on-disk file untouched and only reports the message.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBacktest))]
    private async Task RestoreConfigurationAsync()
    {
        try
        {
            BacktestSettingsLoad settings = await _settingsCoordinator.LoadAsync().ConfigureAwait(true);
            if (settings.Configuration.Status == BacktestConfigurationLoadStatus.Error)
            {
                StatusMessage = _localizationService.GetFormattedString("Backtest_Config_LoadError", "Failed to load backtest configuration: {0}", settings.Configuration.ErrorMessage ?? string.Empty);
                return;
            }

            var candidate = new BacktestSettingsCandidate(
                settings.Configuration.Status == BacktestConfigurationLoadStatus.Loaded
                    ? BacktestConfigurationSnapshot.Create(settings.Configuration.Configuration!)
                    : BuildBuiltInConfigurationDto(),
                settings.Configuration.Status == BacktestConfigurationLoadStatus.NoFileDefaultsApplied,
                BacktestReportDefaults.SnapshotValidated(ResolveReportDefaults(settings.ReportSettings)));
            ApplySettingsCandidate(candidate);

            StatusMessage = settings.ReportSettings.Status == BacktestReportSettingsLoadStatus.BuiltInFallback
                ? _localizationService.GetString("Backtest_Config_RestoreReportFallback")
                : candidate.UseBuiltInConfiguration
                    ? _localizationService.GetString("Backtest_Config_NoFileDefaultsApplied")
                    : _localizationService.GetString("Backtest_Config_RestoreSuccess");
        }
        catch (Exception ex)
        {
            StatusMessage = _localizationService.GetFormattedString("Backtest_Config_LoadError", "Failed to load backtest configuration: {0}", ex.Message);
        }
    }

    private sealed record BacktestSettingsCandidate(
        BacktestConfigurationDto Configuration,
        bool UseBuiltInConfiguration,
        BacktestReportDefaults ReportDefaults);

    private void ApplySettingsCandidate(BacktestSettingsCandidate candidate)
    {
        ApplyConfigurationDto(candidate.Configuration);
        ApplyReportDefaults(candidate.ReportDefaults);
    }

    /// <summary>
    /// Explicit Reset to Defaults recovery path (sa_implementation_plan_BacktestP3Hardening.md Task 2):
    /// a corrupt/unrecognized configuration file leaves the UI and the on-disk file untouched by design
    /// (see <see cref="RestoreConfigurationAsync"/>'s Error case), which previously left no direct way to
    /// get back to a known-good state short of manually re-entering every field. Reuses the same
    /// <see cref="ApplyBuiltInDefaults"/> the constructor and no-file Restore already call - no new
    /// default-resolution logic.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBacktest))]
    private void ResetToDefaults()
    {
        ApplyBuiltInDefaults();
        StatusMessage = _localizationService.GetString("Backtest_Config_ResetToDefaults");
    }

    private BacktestConfigurationDto BuildConfigurationDto() => new()
    {
        SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
        ExecutionModel = ExecutionModel,
        Symbol = Symbol,
        Frame = Frame,
        EvaluationStartUtc = NormalizeUiDate(EvaluationStartUtc),
        EvaluationEndUtc = NormalizeUiDate(EvaluationEndUtc),
        InitialCapital = InitialCapital,
        CommissionFlat = CommissionFlat,
        CommissionPerUnit = CommissionPerUnit,
        SlippageRatio = SlippageRatio,
        SizingModel = SizingModel,
        SizingParameter = SizingParameter,
        InitialMarginRatio = InitialMarginRatio,
        MaintenanceMarginRatio = MaintenanceMarginRatio,
        LiquidationPenaltyRatio = LiquidationPenaltyRatio,
        BootstrapSeed = BootstrapSeed,
        BootstrapIterations = BootstrapIterations,
        SelectedIndicators = IndicatorSelection.AddedIndicators
            .Select(item => new BacktestSelectedIndicatorDto { Key = item.Key, Type = item.Type, Frame = item.Frame, Parameters = item.Parameters?.Clone() })
            .ToList(),
        ConditionEntries = IndicatorSelection.BuildConditionEntryDtos(),
        RiskManagement = IndicatorSelection.BuildRiskManagementSettingsDto(),
    };

    private BacktestReportDefaults BuildReportDefaults() => BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
    {
        SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
        AnnualRiskFreeRate = AnnualRiskFreeRate,
        AnnualMAR = AnnualMar,
        AnnualPeriodsByFrame = AnnualPeriodsByFrame.ToDictionary(entry => entry.Frame, entry => entry.Periods),
    });

    private BacktestReportDefaults ResolveReportDefaults(BacktestReportSettingsLoadResult loaded) =>
        loaded.Status == BacktestReportSettingsLoadStatus.BuiltInFallback
            ? _defaultSettings.ToReportDefaults()
            : loaded.Defaults;

    /// <summary>Applies only the Group B/C and bootstrap fields, leaving Group A and the Tab 2 selections untouched; the single place that maps them onto the bindable properties.</summary>
    private void ApplyCapitalMarginAndBootstrap(BacktestConfigurationDto dto)
    {
        InitialCapital = dto.InitialCapital;
        CommissionFlat = dto.CommissionFlat;
        CommissionPerUnit = dto.CommissionPerUnit;
        SlippageRatio = dto.SlippageRatio;
        SizingModel = dto.SizingModel;
        SizingParameter = dto.SizingParameter;
        InitialMarginRatio = dto.InitialMarginRatio;
        MaintenanceMarginRatio = dto.MaintenanceMarginRatio;
        LiquidationPenaltyRatio = dto.LiquidationPenaltyRatio;
        BootstrapSeed = dto.BootstrapSeed;
        BootstrapIterations = dto.BootstrapIterations;
    }

    private void ApplyReportDefaults(BacktestReportDefaults defaults)
    {
        BacktestReportDefaults snapshot = BacktestReportDefaults.SnapshotValidated(defaults);
        AnnualRiskFreeRate = snapshot.AnnualRiskFreeRate;
        AnnualMar = snapshot.AnnualMAR;
        AnnualPeriodsByFrame.Clear();
        foreach (KeyValuePair<TimeFrame, int> entry in snapshot.AnnualPeriodsByFrame)
        {
            AnnualPeriodsByFrame.Add(new AnnualPeriodEntry(entry.Key, entry.Value));
        }
    }

    private void ApplyConfigurationDto(BacktestConfigurationDto dto)
    {
        ExecutionModel = dto.ExecutionModel;
        Symbol = dto.Symbol;
        Frame = dto.Frame;
        EvaluationStartUtc = dto.EvaluationStartUtc;
        EvaluationEndUtc = dto.EvaluationEndUtc;
        ApplyCapitalMarginAndBootstrap(dto);
        IndicatorSelection.ReplaceAddedIndicators(dto.SelectedIndicators);
        IndicatorSelection.ReplaceConditionEntries(dto.ConditionEntries);
        IndicatorSelection.ReplaceRiskManagementSettings(dto.RiskManagement);
    }

    /// <summary>Single SSoT for this window's built-in Group A-C/bootstrap defaults (spec §5.5), used both at construction and by a no-file Restore - avoids repeating the same literal defaults in two places.</summary>
    private void ApplyBuiltInDefaults()
    {
        ApplyConfigurationDto(BuildBuiltInConfigurationDto());
    }

    private BacktestConfigurationDto BuildBuiltInConfigurationDto()
    {
        DateTime todayUtc = DateTime.UtcNow.Date;
        return new BacktestConfigurationDto
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            ExecutionModel = StockAnalyzer.Core.Models.Backtest.Engine.ExecutionModel.Legacy,
            Symbol = string.Empty,
            Frame = TimeFrame.D1,
            EvaluationStartUtc = todayUtc.AddYears(-1),
            EvaluationEndUtc = todayUtc,
            InitialCapital = _defaultSettings.InitialCapital,
            CommissionFlat = _defaultSettings.CommissionFlat,
            CommissionPerUnit = _defaultSettings.CommissionPerUnit,
            SlippageRatio = _defaultSettings.SlippageRatio,
            SizingModel = _defaultSettings.SizingModel,
            SizingParameter = _defaultSettings.SizingParameter,
            InitialMarginRatio = _defaultSettings.InitialMarginRatio,
            MaintenanceMarginRatio = _defaultSettings.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = _defaultSettings.LiquidationPenaltyRatio,
            BootstrapSeed = _defaultSettings.BootstrapSeed,
            BootstrapIterations = _defaultSettings.BootstrapIterations,
            SelectedIndicators = new List<BacktestSelectedIndicatorDto>(),
            ConditionEntries = new List<BacktestConditionEntryDto>(),
            RiskManagement = null,
        };
    }

    partial void OnStateChanged(BacktestRunState value)
    {
        StateDisplayText = value == BacktestRunState.Idle
            ? string.Empty
            : _localizationService.GetString(value switch
            {
                BacktestRunState.Validating => "Backtest_State_Validating",
                BacktestRunState.Running => "Backtest_State_Running",
                BacktestRunState.Cancelling => "Backtest_State_Cancelling",
                BacktestRunState.Completed => "Backtest_State_Completed",
                BacktestRunState.Cancelled => "Backtest_State_Cancelled",
                BacktestRunState.Failed => "Backtest_State_Failed",
                BacktestRunState.Insolvent => "Backtest_State_Insolvent",
                BacktestRunState.Unsupported => "Backtest_State_Unsupported",
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown BacktestRunState."),
            });
    }

    /// <summary>
    /// Runs on the UI thread (via <see cref="IDispatcherService"/>). Discards stale completions
    /// (superseded by <see cref="OnWindowClosing"/> bumping <see cref="_generation"/>), and per spec
    /// §5.4 only a Completed run replaces <see cref="LastResult"/>/<see cref="LastReport"/> -
    /// Cancelled/Failed/Insolvent all leave the previously displayed results untouched.
    /// </summary>
    private void CommitPreparationFailure(long generation, Exception failure)
    {
        if (!IsCurrent(generation)) return;
        if (failure is StrictEvidenceAdapterUnavailableException)
        {
            State = BacktestRunState.Unsupported;
            StatusMessage = _localizationService.GetString("Backtest_StrictEvidence_AdapterUnavailable");
        }
        else if (failure is YFinanceApproximateSourceUnavailableException)
        {
            State = BacktestRunState.Unsupported;
            StatusMessage = _localizationService.GetString("Backtest_YFinanceApproximate_SourceUnavailable");
        }
        else
        {
            State = BacktestRunState.Idle;
            StatusMessage = failure.Message;
        }
    }

    private void CommitRunOutcome(
        long generation,
        BacktestEvaluationArtifact? artifact,
        BacktestResult? result,
        BacktestReport? report,
        Exception? failure,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        CancellationToken runToken)
    {
        if (!IsCurrent(generation)) return;

        // P3 Hardening Task 5: a real engine-side cancellation surfaces here as OperationCanceledException
        // (thrown by IBacktestEngine.Run's CancellationToken checks) - this must resolve to Cancelled, not
        // Failed, distinct from the generic "any other exception" branch below. Checked by exception TYPE
        // (not by State == Cancelling) so the existing, separately-confirmed rule that a genuine non-cancellation
        // exception occurring while Cancelling still resolves to Failed is left untouched.
        if (failure is OperationCanceledException cancellation &&
            runToken.IsCancellationRequested &&
            cancellation.CancellationToken == runToken)
        {
            State = BacktestRunState.Cancelled;
            StatusMessage = null;
            return;
        }

        if (failure is not null)
        {
            State = BacktestRunState.Failed;
            StatusMessage = failure.Message;
            return;
        }

        if (State == BacktestRunState.Cancelling)
        {
            State = BacktestRunState.Cancelled;
            StatusMessage = null;
            return;
        }

        switch (result!.Status)
        {
            case RunStatus.Completed:
                if (report is null)
                {
                    State = BacktestRunState.Failed;
                    StatusMessage = _localizationService.GetString("Backtest_Status_Failed_Generic");
                    break;
                }
                bool presentationUpdated = artifact is not null
                    ? Results.TryUpdate(artifact, evaluationStartIndex, conditions, isNoOp, riskManagement, out Exception? presentationFailure)
                    : Results.TryUpdate(result, report, evaluationStartIndex, conditions, isNoOp, riskManagement, out presentationFailure);
                if (!presentationUpdated)
                {
                    State = BacktestRunState.Failed;
                    StatusMessage = presentationFailure?.Message ?? _localizationService.GetString("Backtest_Status_Failed_Generic");
                    break;
                }
                OnPropertyChanged(nameof(LastResult));
                OnPropertyChanged(nameof(LastReport));
                OnPropertyChanged(nameof(LastEvaluationArtifact));
                State = BacktestRunState.Completed;
                StatusMessage = result.IsInsufficientData ? _localizationService.GetString("Backtest_Status_InsufficientData") : null;
                break;
            case RunStatus.Insolvent:
                State = BacktestRunState.Insolvent;
                StatusMessage = _localizationService.GetString("Backtest_Status_Insolvent");
                break;
            case RunStatus.Cancelled:
                State = BacktestRunState.Cancelled;
                StatusMessage = null;
                break;
            case RunStatus.Failed:
                State = BacktestRunState.Failed;
                StatusMessage = _localizationService.GetString("Backtest_Status_Failed_Generic");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result.Status, "Unknown RunStatus.");
        }
    }

    private BacktestConfiguration BuildConfiguration() => new()
    {
        ExecutionModel = ExecutionModel,
        InitialCapital = InitialCapital,
        CommissionFlat = CommissionFlat,
        CommissionPerUnit = CommissionPerUnit,
        SlippageRatio = SlippageRatio,
        SizingModel = SizingModel,
        SizingParameter = SizingParameter,
        InitialMarginRatio = InitialMarginRatio,
        MaintenanceMarginRatio = MaintenanceMarginRatio,
        LiquidationPenaltyRatio = LiquidationPenaltyRatio,
    };

    private BacktestRunSnapshot BuildRunSnapshot()
    {
        DateTime evaluationStartUtc = NormalizeUiDate(EvaluationStartUtc);
        DateTime evaluationEndUtc = NormalizeUiDate(EvaluationEndUtc);
        BacktestConfiguration configuration = BuildConfiguration();
        configuration.Validate();

        ImmutableArray<BacktestConditionEntry> conditions = BacktestConditionValidator.Snapshot(
            IndicatorSelection.ConditionEntries,
            checked((int)IndicatorSelection.MaxConditionOffset));
        BacktestRiskManagementSettings? riskManagement = IndicatorSelection.BuildRiskManagementSettings();
        BacktestConditionValidator.ValidateRiskManagement(riskManagement);
        BacktestRiskManagementSettings? ownedRisk = riskManagement is null ? null : new BacktestRiskManagementSettings
        {
            StopLossPercent = riskManagement.StopLossPercent,
            TakeProfitPercent = riskManagement.TakeProfitPercent,
        };

        ImmutableArray<StrategyIndicatorRequest> selectedIndicators = IndicatorSelection.AddedIndicators
            .Select(item => new StrategyIndicatorRequest(
                item.Key,
                item.Type,
                item.Parameters?.Clone(),
                item.Frame))
            .ToImmutableArray();

        return new BacktestRunSnapshot(
            Symbol,
            Frame,
            evaluationStartUtc,
            evaluationEndUtc,
            configuration,
            conditions,
            ownedRisk,
            selectedIndicators,
            BuildReportDefaults(),
            BootstrapSeed,
            BootstrapIterations);
    }

    private bool IsCurrent(long generation) => generation == Interlocked.Read(ref _generation);

    private static DateTime NormalizeUiDate(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    /// <summary>
    /// Window-close hook (spec §5.4 "Window close while Running must request cancellation and must NOT
    /// block the UI thread"): bumping the generation counter makes any in-flight <see cref="RunBacktestAsync"/>
    /// completion a no-op in <see cref="CommitRunOutcome"/> without waiting on or cancelling the
    /// background <see cref="Task"/> itself.
    /// </summary>
    public void OnWindowClosing()
    {
        Interlocked.Increment(ref _generation);
        lock (_ctsGate)
        {
            _cts?.Cancel();
        }
    }
}
