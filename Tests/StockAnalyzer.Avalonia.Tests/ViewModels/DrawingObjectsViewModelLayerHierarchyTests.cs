using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DrawingObjectsViewModelLayerHierarchyTests
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
        public int PanelIndex { get; set; } = -1;

        public void Render(SKCanvas canvas, ICoordinateTransform transform) { }
        public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance) => true;
        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    [Fact]
    public void DrawingObjectsViewModel_PopulatesLayersAndChildren_InHierarchy()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        var o2 = new TestDrawingObject { Type = ChartObjectType.HorizontalLine };
        manager.AddObject(o1);
        manager.AddObject(o2);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        Assert.True(vm.HasLayers);
        Assert.True(vm.HasItems);
        Assert.Equal(2, vm.Items.Count);
        Assert.Single(vm.Layers);

        var defaultLayer = vm.Layers[0];
        Assert.Equal(2, defaultLayer.Children.Count);
        // Children should be in descending ZIndex order
        Assert.Equal(o2.Id, defaultLayer.Children[0].Id);
        Assert.Equal(o1.Id, defaultLayer.Children[1].Id);
    }

    [Fact]
    public void DrawingObjectsViewModel_CreateLayerCommand_AddsLayer()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        int initialCount = vm.Layers.Count;
        vm.CreateLayerCommand.Execute(null);

        Assert.Equal(initialCount + 1, vm.Layers.Count);
    }

    [Fact]
    public void DrawingObjectsViewModel_LayerVisibilityToggle_PropagatesToService()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        var layerVM = vm.Layers[0];
        Assert.True(layerVM.IsVisible);

        layerVM.ToggleVisibilityCommand.Execute(null);

        Assert.False(layerVM.IsVisible);
        var record = layerService.GetLayer(layerVM.LayerId);
        Assert.NotNull(record);
        Assert.False(record.Value.IsVisible);
    }

    [Fact]
    public void DrawingObjectsViewModel_LayerLockToggle_PropagatesToService()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        var layerVM = vm.Layers[0];
        Assert.False(layerVM.IsEditLocked);

        layerVM.ToggleLockCommand.Execute(null);

        Assert.True(layerVM.IsEditLocked);
        var record = layerService.GetLayer(layerVM.LayerId);
        Assert.NotNull(record);
        Assert.True(record.Value.IsEditLocked);
    }

    [Fact]
    public void DrawingObjectsViewModel_DeleteLayer_RemovesFromService()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        // Create a second layer so we don't delete the sole layer
        vm.CreateLayerCommand.Execute(null);
        Assert.Equal(2, vm.Layers.Count);

        var topLayer = vm.Layers[0];
        topLayer.DeleteCommand.Execute(null);

        Assert.Single(vm.Layers);
        Assert.Null(layerService.GetLayer(topLayer.LayerId));
    }

    [Fact]
    public void DrawingObjectsViewModel_CommitPendingRename_CommitsLayerRename()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        var layerVM = vm.Layers[0];
        layerVM.StartRenameCommand.Execute(null);
        layerVM.EditingName = "Renamed Layer Name";

        vm.CommitPendingRename();

        Assert.False(layerVM.IsEditingName);
        Assert.Equal("Renamed Layer Name", layerVM.Name);
        var record = layerService.GetLayer(layerVM.LayerId);
        Assert.Equal("Renamed Layer Name", record?.Name);
    }

    [Fact]
    public void DrawingObjectsViewModel_MoveLayer_UpdatesUIOrder()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        // Initially 1 layer: "Layer 1"
        // Create second layer: "Layer 2"
        vm.CreateLayerCommand.Execute(null);
        Assert.Equal(2, vm.Layers.Count);

        // In UI descending order, top layer is index 0
        var firstId = vm.Layers[0].LayerId;
        var secondId = vm.Layers[1].LayerId;

        // Move bottom layer (vm.Layers[1]) up
        vm.Layers[1].MoveUpCommand.Execute(null);

        // Orders should have swapped
        Assert.Equal(secondId, vm.Layers[0].LayerId);
        Assert.Equal(firstId, vm.Layers[1].LayerId);
    }

    [Fact]
    public void DrawingObjectsViewModel_SyncFromManager_PrunesEmptyDormantNonMainLayers()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        var dormantKey = PanelKey.Indicator("dormant-ind-1");
        layerService.EnsureDefaultLayer(dormantKey);
        Assert.Equal(2, layerService.GetAllLayers().Count);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        // Dormant empty layer should be auto-pruned on sync
        Assert.Single(vm.Layers);
        Assert.Equal(PanelKey.Main, vm.Layers[0].Panel);
        Assert.Empty(layerService.GetLayersForPanel(dormantKey));
    }

    [Fact]
    public void DrawingObjectsViewModel_LayerItemViewModel_CanDelete_EnforcesMainVsNonMainRules()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        // Sole Main layer cannot be deleted
        var mainLayerVM = vm.Layers[0];
        Assert.False(mainLayerVM.CanDelete);
        Assert.False(mainLayerVM.DeleteCommand.CanExecute(null));

        // Adding a second Main layer enables deletion on both
        vm.CreateLayerCommand.Execute(null);
        Assert.Equal(2, vm.Layers.Count);
        Assert.True(vm.Layers[0].CanDelete);
        Assert.True(vm.Layers[0].DeleteCommand.CanExecute(null));
        Assert.True(vm.Layers[1].CanDelete);
        Assert.True(vm.Layers[1].DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void DrawingObjectsViewModel_PanelDisplayName_ResolvesCorrectly()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        Assert.Equal("Main", vm.ResolvePanelDisplayName(PanelKey.Main));
        Assert.Equal("Overlay 2", vm.ResolvePanelDisplayName(PanelKey.OverlayGroup(2)));
        Assert.Equal("Indicator", vm.ResolvePanelDisplayName(PanelKey.Indicator("some-ind")));
    }
}
