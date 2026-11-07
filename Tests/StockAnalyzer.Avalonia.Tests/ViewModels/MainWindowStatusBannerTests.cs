using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// D-3' Run C / W6: the main window shows why automatic workspace saving is stopped and offers
/// "Save current state" and "Details". The localization mock returns the key, so assertions name the key chosen.
/// </summary>
[Collection("MessengerSharedState")]
public class MainWindowStatusBannerTests
{
    private const string PreservedCopyName = "workspace.json.corrupt.20261105T123045Z";

    private static (MainWindowViewModelParts Parts, Mock<IWorkspaceCoordinator> Coordinator) Create(IDispatcherService? dispatcher = null)
    {
        var coordinator = new Mock<IWorkspaceCoordinator>();
        var parts = MainWindowViewModelFactory.Create(dispatcher: dispatcher, workspaceCoordinator: coordinator.Object);
        parts.Localization.Setup(l => l.GetString(It.IsAny<string>())).Returns<string>(key => key);
        return (parts, coordinator);
    }

    private static WorkspaceRestoreIssue Issue(
        WorkspaceRestoreIssueKind kind = WorkspaceRestoreIssueKind.PartialRestore,
        string? copy = "C:\\data\\" + PreservedCopyName,
        params WorkspaceLoadWarning[] warnings) => new(kind, copy, warnings);

    private static WorkspaceLoadWarning Warning(string path, string message) => new(WorkspaceWarningSeverity.Lossy, path, message);

    // ---- what is shown --------------------------------------------------------------------------------------

    [Fact]
    public void NoLock_NoBanner()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        Assert.False(vm.StatusBanner.HasItems);
    }

    [Fact]
    public void PartialRestore_WithPreservedCopy_ShowsWarningWithBothButtons()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        parts.Store.EngageAutoSaveLock(Issue());

        var item = Assert.Single(vm.StatusBanner.Items);
        Assert.Equal(MainWindowViewModel.WorkspaceRestoreBannerId, item.Id);
        Assert.Equal(StatusBannerSeverity.Warning, item.Severity);
        Assert.Equal("Banner_WorkspaceRestore_PartialPreserved", item.Text);
        Assert.Equal(new[] { "Banner_WorkspaceRestore_SaveCurrentState", "Banner_WorkspaceRestore_Details" }, item.Actions.Select(a => a.Text));
    }

    [Fact]
    public void PreservedText_ShowsTheCopyFileNameOnly_NotTheFullPath()
    {
        var (parts, _) = Create();
        parts.Localization.Setup(l => l.GetString("Banner_WorkspaceRestore_PartialPreserved")).Returns("kept as [{0}]");
        using var vm = parts.Vm;

        parts.Store.EngageAutoSaveLock(Issue());

        Assert.Equal($"kept as [{PreservedCopyName}]", Assert.Single(vm.StatusBanner.Items).Text);
    }

    [Fact]
    public void PartialRestore_WithoutPreservedCopy_SaysItWasNotPreserved()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        parts.Store.EngageAutoSaveLock(Issue(copy: null));

        Assert.Equal("Banner_WorkspaceRestore_PartialNotPreserved", Assert.Single(vm.StatusBanner.Items).Text);
    }

    [Theory]
    [InlineData(true, "Banner_WorkspaceRestore_FailedPreserved")]
    [InlineData(false, "Banner_WorkspaceRestore_FailedNotPreserved")]
    public void RestoreFailed_IsAnErrorWithItsOwnWording(bool preserved, string expectedKey)
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.RestoreFailed, preserved ? "C:\\x\\copy" : null));

        var item = Assert.Single(vm.StatusBanner.Items);
        Assert.Equal(StatusBannerSeverity.Error, item.Severity);
        Assert.Equal(expectedKey, item.Text);
    }

    [Fact]
    public void NewerSchema_ShowsTheDedicatedTextWithBothButtons()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.NewerSchema, copy: null));

        var item = Assert.Single(vm.StatusBanner.Items);
        Assert.Equal("Banner_WorkspaceRestore_NewerSchema", item.Text);
        Assert.Equal(new[] { "Banner_WorkspaceRestore_SaveCurrentState", "Banner_WorkspaceRestore_Details" }, item.Actions.Select(a => a.Text));
    }

    [Fact]
    public void ReleasingTheLock_RemovesTheBanner()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue());

        parts.Store.ReleaseAutoSaveLock();

        Assert.False(vm.StatusBanner.HasItems);
    }

    [Fact]
    public void ANewIssue_ReplacesTheShownBanner_ItDoesNotStack()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue());

        parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.RestoreFailed, copy: null));

        Assert.Equal("Banner_WorkspaceRestore_FailedNotPreserved", Assert.Single(vm.StatusBanner.Items).Text);
    }

    [Fact]
    public void OtherBannerItems_AreLeftAloneByTheWorkspaceRestoreBanner()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;
        vm.StatusBanner.Show(new StatusBannerItem("Other", StatusBannerSeverity.Warning, "other", Array.Empty<StatusBannerAction>()));

        parts.Store.EngageAutoSaveLock(Issue());
        parts.Store.ReleaseAutoSaveLock();

        Assert.Equal("Other", Assert.Single(vm.StatusBanner.Items).Id);
    }

    [Fact]
    public void LockEngagedOffTheUiThread_IsAppliedOnTheUiThread()
    {
        var posted = new List<Action>();
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(d => d.CheckAccess()).Returns(false);
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(posted.Add);
        var (parts, _) = Create(dispatcher.Object);
        using var vm = parts.Vm;
        posted.Clear();

        parts.Store.EngageAutoSaveLock(Issue());

        Assert.False(vm.StatusBanner.HasItems);
        Assert.NotEmpty(posted);
        posted.ToList().ForEach(a => a());
        Assert.True(vm.StatusBanner.HasItems);
    }

    // ---- "Save current state" ---------------------------------------------------------------------------------

    private static StatusBannerAction SaveAction(MainWindowViewModel vm) =>
        vm.StatusBanner.Items.Single().Actions.Single(a => a.Text == "Banner_WorkspaceRestore_SaveCurrentState");

    [Fact]
    public async Task SaveCurrentState_Success_AsksTheCoordinatorOnceAndShowsNoError()
    {
        var (parts, coordinator) = Create();
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).ReturnsAsync(true);
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue());

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);

        coordinator.Verify(c => c.SaveCurrentStateAndUnlockAsync(), Times.Once);
        parts.Dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveCurrentState_NothingWritten_TellsTheUserAndKeepsTheBanner()
    {
        var (parts, coordinator) = Create();
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).ReturnsAsync(false);
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue());

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);

        parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Error", "Banner_WorkspaceRestore_SaveFailed"), Times.Once);
        Assert.True(vm.StatusBanner.HasItems);
    }

    [Fact]
    public async Task SaveCurrentState_CoordinatorThrows_TellsTheUserInsteadOfCrashing()
    {
        var (parts, coordinator) = Create();
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).ThrowsAsync(new InvalidOperationException("boom"));
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue());

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);

        parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Error", "Banner_WorkspaceRestore_SaveFailed"), Times.Once);
    }

    // ---- "Details" --------------------------------------------------------------------------------------------

    private static async Task<string> ShowDetailsAsync(MainWindowViewModelParts parts, WorkspaceRestoreIssue issue)
    {
        string? message = null;
        parts.Dialog.Setup(d => d.ShowAlertAsync("Banner_WorkspaceRestore_DetailsTitle", It.IsAny<string>()))
            .Callback<string, string>((_, m) => message = m)
            .Returns(Task.CompletedTask);
        parts.Store.EngageAutoSaveLock(issue);
        var details = parts.Vm.StatusBanner.Items.Single().Actions.Single(a => a.Text == "Banner_WorkspaceRestore_Details");

        await ((IAsyncRelayCommand)details.Command).ExecuteAsync(null);

        return message ?? throw new InvalidOperationException("The details dialog was not shown.");
    }

    [Fact]
    public async Task Details_ListsThePreservedPathAndEveryWarning()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        var message = await ShowDetailsAsync(parts, Issue(warnings: new[] { Warning("Indicators[1]", "dropped"), Warning(string.Empty, "whole file") }));

        Assert.Contains("Banner_WorkspaceRestore_DetailsPreserved", message);
        Assert.Contains("Indicators[1]: dropped", message);
        Assert.Contains("whole file", message);
        Assert.DoesNotContain(": whole file", message);
    }

    [Fact]
    public async Task Details_NoWarnings_SaysSo()
    {
        var (parts, _) = Create();
        using var vm = parts.Vm;

        var message = await ShowDetailsAsync(parts, Issue(copy: null));

        Assert.Equal("Banner_WorkspaceRestore_DetailsNone", message);
    }

    [Fact]
    public async Task Details_MoreThanTheLimit_ListsTheLimitAndCountsTheRest()
    {
        var (parts, _) = Create();
        parts.Localization.Setup(l => l.GetString("Banner_WorkspaceRestore_DetailsMore")).Returns("and {0} more");
        using var vm = parts.Vm;
        var extra = 7;
        var warnings = Enumerable.Range(0, SettingsConstants.UI_MaxRestoreWarningsListed + extra).Select(i => Warning($"Item[{i}]", "x")).ToArray();

        var message = await ShowDetailsAsync(parts, Issue(copy: null, warnings: warnings));

        Assert.Equal(SettingsConstants.UI_MaxRestoreWarningsListed, message.Split('\n').Count(l => l.Contains("Item[")));
        Assert.Contains($"and {extra} more", message);
        Assert.DoesNotContain($"Item[{SettingsConstants.UI_MaxRestoreWarningsListed}]", message);
    }

    // ---- file of a newer version: the overwrite needs a second, informed confirmation (owner decision 1=B) ------

    private static void ConfirmAnswers(MainWindowViewModelParts parts, bool answer) =>
        parts.Dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(answer);

    [Fact]
    public async Task NewerSchema_SaveCurrentState_AsksFirstAndSavesOnlyAfterYes()
    {
        var (parts, coordinator) = Create();
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).ReturnsAsync(true);
        ConfirmAnswers(parts, true);
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.NewerSchema, copy: null));

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);

        parts.Dialog.Verify(d => d.ShowConfirmationAsync("Banner_WorkspaceRestore_OverwriteNewerTitle", "Banner_WorkspaceRestore_OverwriteNewerMessage"), Times.Once);
        coordinator.Verify(c => c.SaveCurrentStateAndUnlockAsync(), Times.Once);
    }

    [Fact]
    public async Task NewerSchema_SaveCurrentState_Cancelled_SavesNothing_AndTheLockAndBannerStay()
    {
        var (parts, coordinator) = Create();
        // If the command wrongly saved, this coordinator would unlock the store exactly like the real one does.
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).Callback(() => parts.Store.ReleaseAutoSaveLock()).ReturnsAsync(true);
        ConfirmAnswers(parts, false);
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.NewerSchema, copy: null));
        var saveCalls = 0;
        await using var scheduler = new LayoutSaveScheduler(parts.Store, null, 20);
        scheduler.RegisterSaveAction(() => { Interlocked.Increment(ref saveCalls); return Task.CompletedTask; });

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);
        scheduler.RequestSave(LayoutChangeReason.PanelResized);
        scheduler.RequestSave(LayoutChangeReason.Shutdown);
        await Task.Delay(150);

        coordinator.Verify(c => c.SaveCurrentStateAndUnlockAsync(), Times.Never);
        Assert.Equal(AutoSaveLock.RestoreAnomaly, parts.Store.AutoSaveLock);
        Assert.Equal(WorkspaceRestoreIssueKind.NewerSchema, parts.Store.RestoreIssue!.Kind);
        Assert.True(vm.StatusBanner.HasItems);
        Assert.Equal(0, saveCalls); // a later automatic save must not slip through
        parts.Dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never); // cancelling is not an error
    }

    [Theory]
    [InlineData(WorkspaceRestoreIssueKind.PartialRestore)]
    [InlineData(WorkspaceRestoreIssueKind.RestoreFailed)]
    public async Task OtherKinds_SaveCurrentState_DoesNotAskForConfirmation(WorkspaceRestoreIssueKind kind)
    {
        var (parts, coordinator) = Create();
        coordinator.Setup(c => c.SaveCurrentStateAndUnlockAsync()).ReturnsAsync(true);
        using var vm = parts.Vm;
        parts.Store.EngageAutoSaveLock(Issue(kind));

        await ((IAsyncRelayCommand)SaveAction(vm).Command).ExecuteAsync(null);

        parts.Dialog.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        coordinator.Verify(c => c.SaveCurrentStateAndUnlockAsync(), Times.Once);
    }

    // ---- manual load: result dialog and the all-or-nothing behaviour of a failed load -------------------------

    private const string LoadedFile = "workspace.json";

    private static (MainWindowViewModelParts Parts, Mock<IWorkspaceCoordinator> Coordinator, Mock<IWorkspaceSerializationService> Serialization) CreateForLoad(string? importedTicker = "MSFT")
    {
        var coordinator = new Mock<IWorkspaceCoordinator>();
        var serialization = new Mock<IWorkspaceSerializationService>();
        serialization.Setup(s => s.ImportPortableAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new WorkspaceSettings { SelectedTicker = importedTicker });
        var parts = MainWindowViewModelFactory.Create(workspaceCoordinator: coordinator.Object, serializationService: serialization.Object);
        parts.Localization.Setup(l => l.GetString(It.IsAny<string>())).Returns<string>(key => key);
        parts.Dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(LoadedFile);
        return (parts, coordinator, serialization);
    }

    [Fact]
    public async Task LoadWorkspace_CleanLoad_ShowsTheSuccessDialog()
    {
        var (parts, _, _) = CreateForLoad();
        using var vm = parts.Vm;

        await vm.LoadWorkspaceCommand.ExecuteAsync(null);

        parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Success", It.IsAny<string>()), Times.Once);
        parts.Dialog.Verify(d => d.ShowAlertAsync("Banner_WorkspaceRestore_DetailsTitle", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LoadWorkspace_WhenPartsWereLost_ShowsThePartialDialogWithTheWarningsInsteadOfSuccess()
    {
        var (parts, coordinator, _) = CreateForLoad();
        parts.Localization.Setup(l => l.GetString("Dialog_WorkspaceLoadPartial")).Returns("loaded {0}|{1}");
        coordinator.Setup(c => c.ApplyLoadedWorkspaceAsync(It.IsAny<WorkspaceSettings>()))
            .Callback(() => parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.PartialRestore, null, Warning("Indicators[2]", "dropped"))))
            .Returns(Task.CompletedTask);
        string? shown = null;
        parts.Dialog.Setup(d => d.ShowAlertAsync("Banner_WorkspaceRestore_DetailsTitle", It.IsAny<string>()))
            .Callback<string, string>((_, m) => shown = m).Returns(Task.CompletedTask);
        using var vm = parts.Vm;

        await vm.LoadWorkspaceCommand.ExecuteAsync(null);

        Assert.Equal($"loaded {LoadedFile}|Indicators[2]: dropped", shown);
        parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Success", It.IsAny<string>()), Times.Never);
        Assert.True(vm.StatusBanner.HasItems);
    }

    [Theory]
    [InlineData(false, 99)]
    [InlineData(true, 99)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task LoadWorkspace_FileOfUnsupportedSchema_FailsBeforeAnythingIsAppliedSavedOrUnlocked(bool lockedBefore, int schemaVersion)
    {
        // Rollback guarantee: the strict import rejects the file BEFORE the coordinator is asked to apply anything,
        // so the screen, the watchlists and the lock are exactly what they were before the attempt.
        var path = Path.Combine(Path.GetTempPath(), "sa_unsupported_" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, $"{{ \"SchemaVersion\": {schemaVersion}, \"Version\": {schemaVersion}, \"SelectedTicker\": \"FUTURE\" }}");
        try
        {
            var coordinator = new Mock<IWorkspaceCoordinator>();
            using var serialization = new WorkspaceSerializationService();
            var parts = MainWindowViewModelFactory.Create(workspaceCoordinator: coordinator.Object, serializationService: serialization);
            parts.Localization.Setup(l => l.GetString(It.IsAny<string>())).Returns<string>(key => key);
            parts.Dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(path);
            using var vm = parts.Vm;
            if (lockedBefore)
            {
                parts.Store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.PartialRestore));
            }
            var tickerBefore = vm.SelectedTicker;
            var lockBefore = parts.Store.AutoSaveLock;
            var issueBefore = parts.Store.RestoreIssue;

            await vm.LoadWorkspaceCommand.ExecuteAsync(null);

            coordinator.Verify(c => c.ApplyLoadedWorkspaceAsync(It.IsAny<WorkspaceSettings>()), Times.Never);
            coordinator.Verify(c => c.SaveActiveWorkspaceAsync(It.IsAny<string?>()), Times.Never);
            Assert.Equal(tickerBefore, vm.SelectedTicker);
            Assert.NotEqual("FUTURE", vm.SelectedTicker);
            Assert.Equal(lockBefore, parts.Store.AutoSaveLock);
            Assert.Same(issueBefore, parts.Store.RestoreIssue);
            parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Error", It.IsAny<string>()), Times.Once);
            parts.Dialog.Verify(d => d.ShowAlertAsync("Dialog_Success", It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
