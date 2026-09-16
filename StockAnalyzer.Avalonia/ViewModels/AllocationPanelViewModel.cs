using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class AllocationPanelViewModel : ViewModelBase, IDisposable, IRecipient<PortfolioSelectedMessage>
{
    private readonly IPortfolioManager _portfolioManager;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<AllocationPanelViewModel> _logger;
    private readonly ILocalizationService _localizationService;
    private readonly IMessenger _messenger;
    private bool _isRefreshing;
    private IDisposable? _timerSubscription;
    private Portfolio? _currentPortfolio;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private decimal _totalValue;

    public ObservableCollection<AllocationEntryViewModel> SectorAllocations { get; } = new();
    public ObservableCollection<AllocationEntryViewModel> AssetAllocations { get; } = new();

    private readonly List<AllocationEntry> _mainItemsBuffer = new();
    private readonly List<AllocationEntry> _smallItemsBuffer = new();
    private readonly List<(string Category, decimal MarketValue, decimal Percentage, uint Color)> _targetEntriesBuffer = new();
    private readonly HashSet<string> _targetKeysBuffer = new();
    private readonly List<AllocationEntryViewModel> _collectionToRemoveBuffer = new();

    public AllocationPanelViewModel(
        IPortfolioManager portfolioManager,
        IMarketDataProvider marketDataProvider,
        IDispatcherService dispatcherService,
        ILocalizationService localizationService,
        ILogger<AllocationPanelViewModel> logger,
        IMessenger messenger)
    {
        _portfolioManager = portfolioManager;
        _marketDataProvider = marketDataProvider;
        _dispatcherService = dispatcherService;
        _localizationService = localizationService;
        _logger = logger;
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));

        _messenger.Register<PortfolioSelectedMessage>(this);

        StartUpdateLoop();
    }

    public void Receive(PortfolioSelectedMessage message)
    {
        _currentPortfolio = message.Portfolio;
        _ = RefreshDataAsync();
    }

    private void StartUpdateLoop()
    {
        _ = Task.Run(async () => 
        {
            await Task.Delay(Random.Shared.Next(LayoutConstants.AllocationJitterMinMs, LayoutConstants.AllocationJitterMaxMs));
            await RefreshDataAsync();
        });

        _timerSubscription = Observable.Interval(TimeSpan.FromSeconds(LayoutConstants.AllocationRefreshIntervalSeconds))
            .Subscribe(async _ => await RefreshDataAsync());
    }

    public async Task RefreshDataAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var currentPortfolio = _currentPortfolio ?? StockAnalyzer.Core.Factories.PortfolioFactory.CreateDefaultMock();
            var symbols = currentPortfolio.Positions.Keys;
            
            if (!symbols.Any() && currentPortfolio.CashBalance == 0)
            {
                _dispatcherService.Post(static vm => 
                {
                    vm.IsEmpty = true;
                    vm.IsLoading = false;
                    vm.SectorAllocations.Clear();
                    vm.AssetAllocations.Clear();
                    vm.TotalValue = 0m;
                }, this);
                return;
            }

            var prices = await _marketDataProvider.GetLatestPricesAsync(symbols);
            var result = await _portfolioManager.GetAllocationAsync(currentPortfolio, prices, _marketDataProvider);

            _dispatcherService.Post(static state =>
            {
                var vm = state.Vm;
                var result = state.Result;
                vm.TotalValue = result.TotalValue;
                vm.UpdateCollection(vm.SectorAllocations, result.SectorAllocations, true);
                vm.UpdateCollection(vm.AssetAllocations, result.AssetAllocations, false);

                vm.IsLoading = false;
                vm.IsEmpty = false;
            }, (Vm: this, Result: result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh allocation data.");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateCollection(ObservableCollection<AllocationEntryViewModel> collection, IReadOnlyList<AllocationEntry> data, bool usePalette)
    {
        var othersThreshold = LayoutConstants.AllocationOthersThreshold;
        
        _mainItemsBuffer.Clear();
        _smallItemsBuffer.Clear();
        
        foreach (var item in data)
        {
            if (item.Percentage >= othersThreshold)
            {
                _mainItemsBuffer.Add(item);
            }
            else
            {
                _smallItemsBuffer.Add(item);
            }
        }
        
        // Sort main items descending by MarketValue (without LINQ OrderBy)
        _mainItemsBuffer.Sort(static (a, b) => b.MarketValue.CompareTo(a.MarketValue));
        
        _targetEntriesBuffer.Clear();

        // Add main items
        for (int i = 0; i < _mainItemsBuffer.Count; i++)
        {
            var item = _mainItemsBuffer[i];
            uint color;
            
            if (item.Category == LayoutConstants.CategoryCash) color = LayoutConstants.ColorCash;
            else if (item.Category == LayoutConstants.CategoryEquity) color = LayoutConstants.ColorEquity;
            else color = usePalette ? LayoutConstants.AllocationSegmentPalette[i % LayoutConstants.AllocationSegmentPalette.Length] : LayoutConstants.ColorOthers;

            _targetEntriesBuffer.Add((item.Category, item.MarketValue, item.Percentage, color));
        }
        
        if (_smallItemsBuffer.Count > 0)
        {
            decimal othersValue = 0m;
            decimal othersPct = 0m;
            foreach (var item in _smallItemsBuffer)
            {
                othersValue += item.MarketValue;
                othersPct += item.Percentage;
            }
            var othersLabel = _localizationService.GetString("Allocation_Label_Others");
            if (string.IsNullOrEmpty(othersLabel) || othersLabel.StartsWith("["))
            {
                othersLabel = "Others";
            }
            _targetEntriesBuffer.Add((othersLabel, othersValue, othersPct, LayoutConstants.ColorOthers));
        }

        _targetKeysBuffer.Clear();
        foreach (var entry in _targetEntriesBuffer)
        {
            _targetKeysBuffer.Add(entry.Category);
        }

        // 1. Remove elements no longer present
        _collectionToRemoveBuffer.Clear();
        foreach (var item in collection)
        {
            if (!_targetKeysBuffer.Contains(item.Category))
            {
                _collectionToRemoveBuffer.Add(item);
            }
        }
        foreach (var item in _collectionToRemoveBuffer)
        {
            collection.Remove(item);
        }
        _collectionToRemoveBuffer.Clear();

        // 2. Add or Update in-place to ensure zero steady-state allocation
        for (int i = 0; i < _targetEntriesBuffer.Count; i++)
        {
            var entry = _targetEntriesBuffer[i];
            
            AllocationEntryViewModel? existing = null;
            foreach (var item in collection)
            {
                if (item.Category == entry.Category)
                {
                    existing = item;
                    break;
                }
            }

            if (existing == null)
            {
                collection.Insert(i, new AllocationEntryViewModel
                {
                    Category = entry.Category,
                    MarketValue = entry.MarketValue,
                    Percentage = entry.Percentage,
                    Color = entry.Color
                });
            }
            else
            {
                existing.MarketValue = entry.MarketValue;
                existing.Percentage = entry.Percentage;
                existing.Color = entry.Color;

                int currentIndex = collection.IndexOf(existing);
                if (currentIndex != i)
                {
                    collection.Move(currentIndex, i);
                }
            }
        }

        _mainItemsBuffer.Clear();
        _smallItemsBuffer.Clear();
        _targetEntriesBuffer.Clear();
        _targetKeysBuffer.Clear();
    }

    public void Dispose()
    {
        _messenger.Unregister<PortfolioSelectedMessage>(this);
        _timerSubscription?.Dispose();
    }
}
