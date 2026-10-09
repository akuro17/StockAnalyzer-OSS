using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One operation owner; immutable requests and explicitly reviewed maintenance actions.</summary>
public sealed partial class RlTrainingDialogViewModel : ObservableObject, IDisposable
{
    private readonly IRlTrainingService _training;
    private readonly IRlModelGenerationRegistry _registry;
    private readonly IBacktestDefaultSettingsManager _defaults;
    private readonly ChartViewModel _chart;
    private readonly StockAnalyzer.Core.Interfaces.ILocalizationService _localization;
    private CancellationTokenSource? _jobCts, _replayCts;
    private long _replayRevision, _directionRevision, _recoveryRevision, _blocksRevision;
    private RlDialogBlocks? _blockEvidence;
    private bool _disposed;
    private BacktestDefaultSettings? _loadedDefaults;
    private static readonly ObservableCollection<string> s_emptyTickers = new();
    public ObservableCollection<RlGeneration> Generations { get; } = new();
    public ObservableCollection<RlGeneration> DirectionGenerations { get; } = new();
    public ObservableCollection<RlEpisodeBlock> Blocks { get; } = new();
    public ObservableCollection<string> AvailableTickers => _chart?.AvailableTickers ?? s_emptyTickers;
    public string[] Policies => SelectedGeneration is null ? Array.Empty<string>() : new[] { SelectedGeneration.Target }
        .Concat(new[] { RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId })
        .Concat(SelectedGeneration.Target == RlContinuousContract.Target ? new[] { RlContinuousContract.PairedBaselineId } : Array.Empty<string>()).ToArray();
    public string Disclosure => _localization.GetString("Rl.Disclosure");
    public string FixedContract => _localization.GetString(UseContinuousPolicy ? "Rl.ContinuousContract" : "Rl.FixedContract");
    public int MaximumDecision => RlContract.EpisodeHorizon - 1;
    public string? ActiveId => SelectedGeneration?.Target == RlContinuousContract.Target || (SelectedGeneration is null && UseContinuousPolicy)
        ? _continuousActive : _directionActive;
    [ObservableProperty] private bool _useContinuousPolicy;
    [ObservableProperty] private bool _useAllCompleteBlocks;
    [ObservableProperty] private bool _useShortHistory;
    /// <summary>Short-history evaluation belongs to the discrete T15 target; the continuous target keeps its own all-block option.</summary>
    public bool ShowShortHistoryOption => !UseContinuousPolicy;
    [ObservableProperty] private RlPointerRecovery? _recoveryPreview;
    [ObservableProperty] private string _recoveryDisplay = "";
    public bool CanRecover => Editable && HasRecoveryCandidate;
    [ObservableProperty] private RlGeneration? _selectedDirectionGeneration;
    [ObservableProperty] private string _directionTemporalDisplay = "";
    public string TemporalStatusDisplay => _localization.GetString("Rl.Temporal." +
        (SelectedGeneration is null ? "Unverified" : SelectedGeneration.TemporalSpecificationId is null ? "Legacy" : SelectedGeneration.TemporalStatus.ToString()));
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _startDate = "";
    [ObservableProperty] private string _endDateExclusive = "";

    public DateTime? StartDateAsDateTime
    {
        get => DateTime.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        set
        {
            var str = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            if (StartDate != str)
            {
                StartDate = str;
            }
            OnPropertyChanged(nameof(StartDateAsDateTime));
        }
    }

    public DateTime? EndDateExclusiveAsDateTime
    {
        get => DateTime.TryParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        set
        {
            var str = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            if (EndDateExclusive != str)
            {
                EndDateExclusive = str;
            }
            OnPropertyChanged(nameof(EndDateExclusiveAsDateTime));
        }
    }

    partial void OnStartDateChanged(string value) => OnPropertyChanged(nameof(StartDateAsDateTime));
    partial void OnEndDateExclusiveChanged(string value) => OnPropertyChanged(nameof(EndDateExclusiveAsDateTime));
    [ObservableProperty] private decimal _initialCapital;
    [ObservableProperty] private decimal _commissionFlat;
    [ObservableProperty] private decimal _commissionPerUnit;
    [ObservableProperty] private decimal _slippageRatio;
    [ObservableProperty] private decimal _liquidationPenaltyRatio;
    [ObservableProperty] private bool _smokeOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    public string StatusDisplay => _pauseCts?.IsCancellationRequested == true && Status != "Paused" && Status != "Cancelled" && Status != "Cancelling"
        ? _localization.GetString("Rl.Checkpoint.PausePending")
        : _localization.GetString(KnownStates.Contains(Status, StringComparer.Ordinal) ? "Rl.State." + Status : "Rl.Ui.Status.Processing");
    private static readonly string[] KnownStates = { "Ready", "Preparing", "Training", "Evaluating", "Qualifying", "Succeeded", "Failed", "Rejected", "Unavailable", "InsufficientData", "Cancelling", "Cancelled", "Paused" };
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private RlGeneration? _selectedGeneration;
    [ObservableProperty] private RlEpisodeBlock? _selectedBlock;
    [ObservableProperty] private string _selectedPolicy = RlFiles.Target;
    [ObservableProperty] private int _decision;
    [ObservableProperty] private string _evaluationReport = "";

    public RlTrainingDialogViewModel(IRlTrainingService training, IRlModelGenerationRegistry registry, IQValueService q,
        IBacktestDefaultSettingsManager defaults, ChartViewModel chart, StockAnalyzer.Core.Interfaces.ILocalizationService localization,
        IDispatcherService? dispatcher = null, RlDialogDataService? data = null)
    {
        _localization = localization; _training = training; _registry = registry; _defaults = defaults; _chart = chart;
        _dispatcher = dispatcher; _data = data ?? new(registry, q); _capabilities = registry.Capabilities; Symbol = chart.Symbol;
        StorageAvailable = _capabilities.Storage;
        if (!StorageAvailable) ArtifactsState = RlDialogLoadState.Unsupported;
        if (!CheckpointAvailable) CheckpointsState = RlDialogLoadState.Unsupported;
        if (!RecoveryAvailable) RecoveryState = RlDialogLoadState.Unsupported;
        PropertyChanged += (_, _) => NotifyUi();
        Services.LocalizationManager.Instance.LocaleChanged += LocaleChanged;
        PopulateFacts();
    }
    public async Task InitializeAsync()
    {
        try
        {
            var loaded = (await _defaults.LoadAsync()).Settings;
            await PublishAsync(() =>
            {
                if (_disposed) return;
                _loadedDefaults = loaded; InitialCapital = loaded.InitialCapital; CommissionFlat = loaded.CommissionFlat;
                CommissionPerUnit = loaded.CommissionPerUnit; SlippageRatio = loaded.SlippageRatio;
                LiquidationPenaltyRatio = loaded.LiquidationPenaltyRatio;
            });
        }
        catch (Exception e) { if (!_disposed) { Status = "Unavailable"; SetErrorMessage(e); } }
        if (_disposed) return;
        await RefreshGenerationsAsync();
        await InitializeArtifactSettingsAsync();
        if (CanInspectRecovery) await InspectRecoveryAsync();
    }
    partial void OnUseContinuousPolicyChanged(bool value)
    {
        if (_disposed) return;
        NextRevision(ref _recoveryRevision); RecoveryPreview = null; RecoveryDisplay = "";
        RecoveryState = RecoveryAvailable ? RlDialogLoadState.Unloaded : RlDialogLoadState.Unsupported;
        PopulateFacts(); OnPropertyChanged(nameof(FixedContract));
    }
    [RelayCommand(CanExecute = nameof(CanInspectRecovery))] private async Task InspectRecoveryAsync()
    {
        if (!CanInspectRecovery) return;
        long revision = NextRevision(ref _recoveryRevision);
        if (_disposed) return;
        string target = UseContinuousPolicy ? RlContinuousContract.Target : RlFiles.Target;
        using var cancel = new CancellationTokenSource(); BeginOperation(RlDialogOperation.InspectingRecovery, cancel);
        RecoveryPreview = null; RecoveryState = RlDialogLoadState.Loading;
        try
        {
            var preview = await Task.Run(() => _registry.PreviewRlRecovery(target, cancel.Token), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _recoveryRevision) return;
            if (preview is null) throw new InvalidDataException("Recovery inspection returned no result.");
            RecoveryPreview = preview; RecoveryState = RlDialogLoadState.Loaded;
            RecoveryDisplay = preview.Unavailable ? string.Format(CultureInfo.CurrentCulture,
                _localization.GetString(preview.BackupModelId is null ? "Rl.RecoveryNoBackup" : "Rl.RecoveryCandidate"), RlUiDisplay.Target(target, _localization), preview.BackupModelId)
                : _localization.GetString("Rl.RecoveryHealthy");
            if (preview.Unavailable) { Status = "Unavailable"; TechnicalDetails = preview.Error ?? ""; }
        }
        catch (OperationCanceledException) { if (!_disposed) RecoveryState = RlDialogLoadState.Unloaded; }
        catch (Exception e) { if (!_disposed && revision == _recoveryRevision) { RecoveryState = RlDialogLoadState.Failed; RecoveryDisplay = DescribeError(e); } }
        finally { EndOperation(); }
    }
    [RelayCommand(CanExecute = nameof(CanRecover))] private async Task RecoverPointerAsync()
    {
        if (!CanRecover) return;
        var reviewed = RecoveryPreview!; NextRevision(ref _recoveryRevision); RecoveryPreview = null; InvalidateArtifactReview();
        using var cancel = new CancellationTokenSource(); BeginOperation(RlDialogOperation.Recovering, cancel);
        try
        {
            await Task.Run(() => _registry.RecoverRlPointerAsync(reviewed, cancel.Token)); cancel.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Status = "Ready"; SetRecoveryNotice(reviewed.BackupModelId);
            await RefreshGenerationsAsync();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception e) { if (!_disposed) { Status = "Rejected"; SetErrorMessage(e); } }
        finally { EndOperation(); }
        if (CanInspectRecovery) await InspectRecoveryAsync();
    }
    private async Task RefreshGenerationsAsync()
    {
        _refreshCts?.Cancel(); using var cancel = new CancellationTokenSource(); _refreshCts = cancel;
        long revision = NextRevision(ref _registryRevision); ModelsState = RlDialogLoadState.Loading;
        if (_disposed) { _refreshCts = null; return; }
        try
        {
            var snapshot = await Task.Run(() => _registry.CaptureRlSnapshot(cancel.Token), cancel.Token);
            if (snapshot.Generations.IsDefault) throw new InvalidDataException("Registry returned an uninitialized collection.");
            await PublishAsync(() =>
            {
                if (_disposed || revision != _registryRevision || cancel.IsCancellationRequested) return;
                string? directionId = SelectedDirectionGeneration?.ModelId, selectedId = SelectedGeneration?.ModelId;
                _capabilities = snapshot.Capabilities; StorageAvailable = _capabilities.Storage;
                if (!StorageAvailable) ArtifactsState = RlDialogLoadState.Unsupported;
                if (!CheckpointAvailable) CheckpointsState = RlDialogLoadState.Unsupported;
                if (!RecoveryAvailable) RecoveryState = RlDialogLoadState.Unsupported;
                _directionActive = snapshot.DirectionActiveId; _continuousActive = snapshot.ContinuousActiveId;
                _directionUnavailable = snapshot.DirectionUnavailable; _continuousUnavailable = snapshot.ContinuousUnavailable;
                Generations.Clear(); DirectionGenerations.Clear();
                foreach (var g in snapshot.Generations) { Generations.Add(g); if (g.Target == RlFiles.Target) DirectionGenerations.Add(g); }
                ModelsState = RlDialogLoadState.Loaded;
                SelectedDirectionGeneration = DirectionGenerations.FirstOrDefault(g => g.ModelId == directionId);
                SelectedGeneration = Generations.FirstOrDefault(g => g.ModelId == selectedId); RebuildChoices(); NotifyUi();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            await PublishAsync(() => { if (_disposed || revision != _registryRevision) return; ModelsState = RlDialogLoadState.Failed;
                SelectedGeneration = null; SelectedDirectionGeneration = null; SetErrorMessage(e); });
        }
        finally { if (ReferenceEquals(_refreshCts, cancel)) _refreshCts = null; }
    }
    private BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
    { SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter, InitialCapital = InitialCapital,
      CommissionFlat = CommissionFlat, CommissionPerUnit = CommissionPerUnit, SlippageRatio = SlippageRatio, LiquidationPenaltyRatio = LiquidationPenaltyRatio });
    [RelayCommand(CanExecute = nameof(CanSaveCosts))] private async Task SaveCostsAsync()
    {
        if (!CanSaveCosts) return;
        var node = JsonSerializer.SerializeToNode(_loadedDefaults!)!;
        node[nameof(BacktestDefaultSettings.InitialCapital)] = InitialCapital; node[nameof(BacktestDefaultSettings.CommissionFlat)] = CommissionFlat;
        node[nameof(BacktestDefaultSettings.CommissionPerUnit)] = CommissionPerUnit; node[nameof(BacktestDefaultSettings.SlippageRatio)] = SlippageRatio;
        node[nameof(BacktestDefaultSettings.LiquidationPenaltyRatio)] = LiquidationPenaltyRatio;
        var saved = node.Deserialize<BacktestDefaultSettings>()!; BeginOperation(RlDialogOperation.SavingCosts);
        try { await _defaults.SaveAsync(saved); if (!_disposed) { _loadedDefaults = saved; Message = _localization.GetString("Rl.CostsSaved"); } }
        catch (Exception e) { if (!_disposed) { Status = "Rejected"; SetErrorMessage(e); } }
        finally { EndOperation(); }
    }
    [RelayCommand(CanExecute = nameof(CanTrain))] private async Task TrainAsync()
    {
        if (!CanTrain) return;
        var start = DateOnly.ParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var job = RlTrainingValidation.Freeze(new RlTrainingJob(Symbol, start, end, Costs(), SmokeOnly: SmokeOnly)
        { FrozenDirectionModelId = UseContinuousPolicy ? SelectedDirectionGeneration!.ModelId : null,
          EvaluationSpecificationId = UseContinuousPolicy && UseAllCompleteBlocks ? RlAllBlockEvaluationContract.SpecificationId : null,
          ShortHistorySpecificationId = !UseContinuousPolicy && UseShortHistory ? RlShortHistoryEvaluationContract.SpecificationId : null });
        using var cancel = new CancellationTokenSource(); using var pause = new CancellationTokenSource();
        _jobCts = cancel; _pauseCts = pause; BeginOperation(RlDialogOperation.Training, cancel); Status = "Preparing"; CompletedSteps = null;
        InvalidateReplay(); InvalidateArtifactReview();
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            var progress = new Progress<RlTrainingProgress>(p => ApplyProgress(p, cancel));
            var result = job.FrozenDirectionModelId is not null && _training is IRlResumableTrainingService resumable
                ? await Task.Run(() => resumable.RunResumableAsync(job, pause.Token, progress, cancel.Token))
                : await Task.Run(() => _training.RunAsync(job, progress, cancel.Token));
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Status = "Succeeded"; Message = _localization.GetString(result.SmokeOnly ? "Rl.SmokeCompleted" : "Rl.State.Succeeded");
            await RefreshGenerationsAsync();
        }
        catch (RlTrainingPausedException) { if (!_disposed) { Status = RlCheckpointContract.PauseState; Message = _localization.GetString("Rl.Checkpoint.Paused"); } }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception e) { if (!_disposed) { Status = e is RlTemporalException or ArgumentException ? "Rejected" : e is FileNotFoundException ? "Unavailable" : RlSourceContract.IsInsufficientData(e) ? "InsufficientData" : "Failed"; SetErrorMessage(e); } }
        finally { _jobCts = null; _pauseCts = null; EndOperation(); }
        if (!_disposed && Status == RlCheckpointContract.PauseState && CanInspectCheckpoints) await InspectCheckpointsAsync();
    }
    private void ApplyProgress(RlTrainingProgress progress, CancellationTokenSource owner)
    {
        _ = PublishAsync(() =>
        {
            if (_disposed || !ReferenceEquals(_jobCts, owner) || owner.IsCancellationRequested) return;
            Status = progress.State; TechnicalDetails = progress.Message;
            _invalidProgress = progress.CompletedSteps < 0;
            CompletedSteps = progress.CompletedSteps >= 0 ? progress.CompletedSteps : null;
            if (_pauseCts?.IsCancellationRequested != true) Message = "";
        });
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel()
    { if (!CanCancel) return; Status = "Cancelling"; _operationCts!.Cancel(); NotifyUi(); }
    [RelayCommand(CanExecute = nameof(CanActivate))] private async Task ActivateAsync()
    {
        if (!CanActivate) return;
        string modelId = SelectedGeneration!.ModelId; InvalidateArtifactReview(); BeginOperation(RlDialogOperation.Activating);
        try { bool activated = await Task.Run(() => _registry.ActivateRlAsync(modelId)); if (!_disposed) { Status = activated ? "Ready" : "Rejected"; await RefreshGenerationsAsync(); } }
        catch (Exception e) { if (!_disposed) { Status = "Rejected"; SetErrorMessage(e); } }
        finally { EndOperation(); }
    }
    partial void OnSelectedGenerationChanged(RlGeneration? value)
    {
        if (_disposed) return;
        InvalidateReplay(); NextRevision(ref _blocksRevision); _blocksCts?.Cancel(); Blocks.Clear(); _blockEvidence = null; SelectedBlock = null; EvaluationReport = ""; BlocksState = RlDialogLoadState.Unloaded; RebuildChoices();
        SelectedPolicy = value?.Target ?? RlFiles.Target; RebuildPolicyOptions(); PopulateFacts();
        OnPropertyChanged(nameof(Policies)); OnPropertyChanged(nameof(TemporalStatusDisplay));
        if (value is not null && !_disposed) _ = LoadBlocksAsync(value, _blocksRevision);
    }
    partial void OnSelectedDirectionGenerationChanged(RlGeneration? value)
    {
        if (_disposed) return;
        long revision = NextRevision(ref _directionRevision); _directionEvidence = null; DirectionTemporalDisplay = DirectionEvidenceText();
        if (value is not null && !_disposed) _ = LoadDirectionTemporalAsync(value.ModelId, revision);
    }
    private async Task LoadDirectionTemporalAsync(string modelId, long revision)
    {
        try
        {
            var evidence = await Task.Run(() => _registry.CaptureRlTemporalEvidence(modelId));
            await PublishAsync(() =>
            {
                if (_disposed || revision != _directionRevision || SelectedDirectionGeneration?.ModelId != modelId) return;
                _directionEvidence = evidence; DirectionTemporalDisplay = DirectionEvidenceText();
            });
        }
        catch (Exception e) { await PublishAsync(() => { if (!_disposed && revision == _directionRevision) DirectionTemporalDisplay = DescribeError(e); }); }
    }
    private string DescribeError(Exception error)
    {
        TechnicalDetails = error.ToString();
        if (error is RlTemporalException temporal)
        {
            string text = _localization.GetString("Rl.Temporal.Rejection." + temporal.Reason);
            return temporal.Reason == RlTemporalRejection.FutureInformation ? string.Format(CultureInfo.CurrentCulture, text,
                temporal.InformationUpperExclusive?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), temporal.DecisionExchangeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : text;
        }
        return _localization.GetString(error switch
        {
            ArgumentException or OverflowException => "Rl.Ui.Status.InvalidInput", FileNotFoundException => "Rl.Ui.Status.DataUnavailable",
            UnauthorizedAccessException => "Rl.Ui.Status.AccessDenied", InvalidDataException when RlSourceContract.IsInsufficientData(error) => "Rl.Ui.Status.InsufficientData",
            InvalidDataException or JsonException => "Rl.Ui.Status.InvalidData",
            IOException => "Rl.Ui.Status.IOFailure", TimeoutException => "Rl.Ui.Status.Timeout", _ => "Rl.Ui.Status.OperationFailed"
        });
    }
    partial void OnSelectedBlockChanged(RlEpisodeBlock? value) => InvalidateReplay();
    partial void OnSelectedPolicyChanged(string value) { InvalidateReplay(); OnPropertyChanged(nameof(SelectedPolicyOption)); }
    partial void OnDecisionChanged(int value) => InvalidateReplay();
    private void InvalidateReplay() { if (_disposed) return; NextRevision(ref _replayRevision); if (_disposed) return; _replayCts?.Cancel(); _chart.ClearRlPresentation(); }
    private async Task LoadBlocksAsync(RlGeneration generation, long revision)
    {
        using var cancel = new CancellationTokenSource(); _blocksCts = cancel; BlocksState = RlDialogLoadState.Loading;
        try
        {
            var result = await _data.LoadBlocksAsync(generation.ModelId, cancel.Token);
            await PublishAsync(() =>
            {
                if (_disposed || revision != _blocksRevision || cancel.IsCancellationRequested || SelectedGeneration?.ModelId != generation.ModelId) return;
                if (result.Blocks.IsDefault) throw new InvalidDataException("Uninitialized replay blocks.");
                _blockEvidence = result; RefreshEvaluationText();
                foreach (var block in result.Blocks) Blocks.Add(block);
                BlocksState = RlDialogLoadState.Loaded; RebuildChoices(); NotifyUi();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { await PublishAsync(() => { if (!_disposed && revision == _blocksRevision) { BlocksState = RlDialogLoadState.Failed; SetErrorMessage(e); } }); }
        finally { if (ReferenceEquals(_blocksCts, cancel)) _blocksCts = null; }
    }
    [RelayCommand(CanExecute = nameof(CanReplay))] private async Task ReplayAsync()
    {
        if (!CanReplay) return;
        string modelId = SelectedGeneration!.ModelId, blockId = SelectedBlock!.Id, policyId = SelectedPolicy; int decision = Decision;
        InvalidateReplay(); long revision = _replayRevision;
        using var cancel = new CancellationTokenSource(); _replayCts = cancel; BeginOperation(RlDialogOperation.Replaying, cancel);
        try
        {
            var result = await _data.ReplayAsync(modelId, blockId, decision, policyId, cancel.Token); cancel.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _replayRevision) return;
            await _chart.ApplyRlReplayAsync(result, _localization, cancel.Token);
            if (!_disposed && revision == _replayRevision) { Status = "Ready"; Message = ""; }
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception e) { if (!_disposed && revision == _replayRevision) { Status = "Unavailable"; SetErrorMessage(e); } }
        finally { if (ReferenceEquals(_replayCts, cancel)) _replayCts = null; EndOperation(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Operation = RlDialogOperation.Disposed;
        NextRevision(ref _checkpointRevision); NextRevision(ref _artifactRevision); NextRevision(ref _directionRevision); NextRevision(ref _recoveryRevision); NextRevision(ref _registryRevision); NextRevision(ref _replayRevision); NextRevision(ref _blocksRevision);
        _operationCts?.Cancel(); _refreshCts?.Cancel(); _blocksCts?.Cancel(); _jobCts?.Cancel(); _replayCts?.Cancel();
        Services.LocalizationManager.Instance.LocaleChanged -= LocaleChanged; NotifyUi();
        // Already published replay remains owned by the chart.
    }
    private void RefreshEvaluationText()
    {
        EvaluationReport = _blockEvidence?.Constraints is { } report ? RlPresentation.FormatEvaluation(report, _localization) : "";
        if (_blockEvidence?.Expanded is { } expanded) EvaluationReport = RlPresentation.FormatAllBlocks(expanded, _localization) + Environment.NewLine + EvaluationReport;
        if (_blockEvidence?.ShortHistory is { } shortHistory) EvaluationReport = RlPresentation.FormatShortHistory(shortHistory, _localization) + Environment.NewLine + EvaluationReport;
    }
}
