using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Link-group behavior owned by <see cref="ChartObjectManager"/> and its persistence / history paths:
/// deletion cleanup, V2 document round trip, legacy positional round trip, history equality and Undo/Redo.
/// </summary>
public class LinkedObjectPersistenceTests
{
    private static readonly DrawingDocumentKey Key = new("7203", TimeframeType.Daily);

    private static TrendLineObject NewLine(int day, decimal price)
        => new(new ChartPoint(new DateTime(2025, 1, day), price), new ChartPoint(new DateTime(2025, 1, day + 3), price + 10m));

    private static (ChartObjectManager manager, TrendLineObject[] lines) CreateLinkedManager()
    {
        var manager = new ChartObjectManager();
        var lines = new[] { NewLine(2, 100m), NewLine(4, 120m), NewLine(6, 140m) };
        foreach (var line in lines) manager.AddObject(line);
        Assert.Equal(LinkResult.Ok, manager.LinkObjects(lines[0].Id, new[] { lines[1].Id, lines[2].Id }));
        return (manager, lines);
    }

    // ---- ChartObjectManager ---------------------------------------------------------------

    [Fact]
    public void LinkObjects_UnknownObject_IsRejectedAndNothingChanges()
    {
        var manager = new ChartObjectManager();
        var a = NewLine(2, 100m);
        manager.AddObject(a);

        Assert.Equal(LinkResult.UnknownObject, manager.LinkObjects(a.Id, new[] { Guid.NewGuid() }));
        Assert.Equal(LinkRole.None, manager.GetLinkRole(a.Id));
    }

    [Fact]
    public void LinkAndUnlink_FireChangedOnlyWhenStateActuallyChanged()
    {
        var manager = new ChartObjectManager();
        var a = NewLine(2, 100m);
        var b = NewLine(4, 120m);
        manager.AddObject(a);
        manager.AddObject(b);
        int changed = 0;
        manager.Changed += () => changed++;

        manager.LinkObjects(a.Id, new[] { b.Id });
        Assert.Equal(1, changed);

        manager.LinkObjects(a.Id, new[] { b.Id }); // nothing new to add
        Assert.Equal(1, changed);

        Assert.Equal(1, manager.UnlinkObjects(new[] { b.Id }));
        Assert.Equal(2, changed);
        Assert.Equal(0, manager.UnlinkObjects(new[] { b.Id }));
        Assert.Equal(2, changed);
    }

    [Fact]
    public void RemoveObject_ParentDissolvesGroup_ChildOnlyLeaves()
    {
        var (manager, lines) = CreateLinkedManager();

        Assert.True(manager.RemoveObject(lines[1].Id));
        Assert.True(manager.TryGetLinkedMembers(lines[0].Id, out var members));
        Assert.Equal(new[] { lines[0].Id, lines[2].Id }, members.ToArray());

        Assert.True(manager.RemoveObject(lines[0].Id));
        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[2].Id));
    }

    [Fact]
    public void DeleteAll_SkipsLockedObjects_AndCleansLinksOfDeletedOnes()
    {
        var (manager, lines) = CreateLinkedManager();
        manager.ToggleLock(lines[2].Id);

        manager.DeleteAll();

        Assert.Equal(1, manager.Count);
        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[2].Id)); // its parent was deleted -> group dissolved
    }

    [Fact]
    public void DuplicateObject_CloneIsNotLinked()
    {
        var (manager, lines) = CreateLinkedManager();

        var clone = manager.DuplicateObject(lines[0].Id);

        Assert.NotNull(clone);
        Assert.Equal(LinkRole.None, manager.GetLinkRole(clone!.Id));
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(lines[0].Id));
    }

    [Fact]
    public void Clear_And_ContextSwitch_KeepGroupsPerContext()
    {
        var (manager, lines) = CreateLinkedManager();

        manager.SwitchContext(ChartDrawingContextType.Renko);
        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[0].Id));
        manager.SwitchContext(ChartDrawingContextType.Standard);
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(lines[0].Id));

        manager.Clear();
        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[0].Id));
    }

    // ---- V2 document codec ----------------------------------------------------------------

    [Fact]
    public void Codec_CaptureSerializeMaterialize_RestoresLinkGroupsWithPersistentIds()
    {
        var (manager, _) = CreateLinkedManager();

        var state = DrawingDocumentCodec.Capture(manager, Key, revision: 1);
        var standard = state.Contexts.Single(c => c.ContextName == nameof(ChartDrawingContextType.Standard));
        var group = Assert.Single(standard.LinkGroups);
        var objectIds = standard.Objects.Select(o => o.ObjectId).ToList();
        Assert.Equal(objectIds[0], group.ParentObjectId);
        Assert.Equal(new[] { objectIds[1], objectIds[2] }, group.ChildObjectIds.ToArray());

        var json = DrawingDocumentCodec.SerializeToJson(state);
        var reloaded = DrawingDocumentCodec.DeserializeFromJson(json);
        using var materialized = DrawingDocumentCodec.Materialize(reloaded);

        var restored = new ChartObjectManager();
        restored.LoadSnapshot(materialized.Objects);
        materialized.DetachObjects();
        foreach (var (context, records) in materialized.LinkGroups)
        {
            restored.LoadLinkGroups(context, records, materialized.PersistentToRuntimeIds);
        }

        var objects = restored.Objects;
        Assert.Equal(3, objects.Count);
        Assert.Equal(LinkRole.Parent, restored.GetLinkRole(objects[0].Id));
        Assert.Equal(LinkRole.Child, restored.GetLinkRole(objects[1].Id));
        Assert.Equal(LinkRole.Child, restored.GetLinkRole(objects[2].Id));
    }

    [Fact]
    public void Codec_DocumentWithoutLinkGroupsProperty_LoadsWithEmptyGroups()
    {
        var manager = new ChartObjectManager();
        manager.AddObject(NewLine(2, 100m));
        var json = DrawingDocumentCodec.SerializeToJson(DrawingDocumentCodec.Capture(manager, Key, 1));

        // Simulate a file written before link groups existed: drop the property from every context.
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var context in root["contexts"]!.AsArray())
        {
            Assert.True(context!.AsObject().Remove("linkGroups"));
        }
        var stripped = root.ToJsonString();
        Assert.DoesNotContain("linkGroups", stripped);

        var reloaded = DrawingDocumentCodec.DeserializeFromJson(stripped);
        Assert.All(reloaded.Contexts, c => Assert.Empty(c.LinkGroups));
    }

    // ---- Legacy positional payload --------------------------------------------------------

    [Fact]
    public void LegacyIndexRecords_SurviveJsonRoundTripAndRestoreOntoFreshObjects()
    {
        var (manager, _) = CreateLinkedManager();

        var payload = new ChartDrawingPayload { LinkGroups = manager.GetLinkGroupIndexRecords() };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(payload.LinkGroups, options);
        var loaded = JsonSerializer.Deserialize<Dictionary<ChartDrawingContextType, List<DrawingLinkGroupIndexRecord>>>(json, options)!;

        var fresh = new ChartObjectManager();
        var freshLines = new[] { NewLine(2, 100m), NewLine(4, 120m), NewLine(6, 140m) };
        foreach (var line in freshLines) fresh.AddObject(line);
        foreach (var (context, records) in loaded) fresh.LoadLinkGroupIndexRecords(context, records);

        Assert.Equal(LinkRole.Parent, fresh.GetLinkRole(freshLines[0].Id));
        Assert.Equal(LinkRole.Child, fresh.GetLinkRole(freshLines[1].Id));
        Assert.Equal(LinkRole.Child, fresh.GetLinkRole(freshLines[2].Id));
    }

    [Fact]
    public void LegacyIndexRecords_OmitContextsWithoutGroups()
    {
        var manager = new ChartObjectManager();
        manager.AddObject(NewLine(2, 100m));

        Assert.Empty(manager.GetLinkGroupIndexRecords());
    }

    // ---- History equality -----------------------------------------------------------------

    [Fact]
    public void AreSemanticallyEqual_DetectsLinkOnlyDifferences()
    {
        var (manager, lines) = CreateLinkedManager();
        // Fixed runtime -> persistent id map so both captures describe the same objects.
        var ids = manager.Objects.ToDictionary(o => o.Id, _ => Guid.NewGuid());
        var linked = FrozenContextState.FromContextState(
            DrawingDocumentCodec.Capture(manager, Key, 1, ids).Contexts.Single(c => c.ContextName == "Standard"));

        var sameAgain = FrozenContextState.FromContextState(
            DrawingDocumentCodec.Capture(manager, Key, 1, ids).Contexts.Single(c => c.ContextName == "Standard"));
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(linked, sameAgain));

        // Same objects and geometry, but no link groups: must not be reported as "no change".
        var withoutLinks = new FrozenContextState(linked.ContextName, linked.Layers, linked.Objects);
        Assert.False(DrawingHistoryService.AreSemanticallyEqual(linked, withoutLinks));

        var differentChildren = new FrozenContextState(
            linked.ContextName, linked.Layers, linked.Objects,
            new[] { new DrawingLinkGroupRecord(linked.LinkGroups[0].ParentObjectId, new[] { linked.LinkGroups[0].ChildObjectIds[0] }) });
        Assert.False(DrawingHistoryService.AreSemanticallyEqual(linked, differentChildren));
        Assert.NotEmpty(lines);
    }

    // ---- Undo / Redo through the coordinator ------------------------------------------------

    [Fact]
    public void Coordinator_LinkAndUnlink_AreUndoableAndRedoable()
    {
        var manager = new ChartObjectManager();
        var session = new DrawingDocumentSession(Key);
        using var history = new DrawingHistoryService(Key);
        using var coordinator = new DrawingEditCoordinator(manager, history, session);

        var lines = new[] { NewLine(2, 100m), NewLine(4, 120m), NewLine(6, 140m) };
        coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
        {
            foreach (var line in lines) manager.AddObject(line);
        });

        var link = coordinator.ExecuteEdit(DrawingOperationKind.LinkObjects, () =>
            manager.LinkObjects(lines[0].Id, new[] { lines[1].Id, lines[2].Id }));
        Assert.True(link.IsSuccess);
        Assert.Equal(2, coordinator.UndoCount);

        Assert.True(coordinator.Undo().IsSuccess);
        Assert.All(manager.Objects, o => Assert.Equal(LinkRole.None, manager.GetLinkRole(o.Id)));

        Assert.True(coordinator.Redo().IsSuccess);
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(manager.Objects[0].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(manager.Objects[1].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(manager.Objects[2].Id));

        var unlink = coordinator.ExecuteEdit(DrawingOperationKind.UnlinkObjects, () =>
            manager.UnlinkObjects(new[] { manager.Objects[1].Id }));
        Assert.True(unlink.IsSuccess);
        Assert.All(manager.Objects, o => Assert.Equal(LinkRole.None, manager.GetLinkRole(o.Id)));

        Assert.True(coordinator.Undo().IsSuccess);
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(manager.Objects[0].Id));
    }
}
