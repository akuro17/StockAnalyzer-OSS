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
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One selectable symbol source. Toggling <see cref="IsSelected"/> re-runs the preflight of the dialog.</summary>
public sealed partial class PortfolioSourceOption : ObservableObject
{
    public PortfolioSourceOption(RlSourceDescriptor descriptor, string name, string kind)
    { Descriptor = descriptor; Name = name; Kind = kind; }
    public RlSourceDescriptor Descriptor { get; }
    public string Name { get; }
    public string Kind { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// One row of the ticker-list tree of the dialog: the node's own name, an optional icon (path data from the ticker tab's SSoT) and, for a node that stands for
/// a source, the shared <see cref="PortfolioSourceOption"/> whose selection it forwards. A node that is not a source (a category without a descriptor, the tag
/// group) is a plain, non-selectable row.
/// </summary>
public sealed partial class PortfolioSourceTreeItem : ObservableObject
{
    public PortfolioSourceTreeItem(string name, string? iconData, PortfolioSourceOption? option)
    {
        _name = name; IconData = iconData; Option = option;
        if (option is not null) option.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PortfolioSourceOption.IsSelected)) OnPropertyChanged(nameof(IsSelected)); };
    }
    [ObservableProperty] private string _name;
    public string? IconData { get; }
    public PortfolioSourceOption? Option { get; }
    public bool IsSelectable => Option is not null;
    public bool HasIcon => IconData is not null;
    public bool IsSelected
    {
        get => Option?.IsSelected ?? false;
        set { if (Option is not null && Option.IsSelected != value) Option.IsSelected = value; }
    }
    public ObservableCollection<PortfolioSourceTreeItem> Children { get; } = new();
}

/// <summary>What a run does with a stored model: nothing (train from scratch), start training from it, or only evaluate it on the new panel.</summary>
public enum PortfolioRunKind { Scratch, FineTune, EvaluateBase }

/// <summary>Where the admission of the chosen base run stands. Only <see cref="Admitted"/> lets a transfer run start.</summary>
public enum PortfolioAdmissionState { None, Pending, Admitted, Rejected }

/// <summary>One stored run that can serve as a base: the run and the line the list shows for it.</summary>
public sealed record PortfolioBaseRunRow(Guid RunId, string Text);

/// <summary>
/// One entry of the panel choice: a stored panel of an earlier run, or (<see cref="PanelId"/> null) the default of capturing a new panel from the local data.
/// A stored panel carries the facts the run request reports for it (the stored panel itself decides the symbols, the period and the holdout).
/// </summary>
public sealed record PortfolioStoredPanelRow(Guid? PanelId, string Text, DateOnly Start, DateOnly EndExclusive, bool Holdout);

/// <summary>Where the check of the chosen stored panel stands. Only <see cref="Ready"/> lets a run on it start.</summary>
public enum PortfolioStoredPanelState { None, Checking, Ready, Failed }

/// <summary>One stored run the comparison entry can use, with the hashes that say which panel it was made on.</summary>
public sealed record PortfolioComparableRunRow(Guid RunId, string Text, string UniverseSha256, string CalendarSha256);

/// <summary>One resolved symbol with its status and the bars it contributes to the period.</summary>
public sealed record PortfolioSymbolRow(string Symbol, RlPanelSymbolStatus Status, string StatusText, int BarsInPeriod, int RequiredBarsPerBlock, string Detail = "",
    RlPanelSymbolRole Role = RlPanelSymbolRole.None, string RoleText = "")
{
    /// <summary>The reason the preflight gave for a data that could not be read; empty for every other status (their wording already says what is wrong).</summary>
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// One symbol's weight allocation visual row in the portfolio allocation viewer. <paramref name="TargetWeightPercent"/> is the bar length only (a double control
/// property); the weights are decimal everywhere else and the text comes from the decimal.
/// </summary>
public sealed record PortfolioWeightVisualRow(string Symbol, double TargetWeightPercent, string WeightDisplay, string RoleText);

/// <summary>
/// Portfolio reinforcement-learning dialog (D23). It holds no calculation and no file access: sources come from the ticker tree and the
/// <see cref="ITickerSourceResolver"/>, the preflight and the run from <see cref="IRlPortfolioService"/>, and the Start rules from the Core
/// <see cref="RlPanelStartGate"/>. Every change of the sources, the period, the evaluation mode or the holdout choice starts a new revision: the
/// previous check is cancelled and a late result is dropped, so a stale check can never overwrite a newer selection. Nothing is adjusted silently.
/// A run may use a stored base model (<see cref="IRlPortfolioTransferService"/>): the base run is admitted against the first validation date of the
/// data check, every change of an input starts the admission again, and a transfer run starts only once it says Admitted.
/// </summary>
public sealed partial class PortfolioRlDialogViewModel : ObservableObject, IDisposable
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly IRlPortfolioService _service;
    private readonly ITickerSourceResolver _resolver;
    private readonly ITickerStateStore _store;
    private readonly IBacktestDefaultSettingsManager _defaults;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource? _refreshCts, _runCts;
    private long _revision;
    private bool _disposed, _loading;
    private PanelPreflight? _preflight;
    private RlSourceResolution? _resolution;
    private RlPanelStartDecision? _decision;
    private readonly TimeProvider _clock;
    private bool _applyingSuggestion;
    /// <summary>The optional stored-period source of the end date; without it the end stays the last confirmed day of the calendar.</summary>
    private readonly IStoredPeriodAdvisor? _periodAdvisor;
    /// <summary>The end is the one the window proposed (the fallback, then the latest stored day + 1) until the user types one.</summary>
    private bool _endIsAuto;
    /// <summary>The pipeline's own write of the end box: it neither marks the end as typed nor schedules a second check.</summary>
    private bool _applyingEnd;
    private RlStartSuggestion? _suggestion;
    private DateOnly _suggestionEnd;
    private readonly IRlPortfolioTransferService? _transfer;
    private long _admissionRevision, _baseListRevision;
    private ImmutableArray<RlPortfolioBaseRunInfo> _baseInfos = ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
    private RlPortfolioBaseRun? _admitted;
    private DateOnly _admittedValidation;
    private string _admissionReason = "";
    private long _storedPanelRevision, _panelListRevision, _runListRevision;
    private ImmutableArray<RlPortfolioStoredPanelInfo> _panelInfos = ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;
    private ImmutableArray<RlPortfolioComparableRun> _runInfos = ImmutableArray<RlPortfolioComparableRun>.Empty;
    private DateOnly _storedPanelValidation;
    private string _storedPanelReason = "";
    private RlPortfolioTransferComparisonResult? _comparison;

    public ObservableCollection<PortfolioSourceOption> Sources { get; } = new();
    /// <summary>The same sources as a tree (the ticker tab's hierarchy); every selectable row shares the option of <see cref="Sources"/>.</summary>
    public ObservableCollection<PortfolioSourceTreeItem> SourceTree { get; } = new();
    public ObservableCollection<PortfolioSymbolRow> Symbols { get; } = new();
    public ObservableCollection<PortfolioWeightVisualRow> PortfolioWeights { get; } = new();
    public ObservableCollection<string> Cells { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();
    public ObservableCollection<string> ResultLines { get; } = new();
    /// <summary>Stored runs that can serve as a base, newest first as the store lists them.</summary>
    public ObservableCollection<PortfolioBaseRunRow> BaseRuns { get; } = new();
    /// <summary>The panel choice: the default (capture a new panel) first, then the panels earlier runs stored.</summary>
    public ObservableCollection<PortfolioStoredPanelRow> StoredPanels { get; } = new();
    /// <summary>Stored runs with an evaluation report; run A is chosen from these.</summary>
    public ObservableCollection<PortfolioComparableRunRow> ComparableRuns { get; } = new();
    /// <summary>The runs run A can be paired with: the same panel (equal hashes), not run A itself.</summary>
    public ObservableCollection<PortfolioComparableRunRow> ReferenceRuns { get; } = new();
    /// <summary>The lines of the last comparison: one block per cell, the sign test as a reference value, then the limitation statement.</summary>
    public ObservableCollection<string> ComparisonLines { get; } = new();

    [ObservableProperty] private string _startDate = "";
    [ObservableProperty] private string _endDateExclusive = "";
    [ObservableProperty] private bool _useShortHistory;
    /// <summary>The partition method of a short-history run; starts at the configured method.</summary>
    [ObservableProperty] private RlPortfolioPartitionMode _partitionMode;
    /// <summary>Minimum bars of the three partitions as typed; start at the configured values. Meaningful only for the short-history evaluation.</summary>
    [ObservableProperty] private string _minimumTrainBarsText = "";
    [ObservableProperty] private string _minimumValidationBarsText = "";
    [ObservableProperty] private string _minimumTestBarsText = "";
    [ObservableProperty] private bool _symbolHoldout = true;
    /// <summary>Holdout share and weight cap of this run as typed (percent); they start at the configured values and change this run only.</summary>
    [ObservableProperty] private string _holdoutPercentText = "";
    [ObservableProperty] private string _maxWeightPercentText = "";
    [ObservableProperty] private bool _excludeUnavailable;
    [ObservableProperty] private bool _smokeOnly;
    /// <summary>The start date follows the stored history of the ticked lists until the user types one; <see cref="RecalculateStartCommand"/> switches it back on.</summary>
    [ObservableProperty] private bool _startIsAuto = true;
    [ObservableProperty] private string _initialCapitalText = "";
    [ObservableProperty] private string _commissionFlatText = "";
    [ObservableProperty] private string _commissionPerUnitText = "";
    [ObservableProperty] private string _slippageRatioText = "";
    /// <summary>The liquidation penalty ratio; loaded from the saved Backtest default and carried by the run request (it was applied without being shown). Valid when 0 &lt;= p &lt; 1, enforced by <see cref="BacktestConfiguration"/>.</summary>
    [ObservableProperty] private string _liquidationPenaltyText = "";
    [ObservableProperty] private RlDialogLoadState _preflightState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private string _preflightText = "";
    [ObservableProperty] private string _blockerText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _technicalDetails = "";
    [ObservableProperty] private PortfolioRunKind _runKind = PortfolioRunKind.Scratch;
    [ObservableProperty] private PortfolioBaseRunRow? _selectedBaseRun;
    /// <summary>A fine-tune keeps the shared encoder at the base weights (the default); unticked, every actor weight trains.</summary>
    [ObservableProperty] private bool _freezeEncoder = true;
    [ObservableProperty] private PortfolioAdmissionState _admissionState = PortfolioAdmissionState.None;
    [ObservableProperty] private PortfolioStoredPanelRow? _selectedStoredPanel;
    [ObservableProperty] private PortfolioStoredPanelState _storedPanelState = PortfolioStoredPanelState.None;
    [ObservableProperty] private PortfolioComparableRunRow? _selectedRunA;
    [ObservableProperty] private PortfolioComparableRunRow? _selectedRunB;
    [ObservableProperty] private string _comparisonMessage = "";
    [ObservableProperty] private string _portfolioAllocationSummary = "";

    /// <param name="transfer">The stored-model seam; null hides every transfer control and the dialog only trains from scratch.</param>
    public PortfolioRlDialogViewModel(IRlPortfolioService service, ITickerSourceResolver resolver, ITickerStateStore store,
        IBacktestDefaultSettingsManager defaults, ILocalizationService localization, TimeProvider? clock = null, IRlPortfolioTransferService? transfer = null,
        IStoredPeriodAdvisor? periodAdvisor = null)
    {
        _transfer = transfer;
        _periodAdvisor = periodAdvisor;
        _clock = clock ?? TimeProvider.System;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        PreflightText = _localization.GetString("Prl.Preflight.Idle");
        // Backing fields, not the properties: the starting values come from the settings and must not schedule a check.
        var settings = _service.Settings;
        _partitionMode = settings.PartitionMode;
        _minimumTrainBarsText = settings.MinimumTrainBars.ToString(CultureInfo.InvariantCulture);
        _minimumValidationBarsText = settings.MinimumValidationBars.ToString(CultureInfo.InvariantCulture);
        _minimumTestBarsText = settings.MinimumTestBars.ToString(CultureInfo.InvariantCulture);
        _holdoutPercentText = settings.HoldoutPercent.ToString(CultureInfo.InvariantCulture);
        _maxWeightPercentText = settings.MaxWeightPercent.ToString(CultureInfo.InvariantCulture);
        Services.LocalizationManager.Instance.LocaleChanged += LocaleChanged;
    }

    // ---- derived state ----

    /// <summary>Phases the runner reports that have no wording of their own show as processing, as in the single-symbol dialog.</summary>
    private static readonly string[] KnownStates = { "Ready", "Preparing", "Training", "Evaluating", "Scaler", "Dependencies", "Succeeded", "Failed", "Rejected",
        "Unavailable", "InsufficientData", "Cancelling", "Cancelled" };
    public string StatusDisplay => _localization.GetString(KnownStates.Contains(Status, StringComparer.Ordinal) ? "Rl.State." + Status : "Rl.Ui.Status.Processing");
    public bool Editable => !_disposed && !IsBusy;
    public bool HasResult => ResultLines.Count > 0;
    /// <summary>Sources are chosen before the check, so the list stays editable while a check runs; only a run locks it.</summary>
    public bool HasSources => Sources.Count > 0;
    public bool HasPortfolioWeights => PortfolioWeights.Count > 0;
    public bool PeriodValid => TryPeriod(out _, out _);
    public bool CostsValid => TryCosts(out _);

    // ---- stored base model (transfer) ----

    public bool TransferAvailable => _transfer is not null;
    /// <summary>The run uses a stored model: its admission gates Start.</summary>
    private bool TransferActive => _transfer is not null && RunKind != PortfolioRunKind.Scratch;
    public bool ShowBaseSelection => TransferActive;
    public bool ShowFreeze => TransferActive && RunKind == PortfolioRunKind.FineTune;
    public bool NoBaseRuns => TransferActive && BaseRuns.Count == 0;
    public bool BaseSelectionEditable => Editable && TransferActive;
    public bool IsScratchRun { get => RunKind == PortfolioRunKind.Scratch; set { if (value) RunKind = PortfolioRunKind.Scratch; } }
    public bool IsFineTuneRun { get => RunKind == PortfolioRunKind.FineTune; set { if (value) RunKind = PortfolioRunKind.FineTune; } }
    public bool IsEvaluateBaseRun { get => RunKind == PortfolioRunKind.EvaluateBase; set { if (value) RunKind = PortfolioRunKind.EvaluateBase; } }
    public bool AdmissionRejected => AdmissionState == PortfolioAdmissionState.Rejected;
    /// <summary>A transfer run needs an admitted base run; a scratch run needs nothing.</summary>
    private bool TransferGateOpen => !TransferActive || AdmissionState == PortfolioAdmissionState.Admitted;
    /// <summary>What the admission says now: the missing choice, the wait, the accepted dates, or the exact reason of the rule that rejected the base.</summary>
    public string AdmissionMessage => !TransferActive ? ""
        : SelectedBaseRun is null ? _localization.GetString("Prl.Transfer.NeedBase")
        : AdmissionState switch
        {
            PortfolioAdmissionState.Pending => _localization.GetString(CurrentFirstValidation is null ? "Prl.Transfer.WaitingForCheck" : "Prl.Transfer.Pending"),
            PortfolioAdmissionState.Admitted when _admitted is not null => PortfolioRlPresentation.Admitted(_admitted, _admittedValidation, _localization),
            PortfolioAdmissionState.Rejected => _admissionReason,
            _ => "",
        };

    /// <summary>
    /// The first validation date of the data check that is current (not one a newer input has already outdated); null while no such check holds. With a stored
    /// panel the date is that of the panel (the check of the stored panel), because the panel, not the entries above, decides what the run uses.
    /// </summary>
    private DateOnly? CurrentFirstValidation => UseStoredPanel ? (StoredPanelState == PortfolioStoredPanelState.Ready ? _storedPanelValidation : null)
        : PreflightState == RlDialogLoadState.Loaded && _decision is not null ? _preflight?.FirstValidationDate : null;

    // ---- panel choice (reuse a stored panel) ----

    /// <summary>The run reuses a panel an earlier run stored; the data check of the symbols and the period is then not used.</summary>
    public bool UseStoredPanel => _transfer is not null && SelectedStoredPanel?.PanelId is not null;
    public bool PanelSelectionEditable => Editable && TransferAvailable;
    public bool StoredPanelFailed => UseStoredPanel && StoredPanelState == PortfolioStoredPanelState.Failed;
    public string StoredPanelMessage => !UseStoredPanel ? ""
        : StoredPanelState switch
        {
            PortfolioStoredPanelState.Checking => _localization.GetString("Prl.Panel.Checking"),
            PortfolioStoredPanelState.Ready => string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Panel.Ready"),
                _storedPanelValidation.ToString(DateFormat, CultureInfo.InvariantCulture)),
            PortfolioStoredPanelState.Failed => _storedPanelReason,
            _ => "",
        };

    private void NotifyStoredPanel()
    {
        OnPropertyChanged(nameof(UseStoredPanel)); OnPropertyChanged(nameof(PanelSelectionEditable)); OnPropertyChanged(nameof(StoredPanelFailed));
        OnPropertyChanged(nameof(StoredPanelMessage)); NotifyState();
    }

    /// <summary>
    /// Checks the chosen stored panel again (off the UI thread: the panel is loaded and fully re-verified under the current method and minimums). Called on a change of
    /// the panel, the evaluation mode or the partition plan; a late answer is dropped. The admission follows the date this check yields.
    /// </summary>
    private void RecheckStoredPanel()
    {
        long revision = Interlocked.Increment(ref _storedPanelRevision);
        _storedPanelReason = "";
        if (_disposed) return;
        bool wasChecked = StoredPanelState != PortfolioStoredPanelState.None;
        if (!UseStoredPanel || !TryPlan(out var plan))        // an invalid minimum has its own message
        {
            SetStoredPanelState(PortfolioStoredPanelState.None);
            if (wasChecked || UseStoredPanel) RecomputeAdmission();     // nothing changes for a dialog that never chose a stored panel
            return;
        }
        SetStoredPanelState(PortfolioStoredPanelState.Checking);
        RecomputeAdmission();
        _ = CheckStoredPanelAsync(revision, SelectedStoredPanel!.PanelId!.Value, UseShortHistory ? RlPanelEvaluationMode.ShortHistory : RlPanelEvaluationMode.Standard, plan);
    }

    private void SetStoredPanelState(PortfolioStoredPanelState state) { StoredPanelState = state; NotifyStoredPanel(); }

    private async Task CheckStoredPanelAsync(long revision, Guid panelId, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan)
    {
        var transfer = _transfer!;
        try
        {
            var date = await Task.Run(() => transfer.StoredPanelFirstValidationDate(panelId, mode, plan)).ConfigureAwait(true);
            if (_disposed || revision != Interlocked.Read(ref _storedPanelRevision)) return;
            _storedPanelValidation = date;
            SetStoredPanelState(PortfolioStoredPanelState.Ready);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed || revision != Interlocked.Read(ref _storedPanelRevision)) return;
            _storedPanelReason = e.Message;
            SetStoredPanelState(PortfolioStoredPanelState.Failed);
        }
        RecomputeAdmission();
    }

    /// <summary>Lists the panels earlier runs stored (off the UI thread); the chosen panel stays chosen when it is still listed, otherwise the default applies.</summary>
    private async Task LoadStoredPanelsAsync()
    {
        var transfer = _transfer;
        if (transfer is null || _disposed) return;
        long revision = Interlocked.Increment(ref _panelListRevision);
        ImmutableArray<RlPortfolioStoredPanelInfo> infos;
        try { infos = await Task.Run(() => transfer.ListStoredPanels()).ConfigureAwait(true); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed || revision != Interlocked.Read(ref _panelListRevision)) return;
            infos = ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;
            TechnicalDetails = e.Message;
        }
        if (_disposed || revision != Interlocked.Read(ref _panelListRevision)) return;
        _panelInfos = infos;
        RenderStoredPanels();
    }

    private void RenderStoredPanels()
    {
        var keep = SelectedStoredPanel?.PanelId;
        StoredPanels.Clear();
        StoredPanels.Add(new PortfolioStoredPanelRow(null, _localization.GetString("Prl.Panel.New"), default, default, false));
        foreach (var info in _panelInfos)
            StoredPanels.Add(new PortfolioStoredPanelRow(info.PanelId, PortfolioRlPresentation.StoredPanel(info, _localization), info.PeriodStartInclusive, info.PeriodEndExclusive, info.HoldoutCount > 0));
        SelectedStoredPanel = StoredPanels.FirstOrDefault(r => r.PanelId == keep) ?? StoredPanels[0];       // a changed selection re-enters the check itself
    }

    // ---- comparison of two runs ----

    public bool CanCompare => !_disposed && !IsBusy && SelectedRunA is not null && SelectedRunB is not null && SelectedRunA.RunId != SelectedRunB.RunId;
    public bool NoComparableRuns => TransferAvailable && ComparableRuns.Count == 0;
    public bool NoReferenceRuns => SelectedRunA is not null && ReferenceRuns.Count == 0;
    public bool HasComparison => ComparisonLines.Count > 0;
    public bool HasComparisonMessage => ComparisonMessage.Length > 0;

    private void NotifyComparison()
    {
        OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(NoComparableRuns)); OnPropertyChanged(nameof(NoReferenceRuns));
        OnPropertyChanged(nameof(HasComparison)); OnPropertyChanged(nameof(HasComparisonMessage)); CompareCommand.NotifyCanExecuteChanged();
    }

    private void ClearComparison() { _comparison = null; ComparisonLines.Clear(); ComparisonMessage = ""; NotifyComparison(); }

    /// <summary>Lists the stored runs that hold an evaluation report (off the UI thread); the chosen runs stay chosen when still listed.</summary>
    private async Task LoadComparableRunsAsync()
    {
        var transfer = _transfer;
        if (transfer is null || _disposed) return;
        long revision = Interlocked.Increment(ref _runListRevision);
        ImmutableArray<RlPortfolioComparableRun> runs;
        try { runs = await Task.Run(() => transfer.ListComparableRuns()).ConfigureAwait(true); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed || revision != Interlocked.Read(ref _runListRevision)) return;
            runs = ImmutableArray<RlPortfolioComparableRun>.Empty;
            TechnicalDetails = e.Message;
        }
        if (_disposed || revision != Interlocked.Read(ref _runListRevision)) return;
        _runInfos = runs;
        RenderComparableRuns();
    }

    private void RenderComparableRuns()
    {
        var keepA = SelectedRunA?.RunId;
        var keepB = SelectedRunB?.RunId;
        ComparableRuns.Clear();
        foreach (var run in _runInfos.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.RunId))
            ComparableRuns.Add(new PortfolioComparableRunRow(run.RunId, PortfolioRlPresentation.ComparableRun(run, _localization), run.UniverseSha256, run.CalendarSha256));
        SelectedRunA = keepA is { } a ? ComparableRuns.FirstOrDefault(r => r.RunId == a) : null;       // a changed selection rebuilds the reference list itself
        RenderReferenceRuns();
        SelectedRunB = keepB is { } b ? ReferenceRuns.FirstOrDefault(r => r.RunId == b) : null;
        NotifyComparison();
    }

    private void RenderReferenceRuns()
    {
        ReferenceRuns.Clear();
        if (SelectedRunA is { } a)
            foreach (var run in ComparableRuns.Where(r => r.RunId != a.RunId && r.UniverseSha256 == a.UniverseSha256 && r.CalendarSha256 == a.CalendarSha256))
                ReferenceRuns.Add(run);
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        if (!CanCompare || _transfer is not { } transfer) return;
        Guid a = SelectedRunA!.RunId, b = SelectedRunB!.RunId;
        ClearComparison();
        try
        {
            var result = await Task.Run(() => transfer.CompareRuns(a, b)).ConfigureAwait(true);
            if (_disposed) return;
            _comparison = result;
            foreach (string line in PortfolioRlPresentation.Comparison(result, _localization)) ComparisonLines.Add(line);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed) return;
            ComparisonMessage = e.Message;           // the exact reason (for example which hash differs); nothing is shown as a result
        }
        NotifyComparison();
    }

    private void NotifyTransfer()
    {
        OnPropertyChanged(nameof(ShowBaseSelection)); OnPropertyChanged(nameof(ShowFreeze)); OnPropertyChanged(nameof(NoBaseRuns));
        OnPropertyChanged(nameof(BaseSelectionEditable)); OnPropertyChanged(nameof(AdmissionRejected)); OnPropertyChanged(nameof(AdmissionMessage));
        OnPropertyChanged(nameof(IsScratchRun)); OnPropertyChanged(nameof(IsFineTuneRun)); OnPropertyChanged(nameof(IsEvaluateBaseRun));
    }

    /// <summary>
    /// Starts the admission of the chosen base run again: the earlier answer is dropped (a late one is ignored), and the new one waits for a current data
    /// check. Called on every change of a base, the run kind or any input the data check depends on.
    /// </summary>
    private void RecomputeAdmission()
    {
        long revision = Interlocked.Increment(ref _admissionRevision);
        _admitted = null; _admissionReason = "";
        if (_disposed) return;
        if (!TransferActive || SelectedBaseRun is null) { SetAdmission(PortfolioAdmissionState.None); return; }
        SetAdmission(PortfolioAdmissionState.Pending);
        if (CurrentFirstValidation is { } date) _ = AdmitAsync(revision, SelectedBaseRun.RunId, date);
    }

    private void SetAdmission(PortfolioAdmissionState state)
    {
        AdmissionState = state;
        NotifyTransfer(); NotifyState();
    }

    /// <summary>The store is read (hashes, manifests, the model metadata), so the admission runs off the UI thread; only the newest revision may publish.</summary>
    private async Task AdmitAsync(long revision, Guid baseRunId, DateOnly firstValidation)
    {
        var transfer = _transfer!;
        try
        {
            var admitted = await Task.Run(() => transfer.Admit(baseRunId, firstValidation)).ConfigureAwait(true);
            if (_disposed || revision != Interlocked.Read(ref _admissionRevision)) return;
            _admitted = admitted; _admittedValidation = firstValidation;
            SetAdmission(PortfolioAdmissionState.Admitted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed || revision != Interlocked.Read(ref _admissionRevision)) return;
            _admissionReason = e.Message;
            SetAdmission(PortfolioAdmissionState.Rejected);
        }
    }

    /// <summary>Lists the stored runs that hold a complete model (off the UI thread); the chosen run stays chosen when it is still listed.</summary>
    private async Task LoadBaseRunsAsync()
    {
        var transfer = _transfer;
        if (transfer is null || _disposed) return;
        long revision = Interlocked.Increment(ref _baseListRevision);
        ImmutableArray<RlPortfolioBaseRunInfo> infos;
        try { infos = await Task.Run(() => transfer.ListBaseRuns()).ConfigureAwait(true); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_disposed || revision != Interlocked.Read(ref _baseListRevision)) return;
            infos = ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
            TechnicalDetails = e.Message;
        }
        if (_disposed || revision != Interlocked.Read(ref _baseListRevision)) return;
        _baseInfos = infos;
        RenderBaseRuns();
    }

    private void RenderBaseRuns()
    {
        var keep = SelectedBaseRun?.RunId;
        BaseRuns.Clear();
        foreach (var info in _baseInfos) BaseRuns.Add(new PortfolioBaseRunRow(info.RunId, PortfolioRlPresentation.BaseRun(info, _localization)));
        SelectedBaseRun = keep is { } id ? BaseRuns.FirstOrDefault(r => r.RunId == id) : null;     // a changed selection re-enters the admission itself
        NotifyTransfer();
    }

    // ---- partition of the short-history evaluation ----

    public bool IsRatioPartition
    {
        get => PartitionMode == RlPortfolioPartitionMode.Ratio;
        set { if (value) PartitionMode = RlPortfolioPartitionMode.Ratio; }
    }
    public bool IsBlockCountPartition
    {
        get => PartitionMode == RlPortfolioPartitionMode.BlockCount;
        set { if (value) PartitionMode = RlPortfolioPartitionMode.BlockCount; }
    }
    /// <summary>The partition controls act on the short-history evaluation only; the Standard evaluation keeps its fixed minimum.</summary>
    public bool PartitionEditable => Editable && UseShortHistory;
    public bool ShowPartitionFixed => !UseShortHistory;
    public string PartitionFixedText
    {
        get
        {
            var standard = RlPanelBlockRequirement.Standard;
            return string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Minimum.StandardFixed"),
                standard.TrainBlocks, standard.ValidationBlocks, standard.TestBlocks);
        }
    }
    /// <summary>The hard band of a minimum-bars input, for the input message (the Core limits are the only owner).</summary>
    public long MinimumBarsLowest => RlPortfolioLimits.MinimumBars.HardMin;
    public long MinimumBarsHighest => RlPortfolioLimits.MinimumBars.HardMax;
    public bool MinimumBarsValid => !UseShortHistory || TryMinimumBars(out _);
    public string MinimumBarsMessage => MinimumBarsValid ? ""
        : string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Minimum.Invalid"), MinimumBarsLowest, MinimumBarsHighest);
    // ---- holdout share and weight cap of this run ----

    private static bool TryPercent(string text, SettingBand band, out int value)
        => int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && band.IsWithinHardLimits(value);
    public bool PercentsValid => TryPercent(HoldoutPercentText, RlPortfolioLimits.HoldoutPercent, out _) && TryPercent(MaxWeightPercentText, RlPortfolioLimits.MaxWeightPercent, out _);
    public string PercentMessage => PercentsValid ? ""
        : string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Percent.Invalid"), RlPortfolioLimits.HoldoutPercent.HardMin,
            RlPortfolioLimits.HoldoutPercent.HardMax, RlPortfolioLimits.MaxWeightPercent.HardMin, RlPortfolioLimits.MaxWeightPercent.HardMax);
    /// <summary>A stored panel carries its own holdout, so the share applies to a new capture only.</summary>
    public bool HoldoutPercentEditable => Editable && SymbolHoldout && !UseStoredPanel;

    /// <summary>The value to send for a typed percent: null when it equals the configured one (so a default run is the request it always was) or is not valid.</summary>
    private int? Override(string text, SettingBand band, int configured)
        => TryPercent(text, band, out int value) && value != configured ? value : null;
    private int? HoldoutPercentOverride => Override(HoldoutPercentText, RlPortfolioLimits.HoldoutPercent, _service.Settings.HoldoutPercent);
    private int? MaxWeightPercentOverride => Override(MaxWeightPercentText, RlPortfolioLimits.MaxWeightPercent, _service.Settings.MaxWeightPercent);
    /// <summary>The settings this run is gated against: the configured ones with the typed holdout share (the weight cap does not affect the gate).</summary>
    private RlPortfolioSettings RunSettings => _service.Settings.WithRunOverrides(HoldoutPercentOverride, null);

    // ---- block-count entry (the bars inputs stay the one source of truth) ----

    public string TrainBlocksText { get => BlocksOf(MinimumTrainBarsText); set => SetBlocks(value, bars => MinimumTrainBarsText = bars); }
    public string ValidationBlocksText { get => BlocksOf(MinimumValidationBarsText); set => SetBlocks(value, bars => MinimumValidationBarsText = bars); }
    public string TestBlocksText { get => BlocksOf(MinimumTestBarsText); set => SetBlocks(value, bars => MinimumTestBarsText = bars); }

    private static string BlocksOf(string bars)
        => TryParseBars(bars, out int value) ? RlPanelBlockRequirement.BlocksFor(value).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>A whole number of blocks sets the minimum to exactly that many blocks; anything else leaves the bars as they are.</summary>
    private static void SetBlocks(string text, Action<string> setBars)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int blocks) || blocks < 1 || blocks > RlPortfolioLimits.MaximumBlocks) return;
        int bars = RlPanelBlockRequirement.BarsOf(blocks);
        if (RlPortfolioLimits.MinimumBars.IsWithinHardLimits(bars)) setBars(bars.ToString(CultureInfo.InvariantCulture));
    }

    public string TrainBarsEffective => Effective(MinimumTrainBarsText);
    public string ValidationBarsEffective => Effective(MinimumValidationBarsText);
    public string TestBarsEffective => Effective(MinimumTestBarsText);
    /// <summary>What the chosen block counts mean for training (replays of the train blocks); empty unless the block-count method is chosen with valid inputs.</summary>
    public IReadOnlyList<string> PartitionNotices
        => UseShortHistory && PartitionMode == RlPortfolioPartitionMode.BlockCount && TryMinimumBars(out var bars)
            ? PortfolioRlPresentation.PartitionNotices(new RlPanelPartitionPlan(PartitionMode, bars.ToRequirement(), bars), _localization)
            : Array.Empty<string>();

    private string Effective(string text)
        => UseShortHistory && TryParseBars(text, out int bars) ? PortfolioRlPresentation.EffectiveBars(bars, _localization) : "";

    private static bool TryParseBars(string text, out int bars)
        => int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bars) && RlPortfolioLimits.MinimumBars.IsWithinHardLimits(bars);

    private bool TryMinimumBars(out RlPanelMinimumBars bars)
    {
        bars = null!;
        if (!TryParseBars(MinimumTrainBarsText, out int train) || !TryParseBars(MinimumValidationBarsText, out int validation)
            || !TryParseBars(MinimumTestBarsText, out int test)) return false;
        bars = new RlPanelMinimumBars(train, validation, test);
        return true;
    }

    /// <summary>
    /// The plan this run passes to the service: null for the Standard evaluation and when the dialog shows exactly the plan of the settings (the service then
    /// resolves it itself); false when a minimum is not a whole number inside the hard band.
    /// </summary>
    private bool TryPlan(out RlPanelPartitionPlan? plan)
    {
        plan = null;
        if (!UseShortHistory) return true;
        if (!TryMinimumBars(out var bars)) return false;
        var candidate = new RlPanelPartitionPlan(PartitionMode, bars.ToRequirement(), bars);
        plan = candidate == RlPanelPartitionPlan.FromSettings(_service.Settings) ? null : candidate;
        return true;
    }

    private void NotifyPartition()
    {
        OnPropertyChanged(nameof(IsRatioPartition)); OnPropertyChanged(nameof(IsBlockCountPartition));
        OnPropertyChanged(nameof(PartitionEditable)); OnPropertyChanged(nameof(ShowPartitionFixed)); OnPropertyChanged(nameof(PartitionFixedText));
        OnPropertyChanged(nameof(MinimumBarsValid)); OnPropertyChanged(nameof(MinimumBarsMessage));
        OnPropertyChanged(nameof(TrainBarsEffective)); OnPropertyChanged(nameof(ValidationBarsEffective)); OnPropertyChanged(nameof(TestBarsEffective));
        OnPropertyChanged(nameof(TrainBlocksText)); OnPropertyChanged(nameof(ValidationBlocksText)); OnPropertyChanged(nameof(TestBlocksText));
        OnPropertyChanged(nameof(PartitionNotices));
    }

    /// <summary>
    /// Nothing for a valid period, and nothing while the start is still empty and either nothing was entered or the start is automatic (it is computed
    /// from the ticked lists); a future end date has its own wording.
    /// </summary>
    public string PeriodMessage => PeriodValid ? ""
        : TryParseDate(EndDateExclusive, out var end) && end > Today ? _localization.GetString("Prl.Period.EndFuture")
        : StartDate.Length == 0 && (EndDateExclusive.Length == 0 || StartIsAuto) ? ""
        : _localization.GetString("Prl.Period.Invalid");
    /// <summary>The local calendar date of the injected clock, read at each use.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
    /// <summary>The latest selectable end date (the end date is exclusive, so today is allowed).</summary>
    public DateTime? EndDateLatest => Today.ToDateTime(TimeOnly.MinValue);
    public bool CanRecalculateStart => !StartIsAuto && Editable;
    /// <summary>How the current start date came about; empty once the user has typed one.</summary>
    public string StartMessage => _suggestion switch
    {
        { Status: RlStartSuggestionStatus.Found } s => string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Start.Auto"), s.RequiredBars, _suggestionEnd.ToString(DateFormat, CultureInfo.InvariantCulture)),
        { Status: RlStartSuggestionStatus.InsufficientHistory } s => string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Start.Insufficient"), s.RequiredBars, s.AvailableBars),
        { Status: RlStartSuggestionStatus.NoUsableData } => _localization.GetString("Prl.Start.NoData"),
        _ => "",
    };
    public string CostsMessage => CostsValid ? "" : _localization.GetString("Prl.Costs.Invalid");
    /// <summary>The data a run needs is ready: the stored panel after its check, or the data check of the symbols and the period.</summary>
    private bool DataGateOpen => UseStoredPanel ? StoredPanelState == PortfolioStoredPanelState.Ready
        : PreflightState == RlDialogLoadState.Loaded && _decision is { CanStart: true } && PeriodValid;
    public bool CanStart => !_disposed && !IsBusy && DataGateOpen && CostsValid && MinimumBarsValid && PercentsValid && TransferGateOpen;
    /// <summary>
    /// One short blocking reason for the footer, taken from the messages the window already has (the Start-gate decision, the period, costs, percent and
    /// minimum-bars messages, the stored-panel and base-run admission); empty when a start is possible, while a run is active and while a data check is only
    /// pending (waiting is not a reason). Nothing here changes what can start.
    /// </summary>
    public string StartBlockerDisplay
    {
        get
        {
            if (!Editable || CanStart) return "";
            foreach (string reason in new[] { PeriodMessage, BlockerText, CostsMessage, PercentMessage, MinimumBarsMessage })
                if (!string.IsNullOrEmpty(reason)) return reason;
            if (UseStoredPanel && StoredPanelState == PortfolioStoredPanelState.Failed) return StoredPanelMessage;
            if (TransferActive && !TransferGateOpen && (SelectedBaseRun is null || AdmissionState == PortfolioAdmissionState.Rejected)) return AdmissionMessage;
            return UseStoredPanel || Sources.Any(s => s.IsSelected) ? "" : _localization.GetString("Prl.Preflight.Idle");
        }
    }
    public bool HasStartBlocker => StartBlockerDisplay.Length > 0;
    /// <summary>Short labels for the options that are not at their default and change what the result means: trial run, short history, a run that starts from a stored model, and the symbol holdout switched off (it is ON by default). Empty in the default state.</summary>
    public IReadOnlyList<string> ModeBadges => RunModeBadges.Build(_localization, (SmokeOnly, "Rl.Badge.Smoke"), (UseShortHistory, "Rl.Badge.ShortHistory"),
        (RunKind == PortfolioRunKind.FineTune, "Rl.Badge.FineTune"), (RunKind == PortfolioRunKind.EvaluateBase, "Rl.Badge.EvaluateBase"),
        (!SymbolHoldout, "Rl.Badge.NoHoldout"));
    public bool HasModeBadges => ModeBadges.Count > 0;
    /// <summary>The exclusion box is meaningful only when some resolved symbol cannot be used.</summary>
    public bool HasSymbols => Symbols.Count > 0;
    public bool HasUnavailableSymbols => Symbols.Any(s => s.Status != RlPanelSymbolStatus.Ready);
    public DateTime? StartDateAsDateTime
    {
        get => TryParseDate(StartDate, out var date) ? date.ToDateTime(TimeOnly.MinValue) : null;
        set { string text = value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? ""; if (StartDate != text) StartDate = text; OnPropertyChanged(); }
    }
    public DateTime? EndDateExclusiveAsDateTime
    {
        get => TryParseDate(EndDateExclusive, out var date) ? date.ToDateTime(TimeOnly.MinValue) : null;
        set { string text = value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? ""; if (EndDateExclusive != text) EndDateExclusive = text; OnPropertyChanged(); }
    }

    private static bool TryParseDate(string text, out DateOnly date)
        => DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Both dates parse, the start is before the end and the end is not later than today (the end date itself is exclusive). Nothing else limits the input.</summary>
    private bool TryPeriod(out DateOnly start, out DateOnly end)
    {
        end = default;
        return TryParseDate(StartDate, out start) && TryEnd(out end) && start < end;
    }

    private bool TryEnd(out DateOnly end) => TryParseDate(EndDateExclusive, out end) && end <= Today;

    private bool TryCosts(out BacktestConfiguration costs)
    {
        costs = null!;
        static bool Parse(string text, out decimal value) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value);
        if (!Parse(InitialCapitalText, out decimal capital) || !Parse(CommissionFlatText, out decimal flat)
            || !Parse(CommissionPerUnitText, out decimal unit) || !Parse(SlippageRatioText, out decimal slippage)
            || !Parse(LiquidationPenaltyText, out decimal liquidation)) return false;
        try
        {
            costs = RlContract.CreateConfiguration(new BacktestConfiguration
            {
                SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter, InitialCapital = capital,
                CommissionFlat = flat, CommissionPerUnit = unit, SlippageRatio = slippage, LiquidationPenaltyRatio = liquidation,
            });
            return true;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException) { return false; }
    }

    // ---- lifecycle ----

    /// <summary>Lists the sources of the ticker tree and loads the cost defaults. Called once when the window opens.</summary>
    public async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            void Visit(TickerGroupNode node, string? parent, HashSet<TickerGroupNode> visited, ObservableCollection<PortfolioSourceTreeItem> into)
            {
                if (node is ActionNode || !visited.Add(node)) return;
                var descriptor = Describe(node);
                string name = parent is null ? node.DisplayName : parent + " / " + node.DisplayName;
                PortfolioSourceOption? option = null;
                if (descriptor is not null)
                {
                    option = Sources.FirstOrDefault(s => s.Descriptor == descriptor);       // a descriptor has one option; a second node shares it
                    if (option is null)
                    {
                        option = new PortfolioSourceOption(descriptor, name, _localization.GetString("Prl.Source.Kind." + descriptor.Kind));
                        Sources.Add(option);
                    }
                }
                var item = new PortfolioSourceTreeItem(node.DisplayName, IconFor(node), option);
                into.Add(item);
                if (node.Children is not null) foreach (var child in node.Children) Visit(child, descriptor is null ? parent : name, visited, item.Children);
            }
            var visitedNodes = new HashSet<TickerGroupNode>(ReferenceEqualityComparer.Instance);
            foreach (var group in _store.Groups) Visit(group, null, visitedNodes, SourceTree);
            foreach (var option in Sources) option.PropertyChanged += OptionChanged;
            if (EndDateExclusive.Length == 0) { WriteAutomaticEnd(FallbackEnd); _endIsAuto = true; }     // the last confirmed day until the stored data says otherwise; no refresh while loading
            try
            {
                var loaded = (await _defaults.LoadAsync().ConfigureAwait(true)).Settings;
                if (_disposed) return;
                InitialCapitalText = Format(loaded.InitialCapital); CommissionFlatText = Format(loaded.CommissionFlat);
                CommissionPerUnitText = Format(loaded.CommissionPerUnit); SlippageRatioText = Format(loaded.SlippageRatio);
                LiquidationPenaltyText = Format(loaded.LiquidationPenaltyRatio);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (!_disposed) { Status = "Unavailable"; TechnicalDetails = e.Message; }
            }
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(HasSources)); NotifyState();
        NotifyPartition();
        if (_transfer is not null) { _ = LoadStoredPanelsAsync(); _ = LoadComparableRunsAsync(); }
    }

    private static string? IconFor(TickerGroupNode node) => node switch
    {
        WatchlistNode => TickerGroupIconGeometries.Watchlist,
        PortfolioNode => TickerGroupIconGeometries.Portfolio,
        FilterNode => TickerGroupIconGeometries.Filter,
        _ => null,
    };

    private static string Format(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>The source a ticker-tree node stands for; the fixed Watchlists and Portfolios categories mean every profile of that kind.</summary>
    private static RlSourceDescriptor? Describe(TickerGroupNode node) => node switch
    {
        AllTickersNode => RlSourceDescriptor.AllTickers(),
        CategoryNode { Id: var id } when id == TickerListViewModel.WatchlistsCategoryId => RlSourceDescriptor.Watchlist(),
        CategoryNode { Id: var id } when id == TickerListViewModel.PortfoliosCategoryId => RlSourceDescriptor.Portfolio(),
        WatchlistNode => RlSourceDescriptor.Watchlist(node.Id),
        PortfolioNode => RlSourceDescriptor.Portfolio(node.Id),
        FilterNode => RlSourceDescriptor.SavedFilter(node.Id),
        CustomCategoryNode => RlSourceDescriptor.CustomCategory(node.Id),
        TickerListFolderNode => RlSourceDescriptor.ListFolder(node.Id),
        _ => null,
    };

    private void OptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortfolioSourceOption.IsSelected)) ScheduleRefresh();
    }

    // ---- change handlers: everything that needs the files re-checks, everything that is only a rule recomputes ----

    partial void OnStartDateChanged(string value)
    {
        OnPropertyChanged(nameof(StartDateAsDateTime));
        if (_applyingSuggestion) { NotifyInputs(); return; }          // the pipeline's own assignment: it continues with the check itself
        _suggestion = null; StartIsAuto = false;                       // a typed start is never overwritten silently
        OnPropertyChanged(nameof(StartMessage));
        NotifyInputs(); ScheduleRefresh();
    }
    partial void OnStartIsAutoChanged(bool value) { OnPropertyChanged(nameof(CanRecalculateStart)); OnPropertyChanged(nameof(PeriodMessage)); RecalculateStartCommand.NotifyCanExecuteChanged(); }
    partial void OnEndDateExclusiveChanged(string value)
    {
        OnPropertyChanged(nameof(EndDateExclusiveAsDateTime));
        if (_applyingEnd) { NotifyInputs(); return; }                  // the pipeline's own assignment: it continues with the check itself
        _endIsAuto = false;                                            // a typed end is never overwritten silently
        NotifyInputs(); ScheduleRefresh();
    }

    /// <summary>The last confirmed day (the exclusive end that includes yesterday's bar): the end until the stored data names the latest day.</summary>
    private DateOnly FallbackEnd => Today.AddDays(-1);

    private void WriteAutomaticEnd(DateOnly end)
    {
        string text = end.ToString(DateFormat, CultureInfo.InvariantCulture);
        if (EndDateExclusive == text) return;
        _applyingEnd = true;
        try { EndDateExclusive = text; }
        finally { _applyingEnd = false; }
    }
    partial void OnUseShortHistoryChanged(bool value) { NotifyModeBadges(); NotifyPartition(); NotifyInputs(); ScheduleRefresh(); RecheckStoredPanel(); }
    partial void OnSmokeOnlyChanged(bool value) => NotifyModeBadges();
    private void NotifyModeBadges() { OnPropertyChanged(nameof(ModeBadges)); OnPropertyChanged(nameof(HasModeBadges)); }
    partial void OnPartitionModeChanged(RlPortfolioPartitionMode value) => PlanInputChanged();
    partial void OnMinimumTrainBarsTextChanged(string value) => PlanInputChanged();
    partial void OnMinimumValidationBarsTextChanged(string value) => PlanInputChanged();
    partial void OnMinimumTestBarsTextChanged(string value) => PlanInputChanged();
    partial void OnSymbolHoldoutChanged(bool value) { OnPropertyChanged(nameof(HoldoutPercentEditable)); NotifyModeBadges(); ScheduleRefresh(); }
    partial void OnHoldoutPercentTextChanged(string value) { NotifyInputs(); ScheduleRefresh(); }
    partial void OnMaxWeightPercentTextChanged(string value)
    {
        NotifyInputs();
        UpdatePortfolioWeights();
    }
    partial void OnExcludeUnavailableChanged(bool value) => ApplyDecision();
    partial void OnInitialCapitalTextChanged(string value) => NotifyInputs();
    partial void OnCommissionFlatTextChanged(string value) => NotifyInputs();
    partial void OnCommissionPerUnitTextChanged(string value) => NotifyInputs();
    partial void OnSlippageRatioTextChanged(string value) => NotifyInputs();
    partial void OnLiquidationPenaltyTextChanged(string value) => NotifyInputs();
    partial void OnPreflightStateChanged(RlDialogLoadState value) => NotifyState();
    partial void OnIsBusyChanged(bool value) { NotifyState(); NotifyStoredPanel(); NotifyComparison(); }
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusDisplay));
    partial void OnRunKindChanged(PortfolioRunKind value)
    {
        NotifyModeBadges();
        NotifyTransfer();
        if (value != PortfolioRunKind.Scratch) _ = LoadBaseRunsAsync();
        RecomputeAdmission();
    }
    partial void OnSelectedBaseRunChanged(PortfolioBaseRunRow? value) { NotifyTransfer(); RecomputeAdmission(); }
    partial void OnSelectedStoredPanelChanged(PortfolioStoredPanelRow? value) => RecheckStoredPanel();
    partial void OnSelectedRunAChanged(PortfolioComparableRunRow? value)
    {
        RenderReferenceRuns();
        SelectedRunB = null;
        ClearComparison();
    }
    partial void OnSelectedRunBChanged(PortfolioComparableRunRow? value) { ClearComparison(); }

    /// <summary>A change of the method or of a minimum: the check is repeated under the new plan (an invalid input only clears it).</summary>
    private void PlanInputChanged() { NotifyPartition(); NotifyInputs(); ScheduleRefresh(); RecheckStoredPanel(); }

    private void NotifyInputs()
    {
        OnPropertyChanged(nameof(PeriodValid)); OnPropertyChanged(nameof(PeriodMessage));
        OnPropertyChanged(nameof(CostsValid)); OnPropertyChanged(nameof(CostsMessage));
        OnPropertyChanged(nameof(PercentsValid)); OnPropertyChanged(nameof(PercentMessage)); NotifyState();
    }

    [RelayCommand(CanExecute = nameof(CanRecalculateStart))]
    private void RecalculateStart() { StartIsAuto = true; ScheduleRefresh(); }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanRecalculateStart)); RecalculateStartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(Editable)); OnPropertyChanged(nameof(HasUnavailableSymbols)); OnPropertyChanged(nameof(HasSymbols));
        OnPropertyChanged(nameof(StartBlockerDisplay)); OnPropertyChanged(nameof(HasStartBlocker));
        OnPropertyChanged(nameof(PartitionEditable)); OnPropertyChanged(nameof(BaseSelectionEditable)); OnPropertyChanged(nameof(HoldoutPercentEditable));
        StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }

    private void LocaleChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(PeriodMessage)); OnPropertyChanged(nameof(CostsMessage)); OnPropertyChanged(nameof(PercentMessage));
        OnPropertyChanged(nameof(StartMessage));
        OnPropertyChanged(nameof(StartBlockerDisplay)); OnPropertyChanged(nameof(HasStartBlocker)); NotifyModeBadges();
        NotifyPartition();
        RenderBaseRuns();
        if (_transfer is not null)
        {
            RenderStoredPanels(); RenderComparableRuns();
            ComparisonLines.Clear();
            if (_comparison is not null) foreach (string line in PortfolioRlPresentation.Comparison(_comparison, _localization)) ComparisonLines.Add(line);
            OnPropertyChanged(nameof(StoredPanelMessage)); NotifyComparison();
        }
        if (_preflight is not null) Publish(); else PreflightText = _localization.GetString("Prl.Preflight.Idle");
    }

    // ---- preflight ----

    private void ScheduleRefresh()
    {
        if (_disposed || _loading) return;
        _refreshCts?.Cancel();
        var cancel = new CancellationTokenSource();
        _refreshCts = cancel;
        long revision = Interlocked.Increment(ref _revision);
        _decision = null; BlockerText = "";
        RecomputeAdmission();               // the data check it waits for is outdated
        _ = RefreshAsync(revision, cancel);
    }

    /// <summary>One check of one selection. Only the newest revision may publish; an older one is dropped silently (it is not an error).</summary>
    private async Task RefreshAsync(long revision, CancellationTokenSource cancel)
    {
        var token = cancel.Token;
        var selected = Sources.Where(s => s.IsSelected).Select(s => s.Descriptor).ToArray();
        bool holdout = SymbolHoldout;
        int? holdoutPercent = HoldoutPercentOverride;
        var mode = UseShortHistory ? RlPanelEvaluationMode.ShortHistory : RlPanelEvaluationMode.Standard;
        try
        {
            // An automatic start is computed from the ticked lists, so it only needs a valid end; a typed start needs a valid period.
            bool automatic = StartIsAuto;
            // An invalid minimum-bars input has its own message and starts no check at all.
            if (!TryPlan(out var plan) || !PercentsValid) { if (revision == Interlocked.Read(ref _revision)) Clear(""); return; }
            if (selected.Length == 0 || !TryEnd(out var end) || (!automatic && !TryPeriod(out _, out _)))
            {
                if (revision != Interlocked.Read(ref _revision)) return;
                Clear(selected.Length == 0 ? _localization.GetString("Prl.Preflight.Idle") : "");
                return;
            }
            PreflightState = RlDialogLoadState.Loading;
            PreflightText = _localization.GetString("Prl.Preflight.Loading");
            var resolution = await _resolver.ResolveAsync(selected, null, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (_endIsAuto && _periodAdvisor is not null)
            {
                // The end the window proposed follows the stored data of the ticked lists: the day after the latest final bar (never later than today),
                // or the last confirmed day when no stored data answers. The run's own end is refreshed before the start is suggested from it.
                var latest = await _periodAdvisor.LatestFinalizedDateAsync(resolution.Symbols, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (_disposed || revision != Interlocked.Read(ref _revision)) return;
                if (_endIsAuto)
                {
                    end = latest is { } stored ? DateOnly.FromDayNumber(Math.Min(stored.AddDays(1).DayNumber, Today.DayNumber)) : FallbackEnd;
                    WriteAutomaticEnd(end);
                }
            }
            if (automatic)
            {
                var suggestion = await _service.SuggestStartAsync(resolution.Symbols, end, mode, plan, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (_disposed || revision != Interlocked.Read(ref _revision)) return;
                ApplySuggestion(suggestion, end);
                if (suggestion is { Status: RlStartSuggestionStatus.NoUsableData })
                {
                    Clear(_localization.GetString("Prl.Preflight.Idle"));
                    return;
                }
            }
            if (!TryPeriod(out var start, out end))
            {
                Clear("");
                return;
            }
            var preflight = await _service.PreflightAsync(resolution.Symbols, start, end, holdout, mode, plan, holdoutPercent, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (_disposed || revision != Interlocked.Read(ref _revision)) return;
            _resolution = resolution; _preflight = preflight;
            Publish();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (_disposed || revision != Interlocked.Read(ref _revision)) return;
            Clear(string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Preflight.Failed"), e.Message));
            PreflightState = RlDialogLoadState.Failed;
        }
        finally { if (ReferenceEquals(_refreshCts, cancel)) _refreshCts = null; cancel.Dispose(); }
    }

    /// <summary>Writes the suggested start without scheduling a second check (the running one continues); null leaves the start as it is.</summary>
    private void ApplySuggestion(RlStartSuggestion? suggestion, DateOnly end)
    {
        _suggestion = suggestion; _suggestionEnd = end;
        if (suggestion is not null)
        {
            _applyingSuggestion = true;
            try { StartDate = suggestion.Start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? ""; }
            finally { _applyingSuggestion = false; }
        }
        OnPropertyChanged(nameof(StartMessage));
    }

    private void Clear(string text)
    {
        _preflight = null; _resolution = null; _decision = null;
        Symbols.Clear(); Cells.Clear(); Notices.Clear(); BlockerText = ""; PreflightText = text;
        PortfolioWeights.Clear(); PortfolioAllocationSummary = ""; OnPropertyChanged(nameof(HasPortfolioWeights));
        PreflightState = RlDialogLoadState.Unloaded; NotifyState();
        RecomputeAdmission();
    }

    /// <summary>Renders the stored preflight and the Start decision (also called after a locale change or an exclusion tick).</summary>
    private void Publish()
    {
        var preflight = _preflight!;
        Symbols.Clear();
        foreach (var s in preflight.Symbols)
            Symbols.Add(new PortfolioSymbolRow(s.Symbol, s.Status, PortfolioRlPresentation.SymbolStatus(s.Status, _localization), s.BarsInPeriod, s.RequiredBarsPerBlock,
                s.Status == RlPanelSymbolStatus.ReadError ? s.Detail : "",
                s.Status == RlPanelSymbolStatus.Ready ? s.Role : RlPanelSymbolRole.None,
                s.Status == RlPanelSymbolStatus.Ready ? PortfolioRlPresentation.Role(s.Role, _localization) : ""));
        Cells.Clear();
        foreach (string line in PortfolioRlPresentation.Cells(preflight, _localization)) Cells.Add(line);
        PreflightState = RlDialogLoadState.Loaded;
        ApplyDecision();
        RecomputeAdmission();
    }

    private void ApplyDecision()
    {
        if (_preflight is null) { PortfolioWeights.Clear(); PortfolioAllocationSummary = ""; OnPropertyChanged(nameof(HasPortfolioWeights)); NotifyState(); return; }
        var preflight = _preflight;
        _decision = RlPanelStartGate.Decide(preflight, ExcludeUnavailable, SymbolHoldout, RunSettings);
        string summary = PortfolioRlPresentation.Panel(preflight, _localization);
        PreflightText = preflight.Feasible ? summary + Environment.NewLine + _localization.GetString("Prl.Preflight.Sufficient")
            : summary + Environment.NewLine + PortfolioRlPresentation.Shortfall(preflight, _localization);
        BlockerText = PortfolioRlPresentation.Blocker(_decision.Blocker, RunSettings, _localization);
        Notices.Clear();
        int missing = _resolution?.Diagnostics.Length ?? 0;
        if (missing > 0) Notices.Add(string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Source.NotFound"), missing));
        if (_decision.HoldoutUnavailable) Notices.Add(_localization.GetString("Prl.Holdout.Unavailable"));
        if (_decision.SymbolHoldout && preflight.HoldoutSplitRisk) Notices.Add(_localization.GetString("Prl.Preflight.HoldoutRisk"));
        NotifyState();
        UpdatePortfolioWeights();
    }

    private void UpdatePortfolioWeights()
    {
        PortfolioWeights.Clear();
        if (_preflight is null)
        {
            PortfolioAllocationSummary = "";
            OnPropertyChanged(nameof(HasPortfolioWeights));
            return;
        }

        var readySymbols = Symbols.Where(s => s.Status == RlPanelSymbolStatus.Ready).ToList();
        if (readySymbols.Count == 0)
        {
            PortfolioAllocationSummary = "";
            OnPropertyChanged(nameof(HasPortfolioWeights));
            return;
        }

        int configuredMaxWeight = _service.Settings.MaxWeightPercent;
        int maxWeight = TryPercent(MaxWeightPercentText, RlPortfolioLimits.MaxWeightPercent, out int parsedMax) ? parsedMax : configuredMaxWeight;

        // Equal-weight baseline shown before training: the same Core definition the evaluation baselines use (capped at MaxWeightPercent).
        var tradable = new bool[readySymbols.Count];
        Array.Fill(tradable, true);
        var baseline = RlPortfolioWeights.EqualWeights(tradable, maxWeight);

        decimal totalAllocated = 0m;
        for (int i = 0; i < readySymbols.Count; i++)
        {
            var sym = readySymbols[i];
            decimal weight = baseline.Assets[i] * RlPercent.DecimalDivisor;
            totalAllocated += weight;
            PortfolioWeights.Add(new PortfolioWeightVisualRow(
                sym.Symbol,
                (double)weight,
                string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", weight),
                sym.RoleText));
        }

        PortfolioAllocationSummary = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Prl.Allocation.Summary"),
            readySymbols.Count,
            maxWeight,
            totalAllocated.ToString("0.0", CultureInfo.CurrentCulture));

        OnPropertyChanged(nameof(HasPortfolioWeights));
    }

    private string ResolveProgressDetails(RlTrainingProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.Message))
            return string.Empty;

        return progress.Message switch
        {
            "Fitting the fixed observation scaler on training windows" => _localization.GetString("Prl.Phase.Scaler.Fit"),
            "Copying the scaler and the weights of the base model" => _localization.GetString("Prl.Phase.Scaler.Copy"),
            "Recording the execution environment" => _localization.GetString("Prl.Phase.Dependencies"),
            "Evaluating the policy and the baselines on every complete block of each cell" => _localization.GetString("Prl.Phase.Evaluating"),
            _ => progress.Message
        };
    }

    // ---- run ----

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart || !TryCosts(out var costs) || !TryPlan(out var plan)) return;
        // The run is frozen from what the Start gate allowed, before any await: later edits cannot change it.
        // A stored panel decides the symbols, the period and the holdout itself; the entries above are then not used.
        var panelRow = UseStoredPanel ? SelectedStoredPanel : null;
        DateOnly start = default, end = default;
        if (panelRow is null && !TryPeriod(out start, out end)) return;
        var decision = panelRow is null ? _decision! : null;
        var kind = TransferActive ? RunKind : PortfolioRunKind.Scratch;
        var baseRun = kind == PortfolioRunKind.Scratch ? null : _admitted;
        if (kind != PortfolioRunKind.Scratch && (baseRun is null || SelectedBaseRun is null)) return;
        var firstValidation = _admittedValidation;
        var freeze = kind == PortfolioRunKind.FineTune ? (FreezeEncoder ? RlPortfolioFreeze.EncoderFrozen : RlPortfolioFreeze.None) : (RlPortfolioFreeze?)null;
        var request = panelRow is null
            ? new RlPortfolioRunRequest(decision!.TrainingSymbols, start, end, decision.SymbolHoldout,
                UseShortHistory ? RlPanelEvaluationMode.ShortHistory : RlPanelEvaluationMode.Standard, costs, SmokeOnly, plan,
                freeze is { } f ? new RlPortfolioTransferInit(baseRun!.RunId, f) : null, null, HoldoutPercentOverride, MaxWeightPercentOverride)
            : new RlPortfolioRunRequest(ImmutableArray<string>.Empty, panelRow.Start, panelRow.EndExclusive, panelRow.Holdout,
                UseShortHistory ? RlPanelEvaluationMode.ShortHistory : RlPanelEvaluationMode.Standard, costs, SmokeOnly, plan,
                freeze is { } g ? new RlPortfolioTransferInit(baseRun!.RunId, g) : null, panelRow.PanelId, null, MaxWeightPercentOverride);
        _refreshCts?.Cancel();
        using var cancel = new CancellationTokenSource();
        _runCts = cancel;
        IsBusy = true; Status = "Preparing"; Message = ""; TechnicalDetails = ""; ResultLines.Clear(); OnPropertyChanged(nameof(HasResult));
        try
        {
            var progress = new Progress<RlTrainingProgress>(p =>
            {
                if (_disposed || cancel.IsCancellationRequested) return;
                Status = p.State; TechnicalDetails = ResolveProgressDetails(p);
            });
            RlPortfolioEvaluationReport evaluation; string directory, panelDirectory;
            if (kind == PortfolioRunKind.EvaluateBase)
            {
                // Nothing is trained: the stored model is only scored on the new panel (the service captures it, admits the base again and publishes the evidence).
                Status = "Evaluating";
                var scored = await _transfer!.EvaluateStoredModelAsync(baseRun!.RunId, request, cancel.Token).ConfigureAwait(true);
                (evaluation, directory, panelDirectory) = (scored.Evaluation, scored.Directory, scored.PanelDirectory);
            }
            else
            {
                var trained = await _service.RunAsync(request, progress, cancel.Token).ConfigureAwait(true);
                (evaluation, directory, panelDirectory) = (trained.Evaluation, trained.Directory, trained.PanelDirectory);
            }
            if (_disposed) return;
            // A report only: which held-out symbols the base may have seen. Read from the two stored panel manifests off the UI thread; never gates or fails the run.
            var overlap = kind == PortfolioRunKind.Scratch ? null : await Task.Run(() => _transfer!.SymbolOverlap(baseRun!.RunId, panelDirectory)).ConfigureAwait(true);
            if (_disposed) return;
            foreach (string line in PortfolioRlPresentation.Result(evaluation, _localization)) ResultLines.Add(line);
            if (kind != PortfolioRunKind.Scratch)
                foreach (string line in PortfolioRlPresentation.Lineage(kind == PortfolioRunKind.FineTune ? RlPortfolioTransferContract.FineTuneKind : RlPortfolioTransferContract.ZeroShotKind,
                    baseRun!, firstValidation, freeze is { } named ? RlPortfolioFreezes.Name(named) : null, _localization, overlap)) ResultLines.Add(line);
            OnPropertyChanged(nameof(HasResult));
            Status = "Succeeded"; TechnicalDetails = "";
            Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Prl.Result.Saved"), directory);
            if (kind == PortfolioRunKind.FineTune) _ = LoadBaseRunsAsync();      // the new run holds a complete model: it is a base candidate from now on
            if (_transfer is not null) { _ = LoadStoredPanelsAsync(); _ = LoadComparableRunsAsync(); }      // a new capture is a stored panel now; the run has a report to compare
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception e)
        {
            if (!_disposed)
            {
                Status = RlSourceContract.IsInsufficientData(e) ? "InsufficientData" : "Failed";
                TechnicalDetails = e.Message;
            }
        }
        finally { _runCts = null; if (!_disposed) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        if (_runCts is { } cancel) { try { cancel.Cancel(); } catch (ObjectDisposedException) { } Status = "Cancelling"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Services.LocalizationManager.Instance.LocaleChanged -= LocaleChanged;
        Interlocked.Increment(ref _admissionRevision); Interlocked.Increment(ref _baseListRevision);
        Interlocked.Increment(ref _storedPanelRevision); Interlocked.Increment(ref _panelListRevision); Interlocked.Increment(ref _runListRevision);
        _refreshCts?.Cancel();
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var option in Sources) option.PropertyChanged -= OptionChanged;
    }
}
