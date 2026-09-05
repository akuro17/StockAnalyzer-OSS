using System;
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

    public bool IsVisible
    {
        get => _manager.IsVisible(Model.Id);
        set
        {
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
        _manager.RenameObject(Model.Id, EditableName);
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
    public bool CanDelete => !_manager.IsLocked(Model.Id);
    public bool CanCopy => !_manager.IsLocked(Model.Id);
    public bool CanBringForward => _manager.CanBringForward(Model.Id);
    public bool CanSendBackward => _manager.CanSendBackward(Model.Id);

    private readonly Func<IChartObject, System.Threading.Tasks.Task>? _onOpenSettings;

    public DrawingObjectItemViewModel(
        IChartObject model,
        ChartObjectManager manager,
        Action? onStateChanged = null,
        Func<IChartObject, System.Threading.Tasks.Task>? onOpenSettings = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _onStateChanged = onStateChanged;
        _onOpenSettings = onOpenSettings;
    }

    public void RefreshState()
    {
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
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
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

    [RelayCommand]
    private void Select()
    {
        IsSelected = !IsSelected;
    }

    [RelayCommand]
    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }

    [RelayCommand]
    private void ToggleLock()
    {
        IsLocked = !IsLocked;
    }

    [RelayCommand]
    private void AdvanceAnchorPoint()
    {
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
        _manager.DuplicateObject(Model.Id);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenSettingsAsync()
    {
        if (_onOpenSettings != null)
        {
            await _onOpenSettings(Model);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        _manager.RemoveObject(Model.Id);
    }

    [RelayCommand(CanExecute = nameof(CanBringForward))]
    private void BringForward()
    {
        _manager.BringForward(Model.Id);
    }

    [RelayCommand(CanExecute = nameof(CanSendBackward))]
    private void SendBackward()
    {
        _manager.SendBackward(Model.Id);
    }
}
