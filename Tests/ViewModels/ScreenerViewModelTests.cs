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

namespace StockAnalyzer.Tests.ViewModels;

public class ScreenerViewModelTests
{
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
}
