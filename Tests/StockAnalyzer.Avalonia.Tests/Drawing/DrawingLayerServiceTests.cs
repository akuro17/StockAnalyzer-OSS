using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingLayerServiceTests
{
    [Fact]
    public void CreateLayer_ValidName_AppendsToPanelOrderAndSetsActive()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;

        var result = service.CreateLayer(mainKey, "Trend Lines");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.TargetId);

        var layers = service.GetLayersForPanel(mainKey);
        Assert.Equal(2, layers.Count); // Default "Layer 1" + new "Trend Lines"
        Assert.Equal("Trend Lines", layers[1].Name);
        Assert.Equal(result.TargetId.Value, service.GetActiveLayerId(mainKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreateLayer_EmptyOrWhitespace_ReturnsInvalidArgument(string? name)
    {
        var service = new DrawingLayerService();
        var result = service.CreateLayer(PanelKey.Main, name!);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.InvalidArgument, result.Status);
    }

    [Fact]
    public void DeleteLayer_SoleRemainingLayer_ReturnsInvalidState()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var defaultLayer = service.GetLayersForPanel(mainKey)[0];

        var result = service.DeleteLayer(defaultLayer.LayerId, confirmed: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.InvalidState, result.Status);
    }

    [Fact]
    public void DeleteLayer_SoleRemainingLayer_NonMainPanel_SucceedsAndRemovesPanel()
    {
        var service = new DrawingLayerService();
        var subKey = PanelKey.Indicator("rsi-panel-1");
        var subLayer = service.EnsureDefaultLayer(subKey);

        Assert.Single(service.GetLayersForPanel(subKey));

        var result = service.DeleteLayer(subLayer.LayerId, confirmed: true);

        Assert.True(result.IsSuccess);
        Assert.Null(service.GetLayer(subLayer.LayerId));
        Assert.Empty(service.GetLayersForPanel(subKey));
        Assert.Null(service.GetActiveLayerId(subKey));
        Assert.Single(service.GetLayersForPanel(PanelKey.Main));
    }

    [Fact]
    public void DeleteLayer_NonEmptyWithoutConfirmation_ReturnsConfirmationRequired()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var created = service.CreateLayer(mainKey, "Extra Layer");
        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, created.TargetId);

        var result = service.DeleteLayer(created.TargetId!.Value, confirmed: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.ConfirmationRequired, result.Status);
    }

    [Fact]
    public void DeleteLayer_NonEmptyWithConfirmation_DeletesLayerAndObjects()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var created = service.CreateLayer(mainKey, "Extra Layer");
        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, created.TargetId);

        var result = service.DeleteLayer(created.TargetId!.Value, confirmed: true);

        Assert.True(result.IsSuccess);
        Assert.Null(service.GetLayer(created.TargetId!.Value));
        Assert.Null(service.GetLayerForObject(objId));
    }

    [Fact]
    public void DeleteLayer_ContainsLockedObject_ReturnsLockedAndRejects()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var created = service.CreateLayer(mainKey, "Protected Layer");
        var lockedObjId = Guid.NewGuid();
        service.AddObjectToLayer(lockedObjId, mainKey, created.TargetId);

        var result = service.DeleteLayer(
            created.TargetId!.Value,
            confirmed: true,
            isObjectLocked: id => id == lockedObjId);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Locked, result.Status);
    }

    [Fact]
    public void DeleteLayer_LayerEditLocked_ReturnsLockedAndRejects()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var created = service.CreateLayer(mainKey, "Locked Layer");
        service.SetLayerEditLock(created.TargetId!.Value, true);

        var result = service.DeleteLayer(created.TargetId!.Value, confirmed: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Locked, result.Status);
    }

    [Fact]
    public void RenameLayer_ValidName_Succeeds()
    {
        var service = new DrawingLayerService();
        var layer = service.GetLayersForPanel(PanelKey.Main)[0];

        var result = service.RenameLayer(layer.LayerId, "Renamed Layer");

        Assert.True(result.IsSuccess);
        var updated = service.GetLayer(layer.LayerId);
        Assert.Equal("Renamed Layer", updated?.Name);
    }

    [Fact]
    public void RenameLayer_WhenEditLocked_ReturnsLocked()
    {
        var service = new DrawingLayerService();
        var layer = service.GetLayersForPanel(PanelKey.Main)[0];
        service.SetLayerEditLock(layer.LayerId, true);

        var result = service.RenameLayer(layer.LayerId, "New Name");

        Assert.False(result.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Locked, result.Status);
    }

    [Fact]
    public void SetLayerVisibility_TogglesVisibilityWithoutAffectingLock()
    {
        var service = new DrawingLayerService();
        var layer = service.GetLayersForPanel(PanelKey.Main)[0];

        var resultHide = service.SetLayerVisibility(layer.LayerId, false);
        Assert.True(resultHide.IsSuccess);
        Assert.False(service.GetLayer(layer.LayerId)?.IsVisible);

        var resultShow = service.SetLayerVisibility(layer.LayerId, true);
        Assert.True(resultShow.IsSuccess);
        Assert.True(service.GetLayer(layer.LayerId)?.IsVisible);
    }

    [Fact]
    public void SetLayerEditLock_TogglesEditLock_AllowsUnlockWhenLocked()
    {
        var service = new DrawingLayerService();
        var layer = service.GetLayersForPanel(PanelKey.Main)[0];

        var lockResult = service.SetLayerEditLock(layer.LayerId, true);
        Assert.True(lockResult.IsSuccess);
        Assert.True(service.GetLayer(layer.LayerId)?.IsEditLocked);

        // Unlocking must succeed even when currently locked
        var unlockResult = service.SetLayerEditLock(layer.LayerId, false);
        Assert.True(unlockResult.IsSuccess);
        Assert.False(service.GetLayer(layer.LayerId)?.IsEditLocked);
    }

    [Fact]
    public void MoveLayer_ValidDelta_ReordersWithinPanel()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var defaultLayer = service.GetLayersForPanel(mainKey)[0];
        var created = service.CreateLayer(mainKey, "Top Layer");

        // Move default layer up (+1)
        var result = service.MoveLayer(defaultLayer.LayerId, +1);
        Assert.True(result.IsSuccess);

        var reordered = service.GetLayersForPanel(mainKey);
        Assert.Equal(created.TargetId, reordered[0].LayerId);
        Assert.Equal(defaultLayer.LayerId, reordered[1].LayerId);
    }

    [Fact]
    public void MoveLayer_BeyondBounds_ReturnsNoChange()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var defaultLayer = service.GetLayersForPanel(mainKey)[0];

        // Default layer is already at bottom (index 0), moving down (-1) must return NoChange
        var result = service.MoveLayer(defaultLayer.LayerId, -1);
        Assert.Equal(DrawingCommandStatus.NoChange, result.Status);
    }

    [Fact]
    public void MoveObjectToLayer_SamePanel_MovesObjectSuccessfully()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var layer1 = service.GetLayersForPanel(mainKey)[0];
        var layer2 = service.CreateLayer(mainKey, "Second Layer");

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, layer1.LayerId);

        var moveResult = service.MoveObjectToLayer(objId, layer2.TargetId!.Value);
        Assert.True(moveResult.IsSuccess);

        Assert.Equal(layer2.TargetId.Value, service.GetLayerForObject(objId)?.LayerId);
        Assert.DoesNotContain(objId, service.GetLayer(layer1.LayerId)!.Value.ObjectIds);
        Assert.Contains(objId, service.GetLayer(layer2.TargetId.Value)!.Value.ObjectIds);
    }

    [Fact]
    public void MoveObjectToLayer_CrossPanel_ReturnsCrossPanelAndRejects()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var subKey = PanelKey.Indicator("rsi-1");

        var mainLayer = service.GetLayersForPanel(mainKey)[0];
        var subLayer = service.EnsureDefaultLayer(subKey);

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, mainLayer.LayerId);

        var moveResult = service.MoveObjectToLayer(objId, subLayer.LayerId);
        Assert.False(moveResult.IsSuccess);
        Assert.Equal(DrawingCommandStatus.CrossPanel, moveResult.Status);
    }

    [Fact]
    public void MoveObjectToLayer_WhenTargetOrSourceEditLocked_ReturnsLocked()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var layer1 = service.GetLayersForPanel(mainKey)[0];
        var layer2 = service.CreateLayer(mainKey, "Second Layer");
        service.SetLayerEditLock(layer2.TargetId!.Value, true);

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, layer1.LayerId);

        var moveResult = service.MoveObjectToLayer(objId, layer2.TargetId.Value);
        Assert.False(moveResult.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Locked, moveResult.Status);
    }

    [Fact]
    public void AddObjectToLayer_EnforcesOneObjectPerLayer()
    {
        var service = new DrawingLayerService();
        var mainKey = PanelKey.Main;
        var layer1 = service.GetLayersForPanel(mainKey)[0];
        var layer2 = service.CreateLayer(mainKey, "Second Layer");

        var objId = Guid.NewGuid();
        service.AddObjectToLayer(objId, mainKey, layer1.LayerId);
        // Adding the same object to layer 2 directly must migrate it from layer 1
        service.AddObjectToLayer(objId, mainKey, layer2.TargetId!.Value);

        Assert.Equal(layer2.TargetId.Value, service.GetLayerForObject(objId)?.LayerId);
        Assert.DoesNotContain(objId, service.GetLayer(layer1.LayerId)!.Value.ObjectIds);
        Assert.Contains(objId, service.GetLayer(layer2.TargetId.Value)!.Value.ObjectIds);
    }

    [Fact]
    public void InitializeLayers_RestoresLayersAndReconstructsObjectMap()
    {
        var service = new DrawingLayerService();
        var layerId = Guid.NewGuid();
        var obj1 = Guid.NewGuid();
        var obj2 = Guid.NewGuid();

        var record = new DrawingLayerRecord(
            layerId,
            "Restored Layer",
            PanelKey.Main,
            isVisible: true,
            isEditLocked: false,
            new[] { obj1, obj2 });

        service.InitializeLayers(new[] { record });

        var loaded = service.GetLayer(layerId);
        Assert.NotNull(loaded);
        Assert.Equal("Restored Layer", loaded.Value.Name);
        Assert.Equal(2, loaded.Value.ObjectIds.Count);

        Assert.Equal(layerId, service.GetLayerForObject(obj1)?.LayerId);
        Assert.Equal(layerId, service.GetLayerForObject(obj2)?.LayerId);
    }
}
