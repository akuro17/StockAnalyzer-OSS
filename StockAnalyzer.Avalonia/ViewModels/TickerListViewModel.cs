using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Ticker List panel.
/// </summary>
public partial class TickerListViewModel : ViewModelBase, IRecipient<TickerSelectedMessage>, IDisposable, ITickerStateStore
{
    private readonly IWatchlistManager _watchlistManager;
    private readonly IPortfolioManager _portfolioManager;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IPythonService _pythonService;
    private readonly IMessenger _messenger;
    private readonly IDispatcherService _dispatcherService;
    private readonly IDialogService _dialogService;
    private readonly ITickerImportService _tickerImportService;
    private readonly ILogger<TickerListViewModel> _logger;
    private bool _isInternalChange; // Guard against selection nullification during collection updates
    private readonly List<string> _allTickers = new();
    private bool _isSyncingSelection;
    private bool _isInitializing;
    private bool _isDisposed;

    private CancellationTokenSource? _hydrationCts;
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _statusCts;
    private CancellationTokenSource? _importCts;
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private bool _isLoaded;
    private List<string>? _pendingTickers;
    private readonly Action _refreshNodesDelegate;
    private readonly Dictionary<string, WatchlistItemViewModel> _viewModelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TickerGroupNode> _syncBuffer = new();
    private readonly List<string> _tickerSortBuffer = new();
    private readonly WatchlistFilterEngine _filterEngine = new();

    
    // Fixed IDs for categories (Deterministic FSM)
    public static readonly Guid AllTickersId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid WatchlistsCategoryId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid PortfoliosCategoryId = new("00000000-0000-0000-0000-000000000003");
    
    public BulkObservableCollection<string> AvailableTickers { get; } = new();
    
    public ObservableCollection<TickerGroupNode> Groups { get; } = new();

    IEnumerable<TickerGroupNode> ITickerStateStore.Groups => Groups;
    IEnumerable<IFilterableSymbol> ITickerStateStore.DisplayItems => DisplayItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWatchlist))]
    [NotifyPropertyChangedFor(nameof(IsAllTickersSelected))]
    private TickerGroupNode? _selectedNode;

    // Compatibility property for layout saving
    public WatchlistProfile? SelectedWatchlist => (SelectedNode as WatchlistNode)?.Profile ?? (SelectedNode as PortfolioNode)?.Profile;

    public bool IsAllTickersSelected => SelectedNode?.Id == AllTickersId || SelectedNode is AllTickersNode;

    [ObservableProperty]
    private double _sidebarGridWidth = 200.0;

    [ObservableProperty]
    private string? _selectedTicker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectAll))]
    private string? _statusMessage = "";

    [ObservableProperty] private bool _isHydrating;
    [ObservableProperty] private bool _isSyncing;

    [ObservableProperty]
    private WatchlistItemViewModel? _selectedItem;

    partial void OnSelectedItemChanged(WatchlistItemViewModel? value)
    {
        if (value != null)
        {
            SelectedTicker = value.Symbol;
        }
    }

    [ObservableProperty]
    private string? _sortMember;

    [ObservableProperty]
    private int _sortDirection;

    [ObservableProperty]
    private bool _isMetadataSyncEnabled = true;

    partial void OnIsMetadataSyncEnabledChanged(bool value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private bool _isTimeSeriesSyncEnabled = true;

    partial void OnIsTimeSeriesSyncEnabledChanged(bool value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private bool _isAutoSyncEnabled = true;

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private bool _isFullHistoryEnabled = false;

    partial void OnIsFullHistoryEnabledChanged(bool value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private decimal _syncDelayMinSeconds = 3.0m;

    partial void OnSyncDelayMinSecondsChanged(decimal value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private decimal _syncDelayMaxSeconds = 5.0m;

    partial void OnSyncDelayMaxSecondsChanged(decimal value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    [ObservableProperty]
    private int _startSyncPeriodYears = 5;

    partial void OnStartSyncPeriodYearsChanged(int value)
    {
        if (_isLoaded)
        {
            _messenger.Send(new LayoutChangedMessage());
        }
    }

    public bool? SelectAll
    {
        get
        {
            if (DisplayItems.Count == 0) return false;
            bool allChecked = DisplayItems.All(x => x.IsChecked);
            bool anyChecked = DisplayItems.Any(x => x.IsChecked);
            return allChecked ? true : anyChecked ? null : false;
        }
        set
        {
            if (!value.HasValue) return;

            bool targetValue = value.Value;
            foreach (var item in DisplayItems) item.IsChecked = targetValue;
            OnPropertyChanged(nameof(SelectAll));
        }
    }

    public IEnumerable<WatchlistProfile> OtherWatchlists => 
        (_watchlistManager.GetAllProfiles() ?? Enumerable.Empty<WatchlistProfile>())
            .Where(p => !p.IsPortfolio && p.Id != (SelectedWatchlist?.Id ?? Guid.Empty));

    public IEnumerable<WatchlistProfile> AllPortfolios => 
        (_watchlistManager.GetAllProfiles() ?? Enumerable.Empty<WatchlistProfile>())
            .Where(p => p.IsPortfolio);

    public IEnumerable<TickerGroupNode> PortfolioNodes => 
        Groups.FirstOrDefault(n => n.Id == PortfoliosCategoryId)?.Children ?? Enumerable.Empty<TickerGroupNode>();

    public BulkObservableCollection<WatchlistItemViewModel> DisplayItems { get; } = new();

    public ObservableCollection<WatchlistColumnMetadata> ActiveColumns { get; } = new();

    public ObservableCollection<string> ExistingTags { get; } = new();

    private readonly IReadOnlyList<WatchlistColumnMetadata> _allColumns = WatchlistColumnRegistry.AllColumns;

    private void InitializeColumns()
    {
        // Source initialization is now handled by the View's Attached Property (TreeDataGridExtensions.Items)
        UpdateColumns();
    }

    public (string? ColumnName, int SortDirection) GetSortState()
    {
        return (SortMember, SortDirection);
    }

    public void ApplySortState(string colName, int direction)
    {
        SortMember = colName;
        SortDirection = direction;
    }

    public void SetActiveColumns(IEnumerable<string> columnNames)
    {
        var names = columnNames.ToList();
        if (names.Count == 0) return;

        ActiveColumns.Clear();
        
        // Ensure Symbol is present
        if (!names.Contains("Symbol", StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, "Symbol");
        }

        foreach (var name in names)
        {
            var col = _allColumns.FirstOrDefault(c => c.MemberName == name);
            if (col != null) ActiveColumns.Add(col);
        }
    }

    [RelayCommand]
    private async Task ShowColumnChooser()
    {
        var active = ActiveColumns.Select(c => c.MemberName).ToList();
        var result = await _dialogService.ShowColumnChooserDialogAsync(_allColumns, active);
        if (result != null)
        {
            SetActiveColumns(result);
        }
    }

    private void UpdateColumns()
    {
        ActiveColumns.Clear();
        foreach (var col in _allColumns)
        {
            // By default show Symbol, Name, Close, ChangePercent
            bool isActive = col.MemberName == "IsChecked" || col.MemberName == "Symbol" || col.MemberName == "Name" || col.MemberName == "Close" || col.MemberName == "ChangePercent";
            if (isActive) ActiveColumns.Add(col);
        }
    }

    public TickerListViewModel(
        IMarketDataProvider marketDataProvider, 
        IPythonService pythonService,
        IMessenger messenger, 
        IDispatcherService dispatcherService, 
        IWatchlistManager watchlistManager, 
        IPortfolioManager portfolioManager,
        IDialogService dialogService,
        ITickerImportService tickerImportService,
        ILogger<TickerListViewModel>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerListViewModel>.Instance;

        try 
        {
            _marketDataProvider = marketDataProvider;
            _pythonService = pythonService;
            _messenger = messenger;
            _dispatcherService = dispatcherService;
            _watchlistManager = watchlistManager;
            _portfolioManager = portfolioManager;
            _dialogService = dialogService;
            _tickerImportService = tickerImportService;
            
            _refreshNodesDelegate = RefreshNodes;
            
            _logger.LogInformation("TickerListViewModel startup initiated.");

            InitializeColumns();

            
            _logger.LogInformation("Columns initialized.");

            _messenger.Register<TickerSelectedMessage>(this);
            _messenger.Register<TickerListViewModel, ColumnChooserAppliedMessage>(this, (r, m) => r.SetActiveColumns(m.ActiveColumns));
            _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;

            RefreshNodes();

            _ = InitializeTickersAsync();
            
            SelectedNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
            
            _logger.LogInformation("TickerListViewModel initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRASH in TickerListViewModel constructor");
            throw;
        }
    }

    [RelayCommand]
    private async Task DeleteFromMasterList()
    {
        var items = DisplayItems.Where(x => x.IsChecked).ToList();
        
        // Use selected item as fallback if no checkboxes are active
        if (items.Count == 0 && SelectedItem != null)
        {
            items = new List<WatchlistItemViewModel> { SelectedItem };
        }

        if (items.Count == 0) return;

        var title = LocalizationManager.Instance["TickerList_DeleteFromMasterConfirm_Title"];
        var symbolText = items.Count == 1 ? items[0].Symbol : $"{items.Count} tickers";
        var message = string.Format(LocalizationManager.Instance["TickerList_DeleteFromMasterConfirm_Message"], symbolText);

        if (await _dialogService.ShowConfirmationAsync(title, message))
        {
            var symbols = items.Select(x => x.Symbol).ToList();
            await _marketDataProvider.RemoveTickersAsync(symbols);
            
            // Cascade deletion to all watchlists/portfolios
            await Task.Run(() => _watchlistManager.RemoveTickersFromAllProfiles(symbols));
            
            await InitializeTickersAsync();
            SelectAll = false;
            OnPropertyChanged(nameof(SelectAll));
        }
    }

    [RelayCommand]
    private async Task BulkRemove()
    {
        var items = DisplayItems.Where(x => x.IsChecked).ToList();
        
        // Use selected item as fallback if no checkboxes are active
        if (items.Count == 0 && SelectedItem != null)
        {
            items = new List<WatchlistItemViewModel> { SelectedItem };
        }

        if (items.Count == 0) return;

        // Master List removal logic
        if (SelectedNode?.Id == AllTickersId)
        {
            await DeleteFromMasterList();
            return;
        }


        if (SelectedWatchlist == null) return;

        var profileId = SelectedWatchlist.Id;

        await Task.Run(() => 
        {
            _watchlistManager.RemoveTickersFromProfile(profileId, items.Select(x => x.Symbol));
        });

        DisplayItems.BeginBulkUpdate();
        try
        {
            foreach (var item in items)
            {
                DisplayItems.Remove(item);
            }
        }
        finally
        {
            DisplayItems.EndBulkUpdate();
            SelectAll = false;
            OnPropertyChanged(nameof(SelectAll));
        }
    }

    [RelayCommand]
    private async Task BulkImport()
    {
        await BulkImportAsync(null, null);
    }

    private async Task BulkImportAsync(IReadOnlyList<Guid>? targetProfileIds = null, string? importTags = null)
    {
        if (IsSyncing) return;

        var title = LocalizationManager.Instance["TickerList_BulkImport_Title"];
        var filter = new[] { "txt", "csv" };
        var filePath = await _dialogService.ShowOpenFileDialogAsync(title, filter);
        
        if (string.IsNullOrEmpty(filePath)) return;

        StatusMessage = LocalizationManager.Instance["TickerList_Importing"];
        IsSyncing = true; 

        try
        {
            _logger.LogInformation("Starting bulk import from {Path}", filePath);
            
            _importCts?.Cancel();
            _importCts?.Dispose();
            _importCts = new CancellationTokenSource();

            IReadOnlyList<string> tickers;
            using (var stream = File.OpenRead(filePath))
            {
                tickers = await _tickerImportService.ImportTickersAsync(stream, _importCts.Token);
            }
            
            if (tickers.Count == 0)
            {
                 _logger.LogWarning("No tickers found in {Path}", filePath);
                 await _dialogService.ShowAlertAsync(title, LocalizationManager.Instance["TickerList_NoTickersFound"]);
                 return;
            }

            _logger.LogDebug("Extracted {Count} tickers. Adding to registry...", tickers.Count);

            // 1. Add to master registry
            await _marketDataProvider.AddTickersAsync(tickers);
            
            // 2. Add to target profiles (multi-selection) or fallback to active profile
            Guid? syncTargetId = null;
            if (targetProfileIds != null && targetProfileIds.Count > 0)
            {
                var validTarget = targetProfileIds.FirstOrDefault(id => id != AllTickersId);
                syncTargetId = validTarget != Guid.Empty ? validTarget : null;
                await Task.Run(() => 
                {
                    foreach (var profileId in targetProfileIds)
                    {
                        if (profileId != AllTickersId)
                        {
                            _logger.LogDebug("Adding {Count} tickers to profile {ProfileId}", tickers.Count, profileId);
                            _watchlistManager.AddTickersToProfile(profileId, tickers);
                        }
                    }
                });
            }
            else
            {
                var currentProfile = SelectedWatchlist;
                if (currentProfile != null && currentProfile.Id != AllTickersId)
                {
                    syncTargetId = currentProfile.Id;
                    _logger.LogDebug("Adding {Count} tickers to active profile {ProfileId}", tickers.Count, currentProfile.Id);
                    await Task.Run(() => 
                    {
                        _watchlistManager.AddTickersToProfile(currentProfile.Id, tickers);
                    });
                }
            }

            // 3. Bulk apply tag attribute if specified
            if (!string.IsNullOrWhiteSpace(importTags))
            {
                var newTags = importTags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                if (newTags.Count > 0)
                {
                    _logger.LogDebug("Bulk applying tags ({Tags}) to {Count} tickers", string.Join(", ", newTags), tickers.Count);
                    foreach (var ticker in tickers)
                    {
                        try
                        {
                            var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                            var existingList = new List<string>();
                            if (!string.IsNullOrEmpty(meta.Tag))
                            {
                                existingList.AddRange(meta.Tag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
                            }

                            bool modified = false;
                            foreach (var tag in newTags)
                            {
                                if (!existingList.Contains(tag, StringComparer.OrdinalIgnoreCase))
                                {
                                    existingList.Add(tag);
                                    modified = true;
                                }
                            }

                            if (modified)
                            {
                                var updatedMeta = meta with { Tag = string.Join(", ", existingList) };
                                await _marketDataProvider.SaveMetadataAsync(ticker, updatedMeta);
                                if (_viewModelCache.TryGetValue(ticker, out var vmItem))
                                {
                                    vmItem.Tag = updatedMeta.Tag;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to apply bulk tag to ticker {Symbol}", ticker);
                        }
                    }
                    _ = RefreshExistingTagsAsync();
                }
            }

            // Sync metadata to _viewModelCache for all imported tickers
            foreach (var ticker in tickers)
            {
                var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                if (_viewModelCache.TryGetValue(ticker, out var vmItem))
                {
                    ApplyMetadataToItem(vmItem, meta);
                }
            }

            var successMsg = LocalizationManager.Instance["TickerList_ImportSuccess"];
            StatusMessage = string.Format(successMsg, tickers.Count);
            
            // 4. Reload tickers, refresh sidebar nodes, select target profile and refresh grid
            RefreshNodes();
            if (syncTargetId.HasValue)
            {
                SelectNodeById(syncTargetId.Value);
            }
            await InitializeTickersAsync();
            ApplyFilter();
            
            // 5. Auto-trigger sync for the new tickers to fetch data/metadata (non-blocking)
            _logger.LogDebug("Triggering background sync for {Count} imported tickers", tickers.Count);
            _syncCts?.Cancel();
            _syncCts?.Dispose();
            _syncCts = new CancellationTokenSource();
            await RunSyncSessionAsync(tickers, _syncCts.Token, rollbackOnFailure: false, targetProfileId: syncTargetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Bulk import failed from {Path}", filePath);
            await _dialogService.ShowAlertAsync(LocalizationManager.Instance["Dialog_Error"] ?? "Error", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            // Clear status message after a short delay (cancelable)
            _statusCts?.Cancel();
            _statusCts?.Dispose();
            _statusCts = new CancellationTokenSource();
            var ct = _statusCts.Token;
            _ = Task.Delay(SettingsConstants.UI_StatusMessageDurationMs, ct).ContinueWith(t => 
            {
                if (!t.IsCanceled)
                {
                    _dispatcherService.Post(static vm => vm.StatusMessage = "", this);
                }
            }, ct);
        }
    }

    [RelayCommand]
    private async Task AddToWatchlist(WatchlistProfile? targetProfile)
    {
        if (targetProfile == null) return;
        var items = DisplayItems.Where(x => x.IsChecked).ToList();

        // Use selected item as fallback if no checkboxes are active
        if (items.Count == 0 && SelectedItem != null)
        {
            items = new List<WatchlistItemViewModel> { SelectedItem };
        }

        if (items.Count == 0) return;

        var profileId = targetProfile.Id;
        await Task.Run(() => 
        {
            _watchlistManager.AddTickersToProfile(profileId, items.Select(x => x.Symbol));
        });

        // Refresh grid if the added watchlist is the currently selected one
        if (SelectedWatchlist?.Id == targetProfile.Id)
        {
            ApplyFilter();
        }
    }

    [RelayCommand]
    private async Task AddToPortfolio(WatchlistProfile? targetProfile)
    {
        if (targetProfile == null) return;
        var items = DisplayItems.Where(x => x.IsChecked).ToList();

        if (items.Count == 0 && SelectedItem != null)
        {
            items = new List<WatchlistItemViewModel> { SelectedItem };
        }

        if (items.Count == 0) return;

        var profileId = targetProfile.Id;
        await Task.Run(() => 
        {
            _watchlistManager.AddTickersToProfile(profileId, items.Select(x => x.Symbol));
        });

        if (SelectedNode is PortfolioNode pNode && pNode.Profile.Id == targetProfile.Id)
        {
            ApplyFilter();
        }
    }

    [RelayCommand]
    private async Task SyncSelectedTickers()
    {
        var selectedSymbols = DisplayItems
            .Where(x => x.IsChecked)
            .Select(x => x.Symbol)
            .ToList();

        // Fallback to currently selected item if no checkboxes are checked
        if (selectedSymbols.Count == 0 && SelectedItem != null)
        {
            selectedSymbols = new List<string> { SelectedItem.Symbol };
        }

        if (selectedSymbols.Count == 0) return;

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        await RunSyncSessionAsync(selectedSymbols, _syncCts.Token);
    }

    [RelayCommand]
    private async Task SyncGroup(TickerGroupNode? node)
    {
        if (node == null) return;

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSymbolsRecursively(node, symbols, 0);

        if (symbols.Count == 0)
        {
            _logger.LogInformation("No symbols found in group {NodeName} for sync.", node.DisplayName);
            return;
        }

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        await RunSyncSessionAsync(symbols, _syncCts.Token);
    }

    [RelayCommand]
    private async Task AddTickerToMaster()
    {
        var targetProfileId = SelectedWatchlist?.Id ?? AllTickersId;
        
        var result = await _dialogService.ShowAddTickerDialogAsync(targetProfileId);
        
        if (result.RequestBulkImport)
        {
            await BulkImportAsync(result.TargetProfileIds, result.ImportTags);
            return;
        }

        var symbol = result.Symbol;
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.Trim().ToUpperInvariant();

        try
        {
            _logger.LogInformation("Adding ticker {Symbol} to master list and profile {ProfileId}", symbol, targetProfileId);
            
            // 1. Add to master registry
            await _marketDataProvider.AddTickerAsync(symbol);
            
            // 2. Add to target profiles (multi-selection) or fallback to active profile
            Guid? syncTargetId = null;
            if (result.TargetProfileIds != null && result.TargetProfileIds.Count > 0)
            {
                var validTarget = result.TargetProfileIds.FirstOrDefault(id => id != AllTickersId);
                syncTargetId = validTarget != Guid.Empty ? validTarget : (targetProfileId != AllTickersId ? targetProfileId : null);
                await Task.Run(() => 
                {
                    foreach (var profileId in result.TargetProfileIds)
                    {
                        if (profileId != AllTickersId)
                        {
                            _logger.LogDebug("Adding ticker {Symbol} to profile {ProfileId}", symbol, profileId);
                            _watchlistManager.AddTickersToProfile(profileId, new[] { symbol });
                        }
                    }
                });
            }
            else if (targetProfileId != AllTickersId)
            {
                syncTargetId = targetProfileId;
                await Task.Run(() => 
                {
                    _watchlistManager.AddTickersToProfile(targetProfileId, new[] { symbol });
                });
            }

            // 3. Apply tag attribute if specified
            if (!string.IsNullOrWhiteSpace(result.ImportTags))
            {
                var newTags = result.ImportTags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                if (newTags.Count > 0)
                {
                    var meta = await _marketDataProvider.GetMetadataAsync(symbol);
                    var existingList = new List<string>();
                    if (!string.IsNullOrEmpty(meta.Tag))
                    {
                        existingList.AddRange(meta.Tag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
                    }

                    bool modified = false;
                    foreach (var tag in newTags)
                    {
                        if (!existingList.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            existingList.Add(tag);
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        var updatedMeta = meta with { Tag = string.Join(", ", existingList) };
                        await _marketDataProvider.SaveMetadataAsync(symbol, updatedMeta);
                        if (_viewModelCache.TryGetValue(symbol, out var vmItem))
                        {
                            vmItem.Tag = updatedMeta.Tag;
                        }
                    }
                }
            }

            // Sync metadata to _viewModelCache
            var freshMeta = await _marketDataProvider.GetMetadataAsync(symbol);
            if (!_viewModelCache.TryGetValue(symbol, out var cachedVm))
            {
                cachedVm = new WatchlistItemViewModel(symbol, symbol, "", "", 0, 0, 0, 0, 0, 0);
                cachedVm.PropertyChanged += OnItemPropertyChanged;
                _viewModelCache[symbol] = cachedVm;
            }
            ApplyMetadataToItem(cachedVm, freshMeta);

            // 4. Reload tickers, refresh sidebar nodes, select target profile and refresh grid
            RefreshNodes();
            if (syncTargetId.HasValue && syncTargetId.Value != AllTickersId)
            {
                SelectNodeById(syncTargetId.Value);
            }
            await InitializeTickersAsync();
            ApplyFilter();

            // 5. Auto-trigger sync for the new ticker to fetch data/metadata (non-blocking)
            _syncCts?.Cancel();
            _syncCts?.Dispose();
            _syncCts = new CancellationTokenSource();
            _ = RunSyncSessionAsync(new[] { symbol }, _syncCts.Token, rollbackOnFailure: false, targetProfileId: syncTargetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add ticker {Symbol} to master list", symbol);
            await _dialogService.ShowAlertAsync(LocalizationManager.Instance["Dialog_Error"] ?? "Error", ex.Message);
        }
    }

    private void CollectSymbolsRecursively(TickerGroupNode node, HashSet<string> symbols, int depth)
    {
        if (depth > 5) return; // Strict depth limit

        if (node is AllTickersNode)
        {
            foreach (var symbol in _allTickers) symbols.Add(symbol);
        }
        else if (node is WatchlistNode watchlistNode)
        {
            foreach (var item in watchlistNode.Profile.Items) symbols.Add(item.Ticker);
        }
        else if (node is PortfolioNode portfolioNode)
        {
            foreach (var item in portfolioNode.Profile.Items) symbols.Add(item.Ticker);
        }

        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                CollectSymbolsRecursively(child, symbols, depth + 1);
            }
        }
    }

    private sealed class SyncProgressReporter : IProgress<int>
    {
        private readonly SyncItemModel _model;
        private readonly SyncItemViewModel _viewModel;
        private readonly IDispatcherService _dispatcherService;

        public SyncProgressReporter(SyncItemModel model, SyncItemViewModel viewModel, IDispatcherService dispatcherService)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        }

        public void Report(int value)
        {
            _model.UpdateProgress(value, $"Syncing {value}%");
            _dispatcherService.Post(static i => i.UpdateFromModel(), _viewModel);
        }
    }

    private Task RunSyncSessionAsync(IEnumerable<string> symbols, CancellationToken ct, bool rollbackOnFailure = false, Guid? targetProfileId = null)
    {
        var syncSession = _dialogService.CreateMultiSyncProgressSession();
        var vm = syncSession.ViewModel;

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        bool isLoopRunning = false;
        var loopLock = new object();

        Action stopHandler = () => { try { sessionCts.Cancel(); } catch { } };
        Action closeHandler = () => { try { sessionCts.Cancel(); } catch { } };
        Action startHandler = () => 
        {
            lock (loopLock)
            {
                if (isLoopRunning) return;
                isLoopRunning = true;
            }

            if (sessionCts.IsCancellationRequested)
            {
                sessionCts.Dispose();
                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            // Reset non-complete items back to Waiting
            foreach (var itemVm in vm.Items)
            {
                if (itemVm.Status != SyncStatus.Completed)
                {
                    itemVm.MarkWaiting();
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var itemVm in vm.Items)
                    {
                        if (sessionCts.Token.IsCancellationRequested) break;
                        if (itemVm.Status == SyncStatus.Completed) continue;

                        await _dispatcherService.PostAsync(
                            static state => state.cmd.ExecuteAsync(state.token),
                            (cmd: itemVm.RetryCommand, token: sessionCts.Token)
                        );

                        // Intra-ticker delay
                        var delayMin = vm.DelayMinSeconds;
                        var delayMax = vm.DelayMaxSeconds;
                        if (delayMax >= 1.0m && itemVm != vm.Items.Last() && !sessionCts.Token.IsCancellationRequested)
                        {
                            var minMs = (double)delayMin * 1000.0;
                            var maxMs = (double)delayMax * 1000.0;
                            double delayMs = Random.Shared.NextDouble() * (maxMs - minMs) + minMs;
                            await Task.Delay((int)delayMs, sessionCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync session loop error");
                }
                finally
                {
                    lock (loopLock)
                    {
                        isLoopRunning = false;
                    }
                }
            }, sessionCts.Token);
        };

        vm.RequestStopAll += stopHandler;
        vm.RequestClose += closeHandler;
        vm.RequestStartAll += startHandler;

        foreach (var symbol in symbols)
        {
            if (sessionCts.Token.IsCancellationRequested) break;
            var model = new SyncItemModel(symbol, "D1");
            var itemVm = new SyncItemViewModel(model);
            
            // Reusable progress reporter allocated once per ticker item
            var progress = new SyncProgressReporter(model, itemVm, _dispatcherService);
            itemVm.ProgressReporter = progress;
            
            itemVm.RequestRetry += async (ivm, retryCt) => 
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token, retryCt);
  
                 try 
                 {
                     model.MarkSyncing();
                     _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);
                     
                     var currentConfig = new SyncSessionConfig(
                         IsTimeSeriesSyncEnabled: vm.IsTimeSeriesSyncEnabled,
                         IsMetadataSyncEnabled: vm.IsMetadataSyncEnabled,
                         IsAutoSyncEnabled: vm.IsAutoSyncEnabled,
                         IsFullHistoryEnabled: vm.IsFullHistoryEnabled,
                         DelayMinSeconds: vm.DelayMinSeconds,
                         DelayMaxSeconds: vm.DelayMaxSeconds,
                         StartSyncPeriodYears: vm.StartSyncPeriodYears
                     );
                     await _pythonService.RunUpdatePipelineAsync(symbol, currentConfig, ivm.ProgressReporter, linkedCts.Token);
                     
                     // Invalidate cache and reload fresh metadata and candles from disk
                     _marketDataProvider.InvalidateMetadataCache(symbol);
                     var freshMeta = await _marketDataProvider.GetMetadataAsync(symbol);
                     IReadOnlyList<Core.Models.CandleData>? freshCandles = null;
                     try
                     {
                         freshCandles = await _marketDataProvider.GetTickersDataAsync(symbol, Core.Models.TimeFrame.D1);
                     }
                     catch (Exception ex)
                     {
                         _logger.LogWarning(ex, "Failed to load fresh candle data for {Symbol} post-sync", symbol);
                     }

                     DateTimeOffset? lastUpdated = null;
                     try
                     {
                         lastUpdated = await _marketDataProvider.GetTimeSeriesLastUpdatedAsync(symbol);
                     }
                     catch (Exception ex)
                     {
                         _logger.LogWarning(ex, "Failed to load time series last updated timestamp for {Symbol} post-sync", symbol);
                     }

                     _dispatcherService.Post(static state =>
                     {
                         state.vm.ApplyMetadataToItem(state.symbol, state.freshMeta);
                         if (state.freshCandles != null && state.freshCandles.Count > 0)
                         {
                             state.vm.ApplyTimeSeriesDataToItem(state.symbol, state.freshCandles);
                             var item = state.vm.DisplayItems.FirstOrDefault(i => i.Symbol == state.symbol);
                             if (item != null)
                             {
                                 item.LastUpdatedUtc = state.lastUpdated;
                             }
                         }
                     }, (vm: this, symbol, freshMeta, freshCandles, lastUpdated));
                     
                     model.MarkComplete();
                     _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);
                 }
                 catch (Exception ex)
                 {
                     if (linkedCts.Token.IsCancellationRequested)
                     {
                         model.MarkCanceled();
                     }
                     else
                     {
                        _logger.LogError(ex, "Sync failed for {Symbol}: {Message}", symbol, ex.Message);
                        model.MarkError(ex.Message);

                        if (rollbackOnFailure)
                        {
                            try
                            {
                                _logger.LogInformation("Rolling back added ticker {Symbol} due to sync failure", symbol);
                                await _marketDataProvider.RemoveTickerAsync(symbol);
                                if (targetProfileId.HasValue && targetProfileId.Value != AllTickersId)
                                {
                                    await Task.Run(() => _watchlistManager.RemoveTickersFromProfile(targetProfileId.Value, new[] { symbol }));
                                }
                                await InitializeTickersAsync();
                            }
                            catch (Exception rollbackEx)
                            {
                                _logger.LogError(rollbackEx, "Failed to rollback ticker {Symbol} on sync failure", symbol);
                            }
                        }
                     }
                     _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);
                 }
            };

            vm.AddItem(itemVm);
        }

        syncSession.Show(_dialogService.GetMainWindowOwner());

        // Auto-start sync if enabled
        if (vm.IsAutoSyncEnabled)
        {
            startHandler();
        }

        vm.RequestClose += () =>
        {
            vm.RequestStopAll -= stopHandler;
            vm.RequestClose -= closeHandler;
            vm.RequestStartAll -= startHandler;
            try { sessionCts.Cancel(); } catch { }
            sessionCts.Dispose();
        };

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddNewListAsync(object? parameter)
    {
        bool isPortfolio = false;
        if (parameter is bool b)
        {
            isPortfolio = b;
        }
        else if (parameter is string s && bool.TryParse(s, out var b2))
        {
            isPortfolio = b2;
        }
        else
        {
            // Fallback: Check if the current selection is under the Portfolios category
            isPortfolio = IsUnderCategory(SelectedNode, PortfoliosCategoryId);
        }

        var title = isPortfolio 
            ? LocalizationManager.Instance["TickerList_CreatePortfolio_Title"] 
            : LocalizationManager.Instance["TickerList_CreateWatchlist_Title"];
            
        var defaultName = isPortfolio 
            ? LocalizationManager.Instance["TickerList_DefaultPortfolioName"] 
            : LocalizationManager.Instance["TickerList_DefaultWatchlistName"];

        var name = await _dialogService.ShowInputAsync(
            title, 
            LocalizationManager.Instance["TickerList_EnterName"], 
            defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        await Task.Run(() => 
        {
            if (isPortfolio)
            {
                _watchlistManager.CreateProfile(name, IndicatorColor.Gray, isPortfolio: true);
            }
            else
            {
                _watchlistManager.CreateProfile(name, IndicatorColor.Gray);
            }
        });
    }

    private bool IsUnderCategory(TickerGroupNode? node, Guid categoryId)
    {
        if (node == null) return false;
        if (node.Id == categoryId) return true;
        
        // Check if parent is category
        return Groups.Any(g => g.Id == categoryId && g.Children != null && g.Children.Any(c => c.Id == node.Id));
    }

    [RelayCommand]
    private async Task RenameNode(object? parameter)
    {
        var profile = parameter as WatchlistProfile ?? (parameter as WatchlistNode)?.Profile ?? (parameter as PortfolioNode)?.Profile ?? SelectedWatchlist;
        if (profile == null || profile.Id == AllTickersId) return;

        var title = LocalizationManager.Instance["TickerList_RenameList_Title"];
        var prompt = LocalizationManager.Instance["TickerList_EnterName"];
        var name = await _dialogService.ShowInputAsync(title, prompt, profile.Name);
        if (!string.IsNullOrWhiteSpace(name) && name != profile.Name)
        {
            var profileId = profile.Id;
            await Task.Run(() => _watchlistManager.UpdateProfileName(profileId, name));
        }
    }

    [RelayCommand]
    private async Task DeleteNode(object? parameter)
    {
        // Try to identify what to delete
        WatchlistProfile? profile = parameter as WatchlistProfile;
        WatchlistNode? wlNode = parameter as WatchlistNode;
        PortfolioNode? pNode = parameter as PortfolioNode;

        if (profile == null && wlNode != null) profile = wlNode.Profile;
        if (profile == null && pNode != null) profile = pNode.Profile;
        if (profile == null && parameter == null) profile = SelectedWatchlist;

        if (profile != null)
        {
            if (profile.Id == AllTickersId) return;
            var typeName = profile.IsPortfolio 
                ? LocalizationManager.Instance["TickerList_TypeName_Portfolio"] 
                : LocalizationManager.Instance["TickerList_TypeName_Watchlist"];

            var title = string.Format(LocalizationManager.Instance["TickerList_DeleteConfirm_Title"], typeName);
            var message = string.Format(LocalizationManager.Instance["TickerList_DeleteConfirm_Message"], profile.Name);

            if (await _dialogService.ShowConfirmationAsync(title, message))
            {
                var profileId = profile.Id;
                await Task.Run(() => _watchlistManager.DeleteProfile(profileId));

                if (SelectedNode is WatchlistNode wln && wln.Profile.Id == profileId)
                {
                    SelectedNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
                }
                else if (SelectedNode is PortfolioNode pn && pn.Profile.Id == profileId)
                {
                    SelectedNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
                }
            }
        }
    }

    [RelayCommand]
    private async Task AddFilter(object? parameter)
    {
        var parentNode = parameter as TickerGroupNode;
        if (parentNode == null) return;

        var initial = new StockAnalyzer.Core.Models.Settings.FilterSettings
        {
            ParentId = parentNode.Id
        };

        FilterNode? filterNode = null;

        var result = await _dialogService.ShowFilterSettingsDialogAsync(initial, onApply: applied =>
        {
            if (filterNode == null)
            {
                filterNode = new FilterNode(applied);
                parentNode.Children?.Add(filterNode);
                SelectedNode = filterNode;
            }
            else
            {
                filterNode.UpdateSettings(applied);
            }
            ApplyFilter();
        });

        if (result != null)
        {
            if (filterNode == null)
            {
                filterNode = new FilterNode(result);
                parentNode.Children?.Add(filterNode);
                SelectedNode = filterNode;
            }
            else
            {
                filterNode.UpdateSettings(result);
            }
            ApplyFilter();
        }
        else
        {
            // Rollback if cancelled and we temporarily added a filter node
            if (filterNode != null)
            {
                parentNode.Children?.Remove(filterNode);
                SelectedNode = parentNode;
                ApplyFilter();
            }
        }
    }

    [RelayCommand]
    private async Task EditFilter(object? parameter)
    {
        var filterNode = parameter as FilterNode;
        if (filterNode == null) return;

        var originalSettings = CloneFilterSettings(filterNode.Settings);

        var result = await _dialogService.ShowFilterSettingsDialogAsync(filterNode.Settings, onApply: applied =>
        {
            filterNode.UpdateSettings(applied);
            ApplyFilter();
        });

        if (result != null)
        {
            filterNode.UpdateSettings(result);
            ApplyFilter();
        }
        else
        {
            // Revert changes if cancelled
            filterNode.UpdateSettings(originalSettings);
            ApplyFilter();
        }
    }

    private StockAnalyzer.Core.Models.Settings.FilterSettings CloneFilterSettings(StockAnalyzer.Core.Models.Settings.FilterSettings source)
    {
        if (source == null) return null;
        var clone = new StockAnalyzer.Core.Models.Settings.FilterSettings
        {
            Id = source.Id,
            Name = source.Name,
            ParentId = source.ParentId,
            Rules = new List<StockAnalyzer.Core.Models.Settings.FilterRule>()
        };
        foreach (var rule in source.Rules)
        {
            clone.Rules.Add(new StockAnalyzer.Core.Models.Settings.FilterRule
            {
                Field = rule.Field,
                Operator = rule.Operator,
                Value = rule.Value
            });
        }
        return clone;
    }

    [RelayCommand]
    private async Task DeleteFilter(object? parameter)
    {
        var filterNode = parameter as FilterNode;
        if (filterNode == null) return;

        var parentNode = FindParentNode(Groups, filterNode);
        var collection = parentNode?.Children ?? Groups;
        if (collection.Contains(filterNode))
        {
            collection.Remove(filterNode);
            if (SelectedNode == filterNode)
            {
                SelectedNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
            }
            else
            {
                ApplyFilter();
            }
        }
    }

    public List<StockAnalyzer.Core.Models.Settings.FilterSettings> ExportFilterSettings()
    {
        var result = new List<StockAnalyzer.Core.Models.Settings.FilterSettings>();
        
        var allTickersNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
        if (allTickersNode?.Children != null)
        {
            foreach (var child in allTickersNode.Children.OfType<FilterNode>())
            {
                result.Add(ExportFilterSettingsRecursive(child, AllTickersId));
            }
        }

        var wlCategory = Groups.FirstOrDefault(n => n.Id == WatchlistsCategoryId);
        if (wlCategory?.Children != null)
        {
            foreach (var node in wlCategory.Children.OfType<WatchlistNode>())
            {
                if (node.Children != null)
                {
                    foreach (var child in node.Children.OfType<FilterNode>())
                    {
                        result.Add(ExportFilterSettingsRecursive(child, node.Profile.Id));
                    }
                }
            }
        }

        var pCategory = Groups.FirstOrDefault(n => n.Id == PortfoliosCategoryId);
        if (pCategory?.Children != null)
        {
            foreach (var node in pCategory.Children.OfType<PortfolioNode>())
            {
                if (node.Children != null)
                {
                    foreach (var child in node.Children.OfType<FilterNode>())
                    {
                        result.Add(ExportFilterSettingsRecursive(child, node.Profile.Id));
                    }
                }
            }
        }

        return result;
    }

    private StockAnalyzer.Core.Models.Settings.FilterSettings ExportFilterSettingsRecursive(FilterNode node, Guid parentId)
    {
        var settings = new StockAnalyzer.Core.Models.Settings.FilterSettings
        {
            Id = node.Settings.Id,
            Name = node.DisplayName,
            ParentId = parentId,
            Rules = node.Settings.Rules,
            Children = new List<StockAnalyzer.Core.Models.Settings.FilterSettings>()
        };

        if (node.Children != null)
        {
            foreach (var child in node.Children.OfType<FilterNode>())
            {
                settings.Children.Add(ExportFilterSettingsRecursive(child, node.Settings.Id));
            }
        }

        return settings;
    }

    public void ImportFilterSettings(List<StockAnalyzer.Core.Models.Settings.FilterSettings> filterSettingsList)
    {
        if (filterSettingsList == null) return;

        var allTickersNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
        if (allTickersNode != null) allTickersNode.Children?.Clear();

        var wlCategory = Groups.FirstOrDefault(n => n.Id == WatchlistsCategoryId);
        if (wlCategory?.Children != null)
        {
            foreach (var node in wlCategory.Children)
            {
                node.Children?.Clear();
            }
        }

        var pCategory = Groups.FirstOrDefault(n => n.Id == PortfoliosCategoryId);
        if (pCategory?.Children != null)
        {
            foreach (var node in pCategory.Children)
            {
                node.Children?.Clear();
            }
        }

        foreach (var settings in filterSettingsList)
        {
            AddFilterNodeToParent(settings);
        }
    }

    private void AddFilterNodeToParent(StockAnalyzer.Core.Models.Settings.FilterSettings settings)
    {
        var filterNode = new FilterNode(settings);
        if (settings.Children != null)
        {
            foreach (var childSettings in settings.Children)
            {
                var childNode = CreateFilterNodeRecursive(childSettings);
                filterNode.Children?.Add(childNode);
            }
        }

        var parentNode = FindNodeById(Groups, settings.ParentId);
        if (parentNode != null)
        {
            parentNode.Children?.Add(filterNode);
        }
    }

    private FilterNode CreateFilterNodeRecursive(StockAnalyzer.Core.Models.Settings.FilterSettings settings)
    {
        var node = new FilterNode(settings);
        if (settings.Children != null)
        {
            foreach (var childSettings in settings.Children)
            {
                node.Children?.Add(CreateFilterNodeRecursive(childSettings));
            }
        }
        return node;
    }

    public void RefreshNodes()
    {
        _isInternalChange = true;
        try 
        {
            var currentSelectionId = SelectedNode?.Id;
            
            // 1. Ensure Categories exist (Zero-Allocation Diff)
            SyncCategory(AllTickersId, LocalizationManager.Instance["TickerList_Category_AllTickers"], null);
            var wlCategory = SyncCategory(WatchlistsCategoryId, LocalizationManager.Instance["TickerList_Category_Watchlists"], null);
            var pCategory = SyncCategory(PortfoliosCategoryId, LocalizationManager.Instance["TickerList_Category_Portfolios"], null);

            // Force Order: All Tickers (0), Watchlists (1), Portfolios (2)
            EnsureCategoryOrder(AllTickersId, 0);
            EnsureCategoryOrder(WatchlistsCategoryId, 1);
            EnsureCategoryOrder(PortfoliosCategoryId, 2);

            // 2. Sync Watchlists
            var allProfiles = _watchlistManager.GetAllProfiles();
            // 1. Sync Watchlists
            _syncBuffer.Clear();
            foreach (var p in allProfiles)
            {
                if (!p.IsPortfolio)
                {
                    _syncBuffer.Add(new WatchlistNode(p));
                }
            }
            // Simple sort by Name
            _syncBuffer.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            SyncChildren(wlCategory, _syncBuffer);

            // 2. Sync Portfolios
            _syncBuffer.Clear();
            foreach (var p in allProfiles)
            {
                if (p.IsPortfolio)
                {
                    _syncBuffer.Add(new PortfolioNode(p));
                }
            }
            _syncBuffer.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            SyncChildren(pCategory, _syncBuffer);

            // 4. Restore selection
            if (currentSelectionId.HasValue)
            {
                SelectedNode = FindNodeById(Groups, currentSelectionId.Value);
            }
            
            if (SelectedNode == null)
            {
                SelectedNode = Groups.FirstOrDefault(n => n.Id == AllTickersId);
            }
        }
        finally
        {
            _isInternalChange = false;
        }

        // Trigger grid refresh if the selected node was affected by the sync
        ApplyFilter();
    }

    private void EnsureCategoryOrder(Guid id, int targetIndex)
    {
        var node = Groups.FirstOrDefault(n => n.Id == id);
        if (node != null)
        {
            var currentIndex = Groups.IndexOf(node);
            if (currentIndex != targetIndex && currentIndex != -1)
            {
                Groups.RemoveAt(currentIndex);
                Groups.Insert(targetIndex, node);
            }
        }
    }

    private TickerGroupNode SyncCategory(Guid id, string name, TickerGroupNode? parent)
    {
        var collection = parent?.Children ?? Groups;
        var existing = collection.FirstOrDefault(n => n.Id == id);
        if (existing == null)
        {
            existing = (id == AllTickersId) ? new AllTickersNode(name) : new CategoryNode(id, name);
            collection.Add(existing);
        }
        else
        {
            existing.DisplayName = name;
        }
        return existing;
    }

    private void SyncChildren(TickerGroupNode parent, List<TickerGroupNode> newChildren)
    {
        var currentChildren = parent.Children!;
        
        // 1. Remove gone (Manual reverse loop to avoid allocations and collection modification issues)
        for (int i = currentChildren.Count - 1; i >= 0; i--)
        {
            var currentId = currentChildren[i].Id;
            bool existsInNew = false;
            for (int j = 0; j < newChildren.Count; j++)
            {
                if (newChildren[j].Id == currentId)
                {
                    existsInNew = true;
                    break;
                }
            }
            
            if (!existsInNew)
            {
                currentChildren.RemoveAt(i);
            }
        }

        // 2. Add or Update
        for (int i = 0; i < newChildren.Count; i++)
        {
            var newNode = newChildren[i];
            TickerGroupNode? existing = null;
            
            // Find existing node
            for (int j = 0; j < currentChildren.Count; j++)
            {
                if (currentChildren[j].Id == newNode.Id)
                {
                    existing = currentChildren[j];
                    break;
                }
            }

            if (existing == null)
            {
                currentChildren.Insert(i, newNode);
            }
            else
            {
                // Update display name if changed
                if (existing.DisplayName != newNode.DisplayName)
                {
                    existing.DisplayName = newNode.DisplayName;
                }
                
                // Sync data profiles
                if (existing is WatchlistNode existingW && newNode is WatchlistNode newW)
                {
                    existingW.UpdateProfile(newW.Profile);
                }
                else if (existing is PortfolioNode existingP && newNode is PortfolioNode newP)
                {
                    existingP.UpdateProfile(newP.Profile);
                }

                // Ensure correct order (Move if necessary)
                int currentIndex = currentChildren.IndexOf(existing);
                if (currentIndex != i)
                {
                    currentChildren.Move(currentIndex, i);
                }
            }
        }
    }

    public void SelectNodeById(Guid id)
    {
        var node = FindNodeById(Groups, id);
        if (node != null)
        {
            SelectedNode = node;
        }
    }

    private TickerGroupNode? FindNodeById(IEnumerable<TickerGroupNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            if (node.Children != null)
            {
                var found = FindNodeById(node.Children, id);
                if (found != null) return found;
            }
        }
        return null;
    }

    private async Task InitializeTickersAsync()
    {
        if (_isInitializing) return;
        _isInitializing = true;

        try
        {
            var tickers = await _marketDataProvider.GetAvailableTickersAsync();
            var sortedTickers = tickers.OrderBy(t => t).ToList();
            
            // Optimization: Only update if the list has changed or is empty
            if (AvailableTickers.Count > 0 && AvailableTickers.SequenceEqual(sortedTickers))
            {
                return;
            }

            _pendingTickers = sortedTickers;
            _dispatcherService.Post(static vm => vm.ApplyPendingTickers(), this);
        }
        catch (Exception ex)

        {
            _logger.LogError(ex, "Failed to load tickers");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void ApplyPendingTickers()
    {
        if (_pendingTickers == null) return;

        var current = SelectedTicker;
        _allTickers.Clear();
        _allTickers.AddRange(_pendingTickers);
        _pendingTickers = null;

        ApplyFilter();

        // Restore selection if it existed (e.g. from workspace restoration)
        if (!string.IsNullOrEmpty(current))
        {
            SelectedTicker = current;
        }

        _ = RefreshExistingTagsAsync();
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e) => _dispatcherService.Post(_refreshNodesDelegate);

    partial void OnSelectedTickerChanged(string? value)

    {
        if (_isSyncingSelection) return;
        if (!string.IsNullOrEmpty(value))
        {
            _messenger.Send(new TickerSelectedMessage(value));
        }
    }

    public void Receive(TickerSelectedMessage message)
    {
        _dispatcherService.Post(static state =>
        {
            state.vm._isSyncingSelection = true;
            try
            {
                var ticker = state.message.Value;
                state.vm.SelectedItem = state.vm.DisplayItems.FirstOrDefault(i => i.Symbol == ticker);
                state.vm.SelectedTicker = ticker;
            }
            finally
            {
                state.vm._isSyncingSelection = false;
            }
        }, (vm: this, message));
    }

    partial void OnSelectedNodeChanged(TickerGroupNode? value)
    {
        if (_isInternalChange) return;
        

        OnPropertyChanged(nameof(SelectedWatchlist));
        OnPropertyChanged(nameof(OtherWatchlists));
        OnPropertyChanged(nameof(AllPortfolios));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var currentTicker = SelectedTicker;

        _tickerSortBuffer.Clear();

        if (SelectedNode is WatchlistNode watchlistNode)
        {
            var items = watchlistNode.Profile.Items;
            for (int i = 0; i < items.Count; i++)
            {
                _tickerSortBuffer.Add(items[i].Ticker);
            }
        }
        else if (SelectedNode is PortfolioNode portfolioNode)
        {
            var items = portfolioNode.Profile.Items;
            for (int i = 0; i < items.Count; i++)
            {
                _tickerSortBuffer.Add(items[i].Ticker);
            }
        }
        else if (SelectedNode is AllTickersNode || SelectedNode == null || SelectedNode.Id == AllTickersId)
        {
            for (int i = 0; i < _allTickers.Count; i++)
            {
                _tickerSortBuffer.Add(_allTickers[i]);
            }
        }
        else if (SelectedNode is FilterNode filterNode)
        {
            var tickers = GetTickersForNode(filterNode);
            foreach (var ticker in tickers)
            {
                _tickerSortBuffer.Add(ticker);
            }
        }
        // else: _tickerSortBuffer remains empty (category or action node)

        _logger.LogDebug("Applying filter for node: {NodeName} (Total all tickers: {AllCount})", 
            SelectedNode?.DisplayName ?? "null", _allTickers.Count);
        
        _tickerSortBuffer.Sort(StringComparer.OrdinalIgnoreCase);
        AvailableTickers.ReplaceRange(_tickerSortBuffer);

        // Sync Display Items
        DisplayItems.BeginBulkUpdate();
        try
        {
            DisplayItems.Clear();
            foreach (var ticker in AvailableTickers)
            {
                if (!_viewModelCache.TryGetValue(ticker, out var item))
                {
                    item = new WatchlistItemViewModel(ticker, ticker, "", "", 0, 0, 0, 0, 0, 0);
                    item.PropertyChanged += OnItemPropertyChanged;
                    _viewModelCache[ticker] = item;
                }
                
                DisplayItems.Add(item);
            }
        }
        finally
        {
            DisplayItems.EndBulkUpdate();
        }

        // Restore selection if symbol still exists
        if (!string.IsNullOrEmpty(currentTicker))
        {
            SelectedItem = DisplayItems.FirstOrDefault(i => i.Symbol == currentTicker);
        }

        // Cache Management (Rule #21/Memory Safety): Prune cache if it exceeds threshold
        if (_viewModelCache.Count > 5000)
        {
            var currentTickers = new HashSet<string>(AvailableTickers, StringComparer.OrdinalIgnoreCase);
            var toRemove = new List<string>();
            foreach (var kvp in _viewModelCache)
            {
                if (!currentTickers.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                }
            }
            
            _logger.LogInformation("Pruning Ticker ViewModel Cache. Removing {Count} items.", toRemove.Count);
            foreach (var key in toRemove)
            {
                if (_viewModelCache.TryGetValue(key, out var item))
                {
                    item.PropertyChanged -= OnItemPropertyChanged;
                }
                _viewModelCache.Remove(key);
            }
        }

        OnPropertyChanged(nameof(DisplayItems));
        StartHydration(DisplayItems.ToList());
        OnPropertyChanged(nameof(SelectAll));
    }

    private IEnumerable<string> GetTickersForNode(TickerGroupNode? node)
    {
        if (node == null) return Enumerable.Empty<string>();

        if (node is WatchlistNode watchlistNode)
        {
            return watchlistNode.Profile.Items.Select(x => x.Ticker);
        }
        if (node is PortfolioNode portfolioNode)
        {
            return portfolioNode.Profile.Items.Select(x => x.Ticker);
        }
        if (node is AllTickersNode || node.Id == AllTickersId)
        {
            return _allTickers;
        }
        if (node is FilterNode filterNode)
        {
            var parentNode = FindParentNode(Groups, filterNode);
            var parentTickers = GetTickersForNode(parentNode);
            return ApplyFilterSettings(parentTickers, filterNode.Settings);
        }

        return Enumerable.Empty<string>();
    }

    private TickerGroupNode? FindParentNode(IEnumerable<TickerGroupNode> nodes, TickerGroupNode targetNode)
    {
        foreach (var node in nodes)
        {
            if (node.Children != null)
            {
                if (node.Children.Contains(targetNode))
                {
                    return node;
                }
                var parent = FindParentNode(node.Children, targetNode);
                if (parent != null)
                {
                    return parent;
                }
            }
        }
        return null;
    }

    private IEnumerable<string> ApplyFilterSettings(IEnumerable<string> tickers, StockAnalyzer.Core.Models.Settings.FilterSettings settings)
    {
        if (settings.Rules == null || settings.Rules.Count == 0)
        {
            return tickers;
        }

        var result = new List<string>();
        foreach (var ticker in tickers)
        {
            if (!_viewModelCache.TryGetValue(ticker, out var item))
            {
                item = new WatchlistItemViewModel(ticker, ticker, "", "", 0, 0, 0, 0, 0, 0);
                try
                {
                    var metaTask = _marketDataProvider.GetMetadataAsync(ticker);
                    if (metaTask.IsCompleted)
                    {
                        ApplyMetadataToItem(item, metaTask.Result);
                    }
                    else
                    {
                        var meta = metaTask.AsTask().GetAwaiter().GetResult();
                        ApplyMetadataToItem(item, meta);
                    }
                }
                catch (System.Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load metadata synchronously for {Symbol} during filtering", ticker);
                }

                item.PropertyChanged += OnItemPropertyChanged;
                _viewModelCache[ticker] = item;
            }

            if (_filterEngine.EvaluateSettings(item, settings))
            {
                result.Add(ticker);
            }
        }
        return result;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchlistItemViewModel.IsChecked))
        {
            OnPropertyChanged(nameof(SelectAll));
        }
        else if (e.PropertyName == nameof(WatchlistItemViewModel.Tag) && sender is WatchlistItemViewModel item)
        {
            _ = SaveItemTagAsync(item);
        }
    }

    private async Task SaveItemTagAsync(WatchlistItemViewModel item)
    {
        try
        {
            var meta = await _marketDataProvider.GetMetadataAsync(item.Symbol);
            var updatedMeta = meta with { Tag = item.Tag };
            await _marketDataProvider.SaveMetadataAsync(item.Symbol, updatedMeta);
            _logger.LogInformation("Saved user-defined tag for {Symbol} successfully.", item.Symbol);
            _ = RefreshExistingTagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user-defined tag for {Symbol}", item.Symbol);
        }
    }

    public async Task RefreshExistingTagsAsync()
    {
        try
        {
            var tickers = _allTickers.ToList();
            var uniqueTags = await Task.Run(async () =>
            {
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ticker in tickers)
                {
                    var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                    if (!string.IsNullOrEmpty(meta.Tag))
                    {
                        var parts = meta.Tag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            var trimmed = part.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                tags.Add(trimmed);
                            }
                        }
                    }
                }
                return tags.OrderBy(t => t).ToList();
            });

            _dispatcherService.Post(static state =>
            {
                var (vm, tagsList) = state;
                vm.ExistingTags.Clear();
                foreach (var tag in tagsList)
                {
                    vm.ExistingTags.Add(tag);
                }
            }, (this, uniqueTags));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh existing tags.");
        }
    }

    private async Task ExecuteBulkAddTagsAsync(List<WatchlistItemViewModel> selectedItems, string tags)
    {
        var inputTags = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (inputTags.Count == 0) return;

        StatusMessage = "Adding tags in bulk...";
        IsSyncing = true;

        try
        {
            foreach (var item in selectedItems)
            {
                var meta = await _marketDataProvider.GetMetadataAsync(item.Symbol);
                var existingList = new List<string>();
                if (!string.IsNullOrEmpty(meta.Tag))
                {
                    existingList.AddRange(meta.Tag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
                }

                bool modified = false;
                foreach (var tag in inputTags)
                {
                    if (!existingList.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        existingList.Add(tag);
                        modified = true;
                    }
                }

                if (modified)
                {
                    var newTagStr = string.Join(",", existingList);
                    var updatedMeta = meta with { Tag = newTagStr };
                    await _marketDataProvider.SaveMetadataAsync(item.Symbol, updatedMeta);
                    _dispatcherService.Post(static state =>
                    {
                        state.item.Tag = state.newTagStr;
                    }, (item, newTagStr));
                }
            }

            _logger.LogInformation("Successfully added tags to {Count} tickers.", selectedItems.Count);
            await RefreshExistingTagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed bulk add tags");
        }
        finally
        {
            IsSyncing = false;
            StatusMessage = string.Empty;
        }
    }

    private async Task ExecuteBulkDeleteTagsAsync(List<WatchlistItemViewModel> selectedItems, string tags)
    {
        var inputTags = tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (inputTags.Count == 0) return;

        StatusMessage = "Deleting tags in bulk...";
        IsSyncing = true;

        try
        {
            foreach (var item in selectedItems)
            {
                var meta = await _marketDataProvider.GetMetadataAsync(item.Symbol);
                if (string.IsNullOrEmpty(meta.Tag)) continue;

                var existingList = meta.Tag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .ToList();

                bool modified = false;
                foreach (var tag in inputTags)
                {
                    var found = existingList.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        existingList.Remove(found);
                        modified = true;
                    }
                }

                if (modified)
                {
                    var newTagStr = existingList.Count > 0 ? string.Join(",", existingList) : null;
                    var updatedMeta = meta with { Tag = newTagStr };
                    await _marketDataProvider.SaveMetadataAsync(item.Symbol, updatedMeta);
                    _dispatcherService.Post(static state =>
                    {
                        state.item.Tag = state.newTagStr;
                    }, (item, newTagStr));
                }
            }

            _logger.LogInformation("Successfully deleted tags from {Count} tickers.", selectedItems.Count);
            await RefreshExistingTagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed bulk delete tags");
        }
        finally
        {
            IsSyncing = false;
            StatusMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ShowAddRemoveTagsDialog()
    {
        var tagsList = ExistingTags.ToList();
        var result = await _dialogService.ShowBulkTagEditDialogAsync(tagsList);
        
        if (result == null || string.IsNullOrWhiteSpace(result.Tags)) return;

        var selectedItems = DisplayItems.Where(x => x.IsChecked).ToList();
        if (selectedItems.Count == 0 && SelectedItem != null)
        {
            selectedItems = new List<WatchlistItemViewModel> { SelectedItem };
        }

        if (selectedItems.Count == 0) return;

        if (result.Action == StockAnalyzer.Avalonia.Models.BulkTagEditAction.Add)
        {
            await ExecuteBulkAddTagsAsync(selectedItems, result.Tags);
        }
        else if (result.Action == StockAnalyzer.Avalonia.Models.BulkTagEditAction.Delete)
        {
            await ExecuteBulkDeleteTagsAsync(selectedItems, result.Tags);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    public async Task SyncSingleTickerMetadataAsync(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return;

        var item = DisplayItems.FirstOrDefault(i => i.Symbol == symbol);
        if (item == null) return;

        IsSyncing = true;
        
        await _syncSemaphore.WaitAsync();
        try
        {
            _dispatcherService.Post(static i => i.MarkLoading(), item);
            var meta = await _marketDataProvider.FetchMetadataFromPythonAsync(symbol);
            
            // Note: Closure here is acceptable for complex multi-parameter updates 
            // but we use the stateful Post where possible.
            _dispatcherService.Post(static state => 
            {
                var (vm, s, m, i) = state;
                vm.ApplyMetadataToItem(s, m);
                i.MarkSuccess();
            }, (this, symbol, meta, item));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync single ticker metadata for {Symbol}", symbol);
            _dispatcherService.Post(static state => state.Item.MarkFailed("SYNC_ERROR", state.Ex.Message), (Item: item, Ex: ex));
        }

        finally
        {
            _syncSemaphore.Release();
            IsSyncing = false;
        }
    }

    public void RestoreSelection(string symbol)
    {
        var target = DisplayItems.FirstOrDefault(i => i.Symbol == symbol);
        if (target != null)
        {
            SelectedItem = target;
        }
    }

    private bool CanSync(string symbol) => !IsSyncing && !string.IsNullOrEmpty(symbol);

    private void StartHydration(IReadOnlyList<WatchlistItemViewModel> items)
    {
        _hydrationCts?.Cancel();
        _hydrationCts = new CancellationTokenSource();
        var ct = _hydrationCts.Token;

        _ = Task.Run(async () => 
        {
            IsHydrating = true;
            try 
            {
                // Note: We create a local copy of the list references ONLY ONCE for the background task
                // to avoid collection modification issues, but we do it manually.
                var itemsToHydrate = new WatchlistItemViewModel[items.Count];
                for (int i = 0; i < items.Count; i++) itemsToHydrate[i] = items[i];

                foreach (var item in itemsToHydrate)
                {
                    if (ct.IsCancellationRequested) break;

                    await _syncSemaphore.WaitAsync(ct);
                    try
                    {
                        // 1. Mark Loading (UI Thread)
                        _dispatcherService.Post(static i => i.MarkLoading(), item);

                        // 2. Load metadata (Sector, Industry, Name)
                        var meta = await _marketDataProvider.GetMetadataAsync(item.Symbol);
                        
                        // 3. Load time series data (OHLCV) from local Parquet
                        IReadOnlyList<Core.Models.CandleData>? candles = null;
                        try
                        {
                            candles = await _marketDataProvider.GetTickersDataAsync(item.Symbol, Core.Models.TimeFrame.D1);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load candle data for {Symbol}", item.Symbol);
                        }

                        DateTimeOffset? lastUpdated = null;
                        try
                        {
                            lastUpdated = await _marketDataProvider.GetTimeSeriesLastUpdatedAsync(item.Symbol);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load time series last updated timestamp for {Symbol}", item.Symbol);
                        }

                        // 4. Apply to UI and Mark Success (UI Thread)
                        if (ct.IsCancellationRequested) break;

                        _dispatcherService.Post(static state => 
                        {
                            if (state.ct.IsCancellationRequested) return;
                            state.vm.ApplyMetadataToItem(state.item.Symbol, state.meta);
                            if (state.candles != null && state.candles.Count > 0)
                            {
                                state.vm.ApplyTimeSeriesDataToItem(state.item.Symbol, state.candles);
                                state.item.LastUpdatedUtc = state.lastUpdated;
                            }
                            state.item.MarkSuccess();
                        }, (vm: this, item, meta, candles, lastUpdated, ct));
                    }
                    catch (OperationCanceledException)
                    {
                        _dispatcherService.Post(static i => i.MarkCanceled(), item);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Hydration failed for {Symbol}", item.Symbol);
                        _dispatcherService.Post(static state => state.Item.MarkFailed("HYDRATION_ERROR", state.Ex.Message), (Item: item, Ex: ex));
                    }
                    finally
                    {
                        _syncSemaphore.Release();
                    }
                }
            }
            catch (OperationCanceledException) { /* Handled */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background hydration failed");
            }
            finally
            {
                IsHydrating = false;
            }
        }, ct);
    }

    private void ApplyMetadataToItem(string symbol, TickerMetadata? meta)
    {
        var item = DisplayItems.FirstOrDefault(i => i.Symbol == symbol);
        if (item != null)
        {
            ApplyMetadataToItem(item, meta);
        }
    }

    private void ApplyMetadataToItem(WatchlistItemViewModel item, TickerMetadata? meta)
    {
        if (item != null && meta != null)
        {
            item.Name = string.IsNullOrEmpty(meta.Value.ShortName) ? item.Symbol : meta.Value.ShortName;
            item.Sector = meta.Value.Sector;
            item.Industry = meta.Value.Industry;
            if (!string.IsNullOrEmpty(meta.Value.Tag) || string.IsNullOrEmpty(item.Tag))
            {
                item.Tag = meta.Value.Tag;
            }
            
            item.ReturnOnEquity = meta.Value.ReturnOnEquity;
            item.ReturnOnAssets = meta.Value.ReturnOnAssets;
            item.GrossMargins = meta.Value.GrossMargins;
            item.OperatingMargins = meta.Value.OperatingMargins;
            item.ProfitMargins = meta.Value.ProfitMargins;
            item.CurrentRatio = meta.Value.CurrentRatio;
            item.DebtToEquity = meta.Value.DebtToEquity;
            item.Ebitda = meta.Value.Ebitda;
            item.FreeCashflow = meta.Value.FreeCashflow;
            item.OperatingCashflow = meta.Value.OperatingCashflow;
            item.TrailingPE = meta.Value.TrailingPE;
            item.ForwardPE = meta.Value.ForwardPE;
            item.PriceToBook = meta.Value.PriceToBook;
            item.TrailingEps = meta.Value.TrailingEps;
            item.ForwardEps = meta.Value.ForwardEps;
            item.BookValue = meta.Value.BookValue;
            item.SharesOutstanding = meta.Value.SharesOutstanding;
            item.FloatShares = meta.Value.FloatShares;
            item.ShortRatio = meta.Value.ShortRatio;
            item.ShortPercentOfFloat = meta.Value.ShortPercentOfFloat;
            item.HeldPercentInsiders = meta.Value.HeldPercentInsiders;
            item.HeldPercentInstitutions = meta.Value.HeldPercentInstitutions;
            item.LongBusinessSummary = meta.Value.LongBusinessSummary;
            item.FullTimeEmployees = meta.Value.FullTimeEmployees;
            item.FiftyTwoWeekHigh = meta.Value.FiftyTwoWeekHigh;
            item.FiftyTwoWeekLow = meta.Value.FiftyTwoWeekLow;
            item.RevenueGrowth = meta.Value.RevenueGrowth;
            item.EarningsGrowth = meta.Value.EarningsGrowth;
            item.EnterpriseValue = meta.Value.EnterpriseValue;
            item.EnterpriseToEbitda = meta.Value.EnterpriseToEbitda;
            item.Beta = meta.Value.Beta;
            item.PayoutRatio = meta.Value.PayoutRatio;
            item.DividendRate = meta.Value.DividendRate;
            item.DividendYield = meta.Value.DividendYield;
            item.TotalDebt = meta.Value.TotalDebt;
            item.TotalCash = meta.Value.TotalCash;
            item.TotalRevenue = meta.Value.TotalRevenue;
            item.MarketCap = meta.Value.MarketCap;
            item.PbrCalculated = meta.Value.PbrCalculated;
            item.DividendYieldCalculated = meta.Value.DividendYieldCalculated;
            item.EarningsYield = meta.Value.EarningsYield;
            item.FcfYield = meta.Value.FcfYield;
            item.FcfMargin = meta.Value.FcfMargin;
            item.NetDebt = meta.Value.NetDebt;
            item.NetDebtToEbitda = meta.Value.NetDebtToEbitda;
            item.DividendCoverage = meta.Value.DividendCoverage;
            item.PctFromFiftyTwoWeekHigh = meta.Value.PctFromFiftyTwoWeekHigh;
            item.FloatRatio = meta.Value.FloatRatio;
            item.MarketCapPerEmployee = meta.Value.MarketCapPerEmployee;
            item.PegRatio = meta.Value.PegRatio;
            item.OperatingCashFlowYield = meta.Value.OperatingCashFlowYield;
            item.NetCashRatio = meta.Value.NetCashRatio;
            item.PriceToSalesTrailing12Months = meta.Value.PriceToSalesTrailing12Months;
            item.EnterpriseToRevenue = meta.Value.EnterpriseToRevenue;
            item.EbitdaMargins = meta.Value.EbitdaMargins;
            item.QuickRatio = meta.Value.QuickRatio;
            item.AverageVolume = meta.Value.AverageVolume;
            item.PriceToCashFlowRatio = meta.Value.PriceToCashFlowRatio;
            item.NetDebtEquityRatio = meta.Value.NetDebtEquityRatio;
            item.FiftyTwoWeekRangePosition = meta.Value.FiftyTwoWeekRangePosition;
            item.AverageTurnoverRate = meta.Value.AverageTurnoverRate;
            item.AverageFloatTurnover = meta.Value.AverageFloatTurnover;
            
            item.Region = meta.Value.Region;
            item.QuoteType = meta.Value.QuoteType;
            item.ExchangeTimezoneName = meta.Value.ExchangeTimezoneName;
            item.GmtOffSetMilliseconds = meta.Value.GmtOffSetMilliseconds;
            item.ExDividendDate = meta.Value.ExDividendDate;
            item.LastFiscalYearEnd = meta.Value.LastFiscalYearEnd;
            item.MostRecentQuarter = meta.Value.MostRecentQuarter;
            item.TargetHighPrice = meta.Value.TargetHighPrice;
            item.TargetLowPrice = meta.Value.TargetLowPrice;
            item.TargetMeanPrice = meta.Value.TargetMeanPrice;
            item.TargetMedianPrice = meta.Value.TargetMedianPrice;
            item.RecommendationKey = meta.Value.RecommendationKey;
            item.RecommendationMean = meta.Value.RecommendationMean;
            item.NumberOfAnalystOpinions = meta.Value.NumberOfAnalystOpinions;

            if (item.Volume != 0)
            {
                if (item.SharesOutstanding.HasValue && item.SharesOutstanding.Value != 0)
                {
                    item.DailyTurnoverRate = (item.Volume / item.SharesOutstanding.Value) * 100m;
                }
                if (item.FloatShares.HasValue && item.FloatShares.Value != 0)
                {
                    item.DailyFloatShareTurnoverRatio = (item.Volume / item.FloatShares.Value) * 100m;
                }
            }

            if (item.Close == 0 && meta.Value.CurrentPrice.HasValue && meta.Value.CurrentPrice.Value != 0)
            {
                item.Close = meta.Value.CurrentPrice.Value;
                if (meta.Value.LastClose.HasValue && meta.Value.LastClose.Value != 0)
                {
                    item.Change = meta.Value.CurrentPrice.Value - meta.Value.LastClose.Value;
                    item.ChangePercent = (double)((meta.Value.CurrentPrice.Value - meta.Value.LastClose.Value) / meta.Value.LastClose.Value * 100m);
                }
            }

            item.MetadataLastUpdated = meta.Value.MetadataLastUpdated;
        }
    }

    private static bool IsSortedByTimestamp(IReadOnlyList<Core.Models.CandleData> candles)
    {
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].Timestamp < candles[i - 1].Timestamp) return false;
        }
        return true;
    }

    private void ApplyTimeSeriesDataToItem(string symbol, IReadOnlyList<Core.Models.CandleData> candles)
    {
        var item = DisplayItems.FirstOrDefault(i => i.Symbol == symbol);
        if (item == null || candles.Count == 0) return;

        IReadOnlyList<Core.Models.CandleData> sortedCandles = IsSortedByTimestamp(candles) 
            ? candles 
            : candles.OrderBy(c => c.Timestamp).ToList();

        var latest = sortedCandles[sortedCandles.Count - 1];
        item.Open = latest.Open;
        item.High = latest.High;
        item.Low = latest.Low;
        item.Close = latest.Close;
        item.Volume = latest.Volume;

        // Change = Close - Open (Candle / Intraday price move) when Open is present, else Close - PrevClose
        if (latest.Open != 0)
        {
            item.Change = latest.Close - latest.Open;
            item.ChangePercent = (double)((latest.Close - latest.Open) / latest.Open * 100m);
        }
        else if (sortedCandles.Count >= 2)
        {
            var prev = sortedCandles[sortedCandles.Count - 2];
            item.Change = latest.Close - prev.Close;
            item.ChangePercent = prev.Close != 0 
                ? (double)((latest.Close - prev.Close) / prev.Close * 100m) 
                : 0;
        }
        else
        {
            item.Change = 0;
            item.ChangePercent = 0;
        }

        // Calculate Daily Turnovers
        if (item.SharesOutstanding.HasValue && item.SharesOutstanding.Value != 0)
        {
            item.DailyTurnoverRate = (latest.Volume / item.SharesOutstanding.Value) * 100m;
        }
        if (item.FloatShares.HasValue && item.FloatShares.Value != 0)
        {
            item.DailyFloatShareTurnoverRatio = (latest.Volume / item.FloatShares.Value) * 100m;
        }

        // Recalculate price-dependent metrics using the latest Close price
        decimal close = latest.Close;
        if (close != 0)
        {
            // 1. Market Cap
            if (item.SharesOutstanding.HasValue && item.SharesOutstanding.Value != 0)
            {
                item.MarketCap = close * item.SharesOutstanding.Value;
            }

            // 2. PBR
            if (item.BookValue.HasValue && item.BookValue.Value != 0)
            {
                item.PbrCalculated = close / item.BookValue.Value;
            }

            // 3. Dividend Yield
            if (item.DividendRate.HasValue)
            {
                item.DividendYieldCalculated = (item.DividendRate.Value / close) * 100m;
            }

            // 4. Earnings Yield
            if (item.TrailingEps.HasValue)
            {
                item.EarningsYield = (item.TrailingEps.Value / close) * 100m;
            }

            // 5. FCF Yield
            if (item.FreeCashflow.HasValue && item.MarketCap.HasValue && item.MarketCap.Value != 0)
            {
                item.FcfYield = (item.FreeCashflow.Value / item.MarketCap.Value) * 100m;
            }

            // 6. % From 52 Week High
            if (item.FiftyTwoWeekHigh.HasValue && item.FiftyTwoWeekHigh.Value != 0)
            {
                item.PctFromFiftyTwoWeekHigh = ((close / item.FiftyTwoWeekHigh.Value) - 1m) * 100m;
            }

            // 7. Market Cap Per Employee
            if (item.MarketCap.HasValue && item.FullTimeEmployees.HasValue && item.FullTimeEmployees.Value != 0)
            {
                item.MarketCapPerEmployee = item.MarketCap.Value / (decimal)item.FullTimeEmployees.Value;
            }

            // 8. Operating Cash Flow Yield
            if (item.OperatingCashflow.HasValue && item.MarketCap.HasValue && item.MarketCap.Value != 0)
            {
                item.OperatingCashFlowYield = (item.OperatingCashflow.Value / item.MarketCap.Value) * 100m;
            }

            // 9. Net Cash Ratio
            if (item.TotalCash.HasValue && item.TotalDebt.HasValue && item.MarketCap.HasValue && item.MarketCap.Value != 0)
            {
                item.NetCashRatio = (item.TotalCash.Value - item.TotalDebt.Value) / item.MarketCap.Value;
            }

            // 10. Price to Cash Flow Ratio (PCFR)
            if (item.OperatingCashflow.HasValue && item.MarketCap.HasValue && item.OperatingCashflow.Value != 0)
            {
                item.PriceToCashFlowRatio = item.MarketCap.Value / item.OperatingCashflow.Value;
            }

            // 11. 52-Week Range Position
            if (item.FiftyTwoWeekLow.HasValue && item.FiftyTwoWeekHigh.HasValue)
            {
                decimal range = item.FiftyTwoWeekHigh.Value - item.FiftyTwoWeekLow.Value;
                if (range != 0)
                {
                    item.FiftyTwoWeekRangePosition = (close - item.FiftyTwoWeekLow.Value) / range;
                }
            }

            // 12. Trailing P/E (Live)
            if (item.TrailingEps.HasValue && item.TrailingEps.Value != 0)
            {
                item.TrailingPE = close / item.TrailingEps.Value;
            }

            // 13. Forward P/E (Live)
            if (item.ForwardEps.HasValue && item.ForwardEps.Value != 0)
            {
                item.ForwardPE = close / item.ForwardEps.Value;
            }
        }
    }

    public void MarkLoaded()
    {
        _isLoaded = true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _logger.LogInformation("Disposing TickerListViewModel.");
        
        foreach (var item in _viewModelCache.Values)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _viewModelCache.Clear();
        
        _watchlistManager.WatchlistsChanged -= OnWatchlistsChanged;
        _messenger.UnregisterAll(this);
        
        try
        {
            _hydrationCts?.Cancel();
            _hydrationCts?.Dispose();
            _syncCts?.Cancel();
            _syncCts?.Dispose();
            _statusCts?.Cancel();
            _statusCts?.Dispose();
            _importCts?.Cancel();
            _importCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _syncSemaphore.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
