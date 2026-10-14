using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

public enum RlDialogLoadState { Unsupported, Unloaded, Loading, Loaded, Failed }
/// <summary>Why Start is disabled, in the priority <see cref="RlTrainingDialogViewModel.CurrentStartBlocker"/> evaluates them.</summary>
public enum RlStartBlocker { None, SymbolMissing, StartDateMissing, EndDateMissing, DateOrderInvalid, CostsInvalid, DirectionModelMissing, RetentionInvalid, InputInvalid }
public enum RlDialogOperation { Idle, Training, InspectingCheckpoints, Replaying, Activating,
    SavingRetention, InspectingArtifacts, Cleaning, InspectingRecovery, Recovering, Disposed }
public sealed record RlNamedValue(string Name, string Value);
public sealed record RlPolicyOption(string Id, string Name);
public sealed record RlModelOption(RlGeneration Generation, string Name);
public sealed record RlBlockOption(RlEpisodeBlock Block, string Name);
public sealed record RlGenerationScatterPoint(
    string ModelId,
    decimal ValidationReturn,
    decimal OosReturn,
    string ValidationDisplay,
    string OosDisplay,
    string OverfitGapDisplay,
    bool IsSelected,
    string QualityBadge,
    string QualityBadgeColor)
{
    public global::Avalonia.Media.FontWeight SelectionFontWeight => IsSelected ? global::Avalonia.Media.FontWeight.Bold : global::Avalonia.Media.FontWeight.Normal;
}

public sealed partial class RlTrainingDialogViewModel
{
    private readonly HashSet<string> _invalidEditors = new(StringComparer.Ordinal);
    private RlRegistryCapabilities _capabilities;
    private readonly IDispatcherService? _dispatcher;
    private readonly RlDialogDataService _data;
    private CancellationTokenSource? _operationCts, _refreshCts, _blocksCts;
    private long _registryRevision;
    private bool _notifyingUi;
    private bool _rebuildingChoices;
    private string? _directionActive, _continuousActive;
    private bool _directionUnavailable, _continuousUnavailable;
    private RlArtifactPolicy? _savedPolicy;
    [ObservableProperty] private RlDialogOperation _operation;
    [ObservableProperty] private RlDialogLoadState _modelsState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogLoadState _blocksState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogLoadState _checkpointsState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogLoadState _artifactsState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogLoadState _recoveryState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private string _technicalDetails = "";
    [ObservableProperty] private int? _completedSteps;
    [ObservableProperty] private int _totalStepsTarget;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string _progressPhaseDisplay = "";
    public bool ShowProgressBar => IsTrainingInProgress || (CompletedSteps.HasValue && CompletedSteps.Value > 0);
    public bool IsTrainingInProgress => Operation == RlDialogOperation.Training;
    public string ProgressPercentDisplay => TotalStepsTarget > 0 ? $"{ProgressPercent:F1}%" : "";
    public ObservableCollection<RlGenerationScatterPoint> GenerationScatterPoints { get; } = new();
    public bool HasGenerationScatterPoints => GenerationScatterPoints.Count > 0;
    [ObservableProperty] private string _generationComparisonSummary = "";
    public ObservableCollection<RlNamedValue> VerificationFacts { get; } = new();
    public ObservableCollection<RlNamedValue> FixedConditions { get; } = new();
    public ObservableCollection<RlModelOption> ModelOptions { get; } = new();
    public ObservableCollection<RlModelOption> DirectionOptions { get; } = new();
    public RlModelOption? SelectedModelOption
    { get => ModelOptions.FirstOrDefault(g => g.Generation == SelectedGeneration); set { if (!_rebuildingChoices) SelectedGeneration = value?.Generation; } }
    public RlModelOption? SelectedDirectionOption
    { get => DirectionOptions.FirstOrDefault(g => g.Generation == SelectedDirectionGeneration); set { if (!_rebuildingChoices) SelectedDirectionGeneration = value?.Generation; } }
    public string RetentionDisplay => _localization.GetString(_savedPolicy is null ? "Rl.Ui.Load.Unloaded" : DraftPolicy() != _savedPolicy ? "Rl.Storage.Unsaved" : _savedPolicy.IsComplete ? "Rl.Storage.Ready" : "Rl.Storage.Incomplete");
    public bool Editable => !_disposed && !IsBusy;
    public bool RecoveryAvailable => _capabilities.Recovery;
    public bool MaintenanceAvailable => StorageAvailable || RecoveryAvailable;
    public bool HasModels => ModelsState == RlDialogLoadState.Loaded && Generations.Count > 0;
    public bool HasDirections => UseContinuousPolicy && ModelsState == RlDialogLoadState.Loaded && DirectionGenerations.Count > 0;
    public bool HasSelectedModel => ModelsState == RlDialogLoadState.Loaded && SelectedGeneration is not null
        && (SelectedGeneration.Target is RlFiles.Target or RlContinuousContract.Target) && Generations.Contains(SelectedGeneration);
    public bool HasSelectedDirection => UseContinuousPolicy && SelectedDirectionGeneration is not null;
    public bool HasBlocks => HasSelectedModel && BlocksState == RlDialogLoadState.Loaded && Blocks.Count > 0;
    public bool HasEvaluation => HasSelectedModel && !string.IsNullOrWhiteSpace(EvaluationReport);
    public string EvaluationStateDisplay => HasEvaluation ? _localization.GetString("Rl.EvaluationDetails") : _localization.GetString("Rl.EvaluationUnavailable");
    public bool HasCheckpoints => CheckpointAvailable && CheckpointsState == RlDialogLoadState.Loaded && CheckpointRows.Count > 0;
    public bool HasResumeSelection => HasCheckpoints && SelectedCheckpoint?.Review.Verified == true && CheckpointRows.Contains(SelectedCheckpoint);
    public bool HasAbandonSelection => HasCheckpoints && SelectedCheckpoint is not null && CheckpointRows.Contains(SelectedCheckpoint);
    public bool HasArtifacts => ArtifactsState == RlDialogLoadState.Loaded && ArtifactRows.Count > 0;
    public bool HasCleanupCandidates => _artifactPreview?.Entries.Any(e => e.Candidate) == true;
    public bool HasRecoveryCandidate => RecoveryState == RlDialogLoadState.Loaded && RecoveryPreview is { Unavailable: true, BackupModelId: not null };
    public bool ShowActivate => HasSelectedModel && SelectedGeneration!.ModelId != ActiveId;
    public bool ShowPause => CanPause;
    public bool ShowCancel => !_disposed && _operationCts is not null;
    public bool CanCancel => ShowCancel && !_operationCts!.IsCancellationRequested;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);
    public bool HasInputError => !ValidCostEditors || _loadedDefaults is not null && !ValidCosts()
        || StorageAvailable && !ValidRetentionEditors
        || IsDateOrderInvalid;
    public string InputStatusDisplay => _localization.GetString("Rl.Ui.Status.InvalidInput");
    public bool CanTrain => Editable && StartInputsValid();
    /// <summary>The input part of <see cref="CanTrain"/>: the cost editors, the frozen job draft (symbol, dates, costs, direction model) and, when storage is offered, the retention editors and policy.</summary>
    private bool StartInputsValid() => ValidCostEditors && ValidJobDraft()
        && (!StorageAvailable || ValidRetentionEditors && ValidPolicy());
    /// <summary>
    /// The first input that keeps Start disabled, in a fixed priority; <see cref="RlStartBlocker.None"/> exactly when the inputs allow a start. The reasons are
    /// the predicates <see cref="CanTrain"/> is made of, taken apart: nothing here changes what can start. A busy window is not asked (see <see cref="StartBlockerDisplay"/>).
    /// </summary>
    public RlStartBlocker CurrentStartBlocker()
    {
        if (StartInputsValid()) return RlStartBlocker.None;
        if (string.IsNullOrWhiteSpace(Symbol)) return RlStartBlocker.SymbolMissing;
        bool startOk = DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
        bool endOk = DateOnly.TryParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
        if (!startOk) return RlStartBlocker.StartDateMissing;
        if (!endOk) return RlStartBlocker.EndDateMissing;
        if (start >= end) return RlStartBlocker.DateOrderInvalid;
        if (!ValidCostEditors || !ValidCosts()) return RlStartBlocker.CostsInvalid;
        if (UseContinuousPolicy && (ModelsState != RlDialogLoadState.Loaded || SelectedDirectionGeneration is null || !DirectionGenerations.Contains(SelectedDirectionGeneration)))
            return RlStartBlocker.DirectionModelMissing;
        if (StorageAvailable && (!ValidRetentionEditors || !ValidPolicy())) return RlStartBlocker.RetentionInvalid;
        return RlStartBlocker.InputInvalid;
    }
    /// <summary>One short blocking reason for the footer; empty when a start is possible and while an operation runs (the run-status strip speaks then).</summary>
    public string StartBlockerDisplay => !Editable ? "" : CurrentStartBlocker() switch
    {
        RlStartBlocker.None => "",
        RlStartBlocker.DateOrderInvalid => _localization.GetString("Rl.Ui.Guide.InvalidDateOrder"),
        RlStartBlocker.InputInvalid => _localization.GetString("Rl.Ui.Status.InvalidInput"),
        var blocker => _localization.GetString("Rl.Ui.Start." + blocker),
    };
    public bool HasStartBlocker => StartBlockerDisplay.Length > 0;
    /// <summary>Short labels for the options that change what the result means and are ON: the trial run and (when offered) the short history. Empty in the default state.</summary>
    public IReadOnlyList<string> ModeBadges => RunModeBadges.Build(_localization, (SmokeOnly, "Rl.Badge.Smoke"), (ShowShortHistoryOption && UseShortHistory, "Rl.Badge.ShortHistory"), (UseContinuousPolicy, "Rl.Badge.Continuous"));
    public bool HasModeBadges => ModeBadges.Count > 0;
    partial void OnSmokeOnlyChanged(bool value) => NotifyModeBadges();
    partial void OnUseShortHistoryChanged(bool value) => NotifyModeBadges();
    private void NotifyModeBadges() { OnPropertyChanged(nameof(ModeBadges)); OnPropertyChanged(nameof(HasModeBadges)); }
    public bool IsDateOrderInvalid => DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
        && DateOnly.TryParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)
        && s >= e;
    public string DateValidationDisplay => _localization.GetString("Rl.Ui.Guide.InvalidDateOrder");
    public bool CanSaveRetention => Editable && StorageAvailable && ValidRetentionEditors && ValidPolicy();
    public bool CanInspectRecovery => Editable && RecoveryAvailable;
    public bool CanInspectArtifacts => Editable && StorageAvailable;
    public bool CanInspectCheckpoints => Editable && CheckpointAvailable;
    public bool CanActivate => Editable && ShowActivate && ModelsState == RlDialogLoadState.Loaded;
    private bool ValidCostEditors => !CostEditors.Any(_invalidEditors.Contains);
    private bool ValidRetentionEditors => !RetentionEditors.Any(_invalidEditors.Contains);
    private static readonly string[] CostEditors = { nameof(InitialCapital), nameof(CommissionFlat), nameof(CommissionPerUnit), nameof(SlippageRatio), nameof(LiquidationPenaltyRatio) };
    private static readonly string[] RetentionEditors = { nameof(StorageQuotaGiB), nameof(StorageMinimumFreeGiB), nameof(StorageFailedRetentionDays), nameof(StorageNonactiveDays), nameof(StorageKeepCount), nameof(StorageCheckpointKeepCount) };
    public string ProcessingStepsDisplay => _invalidProgress ? _localization.GetString("Rl.Ui.Load.Invalid")
        : (CompletedSteps.HasValue && TotalStepsTarget > 0)
            ? $"{CompletedSteps.Value:N0} / {TotalStepsTarget:N0} ({ProgressPercent:F1}%)"
            : (CompletedSteps?.ToString(CultureInfo.CurrentCulture) ?? _localization.GetString("Rl.Ui.Load.Empty"));
    private bool _invalidProgress;
    public string ModelsDisplay => LoadCaption(ModelsState, Generations.Count);
    public string DirectionsDisplay => LoadCaption(ModelsState, DirectionGenerations.Count);
    public string BlocksDisplay => LoadCaption(BlocksState, Blocks.Count);
    public string CheckpointsDisplay => LoadCaption(CheckpointsState, CheckpointRows.Count);
    public string ArtifactsDisplay => LoadCaption(ArtifactsState, ArtifactRows.Count);
    public string ActiveDisplay => ModelsState != RlDialogLoadState.Loaded ? LoadCaption(ModelsState, 0)
        : _localization.GetString(ActiveUnavailable ? "Rl.State.Unavailable" : ActiveId is null ? "Rl.Ui.Load.Empty" : "Rl.RecoveryHealthy");
    public bool HasActiveId => ModelsState == RlDialogLoadState.Loaded && ActiveId is not null;
    public bool ActiveUnavailable => SelectedGeneration?.Target == RlContinuousContract.Target || (SelectedGeneration is null && UseContinuousPolicy)
        ? _continuousUnavailable : _directionUnavailable;
    public string SelectedModelDisplay => SelectedGeneration is { } g ? RlUiDisplay.Generation(g, _localization) : "";
    private string LoadCaption(RlDialogLoadState state, int count) => RlUiDisplay.LoadCaption(state, count, _localization);
    private bool ValidCosts() { try { _ = Costs(); return true; } catch (Exception e) when (e is ArgumentException or OverflowException) { return false; } }
    private bool ValidPolicy() { try { DraftPolicy().Validate(); return true; } catch (Exception e) when (e is ArgumentException or OverflowException) { return false; } }
    private RlArtifactPolicy DraftPolicy() => new(StorageQuotaGiB, StorageMinimumFreeGiB, StorageFailedRetentionDays, StorageNonactiveDays, StorageKeepCount, StorageCheckpointKeepCount);
    private bool ValidJobDraft()
    {
        try
        {
            if (!DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                || UseContinuousPolicy && (ModelsState != RlDialogLoadState.Loaded || SelectedDirectionGeneration is null || !DirectionGenerations.Contains(SelectedDirectionGeneration))) return false;
            _ = RlTrainingValidation.Freeze(new(Symbol, start, end, Costs())); return true;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException) { return false; }
    }
    /// <summary>View-owned raw numeric editor validity prevents admission from a hidden last valid value.</summary>
    public void SetEditorValidity(string field, bool valid)
    {
        if (valid) _invalidEditors.Remove(field); else _invalidEditors.Add(field);
        NotifyUi();
    }
    private void BeginOperation(RlDialogOperation operation, CancellationTokenSource? cancel = null)
    { if (_disposed) { cancel?.Cancel(); return; } Operation = operation; _operationCts = cancel; IsBusy = true; NotifyUi(); }
    private void EndOperation() { _operationCts = null; if (!_disposed) { Operation = RlDialogOperation.Idle; IsBusy = false; NotifyUi(); } }
    private long NextRevision(ref long revision)
    {
        if (revision == long.MaxValue)
        {
            // Never reuse an identity while an older request can still publish.
            _disposed = true; Operation = RlDialogOperation.Disposed;
            _operationCts?.Cancel(); _refreshCts?.Cancel(); _blocksCts?.Cancel(); _jobCts?.Cancel();
            LocalizationManager.Instance.LocaleChanged -= LocaleChanged;
            revision = 0;
        }
        else ++revision;
        return revision;
    }
    private Task PublishAsync(Action publish)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) { publish(); return Task.CompletedTask; }
        return _dispatcher.PostAsync(() => { publish(); return Task.CompletedTask; });
    }
    private void NotifyUi()
    {
        if (_notifyingUi) return;
        _notifyingUi = true;
        try
        {
            foreach (string name in UiProperties) OnPropertyChanged(name);
            TrainCommand.NotifyCanExecuteChanged(); SaveArtifactSettingsCommand.NotifyCanExecuteChanged();
            ActivateCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
            InspectRecoveryCommand.NotifyCanExecuteChanged(); InspectArtifactsCommand.NotifyCanExecuteChanged(); InspectCheckpointsCommand.NotifyCanExecuteChanged();
            NotifyCheckpointCommands(); CleanupArtifactsCommand.NotifyCanExecuteChanged(); RecoverPointerCommand.NotifyCanExecuteChanged();
        }
        finally { _notifyingUi = false; }
    }
    private static readonly string[] UiProperties =
    {
        nameof(HasInputError), nameof(InputStatusDisplay), nameof(HasActiveId), nameof(EvaluationStateDisplay), nameof(SelectedModelOption), nameof(SelectedDirectionOption), nameof(ProcessingStepsDisplay), nameof(RetentionDisplay), nameof(StatusDisplay), nameof(Editable), nameof(RecoveryAvailable), nameof(MaintenanceAvailable), nameof(HasModels), nameof(HasDirections), nameof(HasSelectedModel),
        nameof(HasSelectedDirection), nameof(HasBlocks), nameof(HasEvaluation), nameof(HasCheckpoints), nameof(HasResumeSelection),
        nameof(HasAbandonSelection), nameof(HasArtifacts), nameof(HasCleanupCandidates), nameof(HasRecoveryCandidate), nameof(ShowActivate), nameof(ShowPause),
        nameof(ShowCancel), nameof(CanCancel), nameof(HasMessage), nameof(HasTechnicalDetails), nameof(CanTrain), nameof(StartBlockerDisplay), nameof(HasStartBlocker), nameof(ModeBadges), nameof(HasModeBadges), nameof(CanSaveRetention),
        nameof(CanInspectRecovery), nameof(CanInspectArtifacts), nameof(CanInspectCheckpoints), nameof(CanActivate), nameof(ModelsDisplay),
        nameof(DirectionsDisplay), nameof(BlocksDisplay), nameof(CheckpointsDisplay), nameof(ArtifactsDisplay), nameof(ActiveDisplay), nameof(ActiveUnavailable),
        nameof(SelectedModelDisplay), nameof(ActiveId), nameof(CheckpointAvailable), nameof(CanCleanupArtifacts), nameof(CanRecover),
        nameof(ShowShortHistoryOption), nameof(IsDateOrderInvalid), nameof(DateValidationDisplay), nameof(PeriodStatusDisplay), nameof(HasPeriodStatus),
        nameof(ShowProgressBar), nameof(IsTrainingInProgress), nameof(ProgressPercentDisplay), nameof(ProgressPhaseDisplay),
        nameof(ResumeTooltip), nameof(AbandonTooltip), nameof(CheckpointStatusBadgeDisplay), nameof(HasSelectedCheckpoint),
        nameof(CanConfirmResume), nameof(CanConfirmAbandon), nameof(ResumeConfirmTooltip), nameof(AbandonConfirmTooltip),
        nameof(InspectCheckpointsTooltip), nameof(RecoveryStatusBadgeDisplay),
        nameof(InspectRecoveryTooltip), nameof(RecoverPointerTooltip),
        nameof(HasGenerationScatterPoints), nameof(GenerationComparisonSummary)
    };
    // The daily frame reuses its existing localized training-timeframe name; a contract value without one is shown as its identifier.
    private string FixedTimeframeDisplay()
    {
        string contract = Convert.ToString(RlSourceContract.Acquisition["timeframe"], CultureInfo.InvariantCulture) ?? "";
        return Enum.TryParse(contract, out StockAnalyzer.Core.Models.TimeFrame frame) && frame == StockAnalyzer.Core.Models.TimeFrame.D1
            ? _localization.GetString("Enum_TrainingTimeframe_" + nameof(StockAnalyzer.Core.Models.Training.TrainingTimeframe.Daily)) : contract;
    }
    private void PopulateFacts()
    {
        VerificationFacts.Clear(); FixedConditions.Clear();
        // Fact identifiers are the versioned catalog's explicit ordinal IDs, not domain status numbers.
        foreach (int id in Enumerable.Range(1, 10).Concat(UseContinuousPolicy || SelectedGeneration?.Target == RlContinuousContract.Target ? Enumerable.Range(11, 7) : Array.Empty<int>()).Concat(Enumerable.Range(18, 7)))
            VerificationFacts.Add(new(_localization.GetString($"Rl.Ui.Fact.F{id:00}.Label"), _localization.GetString($"Rl.Ui.Fact.F{id:00}.Value")));
        VerificationFacts.Add(new(_localization.GetString("Rl.Ui.Field.SplitExclusion"), _localization.GetString("Rl.Ui.Value.Unverified")));
        void Condition(string key, object value) => FixedConditions.Add(new(_localization.GetString("Rl.Ui.Field." + key), Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""));
        Condition("FixedTimeframe", FixedTimeframeDisplay()); Condition("FixedWindow", RlContract.WindowLength); Condition("FixedHorizon", RlContract.EpisodeHorizon);
        Condition("FixedMargin", RlContract.InitialMarginRatio);
        Condition("FixedMaintenanceMargin", StockAnalyzer.Core.Models.Backtest.Engine.BacktestDefaults.MaintenanceMarginRatio);
        Condition("Discount", RlContract.Gamma); Condition("CostFreezeTime", _localization.GetString("Rl.Ui.Value.RunStart"));
        if (!UseContinuousPolicy) Condition("FixedQuantity", RlContract.FixedQuantity);
        else
        {
            var lower = RlContinuousContract.Map(RlContinuousContract.ActionMinimum, RlContinuousContract.ActionMinimum);
            var upper = RlContinuousContract.Map(RlContinuousContract.ActionMaximum, RlContinuousContract.ActionMaximum);
            Condition("EntryBudgetRange", $"{lower.SizeRatio * 100m}–{upper.SizeRatio * 100m}");
            Condition("StopDistanceRange", $"{lower.StopDistance * 100m}–{upper.StopDistance * 100m}");
        }
    }
    private void RebuildChoices()
    {
        _rebuildingChoices = true;
        try
        {
            ModelOptions.Clear(); DirectionOptions.Clear();
            foreach (var generation in Generations)
            {
                var option = new RlModelOption(generation, RlUiDisplay.Target(generation.Target, _localization) + " · " + generation.Symbol + " · " + generation.ModelId);
                ModelOptions.Add(option);
                if (generation.Target == RlFiles.Target) DirectionOptions.Add(option);
            }
        }
        finally { _rebuildingChoices = false; }
        OnPropertyChanged(nameof(SelectedModelOption)); OnPropertyChanged(nameof(SelectedDirectionOption));
    }
    private void LocaleChanged(object? sender, EventArgs args)
    {
        _ = PublishAsync(() => { if (_disposed) return; PopulateFacts(); RebuildChoices(); RefreshEvaluationText(); RefreshReviewedText(); NotifyUi(); });
    }
}
