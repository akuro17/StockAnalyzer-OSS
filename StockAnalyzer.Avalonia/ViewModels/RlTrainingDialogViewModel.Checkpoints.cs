using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

public sealed partial class RlTrainingDialogViewModel
{
    public sealed record CheckpointRow(RlCheckpointReview Review, string Display);
    public ObservableCollection<CheckpointRow> CheckpointRows { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanResumeCheckpoint)), NotifyPropertyChangedFor(nameof(CanAbandonCheckpoint)),
        NotifyCanExecuteChangedFor(nameof(ResumeCheckpointCommand)), NotifyCanExecuteChangedFor(nameof(AbandonCheckpointCommand))]
    private CheckpointRow? _selectedCheckpoint;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanResumeCheckpoint)), NotifyCanExecuteChangedFor(nameof(ResumeCheckpointCommand))]
    private bool _resumeConfirmed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAbandonCheckpoint)), NotifyCanExecuteChangedFor(nameof(AbandonCheckpointCommand))]
    private bool _abandonConfirmed;
    private CancellationTokenSource? _pauseCts;
    private long _checkpointRevision;
    public bool CheckpointAvailable => _capabilities.Checkpoints && _training is IRlResumableTrainingService;
    public bool CanPause => !_disposed && Operation == RlDialogOperation.Training && UseContinuousPolicy
        && _jobCts is { IsCancellationRequested: false } && _pauseCts is { IsCancellationRequested: false };
    public bool CanResumeCheckpoint => Editable && HasResumeSelection && ResumeConfirmed && SelectedCheckpoint?.Review.Verified == true && CheckpointAvailable;
    public bool CanAbandonCheckpoint => Editable && HasAbandonSelection && AbandonConfirmed && SelectedCheckpoint is not null && CheckpointAvailable;
    partial void OnSelectedCheckpointChanged(CheckpointRow? value) { ResumeConfirmed = false; AbandonConfirmed = false; }
    private void NotifyCheckpointCommands()
    {
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResumeCheckpoint)); OnPropertyChanged(nameof(CanAbandonCheckpoint));
        OnPropertyChanged(nameof(StatusDisplay));
        PauseCommand.NotifyCanExecuteChanged(); ResumeCheckpointCommand.NotifyCanExecuteChanged(); AbandonCheckpointCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanPause))] private void Pause()
    {
        if (!CanPause) return; _pauseCts!.Cancel(); Message = _localization.GetString("Rl.Checkpoint.PausePending"); NotifyCheckpointCommands();
    }
    [RelayCommand(CanExecute = nameof(CanInspectCheckpoints))] private async Task InspectCheckpointsAsync()
    {
        if (!CanInspectCheckpoints) return;
        long revision = NextRevision(ref _checkpointRevision); SelectedCheckpoint = null; ResumeConfirmed = false; AbandonConfirmed = false; CheckpointRows.Clear(); using var cancel = new CancellationTokenSource(); BeginOperation(RlDialogOperation.InspectingCheckpoints, cancel); CheckpointsState = RlDialogLoadState.Loading;
        try
        {
            var rows = await Task.Run(() => _registry.ListRlCheckpoints(cancel.Token));
            cancel.Token.ThrowIfCancellationRequested(); if (_disposed || revision != _checkpointRevision) return; if (rows is null) throw new System.IO.InvalidDataException("Checkpoint inspection is unavailable."); CheckpointsState = RlDialogLoadState.Loaded;
            foreach (var review in rows)
                CheckpointRows.Add(new(review, string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Checkpoint.Entry"),
                    review.RunId, review.Seed, review.Steps, _localization.GetString("Rl.Checkpoint.Phase." + review.Phase),
                    review.Verified ? _localization.GetString("Rl.Checkpoint.Verified") : _localization.GetString("Rl.State.Unavailable"))));
        }
        catch (OperationCanceledException) { if (!_disposed) CheckpointsState = RlDialogLoadState.Unloaded; }
        catch (Exception error) { if (!_disposed) { CheckpointsState = RlDialogLoadState.Failed; SetErrorMessage(error); } }
        finally { EndOperation(); }
    }
    [RelayCommand(CanExecute = nameof(CanResumeCheckpoint))] private async Task ResumeCheckpointAsync()
    {
        if (!CanResumeCheckpoint || _disposed) return;
        var review = SelectedCheckpoint!.Review; ResumeConfirmed = false; AbandonConfirmed = false;
        using var cancel = new CancellationTokenSource(); using var pause = new CancellationTokenSource();
        _jobCts = cancel; _pauseCts = pause; BeginOperation(RlDialogOperation.Training, cancel); UseContinuousPolicy = true; Status = "Preparing"; InvalidateReplay(); InvalidateArtifactReview();
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            var progress = new Progress<StockAnalyzer.Core.Models.ReinforcementLearning.RlTrainingProgress>(p =>
            ApplyProgress(p, cancel));
            var result = await Task.Run(() => ((IRlResumableTrainingService)_training)
                .ResumeAsync(review, pause.Token, progress, cancel.Token));
            cancel.Token.ThrowIfCancellationRequested(); if (!_disposed) { Status = "Succeeded"; Message = _localization.GetString("Rl.State.Succeeded"); await RefreshGenerationsAsync(); }
        }
        catch (RlTrainingPausedException) { if (!_disposed) { Status = RlCheckpointContract.PauseState; Message = _localization.GetString("Rl.Checkpoint.Paused"); } }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception error) { if (!_disposed) { Status = "Unavailable"; SetErrorMessage(error); } }
        finally { _jobCts = null; _pauseCts = null; EndOperation(); }
        if (!_disposed) await InspectCheckpointsAsync();
    }
    [RelayCommand(CanExecute = nameof(CanAbandonCheckpoint))] private async Task AbandonCheckpointAsync()
    {
        if (!CanAbandonCheckpoint || _disposed) return;
        var reviewed = SelectedCheckpoint!.Review; AbandonConfirmed = false; ResumeConfirmed = false; BeginOperation(RlDialogOperation.InspectingCheckpoints);
        try { await Task.Run(() => _registry.AbandonRlResumeAsync(reviewed)); if (!_disposed) Message = _localization.GetString("Rl.Checkpoint.Abandoned"); }
        catch (Exception error) { if (!_disposed) SetErrorMessage(error); }
        finally { EndOperation(); InvalidateArtifactReview(); }
        if (!_disposed) await InspectCheckpointsAsync();
    }
}
