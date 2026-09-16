using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingPhase2ImprovementTests
{
    private static TrendLineObject CreateSampleLine()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m);
        return new TrendLineObject(p1, p2);
    }

    #region F14: Lossless OverlayGroup Preservation

    [Fact]
    public void F14_PanelKey_PreservesExactOverlayGroupString()
    {
        var key1 = PanelKey.OverlayGroup("1");
        var key2 = PanelKey.OverlayGroup("01");
        var keyCustom = PanelKey.OverlayGroup("CUSTOM_GROUP");

        Assert.Equal("1", key1.OverlayGroupId);
        Assert.Equal("01", key2.OverlayGroupId);
        Assert.Equal("CUSTOM_GROUP", keyCustom.OverlayGroupId);

        // Distinct strings must NOT be equal, preventing hash-collision collapsing
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, keyCustom);
    }

    [Fact]
    public void F14_DrawingPanelResolver_CreateKeyFromSetting_PreservesExactOverlayString()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "test-rsi",
            OverlayPanelId = "AlphaGroup"
        };

        var key = DrawingPanelResolver.CreateKeyFromSetting(setting);

        Assert.Equal(PanelKind.OverlayGroup, key.Kind);
        Assert.Equal("AlphaGroup", key.OverlayGroupId);
    }

    #endregion

    #region F12: Atomic State Validation in InitializeLayers

    [Fact]
    public void F12_InitializeLayers_DuplicateLayerId_ThrowsAndPreservesExistingState()
    {
        var service = new DrawingLayerService();
        var initialLayers = service.GetAllLayers();
        Assert.Single(initialLayers);
        var initialLayerId = initialLayers[0].LayerId;

        var duplicateId = Guid.NewGuid();
        var invalidPayload = new[]
        {
            new DrawingLayerRecord(duplicateId, "Layer A", PanelKey.Main, true, false, Array.Empty<Guid>()),
            new DrawingLayerRecord(duplicateId, "Layer B", PanelKey.Main, true, false, Array.Empty<Guid>())
        };

        Assert.Throws<ArgumentException>(() => service.InitializeLayers(invalidPayload));

        // State must remain completely unchanged
        var afterLayers = service.GetAllLayers();
        Assert.Single(afterLayers);
        Assert.Equal(initialLayerId, afterLayers[0].LayerId);
    }

    [Fact]
    public void F12_InitializeLayers_DuplicateObjectIdAcrossLayers_ThrowsAndPreservesExistingState()
    {
        var service = new DrawingLayerService();
        var initialLayers = service.GetAllLayers();
        Assert.Single(initialLayers);
        var initialLayerId = initialLayers[0].LayerId;

        var sharedObjectId = Guid.NewGuid();
        var invalidPayload = new[]
        {
            new DrawingLayerRecord(Guid.NewGuid(), "Layer 1", PanelKey.Main, true, false, new[] { sharedObjectId }),
            new DrawingLayerRecord(Guid.NewGuid(), "Layer 2", PanelKey.Main, true, false, new[] { sharedObjectId })
        };

        Assert.Throws<ArgumentException>(() => service.InitializeLayers(invalidPayload));

        // State must remain completely unchanged
        var afterLayers = service.GetAllLayers();
        Assert.Single(afterLayers);
        Assert.Equal(initialLayerId, afterLayers[0].LayerId);
        Assert.False(service.IsObjectRegistered(sharedObjectId));
    }

    [Fact]
    public void F12_InitializeLayers_ValidPayload_AtomicallyLoads()
    {
        var service = new DrawingLayerService();
        var obj1 = Guid.NewGuid();
        var obj2 = Guid.NewGuid();
        var layer1Id = Guid.NewGuid();
        var layer2Id = Guid.NewGuid();

        var validPayload = new[]
        {
            new DrawingLayerRecord(layer1Id, "Main Layer", PanelKey.Main, true, false, new[] { obj1 }),
            new DrawingLayerRecord(layer2Id, "Sub Layer", PanelKey.Indicator("rsi-1"), true, true, new[] { obj2 })
        };

        service.InitializeLayers(validPayload);

        var all = service.GetAllLayers();
        Assert.Equal(2, all.Count);
        Assert.True(service.IsObjectRegistered(obj1));
        Assert.True(service.IsObjectRegistered(obj2));
        Assert.Equal(layer1Id, service.GetLayerForObject(obj1)?.LayerId);
        Assert.Equal(layer2Id, service.GetLayerForObject(obj2)?.LayerId);
    }

    #endregion

    #region F10: Selection, S06 Fallback, & Object Migration

    [Fact]
    public void F10_DrawingLayerItemViewModel_SelectCommand_SetsActiveLayer()
    {
        var service = new DrawingLayerService();
        var l1 = service.GetLayersForPanel(PanelKey.Main)[0];
        var l2Res = service.CreateLayer(PanelKey.Main, "Layer 2");
        var l2Id = l2Res.TargetId!.Value;

        // Currently l2 is active because it was just created
        Assert.Equal(l2Id, service.GetActiveLayerId(PanelKey.Main));

        var l1Record = service.GetLayer(l1.LayerId)!.Value;
        bool changedInvoked = false;
        var vm1 = new DrawingLayerItemViewModel(l1Record, service, () => changedInvoked = true);

        vm1.SelectCommand.Execute(null);

        Assert.True(changedInvoked);
        Assert.Equal(l1.LayerId, service.GetActiveLayerId(PanelKey.Main));
    }

    [Fact]
    public void F10_DeleteActiveLayer_FallsBackToFirstRemainingLayerPerS06()
    {
        var service = new DrawingLayerService();
        var l1 = service.GetLayersForPanel(PanelKey.Main)[0];
        var l2 = service.CreateLayer(PanelKey.Main, "Layer 2");
        var l3 = service.CreateLayer(PanelKey.Main, "Layer 3");

        // Set l3 as active
        service.SetActiveLayerId(PanelKey.Main, l3.TargetId!.Value);
        Assert.Equal(l3.TargetId!.Value, service.GetActiveLayerId(PanelKey.Main));

        // Delete active layer l3
        service.DeleteLayer(l3.TargetId!.Value);

        // Per S06, must fallback to first remaining layer (l1), NOT the last layer
        Assert.Equal(l1.LayerId, service.GetActiveLayerId(PanelKey.Main));
    }

    [Fact]
    public void F10_DrawingObjectItemViewModel_EligibleTargetLayers_And_MoveToLayer()
    {
        var service = new DrawingLayerService();
        var manager = new ChartObjectManager();
        var obj = CreateSampleLine();
        manager.AddObject(obj);

        var l1 = service.GetLayersForPanel(PanelKey.Main)[0];
        var l2 = service.CreateLayer(PanelKey.Main, "Target Layer 2");
        var l3Locked = service.CreateLayer(PanelKey.Main, "Locked Layer 3");
        service.SetLayerEditLock(l3Locked.TargetId!.Value, true);
        var subLayer = service.CreateLayer(PanelKey.Indicator("rsi-1"), "Sub Layer");

        service.AddObjectToLayer(obj.Id, PanelKey.Main, l1.LayerId);

        bool stateChanged = false;
        var itemVM = new DrawingObjectItemViewModel(obj, manager, onStateChanged: () => stateChanged = true, layerService: service);

        // Eligible layers must include l2, but NOT l1 (current), NOT l3Locked (locked), and NOT subLayer (cross-panel)
        var eligible = itemVM.EligibleTargetLayers;
        Assert.Single(eligible);
        Assert.Equal(l2.TargetId!.Value, eligible[0].LayerId);
        Assert.True(itemVM.HasEligibleTargetLayers);

        // Execute MoveToLayer
        itemVM.MoveToLayerCommand.Execute(l2.TargetId!.Value);

        Assert.True(stateChanged);
        Assert.Equal(l2.TargetId!.Value, service.GetLayerForObject(obj.Id)?.LayerId);
    }

    #endregion

    #region F13: Strict Confirmation Gating

    [Fact]
    public async Task F13_DrawingLayerItemViewModel_DeleteAsync_FailsWithoutCallbackForNonEmptyLayer()
    {
        var service = new DrawingLayerService();
        var manager = new ChartObjectManager();
        var obj = CreateSampleLine();
        manager.AddObject(obj);

        var l1 = service.GetLayersForPanel(PanelKey.Main)[0];
        service.CreateLayer(PanelKey.Main, "Extra Layer"); // Ensure panel has > 1 layers
        service.AddObjectToLayer(obj.Id, PanelKey.Main, l1.LayerId);

        var l1Record = service.GetLayer(l1.LayerId)!.Value;
        bool changed = false;
        // No delete callback provided:
        var vm = new DrawingLayerItemViewModel(l1Record, service, () => changed = true, deleteConfirmCallback: null);

        await vm.DeleteAsync();

        // Must NOT be deleted because confirmation was required
        Assert.False(changed);
        Assert.NotNull(service.GetLayer(l1.LayerId));
    }

    #endregion

    #region F11: Warm-Path IsObjectRegistered

    [Fact]
    public void F11_IsObjectRegistered_ReturnsExpectedResults()
    {
        var service = new DrawingLayerService();
        var objId = Guid.NewGuid();

        Assert.False(service.IsObjectRegistered(objId));

        service.AddObjectToLayer(objId, PanelKey.Main);
        Assert.True(service.IsObjectRegistered(objId));

        service.RemoveObjectFromLayer(objId);
        Assert.False(service.IsObjectRegistered(objId));
    }

    #endregion

    #region Task 1.1: CopySelected Source Layer Parity

    [Fact]
    public void Task1_1_DrawingObjectsViewModel_CopySelected_RegistersDuplicateToSourceLayer_EvenWhenActiveLayerDiffers()
    {
        var service = new DrawingLayerService();
        var manager = new ChartObjectManager();
        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();

        // 1. Initial setup: default Layer 1 in Main panel
        var l1 = service.GetLayersForPanel(PanelKey.Main)[0];

        // 2. Add object into Layer 1
        var obj = CreateSampleLine();
        manager.AddObject(obj);
        service.AddObjectToLayer(obj.Id, PanelKey.Main, l1.LayerId);

        // 3. Create Layer 2 and make it active
        var l2Result = service.CreateLayer(PanelKey.Main, "Layer 2");
        var l2Id = l2Result.TargetId!.Value;
        service.SetActiveLayerId(PanelKey.Main, l2Id);
        Assert.Equal(l2Id, service.GetActiveLayerId(PanelKey.Main));

        // 4. Create ViewModel and select object (which is in Layer 1)
        using var vm = new DrawingObjectsViewModel(manager, dispatcher, layerService: service);
        var itemVM = Assert.Single(vm.Items);
        itemVM.IsSelected = true;
        vm.NotifySelectedProperties();
        Assert.True(vm.CanCopySelected);

        // 5. Execute CopySelectedCommand
        vm.CopySelectedCommand.Execute(null);

        // 6. Verification: 2 objects in manager, both in Layer 1 (source layer), NOT Layer 2!
        Assert.Equal(2, manager.Objects.Count);
        var originalObj = manager.Objects.First(o => o.Id == obj.Id);
        var clonedObj = manager.Objects.First(o => o.Id != obj.Id);

        Assert.Equal(l1.LayerId, service.GetLayerForObject(originalObj.Id)?.LayerId);
        Assert.Equal(l1.LayerId, service.GetLayerForObject(clonedObj.Id)?.LayerId);
        Assert.NotEqual(l2Id, service.GetLayerForObject(clonedObj.Id)?.LayerId);
    }

    #endregion
}
