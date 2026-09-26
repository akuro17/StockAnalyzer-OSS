using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

    [ObservableProperty]
    private string _importTags = string.Empty;

    [ObservableProperty]
    private string _selectedListsSummary = string.Empty;

    public ObservableCollection<WatchlistProfileSelectItemViewModel> WatchlistProfiles { get; } = new();
    public ObservableCollection<WatchlistProfileSelectItemViewModel> PortfolioProfiles { get; } = new();

    public AddTickerResult Result
    {
        get
        {
            var selectedIds = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Concat(
                        System.Linq.Enumerable.Where(WatchlistProfiles, p => p.IsSelected),
                        System.Linq.Enumerable.Where(PortfolioProfiles, p => p.IsSelected)),
                    p => p.Id));

            return new AddTickerResult(
                ResultSymbol,
                IsBulkRequestRequested,
                selectedIds,
                ImportTags);
        }
    }

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
        
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
            var profiles = _watchlistManager.GetAllProfiles();
            
            var watchlists = System.Linq.Enumerable.OrderBy(
                System.Linq.Enumerable.Where(profiles, p => !p.IsPortfolio),
                p => p.Name, StringComparer.OrdinalIgnoreCase);
            
            var portfolios = System.Linq.Enumerable.OrderBy(
                System.Linq.Enumerable.Where(profiles, p => p.IsPortfolio),
                p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var profile in watchlists)
            {
                var item = new WatchlistProfileSelectItemViewModel(profile.Id, profile.Name, profile.IsPortfolio, isSelected: false);
                item.PropertyChanged += OnTargetProfilePropertyChanged;
                WatchlistProfiles.Add(item);
            }

            foreach (var profile in portfolios)
            {
                var item = new WatchlistProfileSelectItemViewModel(profile.Id, profile.Name, profile.IsPortfolio, isSelected: false);
                item.PropertyChanged += OnTargetProfilePropertyChanged;
                PortfolioProfiles.Add(item);
            }
            UpdateSelectedListsSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize master ticker list or profile lists");
            _masterTickers = Array.Empty<string>();
            State = ValidationState.Invalid;
        }
    }

    private void OnTargetProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchlistProfileSelectItemViewModel.IsSelected))
        {
            UpdateSelectedListsSummary();
        }
    }

    private void UpdateSelectedListsSummary()
    {
        var selectedNames = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Concat(
                    System.Linq.Enumerable.Where(WatchlistProfiles, p => p.IsSelected),
                    System.Linq.Enumerable.Where(PortfolioProfiles, p => p.IsSelected)),
                p => p.Name));

        if (selectedNames.Count == 0)
        {
            var localized = StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["AddTicker_Unspecified"];
            SelectedListsSummary = (string.IsNullOrEmpty(localized) || localized.StartsWith("[")) ? "Unspecified" : localized;
        }
        else
        {
            SelectedListsSummary = string.Join(", ", selectedNames);
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

        Task.Run(async () =>
        {
            try
            {
                // Debounce: Standard Input Throttle
                await Task.Delay(SettingsConstants.UI_StandardInputThrottleMs, token);
                UpdateSearch(value, token);
            }
            catch (TaskCanceledException)
            {
                // Ignore cancellation
            }
        }, token);
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
        ResultSymbol = StockAnalyzer.Core.Helpers.TickerHelper.NormalizeTicker(SearchText);
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

        foreach (var item in WatchlistProfiles)
        {
            item.PropertyChanged -= OnTargetProfilePropertyChanged;
        }
        foreach (var item in PortfolioProfiles)
        {
            item.PropertyChanged -= OnTargetProfilePropertyChanged;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
