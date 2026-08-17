using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class DrawingObjectsViewModelTests
{
    private class SynchronousDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public System.Threading.Tasks.Task PostAsync(Func<System.Threading.Tasks.Task> action) => action();
        public System.Threading.Tasks.Task PostAsync<TState>(Func<TState, System.Threading.Tasks.Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    private class TestDrawingObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.TrendLine;
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
    public void DrawingObjectsViewModel_SyncsItems_InDescendingZIndexOrder()
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
        manager.BringForward(o1.Id);
        manager.BringForward(o1.Id);
        vm.SyncFromManager();
        Assert.Equal(o1.Id, vm.Items[0].Id);
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

        // Toggle Lock on current selected -> Invariant I-03 deselects locked object
        vm.ToggleLockSelectedCommand.Execute(null);
        Assert.False(vm.HasSelectedObject); // Deselected per Invariant I-03
        Assert.True(vm.Items[0].IsLocked);

        // Toggle Lock off via item row
        vm.Items[0].ToggleLockCommand.Execute(null);
        Assert.False(vm.Items[0].IsLocked);

        // Re-select unlocked item
        vm.Items[0].SelectCommand.Execute(null);
        Assert.True(vm.HasSelectedObject);
        Assert.True(vm.CanDeleteSelected);

        // Delete Selected
        vm.DeleteSelectedCommand.Execute(null);
        Assert.Single(vm.Items);
    }

    [Fact]
    public void DrawingObjectItemViewModel_MoveAxisMode_CanBeSetIndividually()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        var item0 = vm.Items[0]; // o2
        var item1 = vm.Items[1]; // o1

        // Default is XY for both
        Assert.True(item0.IsMoveModeXY);
        Assert.True(item1.IsMoveModeXY);

        // Set item0 to X mode
        item0.SetMoveAxisModeCommand.Execute(DrawingMoveAxisMode.X);
        Assert.True(item0.IsMoveModeX);
        Assert.False(item0.IsMoveModeXY);
        Assert.False(item0.IsMoveModeY);

        // item1 remains in XY mode
        Assert.True(item1.IsMoveModeXY);
        Assert.False(item1.IsMoveModeX);

        // Set item1 to Y mode
        item1.SetMoveAxisModeCommand.Execute(DrawingMoveAxisMode.Y);
        Assert.True(item1.IsMoveModeY);
        Assert.False(item1.IsMoveModeXY);

        // item0 remains in X mode
        Assert.True(item0.IsMoveModeX);

        // Manager returns corresponding modes
        Assert.Equal(DrawingMoveAxisMode.X, manager.GetMoveAxisMode(item0.Id));
        Assert.Equal(DrawingMoveAxisMode.Y, manager.GetMoveAxisMode(item1.Id));
    }

    [Fact]
    public void DrawingObjectsViewModel_SelectingDifferentItems_TogglesSingleSelectionOnly()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        var item0 = vm.Items[0]; // o2
        var item1 = vm.Items[1]; // o1

        Assert.False(item0.IsSelected);
        Assert.False(item1.IsSelected);

        // Select item0
        item0.SelectCommand.Execute(null);
        Assert.True(item0.IsSelected);
        Assert.False(item1.IsSelected);
        Assert.Equal(item0.Id, manager.SelectedObject?.Id);

        // Select item1
        item1.SelectCommand.Execute(null);
        Assert.False(item0.IsSelected); // Previously selected item is deselected
        Assert.True(item1.IsSelected);
        Assert.Equal(item1.Id, manager.SelectedObject?.Id);
    }
}
