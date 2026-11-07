using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Central store acting as the Single Source of Truth (SSoT) for layout and lifecycle state across the application.
/// Intended to run on the UI thread and provides deterministic transition verification.
/// </summary>
public partial class LayoutStateStore : ObservableObject
{
    private const string DefaultTimeframe = "Daily";

    private const bool DefaultLeftPanelVisible = true;
    private const bool DefaultRightPanelVisible = true;
    private const bool DefaultTopPanelVisible = false;
    private const bool DefaultBottomPanelVisible = true;

    private readonly ILogger<LayoutStateStore> _logger;
    private WorkspaceLifecycleState _lifecycleState = WorkspaceLifecycleState.Initializing;

    public WorkspaceLifecycleState LifecycleState
    {
        get => _lifecycleState;
        set
        {
            if (_lifecycleState == value) return;
            ValidateStateTransition(_lifecycleState, value);
            _logger.LogInformation("Workspace lifecycle state transitioning from {CurrentState} to {TargetState}", _lifecycleState, value);
            SetProperty(ref _lifecycleState, value);
        }
    }

    // volatile: the layout save scheduler reads the lock from timer threads while the UI thread changes it.
    private volatile AutoSaveLock _autoSaveLock = AutoSaveLock.None;
    private volatile WorkspaceRestoreIssue? _restoreIssue;

    // Every transition below is a read-then-write of both fields ("never downgrade StorageReadOnly" depends on the read), so the transitions
    // run under this gate; readers stay lock-free. Events are raised after the gate is released.
    private readonly object _autoSaveGate = new();

    /// <summary>Why automatic saving of the default workspace is blocked; <see cref="Models.UI.AutoSaveLock.None"/> when it is not.</summary>
    public AutoSaveLock AutoSaveLock => _autoSaveLock;

    /// <summary>
    /// What went wrong while restoring the workspace file; null when there is no restore problem. It can exist together with
    /// <see cref="Models.UI.AutoSaveLock.StorageReadOnly"/> (a damaged file found in a read-only installation).
    /// </summary>
    public WorkspaceRestoreIssue? RestoreIssue => _restoreIssue;

    /// <summary>
    /// Blocks automatic saving because the workspace file was not restored completely. The store only holds and announces the state;
    /// the coordinator decides when to call this. Logs one warning per lock (a repeated call while locked only replaces the issue).
    /// A <see cref="Models.UI.AutoSaveLock.StorageReadOnly"/> lock is never downgraded: the issue is recorded and the reason stays.
    /// </summary>
    public void EngageAutoSaveLock(WorkspaceRestoreIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        bool wasLocked;
        lock (_autoSaveGate)
        {
            wasLocked = _autoSaveLock != AutoSaveLock.None;
            _restoreIssue = issue;
            if (_autoSaveLock != AutoSaveLock.StorageReadOnly)
            {
                _autoSaveLock = AutoSaveLock.RestoreAnomaly;
            }
        }

        if (!wasLocked)
        {
            _logger.LogWarning("Automatic workspace saving is locked: {Kind}. The original file is not overwritten until the user saves or loads explicitly.", issue.Kind);
        }

        OnPropertyChanged(nameof(RestoreIssue));
        if (!wasLocked)
        {
            OnPropertyChanged(nameof(AutoSaveLock));
        }
    }

    /// <summary>
    /// Blocks automatic saving because the persistent data folder is not writable. Final for the life of the process:
    /// <see cref="ReleaseAutoSaveLock"/> never lifts it. Logs one warning per lock.
    /// </summary>
    public void EngageStorageReadOnlyLock()
    {
        bool wasLocked;
        lock (_autoSaveGate)
        {
            if (_autoSaveLock == AutoSaveLock.StorageReadOnly)
            {
                return;
            }

            wasLocked = _autoSaveLock != AutoSaveLock.None;
            _autoSaveLock = AutoSaveLock.StorageReadOnly;
        }

        if (!wasLocked)
        {
            _logger.LogWarning("Automatic workspace saving is locked: the data folder is not writable (read-only mode).");
        }

        OnPropertyChanged(nameof(AutoSaveLock));
    }

    /// <summary>
    /// Lifts the restore-anomaly lock and forgets its issue. Does nothing when there is none. A
    /// <see cref="Models.UI.AutoSaveLock.StorageReadOnly"/> lock stays (only its recorded restore issue is cleared).
    /// </summary>
    public void ReleaseAutoSaveLock()
    {
        bool lockLifted;
        lock (_autoSaveGate)
        {
            if (_restoreIssue is null && _autoSaveLock != AutoSaveLock.RestoreAnomaly)
            {
                return;
            }

            lockLifted = _autoSaveLock == AutoSaveLock.RestoreAnomaly;
            _restoreIssue = null;
            if (lockLifted)
            {
                _autoSaveLock = AutoSaveLock.None;
            }
        }

        // Say what actually happened: under StorageReadOnly only the recorded issue goes away and saving stays blocked.
        _logger.LogInformation(lockLifted
            ? "Automatic workspace saving is unlocked."
            : "The recorded workspace restore issue was cleared; automatic workspace saving stays blocked (read-only mode).");

        OnPropertyChanged(nameof(RestoreIssue));
        if (lockLifted)
        {
            OnPropertyChanged(nameof(AutoSaveLock));
        }
    }

    [ObservableProperty]
    private string? _selectedTicker;

    private string _selectedTimeframe = DefaultTimeframe;

    public string SelectedTimeframe
    {
        get => _selectedTimeframe;
        set => SetProperty(ref _selectedTimeframe, string.IsNullOrWhiteSpace(value) ? DefaultTimeframe : value);
    }

    /// <summary>Most tabs one panel region (or Tab Window) may hold (<see cref="LayoutSettings.MaxPanelTabs"/>).</summary>
    public int MaxPanelTabs { get; }

    /// <summary>Farthest one drag may move a tab (<see cref="LayoutSettings.MaxTabReorderDistance"/>).</summary>
    public int MaxTabReorderDistance { get; }

    public PanelDimensions LeftPanel { get; }
    public PanelDimensions RightPanel { get; }
    public PanelDimensions TopPanel { get; }
    public PanelDimensions BottomPanel { get; }

    private readonly Dictionary<PanelRegion, int> _selectedTabIndices;

    /// <summary>
    /// Read-only dictionary of active tab indices per panel region to maintain immutability.
    /// </summary>
    public IReadOnlyDictionary<PanelRegion, int> SelectedTabIndices => _selectedTabIndices;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutStateStore"/> class and hydrates it with defaults from LayoutConstants.
    /// </summary>
    /// <param name="logger">Optional logger for telemetry and debugging.</param>
    /// <param name="layoutSettings">Configured layout limits; the <see cref="LayoutSettings"/> defaults when omitted.</param>
    public LayoutStateStore(ILogger<LayoutStateStore>? logger = null, LayoutSettings? layoutSettings = null)
    {
        _logger = logger ?? NullLogger<LayoutStateStore>.Instance;

        layoutSettings ??= new LayoutSettings();
        layoutSettings.Validate();
        MaxPanelTabs = layoutSettings.MaxPanelTabs;
        MaxTabReorderDistance = layoutSettings.MaxTabReorderDistance;

        // Apply default sizes from LayoutConstants and specify initial visibility
        LeftPanel = new PanelDimensions(LayoutConstants.DefaultLeftWidth, DefaultLeftPanelVisible, LayoutConstants.MaxPanelWidthClamp);
        RightPanel = new PanelDimensions(LayoutConstants.DefaultRightWidth, DefaultRightPanelVisible, LayoutConstants.MaxPanelWidthClamp);
        
        // Hydrate TopPanel fallback dimensions since it defaults to 0.0 (hidden)
        double initialTopSize = LayoutConstants.DefaultTopHeight > 0.0 ? LayoutConstants.DefaultTopHeight : 200.0;
        TopPanel = new PanelDimensions(initialTopSize, DefaultTopPanelVisible, LayoutConstants.MaxPanelHeightClamp);
        
        BottomPanel = new PanelDimensions(LayoutConstants.DefaultBottomHeight, DefaultBottomPanelVisible, LayoutConstants.MaxPanelHeightClamp);

        // Pre-populate dictionary for all PanelRegion enum values with direct keys (zero allocations)
        _selectedTabIndices = new Dictionary<PanelRegion, int>
        {
            { PanelRegion.Unknown, 0 },
            { PanelRegion.Left, 0 },
            { PanelRegion.Right, 0 },
            { PanelRegion.Top, 0 },
            { PanelRegion.Bottom, 0 }
        };

        _logger.LogDebug("LayoutStateStore initialized and successfully hydrated panel dimensions.");
    }

    /// <summary>
    /// Sets the active tab index for the specified panel region.
    /// </summary>
    public void SetTabIndex(PanelRegion region, int newIndex)
    {
        if (newIndex < 0 || newIndex >= MaxPanelTabs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newIndex),
                $"TabIndex must be in range [0, {MaxPanelTabs}). Given: {newIndex}");
        }

        if (_selectedTabIndices[region] != newIndex)
        {
            _logger.LogInformation("Tab changed in region {Region}: {OldIndex} -> {NewIndex}", region, _selectedTabIndices[region], newIndex);
            _selectedTabIndices[region] = newIndex;
            OnPropertyChanged(nameof(SelectedTabIndices));
        }
    }

    /// <summary>
    /// Toggles the visibility of the specified panel region.
    /// </summary>
    public void TogglePanelVisibility(PanelRegion region)
    {
        var panel = region switch
        {
            PanelRegion.Left => LeftPanel,
            PanelRegion.Right => RightPanel,
            PanelRegion.Top => TopPanel,
            PanelRegion.Bottom => BottomPanel,
            _ => throw new ArgumentException($"Invalid PanelRegion for visibility toggle: {region}", nameof(region))
        };
        panel.IsVisible = !panel.IsVisible;
        _logger.LogInformation("Toggled visibility for region {Region}. New visibility: {IsVisible}", region, panel.IsVisible);
    }

    private void ValidateStateTransition(WorkspaceLifecycleState current, WorkspaceLifecycleState target)
    {
        bool isValid = (current, target) switch
        {
            (WorkspaceLifecycleState.Initializing, WorkspaceLifecycleState.LoadingWorkspace) => true,
            (WorkspaceLifecycleState.Initializing, WorkspaceLifecycleState.ShuttingDown) => true,
            (WorkspaceLifecycleState.LoadingWorkspace, WorkspaceLifecycleState.Ready) => true,
            (WorkspaceLifecycleState.LoadingWorkspace, WorkspaceLifecycleState.ShuttingDown) => true,
            (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.LoadingWorkspace) => true,
            (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.ShuttingDown) => true,
            (WorkspaceLifecycleState.ShuttingDown, WorkspaceLifecycleState.Disposed) => true,
            _ => false
        };

        if (!isValid)
        {
            var exceptionMessage = $"State Machine Violation: Transition from '{current}' to '{target}' is mathematically denied.";
            _logger.LogError(exceptionMessage);
            throw new InvalidOperationException(exceptionMessage);
        }
    }
}
