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
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One frozen job and one cancellable replay request; qualified generations activate only manually.</summary>
public sealed partial class RlTrainingDialogViewModel : ObservableObject, IDisposable
{
    private readonly IRlTrainingService _training;
    private readonly IRlModelGenerationRegistry _registry;
    private readonly IQValueService _q;
    private readonly IBacktestDefaultSettingsManager _defaults;
    private readonly ChartViewModel _chart;
    private readonly StockAnalyzer.Core.Interfaces.ILocalizationService _localization;
    private CancellationTokenSource? _jobCts, _replayCts;
    private long _replayRevision;
    private long _directionRevision;
    private long _recoveryRevision;
    private CancellationTokenSource? _recoveryCts;
    private bool _disposed;
    private BacktestDefaultSettings? _loadedDefaults;
    public ObservableCollection<RlGeneration> Generations { get; } = new();
    public ObservableCollection<RlGeneration> DirectionGenerations { get; } = new();
    public ObservableCollection<RlEpisodeBlock> Blocks { get; } = new();
    public string[] Policies => new[] { SelectedGeneration?.Target ?? RlFiles.Target }.Concat(new[] { RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId })
        .Concat(SelectedGeneration?.Target == RlContinuousContract.Target ? new[] { RlContinuousContract.PairedBaselineId } : Array.Empty<string>()).ToArray();
    public string Disclosure => _localization.GetString("Rl.Disclosure") + (UseContinuousPolicy || SelectedGeneration?.Target == RlContinuousContract.Target
        ? " " + _localization.GetString("Rl.ContinuousDisclosure") : "");
    public string FixedContract => _localization.GetString(UseContinuousPolicy ? "Rl.ContinuousContract" : "Rl.FixedContract");
    public int MaximumDecision => RlContract.EpisodeHorizon - 1;
    public string? ActiveId => SelectedGeneration?.Target == RlContinuousContract.Target || (SelectedGeneration is null && UseContinuousPolicy)
        ? _registry.RlContinuousActiveId : _registry.RlActiveId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Disclosure)), NotifyPropertyChangedFor(nameof(ActiveId)), NotifyPropertyChangedFor(nameof(FixedContract))] private bool _useContinuousPolicy;
    [ObservableProperty] private bool _useAllCompleteBlocks;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRecover)), NotifyCanExecuteChangedFor(nameof(RecoverPointerCommand))] private RlPointerRecovery? _recoveryPreview;
    [ObservableProperty] private string _recoveryDisplay = "";
    public bool CanRecover => !IsBusy && RecoveryPreview is { Unavailable: true, BackupModelId: not null };
    [ObservableProperty] private RlGeneration? _selectedDirectionGeneration;
    [ObservableProperty] private string _directionTemporalDisplay = "";
    public string TemporalStatusDisplay => _localization.GetString("Rl.Temporal." +
        (SelectedGeneration is null ? "Unverified" : SelectedGeneration.TemporalSpecificationId is null ? "Legacy" : SelectedGeneration.TemporalStatus.ToString()));
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _startDate = "";
    [ObservableProperty] private string _endDateExclusive = "";
    [ObservableProperty] private decimal _initialCapital;
    [ObservableProperty] private decimal _commissionFlat;
    [ObservableProperty] private decimal _commissionPerUnit;
    [ObservableProperty] private decimal _slippageRatio;
    [ObservableProperty] private decimal _liquidationPenaltyRatio;
    [ObservableProperty] private bool _smokeOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRecover)), NotifyPropertyChangedFor(nameof(CanCleanupArtifacts)),
        NotifyPropertyChangedFor(nameof(CanPause)), NotifyPropertyChangedFor(nameof(CanResumeCheckpoint)), NotifyPropertyChangedFor(nameof(CanAbandonCheckpoint)),
        NotifyCanExecuteChangedFor(nameof(PauseCommand)), NotifyCanExecuteChangedFor(nameof(ResumeCheckpointCommand)), NotifyCanExecuteChangedFor(nameof(AbandonCheckpointCommand)),
        NotifyCanExecuteChangedFor(nameof(RecoverPointerCommand)), NotifyCanExecuteChangedFor(nameof(CleanupArtifactsCommand))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusDisplay))] private string _status = "Ready";
    public string StatusDisplay => _localization.GetString("Rl.State." + Status);
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private RlGeneration? _selectedGeneration;
    [ObservableProperty] private RlEpisodeBlock? _selectedBlock;
    [ObservableProperty] private string _selectedPolicy = RlFiles.Target;
    [ObservableProperty] private int _decision;
    [ObservableProperty] private string _evaluationReport = "";

    public RlTrainingDialogViewModel(IRlTrainingService training, IRlModelGenerationRegistry registry, IQValueService q,
        IBacktestDefaultSettingsManager defaults, ChartViewModel chart, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    { _localization = localization; _training = training; _registry = registry; _q = q; _defaults = defaults; _chart = chart; Symbol = chart.Symbol; }

    public async Task InitializeAsync()
    {
        _loadedDefaults = (await _defaults.LoadAsync()).Settings;
        if (_disposed) return;
        InitialCapital = _loadedDefaults.InitialCapital; CommissionFlat = _loadedDefaults.CommissionFlat;
        CommissionPerUnit = _loadedDefaults.CommissionPerUnit; SlippageRatio = _loadedDefaults.SlippageRatio;
        LiquidationPenaltyRatio = _loadedDefaults.LiquidationPenaltyRatio;
        RefreshGenerations();
        await InitializeArtifactSettingsAsync();
        await InspectRecoveryAsync();
    }
    partial void OnUseContinuousPolicyChanged(bool value)
    {
        ++_recoveryRevision; RecoveryPreview = null; RecoveryDisplay = "";
        if (!_disposed) _ = InspectRecoveryAsync();
    }
    [RelayCommand] private async Task InspectRecoveryAsync()
    {
        if (_disposed || IsBusy) return;
        long revision = ++_recoveryRevision;
        string target = UseContinuousPolicy ? RlContinuousContract.Target : RlFiles.Target;
        try
        {
            var preview = await Task.Run(() => _registry.PreviewRlRecovery(target));
            if (_disposed || revision != _recoveryRevision) return;
            RecoveryPreview = preview;
            RecoveryDisplay = preview is { Unavailable: true } ? string.Format(CultureInfo.CurrentCulture,
                _localization.GetString(preview.BackupModelId is null ? "Rl.RecoveryNoBackup" : "Rl.RecoveryCandidate"), target, preview.BackupModelId)
                : _localization.GetString("Rl.RecoveryHealthy");
            if (preview is { Unavailable: true }) { Status = "Unavailable"; Message = preview.Error ?? ""; }
        }
        catch (Exception error)
        {
            if (!_disposed && revision == _recoveryRevision) { RecoveryPreview = null; RecoveryDisplay = error.Message; }
        }
    }
    [RelayCommand(CanExecute = nameof(CanRecover))] private async Task RecoverPointerAsync()
    {
        if (!CanRecover || _disposed) return;
        var reviewed = RecoveryPreview!;
        ++_recoveryRevision;
        InvalidateArtifactReview();
        using var cts = new CancellationTokenSource(); _recoveryCts = cts; IsBusy = true;
        try
        {
            await Task.Run(() => _registry.RecoverRlPointerAsync(reviewed, cts.Token));
            if (!_disposed)
            {
                Status = "Ready"; Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.RecoveryCompleted"), reviewed.BackupModelId);
                RefreshGenerations();
            }
        }
        catch (Exception error) { if (!_disposed) { Status = "Rejected"; Message = error.Message; } }
        finally { _recoveryCts = null; if (!_disposed) IsBusy = false; }
        if (!_disposed) await InspectRecoveryAsync();
    }
    private void RefreshGenerations()
    {
        string? directionId = SelectedDirectionGeneration?.ModelId, selectedId = SelectedGeneration?.ModelId;
        Generations.Clear(); DirectionGenerations.Clear();
        foreach (var g in _registry.ListRl()) { Generations.Add(g); if (g.Target == RlFiles.Target) DirectionGenerations.Add(g); }
        SelectedDirectionGeneration = DirectionGenerations.FirstOrDefault(g => g.ModelId == directionId);
        SelectedGeneration = Generations.FirstOrDefault(g => g.ModelId == selectedId);
        OnPropertyChanged(nameof(ActiveId));
    }
    private BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
    { SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter, InitialCapital = InitialCapital, CommissionFlat = CommissionFlat, CommissionPerUnit = CommissionPerUnit,
      SlippageRatio = SlippageRatio, LiquidationPenaltyRatio = LiquidationPenaltyRatio });

    [RelayCommand] private async Task SaveCostsAsync()
    {
        try
        {
            _ = Costs(); var original = _loadedDefaults ?? throw new InvalidOperationException("Defaults are not loaded.");
            var node = JsonSerializer.SerializeToNode(original)!;
            node[nameof(BacktestDefaultSettings.InitialCapital)] = InitialCapital;
            node[nameof(BacktestDefaultSettings.CommissionFlat)] = CommissionFlat;
            node[nameof(BacktestDefaultSettings.CommissionPerUnit)] = CommissionPerUnit;
            node[nameof(BacktestDefaultSettings.SlippageRatio)] = SlippageRatio;
            node[nameof(BacktestDefaultSettings.LiquidationPenaltyRatio)] = LiquidationPenaltyRatio;
            var saved = node.Deserialize<BacktestDefaultSettings>()!; await _defaults.SaveAsync(saved); _loadedDefaults = saved;
            Message = _localization.GetString("Rl.CostsSaved");
        }
        catch (Exception ex) { Status = "Rejected"; Message = ex.Message; }
    }

    [RelayCommand] private async Task TrainAsync()
    {
        if (IsBusy || _disposed) return;
        try
        {
            if (!DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(EndDateExclusive, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new ArgumentException("Explicit yyyy-MM-dd source dates are required.");
            if (UseContinuousPolicy && SelectedDirectionGeneration is null) throw new InvalidOperationException("Unavailable: select a qualified frozen T15 direction first.");
            var job = RlTrainingValidation.Freeze(new RlTrainingJob(Symbol, start, end, Costs(), SmokeOnly: SmokeOnly)
                { FrozenDirectionModelId = UseContinuousPolicy ? SelectedDirectionGeneration!.ModelId : null,
                  EvaluationSpecificationId = UseContinuousPolicy && UseAllCompleteBlocks ? RlAllBlockEvaluationContract.SpecificationId : null });
            using var cts = new CancellationTokenSource(); _jobCts = cts; IsBusy = true; Status = "Preparing";
            using var pause = new CancellationTokenSource(); _pauseCts = pause; NotifyCheckpointCommands();
            InvalidateReplay();
            var progress = new Progress<RlTrainingProgress>(p => { if (!_disposed && ReferenceEquals(_jobCts, cts) && !cts.IsCancellationRequested) { Status = p.State; Message = p.Message; } });
            try
            {
                var result = UseContinuousPolicy && _training is IRlResumableTrainingService resumable
                    ? await Task.Run(() => resumable.RunResumableAsync(job, pause.Token, progress, cts.Token))
                    : await _training.RunAsync(job, progress, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                Status = "Succeeded"; Message = result.SmokeOnly ? _localization.GetString("Rl.SmokeCompleted") : result.ModelId ?? result.Directory;
                RefreshGenerations();
            }
            finally { _jobCts = null; _pauseCts = null; if (!_disposed) { IsBusy = false; NotifyCheckpointCommands(); } }
        }
        catch (RlTrainingPausedException) { if (!_disposed) { Status = RlCheckpointContract.PauseState; Message = _localization.GetString("Rl.Checkpoint.Paused"); await InspectCheckpointsAsync(); } }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Status = ex is RlTemporalException || ex is ArgumentException ? "Rejected"
                    : ex is FileNotFoundException || ex.Message.StartsWith("Unavailable:", StringComparison.Ordinal) ? "Unavailable"
                    : ex.Message.StartsWith("InsufficientData:", StringComparison.Ordinal) ? "InsufficientData" : "Failed";
                Message = DescribeError(ex);
            }
        }
    }
    [RelayCommand] private void Cancel() { Status = "Cancelling"; _jobCts?.Cancel(); _recoveryCts?.Cancel(); _artifactCts?.Cancel(); InvalidateReplay(); }
    [RelayCommand] private async Task ActivateAsync()
    {
        if (IsBusy || SelectedGeneration is null) return;
        InvalidateArtifactReview();
        try { bool activated = await _registry.ActivateRlAsync(SelectedGeneration.ModelId); Status = activated ? "Ready" : "Rejected"; OnPropertyChanged(nameof(ActiveId)); }
        catch (Exception ex) { Status = "Rejected"; Message = ex.Message; }
    }
    partial void OnSelectedGenerationChanged(RlGeneration? value)
    {
        InvalidateReplay(); Blocks.Clear(); SelectedBlock = null;
        EvaluationReport = "";
        OnPropertyChanged(nameof(Policies)); OnPropertyChanged(nameof(ActiveId)); OnPropertyChanged(nameof(Disclosure)); SelectedPolicy = value?.Target ?? RlFiles.Target;
        OnPropertyChanged(nameof(TemporalStatusDisplay));
        if (value is not null) _ = LoadBlocksAsync(value, _replayRevision);
    }
    partial void OnSelectedDirectionGenerationChanged(RlGeneration? value)
    {
        long revision = ++_directionRevision;
        DirectionTemporalDisplay = _localization.GetString("Rl.Temporal.Unverified");
        if (value is not null) _ = LoadDirectionTemporalAsync(value.ModelId, revision);
    }
    private async Task LoadDirectionTemporalAsync(string modelId, long revision)
    {
        try
        {
            var evidence = await Task.Run(() => _registry.CaptureRlTemporalEvidence(modelId));
            if (_disposed || revision != _directionRevision || SelectedDirectionGeneration?.ModelId != modelId) return;
            DirectionTemporalDisplay = evidence is null ? _localization.GetString("Rl.Temporal.Unverified")
                : string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Temporal.DirectionBound"),
                    _localization.GetString("Rl.Temporal." + evidence.Status),
                    evidence.InformationUpperExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            if (!_disposed && revision == _directionRevision) DirectionTemporalDisplay = DescribeError(ex);
        }
    }
    private string DescribeError(Exception error)
    {
        if (error is not RlTemporalException temporal) return error.Message;
        string text = _localization.GetString("Rl.Temporal.Rejection." + temporal.Reason);
        return temporal.Reason == RlTemporalRejection.FutureInformation
            ? string.Format(CultureInfo.CurrentCulture, text,
                temporal.InformationUpperExclusive?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                temporal.DecisionExchangeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : text;
    }
    partial void OnSelectedBlockChanged(RlEpisodeBlock? value) => InvalidateReplay();
    partial void OnSelectedPolicyChanged(string value) => InvalidateReplay();
    partial void OnDecisionChanged(int value) => InvalidateReplay();
    private void InvalidateReplay() { ++_replayRevision; _replayCts?.Cancel(); _chart.ClearRlPresentation(); }
    private async Task LoadBlocksAsync(RlGeneration generation, long revision)
    {
        try
        {
            var blocks = await Task.Run(() =>
            {
                using var lease = _registry.AcquireRl(generation.ModelId) ?? throw new InvalidDataException("Generation is unavailable.");
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(lease.ModelPath)!, RlFiles.Job)));
                var limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
                var source = RlSourceSnapshot.Load(Path.GetDirectoryName(lease.ModelPath)!, limits);
                using var metrics = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + RlFiles.EvaluationSuffix));
                var constraints = metrics.RootElement.TryGetProperty("constraint_diagnostics", out var evidence)
                    ? evidence.Deserialize<RlConstraintReport>(RlFinancialParityContract.TraceJson) : null;
                var expanded = metrics.RootElement.TryGetProperty(RlAllBlockEvaluationContract.ReportKey, out var expandedEvidence)
                    ? expandedEvidence.Deserialize<RlAllBlockEvaluationReport>(RlFinancialParityContract.TraceJson) : null;
                return (Blocks: source.Catalog.Validation.Concat(source.Catalog.Oos).ToArray(), Constraints: constraints, Expanded: expanded);
            });
            if (_disposed || revision != _replayRevision || SelectedGeneration?.ModelId != generation.ModelId) return;
            EvaluationReport = blocks.Constraints is { } report ? RlPresentation.FormatEvaluation(report, _localization)
                : _localization.GetString("Rl.EvaluationUnavailable");
            if (blocks.Expanded is { } expanded) EvaluationReport = RlPresentation.FormatAllBlocks(expanded, _localization) + Environment.NewLine + EvaluationReport;
            foreach (var b in blocks.Blocks) Blocks.Add(b); SelectedBlock = Blocks.FirstOrDefault();
        }
        catch (Exception ex) { if (!_disposed && revision == _replayRevision) { Status = "Unavailable"; Message = DescribeError(ex); } }
    }
    [RelayCommand] private async Task ReplayAsync()
    {
        if (_disposed || IsBusy || SelectedGeneration is null || SelectedBlock is null) return;
        InvalidateReplay(); long revision = _replayRevision;
        using var cts = new CancellationTokenSource(); _replayCts = cts;
        try
        {
            using var lease = _registry.AcquireRl(SelectedGeneration.ModelId) ?? throw new InvalidDataException("Generation is unavailable.");
            var request = new RlReplayRequest(SelectedGeneration.ModelId, Path.GetDirectoryName(lease.ModelPath)!, SelectedBlock.Id, Decision, SelectedPolicy);
            var result = await _q.ReplayAsync(request, cts.Token); cts.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _replayRevision) return;
            await _chart.ApplyRlReplayAsync(result, _localization, cts.Token);
            if (!_disposed && revision == _replayRevision) { Status = "Ready"; Message = result.Disclosure; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && revision == _replayRevision) { Status = "Unavailable"; Message = ex.Message; } }
        finally { if (ReferenceEquals(_replayCts, cts)) _replayCts = null; }
    }
    public void Dispose()
    {
        _disposed = true;
        ++_checkpointRevision;
        ++_artifactRevision; _artifactCts?.Cancel();
        ++_directionRevision;
        ++_recoveryRevision;
        _recoveryCts?.Cancel();
        _jobCts?.Cancel();
        ++_replayRevision;
        _replayCts?.Cancel();
        // Published replay belongs to the chart; closing its controls only cancels pending work.
        // Symbol/timeframe changes still invalidate it through the chart's own lifecycle.
    }
}
