using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using StockAnalyzer.Core.Models.Watchlist;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the modern Add Ticker dialog.
/// Handles reactive search, validation, and bulk import integration.
/// </summary>
public partial class AddTickerViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly ITickerImportService _tickerImportService;
    private readonly IWatchlistManager _watchlistManager;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<AddTickerViewModel> _logger;
    private readonly Guid _targetProfileId;
    private bool _isDisposed;
    
    private IReadOnlyList<string>? _masterTickers;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
    private ValidationState _state = ValidationState.Idle;

    [ObservableProperty]
    private string? _selectedSuggestion;



    [ObservableProperty]
    private bool _isBulkRequestRequested;

    public bool IsSearching => State == ValidationState.Searching;
    public bool IsValid => State == ValidationState.Valid;
    public bool IsNew => State == ValidationState.NewTicker;
    public bool IsInvalid => State == ValidationState.Invalid;

    /// <summary>
    /// Gets the list of ticker symbols that match the current search text.
    /// </summary>
    public ObservableCollection<string> Suggestions { get; } = new();

    /// <summary>
    /// Gets the final symbol to be added if the dialog is closed with a positive result.
    /// </summary>
    public string? ResultSymbol { get; private set; }

    public AddTickerViewModel(
        IMarketDataProvider marketDataProvider,
        ITickerImportService tickerImportService,
        IWatchlistManager watchlistManager,
        IDispatcherService dispatcherService,
        ILogger<AddTickerViewModel>? logger = null,
        Guid targetProfileId = default)
    {
        _marketDataProvider = marketDataProvider;
        _tickerImportService = tickerImportService;
        _watchlistManager = watchlistManager;
        _dispatcherService = dispatcherService;
        _logger = logger ?? NullLogger<AddTickerViewModel>.Instance;
        _targetProfileId = targetProfileId == default ? Guid.Empty : targetProfileId;
        
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        try
        {
            _masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize master ticker list");
            _masterTickers = Array.Empty<string>();
            State = ValidationState.Invalid;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        
        if (string.IsNullOrWhiteSpace(value))
        {
            State = ValidationState.Idle;
            Suggestions.Clear();
            return;
        }

        var token = _searchCts.Token;
        State = ValidationState.Searching;

        // Offload search to background thread with zero closures or boxing
        Task.Factory.StartNew(static async state =>
        {
            var vm = (AddTickerViewModel)state!;
            var token = vm._searchCts?.Token ?? CancellationToken.None;
            try
            {
                // Debounce: Standard Input Throttle
                await Task.Delay(SettingsConstants.UI_StandardInputThrottleMs, token);
                vm.UpdateSearch(vm.SearchText, token);
            }
            catch (TaskCanceledException)
            {
                // Ignore cancellation
            }
        }, this, token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    private void UpdateSearch(string input, CancellationToken ct)
    {
        if (_masterTickers == null) return;

        string normalized = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            _dispatcherService.Post(static vm =>
            {
                vm.State = ValidationState.Idle;
                vm.Suggestions.Clear();
            }, this);
            return;
        }

        bool exactMatch = false;
        string[] matches = ArrayPool<string>.Shared.Rent(WatchlistConstants.MaxSearchSuggestions);
        int matchCount = 0;

        try
        {
            // Zero-allocation search loop (avoiding LINQ)
            for (int i = 0; i < _masterTickers.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    ArrayPool<string>.Shared.Return(matches, clearArray: true);
                    return;
                }

                string ticker = _masterTickers[i];
                
                // Case-insensitive exact match check
                if (string.Equals(ticker, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatch = true;
                }

                // Case-insensitive prefix match for suggestions
                if (ticker.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (matchCount < WatchlistConstants.MaxSearchSuggestions)
                    {
                        matches[matchCount++] = ticker;
                    }
                }
            }

            // Return results to UI thread
            _dispatcherService.Post(static state =>
            {
                try
                {
                    if (state.ct.IsCancellationRequested) return;

                    state.vm.Suggestions.Clear();
                    for (int i = 0; i < state.matchCount; i++)
                    {
                        state.vm.Suggestions.Add(state.matches[i]);
                    }

                    if (state.exactMatch)
                    {
                        state.vm.State = ValidationState.Valid;
                    }
                    else
                    {
                        // Allow adding as a new ticker if it follows basic symbol rules (1-10 alphanumeric)
                        bool isPotentialSymbol = state.normalized.Length > 0 && state.normalized.Length <= WatchlistConstants.MaxTickerLength;
                        state.vm.State = isPotentialSymbol ? ValidationState.NewTicker : ValidationState.Invalid;
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(state.matches, clearArray: true);
                }
            }, (vm: this, matches, matchCount, exactMatch, normalized, ct));
        }
        catch
        {
            ArrayPool<string>.Shared.Return(matches, clearArray: true);
            throw;
        }
    }

    /// <summary>
    /// Confirms the addition of the selected ticker.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (_isDisposed) return;
        ResultSymbol = SearchText.Trim().ToUpperInvariant();
        // The View will observe a request to close the window
    }

    private bool CanAdd() => State == ValidationState.Valid || State == ValidationState.NewTicker;

    /// <summary>
    /// Signals that a bulk import request has been made.
    /// </summary>
    [RelayCommand]
    private void ImportBulk()
    {
        if (_isDisposed) return;
        IsBulkRequestRequested = true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
