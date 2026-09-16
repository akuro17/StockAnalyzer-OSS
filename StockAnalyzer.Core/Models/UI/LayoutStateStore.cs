using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Constants;

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

    [ObservableProperty]
    private string? _selectedTicker;

    private string _selectedTimeframe = DefaultTimeframe;

    public string SelectedTimeframe
    {
        get => _selectedTimeframe;
        set => SetProperty(ref _selectedTimeframe, string.IsNullOrWhiteSpace(value) ? DefaultTimeframe : value);
    }

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
    public LayoutStateStore(ILogger<LayoutStateStore>? logger = null)
    {
        _logger = logger ?? NullLogger<LayoutStateStore>.Instance;

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
        if (newIndex < 0 || newIndex >= LayoutConstants.MaxPanelTabs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newIndex),
                $"TabIndex must be in range [0, {LayoutConstants.MaxPanelTabs}). Given: {newIndex}");
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
