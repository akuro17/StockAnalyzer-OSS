using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel managing the collection of drawing objects and layers in the "Objects" panel tab.
/// Supports hierarchical layer representation, in-place diff synchronization, batch commands,
/// selected object operations, move axis mode switching, and Z-index descending projection.
/// </summary>
public partial class DrawingObjectsViewModel : ViewModelBase, IDisposable
{
    private ChartViewModel? _chartViewModel;
    private ChartObjectManager? _directObjectManager;
    private IDrawingLayerService? _directLayerService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IDialogService? _dialogService;
    private DrawingMoveAxisMode _fallbackMoveAxisMode = DrawingMoveAxisMode.XY;
    private bool _isSyncing;

    public ObservableCollection<DrawingObjectItemViewModel> Items { get; } = new();
    public ObservableCollection<DrawingLayerItemViewModel> Layers { get; } = new();

    public bool HasItems => Items.Count > 0;
    public bool HasLayers => Layers.Count > 0;

    public ChartObjectManager? ObjectManager => _directObjectManager ?? _chartViewModel?.ObjectManager;

    public DrawingObjectItemViewModel? SelectedObjectItem => Items.FirstOrDefault(it => it.IsSelected);
    public bool HasSelectedObject => SelectedObjectItem != null;
    public bool CanCopySelected => SelectedObjectItem != null && SelectedObjectItem.CanCopy;
    public bool CanOpenSettings => SelectedObjectItem != null && SelectedObjectItem.EffectiveEditAllowed;
    public bool CanBringForwardSelected => SelectedObjectItem?.CanBringForward == true;
    public bool CanSendBackwardSelected => SelectedObjectItem?.CanSendBackward == true;
    public bool CanDeleteSelected => SelectedObjectItem?.CanDelete == true;
    public bool CanToggleVisibilitySelected => SelectedObjectItem != null && SelectedObjectItem.EffectiveEditAllowed;
    public bool CanToggleLockSelected => SelectedObjectItem != null && SelectedObjectItem.EffectiveEditAllowed;
    public bool IsSelectedVisible => SelectedObjectItem?.IsVisible ?? false;
    public bool IsSelectedLocked => SelectedObjectItem?.IsLocked ?? false;

    /// <summary>
    /// Master checkbox state for bulk-action targeting: true if all items are targeted, false if none are,
    /// null (indeterminate) if mixed. Setting this value writes it directly to every item's <see cref="DrawingObjectItemViewModel.IsTargeted"/>.
    /// </summary>
    public bool? AreAllTargeted
    {
        get
        {
            if (Items.Count == 0) return false;
            int targetedCount = Items.Count(i => i.IsTargeted);
            if (targetedCount == 0) return false;
            if (targetedCount == Items.Count) return true;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var item in Items)
            {
                item.IsTargeted = target;
            }
            OnPropertyChanged();
        }
    }

    public DrawingMoveAxisMode MoveAxisMode
    {
        get => _chartViewModel?.MoveAxisMode ?? _fallbackMoveAxisMode;
        set
        {
            if (MoveAxisMode != value)
            {
                if (_chartViewModel != null)
                {
                    _chartViewModel.MoveAxisMode = value;
                }
                else
                {
                    _fallbackMoveAxisMode = value;
                }
                NotifyMoveModeProperties();
            }
        }
    }

    public bool IsMoveModeXY => MoveAxisMode == DrawingMoveAxisMode.XY;
    public bool IsMoveModeX => MoveAxisMode == DrawingMoveAxisMode.X;
    public bool IsMoveModeY => MoveAxisMode == DrawingMoveAxisMode.Y;

    private readonly StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? _iconDrawingService;

    public IDrawingLayerService LayerService
    {
        get
        {
            if (_chartViewModel != null)
            {
                return _chartViewModel.LayerService;
            }
            if (_directLayerService == null)
            {
                _directLayerService = new DrawingLayerService();
                _directLayerService.LayerStateChanged += OnLayerStateChanged;
            }
            return _directLayerService;
        }
    }

    public DrawingObjectsViewModel(
        IDispatcherService dispatcherService, 
        IDialogService? dialogService = null, 
        StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? iconDrawingService = null,
        IDrawingLayerService? layerService = null)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _dialogService = dialogService;
        _iconDrawingService = iconDrawingService;
        _directLayerService = layerService;
        if (_directLayerService != null)
        {
            _directLayerService.LayerStateChanged += OnLayerStateChanged;
        }
    }

    public DrawingObjectsViewModel(
        ChartViewModel chartViewModel, 
        IDispatcherService dispatcherService, 
        IDialogService? dialogService = null, 
        StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? iconDrawingService = null,
        IDrawingLayerService? layerService = null)
        : this(dispatcherService, dialogService, iconDrawingService, layerService)
    {
        SetChartViewModel(chartViewModel);
    }

    public DrawingObjectsViewModel(
        ChartObjectManager objectManager, 
        IDispatcherService dispatcherService, 
        IDialogService? dialogService = null, 
        StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? iconDrawingService = null,
        IDrawingLayerService? layerService = null)
        : this(dispatcherService, dialogService, iconDrawingService, layerService)
    {
        SetObjectManager(objectManager);
    }

    public void SetObjectManager(ChartObjectManager? objectManager)
    {
        if (_directObjectManager == objectManager) return;

        if (_directObjectManager != null)
        {
            _directObjectManager.Synced -= OnManagerChanged;
        }

        _directObjectManager = objectManager;

        if (_directObjectManager != null)
        {
            _directObjectManager.Synced += OnManagerChanged;
        }

        SyncFromManager();
    }

    public void SetChartViewModel(ChartViewModel? chartViewModel)
    {
        if (_chartViewModel == chartViewModel) return;

        if (_chartViewModel != null)
        {
            _chartViewModel.ObjectManager.Synced -= OnManagerChanged;
            _chartViewModel.PropertyChanged -= OnChartViewModelPropertyChanged;
            _chartViewModel.LayerService.LayerStateChanged -= OnLayerStateChanged;
        }

        _chartViewModel = chartViewModel;

        if (_chartViewModel != null)
        {
            _chartViewModel.ObjectManager.Synced += OnManagerChanged;
            _chartViewModel.PropertyChanged += OnChartViewModelPropertyChanged;
            _chartViewModel.LayerService.LayerStateChanged += OnLayerStateChanged;
        }

        SyncFromManager();
        NotifyMoveModeProperties();
    }

    private void OnChartViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartViewModel.MoveAxisMode))
        {
            NotifyMoveModeProperties();
        }
    }

    private void OnManagerChanged()
    {
        _dispatcherService.Post(static vm => vm.SyncFromManager(), this);
    }

    private void OnLayerStateChanged()
    {
        _chartViewModel?.RequestRender(RenderReason.DataChanged);
        _dispatcherService.Post(static vm => vm.SyncFromManager(), this);
    }

    internal PanelKey ResolvePanelForObject(IChartObject obj)
    {
        if (obj.PanelIndex == -1 || _chartViewModel == null)
        {
            return PanelKey.Main;
        }
        return DrawingPanelResolver.TryResolvePanelKey(obj.PanelIndex, _chartViewModel.Indicators, _chartViewModel.IsSubWindowVisible)
            ?? PanelKey.OverlayGroup(obj.PanelIndex);
    }

    internal string ResolvePanelDisplayName(PanelKey panel)
    {
        if (panel.Kind == PanelKind.Main)
        {
            var loc = LocalizationManager.Instance["Objects_PanelMain"];
            return (!string.IsNullOrEmpty(loc) && !loc.StartsWith("[")) ? loc : "Main";
        }
        if (panel.Kind == PanelKind.Indicator && panel.IndicatorId != null)
        {
            var ind = _chartViewModel?.Indicators?.FirstOrDefault(i => i.Id == panel.IndicatorId);
            if (ind != null)
            {
                if (!string.IsNullOrEmpty(ind.ShortDisplayName)) return ind.ShortDisplayName;
                if (!string.IsNullOrEmpty(ind.DisplayName)) return ind.DisplayName;
                return "Indicator";
            }
            return "Indicator";
        }
        if (panel.Kind == PanelKind.OverlayGroup && panel.OverlayGroupId != null)
        {
            return $"Overlay {panel.OverlayGroupId}";
        }
        return panel.Kind.ToString();
    }

    public void SyncFromManager()
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            var manager = ObjectManager;
            var layerService = LayerService;

            if (manager == null)
            {
                Items.Clear();
                Layers.Clear();
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(HasLayers));
                NotifySelectedProperties();
                return;
            }

            var models = manager.Objects; // 0..n-1 (ZIndex ascending)

            // 1. Ensure all objects in manager are registered in layerService
            if (layerService != null)
            {
                foreach (var model in models)
                {
                    if (!layerService.IsObjectRegistered(model.Id))
                    {
                        var panel = ResolvePanelForObject(model);
                        layerService.AddObjectToLayer(model.Id, panel);
                    }
                }
            }

            // 2. Diff sync flat Items collection (UI list is in descending ZIndex order: top-most first)
            var modelIdSet = new HashSet<Guid>(models.Select(m => m.Id));
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!modelIdSet.Contains(Items[i].Id))
                {
                    Items.RemoveAt(i);
                }
            }

            var itemMap = Items.ToDictionary(it => it.Id);
            int targetIndex = 0;
            for (int z = models.Count - 1; z >= 0; z--)
            {
                var model = models[z];
                if (itemMap.TryGetValue(model.Id, out var existingVM))
                {
                    int currentIndex = Items.IndexOf(existingVM);
                    if (currentIndex != targetIndex)
                    {
                        Items.Move(currentIndex, targetIndex);
                    }
                    existingVM.RefreshState();
                }
                else
                {
                    var newVM = new DrawingObjectItemViewModel(model, manager, () => NotifySelectedProperties(), m => OpenSettingsForModelAsync(m), _iconDrawingService, layerService, () => _chartViewModel?.DrawingEditCoordinator);
                    Items.Insert(targetIndex, newVM);
                    itemMap[model.Id] = newVM;
                }
                targetIndex++;
            }

            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].RefreshState();
            }

            // 3. Diff sync hierarchical Layers collection
            if (layerService != null)
            {
                bool anyPruned = false;
                var allLayersBefore = layerService.GetAllLayers();
                foreach (var l in allLayersBefore)
                {
                    if (l.Panel.Kind != PanelKind.Main && l.ObjectIds.Count == 0)
                    {
                        bool isPanelActive = DrawingPanelResolver.TryGetPanelIndex(l.Panel, _chartViewModel?.Indicators, _chartViewModel?.IsSubWindowVisible ?? false).HasValue;
                        if (!isPanelActive)
                        {
                            var result = layerService.DeleteLayer(l.LayerId, confirmed: true);
                            if (result.IsSuccess)
                            {
                                anyPruned = true;
                            }
                        }
                    }
                }

                if (anyPruned)
                {
                    _chartViewModel?.PersistCurrentDrawings();
                }

                var allLayers = layerService.GetAllLayers();
                var panels = allLayers.Select(l => l.Panel).Distinct()
                    .OrderBy(p => p.Kind == PanelKind.Main ? 0 : 1)
                    .ThenBy(p => p.IndicatorId ?? p.OverlayGroupId ?? p.OverlayPanelId?.ToString() ?? string.Empty)
                    .ToList();

                var targetLayerRecords = new List<DrawingLayerRecord>();
                foreach (var p in panels)
                {
                    var panelLayers = layerService.GetLayersForPanel(p);
                    for (int i = panelLayers.Count - 1; i >= 0; i--)
                    {
                        targetLayerRecords.Add(panelLayers[i]);
                    }
                }

                var targetLayerIdSet = new HashSet<Guid>(targetLayerRecords.Select(r => r.LayerId));
                for (int i = Layers.Count - 1; i >= 0; i--)
                {
                    if (!targetLayerIdSet.Contains(Layers[i].LayerId))
                    {
                        Layers.RemoveAt(i);
                    }
                }

                var layerMap = Layers.ToDictionary(l => l.LayerId);
                int layerTargetIndex = 0;

                foreach (var record in targetLayerRecords)
                {
                    var panelDisplayName = ResolvePanelDisplayName(record.Panel);
                    DrawingLayerItemViewModel layerVM;
                    if (layerMap.TryGetValue(record.LayerId, out var existingLayerVM))
                    {
                        int currentIndex = Layers.IndexOf(existingLayerVM);
                        if (currentIndex != layerTargetIndex)
                        {
                            Layers.Move(currentIndex, layerTargetIndex);
                        }
                        existingLayerVM.RefreshFromRecord(record, panelDisplayName);
                        layerVM = existingLayerVM;
                    }
                    else
                    {
                        layerVM = new DrawingLayerItemViewModel(
                            record,
                            layerService,
                            () =>
                            {
                                _chartViewModel?.RequestRender(RenderReason.DataChanged);
                                SyncFromManager();
                            },
                            layerItem => OnDeleteLayerRequestedAsync(layerItem),
                            () => _chartViewModel?.DrawingEditCoordinator,
                            panelDisplayName);
                        Layers.Insert(layerTargetIndex, layerVM);
                        layerMap[record.LayerId] = layerVM;
                    }
                    layerTargetIndex++;

                    var activeLayerId = layerService.GetActiveLayerId(record.Panel);
                    layerVM.IsSelected = (activeLayerId == record.LayerId);

                    bool isPanelActive = (record.Panel.Kind == PanelKind.Main) ||
                        DrawingPanelResolver.TryGetPanelIndex(record.Panel, _chartViewModel?.Indicators, _chartViewModel?.IsSubWindowVisible ?? false).HasValue;
                    layerVM.IsDormant = !isPanelActive;

                    // Sync layerVM.Children
                    var layerObjectIds = new HashSet<Guid>(record.ObjectIds);
                    var targetChildren = Items.Where(it => layerObjectIds.Contains(it.Id)).ToList();

                    var targetChildIdSet = new HashSet<Guid>(targetChildren.Select(c => c.Id));
                    for (int c = layerVM.Children.Count - 1; c >= 0; c--)
                    {
                        if (!targetChildIdSet.Contains(layerVM.Children[c].Id))
                        {
                            layerVM.Children.RemoveAt(c);
                        }
                    }

                    int childTargetIndex = 0;
                    foreach (var childVM in targetChildren)
                    {
                        int currentChildIndex = layerVM.Children.IndexOf(childVM);
                        if (currentChildIndex >= 0)
                        {
                            if (currentChildIndex != childTargetIndex)
                            {
                                layerVM.Children.Move(currentChildIndex, childTargetIndex);
                            }
                        }
                        else
                        {
                            layerVM.Children.Insert(childTargetIndex, childVM);
                        }
                        childTargetIndex++;
                    }

                    layerVM.RefreshFromRecord(record, panelDisplayName);
                }
            }

            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasLayers));
            NotifySelectedProperties();
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task OnDeleteLayerRequestedAsync(DrawingLayerItemViewModel layerItem)
    {
        var layer = LayerService.GetLayer(layerItem.LayerId);
        if (!layer.HasValue) return;
        var record = layer.Value;

        bool confirmed = false;
        if (record.ObjectIds.Count > 0)
        {
            var dialogService = _dialogService ?? _chartViewModel?.DialogService;
            if (dialogService == null)
            {
                // Confirmation cannot be obtained without dialog service
                return;
            }

            var title = LocalizationManager.Instance["Objects_DeleteLayerConfirm_Title"];
            var msgTemplate = LocalizationManager.Instance["Objects_DeleteLayerConfirm_Message"];
            var message = string.Format(msgTemplate, layerItem.Name, record.ObjectIds.Count);
            if (!await dialogService.ShowConfirmationAsync(title, message)) return;
            confirmed = true;
        }

        var manager = ObjectManager;
        var coord = _chartViewModel?.DrawingEditCoordinator;
        bool deleteSuccess = false;

        Action performDelete = () =>
        {
            var result = LayerService.DeleteLayer(layerItem.LayerId, confirmed: confirmed, id => manager?.GetObject(id)?.IsLocked == true);
            if (result.IsSuccess)
            {
                deleteSuccess = true;
                if (manager != null && record.ObjectIds.Count > 0)
                {
                    using (manager.BeginBatch())
                    {
                        foreach (var objId in record.ObjectIds)
                        {
                            manager.RemoveObject(objId);
                        }
                    }
                }
            }
        };

        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerDelete, performDelete);
        }
        else
        {
            performDelete();
        }

        if (deleteSuccess)
        {
            _chartViewModel?.PersistCurrentDrawings();
            _chartViewModel?.RequestRender(RenderReason.DataChanged);
            SyncFromManager();
        }
    }

    public void NotifySelectedProperties()
    {
        OnPropertyChanged(nameof(SelectedObjectItem));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(CanCopySelected));
        OnPropertyChanged(nameof(CanOpenSettings));
        OnPropertyChanged(nameof(CanBringForwardSelected));
        OnPropertyChanged(nameof(CanSendBackwardSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanToggleVisibilitySelected));
        OnPropertyChanged(nameof(CanToggleLockSelected));
        OnPropertyChanged(nameof(IsSelectedVisible));
        OnPropertyChanged(nameof(IsSelectedLocked));
        OnPropertyChanged(nameof(AreAllTargeted));

        CopySelectedCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
        ToggleVisibilitySelectedCommand.NotifyCanExecuteChanged();
        ToggleLockSelectedCommand.NotifyCanExecuteChanged();
        BringForwardSelectedCommand.NotifyCanExecuteChanged();
        SendBackwardSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    public void NotifyMoveModeProperties()
    {
        OnPropertyChanged(nameof(MoveAxisMode));
        OnPropertyChanged(nameof(IsMoveModeXY));
        OnPropertyChanged(nameof(IsMoveModeX));
        OnPropertyChanged(nameof(IsMoveModeY));
    }

    [RelayCommand]
    private void SetMoveAxisMode(DrawingMoveAxisMode mode)
    {
        MoveAxisMode = mode;
    }

    [RelayCommand]
    private void SetMoveAxisModeString(string modeStr)
    {
        if (Enum.TryParse<DrawingMoveAxisMode>(modeStr, true, out var mode))
        {
            MoveAxisMode = mode;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.CopyCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    public async Task OpenSettingsForModelAsync(IChartObject model)
    {
        var dialogService = _dialogService ?? _chartViewModel?.DialogService;
        if (model == null || dialogService == null) return;

        // F01/A02 fix: this Layers-Panel entry point shares the same identity risk as the canvas
        // double-click entry point (ChartInteractionController.HandleEditObject) -- an Undo/Redo or a
        // concurrent Cancel elsewhere can materialize a fresh runtime instance (new Id) via
        // ChartObjectManager.LoadSnapshot, orphaning this `model` reference. Re-resolve the live object
        // before binding the dialog so both entry points preserve edits identically (A02). Falling back
        // to model.Id when no persistent mapping exists yet is correct for a just-drawn, never-committed
        // object, not a stale reference.
        var editTarget = model;
        var coordinatorForResolve = _chartViewModel?.DrawingEditCoordinator;
        if (coordinatorForResolve != null)
        {
            var liveId = model.Id;
            if (coordinatorForResolve.TryResolveCurrentRuntimeId(model.Id, out var resolvedLiveId))
            {
                liveId = resolvedLiveId;
            }
            var liveObject = ObjectManager?.GetObject(liveId);
            if (liveObject == null)
            {
                return;
            }
            editTarget = liveObject;
        }

        if (editTarget is StockAnalyzer.Avalonia.Drawing.Objects.InformationObject infoTarget && infoTarget.Snapshot == null && _chartViewModel?.Candles != null && _chartViewModel.Candles.Count > 0)
        {
            infoTarget.Snapshot = Drawing.ChartInformationDataProvider.Extract(
                _chartViewModel,
                _chartViewModel.Candles.Last().Timestamp,
                _chartViewModel.Candles.Last().Close);
        }

        // Color/Thickness/panel-specific edits are written directly onto the model, bypassing every
        // ChartObjectManager mutator (ToggleLock, RenameObject, etc.), so neither ObjectManager.Changed
        // (persistence) nor .Synced (canvas InvalidateVisual, see ChartBaseControl.OnObjectManagerChanged)
        // fires for them on its own. NotifyObjectChanged fires both explicitly -- both here and below
        // for "Changed" -- or the edit stays invisible on the chart until some unrelated interaction
        // happens to force a repaint.
        //
        // Apply live-commits the dialog's current values without closing it, so the chart must be
        // redrawn and persisted immediately -- the same steps taken below for a "Changed" close.
        var result = await dialogService.ShowDrawingSettingsDialogAsync(editTarget, onApply: obj =>
        {
            if (_chartViewModel?.Candles != null)
            {
                Drawing.DeferredComputationRecalculator.TryRecalculate(obj, _chartViewModel.Candles);
            }
            ObjectManager?.NotifyObjectChanged(obj.Id);
            if (_chartViewModel?.DrawingEditCoordinator == null)
            {
                _chartViewModel?.PersistCurrentDrawings();
            }
            _chartViewModel?.RequestRender(RenderReason.DataChanged);
            SyncFromManager();
        }, coordinator: _chartViewModel?.DrawingEditCoordinator, candles: _chartViewModel?.Candles);
        if (result == DrawingSettingsResult.Deleted)
        {
            var coord = _chartViewModel?.DrawingEditCoordinator;
            var targetId = editTarget.Id;
            if (coord != null)
            {
                if (coord.TryResolveCurrentRuntimeId(editTarget.Id, out var resolvedId))
                {
                    targetId = resolvedId;
                }
                coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Delete, () =>
                {
                    ObjectManager?.RemoveObject(targetId);
                    LayerService?.RemoveObjectFromLayer(targetId);
                });
            }
            else
            {
                ObjectManager?.RemoveObject(targetId);
                LayerService?.RemoveObjectFromLayer(targetId);
                _chartViewModel?.PersistCurrentDrawings();
            }
            SyncFromManager();
        }
        else if (result == DrawingSettingsResult.Changed)
        {
            if (_chartViewModel?.Candles != null)
            {
                Drawing.DeferredComputationRecalculator.TryRecalculate(editTarget, _chartViewModel.Candles);
            }
            ObjectManager?.NotifyObjectChanged(editTarget.Id);
            if (_chartViewModel?.DrawingEditCoordinator == null)
            {
                _chartViewModel?.PersistCurrentDrawings();
            }
            _chartViewModel?.RequestRender(RenderReason.DataChanged);
            SyncFromManager();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private async Task OpenSettingsAsync()
    {
        var selected = SelectedObjectItem?.Model;
        if (selected != null)
        {
            await OpenSettingsForModelAsync(selected);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleVisibilitySelected))]
    private void ToggleVisibilitySelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.ToggleVisibilityCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleLockSelected))]
    private void ToggleLockSelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.ToggleLockCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBringForwardSelected))]
    private void BringForwardSelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.BringForwardCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendBackwardSelected))]
    private void SendBackwardSelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.SendBackwardCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (SelectedObjectItem != null)
        {
            SelectedObjectItem.DeleteCommand.Execute(null);
            NotifySelectedProperties();
        }
    }

    private IEnumerable<DrawingObjectItemViewModel> TargetedItems => Items.Where(i => i.IsTargeted);

    [RelayCommand]
    public void CreateLayer()
    {
        var targetPanel = PanelKey.Main;
        var selectedLayer = Layers.FirstOrDefault(l => l.IsSelected);
        if (selectedLayer != null)
        {
            targetPanel = selectedLayer.Panel;
        }
        else if (SelectedObjectItem != null)
        {
            var layerRecord = LayerService.GetLayerForObject(SelectedObjectItem.Id);
            if (layerRecord.HasValue)
            {
                targetPanel = layerRecord.Value.Panel;
            }
        }

        var existingLayers = LayerService.GetLayersForPanel(targetPanel);
        var namePrefix = LocalizationManager.Instance["Objects_LayerDefaultName"];
        if (string.IsNullOrEmpty(namePrefix) || namePrefix.StartsWith("["))
        {
            namePrefix = "Layer";
        }
        var newName = $"{namePrefix} {existingLayers.Count + 1}";

        var coord = _chartViewModel?.DrawingEditCoordinator;
        bool created = false;
        Action createAction = () =>
        {
            var result = LayerService.CreateLayer(targetPanel, newName);
            created = result.IsSuccess;
        };

        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerAdd, createAction);
        }
        else
        {
            createAction();
        }

        if (created)
        {
            _chartViewModel?.RequestRender(RenderReason.DataChanged);
            SyncFromManager();
        }
    }

    /// <summary>
    /// Forces any in-progress inline rename to commit immediately. The rename TextBox only writes
    /// its buffered text into the model on Enter/Escape/LostFocus (see <see cref="DrawingObjectItemViewModel.CommitRenameCommand"/>),
    /// so a window closed via the OS close button while still editing would otherwise never fire
    /// that commit, silently discarding the typed name. Called from
    /// <c>MainWindowViewModel.ForceSaveOnShutdown</c> immediately before the drawing-persistence
    /// flush, mirroring how that method already force-flushes other pending drawing state.
    /// </summary>
    public void CommitPendingRename()
    {
        foreach (var item in Items.Where(i => i.IsEditingName).ToList())
        {
            item.CommitRenameCommand.Execute(null);
        }
        foreach (var layer in Layers.Where(l => l.IsEditingName).ToList())
        {
            layer.CommitRenameCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void LockAll()
    {
        if (ObjectManager == null) return;
        var coord = _chartViewModel?.DrawingEditCoordinator;
        Action act = () =>
        {
            using (ObjectManager.BeginBatch())
            {
                foreach (var item in TargetedItems.Where(i => i.EffectiveEditAllowed).ToList()) item.IsLocked = true;
            }
        };
        if (coord != null) coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.BulkLock, act);
        else act();
    }

    [RelayCommand]
    private void UnlockAll()
    {
        if (ObjectManager == null) return;
        var coord = _chartViewModel?.DrawingEditCoordinator;
        Action act = () =>
        {
            using (ObjectManager.BeginBatch())
            {
                foreach (var item in TargetedItems.Where(i => i.EffectiveEditAllowed).ToList()) item.IsLocked = false;
            }
        };
        if (coord != null) coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.BulkLock, act);
        else act();
    }

    [RelayCommand]
    private void ShowAll()
    {
        if (ObjectManager == null) return;
        var coord = _chartViewModel?.DrawingEditCoordinator;
        Action act = () =>
        {
            using (ObjectManager.BeginBatch())
            {
                foreach (var item in TargetedItems.Where(i => i.EffectiveEditAllowed).ToList()) item.IsVisible = true;
            }
        };
        if (coord != null) coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.BulkVisibility, act);
        else act();
    }

    [RelayCommand]
    private void HideAll()
    {
        if (ObjectManager == null) return;
        var coord = _chartViewModel?.DrawingEditCoordinator;
        Action act = () =>
        {
            using (ObjectManager.BeginBatch())
            {
                foreach (var item in TargetedItems.Where(i => i.EffectiveEditAllowed).ToList()) item.IsVisible = false;
            }
        };
        if (coord != null) coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.BulkVisibility, act);
        else act();
    }

    [RelayCommand]
    private async Task DeleteAll()
    {
        if (ObjectManager == null) return;

        var targets = TargetedItems.Where(i => i.CanDelete).ToList();
        if (targets.Count == 0) return;

        var dialogService = _dialogService ?? _chartViewModel?.DialogService;
        if (dialogService != null)
        {
            var title = LocalizationManager.Instance["Objects_ConfirmDeleteAll_Title"];
            var message = string.Format(LocalizationManager.Instance["Objects_ConfirmDeleteAll_Message"], targets.Count);
            if (!await dialogService.ShowConfirmationAsync(title, message)) return;
        }

        var coord = _chartViewModel?.DrawingEditCoordinator;
        Action act = () =>
        {
            using (ObjectManager.BeginBatch())
            {
                foreach (var item in targets)
                {
                    ObjectManager.RemoveObject(item.Id);
                    LayerService?.RemoveObjectFromLayer(item.Id);
                }
            }
        };

        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.BulkDelete, act);
        }
        else
        {
            act();
            _chartViewModel?.PersistCurrentDrawings();
        }

        SyncFromManager();
    }

    public void Dispose()
    {
        if (_directLayerService != null)
        {
            _directLayerService.LayerStateChanged -= OnLayerStateChanged;
            _directLayerService = null;
        }

        if (_directObjectManager != null)
        {
            _directObjectManager.Synced -= OnManagerChanged;
            _directObjectManager = null;
        }

        if (_chartViewModel != null)
        {
            _chartViewModel.LayerService.LayerStateChanged -= OnLayerStateChanged;
            _chartViewModel.ObjectManager.Synced -= OnManagerChanged;
            _chartViewModel.PropertyChanged -= OnChartViewModelPropertyChanged;
            _chartViewModel = null;
        }
    }
}
