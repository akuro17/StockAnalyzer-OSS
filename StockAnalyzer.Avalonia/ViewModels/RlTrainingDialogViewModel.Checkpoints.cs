using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models.ReinforcementLearning;
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
    public bool CanConfirmResume => Editable && HasResumeSelection;
    public bool CanConfirmAbandon => Editable && HasAbandonSelection;
    public bool CanExportCheckpoint => Editable && SelectedCheckpoint is not null;
    public bool CanImportCheckpoint => Editable && CheckpointAvailable;
    public bool CanExportGeneration => Editable && SelectedGeneration is not null;

    public string InspectCheckpointsTooltip => _localization.GetString("Rl.Ui.Tooltip.InspectCheckpointsHelp");

    public string ResumeConfirmTooltip => !HasResumeSelection
        ? _localization.GetString("Rl.Ui.Tooltip.ResumeConfirmNeedSelect")
        : _localization.GetString("Rl.Ui.Tooltip.ResumeConfirmHelp");

    public string AbandonConfirmTooltip => !HasAbandonSelection
        ? _localization.GetString("Rl.Ui.Tooltip.AbandonConfirmNeedSelect")
        : _localization.GetString("Rl.Ui.Tooltip.AbandonConfirmHelp");

    public string ResumeTooltip => !HasResumeSelection
        ? (SelectedCheckpoint is null
            ? _localization.GetString("Rl.Ui.Tooltip.ResumeNeedSelect")
            : !SelectedCheckpoint.Review.Verified
                ? _localization.GetString("Rl.Ui.Tooltip.ResumeUnverified")
                : _localization.GetString("Rl.Ui.Tooltip.ResumeNeedSelect"))
        : !ResumeConfirmed
            ? _localization.GetString("Rl.Ui.Tooltip.ResumeNeedConfirm")
            : _localization.GetString("Rl.Ui.Tooltip.ResumeReady");

    public string AbandonTooltip => !HasAbandonSelection
        ? _localization.GetString("Rl.Ui.Tooltip.AbandonNeedSelect")
        : !AbandonConfirmed
            ? _localization.GetString("Rl.Ui.Tooltip.AbandonNeedConfirm")
            : _localization.GetString("Rl.Ui.Tooltip.AbandonReady");

    public string CheckpointStatusBadgeDisplay => SelectedCheckpoint is null
        ? (!HasCheckpoints
            ? _localization.GetString("Rl.Ui.Badge.NoCheckpointsLoaded")
            : _localization.GetString("Rl.Ui.Badge.NoCheckpointSelected"))
        : !SelectedCheckpoint.Review.Verified
            ? _localization.GetString("Rl.Ui.Badge.ProtectedUnverified")
            : ResumeConfirmed
                ? _localization.GetString("Rl.Ui.Badge.ResumeReady")
                : AbandonConfirmed
                    ? _localization.GetString("Rl.Ui.Badge.AbandonReady")
                    : _localization.GetString("Rl.Ui.Badge.ProtectedResumable");

    public string InspectRecoveryTooltip => _localization.GetString("Rl.Ui.Tooltip.InspectRecoveryHelp");
    public string RecoverPointerTooltip => _localization.GetString("Rl.Ui.Tooltip.RecoverPointerHelp");
    public string RecoveryStatusBadgeDisplay => !RecoveryAvailable
        ? ""
        : HasRecoveryCandidate
            ? _localization.GetString("Rl.Ui.Badge.RecoveryCandidateFound")
            : _localization.GetString("Rl.Ui.Badge.RecoveryNormal");

    public bool HasSelectedCheckpoint => SelectedCheckpoint is not null;
    partial void OnResumeConfirmedChanged(bool value) => NotifyCheckpointCommands();
    partial void OnAbandonConfirmedChanged(bool value) => NotifyCheckpointCommands();
    partial void OnSelectedCheckpointChanged(CheckpointRow? value)
    {
        ResumeConfirmed = false; AbandonConfirmed = false;
        NotifyCheckpointCommands();
    }
    private void NotifyCheckpointCommands()
    {
        OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResumeCheckpoint)); OnPropertyChanged(nameof(CanAbandonCheckpoint));
        OnPropertyChanged(nameof(CanConfirmResume)); OnPropertyChanged(nameof(CanConfirmAbandon));
        OnPropertyChanged(nameof(CanExportCheckpoint)); OnPropertyChanged(nameof(CanImportCheckpoint)); OnPropertyChanged(nameof(CanExportGeneration));
        OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(ResumeTooltip)); OnPropertyChanged(nameof(AbandonTooltip));
        OnPropertyChanged(nameof(ResumeConfirmTooltip)); OnPropertyChanged(nameof(AbandonConfirmTooltip));
        OnPropertyChanged(nameof(InspectCheckpointsTooltip));
        OnPropertyChanged(nameof(CheckpointStatusBadgeDisplay));
        OnPropertyChanged(nameof(HasSelectedCheckpoint));
        OnPropertyChanged(nameof(RecoveryStatusBadgeDisplay)); OnPropertyChanged(nameof(InspectRecoveryTooltip)); OnPropertyChanged(nameof(RecoverPointerTooltip));
        PauseCommand.NotifyCanExecuteChanged(); ResumeCheckpointCommand.NotifyCanExecuteChanged(); AbandonCheckpointCommand.NotifyCanExecuteChanged();
        ExportCheckpointCommand.NotifyCanExecuteChanged(); ImportCheckpointCommand.NotifyCanExecuteChanged(); ExportGenerationCommand.NotifyCanExecuteChanged();
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
        _jobCts = cancel; _pauseCts = pause; BeginOperation(RlDialogOperation.Training, cancel); UseContinuousPolicy = true; Status = "Preparing";
        TotalStepsTarget = RlContinuousContract.TrainingTransitionsPerSeed;
        ProgressPercent = 0.0;
        IsProgressIndeterminate = true;
        ProgressPhaseDisplay = _localization.GetString("Rl.State.Preparing") ?? "Preparing";
        InvalidateArtifactReview();
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            var progress = new Progress<StockAnalyzer.Core.Models.ReinforcementLearning.RlTrainingProgress>(p =>
            ApplyProgress(p, cancel));
            var result = await Task.Run(() => ((IRlResumableTrainingService)_training)
                .ResumeAsync(review, pause.Token, progress, cancel.Token));
            cancel.Token.ThrowIfCancellationRequested();
            if (!_disposed)
            {
                Status = "Succeeded"; Message = _localization.GetString("Rl.State.Succeeded");
                ProgressPercent = 100.0;
                IsProgressIndeterminate = false;
                ProgressPhaseDisplay = Message;
                await RefreshGenerationsAsync();
            }
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

    [RelayCommand(CanExecute = nameof(CanExportCheckpoint))]
    private async Task ExportCheckpointAsync()
    {
        if (!CanExportCheckpoint || SelectedCheckpoint is null || _disposed) return;
        var review = SelectedCheckpoint.Review;
        string checkpointDir = System.IO.Path.Combine(review.RunDirectory, RlCheckpointContract.Folder, review.CheckpointId);
        if (!System.IO.Directory.Exists(checkpointDir)) checkpointDir = review.RunDirectory;

        string defaultFileName = $"checkpoint_{review.RunId:N}_{review.CheckpointId}.zip";
        string targetPath = "";
        if (_dialogService is not null)
        {
            targetPath = await _dialogService.ShowSaveFileDialogAsync("Export Checkpoint Archive", ".zip", defaultFileName, new[] { "zip" }) ?? "";
        }
        else
        {
            string exportDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StockAnalyzer_Exports");
            System.IO.Directory.CreateDirectory(exportDir);
            targetPath = System.IO.Path.Combine(exportDir, defaultFileName);
        }

        if (string.IsNullOrWhiteSpace(targetPath)) return;

        try
        {
            await _archiveService.ExportCheckpointArchiveAsync(checkpointDir, targetPath);
            Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Archive.ExportSucceeded"), targetPath);
        }
        catch (Exception ex)
        {
            SetErrorMessage(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportCheckpoint))]
    private async Task ImportCheckpointAsync()
    {
        if (!CanImportCheckpoint || _disposed) return;
        string sourceZip = "";
        if (_dialogService is not null)
        {
            sourceZip = await _dialogService.ShowOpenFileDialogAsync("Import Checkpoint Archive", new[] { "zip" }) ?? "";
        }

        if (string.IsNullOrWhiteSpace(sourceZip)) return;

        try
        {
            string targetDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StockAnalyzer_ImportedCheckpoints");
            var result = await _archiveService.ImportArchiveAsync(sourceZip, targetDir);
            if (result.Success)
            {
                Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Archive.ImportSucceeded"), result.Id, result.FileCount);
                await InspectCheckpointsAsync();
            }
            else
            {
                Message = result.Error ?? _localization.GetString("Rl.Archive.InvalidFormat");
            }
        }
        catch (Exception ex)
        {
            SetErrorMessage(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportGeneration))]
    private async Task ExportGenerationAsync()
    {
        if (!CanExportGeneration || SelectedGeneration is null || _disposed) return;
        var gen = SelectedGeneration;
        string modelId = gen.ModelId;
        string defaultFileName = $"model_{gen.Symbol}_{modelId[..Math.Min(8, modelId.Length)]}.zip";
        string targetPath = "";
        if (_dialogService is not null)
        {
            targetPath = await _dialogService.ShowSaveFileDialogAsync("Export Model Archive", ".zip", defaultFileName, new[] { "zip" }) ?? "";
        }
        else
        {
            string exportDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StockAnalyzer_Exports");
            System.IO.Directory.CreateDirectory(exportDir);
            targetPath = System.IO.Path.Combine(exportDir, defaultFileName);
        }

        if (string.IsNullOrWhiteSpace(targetPath)) return;

        try
        {
            string sourceDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stage_export_" + modelId[..Math.Min(8, modelId.Length)]);
            System.IO.Directory.CreateDirectory(sourceDir);
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(gen, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(sourceDir, "manifest.json"), manifestJson);

            await _archiveService.ExportGenerationArchiveAsync(sourceDir, targetPath);
            Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Archive.ExportSucceeded"), targetPath);
        }
        catch (Exception ex)
        {
            SetErrorMessage(ex);
        }
    }
}
