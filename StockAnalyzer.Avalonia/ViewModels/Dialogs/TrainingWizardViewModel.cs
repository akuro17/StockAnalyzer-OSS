using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// One row of the ticker-list tree of the Training Settings tab: the node's own name, an optional icon (path data from the ticker tab's SSoT) and, for a node
/// that can be trained on, a selection flag. Written for the wizard alone; the portfolio RL dialog has its own item type.
/// </summary>
public sealed partial class TrainingSourceTreeItem : ObservableObject
{
    public TrainingSourceTreeItem(TickerGroupNode node, string? iconData, bool isSelectable)
    {
        Node = node; Name = node.DisplayName; IconData = iconData; IsSelectable = isSelectable;
    }
    public TickerGroupNode Node { get; }
    public string Name { get; }
    public string? IconData { get; }
    public bool HasIcon => IconData is not null;
    public bool IsSelectable { get; }
    [ObservableProperty] private bool _isSelected;
    public ObservableCollection<TrainingSourceTreeItem> Children { get; } = new();
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
/// and offers promoting the result via <see cref="IModelDeploymentService"/>. Runs train the fixed
/// OHLCV min-max model (<see cref="PredictionFeatureMode.OhlcvMinMax"/>) unless
/// <see cref="UseComposedFeatures"/> is set, in which case <see cref="FeaturePicker"/> composes a
/// variable-width <see cref="PredictionFeatureMode.ComposedFeatures"/> spec; regression and
/// ensembles are out of scope (later Group). Implements
/// <see cref="IDisposable"/> so <see cref="DialogService"/> can cancel an in-flight run when the
/// window is closed mid-training instead of leaving the trainer process running unattended.
/// </summary>
public partial class TrainingWizardViewModel : ViewModelBase, INotifyDataErrorInfo, IDisposable
{
    /// <summary>
    /// Rolling cap for <see cref="LogLines"/>. A multi-fold walk-forward run can stream many
    /// <c>METRIC:</c> lines; without a cap the bound <c>ListBox</c> grows unbounded for the
    /// life of the window (see <see cref="AppendLogLine"/>).
    /// </summary>
    private const int MaxLogLines = 2000;

    /// <summary>Architectures offered per framework, mirroring each trainer's own <c>ARCHES</c> tuple (train_pytorch.py / train_tensorflow.py) or the single GBDT path (train_lightgbm.py has no --arch).</summary>
    private static readonly IReadOnlyDictionary<TrainingFramework, string[]> ArchitecturesByFramework =
        new Dictionary<TrainingFramework, string[]>
        {
            [TrainingFramework.PyTorch] = new[] { "lstm", "cnn" },
            [TrainingFramework.LightGBM] = new[] { TrainingEnsembleContract.LightGbmArchitecture },
            [TrainingFramework.TensorFlow] = new[] { "lstm", "gru", "cnn" },
        };

    private readonly ITrainingOrchestrator? _orchestrator;
    private readonly IModelDeploymentService? _modelDeploymentService;
    private readonly IEnsembleSettingsManager? _ensembleSettings;
    private readonly IExperimentLogService? _experimentLogService;
    private readonly IWatchlistManager? _watchlistManager;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly ITickerStateStore? _tickerStore;
    private string[]? _resolvedSymbols;
    private readonly ILogger<TrainingWizardViewModel> _logger;

    private IReadOnlyList<string> _allTickers = Array.Empty<string>();
    private CancellationTokenSource? _runCts;
    private long _progressGeneration;
    private bool _runTerminal;

    /// <summary>The ticker tab's hierarchy as a checkbox tree; the selected rows decide which symbols the run trains on.</summary>
    public ObservableCollection<TrainingSourceTreeItem> SourceTree { get; } = new();
    public ObservableCollection<TrainingHyperparameterEntry> Hyperparameters { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public bool HasSourceTree => SourceTree.Count > 0;
    public bool HasLogLines => LogLines.Count > 0;
    public bool HasRunActivity => IsTraining || LastResult is not null || LogLines.Count > 0 || StatusMessage is not null;
    public bool IsRetrainingSaveAvailable => _retrainingScheduler is not null;
    public bool HasFrameworkChoice => SelectableFrameworks.Count > 1;
    public bool HasArchitectureChoice => AvailableArchitectures.Count > 1;
    public bool HasInitializationChoice => AvailableInitializationModes.Count > 1;
    public bool IsManifestRelevant => SelectedTimeframe is TrainingTimeframe.Weekly or TrainingTimeframe.Monthly;
    public bool CanEditClipSigma => UseFixedZScore && UseClipSigma;

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
    /// True when <see cref="FoldResults"/> holds regression rows (see <see cref="FoldMetricRow.IsRegression"/>);
    /// drives which fold-results column set the view renders. A run's rows are homogeneous (one TargetType per
    /// run), so the first row's flag speaks for the whole table.
    /// </summary>
    public bool FoldResultsAreRegression => FoldResults.Count > 0 && FoldResults[0].IsRegression;

    /// <summary>
    /// Informational note the view surfaces next to Window/Horizon (Domain Invariant: bar
    /// counts, not calendar time). While both values are valid (&gt; 0) this also names the
    /// minimum raw bar count the selected scope's shortest history must contain for training to
    /// succeed at all (see <see cref="WalkForwardDataRequirement"/>).
    /// </summary>
    public string WindowUnitNote
    {
        get
        {
            if (WindowSize <= 0 || Horizon <= 0)
            {
                return string.Empty;
            }

            var minBarsTemplate = LocalizationManager.Instance["TrainingWizard_WindowUnitNote_MinBars"]
                ?? "Minimum bars required: {0}";
            var splitCount = NSplits >= 2 ? NSplits : WalkForwardDataRequirement.DefaultSplitCount;
            // A negative Gap (transient invalid input; Start stays disabled) falls back to the
            // default purge gap rather than throwing out of this getter, matching splitCount above.
            try
            {
                var minBars = WalkForwardDataRequirement.MinimumRawBars(
                    WindowSize, Horizon, splitCount, Gap is >= 0 ? Gap : null,
                    maxLag: UseFixedZScore && TryParseLags(out var lags) ? lags.DefaultIfEmpty().Max() : 0);
                return string.Format(minBarsTemplate, minBars);
            }
            catch (OverflowException)
            {
                return LocalizationManager.Instance["TrainingWizard_WindowUnitNote_OverLimit"]
                    ?? "Required history exceeds the supported bar count. Reduce the window, horizon, gap or splits.";
            }
        }
    }

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
    [NotifyPropertyChangedFor(nameof(IsManifestMissing))]
    [NotifyPropertyChangedFor(nameof(IsManifestRelevant))]
    private TrainingTimeframe _selectedTimeframe = TrainingTimeframe.Daily;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(IsManifestMissing))]
    private string? _sourceManifestPath;

    public bool IsManifestMissing => SelectedTimeframe is TrainingTimeframe.Weekly or TrainingTimeframe.Monthly
        && UseComposedFeatures
        && FeaturePicker.Channels.Any(c => c.Kind == FeatureChannelKind.Indicator)
        && string.IsNullOrWhiteSpace(SourceManifestPath);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TrainingFramework _selectedFramework = TrainingFramework.PyTorch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _selectedArchitecture = ArchitecturesByFramework[TrainingFramework.PyTorch][0];

    public IReadOnlyList<string> AvailableArchitectures => ArchitecturesByFramework[SelectedFramework];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TargetType _selectedTargetType = TargetType.Classification;

    /// <summary>All learning objectives the wizard's Target Type combo box can offer.</summary>
    public static IReadOnlyList<TargetType> AvailableTargetTypes { get; } = Enum.GetValues<TargetType>();

    /// <summary>All bar-aggregation levels the wizard's Timeframe combo box can offer.</summary>
    public static IReadOnlyList<TrainingTimeframe> AvailableTimeframes { get; } = Enum.GetValues<TrainingTimeframe>();

    /// <summary>All trainers the wizard's Framework combo box can offer.</summary>
    public static IReadOnlyList<TrainingFramework> AvailableFrameworks { get; } = Enum.GetValues<TrainingFramework>();

    public IReadOnlyList<TrainingFramework> SelectableFrameworks => SelectedTargetType == TargetType.Regression
        ? new[] { TrainingFramework.PyTorch } : AvailableFrameworks;

    public IReadOnlyList<TrainingInitializationMode> AvailableInitializationModes => SelectedFramework switch
    {
        TrainingFramework.PyTorch => Enum.GetValues<TrainingInitializationMode>(),
        TrainingFramework.LightGBM => new[] { TrainingInitializationMode.Fresh, TrainingInitializationMode.FineTune },
        _ => new[] { TrainingInitializationMode.Fresh },
    };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferMode))]
    [NotifyPropertyChangedFor(nameof(IsFineTuneMode))]
    [NotifyPropertyChangedFor(nameof(CanFreezeModules))]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private TrainingInitializationMode _selectedInitializationMode;
    public bool IsTransferMode => SelectedInitializationMode != TrainingInitializationMode.Fresh;
    public bool IsFineTuneMode => SelectedInitializationMode == TrainingInitializationMode.FineTune;
    public bool CanFreezeModules => IsFineTuneMode && SelectedFramework == TrainingFramework.PyTorch;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _checkpointPath = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _parentModelId = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _freezePathsText = string.Empty;
    [ObservableProperty]
    private string? _checkpointManifestPath;

    private string[] SelectedFreezePaths() => CanFreezeModules
        ? FreezePathsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();

    private bool TransferControlsValid() => AvailableInitializationModes.Contains(SelectedInitializationMode)
        && (!IsTransferMode || !string.IsNullOrWhiteSpace(CheckpointPath))
        && (!IsFineTuneMode || TrainingCheckpointContract.IsHash(ParentModelId.Trim()))
        && SelectedFreezePaths().All(TrainingCheckpointContract.IsModulePath)
        && SelectedFreezePaths().Distinct(StringComparer.Ordinal).Count() == SelectedFreezePaths().Length;

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
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    [NotifyPropertyChangedFor(nameof(CanEditClipSigma))]
    private bool _useFixedZScore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(CalibrationValidationMessage))]
    private bool _useCalibration;

    /// <summary>Calibration is defined for classification models whose source is not manifest-prepared.</summary>
    private bool CalibrationUnavailable() => UseCalibration
        && (SelectedTargetType != TargetType.Classification || CreateCurrentTrainingConfig()?.RequiresFinalizedSource == true);

    public string? CalibrationValidationMessage => CalibrationUnavailable()
        ? LocalizationManager.Instance["TrainingWizard_Validation_Calibration"] : null;

    /// <summary>The ensemble methods the Extended-learning combo box offers (T13).</summary>
    public static IReadOnlyList<TrainingEnsembleMethod> AvailableEnsembleMethods { get; } = Enum.GetValues<TrainingEnsembleMethod>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBaggingSelected))]
    [NotifyPropertyChangedFor(nameof(IsStackingSelected))]
    private TrainingEnsembleMethod _selectedEnsembleMethod = TrainingEnsembleMethod.None;

    public bool IsBaggingSelected => SelectedEnsembleMethod == TrainingEnsembleMethod.Bagging;
    public bool IsStackingSelected => SelectedEnsembleMethod == TrainingEnsembleMethod.Stacking;

    [ObservableProperty]
    private int _ensembleMembers = TrainingEnsembleContract.DefaultBaggingMembers;

    /// <summary>Stacking bases, comma separated <c>framework[:architecture]</c> entries.</summary>
    [ObservableProperty]
    private string _stackingBasesText = string.Empty;

    /// <summary>The weight sources the stacking panel offers (D2).</summary>
    public static IReadOnlyList<StackingWeightSource> AvailableStackingWeightSources { get; } = Enum.GetValues<StackingWeightSource>();

    [ObservableProperty]
    private StackingWeightSource _selectedStackingWeightSource = StackingWeightSource.PastOutOfFold;

    /// <summary>The source of the run whose ensemble may be registered next; the weight-transfer note applies to past out-of-fold weights only.</summary>
    private StackingWeightSource _runStackingWeightSource = StackingWeightSource.PastOutOfFold;

    [ObservableProperty]
    private bool _usePretraining;

    /// <summary>Evaluation-only CPCV (T14): C(groups, test groups) extra fits whose scores are reported, nothing is published.</summary>
    [ObservableProperty]
    private bool _useCpcv;

    [ObservableProperty]
    private int _cpcvGroups = TrainingCpcvContract.DefaultGroups;

    [ObservableProperty]
    private int _cpcvTestGroups = TrainingCpcvContract.DefaultTestGroups;

    /// <summary>The combination count of the current CPCV request, or <see langword="null"/> while it is malformed or over the cap.</summary>
    public int? CpcvCombinationCount => TrainingCpcvContract.TryCountCombinations(CpcvGroups, CpcvTestGroups, out var count, out _)
        ? count : null;

    public string? CpcvCountText => CpcvCombinationCount is { } count
        ? string.Format(LocalizationManager.Instance["TrainingWizard_CpcvCount"], count) : null;

    /// <summary>Per-combination CPCV scores, appended live from the run's <c>cpcv_*</c> metric lines.</summary>
    public ObservableCollection<CpcvMetricRow> CpcvResults { get; } = new();

    public bool HasCpcvResults => CpcvResults.Count > 0;

    private int _runCpcvGroups;
    private int _runCpcvTestGroups;

    private string[] StackingBases() => StackingBasesText
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The first reason the extended-learning choices (ensemble / pretraining) cannot run, or <see langword="null"/>. These rules mirror
    /// <see cref="TrainingJobConfig.Validate"/> so Start is disabled with a specific message instead of failing at launch.
    /// </summary>
    public string? ExtendedLearningValidationMessage
    {
        get
        {
            string Text(string key) => LocalizationManager.Instance[key];
            if (SelectedEnsembleMethod != TrainingEnsembleMethod.None)
            {
                if (SelectedTargetType != TargetType.Classification) return Text("TrainingWizard_Validation_EnsembleTarget");
                if (UseCalibration || UsePretraining || SelectedInitializationMode != TrainingInitializationMode.Fresh)
                    return Text("TrainingWizard_Validation_EnsembleExclusive");
                try
                {
                    TrainingEnsembleContract.Validate(SelectedEnsembleMethod, EnsembleMembers, StackingBases(), SelectedTargetType);
                }
                catch (InvalidOperationException)
                {
                    return string.Format(Text(IsBaggingSelected ? "TrainingWizard_Validation_EnsembleMembers"
                            : "TrainingWizard_Validation_EnsembleBases"),
                        TrainingEnsembleContract.MinimumMembers, TrainingResourceOverrides.MaximumEnsembleMembers);
                }
            }

            if (UseCpcv)
            {
                if (SelectedEnsembleMethod != TrainingEnsembleMethod.None || SelectedInitializationMode != TrainingInitializationMode.Fresh)
                    return Text("TrainingWizard_Validation_CpcvExclusive");
                if (CpcvCombinationCount is null)
                    return string.Format(Text("TrainingWizard_Validation_CpcvGroups"),
                        TrainingCpcvContract.MaxGroups, TrainingCpcvContract.MaxCombinations);
            }

            return UsePretraining && (SelectedFramework != TrainingFramework.PyTorch
                    || SelectedInitializationMode == TrainingInitializationMode.FineTune)
                ? Text("TrainingWizard_Validation_Pretraining") : null;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedTargetType) or nameof(SelectedFramework) or nameof(SelectedInitializationMode)
            or nameof(UseCalibration) or nameof(SelectedEnsembleMethod) or nameof(EnsembleMembers)
            or nameof(StackingBasesText) or nameof(UsePretraining) or nameof(UseCpcv) or nameof(CpcvGroups)
            or nameof(CpcvTestGroups))
        {
            OnPropertyChanged(nameof(ExtendedLearningValidationMessage));
            OnPropertyChanged(nameof(CpcvCombinationCount));
            OnPropertyChanged(nameof(CpcvCountText));
            StartTrainingCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditClipSigma))]
    private bool _useClipSigma;

    partial void OnUseClipSigmaChanged(bool value) => RefreshClipSigmaValidation();

    partial void OnUseFixedZScoreChanged(bool value)
    {
        if (!value)
        {
            UseClipSigma = false;
            LagsText = string.Empty;
        }
        RefreshClipSigmaValidation();
    }

    private const decimal DefaultClipSigma = 3.0m;

    /// <summary>Decimal compatibility adapter; zero means the text is invalid or outside decimal precision.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private decimal _clipSigma = DefaultClipSigma;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    private string _clipSigmaText = DefaultClipSigma.ToString(CultureInfo.CurrentCulture);

    partial void OnClipSigmaChanged(decimal value) =>
        ClipSigmaText = value.ToString(CultureInfo.CurrentCulture);

    partial void OnClipSigmaTextChanged(string value)
    {
        if ((decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            || decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            && parsed > 0)
            SetProperty(ref _clipSigma, parsed, nameof(ClipSigma));
        else
            SetProperty(ref _clipSigma, 0m, nameof(ClipSigma));
        RefreshClipSigmaValidation();
    }

    public string? ClipSigmaValidationMessage => !UseClipSigma ? null
        : !UseFixedZScore
            ? LocalizationManager.Instance["TrainingWizard_Validation_ClipRequiresFixed"]
            : TryGetClipSigma(out _) ? null
                : LocalizationManager.Instance["TrainingWizard_Validation_ClipValue"];

    public bool HasErrors => ClipSigmaValidationMessage is not null;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        (string.IsNullOrEmpty(propertyName) || propertyName == nameof(ClipSigmaText))
            && ClipSigmaValidationMessage is { } message ? new[] { message } : Array.Empty<string>();

    private void RefreshClipSigmaValidation()
    {
        OnPropertyChanged(nameof(ClipSigmaValidationMessage));
        OnPropertyChanged(nameof(HasErrors));
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(ClipSigmaText)));
    }

    private bool TryGetClipSigma(out double value)
    {
        if (!double.TryParse(ClipSigmaText, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && !double.TryParse(ClipSigmaText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        return double.IsFinite(value) && value > 0;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private string _lagsText = string.Empty;

    private bool TryParseLags(out int[] lags)
    {
        lags = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(LagsText)) return true;
        var parts = LagsText.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Any(part => !int.TryParse(part, out _))) return false;
        lags = parts.Select(int.Parse).ToArray();
        return TrainingResourceOverrides.ValidLags(lags, out _);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAsEnsembleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRetrainingConfigurationCommand))]
    [NotifyPropertyChangedFor(nameof(HasRunActivity))]
    private bool _isTraining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAsEnsembleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRetrainingConfigurationCommand))]
    private bool _isDeploying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStageDisplay))]
    private string? _currentStage;

    public string? CurrentStageDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentStage)) return CurrentStage;
            var key = $"TrainingWizard_Stage_{CurrentStage}";
            var localized = LocalizationManager.Instance[key];
            return localized == $"[{key}]" ? CurrentStage : localized;
        }
    }

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunActivity))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployToModelsCommand))]
    [NotifyPropertyChangedFor(nameof(HasRunActivity))]
    private TrainingRunResult? _lastResult;

    /// <summary>The generations registered from the last ensemble run, awaiting an explicit "use as ensemble".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseAsEnsembleCommand))]
    [NotifyPropertyChangedFor(nameof(HasRegisteredEnsemble))]
    private EnsembleRegistration? _registeredEnsemble;

    public bool HasRegisteredEnsemble => RegisteredEnsemble is not null;

    partial void OnLastResultChanged(TrainingRunResult? value) => RegisteredEnsemble = null;

    /// <summary>
    /// Feature-composition picker for a <see cref="PredictionFeatureMode.ComposedFeatures"/> run.
    /// Only consulted when <see cref="UseComposedFeatures"/> is set; otherwise the run stays on the
    /// fixed <see cref="PredictionFeatureMode.OhlcvMinMax"/> path.
    /// </summary>
    public FeatureChannelPickerViewModel FeaturePicker { get; }

    /// <summary>
    /// Whether the run trains a composed-features model built from <see cref="FeaturePicker"/>
    /// instead of the fixed OHLCV min-max model. There is no separate on/off toggle in the UI (the
    /// [特徴量] tab always shows the composer): this is simply true whenever at least one channel
    /// has been selected, so an empty selection transparently falls back to the fixed model.
    /// </summary>
    public bool UseComposedFeatures => FeaturePicker.HasChannels;

    public TrainingWizardViewModel(
        ITrainingOrchestrator? orchestrator = null,
        IModelDeploymentService? modelDeploymentService = null,
        IExperimentLogService? experimentLogService = null,
        IWatchlistManager? watchlistManager = null,
        IMarketDataProvider? marketDataProvider = null,
        ILogger<TrainingWizardViewModel>? logger = null,
        IIndicatorFactory? indicatorFactory = null,
        ITemplateService? templateService = null,
        Services.IToastNotificationService? toastService = null, IRetrainingScheduler? retrainingScheduler = null,
        IEnsembleSettingsManager? ensembleSettings = null,
        ITickerStateStore? tickerStore = null)
    {
        _tickerStore = tickerStore;
        _orchestrator = orchestrator;
        _ensembleSettings = ensembleSettings;
        _retrainingScheduler = retrainingScheduler;
        _modelDeploymentService = modelDeploymentService;
        _experimentLogService = experimentLogService;
        _watchlistManager = watchlistManager;
        _marketDataProvider = marketDataProvider;
        _logger = logger ?? NullLogger<TrainingWizardViewModel>.Instance;
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;

        FeaturePicker = new FeatureChannelPickerViewModel(indicatorFactory ?? IndicatorFactory.Default, templateService, toastService)
        {
            ShowColorSettings = false,
            ShowSetAsDefault = false,
        };
        FeaturePicker.Channels.CollectionChanged += (_, _) =>
        {
            StartTrainingCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UseComposedFeatures));
            OnPropertyChanged(nameof(IsManifestMissing));
        };

        CpcvResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCpcvResults));

        FoldResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFoldResults));
            OnPropertyChanged(nameof(FoldResultsAreRegression));
        };

        LogLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLogLines));
            OnPropertyChanged(nameof(HasRunActivity));
        };

        if (_watchlistManager is not null)
        {
            _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;
        }

        _ = InitializeAsync();
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentStageDisplay));
        RefreshClipSigmaValidation();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_marketDataProvider is not null)
            {
                _allTickers = await _marketDataProvider.GetAvailableTickersAsync(); _resolvedSymbols = null;
            }

            RefreshSourceTree();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingWizardViewModel: failed to load ticker/profile lists.");
            StatusMessage = LocalizationManager.Instance["TrainingWizard_LoadScopeError"]
                ?? "Could not load the ticker/watchlist/portfolio lists.";
        }
    }

    /// <summary>
    /// (Re)builds <see cref="SourceTree"/> from the ticker tab's hierarchy. The first build ticks "All tickers" (the population a run used before the list
    /// choice existed); later builds keep the rows that were ticked, matched by node id.
    /// </summary>
    /// <remarks>
    /// Called once from <see cref="InitializeAsync"/> and again on every <see cref="IWatchlistManager.WatchlistsChanged"/> (subscribed in the constructor),
    /// so a wizard opened before the watchlists finished loading, or before the user created one, does not keep an empty list for its lifetime.
    /// </remarks>
    private void RefreshSourceTree()
    {
        if (_tickerStore is null)
        {
            return;
        }

        bool first = !_sourceTreeBuilt;
        var kept = new HashSet<Guid>();
        foreach (var item in Flatten(SourceTree))
        {
            item.PropertyChanged -= OnSourceSelectionChanged;
            if (item.IsSelected) kept.Add(item.Node.Id);
        }

        SourceTree.Clear();
        var visited = new HashSet<TickerGroupNode>(ReferenceEqualityComparer.Instance);
        foreach (var group in _tickerStore.Groups)
        {
            AddSourceNode(group, SourceTree, visited, kept, first);
        }

        _sourceTreeBuilt = true;
        _resolvedSymbols = null;
        OnPropertyChanged(nameof(HasSourceTree));
        StartTrainingCommand.NotifyCanExecuteChanged();
    }

    private bool _sourceTreeBuilt;

    private void AddSourceNode(TickerGroupNode node, ObservableCollection<TrainingSourceTreeItem> into, HashSet<TickerGroupNode> visited, HashSet<Guid> kept, bool first)
    {
        if (node is ActionNode || !visited.Add(node))
        {
            return;
        }

        bool selectable = IsTrainableSource(node);
        var item = new TrainingSourceTreeItem(node, IconFor(node), selectable)
        {
            IsSelected = selectable && (first ? node is AllTickersNode : kept.Contains(node.Id)),
        };
        item.PropertyChanged += OnSourceSelectionChanged;
        into.Add(item);
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                AddSourceNode(child, item.Children, visited, kept, first);
            }
        }
    }

    /// <summary>All tickers, the two fixed Watchlists/Portfolios categories, watchlists, portfolios, saved filters, custom categories and their lists.</summary>
    private static bool IsTrainableSource(TickerGroupNode node) => node switch
    {
        AllTickersNode or WatchlistNode or PortfolioNode or FilterNode or CustomCategoryNode or TickerListFolderNode => true,
        CategoryNode { Id: var id } => id == TickerListViewModel.WatchlistsCategoryId || id == TickerListViewModel.PortfoliosCategoryId,
        _ => false,
    };

    private static string? IconFor(TickerGroupNode node) => node switch
    {
        WatchlistNode => TickerGroupIconGeometries.Watchlist,
        PortfolioNode => TickerGroupIconGeometries.Portfolio,
        FilterNode => TickerGroupIconGeometries.Filter,
        _ => null,
    };

    private static IEnumerable<TrainingSourceTreeItem> Flatten(IEnumerable<TrainingSourceTreeItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e)
    {
        // The ticker store updates its nodes in its own handler of the same event, so rebuild after the current handlers have run.
        var context = SynchronizationContext.Current;
        if (context is null) RefreshSourceTree();
        else context.Post(_ => RefreshSourceTree(), null);
    }

    private void OnSourceSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrainingSourceTreeItem.IsSelected))
        {
            _resolvedSymbols = null;
            StartTrainingCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedFrameworkChanged(TrainingFramework value)
    {
        OnPropertyChanged(nameof(AvailableArchitectures));
        OnPropertyChanged(nameof(AvailableInitializationModes));
        OnPropertyChanged(nameof(HasArchitectureChoice));
        OnPropertyChanged(nameof(HasInitializationChoice));
        OnPropertyChanged(nameof(CanFreezeModules));
        if (!AvailableInitializationModes.Contains(SelectedInitializationMode))
            SelectedInitializationMode = TrainingInitializationMode.Fresh;
        SelectedArchitecture = ArchitecturesByFramework[value][0];
    }

    partial void OnSelectedTargetTypeChanged(TargetType value)
    {
        OnPropertyChanged(nameof(SelectableFrameworks));
        OnPropertyChanged(nameof(HasFrameworkChoice));
        OnPropertyChanged(nameof(CalibrationValidationMessage));
        StartTrainingCommand.NotifyCanExecuteChanged();
        if (value == TargetType.Regression && SelectedFramework != TrainingFramework.PyTorch)
            SelectedFramework = TrainingFramework.PyTorch;
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

    /// <summary>
    /// The symbols of the ticked rows as one de-duplicated list. "All tickers" is the market data provider's list; every other row is what the ticker tab
    /// shows for that node. Without a ticker tree (no store) the only population is all tickers. Cached until the selection or the tree changes
    /// (resolving a saved filter is not free and the Start command asks on every change); <paramref name="refresh"/> resolves again, for the run itself.
    /// </summary>
    private string[] ResolveSymbols(bool refresh = false)
    {
        if (!refresh && _resolvedSymbols is not null)
        {
            return _resolvedSymbols;
        }

        IEnumerable<string> symbols;
        if (_tickerStore is null)
        {
            symbols = _allTickers;
        }
        else
        {
            symbols = Flatten(SourceTree).Where(i => i.IsSelectable && i.IsSelected)
                .SelectMany(i => i.Node is AllTickersNode ? _allTickers : _tickerStore.GetTickersForNode(i.Node));
        }

        return _resolvedSymbols = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool CanStartTraining() =>
        !IsTraining && !IsDeploying
        && TransferControlsValid()
        && _orchestrator is not null
        && (SelectedTargetType != TargetType.Regression || SelectedFramework == TrainingFramework.PyTorch)
        && WindowSize > 0 && Horizon > 0
        && NSplits is >= 2 and <= TrainingResourceOverrides.MaximumEvaluationFolds
        && Gap is not (< 0)
        && OosTailDays is not (< 0)
        && !CalibrationUnavailable()
        && ExtendedLearningValidationMessage is null
        && TryParseLags(out var lags)
        && (!UseClipSigma || (UseFixedZScore && TryGetClipSigma(out _)))
        && (UseFixedZScore || lags.Length == 0)
        && (!UseFixedZScore || !UseComposedFeatures ||
            FeaturePicker.BuildFeatureSpec()?.Channels.All(c => c.Normalization == ChannelNormalization.None) == true)
        && !string.IsNullOrWhiteSpace(SelectedArchitecture)
        && (!UseComposedFeatures || FeaturePicker.BuildFeatureSpec() is not null)
        && !IsManifestMissing
        && ResolveSymbols().Length > 0;

    private TrainingJobConfig? CreateCurrentTrainingConfig()
    {
        if (!TryParseLags(out var lags)) return null;
        double clipSigma = 0;
        if (UseClipSigma && !TryGetClipSigma(out clipSigma)) return null;
        var selectedSpec = UseComposedFeatures ? FeaturePicker.BuildFeatureSpec() : null;
        var composedSpec = selectedSpec is null ? null : selectedSpec with { Lags = lags };

        return new TrainingJobConfig
        {
            Symbols = ResolveSymbols(refresh: true),
            StartDate = StartDate is { } sd ? DateOnly.FromDateTime(sd.Date) : null,
            EndDate = EndDate is { } ed ? DateOnly.FromDateTime(ed.Date) : null,
            Timeframe = SelectedTimeframe,
            Framework = SelectedFramework,
            InitializationMode = SelectedInitializationMode,
            CheckpointPath = IsTransferMode ? CheckpointPath.Trim() : null,
            ParentModelId = IsFineTuneMode ? ParentModelId.Trim() : null,
            FreezePaths = SelectedFreezePaths(),
            Architecture = SelectedArchitecture,
            FeatureMode = composedSpec is not null ? PredictionFeatureMode.ComposedFeatures : PredictionFeatureMode.OhlcvMinMax,
            FeatureSpec = composedSpec,
            FixedZScore = UseFixedZScore,
            Calibration = UseCalibration,
            StackingWeightSource = IsStackingSelected ? SelectedStackingWeightSource : StackingWeightSource.PastOutOfFold,
            EnsembleMethod = SelectedEnsembleMethod,
            EnsembleMembers = EnsembleMembers,
            StackingBases = IsStackingSelected ? StackingBases() : Array.Empty<string>(),
            Pretraining = UsePretraining ? TrainingPretraining.MaskedReconstruction : TrainingPretraining.None,
            Cpcv = UseCpcv,
            CpcvGroups = CpcvGroups,
            CpcvTestGroups = CpcvTestGroups,
            ClipSigma = UseClipSigma ? clipSigma : null,
            Lags = composedSpec is null ? lags : Array.Empty<int>(),
            SourceManifestPath = SourceManifestPath,
            WindowSize = WindowSize,
            Horizon = Horizon,
            TargetType = SelectedTargetType,
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
    }

    [RelayCommand(CanExecute = nameof(CanStartTraining))]
    private async Task StartTrainingAsync()
    {
        if (_orchestrator is null)
        {
            return;
        }

        var config = CreateCurrentTrainingConfig();
        if (config is null) return;

        try
        {
            config.Validate();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "TrainingWizardViewModel: training configuration was rejected.");
            StatusMessage = LocalizationManager.Instance[TrainingValidationMessageKey(ex.Message)]
                ?? "Check the training settings and try again.";
            return;
        }

        IsTraining = true;
        _runTerminal = false;
        ProgressPercent = 0;
        CurrentStage = null;
        LogLines.Clear();
        FoldResults.Clear();
        CpcvResults.Clear();
        _runStackingWeightSource = config.StackingWeightSource;
        _runCpcvGroups = config.Cpcv ? config.CpcvGroups : 0;
        _runCpcvTestGroups = config.Cpcv ? config.CpcvTestGroups : 0;
        LastResult = null;
        CheckpointManifestPath = null;
        StatusMessage = LocalizationManager.Instance["TrainingWizard_Status_Starting"] ?? "Starting training...";

        _runCts = new CancellationTokenSource();
        var generation = ++_progressGeneration;
        var progress = new Progress<TrainingProgress>(update =>
        {
            if (generation == _progressGeneration && IsTraining)
                OnTrainingProgress(update);
        });

        try
        {
            var result = await _orchestrator.StartTrainingAsync(config, progress, _runCts.Token).ConfigureAwait(true);
            _progressGeneration++;
            _runTerminal = true;
            CancelTrainingCommand.NotifyCanExecuteChanged();
            LastResult = result;
            CheckpointManifestPath = result.CheckpointManifestPath ?? CheckpointManifestPath;
            if (config.RequiresFinalizedSource)
                CurrentStage = result.Success ? "Completed" : "Failed";
            StatusMessage = result.Success
                ? (LocalizationManager.Instance["TrainingWizard_Status_Completed"] ?? "Training completed.")
                : (result.Message ?? LocalizationManager.Instance["TrainingWizard_Status_Failed"] ?? "Training failed.");

            if (_experimentLogService is not null)
            {
                try
                {
                    // TrainingOrchestrator.StartTrainingAsync assigns RunId on its own local
                    // config parameter (config = config with { RunId = runId }), which does not
                    // propagate back to this method's config -- so record the same RunId the
                    // orchestrator actually used (result.RunId is authoritative) rather than
                    // persisting config.json without it while metrics.json has it.
                    await _experimentLogService.RecordAsync(config with
                    {
                        RunId = result.RunId,
                        SourceProvenance = result.SourceProvenance,
                    }, result).ConfigureAwait(true);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "TrainingWizardViewModel: failed to record experiment log for run {RunId}.", result.RunId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _progressGeneration++;
            _runTerminal = true;
            CancelTrainingCommand.NotifyCanExecuteChanged();
            if (config.RequiresFinalizedSource)
                CurrentStage = "Cancelled";
            StatusMessage = LocalizationManager.Instance["TrainingWizard_Status_Cancelled"] ?? "Training cancelled.";
        }
        catch (Exception ex)
        {
            _progressGeneration++;
            _runTerminal = true;
            CancelTrainingCommand.NotifyCanExecuteChanged();
            if (config.RequiresFinalizedSource)
                CurrentStage = "Failed";
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

    private static string TrainingValidationMessageKey(string message)
    {
        const string prefix = "TrainingJobConfig: ";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return "TrainingWizard_Validation_General";
        var reason = message[prefix.Length..];
        if (reason.Contains("checkpoint", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("freeze", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("frozen", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("parent ModelId", StringComparison.Ordinal))
            return "TrainingWizard_Validation_Checkpoint";
        if (reason.StartsWith("StartDate ", StringComparison.Ordinal))
            return "TrainingWizard_Validation_DateRange";
        if (reason.StartsWith("FeatureMode ", StringComparison.Ordinal)
            || reason.StartsWith("FeatureSpec ", StringComparison.Ordinal))
            return "TrainingWizard_Validation_Features";
        if (reason.Contains("lag", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("ClipSigma", StringComparison.Ordinal)
            || reason.Contains("clipping", StringComparison.Ordinal)
            || reason.Contains("z-score", StringComparison.Ordinal))
            return "TrainingWizard_Validation_Preprocessing";
        if (reason.Contains("resource bounds", StringComparison.Ordinal))
            return "TrainingWizard_Validation_Resources";
        if (reason.Contains("source manifest", StringComparison.Ordinal))
            return "TrainingWizard_Validation_Source";
        return "TrainingWizard_Validation_General";
    }

    private void OnTrainingProgress(TrainingProgress update)
    {
        if (update.CheckpointManifestPath is { } checkpoint)
            CheckpointManifestPath = checkpoint;
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        if (update.Stage is { Length: > 0 } stage)
        {
            CurrentStage = stage;
            AppendLogLine($"[{timestamp}] {CurrentStageDisplay} ({update.Percent}%)");
        }

        ProgressPercent = update.Percent;

        if (update.Metric is { Count: > 0 } metric)
        {
            var summary = string.Join(", ", metric.Select(kv => $"{kv.Key}={kv.Value:0.####}"));
            AppendLogLine($"[{timestamp}] {summary}");

            if (_runCpcvGroups > 0 && CpcvMetricRow.FromMetric(metric, _runCpcvGroups, _runCpcvTestGroups) is { } cpcvRow)
            {
                var existing = CpcvResults.ToList().FindIndex(r => r.Combination == cpcvRow.Combination);
                if (existing >= 0) CpcvResults[existing] = cpcvRow;
                else CpcvResults.Add(cpcvRow);
            }

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

    /// <summary>
    /// Appends a line to <see cref="LogLines"/>, trimming the oldest entries once
    /// <see cref="MaxLogLines"/> is exceeded. Full run history is retained separately via
    /// <see cref="IExperimentLogService"/>; this cap only bounds the live UI list.
    /// </summary>
    private void AppendLogLine(string line)
    {
        while (LogLines.Count >= MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }

        LogLines.Add(line);
    }

    private bool CanCancelTraining() => IsTraining && !_runTerminal;

    [RelayCommand(CanExecute = nameof(CanCancelTraining))]
    private void CancelTraining()
    {
        if (_runCts is null || _runTerminal) return;
        if (SelectedTimeframe is TrainingTimeframe.Weekly or TrainingTimeframe.Monthly
            && FeaturePicker.Channels.Any(c => c.Kind == FeatureChannelKind.Indicator))
            CurrentStage = "Cancelling";
        _runCts.Cancel();
    }

    private bool CanDeployToModels() => !IsTraining && !IsDeploying && _modelDeploymentService is not null
        && LastResult is { Success: true, OnnxArtifactPath: not null, MetricsArtifactPath: not null };

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
            if (result.EnsembleManifestPath is { } ensemblePath)
            {
                // Every member becomes its own generation; none is activated and the active ensemble is untouched
                // until the user chooses "Use as ensemble".
                var registration = await _modelDeploymentService.RegisterEnsembleAsync(ensemblePath).ConfigureAwait(true);
                RegisteredEnsemble = registration;
                StatusMessage = string.Format(
                    LocalizationManager.Instance["TrainingWizard_Status_EnsembleRegistered"]
                    ?? "Registered {0} ensemble member generations (none activated).",
                    registration.Generations.Count);
                if (registration.Method == TrainingEnsembleMethod.Stacking
                    && _runStackingWeightSource == StackingWeightSource.PastOutOfFold)
                {
                    // Stacking weights come from earlier folds' out-of-fold rows but apply to the release-fold models.
                    StatusMessage += Environment.NewLine
                        + (LocalizationManager.Instance["TrainingWizard_Status_EnsembleWeightTransfer"]
                           ?? "Note: the stacking weights were fitted on earlier folds.");
                }

                return;
            }

            var generation = await _modelDeploymentService.RegisterGenerationAsync(onnxPath, result.MetricsArtifactPath).ConfigureAwait(true);
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_Registered"] ?? "Registered model generation {0}.",
                generation.ModelId);
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

    private bool CanUseAsEnsemble() => !IsTraining && !IsDeploying && _ensembleSettings is not null && RegisteredEnsemble is not null;

    /// <summary>Makes the registered ensemble (equal bagging weights or the fitted stacking weights) the active ensemble.</summary>
    [RelayCommand(CanExecute = nameof(CanUseAsEnsemble))]
    private async Task UseAsEnsembleAsync()
    {
        if (_ensembleSettings is null || RegisteredEnsemble is not { } registration) return;
        IsDeploying = true;
        try
        {
            await _ensembleSettings.SaveAsync(registration.Spec).ConfigureAwait(true);
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_EnsembleApplied"] ?? "The active ensemble now has {0} members.",
                registration.Spec.Members.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingWizardViewModel: UseAsEnsembleAsync failed.");
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_EnsembleError"] ?? "The ensemble could not be applied: {0}",
                ex.Message);
        }
        finally
        {
            IsDeploying = false;
        }
    }

    /// <summary>
    /// Cancels an in-flight training run when the window is closed mid-run (see
    /// <see cref="DialogService.ShowTrainingWizardDialogAsync"/>'s <c>window.Closed</c> hook).
    /// Only requests cancellation here: <see cref="_runCts"/> itself is disposed in
    /// <see cref="StartTrainingAsync"/>'s own <c>finally</c> block once the awaited run actually
    /// unwinds, so disposing it a second time here (while that await may still be in flight)
    /// would race with it.
    /// </summary>
    /// <remarks>
    /// Defensive: on this ViewModel's single-UI-dispatcher-thread execution model, <see cref="_runCts"/>
    /// cannot normally be observed disposed-but-non-null here (nothing yields between the
    /// <c>finally</c> block's <c>Dispose()</c> and its <c>= null</c>), so <see cref="ObjectDisposedException"/>
    /// is not expected in practice. The catch costs nothing and removes any doubt if that
    /// threading assumption ever changes (multiple independent reviews of this design flagged
    /// the theoretical race; see Y:\Temp\sa_analysis_report_WebaiReviewOnnxTrainingBatch.md).
    /// Safe to call more than once (e.g. a duplicate window-close notification).
    /// </remarks>
    public void Dispose()
    {
        LocalizationManager.Instance.LocaleChanged -= OnLocaleChanged;
        if (_watchlistManager is not null)
        {
            _watchlistManager.WatchlistsChanged -= OnWatchlistsChanged;
        }

        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // _runCts was already disposed by StartTrainingAsync's finally block; nothing to cancel.
        }
    }
}
