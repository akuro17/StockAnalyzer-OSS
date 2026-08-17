using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

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
    public string DisplayName => LocalizationManager.Instance[$"DrawTool_{Model.Type}"] ?? Model.Type.ToString();
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
