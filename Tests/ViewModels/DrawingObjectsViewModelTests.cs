using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
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

    /// <summary>Minimal IDialogService test double: only ShowConfirmationAsync is exercised by
    /// DrawingObjectsViewModel.DeleteAll's confirmation gate; every other member is unused here.</summary>
    private class FakeDialogService : IDialogService
    {
        public bool ConfirmationResult { get; set; }
        public int ConfirmationCallCount { get; private set; }
        public string? LastConfirmationMessage { get; private set; }

        public Task<bool> ShowConfirmationAsync(string title, string message)
        {
            ConfirmationCallCount++;
            LastConfirmationMessage = message;
            return Task.FromResult(ConfirmationResult);
        }

        public Task ShowAlertAsync(string title, string message) => throw new NotImplementedException();
        public Task<string?> ShowInputAsync(string title, string message, string defaultValue = "") => throw new NotImplementedException();
        public Task<StockAnalyzer.Avalonia.Models.AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId) => throw new NotImplementedException();
        public Task<StockAnalyzer.Core.Models.Portfolio.Transaction?> ShowEditTransactionDialogAsync(StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTransactionDialogViewModel viewModel) => throw new NotImplementedException();
        public Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12) => throw new NotImplementedException();
        public Task<DrawingSettingsResult> ShowDrawingSettingsDialogAsync(IChartObject drawing, Action<IChartObject>? onApply = null) => throw new NotImplementedException();
        public Task<Color?> ShowColorPickerAsync(Color initialColor) => throw new NotImplementedException();
        public Task ShowIndicatorSettingsDialogAsync(IEnumerable<StockAnalyzer.Core.Models.CoreIndicatorSettings> currentIndicators, Action<IEnumerable<StockAnalyzer.Core.Models.CoreIndicatorSettings>>? onApply = null) => throw new NotImplementedException();
        public Task ShowIndicatorPropertiesDialogAsync(StockAnalyzer.Core.Models.CoreIndicatorSettings indicator, Action<StockAnalyzer.Core.Models.CoreIndicatorSettings>? onApply = null, IEnumerable<StockAnalyzer.Core.Models.CoreIndicatorSettings>? allIndicators = null) => throw new NotImplementedException();
        public Task ShowThemeSettingsDialogAsync() => throw new NotImplementedException();
        public Task ShowSettingsDialogAsync(string? initialCategoryKey = null) => throw new NotImplementedException();
        public Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<StockAnalyzer.Core.Models.Watchlist.WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null) => throw new NotImplementedException();
        public Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings, Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null) => throw new NotImplementedException();
        public Task ShowFilterTemplatePickerDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.FilterNode targetNode) => throw new NotImplementedException();
        public Task ShowFilterTemplatePickerForNewFilterDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode parentNode) => throw new NotImplementedException();
        public Task ShowScreenerDialogAsync() => throw new NotImplementedException();
        public Task ShowTrainingWizardDialogAsync() => throw new NotImplementedException();
        public Task<StockAnalyzer.Avalonia.Models.BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags) => throw new NotImplementedException();
        public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? reminder = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null) => throw new NotImplementedException();
        [Obsolete]
        public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? reminder, Action<decimal?, decimal?, decimal?, string?>? onSave) => throw new NotImplementedException();
        public Task ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab initialTab = StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Deleted) => throw new NotImplementedException();
        public IMultiSyncProgressSession CreateMultiSyncProgressSession() => throw new NotImplementedException();
        public Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonSetupConfirmationAsync() => throw new NotImplementedException();
        public Task ShowManualSetupInstructionsAsync() => throw new NotImplementedException();
        public Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonUpdateConfirmationAsync() => throw new NotImplementedException();
        public Task ShowPythonManualUpdateInstructionsAsync() => throw new NotImplementedException();
        public Task RunWithProgressAsync(string title, Func<IProgress<string>, Task> action) => throw new NotImplementedException();
        public object? GetMainWindowOwner() => null;
        public Task ShowLogViewerAsync() => throw new NotImplementedException();
        public Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null) => throw new NotImplementedException();
        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null, string? initialDirectory = null) => throw new NotImplementedException();
        public Task<string?> ShowOpenFolderDialogAsync(string title, string? initialDirectory = null) => throw new NotImplementedException();
        public Task<bool> ShowExportChartImageDialogAsync(StockAnalyzer.Avalonia.ViewModels.ChartViewModel chartViewModel) => throw new NotImplementedException();
        public void Shutdown() { }
        public void ActivateMainWindow() { }
    }

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
        public int AnchorPointIndex { get; set; } = 0;
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
    public void ItemViewModel_AdvanceAnchorPoint_MovesAnchorOneStepClockwiseAndWraps()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0);
        // Clockwise (on screen) triangle: matches AnchorPointOrderHelperTests' already-clockwise case.
        var o1 = new TestDrawingObject
        {
            Type = ChartObjectType.TrendLine,
            Points = new List<ChartPoint>
            {
                new ChartPoint(baseTime, 0m),
                new ChartPoint(baseTime.AddSeconds(1), -1m),
                new ChartPoint(baseTime.AddSeconds(-1), -1m),
            }
        };
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);
        var item1 = vm.Items[0];

        Assert.Equal(0, o1.AnchorPointIndex);
        Assert.False(item1.IsSelected);

        item1.AdvanceAnchorPointCommand.Execute(null);
        Assert.Equal(1, o1.AnchorPointIndex);
        Assert.True(item1.IsSelected, "Advancing the anchor should select the object so the change is visible.");

        item1.AdvanceAnchorPointCommand.Execute(null);
        Assert.Equal(2, o1.AnchorPointIndex);

        item1.AdvanceAnchorPointCommand.Execute(null);
        Assert.Equal(0, o1.AnchorPointIndex); // wraps back to the start
    }

    [Fact]
    public void ItemViewModel_AdvanceAnchorPoint_SelectsSinglePointObjectEvenThoughItCannotCycle()
    {
        // Regression test: for a single-point object (e.g. GannSquareOfNineObject),
        // ChartObjectManager.AdvanceAnchorPoint is a no-op (nothing to cycle to), but clicking "AP"
        // must still select the object -- otherwise the button appears completely unresponsive.
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject
        {
            Type = ChartObjectType.TrendLine,
            Points = new List<ChartPoint> { new ChartPoint(new DateTime(2025, 1, 1), 10m) }
        };
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);
        var item1 = vm.Items[0];

        Assert.False(item1.IsSelected);

        item1.AdvanceAnchorPointCommand.Execute(null);

        Assert.True(item1.IsSelected, "AP should select the object even when it has too few points to cycle.");
        Assert.Equal(0, o1.AnchorPointIndex); // unchanged -- nothing to cycle to
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

    [Fact]
    public async Task DeleteAll_UserCancelsConfirmation_DoesNotDelete()
    {
        var dispatcher = new SynchronousDispatcherService();
        var dialogService = new FakeDialogService { ConfirmationResult = false };
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, dialogService);
        foreach (var item in vm.Items) item.IsTargeted = true;

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmationCallCount);
        Assert.Single(vm.Items); // Nothing deleted
        Assert.Single(manager.Objects);
    }

    [Fact]
    public async Task DeleteAll_UserConfirms_DeletesTargetedObjects()
    {
        // LocalizationManager.Instance is a process-wide singleton only populated once
        // Initialize() runs; without this, message-content assertions below would be flaky
        // depending on whether an earlier test in the same run happened to initialize it.
        LocalizationManager.Instance.Initialize("en");

        var dispatcher = new SynchronousDispatcherService();
        var dialogService = new FakeDialogService { ConfirmationResult = true };
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        manager.AddObject(o1);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, dialogService);
        foreach (var item in vm.Items) item.IsTargeted = true;

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogService.ConfirmationCallCount);
        Assert.Contains("1", dialogService.LastConfirmationMessage);
        Assert.Empty(vm.Items);
        Assert.Empty(manager.Objects);
    }

    [Fact]
    public async Task DeleteAll_NoTargetedObjects_DoesNotPromptConfirmation()
    {
        var dispatcher = new SynchronousDispatcherService();
        var dialogService = new FakeDialogService { ConfirmationResult = true };
        var manager = new ChartObjectManager();
        var o1 = new TestDrawingObject { Type = ChartObjectType.TrendLine };
        manager.AddObject(o1); // Not targeted

        using var vm = new DrawingObjectsViewModel(manager, dispatcher, dialogService);

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogService.ConfirmationCallCount);
        Assert.Single(vm.Items);
    }
}
