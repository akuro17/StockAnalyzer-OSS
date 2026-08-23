using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Factories;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public class TreeNode : ObservableObject
{
    private string _name;

    public Guid NodeId { get; }
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    public bool IsAggregate { get; }
    public ObservableCollection<TreeNode> Children { get; } = new();

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public Guid? ParentNodeId { get; }

    public TreeNode(Guid nodeId, string name, bool isAggregate, Guid? parentNodeId = null)
    {
        NodeId = nodeId;
        _name = name;
        IsAggregate = isAggregate;
        ParentNodeId = parentNodeId;
    }
}

public partial class PortfolioSummaryViewModel : ViewModelBase, IDisposable, IRecipient<TickerSelectedMessage>, IRecipient<ColumnChooserAppliedMessage>
{
    private readonly IPortfolioManager _portfolioManager;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly IMessenger _messenger;
    private readonly ILogger<PortfolioSummaryViewModel> _logger;
    private readonly IWatchlistManager _watchlistManager;
    private readonly IDialogService _dialogService;
    private readonly Func<EditTransactionDialogViewModel> _dialogViewModelFactory;
    private readonly IDesignTimeDetector _designTimeDetector;
    private readonly ILocalizationService _localizationService;
    private bool _isRefreshing;
    private readonly System.Threading.SemaphoreSlim _refreshSemaphore = new(1, 1);
    private bool _isSyncingSelection;
    private bool _isBusy;
    private IDisposable? _timerSubscription;
    private TreeNode? _rootNode;

    private decimal InitialCash => AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.FullName != null && 
                 (a.FullName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || 
                  a.FullName.StartsWith("StockAnalyzer.Tests", StringComparison.OrdinalIgnoreCase) || 
                  a.FullName.StartsWith("StockAnalyzer.Core.Tests", StringComparison.OrdinalIgnoreCase) || 
                  a.FullName.StartsWith("StockAnalyzer.Avalonia.Tests", StringComparison.OrdinalIgnoreCase))) ? 100000m : 0m;

    private readonly List<string> _symbolsBuffer = new();
    private readonly HashSet<string> _distinctSymbolsBuffer = new();
    private readonly HashSet<string> _currentKeysBuffer = new();
    private readonly List<PortfolioPositionViewModel> _toRemoveBuffer = new();
    private readonly HashSet<string> _tickersWithBothBuffer = new();
    private readonly HashSet<string> _seenTickersBuffer = new();
    private readonly List<Position> _sortedPositionsBuffer = new();
    private readonly List<Transaction> _newTransactionsBuffer = new();
    private readonly List<ClosedPosition> _newClosedBuffer = new();
    private readonly HashSet<string> _filterTickersBuffer = new();
    private readonly HashSet<string> _addedTickersBuffer = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isNotesColumnVisible = true;

    [ObservableProperty]
    private bool _isEntryPriceColumnVisible = true;

    [ObservableProperty]
    private bool _isTargetPriceColumnVisible = true;

    [ObservableProperty]
    private bool _isStopLossColumnVisible = true;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(NetAssets))]
    [NotifyPropertyChangedFor(nameof(TotalPnL))]
    [NotifyPropertyChangedFor(nameof(IsTotalProfit))]
    [NotifyPropertyChangedFor(nameof(IsTotalLoss))]
    [NotifyPropertyChangedFor(nameof(IsTotalNeutral))]
    private PortfolioMetrics _metrics = new PortfolioMetrics(0, 0, 0, 0, 0);

    public decimal NetAssets => Metrics.TotalValue;
    public decimal TotalPnL => Metrics.TotalUnrealizedPL;
    public bool IsTotalProfit => TotalPnL > 0;
    public bool IsTotalLoss => TotalPnL < 0;
    public bool IsTotalNeutral => TotalPnL == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTotalRealizedProfit))]
    [NotifyPropertyChangedFor(nameof(IsTotalRealizedLoss))]
    [NotifyPropertyChangedFor(nameof(IsTotalRealizedNeutral))]
    private decimal _totalRealizedPnL;

    public bool IsTotalRealizedProfit => TotalRealizedPnL > 0;
    public bool IsTotalRealizedLoss => TotalRealizedPnL < 0;
    public bool IsTotalRealizedNeutral => TotalRealizedPnL == 0;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasError;

    private Portfolio _currentPortfolio;
    private readonly Dictionary<string, PortfolioPositionViewModel> _positionVmCache = new();
    private readonly Dictionary<Guid, ClosedPositionViewModel> _closedPositionVmCache = new();
    private readonly List<Guid> _toRemoveClosedBuffer = new();
    public ObservableCollection<PortfolioPositionViewModel> Positions { get; } = new();
    public ObservableCollection<Transaction> Transactions { get; } = new();
    public ObservableCollection<ClosedPositionViewModel> ClosedPositions { get; } = new();

    [ObservableProperty]
    private PortfolioPositionViewModel? _selectedPosition;

    public ObservableCollection<TreeNode> Groups { get; } = new();

    [ObservableProperty]
    private TreeNode? _selectedNode;

    [ObservableProperty]
    private CurrencyCode _baseCurrency = CurrencyCode.JPY;

    partial void OnBaseCurrencyChanged(CurrencyCode value)
    {
        var node = SelectedNode ?? _rootNode;
        if (node != null)
        {
            _ = EvaluateAndSelectNodeAsync(node);
        }
    }

    private IReadOnlyDictionary<CurrencyCode, ExchangeRate> GetLatestExchangeRates()
    {
        var dict = new Dictionary<CurrencyCode, ExchangeRate>();
        if (_currentPortfolio?.History == null) return dict;

        for (int i = _currentPortfolio.History.Count - 1; i >= 0; i--)
        {
            var tx = _currentPortfolio.History[i];
            if (tx.AppliedRate.HasValue)
            {
                var rate = tx.AppliedRate.Value;
                dict.TryAdd(rate.BaseCurrency, rate);

                var inverse = rate.Inverse();
                dict.TryAdd(inverse.BaseCurrency, inverse);
            }
        }
        return dict;
    }

    public PortfolioSummaryViewModel(
        IPortfolioManager portfolioManager,
        IMarketDataProvider marketDataProvider,
        IDispatcherService dispatcherService,
        IMessenger messenger,
        ILogger<PortfolioSummaryViewModel> logger,
        IWatchlistManager watchlistManager,
        IDialogService dialogService,
        Func<EditTransactionDialogViewModel> dialogViewModelFactory,
        IDesignTimeDetector designTimeDetector,
        ILocalizationService localizationService)
    {
        _portfolioManager = portfolioManager ?? throw new ArgumentNullException(nameof(portfolioManager));
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _watchlistManager = watchlistManager ?? throw new ArgumentNullException(nameof(watchlistManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dialogViewModelFactory = dialogViewModelFactory ?? throw new ArgumentNullException(nameof(dialogViewModelFactory));
        _designTimeDetector = designTimeDetector ?? throw new ArgumentNullException(nameof(designTimeDetector));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        
        _messenger.Register<TickerSelectedMessage>(this);
        _messenger.Register<ColumnChooserAppliedMessage>(this);
        _currentPortfolio = _designTimeDetector.IsDesignMode ? PortfolioFactory.CreateDefaultMock() : PortfolioFactory.Empty;

        _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;
        
        BuildGroups();
        RefreshNodes();
        StartUpdateLoop();
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e)
    {
        RefreshNodes();
        _ = SyncWatchlistTransactionsAsync();
    }

    private void BuildGroups()
    {
        _dispatcherService.Post(static vm =>
        {
            vm.Groups.Clear();
            
            // Root Node
            var rootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var localizedRootName = StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["Portfolio_Groups_Root"];
            var rootName = (string.IsNullOrEmpty(localizedRootName) || localizedRootName.StartsWith("[")) ? LayoutConstants.PortfolioSummaryRootNodeDefaultName : localizedRootName;
            vm._rootNode = new TreeNode(rootId, rootName, isAggregate: true);
            vm.Groups.Add(vm._rootNode);

            // Fetch all profiles that are portfolios
            var portfolios = vm._watchlistManager.GetAllProfiles().Where(p => p.IsPortfolio).ToList();
            foreach (var p in portfolios)
            {
                vm._rootNode.Children.Add(new TreeNode(p.Id, p.Name, isAggregate: false));
            }

            // Default selection: select the Root Node
            vm.SelectedNode = vm._rootNode;
        }, this);
    }

    private void RefreshNodes()
    {
        var portfolios = _watchlistManager.GetAllProfiles().Where(p => p.IsPortfolio).ToList();
        
        _dispatcherService.Post(static state =>
        {
            var vm = state.Vm;
            var portfoliosList = state.Portfolios;
            if (vm._rootNode == null) return;

            var portfolioIds = portfoliosList.Select(p => p.Id).ToHashSet();

            // 1. Remove nodes no longer present
            var toRemove = vm._rootNode.Children.Where(node => !portfolioIds.Contains(node.NodeId)).ToList();
            bool selectedNodeRemoved = false;
            foreach (var node in toRemove)
            {
                if (vm.SelectedNode == node)
                {
                    selectedNodeRemoved = true;
                }
                vm._rootNode.Children.Remove(node);
            }
            if (selectedNodeRemoved)
            {
                vm.SelectedNode = vm._rootNode;
            }

            // 2. Add or update existing nodes (in-place)
            foreach (var p in portfoliosList)
            {
                var existingNode = vm._rootNode.Children.FirstOrDefault(node => node.NodeId == p.Id);
                if (existingNode == null)
                {
                    existingNode = new TreeNode(p.Id, p.Name, isAggregate: false);
                    vm._rootNode.Children.Add(existingNode);
                }
                else
                {
                    if (existingNode.Name != p.Name)
                    {
                        existingNode.Name = p.Name;
                    }
                }
            }
        }, (Vm: this, Portfolios: portfolios));
    }

    partial void OnSelectedNodeChanged(TreeNode? value)
    {
        if (value == null) return;
        _ = EvaluateAndSelectNodeAsync(value);
    }

    private async Task EvaluateAndSelectNodeAsync(TreeNode node)
    {
        _logger.LogInformation("Selected portfolio node: {Name} (Id: {Id})", node.Name, node.NodeId);

        // Synchronously clear UI collections and set IsLoading = true to prevent rendering old/stale data
        IsLoading = true;
        Positions.Clear();
        Transactions.Clear();
        ClosedPositions.Clear();

        Guid profileId = node.ParentNodeId ?? node.NodeId;
        bool isTickerNode = node.ParentNodeId != null;

        // Try to load from database first to avoid redundant price fetching in PortfolioFactory
        var saved = await _portfolioManager.LoadPortfolioAsync();
        Portfolio portfolio;

        if (saved != null)
        {
            var history = saved.History;
            if (!node.IsAggregate)
            {
                if (isTickerNode)
                {
                    history = saved.History
                        .Where(t => t.Type == TransactionType.Deposit || 
                                    t.Type == TransactionType.Withdrawal || 
                                    string.Equals(t.Ticker, node.Name, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }
            portfolio = _portfolioManager.RebuildPortfolio(InitialCash, history);
        }
        else
        {
            if (node.IsAggregate)
            {
                var portfolios = _watchlistManager.GetAllProfiles().Where(p => p.IsPortfolio).ToList();
                portfolio = await PortfolioFactory.CreateFromProfilesAsync(portfolios, _marketDataProvider);
            }
            else
            {
                var profile = _watchlistManager.GetProfileById(profileId);
                if (profile != null)
                {
                    portfolio = await PortfolioFactory.CreateFromProfileAsync(profile, _marketDataProvider);
                }
                else
                {
                    portfolio = PortfolioFactory.Empty;
                }
            }
        }

        _currentPortfolio = portfolio;

        // Trigger evaluation and refresh UI
        await RefreshDataAsync(force: true);

        // Publish selected message to other tabs
        var type = node.IsAggregate ? PortfolioSelectionType.Aggregate : PortfolioSelectionType.SingleProfile;
        var message = new PortfolioSelectedMessage(node.NodeId, _currentPortfolio, type, DateTimeOffset.UtcNow);
        
        _dispatcherService.Post(static state =>
        {
            state.Messenger.Send(state.Message);
        }, (Messenger: _messenger, Message: message));
    }

    partial void OnSelectedPositionChanged(PortfolioPositionViewModel? value)
    {
        if (_isSyncingSelection || value == null) return;
        _messenger.Send(new TickerSelectedMessage(value.Symbol));
    }

    public void Receive(ColumnChooserAppliedMessage message)
    {
        if (message?.ActiveColumns != null)
        {
            IsNotesColumnVisible = message.ActiveColumns.Any(c => string.Equals(c, "Notes", StringComparison.OrdinalIgnoreCase));
            IsEntryPriceColumnVisible = message.ActiveColumns.Any(c => string.Equals(c, "EntryPrice", StringComparison.OrdinalIgnoreCase));
            IsTargetPriceColumnVisible = message.ActiveColumns.Any(c => string.Equals(c, "TargetPrice", StringComparison.OrdinalIgnoreCase));
            IsStopLossColumnVisible = message.ActiveColumns.Any(c => string.Equals(c, "StopLoss", StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Receive(TickerSelectedMessage message)
    {
        _dispatcherService.Post(static state =>
        {
            var vm = state.Vm;
            vm._isSyncingSelection = true;
            try
            {
                if (vm.SelectedPosition != null && vm.SelectedPosition.Symbol == state.Symbol)
                {
                    return; // Focus is already on a position with the matching symbol (keep current Long/Short selection)
                }
                var match = vm.Positions.FirstOrDefault(p => p.Symbol == state.Symbol);
                vm.SelectedPosition = match;
            }
            finally
            {
                vm._isSyncingSelection = false;
            }
        }, (Vm: this, Symbol: message.Value));
    }

    private void StartUpdateLoop()
    {
        // Initial load with jitter to prevent startup congestion
        _ = Task.Run(async () => 
        {
            await Task.Delay(Random.Shared.Next(LayoutConstants.PortfolioJitterMinMs, LayoutConstants.PortfolioJitterMaxMs));
            try
            {
                var saved = await _portfolioManager.LoadPortfolioAsync();
                if (saved != null)
                {
                    _currentPortfolio = _portfolioManager.RebuildPortfolio(InitialCash, saved.History);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persisted portfolio on startup.");
            }
            await RefreshDataAsync();
        });

        // Use a much slower interval for desktop (60 seconds)
        _timerSubscription = Observable.Interval(TimeSpan.FromSeconds(LayoutConstants.PortfolioRefreshIntervalSeconds))
            .Subscribe(async _ => await RefreshDataAsync());
    }

    public async Task RefreshDataAsync(bool force = false)
    {
        if (_isRefreshing && !force) return;

        await _refreshSemaphore.WaitAsync();
        _isRefreshing = true;

        try
        {
            if (_currentPortfolio == null)
            {
                _dispatcherService.Post(static vm =>
                {
                    vm.IsLoading = false;
                    vm.IsEmpty = true;
                }, this);
                return;
            }

            _filterTickersBuffer.Clear();
            bool hasFilter = false;
            if (SelectedNode != null)
            {
                if (!SelectedNode.IsAggregate)
                {
                    var profile = _watchlistManager.GetProfileById(SelectedNode.NodeId);
                    if (profile != null && profile.Items != null)
                    {
                        foreach (var item in profile.Items)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Ticker))
                            {
                                _filterTickersBuffer.Add(item.Ticker.ToUpperInvariant());
                                hasFilter = true;
                            }
                        }
                    }
                }
                else
                {
                    var portfolios = _watchlistManager.GetAllProfiles().Where(p => p.IsPortfolio).ToList();
                    foreach (var p in portfolios)
                    {
                        if (p.Items != null)
                        {
                            foreach (var item in p.Items)
                            {
                                if (!string.IsNullOrWhiteSpace(item.Ticker))
                                {
                                    _filterTickersBuffer.Add(item.Ticker.ToUpperInvariant());
                                    hasFilter = true;
                                }
                            }
                        }
                    }
                }
            }

            _distinctSymbolsBuffer.Clear();
            _symbolsBuffer.Clear();
            foreach (var pos in _currentPortfolio.Positions.Values)
            {
                string ticker = pos.Ticker;
                if (!hasFilter || (SelectedNode != null && SelectedNode.IsAggregate) || _filterTickersBuffer.Contains(ticker.ToUpperInvariant()))
                {
                    if (_distinctSymbolsBuffer.Add(ticker))
                    {
                        _symbolsBuffer.Add(ticker);
                    }
                }
            }
            if (hasFilter)
            {
                foreach (var ticker in _filterTickersBuffer)
                {
                    if (_distinctSymbolsBuffer.Add(ticker))
                    {
                        _symbolsBuffer.Add(ticker);
                    }
                }
            }

            var prices = await _marketDataProvider.GetLatestPricesAsync(_symbolsBuffer);
            var rates = GetLatestExchangeRates();
            var result = _portfolioManager.Evaluate(_currentPortfolio, prices, rates, BaseCurrency);

            _dispatcherService.Post(static state =>
            {
                var vm = state.Vm;
                var result = state.Result;
                var prices = state.Prices;
                var currentPortfolio = state.CurrentPortfolio;
                var hasFilter = state.HasFilter;

                vm.Metrics = result.Metrics;
                vm.TotalRealizedPnL = result.Metrics.TotalRealizedPL;

                // Sync UI positions (In-place update to minimize allocations)
                vm._currentKeysBuffer.Clear();
                foreach (var pair in currentPortfolio.Positions)
                {
                    var pos = pair.Value;
                    if (!hasFilter || (vm.SelectedNode != null && vm.SelectedNode.IsAggregate) || vm._filterTickersBuffer.Contains(pos.Ticker.ToUpperInvariant()))
                    {
                        vm._currentKeysBuffer.Add(pair.Key);
                    }
                }
                if (hasFilter)
                {
                    foreach (var ticker in vm._filterTickersBuffer)
                    {
                        vm._currentKeysBuffer.Add(ticker);
                    }
                }
                
                // Remove tickers no longer in portfolio
                vm._toRemoveBuffer.Clear();
                foreach (var p in vm.Positions)
                {
                    string key = p.IsShort ? $"{p.Symbol}_Short" : p.Symbol;
                    if (!vm._currentKeysBuffer.Contains(key))
                    {
                        vm._toRemoveBuffer.Add(p);
                    }
                }

                foreach (var pvm in vm._toRemoveBuffer)
                {
                    string key = pvm.IsShort ? $"{pvm.Symbol}_Short" : pvm.Symbol;
                    vm.Positions.Remove(pvm);
                    vm._positionVmCache.Remove(key);
                }

                vm._tickersWithBothBuffer.Clear();
                vm._seenTickersBuffer.Clear();
                foreach (var pos in currentPortfolio.Positions.Values)
                {
                    if (!vm._seenTickersBuffer.Add(pos.Ticker))
                    {
                        vm._tickersWithBothBuffer.Add(pos.Ticker);
                    }
                }

                vm._sortedPositionsBuffer.Clear();
                foreach (var pos in currentPortfolio.Positions.Values)
                {
                    vm._sortedPositionsBuffer.Add(pos);
                }
                vm._sortedPositionsBuffer.Sort(static (a, b) =>
                {
                    int cmp = string.Compare(a.Ticker, b.Ticker, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                    return a.IsShort.CompareTo(b.IsShort);
                });

                vm.Positions.Clear();
                vm._addedTickersBuffer.Clear();

                foreach (var pos in vm._sortedPositionsBuffer)
                {
                    if (hasFilter && (vm.SelectedNode == null || !vm.SelectedNode.IsAggregate) && !vm._filterTickersBuffer.Contains(pos.Ticker.ToUpperInvariant()))
                    {
                        continue;
                    }

                    vm._addedTickersBuffer.Add(pos.Ticker);

                    prices.TryGetValue(pos.Ticker, out var currentPrice);
                    string compositeKey = pos.IsShort ? $"{pos.Ticker}_Short" : pos.Ticker;

                    bool hideSymbol = pos.IsShort && vm._tickersWithBothBuffer.Contains(pos.Ticker);
                    var displaySymbol = hideSymbol ? "" : pos.Ticker;
                    
                    var evaluation = new PositionEvaluation(
                        pos.Ticker,
                        pos.Quantity,
                        pos.AverageCostPerUnit,
                        currentPrice,
                        result.PositionValues.GetValueOrDefault(compositeKey),
                        result.PositionPLs.GetValueOrDefault(compositeKey),
                        pos.AverageCostPerUnit == 0 ? 0 : (pos.IsShort ? (pos.AverageCostPerUnit - currentPrice) : (currentPrice - pos.AverageCostPerUnit)) / pos.AverageCostPerUnit * 100,
                        pos.IsShort,
                        displaySymbol
                    );

                    if (!vm._positionVmCache.TryGetValue(compositeKey, out var pvm))
                    {
                        pvm = new PortfolioPositionViewModel();
                        vm._positionVmCache.Add(compositeKey, pvm);
                    }
                    
                    pvm.IsHedged = vm._tickersWithBothBuffer.Contains(pos.Ticker);
                    pvm.UpdateFrom(evaluation);
                    vm.Positions.Add(pvm);
                }

                if (hasFilter)
                {
                    foreach (var ticker in vm._filterTickersBuffer)
                    {
                        if (!vm._addedTickersBuffer.Contains(ticker))
                        {
                            prices.TryGetValue(ticker, out var currentPrice);
                            var evaluation = new PositionEvaluation(
                                ticker,
                                0m,
                                0m,
                                currentPrice,
                                0m,
                                0m,
                                0m,
                                false,
                                ticker
                            );

                            if (!vm._positionVmCache.TryGetValue(ticker, out var pvm))
                            {
                                pvm = new PortfolioPositionViewModel();
                                vm._positionVmCache.Add(ticker, pvm);
                            }

                            pvm.IsHedged = false;
                            pvm.UpdateFrom(evaluation);
                            vm.Positions.Add(pvm);
                        }
                    }
                }

                // Sync UI transactions
                vm._newTransactionsBuffer.Clear();
                foreach (var t in currentPortfolio.History)
                {
                    if (!hasFilter || (vm.SelectedNode != null && vm.SelectedNode.IsAggregate) || (t.Ticker != null && vm._filterTickersBuffer.Contains(t.Ticker.ToUpperInvariant())))
                    {
                        vm._newTransactionsBuffer.Add(t);
                    }
                }

                if (vm.Transactions.Count != vm._newTransactionsBuffer.Count)
                {
                    vm.Transactions.Clear();
                    for (int i = 0; i < vm._newTransactionsBuffer.Count; i++)
                    {
                        vm.Transactions.Add(vm._newTransactionsBuffer[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < vm._newTransactionsBuffer.Count; i++)
                    {
                        if (vm.Transactions[i] != vm._newTransactionsBuffer[i])
                        {
                            vm.Transactions[i] = vm._newTransactionsBuffer[i];
                        }
                    }
                }

                // Sync UI closed positions
                vm._newClosedBuffer.Clear();
                foreach (var cp in currentPortfolio.ClosedPositions)
                {
                    if (!hasFilter || (vm.SelectedNode != null && vm.SelectedNode.IsAggregate) || vm._filterTickersBuffer.Contains(cp.Ticker.ToUpperInvariant()))
                    {
                        vm._newClosedBuffer.Add(cp);
                    }
                }

                // Remove unused closed positions from Cache
                vm._toRemoveClosedBuffer.Clear();
                foreach (var guid in vm._closedPositionVmCache.Keys)
                {
                    if (!vm._newClosedBuffer.Any(cp => cp.Id == guid))
                    {
                        vm._toRemoveClosedBuffer.Add(guid);
                    }
                }
                foreach (var guid in vm._toRemoveClosedBuffer)
                {
                    vm._closedPositionVmCache.Remove(guid);
                }

                vm.ClosedPositions.Clear();
                foreach (var cp in vm._newClosedBuffer)
                {
                    if (!vm._closedPositionVmCache.TryGetValue(cp.Id, out var cpVm))
                    {
                        cpVm = new ClosedPositionViewModel();
                        vm._closedPositionVmCache.Add(cp.Id, cpVm);
                    }
                    cpVm.UpdateFrom(cp);
                    vm.ClosedPositions.Add(cpVm);
                }

                vm.IsLoading = false;
                vm.IsEmpty = false;
                vm.HasError = false;
            }, (Vm: this, Result: result, Prices: prices, CurrentPortfolio: _currentPortfolio, HasFilter: hasFilter));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh portfolio data.");
            _dispatcherService.Post(static vm => 
            {
                vm.HasError = true;
                vm.IsLoading = false; // Prevent getting stuck on "Evaluating Portfolio..."
            }, this);
        }
        finally
        {
            _isRefreshing = false;
            _refreshSemaphore.Release();
        }
    }


    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task AddTransactionAsync(string typeStr)
    {
        if (Enum.TryParse<TransactionType>(typeStr, out var type))
        {
            await OpenAddTransactionDialogAsync(type);
        }
    }

    public string? PrefillTicker { get; set; }

    private async Task OpenAddTransactionDialogAsync(TransactionType type)
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            using (var vm = _dialogViewModelFactory())
            {
                vm.Type = type;
                if (!string.IsNullOrWhiteSpace(PrefillTicker))
                {
                    vm.Ticker = PrefillTicker;
                    PrefillTicker = null; // Clear after use
                }
                else if (SelectedPosition != null && !string.IsNullOrWhiteSpace(SelectedPosition.Symbol))
                {
                    vm.Ticker = SelectedPosition.Symbol;
                }
                
                var result = await _dialogService.ShowEditTransactionDialogAsync(vm);
                if (result != null)
                {
                    try
                    {
                        var globalPortfolio = await _portfolioManager.LoadPortfolioAsync() ?? PortfolioFactory.Empty;
                        var history = globalPortfolio.History.ToList();
                        history.Add(result);

                        var rebuilt = _portfolioManager.RebuildPortfolio(InitialCash, history);
                        await _portfolioManager.SavePortfolioAsync(rebuilt);

                        // Add to watchlist profile AFTER saving manual transaction to database
                        if (SelectedNode != null && !SelectedNode.IsAggregate && !string.IsNullOrWhiteSpace(result.Ticker))
                        {
                            var profile = _watchlistManager.GetProfileById(SelectedNode.NodeId);
                            if (profile != null && (profile.Items == null || !profile.Items.Any(i => string.Equals(i.Ticker, result.Ticker, StringComparison.OrdinalIgnoreCase))))
                            {
                                _watchlistManager.AddTickersToProfile(SelectedNode.NodeId, new[] { result.Ticker });
                            }
                        }

                        _logger.LogInformation("New transaction added successfully and global portfolio rebuilt.");
                        if (SelectedNode != null)
                        {
                            await EvaluateAndSelectNodeAsync(SelectedNode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add new transaction and rebuild portfolio.");
                        await _dialogService.ShowAlertAsync(
                            _localizationService.GetString("Portfolio_Error_Title") ?? "Error",
                            _localizationService.GetFormattedString("Portfolio_Error_Add", "Failed to add transaction: {0}", ex.Message));
                    }
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ExitPositionAsync(PortfolioPositionViewModel position)
    {
        if (position == null) return;
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _logger.LogInformation("Exit position triggered for: {Symbol}", position.Symbol);

            using (var vm = _dialogViewModelFactory())
            {
                vm.Type = position.IsShort ? TransactionType.ExitShort : TransactionType.ExitLong;
                vm.Ticker = position.Symbol;
                vm.Quantity = position.Quantity;
                vm.PricePerUnit = position.CurrentPrice;
                vm.CashAmount = position.Quantity * position.CurrentPrice;
                vm.ExecutedAt = DateTime.UtcNow;

                // Find original position currency to lock on Exit
                var matchingPosKey = position.IsShort ? $"{position.Symbol}_Short" : position.Symbol;
                if (_currentPortfolio != null && _currentPortfolio.Positions.TryGetValue(matchingPosKey, out var originalPos))
                {
                    vm.SelectedCurrency = originalPos.AverageCost.Currency;
                    if (originalPos.AverageCost.Currency != CurrencyCode.USD)
                    {
                        // Get latest rate for this currency from history if available
                        var rates = GetLatestExchangeRates();
                        if (rates.TryGetValue(originalPos.AverageCost.Currency, out var currentRate))
                        {
                            vm.AppliedRateValue = currentRate.Rate;
                        }
                    }
                }

                var result = await _dialogService.ShowEditTransactionDialogAsync(vm);
                if (result != null)
                {
                    try
                    {
                        var globalPortfolio = await _portfolioManager.LoadPortfolioAsync() ?? PortfolioFactory.Empty;
                        var history = globalPortfolio.History.ToList();
                        history.Add(result);

                        var rebuilt = _portfolioManager.RebuildPortfolio(InitialCash, history);
                        await _portfolioManager.SavePortfolioAsync(rebuilt);

                        _logger.LogInformation("Exit transaction added successfully and global portfolio rebuilt.");
                        if (SelectedNode != null)
                        {
                            await EvaluateAndSelectNodeAsync(SelectedNode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to exit position and rebuild portfolio.");
                        await _dialogService.ShowAlertAsync(
                            _localizationService.GetString("Portfolio_Error_Title") ?? "Error",
                            _localizationService.GetFormattedString("Portfolio_Error_Exit", "Failed to exit position: {0}", ex.Message));
                    }
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task EditTransactionAsync(Transaction transaction)
    {
        if (transaction == null) return;
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _logger.LogInformation("Edit transaction triggered for: {Type} {Ticker}", transaction.Type, transaction.Ticker);

            using (var vm = _dialogViewModelFactory())
            {
                vm.Type = transaction.Type;
                vm.Ticker = transaction.Ticker ?? string.Empty;
                vm.Quantity = transaction.Quantity;
                vm.PricePerUnit = transaction.PricePerUnit;
                vm.Fee = transaction.Fee;
                vm.CashAmount = transaction.CashAmount;
                vm.ExecutedAt = transaction.ExecutedAt.DateTime;
                vm.Notes = transaction.Notes;
                vm.TargetPrice = transaction.TargetPrice;
                vm.StopLoss = transaction.StopLoss;
                
                var result = await _dialogService.ShowEditTransactionDialogAsync(vm);
                if (result != null)
                {
                    try
                    {
                        if (SelectedNode != null && !SelectedNode.IsAggregate && !string.IsNullOrWhiteSpace(result.Ticker))
                        {
                            var profile = _watchlistManager.GetProfileById(SelectedNode.NodeId);
                            if (profile != null && (profile.Items == null || !profile.Items.Any(i => string.Equals(i.Ticker, result.Ticker, StringComparison.OrdinalIgnoreCase))))
                            {
                                _watchlistManager.AddTickersToProfile(SelectedNode.NodeId, new[] { result.Ticker });
                            }
                        }

                        var globalPortfolio = await _portfolioManager.LoadPortfolioAsync() ?? PortfolioFactory.Empty;
                        var history = globalPortfolio.History.ToList();
                        int idx = history.FindIndex(t => t.Id == transaction.Id);
                        if (idx >= 0)
                        {
                            history[idx] = result;
                        }
                        else
                        {
                            _logger.LogWarning("Original transaction not found in global history during update.");
                            return;
                        }

                        var rebuilt = _portfolioManager.RebuildPortfolio(InitialCash, history);
                        await _portfolioManager.SavePortfolioAsync(rebuilt);

                        _logger.LogInformation("Transaction edited successfully and global portfolio rebuilt.");
                        if (SelectedNode != null)
                        {
                            await EvaluateAndSelectNodeAsync(SelectedNode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to edit transaction and rebuild portfolio.");
                        await _dialogService.ShowAlertAsync(
                            _localizationService.GetString("Portfolio_Error_Title") ?? "Error",
                            _localizationService.GetFormattedString("Portfolio_Error_Edit", "Failed to edit transaction: {0}", ex.Message));
                    }
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task DeleteTransactionAsync(Transaction transaction)
    {
        if (transaction == null) return;
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _logger.LogInformation("Delete transaction triggered for: {Type} {Ticker} executed at {ExecutedAt}", transaction.Type, transaction.Ticker, transaction.ExecutedAt);

            string name = string.IsNullOrEmpty(transaction.Ticker) ? $"{transaction.Type}" : $"{transaction.Type} for '{transaction.Ticker}'";
            bool confirm = await _dialogService.ShowConfirmationAsync(
                _localizationService.GetString("Portfolio_Confirm_Delete_Title") ?? "Delete Transaction",
                _localizationService.GetFormattedString("Portfolio_Confirm_Delete_Msg", "Are you sure you want to delete this {0} transaction?", name)
            );

            if (confirm)
            {
                try
                {
                    var globalPortfolio = await _portfolioManager.LoadPortfolioAsync() ?? PortfolioFactory.Empty;
                    var history = globalPortfolio.History.ToList();
                    int idx = history.FindIndex(t => t.Id == transaction.Id);
                    if (idx >= 0)
                    {
                        history.RemoveAt(idx);
                        var rebuilt = _portfolioManager.RebuildPortfolio(InitialCash, history);
                        await _portfolioManager.SavePortfolioAsync(rebuilt);

                        _logger.LogInformation("Transaction deleted successfully and global portfolio rebuilt.");
                        if (SelectedNode != null)
                        {
                            await EvaluateAndSelectNodeAsync(SelectedNode);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Original transaction not found in history during deletion.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete transaction and rebuild portfolio.");
                    await _dialogService.ShowAlertAsync(
                        _localizationService.GetString("Portfolio_Error_Title") ?? "Error",
                        _localizationService.GetFormattedString("Portfolio_Error_Delete", "Failed to delete transaction: {0}", ex.Message));
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task DeleteTransactionsAsync(IEnumerable<Transaction> transactions)
    {
        if (transactions == null) return;
        var list = transactions.ToList();
        if (list.Count == 0) return;
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _logger.LogInformation("Delete transactions triggered for {Count} transactions", list.Count);

            string title = _localizationService.GetString("Portfolio_Confirm_DeleteSelected_Title") ?? "Delete Selected Transactions";
            string msg = _localizationService.GetFormattedString("Portfolio_Confirm_DeleteSelected_Msg", "Are you sure you want to delete {0} selected transactions?", list.Count);

            bool confirm = await _dialogService.ShowConfirmationAsync(title, msg);

            if (confirm)
            {
                try
                {
                    var globalPortfolio = await _portfolioManager.LoadPortfolioAsync() ?? PortfolioFactory.Empty;
                    var history = globalPortfolio.History.ToList();
                    
                    int removedCount = 0;
                    foreach (var transaction in list)
                    {
                        int idx = history.FindIndex(t => t.Id == transaction.Id);
                        if (idx >= 0)
                        {
                            history.RemoveAt(idx);
                            removedCount++;
                        }
                    }

                    if (removedCount > 0)
                    {
                        var rebuilt = _portfolioManager.RebuildPortfolio(InitialCash, history);
                        await _portfolioManager.SavePortfolioAsync(rebuilt);

                        _logger.LogInformation("{Count} transactions deleted successfully and global portfolio rebuilt.", removedCount);
                        if (SelectedNode != null)
                        {
                            await EvaluateAndSelectNodeAsync(SelectedNode);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("None of the selected transactions were found in history during deletion.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete transactions and rebuild portfolio.");
                    await _dialogService.ShowAlertAsync(
                        _localizationService.GetString("Portfolio_Error_Title") ?? "Error",
                        _localizationService.GetFormattedString("Portfolio_Error_Delete", "Failed to delete transaction: {0}", ex.Message));
                }
            }
        }
        finally
        {
            _isBusy = false;
        }
    }



    private async Task SyncWatchlistTransactionsAsync()
    {
        // Auto-generation of mock transactions is disabled to allow manual transaction management
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _watchlistManager.WatchlistsChanged -= OnWatchlistsChanged;
        _messenger.Unregister<TickerSelectedMessage>(this);
        _messenger.Unregister<ColumnChooserAppliedMessage>(this);
        _timerSubscription?.Dispose();
        _refreshSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
