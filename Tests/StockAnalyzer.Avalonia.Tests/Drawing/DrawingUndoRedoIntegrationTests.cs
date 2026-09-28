using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingUndoRedoIntegrationTests
{
    private static readonly DrawingDocumentKey TestKey = new("7203", TimeframeType.Daily);

    private static (ChartObjectManager manager, DrawingHistoryService history, DrawingDocumentSession session, DrawingLayerService layerService, DrawingEditCoordinator coordinator) CreateIntegrationHarness(Func<IReadOnlyList<CoreCandleData>?>? candlesProvider = null)
    {
        var manager = new ChartObjectManager();
        var history = new DrawingHistoryService(TestKey);
        var session = new DrawingDocumentSession(TestKey);
        var layerService = new DrawingLayerService();
        var coordinator = new DrawingEditCoordinator(manager, history, session, layerService, candlesProvider);
        return (manager, history, session, layerService, coordinator);
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
    public void DragMove_UndoRestoresOriginalCoordinates_RedoRestoresMovedCoordinates()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            manager.AddObject(line);

            // 1. Simulate Move interaction from ChartInteractionController
            var begin = coordinator.BeginEdit(DrawingOperationKind.Move);
            Assert.True(begin.IsSuccess);

            // Drag translation by +50 price
            line.Translate(TimeSpan.Zero, 50m);
            Assert.Equal(1050m, line.Points[0].Price);
            Assert.Equal(1250m, line.Points[1].Price);

            // Commit move
            var commit = coordinator.Commit(begin.Token);
            Assert.True(commit.IsSuccess);
            Assert.Equal(1, coordinator.UndoCount);

            // 2. Undo move
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(1, manager.Count);
            var restored = manager.Objects[0];
            Assert.Equal(1000m, restored.Points[0].Price);
            Assert.Equal(1200m, restored.Points[1].Price);

            // Law P3-04: fresh runtime instance materialized
            Assert.NotSame(line, restored);

            // 3. Redo move
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(1, manager.Count);
            var redone = manager.Objects[0];
            Assert.Equal(1050m, redone.Points[0].Price);
            Assert.Equal(1250m, redone.Points[1].Price);
        }
    }

    [Fact]
    public void HandlePointEdit_UndoRestoresOriginalPoint_RedoRestoresEditedPoint()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            manager.AddObject(line);

            // 1. Simulate Handle drag edit
            var begin = coordinator.BeginEdit(DrawingOperationKind.PointEdit);
            Assert.True(begin.IsSuccess);

            line.Points[1] = new ChartPoint(line.Points[1].Time, 1600m);

            var commit = coordinator.Commit(begin.Token);
            Assert.True(commit.IsSuccess);

            // 2. Undo
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(1200m, manager.Objects[0].Points[1].Price);

            // 3. Redo
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(1600m, manager.Objects[0].Points[1].Price);
        }
    }

    [Fact]
    public void ShapeCompletion_UndoRemovesShape_RedoRestoresShape()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            Assert.Equal(0, manager.Count);

            // Simulate FinishDrawing wrapping Add in ExecuteEdit
            var line = CreateLine(500m, 600m);
            var result = coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
            });
            Assert.True(result.IsSuccess);
            Assert.Equal(1, manager.Count);

            // Undo
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(0, manager.Count);

            // Redo
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(1, manager.Count);
            Assert.Equal(500m, manager.Objects[0].Points[0].Price);
        }
    }

    [Fact]
    public void SettingsDialogLifecycle_ApplyThenOk_MultiStepUndoRestoresEachState()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            line.Color = Colors.Blue;
            line.Thickness = 2.0;
            manager.AddObject(line);

            // Step 1: Dialog opens (BeginEdit)
            var token1 = coordinator.BeginEdit(DrawingOperationKind.SettingsApply).Token;

            // Step 2: User changes color and clicks Apply (Commit + new BeginEdit)
            line.Color = Colors.Red;
            coordinator.Commit(token1);
            var token2 = coordinator.BeginEdit(DrawingOperationKind.SettingsApply).Token;

            // Step 3: User changes thickness and clicks OK (Commit)
            line.Thickness = 5.0;
            coordinator.Commit(token2);

            Assert.Equal(2, coordinator.UndoCount);

            // Current state: Red and 5.0
            Assert.Equal(Colors.Red, manager.Objects[0].Color);
            Assert.Equal(5.0, manager.Objects[0].Thickness);

            // Undo 1: restores state at Apply (Red and 2.0)
            coordinator.Undo();
            Assert.Equal(Colors.Red, manager.Objects[0].Color);
            Assert.Equal(2.0, manager.Objects[0].Thickness);

            // Undo 2: restores initial state before dialog opened (Blue and 2.0)
            coordinator.Undo();
            Assert.Equal(Colors.Blue, manager.Objects[0].Color);
            Assert.Equal(2.0, manager.Objects[0].Thickness);
        }
    }

    [Fact]
    public void SettingsDialogLifecycle_Cancel_RollsBackWithoutAddingHistory()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            line.Color = Colors.Blue;
            manager.AddObject(line);

            // Dialog opens
            var token = coordinator.BeginEdit(DrawingOperationKind.SettingsApply).Token;

            // User mutates
            line.Color = Colors.Green;

            // User clicks Cancel
            var cancel = coordinator.Cancel(token);
            Assert.True(cancel.IsSuccess);
            Assert.Equal(0, coordinator.UndoCount); // No entry pushed
            Assert.Equal(Colors.Blue, manager.Objects[0].Color); // Asserts color rolls back to Blue
        }
    }

    [Fact]
    public void LayerOperations_CreateAndRename_UndoRestoresPreviousLayers()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var mainKey = PanelKey.Main;
            var initialLayers = layerService.GetLayersForPanel(mainKey);
            Assert.Single(initialLayers);

            // 1. Create Layer wrapped in coordinator
            Guid createdLayerId = Guid.Empty;
            coordinator.ExecuteEdit(DrawingOperationKind.LayerAdd, () =>
            {
                var res = layerService.CreateLayer(mainKey, "Fibonacci");
                createdLayerId = res.TargetId!.Value;
            });

            Assert.Equal(2, layerService.GetLayersForPanel(mainKey).Count);
            Assert.Equal(1, coordinator.UndoCount);

            // 2. Undo creation
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Single(layerService.GetLayersForPanel(mainKey));
            Assert.Null(layerService.GetLayer(createdLayerId));

            // 3. Redo creation
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(2, layerService.GetLayersForPanel(mainKey).Count);
        }
    }

    [Fact]
    public void DeferredComputation_RecalculatesOnUndoRedo()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            decimal price = 100m + i * 10m;
            candles.Add(new CoreCandleData(baseTime.AddDays(i), price, price + 5m, price - 5m, price, 1000));
        }

        var (manager, _, _, _, coordinator) = CreateIntegrationHarness(candlesProvider: () => candles);
        using (coordinator)
        {
            var regObj = new RegressionTrendObject(
                new ChartPoint(baseTime, 0m),
                new ChartPoint(baseTime.AddDays(9), 0m));

            // Initial calculation
            regObj.Recalculate(candles);
            manager.AddObject(regObj);

            // Initial regression points are fitted to prices
            Assert.Equal(100m, regObj.Points[0].Price);
            Assert.Equal(190m, regObj.Points[1].Price);

            // Change start time to day 5 (fitted price 150m)
            var token = coordinator.BeginEdit(DrawingOperationKind.PointEdit).Token;
            regObj.Points[0] = new ChartPoint(baseTime.AddDays(5), 0m);
            regObj.Recalculate(candles);
            coordinator.Commit(token);

            Assert.Equal(150m, manager.Objects[0].Points[0].Price);

            // Undo restores day 0 and recalculates points back to 100m and 190m
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(100m, manager.Objects[0].Points[0].Price);
            Assert.Equal(190m, manager.Objects[0].Points[1].Price);

            // Redo restores day 5 and recalculates points back to 150m and 190m
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(150m, manager.Objects[0].Points[0].Price);
            Assert.Equal(190m, manager.Objects[0].Points[1].Price);
        }
    }

    [Fact]
    public void RestorationHardening_FailureRollsBackHistoryPointer_AllOrNothing()
    {
        var (manager, history, session, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(line));
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);

            // Create a state with an unknown invalid type name that will throw during Materialize
            var corruptObject = new DrawingObjectRecord(
                Guid.NewGuid(),
                "NonExistentCorruptedType",
                PanelKey.Main,
                DrawingCoordinateKind.UtcTime,
                new List<DrawingStoredPoint> { DrawingStoredPoint.CreateUtc(DateTime.UtcNow, 1000m) },
                null);

            var corruptBefore = new FrozenContextState(
                ChartDrawingContextType.Standard.ToString(),
                null,
                new List<DrawingObjectRecord> { corruptObject });

            var validAfter = new FrozenContextState(
                ChartDrawingContextType.Standard.ToString(),
                null,
                new List<DrawingObjectRecord>());

            var begin = history.BeginEdit(DrawingOperationKind.PointEdit, corruptBefore);
            Assert.True(begin.IsSuccess);
            var commit = history.Commit(begin.Token, validAfter);
            Assert.True(commit.IsSuccess);
            Assert.Equal(2, coordinator.UndoCount);

            // Undo on corrupt entry should fail All-or-Nothing
            var undoResult = coordinator.Undo();
            Assert.False(undoResult.IsSuccess);
            Assert.Equal(DrawingCommandStatus.InvalidState, undoResult.Status);

            // Crucial: History pointer rolled back, document state and live objects are not corrupted
            Assert.Equal(2, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);
            Assert.Single(manager.Objects);
            Assert.Equal(line.Id, manager.Objects[0].Id);
            Assert.Equal(1000m, manager.Objects[0].Points[0].Price);
        }
    }

    [Fact]
    public void DrawingObjectItemViewModel_ToggleVisibilityAndLock_UndoReverts()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            manager.AddObject(line);

            var itemVm = new DrawingObjectItemViewModel(
                line,
                manager,
                onStateChanged: null,
                onOpenSettings: null,
                iconDrawingService: null,
                layerService: layerService,
                coordinatorProvider: () => coordinator);

            Assert.True(line.IsVisible);

            // 1. Toggle visibility -> false
            itemVm.ToggleVisibilityCommand.Execute(null);
            Assert.False(line.IsVisible);
            Assert.Equal(1, coordinator.UndoCount);

            // Undo -> true
            coordinator.Undo();
            Assert.True(manager.Objects[0].IsVisible);

            // Redo -> false
            coordinator.Redo();
            Assert.False(manager.Objects[0].IsVisible);

            // 2. Wrap the active runtime object in manager.Objects (P3-04: fresh runtime instance created on restoration)
            var activeVm = new DrawingObjectItemViewModel(
                manager.Objects[0],
                manager,
                onStateChanged: null,
                onOpenSettings: null,
                iconDrawingService: null,
                layerService: layerService,
                coordinatorProvider: () => coordinator);

            // Toggle lock -> true
            activeVm.ToggleLockCommand.Execute(null);
            Assert.True(manager.Objects[0].IsLocked);

            // Undo -> false
            coordinator.Undo();
            Assert.False(manager.Objects[0].IsLocked);
        }
    }

    [Fact]
    public void DrawingObjectItemViewModel_Copy_UndoRemovesDuplicate_RedoRestoresDuplicate()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            manager.AddObject(line);
            layerService.AddObjectToLayer(line.Id, PanelKey.Main);

            var itemVm = new DrawingObjectItemViewModel(
                line,
                manager,
                onStateChanged: null,
                onOpenSettings: null,
                iconDrawingService: null,
                layerService: layerService,
                coordinatorProvider: () => coordinator);

            Assert.Equal(1, manager.Count);

            // Duplicate via CopyCommand
            itemVm.CopyCommand.Execute(null);
            Assert.Equal(2, manager.Count);
            Assert.Equal(1, coordinator.UndoCount);

            // Undo removes clone
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(1, manager.Count);

            // Redo restores clone
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(2, manager.Count);
        }
    }

    [Fact]
    public void DrawingLayerItemViewModel_ToggleVisibilityAndLock_UndoReverts()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var mainKey = PanelKey.Main;
            var defaultLayer = layerService.GetLayersForPanel(mainKey)[0];

            var layerVm = new DrawingLayerItemViewModel(
                defaultLayer,
                layerService,
                onChangedCallback: () => { },
                deleteConfirmCallback: _ => System.Threading.Tasks.Task.CompletedTask,
                coordinatorProvider: () => coordinator);

            Assert.True(layerVm.IsVisible);

            // Toggle visibility -> false
            layerVm.ToggleVisibilityCommand.Execute(null);
            Assert.False(layerVm.IsVisible);
            Assert.Equal(1, coordinator.UndoCount);

            // Undo -> true
            coordinator.Undo();
            Assert.True(layerService.GetLayer(defaultLayer.LayerId)!.Value.IsVisible);

            // Redo -> false
            coordinator.Redo();
            Assert.False(layerService.GetLayer(defaultLayer.LayerId)!.Value.IsVisible);

            // Toggle lock -> true
            layerVm.ToggleLockCommand.Execute(null);
            Assert.True(layerService.GetLayer(defaultLayer.LayerId)!.Value.IsEditLocked);

            // Undo -> false
            coordinator.Undo();
            Assert.False(layerService.GetLayer(defaultLayer.LayerId)!.Value.IsEditLocked);
        }
    }

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void Hotkey_CtrlZ_And_CtrlY_Handled_And_TextBoxIsIgnored()
    {
        var controller = new ChartInteractionController();

        // 1. KeyDown inside TextBox -> must be ignored (returns false)
        var textBox = new TextBox();
        var textBoxArgs = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Z,
            KeyModifiers = KeyModifiers.Control,
            Source = textBox
        };

        bool handledInsideTextBox = controller.HandleKeyDown(textBoxArgs, null);
        Assert.False(handledInsideTextBox);
        Assert.False(textBoxArgs.Handled);

        // 2. KeyDown on canvas with active coordinator -> executes Undo / Redo
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(line));
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);

            var canvasSource = new global::Avalonia.Controls.Canvas();

            // Ctrl+Z triggers Undo
            var ctrlZArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Z,
                KeyModifiers = KeyModifiers.Control,
                Source = canvasSource
            };
            bool handledZ = controller.HandleKeyDown(ctrlZArgs, null, coordinator);
            Assert.True(handledZ);
            Assert.True(ctrlZArgs.Handled);
            Assert.Equal(0, coordinator.UndoCount);
            Assert.Equal(1, coordinator.RedoCount);

            // Ctrl+Y triggers Redo
            var ctrlYArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Y,
                KeyModifiers = KeyModifiers.Control,
                Source = canvasSource
            };
            bool handledY = controller.HandleKeyDown(ctrlYArgs, null, coordinator);
            Assert.True(handledY);
            Assert.True(ctrlYArgs.Handled);
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);

            // Undo again then Ctrl+Shift+Z triggers Redo
            coordinator.Undo();
            var ctrlShiftZArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Z,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Source = canvasSource
            };
            bool handledShiftZ = controller.HandleKeyDown(ctrlShiftZArgs, null, coordinator);
            Assert.True(handledShiftZ);
            Assert.True(ctrlShiftZArgs.Handled);
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(0, coordinator.RedoCount);
        }
    }

    [Fact]
    public void SettingsDialogLifecycle_ApplyThenCancel_RevertsToAppliedState_AndUndoRevertsToPreOpenState()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            line.Color = Colors.Blue;
            line.Thickness = 2.0;
            manager.AddObject(line);

            // Step 1: Dialog opens
            var token1 = coordinator.BeginEdit(DrawingOperationKind.SettingsApply).Token;

            // Step 2: User changes color to Red and clicks Apply
            line.Color = Colors.Red;
            coordinator.Commit(token1);
            var token2 = coordinator.BeginEdit(DrawingOperationKind.SettingsApply).Token;

            // Step 3: User changes thickness to 6.0 and clicks Cancel
            line.Thickness = 6.0;
            coordinator.Cancel(token2);

            // Cancel rolls back live thickness to 2.0 while keeping applied Red color
            Assert.Equal(1, coordinator.UndoCount);
            Assert.Equal(2.0, manager.Objects[0].Thickness);
            Assert.Equal(Colors.Red, manager.Objects[0].Color);

            // Undo once reverts to pre-dialog state (Blue, 2.0)
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(Colors.Blue, manager.Objects[0].Color);
            Assert.Equal(2.0, manager.Objects[0].Thickness);
            Assert.Equal(0, coordinator.UndoCount);

            // Redo restores applied state (Red, 2.0)
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(Colors.Red, manager.Objects[0].Color);
            Assert.Equal(2.0, manager.Objects[0].Thickness);
            Assert.Equal(1, coordinator.UndoCount);
        }
    }

    [Fact]
    public void LawH6_CompositeComplexEntry_LockedAndHiddenLayers_RoundTripMatches()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var mainKey = PanelKey.Main;
            var defaultLayer = layerService.GetLayersForPanel(mainKey)[0];

            // Create second layer: hidden and locked
            Guid layer2Id = Guid.Empty;
            coordinator.ExecuteEdit(DrawingOperationKind.LayerAdd, () =>
            {
                var res = layerService.CreateLayer(mainKey, "SubLayer");
                layer2Id = res.TargetId!.Value;
                layerService.SetLayerVisibility(layer2Id, false);
                layerService.SetLayerEditLock(layer2Id, true);
            });

            var lineA = CreateLine(1000m, 1200m);
            var lineB = CreateLine(2000m, 2200m);
            lineB.IsLocked = true;

            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(lineA);
                layerService.AddObjectToLayer(lineA.Id, mainKey, defaultLayer.LayerId);

                manager.AddObject(lineB);
                layerService.AddObjectToLayer(lineB.Id, mainKey, layer2Id);
            });

            Assert.Equal(2, manager.Count);
            int undoCountBeforeDelete = coordinator.UndoCount;

            // Delete object A in Layer 1
            coordinator.ExecuteEdit(DrawingOperationKind.Delete, () =>
            {
                manager.RemoveObject(lineA.Id);
                layerService.RemoveObjectFromLayer(lineA.Id);
            });

            Assert.Equal(1, manager.Count);
            Assert.Equal(lineB.Id, manager.Objects[0].Id);

            // Undo delete: both objects restored, layer 2 stays hidden and locked
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(2, manager.Count);

            var restoredLayer2 = layerService.GetLayer(layer2Id);
            Assert.NotNull(restoredLayer2);
            Assert.False(restoredLayer2.Value.IsVisible);
            Assert.True(restoredLayer2.Value.IsEditLocked);

            // Object B still locked
            var restoredB = manager.Objects.FirstOrDefault(o => o.Points[0].Price == 2000m);
            Assert.NotNull(restoredB);
            Assert.True(restoredB.IsLocked);
        }
    }

    [Fact]
    public void ScopeIsolation_DifferentDocumentKeys_MaintainIndependentHistories()
    {
        var keyA = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var keyB = new DrawingDocumentKey("9984", TimeframeType.H1);

        var managerA = new ChartObjectManager();
        var historyA = new DrawingHistoryService(keyA);
        var sessionA = new DrawingDocumentSession(keyA);
        using var coordA = new DrawingEditCoordinator(managerA, historyA, sessionA);

        var managerB = new ChartObjectManager();
        var historyB = new DrawingHistoryService(keyB);
        var sessionB = new DrawingDocumentSession(keyB);
        using var coordB = new DrawingEditCoordinator(managerB, historyB, sessionB);

        // Mutate A
        coordA.ExecuteEdit(DrawingOperationKind.Add, () => managerA.AddObject(CreateLine(1000m, 1200m)));

        Assert.Equal(1, coordA.UndoCount);
        Assert.Equal(0, coordB.UndoCount);

        // Undo in B does nothing
        var undoB = coordB.Undo();
        Assert.Equal(DrawingCommandStatus.NoChange, undoB.Status);

        // Undo in A restores
        var undoA = coordA.Undo();
        Assert.True(undoA.IsSuccess);
        Assert.Equal(0, managerA.Count);
    }

    [Fact]
    public void PersistenceNotification_FiresExactlyOncePerCommitUndoRedo_NeverOnCancelOrNoChange()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            int commitEventCount = 0;
            coordinator.CommitSucceeded += (_, _) => commitEventCount++;

            // 1. NoChange Commit -> 0 events
            var t1 = coordinator.BeginEdit(DrawingOperationKind.StyleChange).Token;
            var res1 = coordinator.Commit(t1);
            Assert.Equal(DrawingCommandStatus.NoChange, res1.Status);
            Assert.Equal(0, commitEventCount);

            // 2. Cancel -> 0 events
            var t2 = coordinator.BeginEdit(DrawingOperationKind.Add).Token;
            coordinator.Cancel(t2);
            Assert.Equal(0, commitEventCount);

            // 3. Mutating Commit -> 1 event
            var t3 = coordinator.BeginEdit(DrawingOperationKind.Add).Token;
            manager.AddObject(CreateLine());
            coordinator.Commit(t3);
            Assert.Equal(1, commitEventCount);

            // 4. Undo -> 2 events
            coordinator.Undo();
            Assert.Equal(2, commitEventCount);

            // 5. Redo -> 3 events
            coordinator.Redo();
            Assert.Equal(3, commitEventCount);
        }
    }

    [Fact]
    public void R01_LiveObjectPaintResourcesAreNotDisposedAfterUndoRedo()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            line.Color = Colors.Red;
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(line));

            // Mutate line
            var t = coordinator.BeginEdit(DrawingOperationKind.PointEdit).Token;
            line.Points[0] = new ChartPoint(line.Points[0].Time, 1100m);
            coordinator.Commit(t);

            // Undo restores previous state
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);

            // Verify live object is usable and paint resources are not disposed (R01)
            var restoredObj = manager.Objects[0];
            Assert.NotNull(restoredObj);
            Assert.Equal(1000m, restoredObj.Points[0].Price);
            var mockTransform = new Mock<ICoordinateTransform>();
            mockTransform.Setup(x => x.ChartToScreen(It.IsAny<ChartPoint>())).Returns(new global::Avalonia.Point(10, 10));

            // Calling HitTest or querying properties should not throw ObjectDisposedException
            bool hit = restoredObj.HitTest(new global::Avalonia.Point(0, 0), mockTransform.Object);
            Assert.False(hit);

            // Skia Render execution on Undo: must not throw ObjectDisposedException
            using var bitmap = new SKBitmap(200, 200);
            using var canvas = new SKCanvas(bitmap);
            restoredObj.Render(canvas, mockTransform.Object);

            // Redo and Render again
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            var redoneObj = manager.Objects[0];
            Assert.Equal(1100m, redoneObj.Points[0].Price);
            redoneObj.Render(canvas, mockTransform.Object);
        }
    }

    [Fact]
    public void R02_LayerServiceObjectMappingPreservedAfterUndo()
    {
        var (manager, _, _, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
                layerService.AddObjectToLayer(line.Id, PanelKey.Main);
            });

            var originalLayer = layerService.GetLayerForObject(line.Id);
            Assert.NotNull(originalLayer);

            // Mutate object
            var t = coordinator.BeginEdit(DrawingOperationKind.PointEdit).Token;
            line.Points[0] = new ChartPoint(line.Points[0].Time, 1500m);
            coordinator.Commit(t);

            // Undo restores object
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);

            // Check that restored object's runtime ID is correctly mapped in the layer (R02)
            var restoredObj = manager.Objects[0];
            var restoredLayer = layerService.GetLayerForObject(restoredObj.Id);
            Assert.NotNull(restoredLayer);
            Assert.Equal(originalLayer.Value.LayerId, restoredLayer.Value.LayerId);
            Assert.Contains(restoredObj.Id, restoredLayer.Value.ObjectIds);
        }
    }

    [Fact]
    public void R06_R07_SessionPreservesHistoryAcrossCoordinatorRebindAndIsolatesContexts()
    {
        var key = new DrawingDocumentKey("AAPL", TimeframeType.Daily);
        var store = new DrawingDocumentSessionStore();
        var session = store.GetOrCreate(key);

        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        // 1. Coordinator for Standard context
        var historyStandard = session.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var coordStandard = new DrawingEditCoordinator(manager, historyStandard, session, layerService);

        var line = CreateLine(1000m, 1200m);
        coordStandard.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(line));
        Assert.Equal(1, coordStandard.UndoCount);

        // Rebind coordinator (e.g. timeframe/context switch teardown)
        coordStandard.Dispose();

        // 2. Coordinator for Kagi context -> should have 0 undo entries (R07 isolation)
        var historyKagi = session.GetOrCreateHistory(ChartDrawingContextType.Kagi);
        var coordKagi = new DrawingEditCoordinator(manager, historyKagi, session, layerService);
        Assert.Equal(0, coordKagi.UndoCount);
        coordKagi.Dispose();

        // 3. Coordinator back to Standard context -> history must be preserved! (R06 retention)
        var historyStandard2 = session.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var coordStandard2 = new DrawingEditCoordinator(manager, historyStandard2, session, layerService);
        Assert.Equal(1, coordStandard2.UndoCount);

        // Undo succeeds
        var undo = coordStandard2.Undo();
        Assert.True(undo.IsSuccess);
        Assert.Equal(0, coordStandard2.UndoCount);
        coordStandard2.Dispose();
    }

    [Fact]
    public void F01_DialogDelete_AfterCancel_ResolvesCurrentRuntimeId_AndSucceeds()
    {
        var (manager, _, session, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(line);
                layerService.AddObjectToLayer(line.Id, PanelKey.Main);
            });
            var initialRuntimeId = line.Id;

            // Dialog opens (BeginEdit)
            var begin = coordinator.BeginEdit(DrawingOperationKind.SettingsApply);
            Assert.True(begin.IsSuccess);

            // Dialog modifies line and user clicks Cancel
            line.Color = Colors.Green;
            var cancel = coordinator.Cancel(begin.Token);
            Assert.True(cancel.IsSuccess);

            // Cancel restores live objects via Materialize, so manager.Objects[0].Id is fresh!
            var currentObj = manager.Objects[0];
            Assert.NotEqual(initialRuntimeId, currentObj.Id);

            // Resolving initialRuntimeId must return the new current runtime ID!
            bool resolved = coordinator.TryResolveCurrentRuntimeId(initialRuntimeId, out var resolvedId);
            Assert.True(resolved);
            Assert.Equal(currentObj.Id, resolvedId);

            // Calling Delete with resolvedId succeeds!
            var delResult = coordinator.ExecuteEdit(DrawingOperationKind.Delete, () =>
            {
                manager.RemoveObject(resolvedId);
                layerService.RemoveObjectFromLayer(resolvedId);
            });
            Assert.True(delResult.IsSuccess);
            Assert.Equal(0, manager.Count);

            // Undo restores the deleted object
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(1, manager.Count);
        }
    }

    [Fact]
    public void F02_F03_ConcurrencyAndInFlightDispose_DoesNotLockHistoryOrDropSessionLock()
    {
        var key = new DrawingDocumentKey("MSFT", TimeframeType.Daily);
        var session = new DrawingDocumentSession(key);
        var history = session.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        var coord1 = new DrawingEditCoordinator(manager, history, session, layerService);
        var begin1 = coord1.BeginEdit(DrawingOperationKind.Move);
        Assert.True(begin1.IsSuccess);

        // F02: Second BeginEdit on same coordinator while edit is in progress fails with Busy
        var beginDuplicate = coord1.BeginEdit(DrawingOperationKind.Add);
        Assert.False(beginDuplicate.IsSuccess);
        Assert.Equal(DrawingCommandStatus.Busy, beginDuplicate.Status);

        // Valid edit token remains active and can be committed
        var commitRes = coord1.Commit(begin1.Token);
        Assert.True(commitRes.IsSuccess || commitRes.Status == DrawingCommandStatus.NoChange);

        // In-flight Dispose: Begin edit then dispose coord1 without committing/canceling
        var begin2 = coord1.BeginEdit(DrawingOperationKind.PointEdit);
        Assert.True(begin2.IsSuccess);
        coord1.Dispose();

        // F03: Disposing coord1 safely cancelled active edit in history; session and history are not stuck in Busy
        var coord2 = new DrawingEditCoordinator(manager, history, session, layerService);
        var beginCoord2 = coord2.BeginEdit(DrawingOperationKind.Move);
        Assert.True(beginCoord2.IsSuccess);
        coord2.Cancel(beginCoord2.Token);
        coord2.Dispose();
    }

    [Fact]
    public void F04_RestoreContextState_AllOrNothing_MaterializedObjectsNotDisposedOnException()
    {
        var (manager, _, session, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var line = CreateLine(1000m, 1200m);
            coordinator.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(line));

            // Subscribe to CommitSucceeded with a handler that throws an exception
            coordinator.CommitSucceeded += (_, _) => throw new InvalidOperationException("Simulated subscriber exception");

            var t = coordinator.BeginEdit(DrawingOperationKind.PointEdit).Token;
            line.Points[0] = new ChartPoint(line.Points[0].Time, 1400m);

            // Commit should catch the subscriber exception without crashing or disposing live objects
            var commitResult = coordinator.Commit(t);
            Assert.True(commitResult.IsSuccess);

            // Live object is intact and not disposed
            Assert.Single(manager.Objects);
            var obj = manager.Objects[0];
            Assert.Equal(1400m, obj.Points[0].Price);
            Assert.False(obj.IsLocked);
        }
    }

    [Fact]
    public void F05_F06_TwoPhaseCommit_AndCancelPurity()
    {
        var key = new DrawingDocumentKey("NVDA", TimeframeType.Daily);
        var session = new DrawingDocumentSession(key);
        var history = session.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var manager = new ChartObjectManager();
        var coordinator = new DrawingEditCoordinator(manager, history, session);

        using (coordinator)
        {
            long initialRev = session.Revision;
            Assert.False(session.IsDirty);

            // Cancel Purity (F06)
            var b1 = coordinator.BeginEdit(DrawingOperationKind.Add);
            manager.AddObject(CreateLine());
            var cancel = coordinator.Cancel(b1.Token);
            Assert.True(cancel.IsSuccess);

            Assert.Equal(initialRev, session.Revision);
            Assert.False(session.IsDirty);
            Assert.Equal(0, coordinator.UndoCount);

            // Two-Phase Commit rejection (F05): Simulate session lock takeover so coordinator cannot update session
            var b2 = coordinator.BeginEdit(DrawingOperationKind.Add);
            manager.AddObject(CreateLine());

            // Simulate external session token takeover
            var externalToken = Guid.NewGuid();
            session.ForceAcquireEdit(externalToken);

            var commitResult = coordinator.Commit(b2.Token);
            Assert.False(commitResult.IsSuccess);
            Assert.Equal(DrawingCommandStatus.Busy, commitResult.Status);
            Assert.Equal(0, coordinator.UndoCount); // Not committed to history
        }
    }

    [Fact]
    public void F07_ContextLayers_PartitionedAndPreservedAcrossMutations()
    {
        var key = new DrawingDocumentKey("GOOG", TimeframeType.Daily);
        var session = new DrawingDocumentSession(key);
        var manager = new ChartObjectManager();
        var layerService = new DrawingLayerService();

        var historyStd = session.GetOrCreateHistory(ChartDrawingContextType.Standard);
        using var coordStd = new DrawingEditCoordinator(manager, historyStd, session, layerService);

        // Create Layer in Standard
        layerService.CreateLayer(PanelKey.Main, "StandardLayer");
        var lineStd = CreateLine(100m, 200m);
        coordStd.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(lineStd));

        // Switch to Kagi context
        manager.SwitchContext(ChartDrawingContextType.Kagi);
        var kagiLayers = new DrawingLayerService();
        kagiLayers.CreateLayer(PanelKey.Main, "KagiLayer");
        var historyKagi = session.GetOrCreateHistory(ChartDrawingContextType.Kagi);
        using var coordKagi = new DrawingEditCoordinator(manager, historyKagi, session, kagiLayers);

        var lineKagi = CreateLine(300m, 400m);
        coordKagi.ExecuteEdit(DrawingOperationKind.Add, () => manager.AddObject(lineKagi));

        // Session document must contain both Standard and Kagi contexts with their respective layers
        Assert.NotNull(session.CurrentDocument);
        var doc = session.CurrentDocument.Value;
        var stdCtx = doc.Contexts.FirstOrDefault(c => c.ContextName == "Standard");
        var kagiCtx = doc.Contexts.FirstOrDefault(c => c.ContextName == "Kagi");

        Assert.NotNull(stdCtx.ContextName);
        Assert.NotNull(kagiCtx.ContextName);
        Assert.Contains(stdCtx.Layers, l => l.Name == "StandardLayer");
        Assert.Contains(kagiCtx.Layers, l => l.Name == "KagiLayer");
    }

    [Fact]
    public void F08_RepositorySaveCounts_ExactPerMutationAndNeverOnCancel()
    {
        var (manager, _, _, _, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            int saveCount = 0;
            coordinator.CommitSucceeded += (_, _) => saveCount++;

            // 1. Cancel -> 0 saves
            var t1 = coordinator.BeginEdit(DrawingOperationKind.Add).Token;
            coordinator.Cancel(t1);
            Assert.Equal(0, saveCount);

            // 2. Commit NoChange -> 0 saves
            var t2 = coordinator.BeginEdit(DrawingOperationKind.Move).Token;
            coordinator.Commit(t2);
            Assert.Equal(0, saveCount);

            // 3. Commit Add -> exactly 1 save
            var t3 = coordinator.BeginEdit(DrawingOperationKind.Add).Token;
            manager.AddObject(CreateLine());
            coordinator.Commit(t3);
            Assert.Equal(1, saveCount);

            // 4. Undo -> exactly 1 save (total 2)
            coordinator.Undo();
            Assert.Equal(2, saveCount);

            // 5. Redo -> exactly 1 save (total 3)
            coordinator.Redo();
            Assert.Equal(3, saveCount);
        }
    }

    [Fact]
    public void A01_ChartDrawingRepository_LoadPayload_ObjectsNotDisposed()
    {
        var repo = new ChartDrawingRepository();
        var key = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var manager = new ChartObjectManager();
        var line = CreateLine(1000m, 1200m);
        manager.AddObject(line);

        var payload = new ChartDrawingPayload
        {
            Objects = manager.GetSnapshot(),
            Layers = new Dictionary<ChartDrawingContextType, List<DrawingLayerRecord>>
            {
                [ChartDrawingContextType.Standard] = new() { new DrawingLayerRecord(Guid.NewGuid(), "Layer1", PanelKey.Main, true, false, new[] { line.Id }) }
            }
        };

        repo.SavePayload("7203", TimeframeType.Daily, payload);

        // LoadPayload must return live, usable objects that are NOT disposed upon exit
        var loaded = repo.LoadPayload("7203", TimeframeType.Daily);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Objects);
        var loadedObjs = loaded.Objects[ChartDrawingContextType.Standard];
        Assert.Single(loadedObjs);
        var restoredLine = loadedObjs[0];
        Assert.Equal(1000m, restoredLine.Points[0].Price);
        Assert.False(restoredLine.IsLocked);

        // Object is fully operable
        restoredLine.Translate(TimeSpan.Zero, 50m);
        Assert.Equal(1050m, restoredLine.Points[0].Price);
    }

    [Fact]
    public void HandleTextTool_MainPanel_RegistersToLayer_AndSupportsUndoRedo()
    {
        var (manager, _, session, layerService, coordinator) = CreateIntegrationHarness();
        using (coordinator)
        {
            var pt = new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1000m);
            var textObj = new TextObject(pt, "Test Note") { PanelIndex = -1 };

            // Simulate the exact code executed by ChartInteractionController.HandleTextTool
            var result = coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
            {
                manager.AddObject(textObj);
                var targetPanel = textObj.PanelIndex >= 0
                    ? PanelKey.OverlayGroup(textObj.PanelIndex)
                    : PanelKey.Main;
                layerService.AddObjectToLayer(textObj.Id, targetPanel);
                manager.SelectObject(textObj.Id);
            });

            Assert.True(result.IsSuccess);
            Assert.Equal(1, manager.Count);
            Assert.True(layerService.IsObjectRegistered(textObj.Id));
            var layer = layerService.GetLayerForObject(textObj.Id);
            Assert.NotNull(layer);
            Assert.Equal(PanelKey.Main, layer.Value.Panel);

            // Undo
            var undo = coordinator.Undo();
            Assert.True(undo.IsSuccess);
            Assert.Equal(0, manager.Count);

            // Redo
            var redo = coordinator.Redo();
            Assert.True(redo.IsSuccess);
            Assert.Equal(1, manager.Count);
            var restoredText = manager.Objects[0];
            Assert.True(layerService.IsObjectRegistered(restoredText.Id));
        }
    }

    [Fact]
    public void CoordinatorCommit_EnforcesGlobalHistoryBudget_WhenSessionStoreAttached()
    {
        var store = new DrawingDocumentSessionStore();
        var key1 = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var key2 = new DrawingDocumentKey("9984", TimeframeType.Daily);

        var session1 = store.GetOrCreate(key1);
        var session2 = store.GetOrCreate(key2);

        var h1 = session1.GetOrCreateHistory(ChartDrawingContextType.Standard);
        var h2 = session2.GetOrCreateHistory(ChartDrawingContextType.Standard);

        var m1 = new ChartObjectManager();
        var m2 = new ChartObjectManager();
        var l1 = new DrawingLayerService();
        var l2 = new DrawingLayerService();

        using var coord1 = new DrawingEditCoordinator(m1, h1, session1, l1, sessionStore: store);
        using var coord2 = new DrawingEditCoordinator(m2, h2, session2, l2, sessionStore: store);

        // Add to coord1
        var line1 = CreateLine(1000m, 1200m);
        coord1.ExecuteEdit(DrawingOperationKind.Add, () =>
        {
            m1.AddObject(line1);
            l1.AddObjectToLayer(line1.Id, PanelKey.Main);
        });

        // Add to coord2
        var line2 = CreateLine(2000m, 2200m);
        coord2.ExecuteEdit(DrawingOperationKind.Add, () =>
        {
            m2.AddObject(line2);
            l2.AddObjectToLayer(line2.Id, PanelKey.Main);
        });

        Assert.Equal(1, coord1.UndoCount);
        Assert.Equal(1, coord2.UndoCount);
        long totalBytes = store.TotalHistoryPayloadBytes;
        Assert.True(totalBytes > 0);

        // Budget enforcement: if budget is smaller than totalBytes, coord1's older entry is evicted
        store.EnforceGlobalHistoryBudget(totalBytes - 10);
        Assert.Equal(0, coord1.UndoCount);
        Assert.Equal(1, coord2.UndoCount);
    }
}
