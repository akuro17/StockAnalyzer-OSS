using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel managing the collection of drawing objects in the "Objects" panel tab.
/// Supports in-place diff synchronization, batch commands, selected object operations,
/// move axis mode switching, and Z-index descending projection.
/// </summary>
public partial class DrawingObjectsViewModel : ViewModelBase, IDisposable
{
    private ChartViewModel? _chartViewModel;
    private ChartObjectManager? _directObjectManager;
    private readonly IDispatcherService _dispatcherService;
    private readonly IDialogService? _dialogService;
    private DrawingMoveAxisMode _fallbackMoveAxisMode = DrawingMoveAxisMode.XY;

    public ObservableCollection<DrawingObjectItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public ChartObjectManager? ObjectManager => _directObjectManager ?? _chartViewModel?.ObjectManager;

    public DrawingObjectItemViewModel? SelectedObjectItem => Items.FirstOrDefault(it => it.IsSelected);
    public bool HasSelectedObject => SelectedObjectItem != null;
    public bool CanCopySelected => SelectedObjectItem != null && !SelectedObjectItem.IsLocked;
    public bool CanOpenSettings => SelectedObjectItem != null;
    public bool CanBringForwardSelected => SelectedObjectItem?.CanBringForward == true;
    public bool CanSendBackwardSelected => SelectedObjectItem?.CanSendBackward == true;
    public bool CanDeleteSelected => SelectedObjectItem?.CanDelete == true;
    public bool CanToggleVisibilitySelected => SelectedObjectItem != null;
    public bool CanToggleLockSelected => SelectedObjectItem != null;
    public bool IsSelectedVisible => SelectedObjectItem?.IsVisible ?? false;
    public bool IsSelectedLocked => SelectedObjectItem?.IsLocked ?? false;

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

    public DrawingObjectsViewModel(IDispatcherService dispatcherService, IDialogService? dialogService = null)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _dialogService = dialogService;
    }

    public DrawingObjectsViewModel(ChartViewModel chartViewModel, IDispatcherService dispatcherService, IDialogService? dialogService = null)
        : this(dispatcherService, dialogService)
    {
        SetChartViewModel(chartViewModel);
    }

    public DrawingObjectsViewModel(ChartObjectManager objectManager, IDispatcherService dispatcherService, IDialogService? dialogService = null)
        : this(dispatcherService, dialogService)
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
        }

        _chartViewModel = chartViewModel;

        if (_chartViewModel != null)
        {
            _chartViewModel.ObjectManager.Synced += OnManagerChanged;
            _chartViewModel.PropertyChanged += OnChartViewModelPropertyChanged;
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

    public void SyncFromManager()
    {
        var manager = ObjectManager;
        if (manager == null)
        {
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            NotifySelectedProperties();
            return;
        }

        var models = manager.Objects; // 0..n-1 (ZIndex ascending)

        // 1. Remove deleted items
        var modelIdSet = new HashSet<Guid>(models.Select(m => m.Id));
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!modelIdSet.Contains(Items[i].Id))
            {
                Items.RemoveAt(i);
            }
        }

        // 2. Insert new items and update / reorder existing items
        var itemMap = Items.ToDictionary(it => it.Id);
        // UI list is in descending ZIndex order (top-most first, n-1 down to 0)
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
                var newVM = new DrawingObjectItemViewModel(model, manager, () => NotifySelectedProperties(), m => OpenSettingsForModelAsync(m));
                Items.Insert(targetIndex, newVM);
            }
            targetIndex++;
        }

        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].RefreshState();
        }

        OnPropertyChanged(nameof(HasItems));
        NotifySelectedProperties();
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
        var selected = SelectedObjectItem;
        if (selected == null || ObjectManager == null) return;
        var clone = ObjectManager.DuplicateObject(selected.Id);
        if (clone != null)
        {
            SyncFromManager();
        }
    }

    public async Task OpenSettingsForModelAsync(IChartObject model)
    {
        var dialogService = _dialogService ?? _chartViewModel?.DialogService;
        if (model == null || dialogService == null) return;

        var result = await dialogService.ShowDrawingSettingsDialogAsync(model);
        if (result == DrawingSettingsResult.Deleted)
        {
            ObjectManager?.RemoveObject(model.Id);
            _chartViewModel?.PersistCurrentDrawings();
            SyncFromManager();
        }
        else if (result == DrawingSettingsResult.Changed)
        {
            _chartViewModel?.PersistCurrentDrawings();
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

    [RelayCommand]
    private void LockAll() => ObjectManager?.LockAll();

    [RelayCommand]
    private void UnlockAll() => ObjectManager?.UnlockAll();

    [RelayCommand]
    private void ShowAll() => ObjectManager?.ShowAll();

    [RelayCommand]
    private void HideAll() => ObjectManager?.HideAll();

    [RelayCommand]
    private void DeleteAll() => ObjectManager?.DeleteAll();

    public void Dispose()
    {
        if (_directObjectManager != null)
        {
            _directObjectManager.Synced -= OnManagerChanged;
            _directObjectManager = null;
        }

        if (_chartViewModel != null)
        {
            _chartViewModel.ObjectManager.Synced -= OnManagerChanged;
            _chartViewModel.PropertyChanged -= OnChartViewModelPropertyChanged;
            _chartViewModel = null;
        }
    }
}
