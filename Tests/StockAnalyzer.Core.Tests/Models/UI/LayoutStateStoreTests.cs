using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Tests.Models.UI;

/// <summary>
/// Test suite verifying the deterministic lifecycle transitions, boundary values,
/// and invalid state defense policies for LayoutStateStore and PanelDimensions.
/// </summary>
public class LayoutStateStoreTests
{
    // =========================================================================
    // Test 1: Hydration Verification from LayoutConstants on Initialization
    // =========================================================================
    [Fact]
    public void LayoutStateStore_Initialization_CorrectlyHydratesFromLayoutConstants()
    {
        var store = new LayoutStateStore();

        Assert.Equal(WorkspaceLifecycleState.Initializing, store.LifecycleState);
        Assert.Null(store.SelectedTicker);
        Assert.Equal("Daily", store.SelectedTimeframe);

        // Verify Left panel defaults (Visible = true)
        Assert.Equal(LayoutConstants.DefaultLeftWidth, store.LeftPanel.WidthOrHeight);
        Assert.True(store.LeftPanel.IsVisible);

        // Verify Right panel defaults (Visible = true)
        Assert.Equal(LayoutConstants.DefaultRightWidth, store.RightPanel.WidthOrHeight);
        Assert.True(store.RightPanel.IsVisible);

        // Verify Top panel defaults (Visible = false, Width = 0.0)
        Assert.Equal(0.0, store.TopPanel.WidthOrHeight);
        Assert.False(store.TopPanel.IsVisible);
        Assert.Equal(200.0, store.TopPanel.LastSize); // Initial backed up size

        // Verify Bottom panel defaults (Visible = true)
        Assert.Equal(LayoutConstants.DefaultBottomHeight, store.BottomPanel.WidthOrHeight);
        Assert.True(store.BottomPanel.IsVisible);

        // Verify that all panel region tab indices are initialized to 0
        foreach (PanelRegion region in Enum.GetValues<PanelRegion>())
        {
            Assert.True(store.SelectedTabIndices.ContainsKey(region));
            Assert.Equal(0, store.SelectedTabIndices[region]);
        }
    }

    // =========================================================================
    // Test 2: Lifecycle State Machine Transitions and Exception Guard Checks
    // =========================================================================
    [Fact]
    public void LifecycleState_ValidTransitions_Succeeds()
    {
        var store = new LayoutStateStore();

        store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        Assert.Equal(WorkspaceLifecycleState.LoadingWorkspace, store.LifecycleState);

        store.LifecycleState = WorkspaceLifecycleState.Ready;
        Assert.Equal(WorkspaceLifecycleState.Ready, store.LifecycleState);

        // Verify that reloading from Ready to Loading is allowed
        store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        Assert.Equal(WorkspaceLifecycleState.LoadingWorkspace, store.LifecycleState);

        store.LifecycleState = WorkspaceLifecycleState.Ready;
        store.LifecycleState = WorkspaceLifecycleState.ShuttingDown;
        Assert.Equal(WorkspaceLifecycleState.ShuttingDown, store.LifecycleState);

        store.LifecycleState = WorkspaceLifecycleState.Disposed;
        Assert.Equal(WorkspaceLifecycleState.Disposed, store.LifecycleState);
    }

    [Fact]
    public void LifecycleState_InvalidTransition_ThrowsInvalidOperationException()
    {
        var store = new LayoutStateStore();

        // Skip transition from Initializing -> Ready is prohibited and throws an exception
        Assert.Throws<InvalidOperationException>(() => store.LifecycleState = WorkspaceLifecycleState.Ready);
    }

    // =========================================================================
    // Test 3: PropertyChanged Notification Propagation & Value De-duplication
    // =========================================================================
    [Fact]
    public void PropertyChanged_FiresCorrectly_OnValidChanges()
    {
        var store = new LayoutStateStore();
        var firedProperties = new List<string>();

        store.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) firedProperties.Add(e.PropertyName);
        };

        store.SelectedTicker = "AAPL";
        store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;

        Assert.Contains(nameof(LayoutStateStore.SelectedTicker), firedProperties);
        Assert.Contains(nameof(LayoutStateStore.LifecycleState), firedProperties);
    }

    [Fact]
    public void PropertyChanged_DoesNotFire_OnEquivalentAssignment()
    {
        var store = new LayoutStateStore();
        var firedProperties = new List<string>();

        store.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) firedProperties.Add(e.PropertyName);
        };

        // The default timeframe is "Daily". Re-assigning it must not trigger an event.
        store.SelectedTimeframe = "Daily";

        Assert.Empty(firedProperties);
    }

    // =========================================================================
    // Test 4: Tab Index Clamping and Exception Boundaries
    // =========================================================================
    [Fact]
    public void SetTabIndex_ValidValues_UpdatesCorrectly()
    {
        var store = new LayoutStateStore();
        store.SetTabIndex(PanelRegion.Left, 5);

        Assert.Equal(5, store.SelectedTabIndices[PanelRegion.Left]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)] // Index >= 16 throws an exception
    public void SetTabIndex_OutOfRange_ThrowsArgumentOutOfRangeException(int invalidIndex)
    {
        var store = new LayoutStateStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetTabIndex(PanelRegion.Right, invalidIndex));
    }

    // =========================================================================
    // Test 5: PanelDimensions Input Guard Checks for Invalid Sizes
    // =========================================================================
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-10.0)]
    public void PanelDimensions_InvalidDoubleSize_ThrowsException(double invalidValue)
    {
        // Validation guard within the constructor
        Assert.Throws<ArgumentOutOfRangeException>(() => new PanelDimensions(invalidValue, true));

        // Validation guard within property setter
        var panel = new PanelDimensions(100.0, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.WidthOrHeight = invalidValue);
    }

    [Fact]
    public void PanelDimensions_HiddenPanel_NonZeroValueThrows()
    {
        var panel = new PanelDimensions(100.0, false); // Initialized as hidden
        Assert.Equal(0.0, panel.WidthOrHeight);

        // Attempting to modify non-zero size on a hidden panel throws an exception
        Assert.Throws<ArgumentException>(() => panel.WidthOrHeight = 150.0);
    }

    // =========================================================================
    // Test 6: Dimensions Save & Restore Parity on Visibility Toggle
    // =========================================================================
    [Fact]
    public void TogglePanelVisibility_SavesAndRestoresCorrectly()
    {
        var store = new LayoutStateStore();
        double originalLeftWidth = store.LeftPanel.WidthOrHeight; // Default left width

        // Toggle left panel to hidden
        store.TogglePanelVisibility(PanelRegion.Left);

        Assert.False(store.LeftPanel.IsVisible);
        Assert.Equal(0.0, store.LeftPanel.WidthOrHeight);
        Assert.Equal(originalLeftWidth, store.LeftPanel.LastSize); // Verify size is saved to LastSize

        // Toggle left panel to visible
        store.TogglePanelVisibility(PanelRegion.Left);

        Assert.True(store.LeftPanel.IsVisible);
        Assert.Equal(originalLeftWidth, store.LeftPanel.WidthOrHeight); // Verify size is restored from LastSize
    }

    // =========================================================================
    // Test 7: Constructor Dependency Injection (ILogger) Integration Verification
    // =========================================================================
    [Fact]
    public void LayoutStateStore_WithLogger_IntegratesSuccessfully()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<LayoutStateStore>.Instance;
        var store = new LayoutStateStore(logger);

        Assert.NotNull(store);
        
        // Verify state machine transition logging path
        store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        Assert.Equal(WorkspaceLifecycleState.LoadingWorkspace, store.LifecycleState);
    }

    // =========================================================================
    // Test 8: Vertical and Horizontal Panel Clamping Limits Verification
    // =========================================================================
    [Fact]
    public void LayoutStateStore_PanelClamping_EnforcesDifferentLimitsForHorizontalAndVertical()
    {
        var store = new LayoutStateStore();

        // 1. Horizontal Panels (Left / Right) - clamped to MaxPanelWidthClamp (2000.0)
        store.LeftPanel.WidthOrHeight = 3000.0;
        Assert.Equal(LayoutConstants.MaxPanelWidthClamp, store.LeftPanel.WidthOrHeight);

        store.RightPanel.WidthOrHeight = 2500.0;
        Assert.Equal(LayoutConstants.MaxPanelWidthClamp, store.RightPanel.WidthOrHeight);

        // 2. Vertical Panels (Top / Bottom) - clamped to MaxPanelHeightClamp (1000.0)
        store.TogglePanelVisibility(PanelRegion.Top); // Top is hidden by default, toggle to visible
        store.TopPanel.WidthOrHeight = 1500.0;
        Assert.Equal(LayoutConstants.MaxPanelHeightClamp, store.TopPanel.WidthOrHeight);

        store.BottomPanel.WidthOrHeight = 1200.0;
        Assert.Equal(LayoutConstants.MaxPanelHeightClamp, store.BottomPanel.WidthOrHeight);
    }
}
