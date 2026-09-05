using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// Container ViewModel managing multiple synchronization tasks.
/// </summary>
public partial class MultiSyncProgressViewModel : ViewModelBase
{
    private readonly TickerListViewModel _tickerListViewModel;

    public MultiSyncProgressViewModel(TickerListViewModel tickerListViewModel)
    {
        _tickerListViewModel = tickerListViewModel;
        _isMetadataSyncEnabled = _tickerListViewModel.IsMetadataSyncEnabled;
        _isImputeMissingMetadataEnabled = _tickerListViewModel.IsImputeMissingMetadataEnabled;
        _isTimeSeriesSyncEnabled = _tickerListViewModel.IsTimeSeriesSyncEnabled;
        _isAutoSyncEnabled = _tickerListViewModel.IsAutoSyncEnabled;
        _isFullHistoryEnabled = _tickerListViewModel.IsFullHistoryEnabled;
        _isForcePeriodDownloadEnabled = _tickerListViewModel.IsForcePeriodDownloadEnabled;
        _delayMinSeconds = _tickerListViewModel.SyncDelayMinSeconds;
        _delayMaxSeconds = _tickerListViewModel.SyncDelayMaxSeconds;
        _startSyncPeriodYears = _tickerListViewModel.StartSyncPeriodYears;

        Items = new ObservableCollection<SyncItemViewModel>();
        Items.CollectionChanged += (s, e) => UpdateSummary();
        
        StopAllCommand = new RelayCommand(OnStopAll);
        StartAllCommand = new AsyncRelayCommand(OnStartAll);
        CloseCommand = new RelayCommand(OnClose);

        UpdateSummary();
    }

    /// <summary>
    /// Collection of individual sync items.
    /// </summary>
    public ObservableCollection<SyncItemViewModel> Items { get; }

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _syncingCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    private bool _isMetadataSyncEnabled = true;
    public bool IsMetadataSyncEnabled
    {
        get => _isMetadataSyncEnabled;
        set
        {
            if (SetProperty(ref _isMetadataSyncEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsMetadataSyncEnabled = value;
                }
            }
        }
    }

    private bool _isImputeMissingMetadataEnabled = false;
    public bool IsImputeMissingMetadataEnabled
    {
        get => _isImputeMissingMetadataEnabled;
        set
        {
            if (SetProperty(ref _isImputeMissingMetadataEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsImputeMissingMetadataEnabled = value;
                }
            }
        }
    }

    private bool _isTimeSeriesSyncEnabled = true;
    public bool IsTimeSeriesSyncEnabled
    {
        get => _isTimeSeriesSyncEnabled;
        set
        {
            if (SetProperty(ref _isTimeSeriesSyncEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsTimeSeriesSyncEnabled = value;
                }
            }
        }
    }

    private bool _isAutoSyncEnabled = true;
    public bool IsAutoSyncEnabled
    {
        get => _isAutoSyncEnabled;
        set
        {
            if (SetProperty(ref _isAutoSyncEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsAutoSyncEnabled = value;
                }
            }
        }
    }

    private bool _isFullHistoryEnabled = false;
    public bool IsFullHistoryEnabled
    {
        get => _isFullHistoryEnabled;
        set
        {
            if (SetProperty(ref _isFullHistoryEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsFullHistoryEnabled = value;
                }
            }
        }
    }

    private bool _isForcePeriodDownloadEnabled = false;
    public bool IsForcePeriodDownloadEnabled
    {
        get => _isForcePeriodDownloadEnabled;
        set
        {
            if (SetProperty(ref _isForcePeriodDownloadEnabled, value))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.IsForcePeriodDownloadEnabled = value;
                }
            }
        }
    }

    private decimal _delayMinSeconds = 3.0m;
    public decimal DelayMinSeconds
    {
        get => _delayMinSeconds;
        set
        {
            var clamped = Math.Clamp(value, 3.0m, 60.0m);
            clamped = Math.Round(clamped, 1);
            if (clamped > _delayMaxSeconds)
            {
                clamped = _delayMaxSeconds;
            }
            if (SetProperty(ref _delayMinSeconds, clamped))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.SyncDelayMinSeconds = clamped;
                }
            }
        }
    }

    private decimal _delayMaxSeconds = 5.0m;
    public decimal DelayMaxSeconds
    {
        get => _delayMaxSeconds;
        set
        {
            var clamped = Math.Clamp(value, 3.0m, 60.0m);
            clamped = Math.Round(clamped, 1);
            if (clamped < _delayMinSeconds)
            {
                clamped = _delayMinSeconds;
            }
            if (SetProperty(ref _delayMaxSeconds, clamped))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.SyncDelayMaxSeconds = clamped;
                }
            }
        }
    }

    private int _startSyncPeriodYears = 5;
    public int StartSyncPeriodYears
    {
        get => _startSyncPeriodYears;
        set
        {
            var clamped = Math.Clamp(value, 1, 50);
            if (SetProperty(ref _startSyncPeriodYears, clamped))
            {
                if (_tickerListViewModel != null)
                {
                    _tickerListViewModel.StartSyncPeriodYears = clamped;
                }
            }
        }
    }



    /// <summary>
    /// Command to stop all ongoing synchronization tasks.
    /// </summary>
    public IRelayCommand StopAllCommand { get; }

    /// <summary>
    /// Command to start/restart all synchronization tasks.
    /// </summary>
    public IAsyncRelayCommand StartAllCommand { get; }

    /// <summary>
    /// Command to close the progress window.
    /// </summary>
    public IRelayCommand CloseCommand { get; }



    /// <summary>
    /// Event triggered when start all is requested.
    /// </summary>
    public event Action? RequestStartAll;

    /// <summary>
    /// Event triggered when stop all is requested.
    /// </summary>
    public event Action? RequestStopAll;

    /// <summary>
    /// Event triggered when window close is requested.
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Adds a new sync item to the management collection.
    /// </summary>
    public void AddItem(SyncItemViewModel item)
    {
        item.PropertyChanged += Item_PropertyChanged;
        Items.Add(item);
        UpdateSummary();
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SyncItemViewModel.Status))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        CompletedCount = Items.Count(x => x.Status == SyncStatus.Completed);
        SyncingCount = Items.Count(x => x.Status == SyncStatus.Syncing);
        ErrorCount = Items.Count(x => x.Status == SyncStatus.Error);
        
        var manager = LocalizationManager.Instance;

        if (SyncingCount > 0)
        {
            StatusMessage = manager.Get("SyncWindow_Status_Syncing");
        }
        else if (ErrorCount > 0 && SyncingCount == 0)
        {
            StatusMessage = manager.Get("SyncWindow_Status_Error");
        }
        else if (CompletedCount == Items.Count && Items.Count > 0)
        {
            StatusMessage = manager.Get("SyncWindow_Status_Complete");
        }
        else
        {
            StatusMessage = manager.Get("SyncWindow_Status_Waiting");
        }
    }



    private void OnStopAll()
    {
        RequestStopAll?.Invoke();
    }

    private void OnClose()
    {
        OnStopAll();
        RequestClose?.Invoke();
    }

    private async Task OnStartAll()
    {
        RequestStartAll?.Invoke();
        await Task.CompletedTask;
    }
}
