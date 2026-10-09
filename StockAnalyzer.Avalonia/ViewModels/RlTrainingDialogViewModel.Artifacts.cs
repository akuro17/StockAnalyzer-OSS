using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

public sealed partial class RlTrainingDialogViewModel
{
    public sealed record ArtifactRow(string Display, bool Candidate);
    public ObservableCollection<ArtifactRow> ArtifactRows { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CheckpointAvailable))] private bool _storageAvailable;
    [ObservableProperty] private decimal _storageQuotaGiB;
    [ObservableProperty] private decimal _storageMinimumFreeGiB;
    [ObservableProperty] private int _storageFailedRetentionDays;
    [ObservableProperty] private int? _storageNonactiveDays;
    [ObservableProperty] private int? _storageKeepCount;
    [ObservableProperty] private int _storageCheckpointKeepCount;
    [ObservableProperty] private string _storageDisplay = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCleanupArtifacts)), NotifyCanExecuteChangedFor(nameof(CleanupArtifactsCommand))]
    private bool _cleanupConfirmed;
    private RlArtifactPreview? _artifactPreview;
    private long _artifactRevision;
    
    public bool CanCleanupArtifacts => Editable && CleanupConfirmed && ArtifactsState == RlDialogLoadState.Loaded && _savedPolicy?.IsComplete == true && ValidRetentionEditors && DraftPolicy() == _artifactPreview?.Policy && _artifactPreview?.Entries.Any(e => e.Candidate) == true;
    private void InvalidateArtifactReview()
    {
        NextRevision(ref _artifactRevision); _artifactPreview = null; _lastCleanupResult = null; CleanupConfirmed = false; ArtifactRows.Clear(); ArtifactsState = StorageAvailable ? RlDialogLoadState.Unloaded : RlDialogLoadState.Unsupported;
        OnPropertyChanged(nameof(CanCleanupArtifacts)); CleanupArtifactsCommand.NotifyCanExecuteChanged();
    }
    partial void OnStorageQuotaGiBChanged(decimal value) => InvalidateArtifactReview();
    partial void OnStorageMinimumFreeGiBChanged(decimal value) => InvalidateArtifactReview();
    partial void OnStorageFailedRetentionDaysChanged(int value) => InvalidateArtifactReview();
    partial void OnStorageNonactiveDaysChanged(int? value) => InvalidateArtifactReview();
    partial void OnStorageKeepCountChanged(int? value) => InvalidateArtifactReview();
    partial void OnStorageCheckpointKeepCountChanged(int value) => InvalidateArtifactReview();
    private async Task InitializeArtifactSettingsAsync()
    {
        if (!StorageAvailable) return;
        try
        {
            var policy = await Task.Run(() => _registry.RlArtifactPolicy);
            if (_disposed) return; if (policy is null) throw new System.IO.InvalidDataException("Storage policy is unavailable."); _savedPolicy = policy;
            StorageAvailable = true; StorageQuotaGiB = policy.QuotaGiB; StorageMinimumFreeGiB = policy.MinimumFreeGiB;
            StorageFailedRetentionDays = policy.FailedRetentionDays; StorageNonactiveDays = policy.NonactiveRetentionDays;
            StorageKeepCount = policy.NonactiveKeepCount;
            StorageCheckpointKeepCount = policy.CheckpointKeepCount;
            StorageDisplay = _localization.GetString(policy.IsComplete ? "Rl.Storage.Ready" : "Rl.Storage.Incomplete");
        }
        catch (Exception error) { if (!_disposed) { StorageDisplay = DescribeError(error); } }
    }
    [RelayCommand(CanExecute = nameof(CanSaveRetention))] private async Task SaveArtifactSettingsAsync()
    {
        if (!CanSaveRetention) return;
        var policy = new RlArtifactPolicy(StorageQuotaGiB, StorageMinimumFreeGiB, StorageFailedRetentionDays, StorageNonactiveDays, StorageKeepCount, StorageCheckpointKeepCount);
        InvalidateArtifactReview(); BeginOperation(RlDialogOperation.SavingRetention);
        try
        {
            await Task.Run(() => _registry.SaveRlArtifactPolicyAsync(policy));
            if (!_disposed) { _savedPolicy = policy; StorageDisplay = _localization.GetString(policy.IsComplete ? "Rl.Storage.Saved" : "Rl.Storage.Incomplete"); NotifyUi(); }
        }
        catch (Exception error) { if (!_disposed) StorageDisplay = DescribeError(error); }
        finally { EndOperation(); }
    }
    [RelayCommand(CanExecute = nameof(CanInspectArtifacts))] private async Task InspectArtifactsAsync()
    {
        if (!CanInspectArtifacts) return;
        InvalidateArtifactReview(); long revision = _artifactRevision; using var cancel = new CancellationTokenSource(); BeginOperation(RlDialogOperation.InspectingArtifacts, cancel); ArtifactsState = RlDialogLoadState.Loading;
        var draft = new RlArtifactPolicy(StorageQuotaGiB, StorageMinimumFreeGiB, StorageFailedRetentionDays, StorageNonactiveDays, StorageKeepCount, StorageCheckpointKeepCount);
        try
        {
            var preview = await Task.Run(() => _registry.PreviewRlArtifacts(cancel.Token));
            cancel.Token.ThrowIfCancellationRequested(); if (_disposed || revision != _artifactRevision) return; if (preview is null || preview.Entries.IsDefault) throw new System.IO.InvalidDataException("Storage inspection is unavailable.");
            if (draft != preview.Policy) { ArtifactsState = RlDialogLoadState.Unloaded; StorageDisplay = _localization.GetString("Rl.Storage.Unsaved"); return; }
            _artifactPreview = preview; ArtifactsState = RlDialogLoadState.Loaded;
            RefreshArtifactText();
            NotifyUi();
        }
        catch (OperationCanceledException) { if (!_disposed) ArtifactsState = RlDialogLoadState.Unloaded; }
        catch (Exception error) { if (!_disposed) { ArtifactsState = RlDialogLoadState.Failed; StorageDisplay = DescribeError(error); } }
        finally { EndOperation(); }
    }
    [RelayCommand(CanExecute = nameof(CanCleanupArtifacts))] private async Task CleanupArtifactsAsync()
    {
        if (!CanCleanupArtifacts || _disposed) return;
        var reviewed = _artifactPreview!; InvalidateArtifactReview();
        using var cts = new CancellationTokenSource(); BeginOperation(RlDialogOperation.Cleaning, cts);
        try
        {
            var result = await Task.Run(() => _registry.CleanupRlArtifactsAsync(reviewed, cts.Token));
            if (!_disposed)
            {
                _lastCleanupResult = result;
                StorageDisplay = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Cleaned"), result.Deleted.Length,
                    result.Failures.Length, result.RemainingBytes / (decimal)RlArtifactSettings.BytesPerGiB);
                if (result.Failures.Length > 0) TechnicalDetails = string.Join(Environment.NewLine, result.Failures);
                await RefreshGenerationsAsync();
            }
        }
        catch (Exception error) { if (!_disposed) StorageDisplay = DescribeError(error); }
        finally { EndOperation(); }
    }
}
