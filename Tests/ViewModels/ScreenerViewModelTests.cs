using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;

using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Tests.ViewModels;

public class ScreenerViewModelTests
{
    public ScreenerViewModelTests()
    {
        LocalizationManager.Instance.Initialize("ja");
    }
    private class DummyMarketDataProvider : IMarketDataProvider
    {
        private readonly List<string> _tickers;
        private readonly Dictionary<string, (string Name, string Sector, string Industry, string Tag)> _meta;

        public DummyMarketDataProvider(List<string> tickers, Dictionary<string, (string, string, string, string)> meta = null!)
        {
            _tickers = tickers;
            _meta = meta ?? new();
        }

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult<IReadOnlyList<string>>(_tickers);

        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
        {
            if (_meta.TryGetValue(ticker, out var m))
            {
                return ValueTask.FromResult(new TickerMetadata(ticker, m.Name, "US", m.Sector, m.Industry, "USD") { Tag = m.Tag });
            }
            return ValueTask.FromResult(TickerMetadata.Unknown);
        }

        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(_tickers);
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Task.FromResult(TickerMetadata.Unknown);
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
        public Task AddTickerAsync(string symbol) => Task.CompletedTask;
        public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public void InvalidateMetadataCache(string ticker) { }
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);
    }

    private class DummyWatchlistManager : IWatchlistManager
    {
        private readonly List<WatchlistProfile> _profiles = new();
        public event EventHandler? WatchlistsChanged;

        public DummyWatchlistManager(IEnumerable<WatchlistProfile> profiles)
        {
            _profiles.AddRange(profiles);
        }

        public IReadOnlyList<WatchlistProfile> GetAllProfiles() => _profiles;
        public WatchlistProfile? GetProfileById(Guid profileId) => _profiles.FirstOrDefault(p => p.Id == profileId);
        public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false) => throw new NotImplementedException();
        public void UpdateProfileName(Guid profileId, string name) { }
        public void DeleteProfile(Guid profileId) { }
        public void AddTickerToProfile(Guid profileId, string ticker) { }
        public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickerFromProfile(Guid profileId, string ticker) { }
        public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers) { }
        public void Initialize(IEnumerable<WatchlistProfile> profiles) { }
    }

    [Fact]
    public async Task LoadTargetSourcesAsync_PopulatesAllTickersWatchlistsPortfoliosAndTags()
    {
        // Arrange
        var mockMarket = new DummyMarketDataProvider(
            new List<string> { "AAPL", "MSFT", "GOOGL" },
            new Dictionary<string, (string, string, string, string)>
            {
                { "AAPL", ("Apple", "Tech", "Hardware", "Growth, Tech") },
                { "MSFT", ("Microsoft", "Tech", "Software", "Tech") }
            }
        );

        var profile1 = new WatchlistProfile(Guid.NewGuid(), "Tech Stars", IndicatorColor.Gray, isPortfolio: false, new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow)
        });

        var profile2 = new WatchlistProfile(Guid.NewGuid(), "Main Portfolio", IndicatorColor.Gray, isPortfolio: true, new List<WatchlistItem>
        {
            new WatchlistItem("MSFT", DateTimeOffset.UtcNow)
        });

        var mockWlManager = new DummyWatchlistManager(new[] { profile1, profile2 });

        var vm = new ScreenerViewModel(null!, null!, null!, mockMarket, mockWlManager);

        // Act
        await vm.LoadTargetSourcesAsync();

        // Assert
        Assert.NotEmpty(vm.TargetSources);
        Assert.Contains(vm.TargetSources, s => s.Type == TargetSourceType.AllTickers);
        Assert.Contains(vm.TargetSources, s => s.Type == TargetSourceType.Watchlist && s.ProfileId == profile1.Id);
        Assert.Contains(vm.TargetSources, s => s.Type == TargetSourceType.Portfolio && s.ProfileId == profile2.Id);
        Assert.Contains(vm.TargetSources, s => s.Type == TargetSourceType.TagFilter && s.TagName == "Growth");
    }

    [Fact]
    public async Task UpdateTargetSymbolsAsync_ResolvesCorrectSymbolsForSelectedSource()
    {
        // Arrange
        var mockMarket = new DummyMarketDataProvider(
            new List<string> { "AAPL", "MSFT", "TSLA" },
            new Dictionary<string, (string, string, string, string)>
            {
                { "AAPL", ("Apple", "Tech", "Hardware", "Tech") },
                { "TSLA", ("Tesla", "Auto", "EV", "Growth") }
            }
        );

        var profile1 = new WatchlistProfile(Guid.NewGuid(), "My Watchlist", IndicatorColor.Gray, isPortfolio: false, new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
            new WatchlistItem("MSFT", DateTimeOffset.UtcNow)
        });

        var mockWlManager = new DummyWatchlistManager(new[] { profile1 });
        var vm = new ScreenerViewModel(null!, null!, null!, mockMarket, mockWlManager);

        await vm.LoadTargetSourcesAsync();

        // 1. All Tickers
        var allTickersSource = vm.TargetSources.First(s => s.Type == TargetSourceType.AllTickers);
        await vm.UpdateTargetSymbolsAsync(allTickersSource);
        Assert.Equal(new[] { "AAPL", "MSFT", "TSLA" }, vm.TargetSymbols);

        // 2. Watchlist Source
        var wlSource = vm.TargetSources.First(s => s.Type == TargetSourceType.Watchlist && !s.IsHeader);
        await vm.UpdateTargetSymbolsAsync(wlSource);
        Assert.Equal(new[] { "AAPL", "MSFT" }, vm.TargetSymbols);

        // 3. Tag Source
        var tagSource = vm.TargetSources.First(s => s.Type == TargetSourceType.TagFilter && s.TagName == "Growth" && !s.IsHeader);
        await vm.UpdateTargetSymbolsAsync(tagSource);
        Assert.Equal(new[] { "TSLA" }, vm.TargetSymbols);
    }

    [Fact]
    public void CriteriaFlowItems_GeneratesUppercaseLabelsAndCounts_Correctly()
    {
        var vm = new ScreenerViewModel();
        Assert.Empty(vm.CriteriaFlowItems);

        // Add 3 registered entries
        var entry1 = new ScreenerIndicatorEntry();
        var entry2 = new ScreenerIndicatorEntry();
        var entry3 = new ScreenerIndicatorEntry();

        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry1);
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry2);
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry3);

        Assert.Equal(3, vm.CriteriaFlowItems.Count);
        Assert.Equal("A", vm.CriteriaFlowItems[0].Label);
        Assert.Equal("B", vm.CriteriaFlowItems[1].Label);
        Assert.Equal("C", vm.CriteriaFlowItems[2].Label);

        Assert.Equal("[A: --]", vm.CriteriaFlowItems[0].DisplayText);
        Assert.True(vm.CriteriaFlowItems[0].HasNext);
        Assert.False(vm.CriteriaFlowItems[2].HasNext);

        // Update counts
        vm.UpdateCriteriaFlowItems(new Dictionary<int, int> { { 0, 150 }, { 1, 42 }, { 2, 12 } });

        Assert.Equal("[A: 150件]", vm.CriteriaFlowItems[0].DisplayText);
        Assert.Equal("[B: 42件]", vm.CriteriaFlowItems[1].DisplayText);
        Assert.Equal("[C: 12件]", vm.CriteriaFlowItems[2].DisplayText);

        // Test Fix 2 & 5: Toggle Logical Operator (And ∩ -> Or ∪)
        vm.ToggleEntryLogicalOperatorCommand.Execute(entry1);
        Assert.Equal(LogicalOperator.Or, entry1.LogicalOperator);
        Assert.Equal("∪", entry1.LogicalOperatorSymbol);
        Assert.Equal("∪", vm.CriteriaFlowItems[0].OperatorSymbol);

        // Test Fix 4: Label updates when entry is removed
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Remove(entry2);
        Assert.Equal(2, vm.CriteriaFlowItems.Count);
        Assert.Equal("A", entry1.Label);
        Assert.Equal("B", entry3.Label);
        Assert.Equal("A", vm.CriteriaFlowItems[0].Label);
        Assert.Equal("B", vm.CriteriaFlowItems[1].Label);
    }

    [Fact]
    public void DisabledEntries_DoNotCorruptLogicalOperatorScoping()
    {
        var entry1 = new ScreenerIndicatorEntry { IsEnabled = true, LogicalOperator = LogicalOperator.Or };
        var entry2 = new ScreenerIndicatorEntry { IsEnabled = false, LogicalOperator = LogicalOperator.And };
        var entry3 = new ScreenerIndicatorEntry { IsEnabled = true, LogicalOperator = LogicalOperator.And };

        var registeredEntries = new List<ScreenerIndicatorEntry> { entry1, entry2, entry3 };
        var activeEntries = registeredEntries.Where(e => e.IsEnabled).ToList();

        Assert.Equal(2, activeEntries.Count);
        Assert.Same(entry1, activeEntries[0]);
        Assert.Same(entry3, activeEntries[1]);
        // The operator joining step 1 (entry3) to step 0 (entry1) is entry1's operator (Or)
        Assert.Equal(LogicalOperator.Or, activeEntries[0].LogicalOperator);
    }
}
