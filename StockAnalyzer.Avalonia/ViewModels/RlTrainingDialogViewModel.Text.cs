using System;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

public sealed partial class RlTrainingDialogViewModel
{
    private Exception? _messageError;
    private string? _messageKey, _recoveredNoticeId;
    private RlTemporalEvidence? _directionEvidence;
    private RlArtifactCleanupResult? _lastCleanupResult;
    private static readonly string[] NoticeKeys =
    {
        "Rl.CostsSaved", "Rl.SmokeCompleted", "Rl.State.Succeeded", "Rl.Checkpoint.Paused",
        "Rl.Checkpoint.PausePending", "Rl.Checkpoint.Abandoned"
    };
    partial void OnMessageChanged(string value)
    {
        _messageError = null; _recoveredNoticeId = null;
        _messageKey = NoticeKeys.FirstOrDefault(key => _localization.GetString(key) == value);
    }
    private void SetErrorMessage(Exception error)
    { Message = DescribeError(error); _messageError = error; }
    private void SetRecoveryNotice(string? modelId)
    { Message = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.RecoveryCompleted"), modelId); _recoveredNoticeId = modelId; }
    private string KnownLabel(string key)
    {
        string label = _localization.GetString(key);
        return label == "[" + key + "]" ? _localization.GetString("Rl.Ui.Load.Unsupported") : label;
    }
    private string DirectionEvidenceText() => _directionEvidence is not { } evidence ? _localization.GetString("Rl.Temporal.Unverified")
        : string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Temporal.DirectionBound"),
            KnownLabel("Rl.Temporal." + evidence.Status), evidence.InformationUpperExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    private string CheckpointText(RlCheckpointReview review) => string.Format(CultureInfo.CurrentCulture,
        _localization.GetString("Rl.Checkpoint.Entry"), review.RunId, review.Seed, review.Steps,
        KnownLabel("Rl.Checkpoint.Phase." + review.Phase), _localization.GetString(review.Verified ? "Rl.Checkpoint.Verified" : "Rl.State.Unavailable"));
    private void RefreshArtifactText()
    {
        ArtifactRows.Clear();
        if (_artifactPreview is { } preview && ArtifactsState == RlDialogLoadState.Loaded)
        {
            foreach (var entry in preview.Entries)
            {
                string reasons = string.Join(", ", entry.Reasons.Select(r => KnownLabel("Rl.Storage.Reason." + r)));
                ArtifactRows.Add(new(string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Entry"),
                    KnownLabel("Rl.Storage.Kind." + entry.Kind), entry.Id, entry.Bytes / (decimal)RlArtifactSettings.BytesPerGiB, reasons), entry.Candidate));
            }
            StorageDisplay = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Storage.Summary"),
                preview.UsedBytes / (decimal)RlArtifactSettings.BytesPerGiB, preview.FreeBytes / (decimal)RlArtifactSettings.BytesPerGiB,
                preview.Entries.Count(e => e.Candidate), preview.Entries.Where(e => e.Candidate).Sum(e => e.Bytes) / (decimal)RlArtifactSettings.BytesPerGiB);
            if (!preview.Policy.IsComplete) StorageDisplay += " " + _localization.GetString("Rl.Storage.Incomplete");
        }
        else if (_lastCleanupResult is { } result) StorageDisplay = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString("Rl.Storage.Cleaned"), result.Deleted.Length, result.Failures.Length, result.RemainingBytes / (decimal)RlArtifactSettings.BytesPerGiB);
        else StorageDisplay = ArtifactsState is RlDialogLoadState.Failed or RlDialogLoadState.Loading
            ? LoadCaption(ArtifactsState, 0) : RetentionDisplay;
    }
    private void RefreshReviewedText()
    {
        if (RecoveryPreview is { } preview)
            RecoveryDisplay = preview.Unavailable ? string.Format(CultureInfo.CurrentCulture,
                _localization.GetString(preview.BackupModelId is null ? "Rl.RecoveryNoBackup" : "Rl.RecoveryCandidate"),
                RlUiDisplay.Target(preview.Target, _localization), preview.BackupModelId) : _localization.GetString("Rl.RecoveryHealthy");
        else RecoveryDisplay = LoadCaption(RecoveryState, 0);
        DirectionTemporalDisplay = DirectionEvidenceText();
        var selected = SelectedCheckpoint?.Review; var rows = CheckpointRows.Select(r => r.Review).ToArray();
        CheckpointRows.Clear();
        foreach (var review in rows) CheckpointRows.Add(new(review, CheckpointText(review)));
        SelectedCheckpoint = CheckpointRows.FirstOrDefault(r => r.Review == selected);
        RefreshArtifactText();
        if (_messageError is { } error) SetErrorMessage(error);
        else if (_messageKey is { } key) Message = _localization.GetString(key);
        else if (_recoveredNoticeId is { } id) SetRecoveryNotice(id);
    }
}
