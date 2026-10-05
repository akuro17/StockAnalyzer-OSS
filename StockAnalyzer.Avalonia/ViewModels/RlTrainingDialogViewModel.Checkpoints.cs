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
    public bool CheckpointAvailable => StorageAvailable && _training is IRlResumableTrainingService;
    public bool CanPause => IsBusy && UseContinuousPolicy && _pauseCts is { IsCancellationRequested: false };
    public bool CanResumeCheckpoint => !IsBusy && ResumeConfirmed && SelectedCheckpoint?.Review.Verified == true && CheckpointAvailable;
    public bool CanAbandonCheckpoint => !IsBusy && AbandonConfirmed && SelectedCheckpoint is not null && CheckpointAvailable;
    partial void OnSelectedCheckpointChanged(CheckpointRow? value) { ResumeConfirmed = false; AbandonConfirmed = false; }
    private void NotifyCheckpointCommands()
    {
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResumeCheckpoint)); OnPropertyChanged(nameof(CanAbandonCheckpoint));
        PauseCommand.NotifyCanExecuteChanged(); ResumeCheckpointCommand.NotifyCanExecuteChanged(); AbandonCheckpointCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanPause))] private void Pause()
    {
        _pauseCts?.Cancel(); Message = _localization.GetString("Rl.Checkpoint.PausePending"); NotifyCheckpointCommands();
    }
    [RelayCommand] private async Task InspectCheckpointsAsync()
    {
        if (_disposed || IsBusy) return;
        long revision = ++_checkpointRevision; SelectedCheckpoint = null; CheckpointRows.Clear(); IsBusy = true;
        try
        {
            var rows = await Task.Run(() => _registry.ListRlCheckpoints());
            if (_disposed || revision != _checkpointRevision) return;
            foreach (var review in rows)
                CheckpointRows.Add(new(review, string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Checkpoint.Entry"),
                    review.RunId, review.Seed, review.Steps, _localization.GetString("Rl.Checkpoint.Phase." + review.Phase),
                    review.Verified ? _localization.GetString("Rl.Checkpoint.Verified") : review.Error)));
        }
        catch (Exception error) { if (!_disposed) Message = error.Message; }
        finally { if (!_disposed) { IsBusy = false; NotifyCheckpointCommands(); } }
    }
    [RelayCommand(CanExecute = nameof(CanResumeCheckpoint))] private async Task ResumeCheckpointAsync()
    {
        if (!CanResumeCheckpoint || _disposed) return;
        var review = SelectedCheckpoint!.Review; ResumeConfirmed = false; AbandonConfirmed = false;
        UseContinuousPolicy = true;
        using var cancel = new CancellationTokenSource(); using var pause = new CancellationTokenSource();
        _jobCts = cancel; _pauseCts = pause; IsBusy = true; NotifyCheckpointCommands(); InvalidateArtifactReview();
        try
        {
            var progress = new Progress<StockAnalyzer.Core.Models.ReinforcementLearning.RlTrainingProgress>(p =>
            { if (!_disposed && ReferenceEquals(_jobCts, cancel)) { Status = p.State; Message = p.Message; } });
            var result = await Task.Run(() => ((IRlResumableTrainingService)_training)
                .ResumeAsync(review, pause.Token, progress, cancel.Token));
            if (!_disposed) { Status = "Succeeded"; Message = result.ModelId ?? _localization.GetString("Rl.SmokeCompleted"); RefreshGenerations(); }
        }
        catch (RlTrainingPausedException) { if (!_disposed) { Status = RlCheckpointContract.PauseState; Message = _localization.GetString("Rl.Checkpoint.Paused"); } }
        catch (OperationCanceledException) { if (!_disposed) Status = "Cancelled"; }
        catch (Exception error) { if (!_disposed) { Status = "Unavailable"; Message = error.Message; } }
        finally { _jobCts = null; _pauseCts = null; if (!_disposed) { IsBusy = false; NotifyCheckpointCommands(); } }
        if (!_disposed) await InspectCheckpointsAsync();
    }
    [RelayCommand(CanExecute = nameof(CanAbandonCheckpoint))] private async Task AbandonCheckpointAsync()
    {
        if (!CanAbandonCheckpoint || _disposed) return;
        var reviewed = SelectedCheckpoint!.Review; AbandonConfirmed = false; ResumeConfirmed = false; IsBusy = true;
        try { await Task.Run(() => _registry.AbandonRlResumeAsync(reviewed)); if (!_disposed) Message = _localization.GetString("Rl.Checkpoint.Abandoned"); }
        catch (Exception error) { if (!_disposed) Message = error.Message; }
        finally { if (!_disposed) IsBusy = false; InvalidateArtifactReview(); NotifyCheckpointCommands(); }
        if (!_disposed) await InspectCheckpointsAsync();
    }
}
