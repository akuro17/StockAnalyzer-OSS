using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel representing an individual drawing layer in the objects list tab.
/// Owns its child drawing objects and provides commands for layer visibility,
/// edit lock, rename, reordering, and deletion.
/// </summary>
public partial class DrawingLayerItemViewModel : ViewModelBase
{
    private readonly IDrawingLayerService _layerService;
    private readonly Action _onChangedCallback;
    private readonly Func<DrawingLayerItemViewModel, System.Threading.Tasks.Task>? _deleteConfirmCallback;
    private readonly Func<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator?>? _coordinatorProvider;

    public Guid LayerId { get; }
    public PanelKey Panel { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isEditLocked = false;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected = false;

    [ObservableProperty]
    private bool _isDormant = false;

    [ObservableProperty]
    private string _panelDisplayName = string.Empty;

    [ObservableProperty]
    private bool _isEditingName = false;

    [ObservableProperty]
    private string _editingName = string.Empty;

    public ObservableCollection<DrawingObjectItemViewModel> Children { get; } = new();

    public int ObjectCount => Children.Count;
    public bool HasChildren => Children.Count > 0;

    public bool CanDelete => !IsEditLocked && (Panel.Kind != PanelKind.Main || _layerService.GetLayersForPanel(Panel).Count > 1);

    public DrawingLayerItemViewModel(
        DrawingLayerRecord record,
        IDrawingLayerService layerService,
        Action onChangedCallback,
        Func<DrawingLayerItemViewModel, System.Threading.Tasks.Task>? deleteConfirmCallback = null,
        Func<StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator?>? coordinatorProvider = null,
        string? panelDisplayName = null)
    {
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
        _onChangedCallback = onChangedCallback ?? throw new ArgumentNullException(nameof(onChangedCallback));
        _deleteConfirmCallback = deleteConfirmCallback;
        _coordinatorProvider = coordinatorProvider;

        LayerId = record.LayerId;
        Panel = record.Panel;
        _name = record.Name;
        _isVisible = record.IsVisible;
        _isEditLocked = record.IsEditLocked;
        _panelDisplayName = panelDisplayName ?? (record.Panel.Kind == PanelKind.Main ? "Main" : record.Panel.Kind.ToString());
    }

    public void RefreshFromRecord(DrawingLayerRecord record, string? panelDisplayName = null)
    {
        if (!string.IsNullOrEmpty(panelDisplayName))
        {
            PanelDisplayName = panelDisplayName;
        }
        Name = record.Name;
        IsVisible = record.IsVisible;
        IsEditLocked = record.IsEditLocked;
        OnPropertyChanged(nameof(CanDelete));
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ObjectCount));
        OnPropertyChanged(nameof(HasChildren));
    }

    [RelayCommand]
    public void ToggleVisibility()
    {
        if (IsEditLocked) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerVisibilityToggle, () =>
            {
                var result = _layerService.SetLayerVisibility(LayerId, !IsVisible);
                if (result.IsSuccess) IsVisible = !IsVisible;
            });
            _onChangedCallback();
        }
        else
        {
            var result = _layerService.SetLayerVisibility(LayerId, !IsVisible);
            if (result.IsSuccess)
            {
                IsVisible = !IsVisible;
                _onChangedCallback();
            }
        }
    }

    [RelayCommand]
    public void ToggleLock()
    {
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerLockToggle, () =>
            {
                var result = _layerService.SetLayerEditLock(LayerId, !IsEditLocked);
                if (result.IsSuccess) IsEditLocked = !IsEditLocked;
            });
            OnPropertyChanged(nameof(CanDelete));
            DeleteCommand.NotifyCanExecuteChanged();
            _onChangedCallback();
        }
        else
        {
            var result = _layerService.SetLayerEditLock(LayerId, !IsEditLocked);
            if (result.IsSuccess)
            {
                IsEditLocked = !IsEditLocked;
                OnPropertyChanged(nameof(CanDelete));
                DeleteCommand.NotifyCanExecuteChanged();
                _onChangedCallback();
            }
        }
    }

    [RelayCommand]
    public void Select()
    {
        _layerService.SetActiveLayerId(Panel, LayerId);
        _onChangedCallback();
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    public void MoveUp()
    {
        if (IsEditLocked) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerReorder, () =>
            {
                _layerService.MoveLayer(LayerId, +1);
            });
            _onChangedCallback();
        }
        else
        {
            var result = _layerService.MoveLayer(LayerId, +1);
            if (result.IsSuccess)
            {
                _onChangedCallback();
            }
        }
    }

    [RelayCommand]
    public void MoveDown()
    {
        if (IsEditLocked) return;
        var coord = _coordinatorProvider?.Invoke();
        if (coord != null)
        {
            coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerReorder, () =>
            {
                _layerService.MoveLayer(LayerId, -1);
            });
            _onChangedCallback();
        }
        else
        {
            var result = _layerService.MoveLayer(LayerId, -1);
            if (result.IsSuccess)
            {
                _onChangedCallback();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async System.Threading.Tasks.Task DeleteAsync()
    {
        if (IsEditLocked) return;

        if (_deleteConfirmCallback != null)
        {
            await _deleteConfirmCallback(this);
        }
        else
        {
            var coord = _coordinatorProvider?.Invoke();
            if (coord != null)
            {
                coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerDelete, () =>
                {
                    _layerService.DeleteLayer(LayerId, confirmed: false);
                });
                _onChangedCallback();
            }
            else
            {
                var result = _layerService.DeleteLayer(LayerId, confirmed: false);
                if (result.IsSuccess)
                {
                    _onChangedCallback();
                }
            }
        }
    }

    [RelayCommand]
    public void StartRename()
    {
        if (IsEditLocked) return;
        EditingName = Name;
        IsEditingName = true;
    }

    [RelayCommand]
    public void CommitRename()
    {
        if (!IsEditingName) return;

        var trimmed = EditingName?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && trimmed != Name)
        {
            var coord = _coordinatorProvider?.Invoke();
            if (coord != null)
            {
                coord.ExecuteEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.LayerRename, () =>
                {
                    var result = _layerService.RenameLayer(LayerId, trimmed);
                    if (result.IsSuccess) Name = trimmed;
                });
                _onChangedCallback();
            }
            else
            {
                var result = _layerService.RenameLayer(LayerId, trimmed);
                if (result.IsSuccess)
                {
                    Name = trimmed;
                    _onChangedCallback();
                }
            }
        }

        IsEditingName = false;
    }

    [RelayCommand]
    public void CancelRename()
    {
        IsEditingName = false;
        EditingName = string.Empty;
    }
}
