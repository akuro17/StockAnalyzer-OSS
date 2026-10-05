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
    private CancellationTokenSource? _artifactCts;
    public bool CanCleanupArtifacts => !IsBusy && CleanupConfirmed && _artifactPreview?.Entries.Any(e => e.Candidate) == true;
    private void InvalidateArtifactReview()
    {
        ++_artifactRevision; _artifactPreview = null; CleanupConfirmed = false; ArtifactRows.Clear();
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
        try
        {
            var policy = await Task.Run(() => _registry.RlArtifactPolicy);
            if (_disposed || policy is null) return;
            StorageAvailable = true; StorageQuotaGiB = policy.QuotaGiB; StorageMinimumFreeGiB = policy.MinimumFreeGiB;
            StorageFailedRetentionDays = policy.FailedRetentionDays; StorageNonactiveDays = policy.NonactiveRetentionDays;
            StorageKeepCount = policy.NonactiveKeepCount;
            StorageCheckpointKeepCount = policy.CheckpointKeepCount;
            StorageDisplay = _localization.GetString(policy.IsComplete ? "Rl.Storage.Ready" : "Rl.Storage.Incomplete");
        }
        catch (Exception error) { if (!_disposed) { StorageAvailable = true; StorageDisplay = error.Message; } }
    }
    [RelayCommand] private async Task SaveArtifactSettingsAsync()
    {
        if (IsBusy || _disposed) return;
        var policy = new RlArtifactPolicy(StorageQuotaGiB, StorageMinimumFreeGiB, StorageFailedRetentionDays, StorageNonactiveDays, StorageKeepCount, StorageCheckpointKeepCount);
        InvalidateArtifactReview(); IsBusy = true;
        try
        {
            await Task.Run(() => _registry.SaveRlArtifactPolicyAsync(policy));
            if (!_disposed) StorageDisplay = _localization.GetString(policy.IsComplete ? "Rl.Storage.Saved" : "Rl.Storage.Incomplete");
        }
        catch (Exception error) { if (!_disposed) StorageDisplay = error.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }
    [RelayCommand] private async Task InspectArtifactsAsync()
    {
        if (IsBusy || _disposed) return;
        InvalidateArtifactReview(); long revision = _artifactRevision; IsBusy = true;
        var draft = new RlArtifactPolicy(StorageQuotaGiB, StorageMinimumFreeGiB, StorageFailedRetentionDays, StorageNonactiveDays, StorageKeepCount, StorageCheckpointKeepCount);
        try
        {
            var preview = await Task.Run(() => _registry.PreviewRlArtifacts());
            if (_disposed || revision != _artifactRevision || preview is null) return;
            if (draft != preview.Policy) { StorageDisplay = _localization.GetString("Rl.Storage.Unsaved"); return; }
            _artifactPreview = preview;
            foreach (var entry in preview.Entries)
            {
                string reasons = string.Join(", ", entry.Reasons.Select(r => _localization.GetString("Rl.Storage.Reason." + r)));
                ArtifactRows.Add(new(string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Entry"),
                    _localization.GetString("Rl.Storage.Kind." + entry.Kind), entry.Id,
                    entry.Bytes / (decimal)RlArtifactSettings.BytesPerGiB, reasons), entry.Candidate));
            }
            StorageDisplay = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Summary"),
                preview.UsedBytes / (decimal)RlArtifactSettings.BytesPerGiB, preview.FreeBytes / (decimal)RlArtifactSettings.BytesPerGiB,
                preview.Entries.Count(e => e.Candidate), preview.Entries.Where(e => e.Candidate).Sum(e => e.Bytes) / (decimal)RlArtifactSettings.BytesPerGiB);
            if (!preview.Policy.IsComplete) StorageDisplay += " " + _localization.GetString("Rl.Storage.Incomplete");
        }
        catch (Exception error) { if (!_disposed) StorageDisplay = error.Message; }
        finally { if (!_disposed) { IsBusy = false; OnPropertyChanged(nameof(CanCleanupArtifacts)); CleanupArtifactsCommand.NotifyCanExecuteChanged(); } }
    }
    [RelayCommand(CanExecute = nameof(CanCleanupArtifacts))] private async Task CleanupArtifactsAsync()
    {
        if (!CanCleanupArtifacts || _disposed) return;
        var reviewed = _artifactPreview!; InvalidateArtifactReview(); IsBusy = true;
        using var cts = new CancellationTokenSource(); _artifactCts = cts;
        try
        {
            var result = await Task.Run(() => _registry.CleanupRlArtifactsAsync(reviewed, cts.Token));
            if (!_disposed)
            {
                StorageDisplay = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Cleaned"), result.Deleted.Length,
                    result.Failures.Length, result.RemainingBytes / (decimal)RlArtifactSettings.BytesPerGiB);
                if (result.Failures.Length > 0) StorageDisplay += " " + string.Join(Environment.NewLine, result.Failures);
                RefreshGenerations();
            }
        }
        catch (Exception error) { if (!_disposed) StorageDisplay = error.Message; }
        finally { _artifactCts = null; if (!_disposed) IsBusy = false; }
    }
}
