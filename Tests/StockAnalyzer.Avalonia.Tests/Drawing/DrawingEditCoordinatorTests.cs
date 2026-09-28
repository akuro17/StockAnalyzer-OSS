using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingEditCoordinatorTests
{
    private static readonly DrawingDocumentKey TestKey = new("7203", TimeframeType.Daily);

    private static (ChartObjectManager manager, DrawingHistoryService history, DrawingDocumentSession session, DrawingEditCoordinator coordinator) CreateHarness()
    {
        var manager = new ChartObjectManager();
        var history = new DrawingHistoryService(TestKey);
        var session = new DrawingDocumentSession(TestKey);
        var coordinator = new DrawingEditCoordinator(manager, history, session);
        return (manager, history, session, coordinator);
    }

    private static TrendLineObject CreateLine(decimal price1 = 1000m, decimal price2 = 1200m)
    {
        return new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), price1),
            new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), price2))
        {
            Color = Colors.Blue,
            Thickness = 2.0
        };
    }

    [Fact]
    public void BeginEdit_CapturesBefore_Commit_FiresCommitSucceeded_WhenChanged()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var begin = coordinator.BeginEdit(DrawingOperationKind.Add);
            Assert.True(begin.IsSuccess);
            Assert.True(coordinator.IsEditing);

            // Mutate
            var line = CreateLine(1000m, 1200m);
            manager.AddObject(line);

            // Commit
            var commit = coordinator.Commit(begin.Token);
            Assert.True(commit.IsSuccess);
            Assert.False(coordinator.IsEditing);
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(1, eventCount);
            Assert.True(session.IsDirty);
        }
    }

    [Fact]
    public void Commit_WithNoChange_DoesNotFireCommitSucceeded()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var begin = coordinator.BeginEdit(DrawingOperationKind.Move);
            Assert.True(begin.IsSuccess);

            // No mutation performed

            var commit = coordinator.Commit(begin.Token);
            Assert.Equal(DrawingCommandStatus.NoChange, commit.Status);
            Assert.Equal(0, coordinator.UndoCount);
            Assert.Equal(0, eventCount);
        }
    }

    [Fact]
    public void Cancel_ReleasesToken_DoesNotFireCommitSucceeded()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var begin = coordinator.BeginEdit(DrawingOperationKind.Add);
            Assert.True(coordinator.IsEditing);

            manager.AddObject(CreateLine());

            var cancel = coordinator.Cancel(begin.Token);
            Assert.True(cancel.IsSuccess);
            Assert.False(coordinator.IsEditing);
            Assert.Equal(0, coordinator.UndoCount);
            Assert.Equal(0, eventCount);
        }
    }

    [Fact]
    public void ExecuteEdit_WrapsAction_Success()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var result = coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(CreateLine(1000m, 1500m));
            });

            Assert.True(result.IsSuccess);
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(1, eventCount);
            Assert.Equal(1, manager.Count);
        }
    }

    [Fact]
    public void ExecuteEdit_OnException_CancelsAndRethrows()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            Assert.Throws<InvalidOperationException>(() =>
            {
                coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
                {
                    manager.AddObject(CreateLine());
                    throw new InvalidOperationException("Simulation failure");
                });
            });

            Assert.False(coordinator.IsEditing);
            Assert.Equal(0, coordinator.UndoCount);
            Assert.Equal(0, eventCount);
        }
    }

    [Fact]
    public void Undo_RestoresPreviousState_FiresCommitSucceeded()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
            });

            Assert.Equal(1, manager.Count);

            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(0, manager.Count); // Line is gone
            Assert.Equal(1, eventCount);
            Assert.Equal(0, coordinator.UndoCount);
            Assert.Equal(1, coordinator.RedoCount);
        }
    }

    [Fact]
    public void Redo_RestoresNextState_FiresCommitSucceeded()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
            });

            coordinator.Undo();
            Assert.Equal(0, manager.Count);

            int eventCount = 0;
            coordinator.CommitSucceeded += (_, _) => eventCount++;

            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(1, manager.Count); // Line is back
            Assert.Equal(1, eventCount);
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);
        }
    }

    [Fact]
    public void Undo_PreservesObjectId_CreatesFreshRuntimeInstance()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            Guid originalRuntimeId = line.Id;

            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
            });

            // Capture the persistent ID mapped in session
            Assert.True(session.TryGetPersistentId(originalRuntimeId, out var persistentId));

            // Undo removes the object
            coordinator.Undo();
            Assert.Equal(0, manager.Count);

            // Redo materializes a fresh runtime object
            coordinator.Redo();
            Assert.Equal(1, manager.Count);

            var restoredObj = manager.Objects[0];

            // P3-04 verification: Persistent ID is identical
            Assert.True(session.TryGetPersistentId(restoredObj.Id, out var restoredPersistentId));
            Assert.Equal(persistentId, restoredPersistentId);

            // Price and coordinates preserved
            Assert.Equal(1000m, restoredObj.Points[0].Price);
            Assert.Equal(1200m, restoredObj.Points[1].Price);
        }
    }

    [Fact]
    public void OverlappingBeginEdit_ReturnsBusy()
    {
        var (manager, history, session, coordinator) = CreateHarness();
        using (coordinator)
        {
            var first = coordinator.BeginEdit(DrawingOperationKind.Add);
            Assert.True(first.IsSuccess);

            var second = coordinator.BeginEdit(DrawingOperationKind.Move);
            Assert.Equal(DrawingCommandStatus.Busy, second.Status);
        }
    }
}
