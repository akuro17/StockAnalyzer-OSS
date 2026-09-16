using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingHistoryServiceTests
{
    private static readonly DrawingDocumentKey TestKey = new("7203", TimeframeType.Daily);

    private static readonly Guid FixedObjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FixedLayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static FrozenContextState CreateTestState(
        string contextName = "Standard",
        Guid? objectId = null,
        Guid? layerId = null,
        decimal price = 1000m,
        string colorHex = "#FF0000")
    {
        var objId = objectId ?? FixedObjectId;
        var lid = layerId ?? FixedLayerId;
        var points = new List<DrawingStoredPoint>
        {
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), price),
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), price + 50m)
        };

        var parameters = new Dictionary<string, object?>
        {
            ["color"] = colorHex,
            ["thickness"] = 2.0
        };

        var objects = new List<DrawingObjectRecord>
        {
            new(
                objId,
                "TrendLineObject",
                PanelKey.Main,
                DrawingCoordinateKind.UtcTime,
                points,
                parameters
            )
        };

        var layers = new List<DrawingLayerRecord>
        {
            new(
                lid,
                "Default Layer",
                PanelKey.Main,
                isVisible: true,
                isEditLocked: false,
                objectIds: new[] { objId }
            )
        };

        return new FrozenContextState(contextName, layers, objects);
    }

    [Fact]
    public void LawH1_UndoRestoresBefore_RedoRestoresAfter()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1500m);

        var beginResult = service.BeginEdit(DrawingOperationKind.Move, s0);
        Assert.True(beginResult.IsSuccess);
        Assert.False(beginResult.Token.IsEmpty);

        var commitResult = service.Commit(beginResult.Token, s1);
        Assert.True(commitResult.IsSuccess);
        Assert.Equal(1, service.UndoCount);
        Assert.Equal(0, service.RedoCount);
        Assert.True(service.CanUndo);
        Assert.False(service.CanRedo);

        // Undo -> restores S0
        var undoResult = service.Undo();
        Assert.True(undoResult.IsSuccess);
        Assert.NotNull(undoResult.RestoredState);
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(s0, undoResult.RestoredState.Value));
        Assert.Equal(0, service.UndoCount);
        Assert.Equal(1, service.RedoCount);
        Assert.False(service.CanUndo);
        Assert.True(service.CanRedo);

        // Redo -> restores S1
        var redoResult = service.Redo();
        Assert.True(redoResult.IsSuccess);
        Assert.NotNull(redoResult.RestoredState);
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(s1, redoResult.RestoredState.Value));
        Assert.Equal(1, service.UndoCount);
        Assert.Equal(0, service.RedoCount);
        Assert.True(service.CanUndo);
        Assert.False(service.CanRedo);
    }

    [Fact]
    public void LawH2_NewCommitAfterUndo_ClearsRedoStack()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1100m);
        var s2 = CreateTestState(price: 1200m);
        var s3 = CreateTestState(price: 1300m);

        // S0 -> S1
        var t1 = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        service.Commit(t1, s1);

        // S1 -> S2
        var t2 = service.BeginEdit(DrawingOperationKind.Move, s1).Token;
        service.Commit(t2, s2);

        Assert.Equal(2, service.UndoCount);
        Assert.Equal(0, service.RedoCount);

        // Undo -> S1 (redo has S2)
        var undo = service.Undo();
        Assert.True(undo.IsSuccess);
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(s1, undo.RestoredState!.Value));
        Assert.Equal(1, service.UndoCount);
        Assert.Equal(1, service.RedoCount);

        // New operation S3 after Undo
        var t3 = service.BeginEdit(DrawingOperationKind.PointEdit, s1).Token;
        var commit3 = service.Commit(t3, s3);
        Assert.True(commit3.IsSuccess);

        // Redo stack must now be empty, Redo returns NoChange
        Assert.Equal(2, service.UndoCount);
        Assert.Equal(0, service.RedoCount);
        Assert.False(service.CanRedo);

        var redo = service.Redo();
        Assert.Equal(DrawingCommandStatus.NoChange, redo.Status);
    }

    [Fact]
    public void LawH3_EmptyUndoRedo_ReturnsNoChange_HistoryCountUnchanged()
    {
        using var service = new DrawingHistoryService(TestKey);

        var undo = service.Undo();
        Assert.Equal(DrawingCommandStatus.NoChange, undo.Status);
        Assert.Equal(0, service.UndoCount);

        var redo = service.Redo();
        Assert.Equal(DrawingCommandStatus.NoChange, redo.Status);
        Assert.Equal(0, service.RedoCount);
    }

    [Fact]
    public void LawH3_NoChange_CommitDoesNotAddEntry()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var sIdentical = CreateTestState(price: 1000m);

        var token = service.BeginEdit(DrawingOperationKind.StyleChange, s0).Token;
        var result = service.Commit(token, sIdentical);

        Assert.Equal(DrawingCommandStatus.NoChange, result.Status);
        Assert.Equal(0, service.UndoCount);
        Assert.False(service.CanUndo);
    }

    [Fact]
    public void LawH3_Cancel_DoesNotAddEntry_ReleasesToken()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);

        var begin = service.BeginEdit(DrawingOperationKind.Add, s0);
        Assert.True(begin.IsSuccess);
        Assert.True(service.IsEditing);

        var cancel = service.Cancel(begin.Token);
        Assert.True(cancel.IsSuccess);
        Assert.False(service.IsEditing);
        Assert.Equal(0, service.UndoCount);

        // Can immediately begin another edit
        var nextBegin = service.BeginEdit(DrawingOperationKind.Add, s0);
        Assert.True(nextBegin.IsSuccess);
    }

    [Fact]
    public void LawH4_CommitDecoupledFromSourceMutations()
    {
        using var service = new DrawingHistoryService(TestKey);
        var objId = Guid.NewGuid();
        var s0 = CreateTestState(objectId: objId, price: 1000m);
        var s1 = CreateTestState(objectId: objId, price: 2000m);

        var token = service.BeginEdit(DrawingOperationKind.Move, s0).Token;
        service.Commit(token, s1);

        // Verify that s0 points remain 1000m inside the recorded history
        var undo = service.Undo();
        Assert.True(undo.IsSuccess);
        Assert.Equal(1000m, undo.RestoredState!.Value.Objects[0].Points[0].YValue);

        // Verify that s1 points remain 2000m upon redo
        var redo = service.Redo();
        Assert.True(redo.IsSuccess);
        Assert.Equal(2000m, redo.RestoredState!.Value.Objects[0].Points[0].YValue);
    }

    [Fact]
    public void BeginEdit_WhenAlreadyActive_ReturnsBusy()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState();

        var first = service.BeginEdit(DrawingOperationKind.Add, s0);
        Assert.True(first.IsSuccess);

        var second = service.BeginEdit(DrawingOperationKind.Move, s0);
        Assert.Equal(DrawingCommandStatus.Busy, second.Status);
        Assert.True(second.Token.IsEmpty);
    }

    [Fact]
    public void Commit_WithMismatchedToken_ReturnsInvalidArgument()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1500m);

        service.BeginEdit(DrawingOperationKind.Add, s0);

        // Try committing with wrong token
        var wrongToken = DrawingEditToken.NewToken();
        var result = service.Commit(wrongToken, s1);

        Assert.Equal(DrawingCommandStatus.InvalidArgument, result.Status);
        Assert.Equal(0, service.UndoCount);
        Assert.True(service.IsEditing); // Original edit still active
    }

    [Fact]
    public void Cancel_WithMismatchedToken_ReturnsInvalidArgument()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState();

        service.BeginEdit(DrawingOperationKind.Add, s0);

        var wrongToken = DrawingEditToken.NewToken();
        var result = service.Cancel(wrongToken);

        Assert.Equal(DrawingCommandStatus.InvalidArgument, result.Status);
        Assert.True(service.IsEditing);
    }

    [Fact]
    public void UndoRedo_WhileEditing_ReturnsBusy()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1500m);

        var t1 = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        service.Commit(t1, s1);

        // Start a new edit
        service.BeginEdit(DrawingOperationKind.Move, s1);

        // Undo during active edit -> Busy
        var undo = service.Undo();
        Assert.Equal(DrawingCommandStatus.Busy, undo.Status);

        var redo = service.Redo();
        Assert.Equal(DrawingCommandStatus.Busy, redo.Status);
    }

    [Fact]
    public void Capacity_EntryCountCap_EvictsOldest()
    {
        const int maxEntries = 3;
        using var service = new DrawingHistoryService(TestKey, maxEntryCount: maxEntries, maxByteBudget: 10_000_000);

        var states = new List<FrozenContextState>();
        for (int i = 0; i <= maxEntries + 1; i++)
        {
            states.Add(CreateTestState(price: 1000m + i * 100m));
        }

        // Commit 4 operations into a cap of 3
        for (int i = 0; i < maxEntries + 1; i++)
        {
            var token = service.BeginEdit(DrawingOperationKind.Move, states[i]).Token;
            service.Commit(token, states[i + 1]);
        }

        // Total entries capped at 3
        Assert.Equal(maxEntries, service.UndoCount);

        // The oldest operation (states[0] -> states[1]) was evicted.
        // Undo 3 times should trace back to states[1], not states[0].
        var u1 = service.Undo(); // back to states[3]
        Assert.True(u1.IsSuccess);
        var u2 = service.Undo(); // back to states[2]
        Assert.True(u2.IsSuccess);
        var u3 = service.Undo(); // back to states[1]
        Assert.True(u3.IsSuccess);
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(states[1], u3.RestoredState!.Value));

        // 4th undo should return NoChange (oldest was evicted)
        var u4 = service.Undo();
        Assert.Equal(DrawingCommandStatus.NoChange, u4.Status);
    }

    [Fact]
    public void Capacity_SingleEntryExceedingBudget_Rejected()
    {
        // Budget only 100 bytes (smaller than any JSON context state)
        using var service = new DrawingHistoryService(TestKey, maxEntryCount: 10, maxByteBudget: 100);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 2000m);

        var token = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        var result = service.Commit(token, s1);

        Assert.Equal(DrawingCommandStatus.CapacityExceeded, result.Status);
        Assert.Equal(0, service.UndoCount);
        Assert.False(service.IsEditing); // Token cleared on rejected commit
    }

    [Fact]
    public void Capacity_ByteBudget_EvictsOldestUntilFits()
    {
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 2000m);
        var s2 = CreateTestState(price: 3000m);

        long singleEntryBytes = DrawingHistoryService.ComputePayloadBytes(s0, s1);
        Assert.True(singleEntryBytes > 0);

        // Set budget to fit exactly 1.5 entries
        long budget = (long)(singleEntryBytes * 1.5);
        using var service = new DrawingHistoryService(TestKey, maxEntryCount: 100, maxByteBudget: budget);

        // Op 1: s0 -> s1 (fits, 1 entry)
        var t1 = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        service.Commit(t1, s1);
        Assert.Equal(1, service.UndoCount);

        // Op 2: s1 -> s2 (adding this would exceed budget, so op 1 is evicted)
        var t2 = service.BeginEdit(DrawingOperationKind.Move, s1).Token;
        service.Commit(t2, s2);
        Assert.Equal(1, service.UndoCount);

        // Undo returns s1 (op 2's before), op 1 was evicted
        var undo = service.Undo();
        Assert.True(undo.IsSuccess);
        Assert.True(DrawingHistoryService.AreSemanticallyEqual(s1, undo.RestoredState!.Value));

        var undoOldest = service.Undo();
        Assert.Equal(DrawingCommandStatus.NoChange, undoOldest.Status);
    }

    [Fact]
    public void Dispose_ClearsState_ThrowsOnAccess()
    {
        var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState();
        var s1 = CreateTestState(price: 1500m);

        var token = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        service.Commit(token, s1);

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.BeginEdit(DrawingOperationKind.Add, s0));
        Assert.Throws<ObjectDisposedException>(() => service.Commit(token, s1));
        Assert.Throws<ObjectDisposedException>(() => service.Cancel(token));
        Assert.Throws<ObjectDisposedException>(() => service.Undo());
        Assert.Throws<ObjectDisposedException>(() => service.Redo());
    }

    [Fact]
    public void LayerOperations_ClassifiedInDrawingOperationKind()
    {
        // Verification that G4 layer operations can be logged in history
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState();
        var s1 = CreateTestState(colorHex: "#0000FF");

        var token = service.BeginEdit(DrawingOperationKind.LayerVisibilityToggle, s0).Token;
        var commit = service.Commit(token, s1);

        Assert.True(commit.IsSuccess);
        Assert.Equal(1, service.UndoCount);
    }

    [Fact]
    public void TryEvictOldestEntry_EvictsOldestAndUpdatesByteCount()
    {
        using var service = new DrawingHistoryService(TestKey);
        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1100m);
        var s2 = CreateTestState(price: 1200m);

        var t1 = service.BeginEdit(DrawingOperationKind.Add, s0).Token;
        service.Commit(t1, s1);
        var t2 = service.BeginEdit(DrawingOperationKind.Move, s1).Token;
        service.Commit(t2, s2);

        Assert.Equal(2, service.UndoCount);
        long originalBytes = service.CurrentByteTotal;
        Assert.True(originalBytes > 0);

        bool evicted = service.TryEvictOldestEntry(out long freedBytes);
        Assert.True(evicted);
        Assert.True(freedBytes > 0);
        Assert.Equal(1, service.UndoCount);
        Assert.Equal(originalBytes - freedBytes, service.CurrentByteTotal);
    }

    [Fact]
    public void DrawingDocumentSessionStore_EnforceGlobalHistoryBudget_PrunesOldestEntriesAcrossSessions()
    {
        var store = new DrawingDocumentSessionStore();
        var key1 = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var key2 = new DrawingDocumentKey("9984", TimeframeType.Daily);

        var session1 = store.GetOrCreate(key1);
        var session2 = store.GetOrCreate(key2);

        var h1 = session1.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var h2 = session2.GetOrCreateHistory(ChartDrawingContextType.Standard);

        var s0 = CreateTestState(price: 1000m);
        var s1 = CreateTestState(price: 1100m);
        var s2 = CreateTestState(price: 1200m);

        // Commit in session 1 first (older timestamp)
        var t1 = h1.BeginEdit(DrawingOperationKind.Add, s0).Token;
        h1.Commit(t1, s1);

        // Commit in session 2 second (newer timestamp)
        var t2 = h2.BeginEdit(DrawingOperationKind.Add, s0).Token;
        h2.Commit(t2, s2);

        Assert.Equal(1, h1.UndoCount);
        Assert.Equal(1, h2.UndoCount);
        long totalBytes = store.TotalHistoryPayloadBytes;
        Assert.True(totalBytes > 0);

        // Enforce a budget lower than totalBytes
        int evicted = store.EnforceGlobalHistoryBudget(totalBytes - 10);
        Assert.Equal(1, evicted);
        // Oldest entry was in session 1 (h1)
        Assert.Equal(0, h1.UndoCount);
        Assert.Equal(1, h2.UndoCount);
        Assert.True(store.TotalHistoryPayloadBytes <= totalBytes - 10);
    }
}
