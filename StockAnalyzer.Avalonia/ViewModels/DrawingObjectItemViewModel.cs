using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel representing an individual drawing object in the Objects management list.
/// </summary>
public partial class DrawingObjectItemViewModel : ViewModelBase
{
    public IChartObject Model { get; }
    private readonly ChartObjectManager _manager;
    private readonly Action? _onStateChanged;

    public Guid Id => Model.Id;
    public ChartObjectType Type => Model.Type;
    public string DisplayName => DrawingObjectDisplayNameHelper.GetDisplayName(Model);
    public Color Color => Model.Color;
    public IBrush ColorBrush => new SolidColorBrush(Model.Color);

    public bool IsIconObject => Model is IconObject;

    public global::Avalonia.Media.Imaging.Bitmap? IconThumbnail
    {
        get
        {
            if (Model is IconObject icon && !string.IsNullOrEmpty(icon.IconPath))
            {
                return _iconDrawingService?.GetAvaloniaBitmap(icon.IconPath);
            }
            return null;
        }
    }

    public bool IsVisible
    {
        get => _manager.IsVisible(Model.Id);
        set
        {
            if (!EffectiveEditAllowed) return;
            if (_manager.IsVisible(Model.Id) != value)
            {
                _manager.ToggleVisibility(Model.Id);
                OnPropertyChanged();
                _onStateChanged?.Invoke();
            }
        }
    }

    public bool IsLocked
    {
        get => _manager.IsLocked(Model.Id);
        set
        {
            if (!EffectiveEditAllowed) return;
            if (_manager.IsLocked(Model.Id) != value)
            {
                _manager.ToggleLock(Model.Id);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDelete));
                DeleteCommand.NotifyCanExecuteChanged();
                _onStateChanged?.Invoke();
            }
        }
    }

    public bool IsSelected
    {
        get => Model.IsSelected;
        set
        {
            if (Model.IsSelected != value)
            {
                if (value) _manager.SelectObject(Model.Id);
                else _manager.DeselectAll();
                OnPropertyChanged();
                _onStateChanged?.Invoke();
            }
        }
    }

    private bool _isTargeted;

    /// <summary>
    /// Marks this item as a target for the panel's bulk actions (Show/Hide/Lock/Unlock/Delete).
    /// Independent of <see cref="IsVisible"/> (render visibility) and <see cref="IsSelected"/> (canvas selection).
    /// </summary>
    public bool IsTargeted
    {
        get => _isTargeted;
        set
        {
            if (_isTargeted != value)
            {
                _isTargeted = value;
                OnPropertyChanged();
                _onStateChanged?.Invoke();
            }
        }
    }

    private bool _isEditingName;

    /// <summary>Whether the Layers Panel row is currently showing the inline rename TextBox for this item.</summary>
    public bool IsEditingName
    {
        get => _isEditingName;
        private set
        {
            if (_isEditingName != value)
            {
                _isEditingName = value;
                OnPropertyChanged();
            }
        }
    }

    private string _editableName = string.Empty;

    /// <summary>Working buffer bound to the inline rename TextBox; committed to <see cref="Model"/> on confirm.</summary>
    public string EditableName
    {
        get => _editableName;
        set
        {
            if (_editableName != value)
            {
                _editableName = value;
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void StartRename()
    {
        EditableName = DisplayName;
        IsEditingName = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!EffectiveEditAllowed)
        {
            IsEditingName = false;
            return;
        }
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.SettingsApply, () =>
            {
                _manager.RenameObject(Model.Id, EditableName);
            });
        }
        else
        {
            _manager.RenameObject(Model.Id, EditableName);
        }
        IsEditingName = false;
        OnPropertyChanged(nameof(DisplayName));
        _onStateChanged?.Invoke();
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsEditingName = false;
    }

    public DrawingMoveAxisMode MoveAxisMode
    {
        get => _manager.GetMoveAxisMode(Model.Id);
        set
        {
            if (_manager.GetMoveAxisMode(Model.Id) != value)
            {
                _manager.SetMoveAxisMode(Model.Id, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMoveModeXY));
                OnPropertyChanged(nameof(IsMoveModeX));
                OnPropertyChanged(nameof(IsMoveModeY));
                _onStateChanged?.Invoke();
            }
        }
    }

    public bool IsMoveModeXY => MoveAxisMode == DrawingMoveAxisMode.XY;
    public bool IsMoveModeX => MoveAxisMode == DrawingMoveAxisMode.X;
    public bool IsMoveModeY => MoveAxisMode == DrawingMoveAxisMode.Y;

    public int ZIndex => _manager.GetZIndex(Model.Id);

    public bool EffectiveEditAllowed
    {
        get
        {
            if (_layerService == null) return true;
            var layer = _layerService.GetLayerForObject(Model.Id);
            return layer == null || !layer.Value.IsEditLocked;
        }
    }

    public bool CanDelete => !_manager.IsLocked(Model.Id) && EffectiveEditAllowed;
    public bool CanCopy => !_manager.IsLocked(Model.Id) && EffectiveEditAllowed;
    public bool CanBringForward => _manager.CanBringForward(Model.Id) && EffectiveEditAllowed;
    public bool CanSendBackward => _manager.CanSendBackward(Model.Id) && EffectiveEditAllowed;

    private readonly Func<IChartObject, System.Threading.Tasks.Task>? _onOpenSettings;
    private readonly StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? _iconDrawingService;
    private readonly StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService? _layerService;
    private readonly Func<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator?>? _coordinatorProvider;

    public DrawingObjectItemViewModel(
        IChartObject model,
        ChartObjectManager manager,
        Action? onStateChanged = null,
        Func<IChartObject, System.Threading.Tasks.Task>? onOpenSettings = null,
        StockAnalyzer.Avalonia.Services.Drawing.IIconDrawingService? iconDrawingService = null,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService? layerService = null,
        Func<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator?>? coordinatorProvider = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _onStateChanged = onStateChanged;
        _onOpenSettings = onOpenSettings;
        _iconDrawingService = iconDrawingService;
        _layerService = layerService;
        _coordinatorProvider = coordinatorProvider;
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(EffectiveEditAllowed));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(MoveAxisMode));
        OnPropertyChanged(nameof(IsMoveModeXY));
        OnPropertyChanged(nameof(IsMoveModeX));
        OnPropertyChanged(nameof(IsMoveModeY));
        OnPropertyChanged(nameof(ZIndex));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanBringForward));
        OnPropertyChanged(nameof(CanSendBackward));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(IsIconObject));
        OnPropertyChanged(nameof(IconThumbnail));
        OnPropertyChanged(nameof(EligibleTargetLayers));
        OnPropertyChanged(nameof(HasEligibleTargetLayers));
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ToggleVisibilityCommand.NotifyCanExecuteChanged();
        ToggleLockCommand.NotifyCanExecuteChanged();
    }

    public bool HasEligibleTargetLayers => EligibleTargetLayers.Count > 0;

    public IReadOnlyList<StockAnalyzer.Core.Models.Drawing.DrawingLayerRecord> EligibleTargetLayers
    {
        get
        {
            if (_layerService == null) return Array.Empty<StockAnalyzer.Core.Models.Drawing.DrawingLayerRecord>();
            var currentLayer = _layerService.GetLayerForObject(Model.Id);
            if (!currentLayer.HasValue) return Array.Empty<StockAnalyzer.Core.Models.Drawing.DrawingLayerRecord>();
            var panelLayers = _layerService.GetLayersForPanel(currentLayer.Value.Panel);
            var list = new List<StockAnalyzer.Core.Models.Drawing.DrawingLayerRecord>();
            for (int i = 0; i < panelLayers.Count; i++)
            {
                var l = panelLayers[i];
                if (l.LayerId != currentLayer.Value.LayerId && !l.IsEditLocked)
                {
                    list.Add(l);
                }
            }
            return list;
        }
    }

    [RelayCommand]
    public void MoveToLayer(Guid targetLayerId)
    {
        if (!EffectiveEditAllowed || _layerService == null) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerReorder, () =>
            {
                _layerService.MoveObjectToLayer(Model.Id, targetLayerId);
            });
            _onStateChanged?.Invoke();
        }
        else
        {
            var result = _layerService.MoveObjectToLayer(Model.Id, targetLayerId);
            if (result.IsSuccess)
            {
                _onStateChanged?.Invoke();
            }
        }
    }

    [RelayCommand]
    private void SetMoveAxisMode(DrawingMoveAxisMode mode)
    {
        if (!EffectiveEditAllowed) return;
        MoveAxisMode = mode;
    }

    [RelayCommand]
    private void SetMoveAxisModeString(string modeStr)
    {
        if (!EffectiveEditAllowed) return;
        if (Enum.TryParse<DrawingMoveAxisMode>(modeStr, true, out var mode))
        {
            MoveAxisMode = mode;
        }
    }

    [RelayCommand]
    private void Select()
    {
        IsSelected = !IsSelected;
    }

    [RelayCommand(CanExecute = nameof(EffectiveEditAllowed))]
    private void ToggleVisibility()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.VisibilityToggle, () =>
            {
                _manager.ToggleVisibility(Model.Id);
            });
        }
        else
        {
            _manager.ToggleVisibility(Model.Id);
        }
        OnPropertyChanged(nameof(IsVisible));
        _onStateChanged?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(EffectiveEditAllowed))]
    private void ToggleLock()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LockToggle, () =>
            {
                _manager.ToggleLock(Model.Id);
            });
        }
        else
        {
            _manager.ToggleLock(Model.Id);
        }
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanDelete));
        DeleteCommand.NotifyCanExecuteChanged();
        _onStateChanged?.Invoke();
    }

    [RelayCommand]
    private void AdvanceAnchorPoint()
    {
        if (!EffectiveEditAllowed) return;
        // Always select on click, even for single-point objects where the manager call below is a
        // no-op (nothing to cycle to) -- otherwise the "AP" button appears completely unresponsive
        // for those tools instead of at least revealing which object it belongs to.
        _manager.AdvanceAnchorPoint(Model.Id);
        if (!IsSelected) IsSelected = true;
        _onStateChanged?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Duplicate, () =>
            {
                var clone = _manager.DuplicateObject(Model.Id);
                if (clone != null && _layerService != null)
                {
                    var srcLayer = _layerService.GetLayerForObject(Model.Id);
                    if (srcLayer != null)
                    {
                        _layerService.AddObjectToLayer(clone.Id, srcLayer.Value.Panel, srcLayer.Value.LayerId);
                    }
                }
            });
        }
        else
        {
            var clone = _manager.DuplicateObject(Model.Id);
            if (clone != null && _layerService != null)
            {
                var srcLayer = _layerService.GetLayerForObject(Model.Id);
                if (srcLayer != null)
                {
                    _layerService.AddObjectToLayer(clone.Id, srcLayer.Value.Panel, srcLayer.Value.LayerId);
                }
            }
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenSettingsAsync()
    {
        if (!EffectiveEditAllowed) return;
        if (_onOpenSettings != null)
        {
            await _onOpenSettings(Model);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.Delete, () =>
            {
                _manager.RemoveObject(Model.Id);
                _layerService?.RemoveObjectFromLayer(Model.Id);
            });
        }
        else
        {
            _manager.RemoveObject(Model.Id);
            _layerService?.RemoveObjectFromLayer(Model.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanBringForward))]
    private void BringForward()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.ZOrderChange, () =>
            {
                _layerService?.MoveObjectWithinLayer(Model.Id, +1);
                _manager.BringForward(Model.Id);
            });
        }
        else
        {
            _layerService?.MoveObjectWithinLayer(Model.Id, +1);
            _manager.BringForward(Model.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendBackward))]
    private void SendBackward()
    {
        if (!EffectiveEditAllowed) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.ZOrderChange, () =>
            {
                _layerService?.MoveObjectWithinLayer(Model.Id, -1);
                _manager.SendBackward(Model.Id);
            });
        }
        else
        {
            _layerService?.MoveObjectWithinLayer(Model.Id, -1);
            _manager.SendBackward(Model.Id);
        }
    }
}
