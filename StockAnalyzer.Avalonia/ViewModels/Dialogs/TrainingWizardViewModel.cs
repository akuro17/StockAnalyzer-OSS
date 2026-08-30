using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Which population of tickers a training run scopes to. Resolved into
/// <see cref="TrainingJobConfig.Symbols"/> by <see cref="TrainingWizardViewModel"/>.
/// </summary>
public enum TrainingScopeKind
{
    AllTickers,
    Watchlist,
    Portfolio,
}

/// <summary>
/// One user-supplied trainer hyperparameter (passed through to the Python trainer as
/// <c>--&lt;key&gt; &lt;value&gt;</c>; see <c>run_training._build_trainer_argv</c>).
/// </summary>
public partial class TrainingHyperparameterEntry : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>
/// Collects a training job's scope / period / timeframe / architecture / hyperparameters and
/// walk-forward validation settings (split count, purge gap, out-of-sample holdout), starts it
/// via <see cref="ITrainingOrchestrator"/>, streams progress (including a live per-fold results
/// table, see <see cref="FoldResults"/>), records the run via <see cref="IExperimentLogService"/>,
/// and offers promoting the result via <see cref="IModelDeploymentService"/>. This release
/// trains OHLCV-only classification models (<see cref="PredictionFeatureMode.OhlcvMinMax"/>);
/// the feature-picker tab, regression, and ensembles are out of scope (later Group).
/// </summary>
public partial class TrainingWizardViewModel : ViewModelBase
{
    /// <summary>Architectures offered per framework, mirroring each trainer's own <c>ARCHES</c> tuple (train_pytorch.py / train_tensorflow.py) or the single GBDT path (train_lightgbm.py has no --arch).</summary>
    private static readonly IReadOnlyDictionary<TrainingFramework, string[]> ArchitecturesByFramework =
        new Dictionary<TrainingFramework, string[]>
        {
            [TrainingFramework.PyTorch] = new[] { "lstm", "cnn" },
            [TrainingFramework.LightGBM] = new[] { "gbdt" },
            [TrainingFramework.TensorFlow] = new[] { "lstm", "gru", "cnn" },
        };

    private readonly ITrainingOrchestrator? _orchestrator;
    private readonly IModelDeploymentService? _modelDeploymentService;
    private readonly IExperimentLogService? _experimentLogService;
    private readonly IWatchlistManager? _watchlistManager;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly ILogger<TrainingWizardViewModel> _logger;

    private IReadOnlyList<string> _allTickers = Array.Empty<string>();
    private CancellationTokenSource? _runCts;

    public ObservableCollection<WatchlistProfileSelectItemViewModel> WatchlistProfiles { get; } = new();
    public ObservableCollection<WatchlistProfileSelectItemViewModel> PortfolioProfiles { get; } = new();
    public ObservableCollection<TrainingHyperparameterEntry> Hyperparameters { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>
    /// Per-fold results table, appended live as the run emits fold-scoped <c>METRIC:</c> lines
    /// (see <see cref="FoldMetricRow"/>). Cleared at the start of each run. The out-of-sample
    /// line and the final aggregate line are not fold rows and are shown only in
    /// <see cref="LogLines"/>.
    /// </summary>
    public ObservableCollection<FoldMetricRow> FoldResults { get; } = new();

    /// <summary>Whether <see cref="FoldResults"/> has at least one row (drives the table's visibility).</summary>
    public bool HasFoldResults => FoldResults.Count > 0;

    /// <summary>
    /// Informational note the view surfaces next to Window/Horizon (Domain Invariant: bar
    /// counts, not calendar time). While both values are valid (&gt; 0) this also names the
    /// minimum raw bar count the selected scope's shortest history must contain for training to
    /// succeed at all (see <see cref="WalkForwardDataRequirement"/>) - added after three
    /// "Empty train or val split" reports that all traced back to a selected date range looking
    /// adequate by eye but not actually meeting this (non-obvious) requirement.
    /// </summary>
    public string WindowUnitNote
    {
        get
        {
            var baseNote = LocalizationManager.Instance["TrainingWizard_WindowUnitNote"]
                ?? "Window and horizon are counted in bars; their calendar span changes with the selected timeframe.";
            if (WindowSize <= 0 || Horizon <= 0)
            {
                return baseNote;
            }

            var minBarsTemplate = LocalizationManager.Instance["TrainingWizard_WindowUnitNote_MinBars"]
                ?? " At least {0} bars of history are required for this Window/Horizon.";
            var splitCount = NSplits >= 2 ? NSplits : WalkForwardDataRequirement.DefaultSplitCount;
            // A negative Gap (transient invalid input; Start stays disabled) falls back to the
            // default purge gap rather than throwing out of this getter, matching splitCount above.
            var minBars = WalkForwardDataRequirement.MinimumRawBars(
                WindowSize, Horizon, splitCount, Gap is >= 0 ? Gap : null);
            return baseNote + string.Format(minBarsTemplate, minBars);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllTickersScope))]
    [NotifyPropertyChangedFor(nameof(IsWatchlistScope))]
    [NotifyPropertyChangedFor(nameof(IsPortfolioScope))]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TrainingScopeKind _selectedScope = TrainingScopeKind.AllTickers;

    public bool IsAllTickersScope => SelectedScope == TrainingScopeKind.AllTickers;
    public bool IsWatchlistScope => SelectedScope == TrainingScopeKind.Watchlist;
    public bool IsPortfolioScope => SelectedScope == TrainingScopeKind.Portfolio;

    // DateTime?, not DateTimeOffset?: matches Avalonia's CalendarDatePicker.SelectedDate type
    // exactly (see EditTransactionDialogViewModel.ExecutedAt for the same established
    // convention). A DateTimeOffset? here would runtime-fail on TwoWay write-back from the
    // picker (bug: setting Start/End Date threw System.InvalidCastException).
    [ObservableProperty]
    private DateTime? _startDate;

    [ObservableProperty]
    private DateTime? _endDate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TrainingTimeframe _selectedTimeframe = TrainingTimeframe.Daily;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TrainingFramework _selectedFramework = TrainingFramework.PyTorch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _selectedArchitecture = ArchitecturesByFramework[TrainingFramework.PyTorch][0];

    public IReadOnlyList<string> AvailableArchitectures => ArchitecturesByFramework[SelectedFramework];

    /// <summary>All bar-aggregation levels the wizard's Timeframe combo box can offer.</summary>
    public static IReadOnlyList<TrainingTimeframe> AvailableTimeframes { get; } = Enum.GetValues<TrainingTimeframe>();

    /// <summary>All trainers the wizard's Framework combo box can offer.</summary>
    public static IReadOnlyList<TrainingFramework> AvailableFrameworks { get; } = Enum.GetValues<TrainingFramework>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int _windowSize = PredictionSettingsManager.DefaultWindowSize;

    /// <summary>
    /// Forward label horizon in bars. Deliberately left at 0 (invalid; Start stays disabled)
    /// rather than seeded with a guessed default: unlike <see cref="WindowSize"/>, there is no
    /// existing C#-side SSoT constant for it (only the training-side <c>dataset.DEFAULT_HORIZON</c>,
    /// which this layer cannot read without invoking Python). The user must set it explicitly.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int _horizon;

    /// <summary>
    /// Walk-forward validation split count (must be &gt;= 2). Mirrors
    /// <see cref="TrainingJobConfig.NSplits"/> and is seeded from
    /// <see cref="WalkForwardDataRequirement.DefaultSplitCount"/> (the C# SSoT mirror of the
    /// trainer's <c>dataset.DEFAULT_WF_SPLITS</c>). Propagated to the trainer as
    /// <c>--wf-splits</c> and drives how many rows the per-fold results table has.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int _nSplits = WalkForwardDataRequirement.DefaultSplitCount;

    /// <summary>
    /// Purge gap in bars between each fold's train and validation blocks.
    /// <see langword="null"/> leaves the split at its default (<c>window + horizon - 1</c>).
    /// Mirrors <see cref="TrainingJobConfig.Gap"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int? _gap;

    /// <summary>
    /// Fixed out-of-sample holdout length in trailing calendar days, excluded from training
    /// and walk-forward CV and scored once at the end. <see langword="null"/> disables the
    /// holdout. Mirrors <see cref="TrainingJobConfig.OosTailDays"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private int? _oosTailDays;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    private bool _isTraining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    private bool _isDeploying;

    [ObservableProperty]
    private string? _currentStage;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    private TrainingRunResult? _lastResult;

    public TrainingWizardViewModel(
        ITrainingOrchestrator? orchestrator = null,
        IModelDeploymentService? modelDeploymentService = null,
        IExperimentLogService? experimentLogService = null,
        IWatchlistManager? watchlistManager = null,
        IMarketDataProvider? marketDataProvider = null,
        ILogger<TrainingWizardViewModel>? logger = null)
    {
        _orchestrator = orchestrator;
        _modelDeploymentService = modelDeploymentService;
        _experimentLogService = experimentLogService;
        _watchlistManager = watchlistManager;
        _marketDataProvider = marketDataProvider;
        _logger = logger ?? NullLogger<TrainingWizardViewModel>.Instance;

        FoldResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFoldResults));

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_marketDataProvider is not null)
            {
                _allTickers = await _marketDataProvider.GetAvailableTickersAsync();
            }

            if (_watchlistManager is not null)
            {
                var profiles = _watchlistManager.GetAllProfiles();

                foreach (var profile in profiles.Where(p => !p.IsPortfolio).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    AddSelectableProfile(WatchlistProfiles, profile);
                }

                foreach (var profile in profiles.Where(p => p.IsPortfolio).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    AddSelectableProfile(PortfolioProfiles, profile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingWizardViewModel: failed to load ticker/profile lists.");
            StatusMessage = LocalizationManager.Instance["TrainingWizard_LoadScopeError"]
                ?? "Could not load the ticker/watchlist/portfolio lists.";
        }
    }

    private void AddSelectableProfile(ObservableCollection<WatchlistProfileSelectItemViewModel> target, StockAnalyzer.Core.Models.Watchlist.WatchlistProfile profile)
    {
        var item = new WatchlistProfileSelectItemViewModel(profile.Id, profile.Name, profile.IsPortfolio, isSelected: false);
        item.PropertyChanged += OnProfileSelectionChanged;
        target.Add(item);
    }

    private void OnProfileSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchlistProfileSelectItemViewModel.IsSelected))
        {
            StartTrainingCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedFrameworkChanged(TrainingFramework value)
    {
        OnPropertyChanged(nameof(AvailableArchitectures));
        SelectedArchitecture = ArchitecturesByFramework[value][0];
    }

    /// <summary>
    /// Sets <see cref="SelectedScope"/> from a <see cref="TrainingScopeKind"/> member name.
    /// Bound from the view's scope RadioButtons via CommandParameter, mirroring the existing
    /// string-CommandParameter -&gt; Enum.TryParse pattern used by
    /// <c>MainWindowViewModel.SetTimeframe(string)</c>.
    /// </summary>
    [RelayCommand]
    private void SetScope(string scopeName)
    {
        if (Enum.TryParse<TrainingScopeKind>(scopeName, out var scope))
        {
            SelectedScope = scope;
        }
    }

    [RelayCommand]
    private void AddHyperparameter() => Hyperparameters.Add(new TrainingHyperparameterEntry());

    [RelayCommand]
    private void RemoveHyperparameter(TrainingHyperparameterEntry? entry)
    {
        if (entry is not null)
        {
            Hyperparameters.Remove(entry);
        }
    }

    /// <summary>Resolves the current scope selection into a concrete, de-duplicated symbol list.</summary>
    private string[] ResolveSymbols()
    {
        IEnumerable<string> symbols = SelectedScope switch
        {
            TrainingScopeKind.AllTickers => _allTickers,
            TrainingScopeKind.Watchlist => ResolveProfileSymbols(WatchlistProfiles),
            TrainingScopeKind.Portfolio => ResolveProfileSymbols(PortfolioProfiles),
            _ => Array.Empty<string>(),
        };

        return symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IEnumerable<string> ResolveProfileSymbols(ObservableCollection<WatchlistProfileSelectItemViewModel> profiles)
    {
        if (_watchlistManager is null)
        {
            return Array.Empty<string>();
        }

        return profiles
            .Where(p => p.IsSelected)
            .SelectMany(p => _watchlistManager.GetProfileById(p.Id)?.Items.Select(i => i.Ticker) ?? Enumerable.Empty<string>());
    }

    private bool CanStartTraining() =>
        !IsTraining && !IsDeploying
        && _orchestrator is not null
        && WindowSize > 0 && Horizon > 0
        && NSplits >= 2
        && Gap is not (< 0)
        && OosTailDays is not (< 0)
        && !string.IsNullOrWhiteSpace(SelectedArchitecture)
        && ResolveSymbols().Length > 0;

    [RelayCommand(CanExecute = nameof(CanStartTraining))]
    private async Task StartTrainingAsync()
    {
        if (_orchestrator is null)
        {
            return;
        }

        var config = new TrainingJobConfig
        {
            Symbols = ResolveSymbols(),
            StartDate = StartDate is { } sd ? DateOnly.FromDateTime(sd.Date) : null,
            EndDate = EndDate is { } ed ? DateOnly.FromDateTime(ed.Date) : null,
            Timeframe = SelectedTimeframe,
            Framework = SelectedFramework,
            Architecture = SelectedArchitecture,
            WindowSize = WindowSize,
            Horizon = Horizon,
            NSplits = NSplits,
            Gap = Gap,
            OosTailDays = OosTailDays,
            Hyperparameters = Hyperparameters
                .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                .ToDictionary(h => h.Key.Trim(), h => h.Value, StringComparer.OrdinalIgnoreCase),
            // OutputName intentionally left null: run_training.py's _derive_output_stem already
            // implements NAME-01 (single symbol -> that symbol; else -> multi-<count>). Reusing
            // the real watchlist/portfolio name as the scope token (NAME-02) would require
            // duplicating that stem-assembly logic on the C# side; deferred to a follow-up.
        };

        try
        {
            config.Validate();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        IsTraining = true;
        ProgressPercent = 0;
        CurrentStage = null;
        LogLines.Clear();
        FoldResults.Clear();
        LastResult = null;
        StatusMessage = LocalizationManager.Instance["TrainingWizard_Status_Starting"] ?? "Starting training...";

        _runCts = new CancellationTokenSource();
        var progress = new Progress<TrainingProgress>(OnTrainingProgress);

        try
        {
            var result = await _orchestrator.StartTrainingAsync(config, progress, _runCts.Token).ConfigureAwait(true);
            LastResult = result;
            StatusMessage = result.Success
                ? (LocalizationManager.Instance["TrainingWizard_Status_Completed"] ?? "Training completed.")
                : (result.Message ?? LocalizationManager.Instance["TrainingWizard_Status_Failed"] ?? "Training failed.");

            if (_experimentLogService is not null)
            {
                try
                {
                    await _experimentLogService.RecordAsync(config, result).ConfigureAwait(true);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "TrainingWizardViewModel: failed to record experiment log for run {RunId}.", result.RunId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = LocalizationManager.Instance["TrainingWizard_Status_Cancelled"] ?? "Training cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingWizardViewModel: StartTrainingAsync failed.");
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_StartError"] ?? "Could not start training: {0}",
                ex.Message);
        }
        finally
        {
            IsTraining = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnTrainingProgress(TrainingProgress update)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        if (update.Stage is { Length: > 0 } stage)
        {
            CurrentStage = stage;
            LogLines.Add($"[{timestamp}] {stage} ({update.Percent}%)");
        }

        ProgressPercent = update.Percent;

        if (update.Metric is { Count: > 0 } metric)
        {
            var summary = string.Join(", ", metric.Select(kv => $"{kv.Key}={kv.Value:0.####}"));
            LogLines.Add($"[{timestamp}] {summary}");

            if (FoldMetricRow.FromMetric(metric) is { } foldRow)
            {
                var existingIndex = -1;
                for (var i = 0; i < FoldResults.Count; i++)
                {
                    if (FoldResults[i].Fold == foldRow.Fold)
                    {
                        existingIndex = i;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    FoldResults[existingIndex] = foldRow;
                }
                else
                {
                    FoldResults.Add(foldRow);
                }
            }
        }
    }

    private bool CanCancelTraining() => IsTraining;

    [RelayCommand(CanExecute = nameof(CanCancelTraining))]
    private void CancelTraining() => _runCts?.Cancel();

    private bool CanDeployToModels() => !IsTraining && !IsDeploying && _modelDeploymentService is not null && LastResult is { Success: true, OnnxArtifactPath: not null };

    [RelayCommand(CanExecute = nameof(CanDeployToModels))]
    private async Task DeployToModelsAsync()
    {
        if (_modelDeploymentService is null || LastResult is not { Success: true, OnnxArtifactPath: { } onnxPath } result)
        {
            return;
        }

        IsDeploying = true;
        try
        {
            var finalPath = await _modelDeploymentService.DeployAsync(onnxPath, result.MetricsArtifactPath).ConfigureAwait(true);
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_Deployed"] ?? "Deployed to {0}.",
                finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingWizardViewModel: DeployToModelsAsync failed.");
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_DeployError"] ?? "Deployment failed: {0}",
                ex.Message);
        }
        finally
        {
            IsDeploying = false;
        }
    }
}
