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
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PredictionConfigurationChangedMessage = StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.StoredPeriod;

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
    [NotifyPropertyChangedFor(nameof(SelectedOption))]
    [NotifyPropertyChangedFor(nameof(KeyDescription))]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Why this row cannot be passed to the trainer (value outside its domain, or a key entered twice); null = acceptable. Set by the wizard.</summary>
    [ObservableProperty]
    private string? _validationMessage;

    /// <summary>
    /// What the key list binds to. The list clears its selection while its items are replaced (a framework change); a null it writes back must not erase
    /// the key, so the wizard raises this property again afterwards. Options are value records, so the list finds this one among its items.
    /// </summary>
    public TrainingHyperparameterOption? SelectedOption
    {
        get => TrainingHyperparameterCatalog.Find(Key) is { } definition ? TrainingHyperparameterOption.From(definition) : null;
        set
        {
            if (value is not null) Key = value.Key;
        }
    }

    internal void RefreshSelectedOption() => OnPropertyChanged(nameof(SelectedOption));

    /// <summary>The tooltip of the chosen key (null for an empty or unknown key).</summary>
    public string? KeyDescription => TrainingHyperparameterOption.DescriptionOf(Key);

    /// <summary>Choosing a key brings the trainer's default value, unless the owner already typed another value.</summary>
    partial void OnKeyChanged(string? oldValue, string newValue)
    {
        if (TrainingHyperparameterCatalog.Find(newValue) is not { } definition) return;
        var previousDefault = TrainingHyperparameterCatalog.Find(oldValue)?.DefaultValue;
        if (string.IsNullOrWhiteSpace(Value) || Value == previousDefault) Value = definition.DefaultValue;
    }
}

/// <summary>One entry of the wizard's hyperparameter key list: the key, the trainer's default and the short explanation shown as a tooltip.</summary>
public sealed record TrainingHyperparameterOption(string Key, string DefaultValue, string Description)
{
    public static string LocalizationKey(TrainingHyperparameterDefinition definition) => "TrainingWizard_Hp_" + definition.LocalizationId;

    public static TrainingHyperparameterOption From(TrainingHyperparameterDefinition definition) =>
        new(definition.Key, definition.DefaultValue, LocalizationManager.Instance[LocalizationKey(definition)]);

    internal static string? DescriptionOf(string? key) =>
        TrainingHyperparameterCatalog.Find(key) is { } definition ? LocalizationManager.Instance[LocalizationKey(definition)] : null;
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
            [TrainingFramework.PyTorch] = new[] { "lstm", "cnn", AttentionContract.Architecture },
            [TrainingFramework.LightGBM] = new[] { TrainingEnsembleContract.LightGbmArchitecture },
            [TrainingFramework.TensorFlow] = new[] { "lstm", "gru", "cnn" },
        };

    /// <summary>The architecture each framework starts with. Explicit on purpose: re-ordering <see cref="ArchitecturesByFramework"/> must not change the default.</summary>
    private static readonly IReadOnlyDictionary<TrainingFramework, string> DefaultArchitectureByFramework =
        new Dictionary<TrainingFramework, string>
        {
            [TrainingFramework.PyTorch] = "lstm",
            [TrainingFramework.LightGBM] = TrainingEnsembleContract.LightGbmArchitecture,
            [TrainingFramework.TensorFlow] = "lstm",
        };

    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private readonly ITrainingOrchestrator? _orchestrator;
    private readonly IModelDeploymentService? _modelDeploymentService;
    private readonly IModelGenerationRegistry? _modelRegistry;
    private readonly IEnsembleSettingsManager? _ensembleSettings;
    private readonly IExperimentLogService? _experimentLogService;
    private readonly IWatchlistManager? _watchlistManager;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly ITickerStateStore? _tickerStore;
    private string[]? _resolvedSymbols;
    private readonly IStockAnalyzerSettings? _settings;   // the confidence threshold the chart applies, for the held-out confidence line
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
            switch (CalculateMinimumRawBars(out var minBars))
            {
                case MinimumBarsState.Invalid:
                    return string.Empty;
                case MinimumBarsState.OverLimit:
                    return LocalizationManager.Instance["TrainingWizard_WindowUnitNote_OverLimit"]
                        ?? "Required history exceeds the supported bar count. Reduce the window, horizon, gap or splits.";
                default:
                    var minBarsTemplate = LocalizationManager.Instance["TrainingWizard_WindowUnitNote_MinBars"]
                        ?? "Minimum bars required: {0}";
                    return string.Format(minBarsTemplate, minBars);
            }
        }
    }

    private enum MinimumBarsState { Invalid, OverLimit, Ok }

    /// <summary>
    /// The minimum raw bar count of the CURRENT inputs (the one expression behind <see cref="WindowUnitNote"/> and the pre-filled start date).
    /// Invalid while the window or the horizon is not positive; OverLimit when the requirement overflows.
    /// </summary>
    private MinimumBarsState CalculateMinimumRawBars(out int minimumBars)
    {
        minimumBars = 0;
        if (WindowSize <= 0 || Horizon <= 0)
        {
            return MinimumBarsState.Invalid;
        }

        var splitCount = CurrentSplitCount();
        // A negative Gap (transient invalid input; Start stays disabled) falls back to the
        // default purge gap rather than throwing out of this getter, matching splitCount above.
        try
        {
            // The whole run, not only the last fold: every outer fold plus its inner validation split (and the calibration /
            // stacking meta segment of the release fold) must exist, otherwise the trainer stops before it starts.
            minimumBars = WalkForwardDataRequirement.MinimumRawBarsForTraining(
                WindowSize, Horizon, splitCount, Gap is >= 0 ? Gap : null,
                maxLag: CurrentMaxLag(),
                calibrationSegment: UseCalibration
                    || (IsStackingSelected && SelectedStackingWeightSource == StackingWeightSource.ReleaseMetaSegment));
            return MinimumBarsState.Ok;
        }
        catch (OverflowException)
        {
            return MinimumBarsState.OverLimit;
        }
    }

    /// <summary>The deepest explicit causal lag in bars that the current inputs use (0 without a fixed z-score or without lags); the one expression behind the minimum bar count.</summary>
    private int CurrentMaxLag() => UseFixedZScore && TryParseLags(out var lags) ? lags.DefaultIfEmpty().Max() : 0;

    /// <summary>The walk-forward split count the trainer will use: the entered count, or the default while it is not a usable count.</summary>
    private int CurrentSplitCount() => NSplits >= 2 ? NSplits : WalkForwardDataRequirement.DefaultSplitCount;

    private bool TryMinimumRawBars(out int minimumBars) => CalculateMinimumRawBars(out minimumBars) == MinimumBarsState.Ok;

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
    private string _selectedArchitecture = DefaultArchitectureByFramework[TrainingFramework.PyTorch];

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
    [NotifyPropertyChangedFor(nameof(ModeBadges))]
    [NotifyPropertyChangedFor(nameof(HasModeBadges))]
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
        && CheckpointPathValid() && ParentModelValid() && FreezePathsValid();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int _windowSize = PredictionSettingsManager.DefaultWindowSize;

    /// <summary>
    /// Forward label horizon in bars. Seeded from <see cref="WalkForwardDataRequirement.DefaultHorizon"/>
    /// (the C# SSoT mirror of the trainer's <c>dataset.DEFAULT_HORIZON</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTrainingCommand))]
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private int _horizon = WalkForwardDataRequirement.DefaultHorizon;

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
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
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
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    [NotifyPropertyChangedFor(nameof(ModeBadges))]
    [NotifyPropertyChangedFor(nameof(HasModeBadges))]
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
    [NotifyPropertyChangedFor(nameof(WindowUnitNote))]
    private StackingWeightSource _selectedStackingWeightSource = StackingWeightSource.PastOutOfFold;

    /// <summary>The source of the run whose ensemble may be registered next; the weight-transfer note applies to past out-of-fold weights only.</summary>
    private StackingWeightSource _runStackingWeightSource = StackingWeightSource.PastOutOfFold;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadges))]
    [NotifyPropertyChangedFor(nameof(HasModeBadges))]
    private bool _usePretraining;

    /// <summary>Evaluation-only CPCV (T14): C(groups, test groups) extra fits whose scores are reported, nothing is published.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadges))]
    [NotifyPropertyChangedFor(nameof(HasModeBadges))]
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

            if (UsePretraining && AttentionContract.IsAttentionArchitecture(SelectedArchitecture))
                return string.Format(Text("TrainingWizard_Validation_PretrainingAttention"), AttentionContract.Architecture);
            return UsePretraining && (SelectedFramework != TrainingFramework.PyTorch
                    || SelectedInitializationMode == TrainingInitializationMode.FineTune)
                ? Text("TrainingWizard_Validation_Pretraining") : null;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        PeriodInputChanged(e.PropertyName);
        SufficiencyInputChanged(e.PropertyName);
        if (e.PropertyName is nameof(SelectedFramework) or nameof(SelectedArchitecture))
        {
            RefreshHyperparameterOptions();
            RefreshHyperparameterValidation();
        }
        if (e.PropertyName is nameof(SelectedTargetType) or nameof(SelectedFramework) or nameof(SelectedArchitecture) or nameof(SelectedInitializationMode)
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
        ITickerStateStore? tickerStore = null, IMessenger? messenger = null,
        IModelGenerationRegistry? modelRegistry = null,
        IStoredPeriodAdvisor? periodAdvisor = null, TimeProvider? clock = null, ITrainingResourceSettings? resourceSettings = null,
        IStockAnalyzerSettings? settings = null)
    {
        _settings = settings;
        ConfigurePeriod(periodAdvisor, clock);
        ConfigureSufficiency(resourceSettings);
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _tickerStore = tickerStore;
        _orchestrator = orchestrator;
        _ensembleSettings = ensembleSettings;
        _retrainingScheduler = retrainingScheduler;
        _modelDeploymentService = modelDeploymentService;
        _modelRegistry = modelRegistry;
        _experimentLogService = experimentLogService;
        _watchlistManager = watchlistManager;
        _marketDataProvider = marketDataProvider;
        _logger = logger ?? NullLogger<TrainingWizardViewModel>.Instance;
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;
        StartTrainingCommand.CanExecuteChanged += (_, _) => NotifyStartBlocker();       // every input the gate reads already notifies the command

        FeaturePicker =new FeatureChannelPickerViewModel(indicatorFactory ?? IndicatorFactory.Default, templateService, toastService)
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
        Hyperparameters.CollectionChanged += OnHyperparametersChanged;

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
        OnPropertyChanged(nameof(SelectedSymbolSummary));
        NotifyStartBlocker(); OnPropertyChanged(nameof(ModeBadges)); NotifyPeriodStatus();
        RefreshClipSigmaValidation();
        RefreshHyperparameterValidation();
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
            SchedulePeriod(immediate: true);
            ScheduleSufficiency();
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
        OnPropertyChanged(nameof(SelectedSymbolSummary));
        StartTrainingCommand.NotifyCanExecuteChanged();
        if (!first)
        {
            SchedulePeriod(immediate: false);
            ScheduleSufficiency();
        }
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
            // "All tickers" is ticked at first; ticking any other list means the owner wants that list, not everything, so it is unticked
            // (ticking "All tickers" itself, or unticking a list, changes nothing else).
            // The notice shows only right after an automatic untick: every selection change clears it first (the nested change of the untick included).
            SourceTreeNotice = null;
            if (sender is TrainingSourceTreeItem { IsSelected: true, Node: not AllTickersNode })
            {
                var unticked = Flatten(SourceTree).Where(i => i.Node is AllTickersNode && i.IsSelected).ToArray();
                foreach (var all in unticked) all.IsSelected = false;
                if (unticked.Length > 0) SourceTreeNotice = LocalizationManager.Instance["TrainingWizard_AllTickersUntickedNotice"];
            }

            _resolvedSymbols = null;
            OnPropertyChanged(nameof(SelectedSymbolSummary));
            StartTrainingCommand.NotifyCanExecuteChanged();
            SchedulePeriod(immediate: false);
            ScheduleSufficiency();
        }
    }

    /// <summary>How many tickers the ticked rows add up to (the union the run trains on), so a row ticked by default is not overlooked.</summary>
    public string SelectedSymbolSummary => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        LocalizationManager.Instance["TrainingWizard_SelectedTickers"], ResolveSymbols().Length);

    partial void OnSelectedFrameworkChanged(TrainingFramework value)
    {
        OnPropertyChanged(nameof(AvailableArchitectures));
        OnPropertyChanged(nameof(AvailableInitializationModes));
        OnPropertyChanged(nameof(HasArchitectureChoice));
        OnPropertyChanged(nameof(HasInitializationChoice));
        OnPropertyChanged(nameof(CanFreezeModules));
        if (!AvailableInitializationModes.Contains(SelectedInitializationMode))
            SelectedInitializationMode = TrainingInitializationMode.Fresh;
        SelectedArchitecture = DefaultArchitectureByFramework[value];
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
    private void AddHyperparameter()
    {
        HyperparameterNotice = null;
        Hyperparameters.Add(new TrainingHyperparameterEntry());
    }

    [RelayCommand]
    private void RemoveHyperparameter(TrainingHyperparameterEntry? entry)
    {
        HyperparameterNotice = null;
        if (entry is not null)
        {
            Hyperparameters.Remove(entry);
        }
    }

    private IReadOnlyList<TrainingHyperparameterOption>? _hyperparameterOptions;

    /// <summary>The keys the trainer of the chosen framework / architecture accepts, with their defaults and tooltips.</summary>
    public IReadOnlyList<TrainingHyperparameterOption> HyperparameterOptions =>
        _hyperparameterOptions ??= TrainingHyperparameterCatalog.For(SelectedFramework, SelectedArchitecture)
            .Select(TrainingHyperparameterOption.From).ToArray();

    /// <summary>Which rows were removed because the chosen framework / architecture does not accept their key (null = none).</summary>
    [ObservableProperty]
    private string? _hyperparameterNotice;

    /// <summary>The keys of the rows that are not in the key list (null = none). Informational: the trainer rejects an unknown flag, but Start stays possible.</summary>
    [ObservableProperty]
    private string? _unknownKeyNotice;

    /// <summary>Set when "All tickers" was unticked automatically because a list was ticked (null otherwise). Informational.</summary>
    [ObservableProperty]
    private string? _sourceTreeNotice;

    /// <summary>The attention architecture needs <c>hidden</c> to be a multiple of <c>heads</c>; this names the violating pair (null = fine). Blocks Start.</summary>
    [ObservableProperty]
    private string? _hyperparameterDependencyMessage;

    /// <summary>The rows whose edits are observed. <c>Clear()</c> raises a reset without the removed rows, so the removed ones are found by this list.</summary>
    private readonly List<TrainingHyperparameterEntry> _observedHyperparameters = new();

    private void OnHyperparametersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var entry in _observedHyperparameters.Where(observed => !Hyperparameters.Contains(observed)).ToArray())
        {
            entry.PropertyChanged -= OnHyperparameterEntryChanged;
            _observedHyperparameters.Remove(entry);
        }
        foreach (var entry in Hyperparameters.Where(row => !_observedHyperparameters.Contains(row)))
        {
            entry.PropertyChanged += OnHyperparameterEntryChanged;
            _observedHyperparameters.Add(entry);
        }
        RefreshHyperparameterValidation();
    }

    private void OnHyperparameterEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrainingHyperparameterEntry.Key) or nameof(TrainingHyperparameterEntry.Value)) RefreshHyperparameterValidation();
    }

    /// <summary>
    /// Recomputes the row messages, the unknown-key notice and the heads / hidden dependency. Rows with an empty key are ignored, as they are when the run
    /// starts. A key entered twice (also as <c>epochs</c> and <c>--epochs</c>) is one trainer flag and blocks Start, which also keeps the dictionary of the run
    /// configuration from receiving two equal keys.
    /// </summary>
    private void RefreshHyperparameterValidation()
    {
        var localization = LocalizationManager.Instance;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var filled = Hyperparameters.Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToArray();
        var duplicates = TrainingHyperparameterCatalog.FindDuplicates(filled.Select(h => h.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Hyperparameters)
        {
            if (string.IsNullOrWhiteSpace(row.Key)) row.ValidationMessage = null;
            else if (duplicates.Contains(TrainingHyperparameterCatalog.Normalize(row.Key)))
                row.ValidationMessage = string.Format(culture, localization["TrainingWizard_Hp_Error_DuplicateKey"], TrainingHyperparameterCatalog.Normalize(row.Key));
            else row.ValidationMessage = DomainMessage(row.Key, row.Value);
        }

        var unknown = filled.Where(h => TrainingHyperparameterCatalog.Find(h.Key) is null).Select(h => h.Key.Trim()).ToArray();
        UnknownKeyNotice = unknown.Length == 0 ? null : string.Format(culture, localization["TrainingWizard_Hp_UnknownKey"], string.Join(", ", unknown));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in filled) values.TryAdd(row.Key.Trim(), row.Value);
        HyperparameterDependencyMessage = TrainingHyperparameterCatalog.ValidateAttentionHeads(SelectedArchitecture, values) == HyperparameterProblem.None
            ? null
            : FormatHeadsDependency(TrainingHyperparameterCatalog.ResolveHiddenAndHeads(values));
        StartTrainingCommand.NotifyCanExecuteChanged();
    }

    private static string FormatHeadsDependency((long Hidden, long Heads) pair) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        LocalizationManager.Instance["TrainingWizard_Hp_Error_HeadsDoNotDivideHidden"], pair.Hidden, pair.Heads);

    /// <summary>The localized reason a value is outside the domain of its key, or null when it is acceptable (or the key is not judged).</summary>
    private static string? DomainMessage(string key, string value)
    {
        var problem = TrainingHyperparameterCatalog.Validate(key, value);
        if (problem == HyperparameterProblem.None || TrainingHyperparameterCatalog.Find(key) is not { Domain: { } domain } definition) return null;
        var localization = LocalizationManager.Instance;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string Bound(bool upper, string textKey) => string.Format(culture, localization[textKey], TrainingHyperparameterCatalog.LimitText(domain, upper));
        return problem switch
        {
            HyperparameterProblem.NotInteger => string.Format(culture, localization["TrainingWizard_Hp_Error_NotInteger"], definition.Key),
            HyperparameterProblem.NotNumber => string.Format(culture, localization["TrainingWizard_Hp_Error_NotNumber"], definition.Key),
            HyperparameterProblem.NotFinite => string.Format(culture, localization["TrainingWizard_Hp_Error_NotFinite"], definition.Key),
            HyperparameterProblem.BelowMinimum => string.Format(culture, localization["TrainingWizard_Hp_Error_BelowMinimum"], definition.Key,
                Bound(upper: false, domain.MinimumInclusive ? "TrainingWizard_Hp_Bound_AtLeast" : "TrainingWizard_Hp_Bound_GreaterThan")),
            HyperparameterProblem.AboveMaximum => string.Format(culture, localization["TrainingWizard_Hp_Error_AboveMaximum"], definition.Key,
                Bound(upper: true, domain.MaximumInclusive ? "TrainingWizard_Hp_Bound_AtMost" : "TrainingWizard_Hp_Bound_LessThan")),
            _ => null,
        };
    }

    /// <summary>
    /// A framework or architecture change replaces the key list. A row whose key the new trainer would reject is removed and named in
    /// <see cref="HyperparameterNotice"/>, never dropped silently and never left to fail at launch.
    /// </summary>
    private void RefreshHyperparameterOptions()
    {
        _hyperparameterOptions = null;
        OnPropertyChanged(nameof(HyperparameterOptions));
        foreach (var row in Hyperparameters) row.RefreshSelectedOption();
        var accepted = HyperparameterOptions.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = Hyperparameters
            .Where(h => TrainingHyperparameterCatalog.Find(h.Key) is { } definition && !accepted.Contains(definition.Key))
            .ToArray();
        if (rejected.Length == 0) return;
        foreach (var entry in rejected) Hyperparameters.Remove(entry);
        HyperparameterNotice = string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Instance["TrainingWizard_Hyperparameters_Removed"],
            string.Join(", ", rejected.Select(h => h.Key)));
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
            symbols = Array.Empty<string>();
        }
        else
        {
            symbols = Flatten(SourceTree).Where(i => i.IsSelectable && i.IsSelected)
                .SelectMany(i => i.Node is AllTickersNode ? _allTickers : _tickerStore.GetTickersForNode(i.Node));
        }

        return _resolvedSymbols = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // The conditions of CanStartTraining that have no message beside their field. Each one is a named predicate so that the footer reason
    // (StartBlockerDisplay) and the start gate read the same definition; the gate itself is unchanged (TrainingWizardStartBlockerAuditTests pins the table).
    private bool CheckpointPathValid() => !IsTransferMode || !string.IsNullOrWhiteSpace(CheckpointPath);
    private bool ParentModelValid() => !IsFineTuneMode || TrainingCheckpointContract.IsHash(ParentModelId.Trim());
    private bool FreezePathsValid() => SelectedFreezePaths().All(TrainingCheckpointContract.IsModulePath)
        && SelectedFreezePaths().Distinct(StringComparer.Ordinal).Count() == SelectedFreezePaths().Length;
    private bool SplitsValid() => NSplits is >= 2 and <= TrainingResourceOverrides.MaximumEvaluationFolds;
    private bool ZScoreNormalizationValid() => !UseFixedZScore || !UseComposedFeatures ||
        FeaturePicker.BuildFeatureSpec()?.Channels.All(c => c.Normalization == ChannelNormalization.None) == true;
    private bool ComposedSpecValid() => !UseComposedFeatures || FeaturePicker.BuildFeatureSpec() is not null;

    private bool CanStartTraining() =>
        !IsTraining && !IsDeploying
        && TransferControlsValid()
        && _orchestrator is not null
        && (SelectedTargetType != TargetType.Regression || SelectedFramework == TrainingFramework.PyTorch)
        && WindowSize > 0 && Horizon > 0
        && SplitsValid()
        && Gap is not (< 0)
        && OosTailDays is not (< 0)
        && !CalibrationUnavailable()
        && ExtendedLearningValidationMessage is null
        && TryParseLags(out var lags)
        && (!UseClipSigma || (UseFixedZScore && TryGetClipSigma(out _)))
        && (UseFixedZScore || lags.Length == 0)
        && ZScoreNormalizationValid()
        && !string.IsNullOrWhiteSpace(SelectedArchitecture)
        && ComposedSpecValid()
        && !IsManifestMissing
        && Hyperparameters.All(h => h.ValidationMessage is null)
        && HyperparameterDependencyMessage is null
        && ResolveSymbols().Length > 0;

    /// <summary>
    /// The reason Start is disabled for a condition that shows no message beside its field, in the order of <see cref="CanStartTraining"/>; empty when
    /// a start is possible, while a run or a deployment is active, and for the conditions that already have a message (calibration, extended learning, clip sigma,
    /// manifest, hyperparameters) or that the controls cannot produce. One line, blocking reasons only.
    /// </summary>
    public string StartBlockerDisplay
    {
        get
        {
            if (IsTraining || IsDeploying) return "";
            var text = LocalizationManager.Instance;
            if (!CheckpointPathValid()) return text["TrainingWizard_Start_CheckpointPath"];
            if (!ParentModelValid()) return text["TrainingWizard_Start_ParentModel"];
            if (!FreezePathsValid()) return text["TrainingWizard_Start_FreezePaths"];
            if (WindowSize <= 0) return text["TrainingWizard_Start_WindowSize"];
            if (Horizon <= 0) return text["TrainingWizard_Start_Horizon"];
            if (!SplitsValid()) return string.Format(CultureInfo.CurrentCulture, text["TrainingWizard_Start_Splits"], TrainingResourceOverrides.MaximumEvaluationFolds);
            if (!TryParseLags(out _)) return text["TrainingWizard_Start_Lags"];
            if (!ZScoreNormalizationValid()) return text["TrainingWizard_Start_ZScoreNormalization"];
            if (!ComposedSpecValid()) return text["TrainingWizard_Start_Features"];
            if (ResolveSymbols().Length == 0) return text["TrainingWizard_Start_NoTickers"];
            return "";
        }
    }
    public bool HasStartBlocker => StartBlockerDisplay.Length > 0;
    private void NotifyStartBlocker() { OnPropertyChanged(nameof(StartBlockerDisplay)); OnPropertyChanged(nameof(HasStartBlocker)); }

    /// <summary>The options that are not at their default and change what the result means: the start mode ("Continue from model", "Resume training"), CPCV, encoder pretraining and an ensemble method. Empty by default.</summary>
    public IReadOnlyList<string> ModeBadges => RunModeBadges.Build(key => LocalizationManager.Instance[key],
        (SelectedInitializationMode != TrainingInitializationMode.Fresh, "Enum_TrainingInitializationMode_" + SelectedInitializationMode),
        (UseCpcv, "TrainingWizard_Badge_Cpcv"),
        (UsePretraining, "TrainingWizard_Badge_Pretraining"),
        (SelectedEnsembleMethod != TrainingEnsembleMethod.None, "Enum_TrainingEnsembleMethod_" + SelectedEnsembleMethod));
    public bool HasModeBadges => ModeBadges.Count > 0;

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

        _runTerminal = false;           // before IsTraining: its change re-evaluates Cancel, which must not still see the previous run as finished
        IsTraining = true;
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
                ? (LocalizationManager.Instance["TrainingWizard_Status_Completed"] ?? "Training completed.") + ReachSuffix(ReadEvaluation(result.MetricsArtifactPath))
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
            var evaluation = ReadEvaluation(result.MetricsArtifactPath);   // the metrics file is read once for the lines below
            _messenger.Send(new PredictionConfigurationChangedMessage()); // the registration activates the generation when it may
            // Without the registry the wizard cannot tell whether the generation became active, so it states no reason for a missing activation (the application always
            // provides the registry; only a bare construction in a test lacks it).
            if (_modelRegistry is not null)
            {
                if (!string.Equals(_modelRegistry.ActiveId, generation.ModelId, StringComparison.Ordinal))
                {
                    StatusMessage = (AutoActivationStopText(evaluation, generation) is { } stopped
                        ? string.Format(DisplayFormat.Culture, LocalizationManager.Instance["TrainingWizard_Status_RegisteredNotAutoActivated"], generation.ModelId, stopped)
                        : string.Format(
                            LocalizationManager.Instance["TrainingWizard_Status_RegisteredNotActivated"]
                            ?? "Model generation {0} is registered but not active. To use it, open Tools > Model Execution > Prediction Models and tick the model. If it still does not become active, the message shown there names the reason.",
                            generation.ModelId))
                        + QualityHintSuffix(evaluation, generation.ClassOrder) + ReachSuffix(evaluation);
                    return;
                }
            }
            StatusMessage = string.Format(
                LocalizationManager.Instance["TrainingWizard_Status_Registered"] ?? "Registered model generation {0}.",
                generation.ModelId) + QualityHintSuffix(evaluation, generation.ClassOrder) + ReachSuffix(evaluation);
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

    /// <summary>The held-out evaluation beside a model (nothing known when there is no metrics file).</summary>
    private static ModelEvaluationSummary ReadEvaluation(string? metricsPath) =>
        metricsPath is null ? ModelEvaluationSummary.Unknown : ModelEvaluationSummary.Read(metricsPath);

    /// <summary>The reasons (joined) why <see cref="ModelAutoActivationGate"/> stopped the automatic activation of <paramref name="generation"/>, or null when it did not stop it.</summary>
    private string? AutoActivationStopText(ModelEvaluationSummary evaluation, ModelGenerationManifest generation)
    {
        var threshold = _settings?.PredictionConfidenceThreshold;
        var decision = ModelAutoActivationGate.Evaluate(evaluation, generation.ClassOrder, threshold);
        return decision.Stopped ? ModelReachDisplay.StopReasons(decision, threshold) : null;
    }

    /// <summary>The registration status gets one more line when the evaluation says the model is no better than a uniform guess (hint only; the model is registered as before).</summary>
    private static string QualityHintSuffix(ModelEvaluationSummary evaluation, string classOrder) =>
        ModelQualityCheck.IsNotBetterThanUniform(evaluation, classOrder) == true
            ? Environment.NewLine + LocalizationManager.Instance["TrainingWizard_Status_NotBetterThanGuessing"]
            : string.Empty;

    /// <summary>One more line saying how often the model reaches the chart's confidence threshold on its held-out windows (nothing when that is not known).</summary>
    private string ReachSuffix(ModelEvaluationSummary evaluation) =>
        _settings is not null && evaluation.Reach is { } histogram && histogram.ShareAtOrAbove(_settings.PredictionConfidenceThreshold) is { } share
            ? Environment.NewLine + ModelReachDisplay.StatusLine(share,
                histogram.ShareUpperBound(_settings.PredictionConfidenceThreshold) == 0.0, _settings.PredictionConfidenceThreshold)
            : string.Empty;

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
            _messenger.Send(new PredictionConfigurationChangedMessage());
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
    /// the theoretical race; see docs/sa_analysis_report_WebaiReviewOnnxTrainingBatch).
    /// Safe to call more than once (e.g. a duplicate window-close notification).
    /// </remarks>
    public void Dispose()
    {
        DisposePeriod();
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
