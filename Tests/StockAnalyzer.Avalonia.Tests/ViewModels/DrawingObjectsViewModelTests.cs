using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DrawingObjectsViewModelTests
{
    private class TestDrawingObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.TrendLine;
        public string? CustomName { get; set; }
        public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
        public bool IsMoveAxisModeExplicit { get; set; } = false;
        public List<ChartPoint> Points { get; set; } = new List<ChartPoint>();
        public Color Color { get; set; } = Colors.DodgerBlue;
        public double Thickness { get; set; } = 2.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public int ZIndex { get; set; } = 0;
        public SKColor SkiaColor => SKColors.Blue;

        public void Render(SKCanvas canvas, ICoordinateTransform transform) { }
        public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance) => true;
        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    [Fact]
    public void DrawingObjectsViewModel_SyncsItems_InDescendingZIndexOrder_Invariant_I02()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        var o3 = new TestDrawingObject { Type = ChartObjectType.Rectangle };

        manager.AddObject(o1);
        manager.AddObject(o2);
        manager.AddObject(o3);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        Assert.Equal(3, vm.Items.Count);
        Assert.True(vm.HasItems);

        // Top-most in manager is o3 (ZIndex 2), should be first (index 0) in UI Items
        Assert.Equal(o3.Id, vm.Items[0].Id);
        Assert.Equal(o2.Id, vm.Items[1].Id);
        Assert.Equal(o1.Id, vm.Items[2].Id);

        // Bring o1 to front (now o1 has max ZIndex)
        manager.BringToFront(o1.Id);
        Assert.Equal(o1.Id, vm.Items[0].Id);
        Assert.Equal(o3.Id, vm.Items[1].Id);
        Assert.Equal(o2.Id, vm.Items[2].Id);
    }

    [Fact]
    public void ItemViewModel_Commands_UpdateManagerAndState()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        var item1 = vm.Items[1]; // o1 (ZIndex 0, bottom)
        var item2 = vm.Items[0]; // o2 (ZIndex 1, top)

        // Toggle visibility
        item1.ToggleVisibilityCommand.Execute(null);
        Assert.False(o1.IsVisible);
        Assert.False(item1.IsVisible);

        // Toggle lock
        item1.ToggleLockCommand.Execute(null);
        Assert.True(o1.IsLocked);
        Assert.True(item1.IsLocked);
        Assert.False(item1.CanDelete);

        // Selection
        item2.SelectCommand.Execute(null);
        Assert.True(o2.IsSelected);
        Assert.True(item2.IsSelected);

        // Reordering
        Assert.True(item1.CanBringForward);
        item1.BringForwardCommand.Execute(null);
        Assert.Equal(1, o1.ZIndex);
        Assert.Equal(0, o2.ZIndex);
    }

    [Fact]
    public void ViewModel_BatchCommands_ApplyToAllObjects()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        // Bulk actions only apply to items the user has checked as targeted (IsTargeted defaults
        // to false since the 2026-08 UX change), so mark every item as a target first.
        foreach (var item in vm.Items) item.IsTargeted = true;

        // LockAll
        vm.LockAllCommand.Execute(null);
        Assert.True(o1.IsLocked && o2.IsLocked);

        // UnlockAll
        vm.UnlockAllCommand.Execute(null);
        Assert.False(o1.IsLocked || o2.IsLocked);

        // HideAll
        vm.HideAllCommand.Execute(null);
        Assert.False(o1.IsVisible || o2.IsVisible);

        // ShowAll
        vm.ShowAllCommand.Execute(null);
        Assert.True(o1.IsVisible && o2.IsVisible);

        // DeleteAll (o1 unlocked, o2 locked)
        manager.ToggleLock(o2.Id);
        vm.DeleteAllCommand.Execute(null);
        Assert.Single(vm.Items);
        Assert.Equal(o2.Id, vm.Items[0].Id);
    }

    [Fact]
    public void DrawingObjectsViewModel_ItemRemoval_SyncsCleanly()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject();
        var o2 = new TestDrawingObject();
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);
        Assert.Equal(2, vm.Items.Count);

        manager.RemoveObject(o1.Id);
        Assert.Single(vm.Items);
        Assert.Equal(o2.Id, vm.Items[0].Id);

        manager.RemoveObject(o2.Id);
        Assert.Empty(vm.Items);
        Assert.False(vm.HasItems);
    }

    [Fact]
    public void DrawingObjectsViewModel_LoadSnapshot_SyncsItemsImmediately()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        using var vm = new DrawingObjectsViewModel(manager, dispatcher);
        Assert.Empty(vm.Items);

        // Simulate startup load from persistence via LoadSnapshot
        var snapshot = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>
            {
                new TestDrawingObject { Type = ChartObjectType.TrendLine },
                new TestDrawingObject { Type = ChartObjectType.Rectangle }
            }
        };

        manager.LoadSnapshot(snapshot);

        // Verify that VM immediately synchronizes the restored items without any manual trigger
        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.HasItems);
        Assert.Equal(ChartObjectType.Rectangle, vm.Items[0].Type); // ZIndex 1 (top-most in UI)
        Assert.Equal(ChartObjectType.TrendLine, vm.Items[1].Type);  // ZIndex 0
    }

    [Fact]
    public void DrawingObjectsViewModel_MoveAxisMode_SwitchesAndNotifiesProperties()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        Assert.Equal(DrawingMoveAxisMode.XY, vm.MoveAxisMode);
        Assert.True(vm.IsMoveModeXY);
        Assert.False(vm.IsMoveModeX);
        Assert.False(vm.IsMoveModeY);

        vm.SetMoveAxisModeCommand.Execute(DrawingMoveAxisMode.X);
        Assert.Equal(DrawingMoveAxisMode.X, vm.MoveAxisMode);
        Assert.False(vm.IsMoveModeXY);
        Assert.True(vm.IsMoveModeX);
        Assert.False(vm.IsMoveModeY);

        vm.SetMoveAxisModeStringCommand.Execute("Y");
        Assert.Equal(DrawingMoveAxisMode.Y, vm.MoveAxisMode);
        Assert.False(vm.IsMoveModeXY);
        Assert.False(vm.IsMoveModeX);
        Assert.True(vm.IsMoveModeY);
    }

    [Fact]
    public void DrawingObjectsViewModel_SelectedObjectCommands_OperateOnSelection()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 5), 120m);
        var o1 = new TrendLineObject(p1, p2);
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        Assert.False(vm.HasSelectedObject);
        Assert.Null(vm.SelectedObjectItem);
        Assert.False(vm.CanCopySelected);

        // Select the object
        vm.Items[0].SelectCommand.Execute(null);

        Assert.True(vm.HasSelectedObject);
        Assert.NotNull(vm.SelectedObjectItem);
        Assert.True(vm.CanCopySelected);
        Assert.True(vm.CanOpenSettings);
        Assert.True(vm.CanDeleteSelected);
        Assert.True(vm.IsSelectedVisible);
        Assert.False(vm.IsSelectedLocked);

        // Copy Selected
        vm.CopySelectedCommand.Execute(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, manager.Objects.Count);

        // Toggle Lock on current selected -> Locking preserves selection, disables delete/copy
        vm.ToggleLockSelectedCommand.Execute(null);
        Assert.True(vm.HasSelectedObject);
        Assert.True(vm.IsSelectedLocked);
        Assert.False(vm.CanDeleteSelected);
        Assert.False(vm.CanCopySelected);
        Assert.True(vm.Items[0].IsLocked);

        // Toggle Lock off via item row
        vm.Items[0].ToggleLockCommand.Execute(null);
        Assert.False(vm.Items[0].IsLocked);
        Assert.False(vm.IsSelectedLocked);
        Assert.True(vm.CanDeleteSelected);
        Assert.True(vm.CanCopySelected);

        // Delete Selected
        vm.DeleteSelectedCommand.Execute(null);
        Assert.Single(vm.Items);
    }

    [Fact]
    public void CommitPendingRename_CommitsAnyInProgressEdit_WithoutRequiringLostFocus()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);
        var item = vm.Items[0];

        // Simulate the window being closed via the OS close button while the user is still
        // mid-edit: StartRename was invoked (e.g. via the "Rename" context menu item) and text was
        // typed, but neither Enter nor a focus-losing click ever happened to trigger CommitRename.
        item.StartRenameCommand.Execute(null);
        item.EditableName = "Renamed While Editing";
        Assert.True(item.IsEditingName);
        Assert.Null(o1.CustomName);

        vm.CommitPendingRename();

        Assert.False(item.IsEditingName);
        Assert.Equal("Renamed While Editing", o1.CustomName);
    }
}
