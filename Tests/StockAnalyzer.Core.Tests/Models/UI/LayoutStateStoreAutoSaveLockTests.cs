using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.UI;

/// <summary>D-3' Run C / W4: the store only holds and announces the automatic-save lock.</summary>
public class LayoutStateStoreAutoSaveLockTests
{
    private static WorkspaceRestoreIssue Issue(WorkspaceRestoreIssueKind kind = WorkspaceRestoreIssueKind.PartialRestore, string? copy = null) =>
        new(kind, copy, Array.Empty<WorkspaceLoadWarning>());

    [Fact]
    public void NewStore_IsNotLocked()
    {
        var store = new LayoutStateStore();

        Assert.Equal(AutoSaveLock.None, store.AutoSaveLock);
        Assert.Null(store.RestoreIssue);
    }

    [Fact]
    public void Engage_HoldsTheIssueAndAnnouncesBothProperties()
    {
        var store = new LayoutStateStore();
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var issue = Issue(WorkspaceRestoreIssueKind.NewerSchema);

        store.EngageAutoSaveLock(issue);

        Assert.Equal(AutoSaveLock.RestoreAnomaly, store.AutoSaveLock);
        Assert.Same(issue, store.RestoreIssue);
        Assert.Contains(nameof(LayoutStateStore.AutoSaveLock), changed);
        Assert.Contains(nameof(LayoutStateStore.RestoreIssue), changed);
    }

    [Fact]
    public void Engage_StateIsCompleteWhenTheFirstNotificationArrives()
    {
        var store = new LayoutStateStore();
        AutoSaveLock lockAtNotification = AutoSaveLock.None;
        WorkspaceRestoreIssue? issueAtNotification = null;
        store.PropertyChanged += (_, _) =>
        {
            lockAtNotification = store.AutoSaveLock;
            issueAtNotification = store.RestoreIssue;
        };

        store.EngageAutoSaveLock(Issue());

        Assert.Equal(AutoSaveLock.RestoreAnomaly, lockAtNotification);
        Assert.NotNull(issueAtNotification);
    }

    [Fact]
    public void Engage_WhileLocked_ReplacesTheIssue()
    {
        var store = new LayoutStateStore();
        store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.PartialRestore));
        var second = Issue(WorkspaceRestoreIssueKind.RestoreFailed, "copy");

        store.EngageAutoSaveLock(second);

        Assert.Same(second, store.RestoreIssue);
        Assert.Equal(AutoSaveLock.RestoreAnomaly, store.AutoSaveLock);
    }

    [Fact]
    public void Release_ClearsTheLockAndTheIssueAndAnnounces()
    {
        var store = new LayoutStateStore();
        store.EngageAutoSaveLock(Issue());
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.ReleaseAutoSaveLock();

        Assert.Equal(AutoSaveLock.None, store.AutoSaveLock);
        Assert.Null(store.RestoreIssue);
        Assert.Contains(nameof(LayoutStateStore.AutoSaveLock), changed);
        Assert.Contains(nameof(LayoutStateStore.RestoreIssue), changed);
    }

    [Fact]
    public void Release_WhenNotLocked_DoesNothingAndAnnouncesNothing()
    {
        var store = new LayoutStateStore();
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.ReleaseAutoSaveLock();

        Assert.Empty(changed);
    }

    [Fact]
    public void Engage_NullIssue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LayoutStateStore().EngageAutoSaveLock(null!));
    }

    [Fact]
    public void Lock_DoesNotChangeLifecycleState()
    {
        var store = new LayoutStateStore();
        store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        store.LifecycleState = WorkspaceLifecycleState.Ready;

        store.EngageAutoSaveLock(Issue());
        store.ReleaseAutoSaveLock();

        Assert.Equal(WorkspaceLifecycleState.Ready, store.LifecycleState);
    }

    [Fact]
    public void Engage_LogsOneWarningPerLock_NotPerCall()
    {
        var logger = new CapturingLogger<LayoutStateStore>();
        var store = new LayoutStateStore(logger);

        store.EngageAutoSaveLock(Issue());
        store.EngageAutoSaveLock(Issue(WorkspaceRestoreIssueKind.RestoreFailed));
        store.ReleaseAutoSaveLock();
        store.EngageAutoSaveLock(Issue());

        Assert.Equal(2, logger.Warnings);
    }

    // ---- D-2' Run 2 / S3: the read-only storage reason ----------------------------------------------------

    [Fact]
    public void EngageStorageReadOnlyLock_SetsTheReasonAndAnnouncesIt_WithoutAnIssue()
    {
        var store = new LayoutStateStore();
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.EngageStorageReadOnlyLock();

        Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
        Assert.Null(store.RestoreIssue);
        Assert.Contains(nameof(LayoutStateStore.AutoSaveLock), changed);
    }

    [Fact]
    public void ReleaseAutoSaveLock_NeverLiftsTheReadOnlyLock()
    {
        var store = new LayoutStateStore();
        store.EngageStorageReadOnlyLock();
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.ReleaseAutoSaveLock();

        Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
        Assert.Empty(changed);
    }

    [Fact]
    public void RestoreIssue_InAReadOnlyInstallation_IsRecordedButTheReasonStaysReadOnly()
    {
        var store = new LayoutStateStore();
        store.EngageStorageReadOnlyLock();
        var issue = Issue(WorkspaceRestoreIssueKind.PartialRestore);

        store.EngageAutoSaveLock(issue);

        Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
        Assert.Same(issue, store.RestoreIssue);
    }

    [Fact]
    public void Release_AfterAnIssueInAReadOnlyInstallation_ClearsOnlyTheIssue()
    {
        var store = new LayoutStateStore();
        store.EngageStorageReadOnlyLock();
        store.EngageAutoSaveLock(Issue());
        var changed = new List<string?>();
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.ReleaseAutoSaveLock();

        Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
        Assert.Null(store.RestoreIssue);
        Assert.Contains(nameof(LayoutStateStore.RestoreIssue), changed);
        Assert.DoesNotContain(nameof(LayoutStateStore.AutoSaveLock), changed);
    }

    [Fact]
    public void ReadOnlyLockEngagedWhileAnIssueExists_UpgradesTheReasonAndKeepsTheIssue()
    {
        var store = new LayoutStateStore();
        var issue = Issue();
        store.EngageAutoSaveLock(issue);

        store.EngageStorageReadOnlyLock();

        Assert.Equal(AutoSaveLock.StorageReadOnly, store.AutoSaveLock);
        Assert.Same(issue, store.RestoreIssue);
    }

    [Fact]
    public void EngageStorageReadOnlyLock_Twice_AnnouncesAndLogsOnlyOnce()
    {
        var logger = new CapturingLogger<LayoutStateStore>();
        var store = new LayoutStateStore(logger);
        var changed = 0;
        store.PropertyChanged += (_, _) => changed++;

        store.EngageStorageReadOnlyLock();
        store.EngageStorageReadOnlyLock();

        Assert.Equal(1, changed);
        Assert.Equal(1, logger.Warnings);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
