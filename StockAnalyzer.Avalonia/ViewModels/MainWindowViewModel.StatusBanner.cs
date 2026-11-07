using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// The banner that tells the user why automatic workspace saving is stopped (D-3' restore anomaly lock) and offers the way out.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Banner item id of the workspace restore message. One message at a time; a new issue replaces the shown one.</summary>
    internal const string WorkspaceRestoreBannerId = "WorkspaceRestore";

    /// <summary>Banner item id of the read-only storage notice. Shown from construction to the end of the run (a restart is the only way out).</summary>
    internal const string StorageReadOnlyBannerId = "StorageReadOnly";

    /// <summary>The banner row. Items are added and removed here only, on the UI thread.</summary>
    public StatusBannerViewModel StatusBanner { get; } = new();

    /// <summary>
    /// Announces the read-only mode: one sentence, no buttons (leaving the mode needs a restart after fixing the permissions),
    /// and a tooltip with the reason and the sanitised folder.
    /// </summary>
    private void ShowStorageReadOnlyBannerIfNeeded(StockAnalyzer.Core.Common.IDataStorageState? dataStorageState)
    {
        if (dataStorageState?.Current is not { IsWritable: false } status)
        {
            return;
        }

        var reasonKey = status.Reason == StockAnalyzer.Core.Common.DataStorageReadOnlyReason.DataRootCannotBeCreated
            ? "Banner_StorageReadOnly_Reason_CannotBeCreated"
            : "Banner_StorageReadOnly_Reason_NotWritable";
        var toolTip = string.Format(
            _localizationService.GetString("Banner_StorageReadOnly_ToolTip"),
            _localizationService.GetString(reasonKey),
            StockAnalyzer.Core.Common.ExceptionSanitizer.Sanitize(status.DataRoot));

        StatusBanner.Show(new StatusBannerItem(
            StorageReadOnlyBannerId,
            StatusBannerSeverity.Warning,
            _localizationService.GetString("Banner_StorageReadOnly"),
            Array.Empty<StatusBannerAction>(),
            toolTip));
    }

    private void RequestWorkspaceRestoreBannerRefresh()
    {
        // The lock may be engaged or released from a worker thread; the banner collection is UI-bound.
        if (_dispatcherService.CheckAccess())
        {
            RefreshWorkspaceRestoreBanner();
        }
        else
        {
            _dispatcherService.Post(RefreshWorkspaceRestoreBanner);
        }
    }

    private void RefreshWorkspaceRestoreBanner()
    {
        var issue = _stateStore.RestoreIssue;
        if (issue is null)
        {
            StatusBanner.Remove(WorkspaceRestoreBannerId);
            return;
        }

        var actions = new List<StatusBannerAction>
        {
            new(_localizationService.GetString("Banner_WorkspaceRestore_SaveCurrentState"), SaveCurrentStateAndUnlockCommand),
            new(_localizationService.GetString("Banner_WorkspaceRestore_Details"), ShowWorkspaceRestoreDetailsCommand)
        };

        StatusBanner.Show(new StatusBannerItem(
            WorkspaceRestoreBannerId,
            issue.Kind == WorkspaceRestoreIssueKind.RestoreFailed ? StatusBannerSeverity.Error : StatusBannerSeverity.Warning,
            BuildWorkspaceRestoreText(issue),
            actions));
    }

    private string BuildWorkspaceRestoreText(WorkspaceRestoreIssue issue)
    {
        if (issue.Kind == WorkspaceRestoreIssueKind.NewerSchema)
        {
            return _localizationService.GetString("Banner_WorkspaceRestore_NewerSchema");
        }

        // One complete sentence per case (not head + tail) so every language controls its own punctuation and spacing.
        var failed = issue.Kind == WorkspaceRestoreIssueKind.RestoreFailed;
        if (issue.PreservedCopyPath is { } copy)
        {
            var preserved = _localizationService.GetString(failed ? "Banner_WorkspaceRestore_FailedPreserved" : "Banner_WorkspaceRestore_PartialPreserved");
            return string.Format(preserved, Path.GetFileName(copy));
        }

        return _localizationService.GetString(failed ? "Banner_WorkspaceRestore_FailedNotPreserved" : "Banner_WorkspaceRestore_PartialNotPreserved");
    }

    /// <summary>The user's explicit decision to overwrite the protected file with what is on screen.</summary>
    [RelayCommand]
    private async Task SaveCurrentStateAndUnlockAsync()
    {
        // A file written by a newer version has no preserved copy: this click would destroy its newer settings for good,
        // so it needs a second, informed confirmation. Cancelling changes nothing: no save, the lock and the banner stay.
        if (_stateStore.RestoreIssue?.Kind == WorkspaceRestoreIssueKind.NewerSchema)
        {
            var confirmed = await _dialogService.ShowConfirmationAsync(
                _localizationService.GetString("Banner_WorkspaceRestore_OverwriteNewerTitle"),
                _localizationService.GetString("Banner_WorkspaceRestore_OverwriteNewerMessage"));
            if (!confirmed)
            {
                return;
            }
        }

        var saved = false;
        try
        {
            saved = await _workspaceCoordinator.SaveCurrentStateAndUnlockAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the current state over the protected workspace file failed.");
        }

        if (!saved)
        {
            await _dialogService.ShowAlertAsync(
                _localizationService.GetString("Dialog_Error"),
                _localizationService.GetString("Banner_WorkspaceRestore_SaveFailed"));
        }
    }

    [RelayCommand]
    private async Task ShowWorkspaceRestoreDetailsAsync()
    {
        var issue = _stateStore.RestoreIssue;
        if (issue is null)
        {
            return;
        }

        await _dialogService.ShowAlertAsync(
            _localizationService.GetString("Banner_WorkspaceRestore_DetailsTitle"),
            BuildWorkspaceRestoreDetails(issue));
    }

    private string BuildWorkspaceRestoreDetails(WorkspaceRestoreIssue issue)
    {
        var text = new StringBuilder();
        if (issue.PreservedCopyPath is { } copy)
        {
            text.AppendLine(string.Format(_localizationService.GetString("Banner_WorkspaceRestore_DetailsPreserved"), copy));
        }

        if (issue.Warnings.Count == 0)
        {
            text.Append(_localizationService.GetString("Banner_WorkspaceRestore_DetailsNone"));
            return text.ToString().TrimEnd();
        }

        foreach (var warning in issue.Warnings.Take(StockAnalyzer.Avalonia.Models.SettingsConstants.UI_MaxRestoreWarningsListed))
        {
            text.AppendLine(string.IsNullOrEmpty(warning.Path) ? warning.Message : $"{warning.Path}: {warning.Message}");
        }

        var hidden = issue.Warnings.Count - StockAnalyzer.Avalonia.Models.SettingsConstants.UI_MaxRestoreWarningsListed;
        if (hidden > 0)
        {
            text.Append(string.Format(_localizationService.GetString("Banner_WorkspaceRestore_DetailsMore"), hidden));
        }

        return text.ToString().TrimEnd();
    }
}
