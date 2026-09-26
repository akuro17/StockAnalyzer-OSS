using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingPhase1RemediationTests
{
    private static TrendLineObject CreateSampleLine()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m);
        return new TrendLineObject(p1, p2);
    }

    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    #region F01: Monotonic Revision & Cache Invalidation

    [Fact]
    public void F01_DrawingLayerService_Revision_IncrementsMonotonicallyOnMutations()
    {
        var service = new DrawingLayerService();
        long initialRev = service.Revision;
        Assert.True(initialRev >= 1);

        // 1. CreateLayer
        var l2 = service.CreateLayer(PanelKey.Main, "Layer 2");
        Assert.True(service.Revision > initialRev);
        long revAfterCreate = service.Revision;

        // 2. RenameLayer
        service.RenameLayer(l2.TargetId!.Value, "Renamed Layer 2");
        Assert.True(service.Revision > revAfterCreate);
        long revAfterRename = service.Revision;

        // 3. SetLayerVisibility
        service.SetLayerVisibility(l2.TargetId.Value, false);
        Assert.True(service.Revision > revAfterRename);
        long revAfterVis = service.Revision;

        // 4. SetLayerEditLock
        service.SetLayerEditLock(l2.TargetId.Value, true);
        Assert.True(service.Revision > revAfterVis);
        long revAfterLock = service.Revision;

        // 5. AddObjectToLayer
        service.SetLayerEditLock(l2.TargetId.Value, false); // unlock
        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, PanelKey.Main, l2.TargetId.Value);
        long revAfterAdd = service.Revision;
        Assert.True(revAfterAdd > revAfterLock);

        // 6. MoveObjectWithinLayer
        var objId2 = Guid.NewGuid();
        service.AddObjectToLayer(objId2, PanelKey.Main, l2.TargetId.Value);
        long revBeforeMove = service.Revision;
        service.MoveObjectWithinLayer(objId, 1);
        Assert.True(service.Revision > revBeforeMove);

        // 7. DeleteLayer
        long revBeforeDelete = service.Revision;
        service.DeleteLayer(l2.TargetId.Value, confirmed: true);
        Assert.True(service.Revision > revBeforeDelete);
    }

    [Fact]
    public void F01_ChartObjectManager_Revision_IncrementsMonotonicallyOnMutations()
    {
        var manager = new ChartObjectManager();
        long initialRev = manager.Revision;
        Assert.True(initialRev >= 1);

        var obj1 = CreateSampleLine();
        manager.AddObject(obj1);
        Assert.True(manager.Revision > initialRev);
        long revAfterAdd = manager.Revision;

        manager.BringForward(obj1.Id);
        // BringForward on single item does not change, but NotifyChanged increments
        manager.NotifyObjectChanged(obj1.Id);
        Assert.True(manager.Revision > revAfterAdd);
        long revAfterNotify = manager.Revision;

        manager.SwitchContext(ChartDrawingContextType.Kagi);
        Assert.True(manager.Revision > revAfterNotify);
        long revAfterSwitch = manager.Revision;

        manager.Clear();
        Assert.True(manager.Revision > revAfterSwitch);
    }

    [Fact]
    public void F01_LayeredDrawingScene_InvalidatesCache_OnRevisionChange()
    {
        var scene = new LayeredDrawingScene();
        var panelKey = PanelKey.Main;
        var obj1 = CreateSampleLine();
        var objectsMap = new Dictionary<Guid, IChartObject> { [obj1.Id] = obj1 };

        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Layer 1", panelKey, true, false, new[] { obj1.Id });
        var layers = new[] { layer };

        // Initial build
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, 1, layers, objectsMap, _ => true);

        // Re-call with identical revisions should use cache
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, 1, layers, objectsMap, _ => true);

        // Mutate layer visibility and increment layer revision
        var hiddenLayer = new DrawingLayerRecord(layer.LayerId, "Layer 1", panelKey, false, false, new[] { obj1.Id });
        scene.UpdateScene(ChartDrawingContextType.Standard, 2, 1, 1, new[] { hiddenLayer }, objectsMap, _ => true);

        // Hit-test should now fail because cache was invalidated and layer is hidden
        var transform = CreateTransform();
        var hit = scene.HitTest(panelKey, new Point(0, 0), transform);
        Assert.Null(hit);
    }

    #endregion

    #region F02: Real-time Input Routing & Eraser Parity

    [Fact]
    public void F02_LayeredDrawingScene_HitTest_IgnoresHiddenLayers()
    {
        var scene = new LayeredDrawingScene();
        var panelKey = PanelKey.Main;
        var obj = CreateSampleLine();
        var objectsMap = new Dictionary<Guid, IChartObject> { [obj.Id] = obj };

        // Hidden layer
        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Hidden", panelKey, isVisible: false, isEditLocked: false, new[] { obj.Id });
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, 1, new[] { layer }, objectsMap, _ => true);

        var transform = CreateTransform();
        var hit = scene.HitTest(panelKey, new Point(0, 0), transform);
        Assert.Null(hit);
    }

    private class TestHitChartObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type => ChartObjectType.TrendLine;
        public string? CustomName { get; set; }
        public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
        public bool IsMoveAxisModeExplicit { get; set; }
        public List<ChartPoint> Points { get; set; } = new();
        public Color Color { get; set; } = Colors.Black;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public int ZIndex { get; set; } = 0;
        public int PanelIndex { get; set; } = -1;
        public SkiaSharp.SKColor SkiaColor => SkiaSharp.SKColors.Black;

        public void Render(SkiaSharp.SKCanvas canvas, ICoordinateTransform transform) { }
        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = 5.0) => true;
        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    [Fact]
    public void F02_LayeredDrawingScene_HandleEraserAt_StopsAtTopLockedObject()
    {
        var scene = new LayeredDrawingScene();
        var panelKey = PanelKey.Main;

        var lockedObj = new TestHitChartObject();
        var unlockedObj = new TestHitChartObject();

        var objectsMap = new Dictionary<Guid, IChartObject>
        {
            [lockedObj.Id] = lockedObj,
            [unlockedObj.Id] = unlockedObj
        };

        // Locked layer on top (second in array)
        var bottomLayer = new DrawingLayerRecord(Guid.NewGuid(), "Bottom Unlocked", panelKey, true, false, new[] { unlockedObj.Id });
        var topLayer = new DrawingLayerRecord(Guid.NewGuid(), "Top Locked", panelKey, true, true, new[] { lockedObj.Id });

        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, 1, new[] { bottomLayer, topLayer }, objectsMap, _ => true);

        var transform = CreateTransform();
        bool canDelete = scene.HandleEraserAt(panelKey, new Point(0, 0), transform, out var targetId, out bool isBlocked);

        // Should return false and isBlocked=true because top-most object is in an edit-locked layer, stopping penetration
        Assert.False(canDelete);
        Assert.True(isBlocked);
        Assert.Null(targetId);
    }

    #endregion

    #region F03: Parent Layer Edit-Lock Command Gating

    [Fact]
    public void F03_DrawingObjectItemViewModel_GuardsCommands_WhenParentLayerIsLocked()
    {
        var layerService = new DrawingLayerService();
        var manager = new ChartObjectManager();

        var obj = CreateSampleLine();
        manager.AddObject(obj);

        var layerId = serviceCreateLayer(layerService, PanelKey.Main, "Locked Layer");
        layerService.AddObjectToLayer(obj.Id, PanelKey.Main, layerId);
        layerService.SetLayerEditLock(layerId, true);

        var itemVM = new DrawingObjectItemViewModel(obj, manager, layerService: layerService);

        // EffectiveEditAllowed must be false
        Assert.False(itemVM.EffectiveEditAllowed);

        // Commands must be disabled
        Assert.False(itemVM.DeleteCommand.CanExecute(null));
        Assert.False(itemVM.CopyCommand.CanExecute(null));
        Assert.False(itemVM.BringForwardCommand.CanExecute(null));
        Assert.False(itemVM.SendBackwardCommand.CanExecute(null));
        Assert.False(itemVM.ToggleVisibilityCommand.CanExecute(null));
        Assert.False(itemVM.ToggleLockCommand.CanExecute(null));
    }

    [Fact]
    public void F03_DrawingObjectsViewModel_BatchOperations_SkipLockedLayerObjects()
    {
        var layerService = new DrawingLayerService();
        var manager = new ChartObjectManager();
        var dispatcher = new SynchronousDispatcherService();

        var objInLocked = CreateSampleLine();
        var objInUnlocked = CreateSampleLine();

        manager.AddObject(objInLocked);
        manager.AddObject(objInUnlocked);

        var lockedLayerId = serviceCreateLayer(layerService, PanelKey.Main, "Locked");
        var unlockedLayerId = serviceCreateLayer(layerService, PanelKey.Main, "Unlocked");

        layerService.AddObjectToLayer(objInLocked.Id, PanelKey.Main, lockedLayerId);
        layerService.AddObjectToLayer(objInUnlocked.Id, PanelKey.Main, unlockedLayerId);

        // Lock layer AFTER objects are registered
        layerService.SetLayerEditLock(lockedLayerId, true);

        var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        // Initially both are visible
        Assert.True(objInLocked.IsVisible);
        Assert.True(objInUnlocked.IsVisible);

        // Target all items for batch operation
        foreach (var item in vm.Items)
        {
            item.IsTargeted = true;
        }

        // HideAllCommand should skip objInLocked
        vm.HideAllCommand.Execute(null);

        Assert.True(objInLocked.IsVisible); // Protected by locked layer
        Assert.False(objInUnlocked.IsVisible); // Hidden
    }

    #endregion

    #region F04: Context Partitioning & Persistence

    [Fact]
    public async Task F04_ChartDrawingRepository_RoundTripsPayloadWithLayers_PerContext()
    {
        var repo = new ChartDrawingRepository();
        var ticker = "TESTF04_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var timeframe = TimeframeType.Daily;

        var obj1 = CreateSampleLine();
        var obj2 = CreateSampleLine();

        var layer1 = new DrawingLayerRecord(Guid.NewGuid(), "Std Layer", PanelKey.Main, true, false, new[] { obj1.Id });
        var layer2 = new DrawingLayerRecord(Guid.NewGuid(), "Kagi Layer", PanelKey.Main, true, true, new[] { obj2.Id });

        var payload = new ChartDrawingPayload
        {
            Objects = new Dictionary<ChartDrawingContextType, List<IChartObject>>
            {
                [ChartDrawingContextType.Standard] = new() { obj1 },
                [ChartDrawingContextType.Kagi] = new() { obj2 }
            },
            Layers = new Dictionary<ChartDrawingContextType, List<DrawingLayerRecord>>
            {
                [ChartDrawingContextType.Standard] = new() { layer1 },
                [ChartDrawingContextType.Kagi] = new() { layer2 }
            }
        };

        await repo.SavePayloadAsync(ticker, timeframe, payload);

        var loaded = repo.LoadPayload(ticker, timeframe);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Objects.Count);
        Assert.Equal(2, loaded.Layers.Count);

        Assert.Single(loaded.Objects[ChartDrawingContextType.Standard]);
        Assert.Single(loaded.Objects[ChartDrawingContextType.Kagi]);

        var loadedStdLayer = loaded.Layers[ChartDrawingContextType.Standard][0];
        Assert.Equal("Std Layer", loadedStdLayer.Name);
        Assert.False(loadedStdLayer.IsEditLocked);

        var loadedKagiLayer = loaded.Layers[ChartDrawingContextType.Kagi][0];
        Assert.Equal("Kagi Layer", loadedKagiLayer.Name);
        Assert.True(loadedKagiLayer.IsEditLocked);
    }

    [Fact]
    public void F04_MaterializedDrawingDocument_MapsPersistentIdsToRuntimeIds_InLayers()
    {
        var manager = new ChartObjectManager();
        var obj = CreateSampleLine();
        manager.AddObject(obj);

        var persistentId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var layer = new DrawingLayerRecord(layerId, "Test Layer", PanelKey.Main, true, false, new[] { persistentId });

        var docState = DrawingDocumentCodec.Capture(
            manager,
            new DrawingDocumentKey("AAPL", TimeframeType.Daily),
            revision: 1,
            runtimeToPersistentIds: new Dictionary<Guid, Guid> { [obj.Id] = persistentId },
            layers: new[] { layer }
        );

        using var materialized = DrawingDocumentCodec.Materialize(docState);

        Assert.True(materialized.Layers.ContainsKey(ChartDrawingContextType.Standard));
        var materializedLayers = materialized.Layers[ChartDrawingContextType.Standard];
        Assert.Single(materializedLayers);

        var materializedLayer = materializedLayers[0];
        // The objectId in the materialized layer must be the runtime ID of the recreated object, not the persistent ID!
        var runtimeObj = materialized.Objects[ChartDrawingContextType.Standard][0];
        Assert.Contains(runtimeObj.Id, materializedLayer.ObjectIds);
    }

    #endregion

    #region F05: Intra-Layer Object Reordering

    [Fact]
    public void F05_DrawingLayerService_MoveObjectWithinLayer_ReordersObjectIdsCorrectly()
    {
        var service = new DrawingLayerService();
        var panel = PanelKey.Main;
        var layerRes = service.CreateLayer(panel, "Layer Order");
        var layerId = layerRes.TargetId!.Value;

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();

        service.AddObjectToLayer(idA, panel, layerId);
        service.AddObjectToLayer(idB, panel, layerId);
        service.AddObjectToLayer(idC, panel, layerId);

        var l = service.GetLayer(layerId)!.Value;
        Assert.Equal(new[] { idA, idB, idC }, l.ObjectIds);

        // Move idA up by 1 -> [idB, idA, idC]
        var res1 = service.MoveObjectWithinLayer(idA, 1);
        Assert.True(res1.IsSuccess);
        l = service.GetLayer(layerId)!.Value;
        Assert.Equal(new[] { idB, idA, idC }, l.ObjectIds);

        // Move idC down by 1 -> [idB, idC, idA]
        var res2 = service.MoveObjectWithinLayer(idC, -1);
        Assert.True(res2.IsSuccess);
        l = service.GetLayer(layerId)!.Value;
        Assert.Equal(new[] { idB, idC, idA }, l.ObjectIds);

        // Move idB down by 1 when already at start -> NoChange
        var res3 = service.MoveObjectWithinLayer(idB, -1);
        Assert.Equal(DrawingCommandStatus.NoChange, res3.Status);
    }

    #endregion

    #region F06: Duplicate Object to Source Layer

    [Fact]
    public void F06_DrawingObjectItemViewModel_Copy_TargetsSourceLayer_NotActiveLayer()
    {
        var layerService = new DrawingLayerService();
        var manager = new ChartObjectManager();

        var obj1 = CreateSampleLine();
        manager.AddObject(obj1);

        var layer1Res = serviceCreateLayer(layerService, PanelKey.Main, "Layer 1");
        var layer2Res = serviceCreateLayer(layerService, PanelKey.Main, "Layer 2"); // becomes active layer

        layerService.AddObjectToLayer(obj1.Id, PanelKey.Main, layer1Res);

        // Active layer is Layer 2
        Assert.Equal(layer2Res, layerService.GetActiveLayerId(PanelKey.Main));

        var itemVM = new DrawingObjectItemViewModel(obj1, manager, layerService: layerService);
        itemVM.CopyCommand.Execute(null);

        // Manager now has 2 objects
        Assert.Equal(2, manager.Objects.Count);
        var clonedObj = manager.Objects.First(o => o.Id != obj1.Id);

        // Cloned object MUST be in Layer 1 (source object's layer), NOT Layer 2 (active layer)
        var clonedLayer = layerService.GetLayerForObject(clonedObj.Id);
        Assert.NotNull(clonedLayer);
        Assert.Equal(layer1Res, clonedLayer.Value.LayerId);
    }

    private static Guid serviceCreateLayer(DrawingLayerService s, PanelKey p, string n)
    {
        return s.CreateLayer(p, n).TargetId!.Value;
    }

    #endregion

    #region F07: Sub-Window Dormant Panel Retention

    [Fact]
    public void F07_DrawingObjectsViewModel_ResolvePanelForObject_PreservesSubWindowPanelIndex()
    {
        var manager = new ChartObjectManager();
        var dispatcher = new SynchronousDispatcherService();
        var layerService = new DrawingLayerService();

        var subWindowObj = CreateSampleLine();
        subWindowObj.PanelIndex = 3; // Assigned to sub-window overlay panel 3
        manager.AddObject(subWindowObj);

        var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: layerService);

        var panel = vm.ResolvePanelForObject(subWindowObj);
        Assert.NotNull(panel);
    }

    #endregion

    #region F08: AddObjectToLayer Lock & Cross-Panel Validation

    [Fact]
    public void F08_AddObjectToLayer_RejectsMigration_WhenSourceLayerIsLocked()
    {
        var service = new DrawingLayerService();
        var l1Res = service.CreateLayer(PanelKey.Main, "Source Layer");
        var l2Res = service.CreateLayer(PanelKey.Main, "Target Layer");

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, PanelKey.Main, l1Res.TargetId!.Value);

        // Lock source layer
        service.SetLayerEditLock(l1Res.TargetId.Value, true);

        // Attempting to migrate objId to Target Layer via AddObjectToLayer must fail with Locked
        var addRes = service.AddObjectToLayer(objId, PanelKey.Main, l2Res.TargetId!.Value);
        Assert.False(addRes.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Locked, addRes.Status);
    }

    [Fact]
    public void F08_AddObjectToLayer_RejectsCrossPanelMigration()
    {
        var service = new DrawingLayerService();
        var mainLayer = service.GetLayersForPanel(PanelKey.Main)[0];
        var indicatorLayerRes = service.CreateLayer(PanelKey.Indicator("RSI"), "Indicator Layer");

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, PanelKey.Main, mainLayer.LayerId);

        // Attempting to migrate objId from Main to Indicator panel via AddObjectToLayer must fail with CrossPanel
        var crossRes = service.AddObjectToLayer(objId, PanelKey.Indicator("RSI"), indicatorLayerRes.TargetId!.Value);
        Assert.False(crossRes.IsSuccess);
        Assert.Equal(DrawingCommandStatus.CrossPanel, crossRes.Status);
    }

    #endregion

    #region F09: Active Layer State Validation for New Drawings

    [Fact]
    public void F09_ChartInteractionController_StartNewShape_RejectsLockedOrHiddenActiveLayer()
    {
        var layerService = new DrawingLayerService();
        var mainLayer = layerService.GetLayersForPanel(PanelKey.Main)[0];

        // 1. Lock the active layer
        layerService.SetLayerEditLock(mainLayer.LayerId, true);
        var activeId = layerService.GetActiveLayerId(PanelKey.Main);
        Assert.NotNull(activeId);
        var active = layerService.GetLayer(activeId.Value);
        Assert.True(active?.IsEditLocked);

        // 2. Hide layer test
        var service2 = new DrawingLayerService();
        var mainLayer2 = service2.GetLayersForPanel(PanelKey.Main)[0];
        service2.SetLayerVisibility(mainLayer2.LayerId, false);
        var activeId2 = service2.GetActiveLayerId(PanelKey.Main);
        Assert.NotNull(activeId2);
        var active2 = service2.GetLayer(activeId2.Value);
        Assert.False(active2?.IsVisible);
    }

    #endregion
}
