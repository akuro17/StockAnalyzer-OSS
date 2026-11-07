using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// D-2' Run 2 / S3: with a data folder that cannot be written, the application does not write the workspace anywhere
/// (no redirect to the temp folder), locks automatic saving and announces the read-only mode in the banner.
/// The scenario uses the real probe on a real read-only install folder; it swaps the process-wide base directory, so it runs alone.
/// </summary>
[Collection("ReadOnlyDataRoot")]
public class ReadOnlyStorageModeTests
{
    private const string BannerText = "Banner_StorageReadOnly";

    private static WorkspaceCoordinator NewCoordinator(LayoutStateStore store, string workspacePath, IDataStorageState state)
    {
        var target = new Mock<IWorkspaceLayoutTarget>();
        target.SetupAllProperties();
        target.Setup(t => t.LeftPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.RightPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.TopPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.BottomPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        var coordinator = new WorkspaceCoordinator(
            store,
            new WorkspaceSerializationService(),
            new Mock<IWatchlistManager>().Object,
            Options.Create(new WorkspaceCoordinatorOptions { WorkspacePath = workspacePath }),
            new SynchronousDispatcherService(),
            NullLogger<WorkspaceCoordinator>.Instance,
            dataStorageState: state);
        coordinator.Bind(target.Object);
        return coordinator;
    }

    // ---- the whole path: real folder, real probe, real paths ----------------------------------------------------

    [Fact]
    public async Task ReadOnlyInstall_StartsWithoutException_WritesNothingAnywhere_AndLocks()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            var status = DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating());
            var options = new WorkspaceCoordinatorOptions();
            var tempCopy = Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, "Config", Path.GetFileName(options.WorkspacePath));
            var tempBefore = File.Exists(tempCopy) ? File.GetLastWriteTimeUtc(tempCopy) : (DateTime?)null;
            var store = new LayoutStateStore();
            var coordinator = NewCoordinator(store, options.WorkspacePath, new DataStorageState(status));

            await coordinator.InitializeWorkspaceAsync();
            await coordinator.SaveActiveWorkspaceAsync();
            coordinator.ForceSaveSync(new WorkspaceSettings());

            Assert.False(status.IsWritable);
            Assert.StartsWith(fixture.DataRoot, options.WorkspacePath, StringComparison.OrdinalIgnoreCase); // no longer redirected to %TEMP%
            Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
            Assert.False(File.Exists(options.WorkspacePath));
            Assert.Equal(tempBefore, File.Exists(tempCopy) ? File.GetLastWriteTimeUtc(tempCopy) : (DateTime?)null); // and nothing was written there either
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        }
    }

    [Fact]
    public async Task WritableInstall_SameScenario_BehavesAsBefore_NoLockAndTheWorkspaceIsSaved()
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            var status = DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating());
            var options = new WorkspaceCoordinatorOptions();
            var store = new LayoutStateStore();
            var coordinator = NewCoordinator(store, options.WorkspacePath, new DataStorageState(status));

            await coordinator.InitializeWorkspaceAsync();
            await coordinator.SaveActiveWorkspaceAsync();

            Assert.True(status.IsWritable);
            Assert.Equal(AutoSaveLock.None, store.AutoSaveLock);
            Assert.True(File.Exists(options.WorkspacePath));
            Assert.StartsWith(fixture.DataRoot, options.WorkspacePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- the banner -------------------------------------------------------------------------------------------

    private static void KeyOnly(Mock<ILocalizationService> localization) =>
        localization.Setup(l => l.GetString(It.IsAny<string>())).Returns<string>(key => key);

    private static MainWindowViewModelParts CreateViewModel(IDataStorageState state, Action<Mock<ILocalizationService>>? more = null) =>
        MainWindowViewModelFactory.Create(
            dataStorageState: state,
            configureLocalization: l => { KeyOnly(l); more?.Invoke(l); });

    [Fact]
    public void ReadOnly_ShowsOneWarningWithoutButtons()
    {
        var parts = CreateViewModel(TestDataStorageState.ReadOnlyState());
        using var vm = parts.Vm;

        var item = Assert.Single(vm.StatusBanner.Items);
        Assert.Equal(MainWindowViewModel.StorageReadOnlyBannerId, item.Id);
        Assert.Equal(StatusBannerSeverity.Warning, item.Severity);
        Assert.Equal(BannerText, item.Text);
        Assert.Empty(item.Actions);
    }

    [Theory]
    [InlineData(DataStorageReadOnlyReason.DataRootCannotBeCreated, "Banner_StorageReadOnly_Reason_CannotBeCreated")]
    [InlineData(DataStorageReadOnlyReason.DataRootNotWritable, "Banner_StorageReadOnly_Reason_NotWritable")]
    public void ReadOnly_ToolTipNamesTheReasonAndTheSanitisedFolder(DataStorageReadOnlyReason reason, string reasonKey)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(profile, "app", "Data");
        var parts = CreateViewModel(
            TestDataStorageState.ReadOnlyState(reason, root),
            l => l.Setup(x => x.GetString("Banner_StorageReadOnly_ToolTip")).Returns("why={0};where={1}"));
        using var vm = parts.Vm;

        var tip = Assert.Single(vm.StatusBanner.Items).ToolTip;

        Assert.Equal($"why={reasonKey};where={ExceptionSanitizer.Sanitize(root)}", tip);
        Assert.DoesNotContain(profile, tip!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writable_ShowsNoBanner()
    {
        var parts = MainWindowViewModelFactory.Create(dataStorageState: TestDataStorageState.WritableState());
        using var vm = parts.Vm;

        Assert.False(vm.StatusBanner.HasItems);
    }

    [Fact]
    public void NoStorageState_ShowsNoBanner()
    {
        var parts = MainWindowViewModelFactory.Create();
        using var vm = parts.Vm;

        Assert.False(vm.StatusBanner.HasItems);
    }

    [Fact]
    public void RestoreBanner_AddedAndRemoved_LeavesTheReadOnlyBannerInPlace()
    {
        var parts = CreateViewModel(TestDataStorageState.ReadOnlyState());
        using var vm = parts.Vm;
        var issue = new WorkspaceRestoreIssue(WorkspaceRestoreIssueKind.PartialRestore, null, Array.Empty<WorkspaceLoadWarning>());

        parts.Store.EngageAutoSaveLock(issue);
        Assert.Equal(new[] { MainWindowViewModel.StorageReadOnlyBannerId, MainWindowViewModel.WorkspaceRestoreBannerId }, vm.StatusBanner.Items.Select(i => i.Id));

        parts.Store.ReleaseAutoSaveLock();
        Assert.Equal(MainWindowViewModel.StorageReadOnlyBannerId, Assert.Single(vm.StatusBanner.Items).Id);
    }
}
