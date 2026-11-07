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
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;

using StockAnalyzer.Avalonia.Services;
using System.Collections.Immutable;

namespace StockAnalyzer.Tests.ViewModels;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
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

        /// <summary>Tickers whose metadata call waits until the given task completes (to hold a target-symbol load half way).</summary>
        public Dictionary<string, Task> MetadataGates { get; } = new();

        public async ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
        {
            if (MetadataGates.TryGetValue(ticker, out var gate))
            {
                await gate;
            }
            if (_meta.TryGetValue(ticker, out var m))
            {
                return new TickerMetadata(ticker, m.Name, "US", m.Sector, m.Industry, "USD") { Tag = m.Tag };
            }
            return TickerMetadata.Unknown;
        }

        /// <summary>The daily bars served per symbol (the result list reads its fixed price columns from them); none by default.</summary>
        public Dictionary<string, IReadOnlyList<CandleData>> Candles { get; } = new();

        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame)
            => Task.FromResult(Candles.TryGetValue(symbol, out var bars) ? bars : Array.Empty<CandleData>());
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
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
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

    private class DummyTickerSourceResolver(Guid wlId, Guid pId) : ITickerSourceResolver
    {
        public Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources, IReadOnlyCollection<string>? excluded = null, CancellationToken cancellationToken = default)
        {
            var desc = sources[0];
            var list = desc.Kind switch
            {
                RlSourceKind.AllTickers => new[] { "AAPL", "MSFT", "TSLA" },
                RlSourceKind.Watchlist when desc.Id == wlId => new[] { "AAPL", "MSFT" },
                RlSourceKind.Portfolio when desc.Id == pId => new[] { "TSLA" },
                RlSourceKind.Tag when desc.TagName == "Growth" => new[] { "AAPL", "TSLA" },
                _ => Array.Empty<string>()
            };
            return Task.FromResult(new RlSourceResolution(list.ToImmutableArray(), ImmutableArray<RlSourceDiagnostic>.Empty));
        }
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

        var vm = new ScreenerViewModel(null!, null!, mockMarket, mockWlManager);

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
        var vm = new ScreenerViewModel(null!, null!, mockMarket, mockWlManager);

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
    public async Task UpdateTargetSymbolsAsync_WithInjectedResolver_ProducesIdenticalSymbolsToLegacy()
    {
        // Arrange
        var mockMarket = new DummyMarketDataProvider(
            new List<string> { "AAPL", "MSFT", "TSLA" },
            new Dictionary<string, (string, string, string, string)>
            {
                { "AAPL", ("Apple", "Tech", "Hardware", "Growth") },
                { "MSFT", ("Microsoft", "Tech", "Software", "Value") },
                { "TSLA", ("Tesla", "Auto", "EV", "Growth") }
            }
        );

        var profile1 = new WatchlistProfile(Guid.NewGuid(), "My Watchlist", IndicatorColor.Gray, isPortfolio: false, new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
            new WatchlistItem("MSFT", DateTimeOffset.UtcNow)
        });
        var profile2 = new WatchlistProfile(Guid.NewGuid(), "My Portfolio", IndicatorColor.Gray, isPortfolio: true, new List<WatchlistItem>
        {
            new WatchlistItem("TSLA", DateTimeOffset.UtcNow)
        });

        var mockWlManager = new DummyWatchlistManager(new[] { profile1, profile2 });

        var resolver = new DummyTickerSourceResolver(profile1.Id, profile2.Id);
        var resolverVm = new ScreenerViewModel(null!, null!, mockMarket, mockWlManager, tickerSourceResolver: resolver);
        await resolverVm.LoadTargetSourcesAsync();

        // 1. All Tickers
        var allSource = resolverVm.TargetSources.First(s => s.Type == TargetSourceType.AllTickers);
        await resolverVm.UpdateTargetSymbolsAsync(allSource);
        Assert.Equal(new[] { "AAPL", "MSFT", "TSLA" }, resolverVm.TargetSymbols);

        // 2. Watchlist
        var wlSource = resolverVm.TargetSources.First(s => s.Type == TargetSourceType.Watchlist && s.ProfileId == profile1.Id);
        await resolverVm.UpdateTargetSymbolsAsync(wlSource);
        Assert.Equal(new[] { "AAPL", "MSFT" }, resolverVm.TargetSymbols);

        // 3. Portfolio
        var pSource = resolverVm.TargetSources.First(s => s.Type == TargetSourceType.Portfolio && s.ProfileId == profile2.Id);
        await resolverVm.UpdateTargetSymbolsAsync(pSource);
        Assert.Equal(new[] { "TSLA" }, resolverVm.TargetSymbols);

        // 4. Tag Filter
        var tagSource = new ScreenerTargetSource(TargetSourceType.TagFilter, "Growth", tagName: "Growth");
        await resolverVm.UpdateTargetSymbolsAsync(tagSource);
        Assert.Equal(new[] { "AAPL", "TSLA" }, resolverVm.TargetSymbols);
    }

    // Tickers AAPL (Growth), MSFT, TSLA (Growth); the watchlist holds AAPL and MSFT.
    private static (ScreenerViewModel Vm, DummyMarketDataProvider Market) CreateTargetSourceViewModel()
    {
        var market = new DummyMarketDataProvider(
            new List<string> { "AAPL", "MSFT", "TSLA" },
            new Dictionary<string, (string, string, string, string)>
            {
                { "AAPL", ("Apple", "Tech", "Hardware", "Growth") },
                { "MSFT", ("Microsoft", "Tech", "Software", "Value") },
                { "TSLA", ("Tesla", "Auto", "EV", "Growth") }
            });
        var watchlist = new WatchlistProfile(Guid.NewGuid(), "My Watchlist", IndicatorColor.Gray, isPortfolio: false, new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
            new WatchlistItem("MSFT", DateTimeOffset.UtcNow)
        });
        return (new ScreenerViewModel(null!, null!, market, new DummyWatchlistManager(new[] { watchlist })), market);
    }

    [Fact]
    public async Task SelectingTwoSources_FastSwitch_KeepsOnlyLastSource()
    {
        var (vm, market) = CreateTargetSourceViewModel();
        await vm.LoadTargetSourcesAsync();
        var tagSource = vm.TargetSources.First(s => s.Type == TargetSourceType.TagFilter && s.TagName == "Growth" && !s.IsHeader);
        var watchlistSource = vm.TargetSources.First(s => s.Type == TargetSourceType.Watchlist && !s.IsHeader);

        // The tag load stops at the metadata of its first candidate until the gate opens.
        var gate = new TaskCompletionSource();
        market.MetadataGates["AAPL"] = gate.Task;
        vm.SelectedTargetSource = tagSource;
        vm.SelectedTargetSource = watchlistSource;
        Assert.Equal(new[] { "AAPL", "MSFT" }, vm.TargetSymbols);

        gate.SetResult();

        Assert.Equal(new[] { "AAPL", "MSFT" }, vm.TargetSymbols);
    }

    [Fact]
    public async Task Dispose_CancelsPendingTargetLoad()
    {
        var (vm, market) = CreateTargetSourceViewModel();
        await vm.LoadTargetSourcesAsync();
        var tagSource = vm.TargetSources.First(s => s.Type == TargetSourceType.TagFilter && s.TagName == "Growth" && !s.IsHeader);

        var gate = new TaskCompletionSource();
        market.MetadataGates["AAPL"] = gate.Task;
        vm.SelectedTargetSource = tagSource;
        vm.Dispose();

        gate.SetResult();

        Assert.Empty(vm.TargetSymbols);
    }

    [Fact]
    public async Task SingleSelection_ResultUnchanged()
    {
        var (vm, _) = CreateTargetSourceViewModel();
        await vm.LoadTargetSourcesAsync();

        vm.SelectedTargetSource = vm.TargetSources.First(s => s.Type == TargetSourceType.AllTickers);
        Assert.Equal(new[] { "AAPL", "MSFT", "TSLA" }, vm.TargetSymbols);

        vm.SelectedTargetSource = vm.TargetSources.First(s => s.Type == TargetSourceType.Watchlist && !s.IsHeader);
        Assert.Equal(new[] { "AAPL", "MSFT" }, vm.TargetSymbols);

        vm.SelectedTargetSource = vm.TargetSources.First(s => s.Type == TargetSourceType.TagFilter && s.TagName == "Growth" && !s.IsHeader);
        Assert.Equal(new[] { "AAPL", "TSLA" }, vm.TargetSymbols);
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

        // Test Fix 2 & 5: CriteriaFlowItems reflects LogicalOperator (And ∩ -> Or ∪). The per-entry toggle button/command
        // was retired when Filters became a tree (ScreenerFilterConditionTree Task 5):
        // AND/OR is now set per GROUP via the tree's Edit Group dialog, not per entry, so the badge is exercised here by
        // setting the property directly instead of executing the removed command.
        entry1.LogicalOperator = LogicalOperator.Or;
        vm.UpdateCriteriaFlowItems();
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

    [Fact]
    public async Task ScanAsync_WhenNoConditionsRegistered_ShowsWarningAndDoesNotScan()
    {
        var vm = new ScreenerViewModel(null!, null!, null!, null!);
        Assert.Empty(vm.IndicatorRegistrationViewModel.RegisteredEntries);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.IsScanning);
        Assert.Empty(vm.Results);
        Assert.Equal(LocalizationManager.Instance["Screener_NoConditionsRegistered"], vm.StatusMessage);
    }

    [Fact]
    public async Task ScanAsync_WhenAllConditionsDisabled_ShowsWarningAndDoesNotScan()
    {
        var vm = new ScreenerViewModel(null!, null!, null!, null!);
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            IsEnabled = false
        });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.IsScanning);
        Assert.Empty(vm.Results);
        Assert.Equal(LocalizationManager.Instance["Screener_NoConditionsRegistered"], vm.StatusMessage);
    }

    // ---- the frame of each condition side (daily / weekly / monthly) ----------------------------------------------------------------------

    private const decimal DailyClose = 111m;
    private const decimal WeeklyClose = 777m;

    /// <summary>The plain scan only (matches AAA, records the frames asked for). It has none of the other members, so they keep the interface defaults and refuse.</summary>
    private class PlainScreenerService : IScreenerService
    {
        public List<TimeFrame> PlainScans { get; } = new();

        public Task<List<string>> ScreenAsync(ScreeningCriteria criteria, IProgress<int> progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IProgress<int> progress, CancellationToken ct)
        {
            PlainScans.Add(timeFrame);
            return Task.FromResult(new List<string> { "AAA" });
        }
    }

    /// <summary>
    /// Records which scan the tab asked for. The plain scan and the side-frames scan both match AAA; the bars of the result columns are one weekly-looking bar closing at
    /// <see cref="WeeklyClose"/> so a column value shows which bars it was read from.
    /// </summary>
    private sealed class RecordingScreenerService : PlainScreenerService, IScreenerService
    {
        public List<(ScreenerBarSelection Selection, TimeFrame[] Frames)> SideFrameScans { get; } = new();
        public List<(string Symbol, TimeFrame Frame, ScreenerBarSelection Selection, int Count)> BarReads { get; } = new();
        public ScreenerSideFramesResult Answer { get; set; } = new(new[] { "AAA" }, Array.Empty<string>(), Array.Empty<ScreenerFrameReport>());

        public Task<ScreenerSideFramesResult> ScreenWithSideFramesAsync(List<string> symbols, IScreeningCondition condition, ScreenerBarSelection selection, IProgress<int> progress, CancellationToken ct)
        {
            SideFrameScans.Add((selection, ScreenerSideFrames.Collect(condition).ToArray()));
            return Task.FromResult(Answer);
        }

        public Task<IReadOnlyList<CandleData>> LoadScreenerBarsAsync(string symbol, TimeFrame timeFrame, ScreenerBarSelection selection, int count, CancellationToken ct)
        {
            BarReads.Add((symbol, timeFrame, selection, count));
            return Task.FromResult<IReadOnlyList<CandleData>>(new[] { new CandleData(new DateTime(2024, 1, 22), WeeklyClose, WeeklyClose, WeeklyClose, WeeklyClose, 1) });
        }
    }

    private static ScreenerIndicatorSideConfig PriceSide(string output, TimeFrame frame)
        => new() { IndicatorType = IndicatorType.Price, OutputName = output, CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = frame };

    private static ScreenerIndicatorSideConfig ColumnSide(string name, TimeFrame frame = TimeFrame.D1)
        => new() { OutputName = name, CategoryType = ScreenerItemCategoryType.Column, TimeFrame = frame };

    private static ScreenerIndicatorEntry Entry(ScreenerIndicatorSideConfig left, ScreenerIndicatorSideConfig? right = null, bool enabled = true) => new()
    {
        LeftHand = left,
        CategoryType = left.CategoryType, // the registration window sets the entry's category with its side's
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = right is null ? RightHandTargetMode.NumericValue : RightHandTargetMode.Indicator,
        RightNumericValue = 0m,
        RightHand = right ?? new ScreenerIndicatorSideConfig(),
        IsEnabled = enabled,
    };

    private static ScreenerViewModel NewScreener(IScreenerService service)
    {
        var market = new DummyMarketDataProvider(new List<string> { "AAA", "BBB" });
        market.Candles["AAA"] = new[]
        {
            new CandleData(new DateTime(2024, 1, 30), 100m, 100m, 100m, 100m, 5),
            new CandleData(new DateTime(2024, 1, 31), DailyClose, DailyClose, DailyClose, DailyClose, 5),
        };
        return new ScreenerViewModel(service, null, market, null);
    }

    private static async Task<(ScreenerViewModel Vm, RecordingScreenerService Service)> ScreenerWithAsync(params ScreenerIndicatorEntry[] entries)
    {
        var service = new RecordingScreenerService();
        ScreenerViewModel vm = NewScreener(service);
        foreach (ScreenerIndicatorEntry entry in entries)
        {
            vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry);
        }

        await vm.LoadTargetSourcesAsync();
        await vm.UpdateTargetSymbolsAsync(vm.SelectedTargetSource);
        return (vm, service);
    }

    private static List<WatchlistColumnMetadata> IndicatorColumns(ScreenerViewModel vm) => vm.ActiveColumns.Where(column => column.MemberName.StartsWith("Indicator_", StringComparison.Ordinal)).ToList();

    private static WatchlistColumnMetadata IndicatorColumn(ScreenerViewModel vm) => Assert.Single(IndicatorColumns(vm));

    [Fact]
    public void TheTabOffersTheFramesOfTheSingleDefinitionOnEachSideAndHasNoFrameOfItsOwn()
    {
        var vm = new ScreenerViewModel(new RecordingScreenerService());

        Assert.Equal(DataServiceTimeFrames.Supported.ToArray(), vm.IndicatorRegistrationViewModel.AvailableTimeFrames.Select(frame => frame.Value).ToArray()); // the single definition, not a third copy
        Assert.Equal(
            new[] { "Enum_TimeFrame_D1", "Enum_TimeFrame_W1", "Enum_TimeFrame_MN1" }.Select(key => LocalizationManager.Instance[key]).ToArray(),
            vm.IndicatorRegistrationViewModel.AvailableTimeFrames.Select(frame => frame.DisplayName).ToArray());
        Assert.True(vm.CompleteBarsOnly);
        Assert.False(vm.IsCompleteBarsOptionVisible);
        Assert.DoesNotContain(typeof(ScreenerViewModel).GetProperties(), property => property.Name is "ScanTimeFrames" or "SelectedScanTimeFrame");
    }

    [Fact]
    public void TheCompleteBarsChoiceShowsOnlyWhileAnEnabledConditionReadsAWeeklyOrMonthlyBarList()
    {
        var vm = new ScreenerViewModel(new RecordingScreenerService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        ScreenerIndicatorEntry daily = Entry(PriceSide("Close", TimeFrame.D1));
        ScreenerIndicatorEntry weekly = Entry(PriceSide("Close", TimeFrame.W1));
        ScreenerIndicatorEntry monthlyRight = Entry(PriceSide("Close", TimeFrame.D1), PriceSide("Close", TimeFrame.MN1));
        var entries = vm.IndicatorRegistrationViewModel.RegisteredEntries;

        entries.Add(daily);
        Assert.False(vm.IsCompleteBarsOptionVisible);

        entries.Add(weekly);
        Assert.True(vm.IsCompleteBarsOptionVisible);
        Assert.Contains(nameof(ScreenerViewModel.IsCompleteBarsOptionVisible), changed);

        weekly.IsEnabled = false;
        Assert.False(vm.IsCompleteBarsOptionVisible);
        weekly.IsEnabled = true;
        Assert.True(vm.IsCompleteBarsOptionVisible);

        entries.Remove(weekly);
        Assert.False(vm.IsCompleteBarsOptionVisible);

        entries.Add(monthlyRight); // the right side of an indicator comparison counts
        Assert.True(vm.IsCompleteBarsOptionVisible);
        entries.Clear();
        Assert.False(vm.IsCompleteBarsOptionVisible);

        weekly.IsEnabled = false; // a removed entry is no longer watched
        weekly.IsEnabled = true;
        Assert.False(vm.IsCompleteBarsOptionVisible);
    }

    [Fact]
    public async Task ADailyScanIsTheUnchangedPlainScanAndReadsTheDailyBarsForEveryColumn()
    {
        (ScreenerViewModel vm, RecordingScreenerService service) = await ScreenerWithAsync(Entry(PriceSide("Close", TimeFrame.D1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { TimeFrame.D1 }, service.PlainScans);
        Assert.Empty(service.SideFrameScans);
        Assert.Empty(service.BarReads);
        WatchlistColumnMetadata column = IndicatorColumn(vm);
        Assert.DoesNotContain("[", column.HeaderKey, StringComparison.Ordinal);
        WatchlistItemViewModel item = Assert.Single(vm.ResultItems);
        Assert.Equal(DailyClose, item.Close);
        Assert.Equal(DailyClose, item.DynamicIndicatorValues[column.MemberName]);
    }

    [Theory]
    [InlineData(TimeFrame.W1, true)]
    [InlineData(TimeFrame.W1, false)]
    [InlineData(TimeFrame.MN1, true)]
    public async Task AConditionWithAWeeklyOrMonthlySideRunsOnTheSideFramesAndReadsItsColumnFromThoseBarsWhileTheFixedPriceColumnsStayDaily(TimeFrame frame, bool completeBarsOnly)
    {
        (ScreenerViewModel vm, RecordingScreenerService service) = await ScreenerWithAsync(Entry(PriceSide("Close", frame)));
        vm.CompleteBarsOnly = completeBarsOnly;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(service.PlainScans);
        (ScreenerBarSelection selection, TimeFrame[] scanned) = Assert.Single(service.SideFrameScans);
        Assert.Equal(new[] { TimeFrame.D1, frame }, scanned);
        Assert.Equal(new ScreenerBarSelection(null, completeBarsOnly), selection); // no date: the GUI evaluates the latest bars
        (string symbol, TimeFrame readFrame, ScreenerBarSelection readSelection, _) = Assert.Single(service.BarReads);
        Assert.Equal(("AAA", frame, selection), (symbol, readFrame, readSelection)); // the same bars the scan evaluated
        WatchlistColumnMetadata column = IndicatorColumn(vm);
        Assert.EndsWith($" [{frame}]", column.HeaderKey, StringComparison.Ordinal);
        WatchlistItemViewModel item = Assert.Single(vm.ResultItems);
        Assert.Equal(WeeklyClose, item.DynamicIndicatorValues[column.MemberName]); // the value the condition read
        Assert.Equal(DailyClose, item.Close); // the fixed price column is the latest daily bar
    }

    [Fact]
    public async Task EachSideKeepsItsOwnFrameAndAFrameListIsReadOncePerSymbol()
    {
        // Close(D1) > Close(W1), and Open(W1) > 0: three condition columns, two of them on the weekly bars.
        (ScreenerViewModel vm, RecordingScreenerService service) = await ScreenerWithAsync(
            Entry(PriceSide("Close", TimeFrame.D1), PriceSide("Close", TimeFrame.W1)),
            Entry(PriceSide("Open", TimeFrame.W1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1 }, Assert.Single(service.SideFrameScans).Frames);
        List<WatchlistColumnMetadata> columns = IndicatorColumns(vm);
        Assert.Equal(3, columns.Count);
        WatchlistItemViewModel item = Assert.Single(vm.ResultItems);
        WatchlistColumnMetadata dailyClose = Assert.Single(columns, column => column.HeaderKey.EndsWith("(Close)", StringComparison.Ordinal));
        WatchlistColumnMetadata weeklyClose = Assert.Single(columns, column => column.HeaderKey.EndsWith("(Close) [W1]", StringComparison.Ordinal));
        WatchlistColumnMetadata weeklyOpen = Assert.Single(columns, column => column.HeaderKey.EndsWith("(Open) [W1]", StringComparison.Ordinal));
        Assert.Equal(DailyClose, item.DynamicIndicatorValues[dailyClose.MemberName]);
        Assert.Equal(WeeklyClose, item.DynamicIndicatorValues[weeklyClose.MemberName]);
        Assert.Equal(WeeklyClose, item.DynamicIndicatorValues[weeklyOpen.MemberName]);
        Assert.Single(service.BarReads); // one weekly read for the two weekly columns
    }

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public async Task ABarValueColumnOfAWeeklyOrMonthlySideIsShownFromItsBarsWithTheFrameTitle(TimeFrame frame)
    {
        // A Column that names a bar value (Close) is evaluated on the side's bars, so the table must show that value, not the daily snapshot.
        (ScreenerViewModel vm, _) = await ScreenerWithAsync(Entry(ColumnSide("Close", frame)));

        await vm.ScanCommand.ExecuteAsync(null);

        WatchlistColumnMetadata column = IndicatorColumn(vm);
        Assert.EndsWith($" [{frame}]", column.HeaderKey, StringComparison.Ordinal);
        WatchlistItemViewModel item = Assert.Single(vm.ResultItems);
        Assert.Equal(WeeklyClose, item.DynamicIndicatorValues[column.MemberName]);
        Assert.Equal(DailyClose, item.Close); // the fixed column stays the daily snapshot
    }

    [Fact]
    public async Task ABarValueColumnOfADailySideStaysTheDailySnapshotAndAFundamentalColumnIsNeverFrameTitled()
    {
        (ScreenerViewModel daily, _) = await ScreenerWithAsync(Entry(ColumnSide("Close")));
        await daily.ScanCommand.ExecuteAsync(null);
        WatchlistColumnMetadata dailyColumn = IndicatorColumn(daily);
        Assert.DoesNotContain("[", dailyColumn.HeaderKey, StringComparison.Ordinal);
        Assert.Equal(DailyClose, Assert.Single(daily.ResultItems).DynamicIndicatorValues[dailyColumn.MemberName]);

        // a fundamental (a property of the ticker, not of a bar) has no time frame: an older entry that stored one is neither retitled nor re-read, and it keeps the plain scan
        (ScreenerViewModel fundamental, RecordingScreenerService service) = await ScreenerWithAsync(Entry(ColumnSide("MarketCap", TimeFrame.W1)));
        await fundamental.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { TimeFrame.D1 }, service.PlainScans);
        Assert.Empty(service.SideFrameScans);
        Assert.Empty(service.BarReads);
        WatchlistColumnMetadata fundamentalColumn = IndicatorColumn(fundamental);
        Assert.DoesNotContain("[", fundamentalColumn.HeaderKey, StringComparison.Ordinal);
        Assert.NotEqual(WeeklyClose, Assert.Single(fundamental.ResultItems).DynamicIndicatorValues[fundamentalColumn.MemberName]);
    }

    [Fact]
    public async Task ChangingTheFrameOfAConditionGivesItsColumnAnotherIdentitySoTheGridCannotKeepAStaleTitle()
    {
        (ScreenerViewModel vm, _) = await ScreenerWithAsync(Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        string daily = IndicatorColumn(vm).MemberName;

        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(Entry(PriceSide("Close", TimeFrame.W1)));
        await vm.ScanCommand.ExecuteAsync(null);
        string weekly = IndicatorColumn(vm).MemberName;

        Assert.NotEqual(daily, weekly);
        Assert.EndsWith(" [W1]", weekly, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusLineSaysWhichUnfinishedPeriodsWereLeftOutOrEvaluatedAndWhichSymbolsHadNoBarPerFrame()
    {
        (ScreenerViewModel vm, RecordingScreenerService service) = await ScreenerWithAsync(
            Entry(PriceSide("Close", TimeFrame.W1), PriceSide("Close", TimeFrame.MN1)));
        service.Answer = new ScreenerSideFramesResult(
            new[] { "AAA" },
            Array.Empty<string>(),
            new[]
            {
                new ScreenerFrameReport(TimeFrame.W1, new[] { "BBB" }, new[] { new DateOnly(2024, 1, 29) }, 3),
                new ScreenerFrameReport(TimeFrame.MN1, Array.Empty<string>(), Array.Empty<DateOnly>(), 2),
            });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains(string.Format(LocalizationManager.Instance["Screener_Status_ScanComplete"], 1), vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(string.Format(LocalizationManager.Instance["Screener_Scan_FrameNoteSkipped"], 3, LocalizationManager.Instance["Timeframe_Week"].ToLowerInvariant()), vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("2024-01-29", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(string.Format(LocalizationManager.Instance["Screener_Scan_FrameNoteNoBars"], 1), vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(string.Format(LocalizationManager.Instance["Screener_Scan_FrameNoteSkipped"], 2, LocalizationManager.Instance["Timeframe_Month"].ToLowerInvariant()), vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADailyScanAddsNoFrameNoteToTheStatusLine()
    {
        (ScreenerViewModel vm, _) = await ScreenerWithAsync(Entry(PriceSide("Close", TimeFrame.D1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(LocalizationManager.Instance["Screener_Status_ScanComplete"], 1), vm.StatusMessage);
    }

    [Fact]
    public async Task AScreenerServiceThatCannotScanSidesOnTheirFramesFailsLoudlyInsteadOfScanningDailyBars()
    {
        var service = new PlainScreenerService();
        ScreenerViewModel vm = NewScreener(service);
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(Entry(PriceSide("Close", TimeFrame.W1)));
        await vm.LoadTargetSourcesAsync();
        await vm.UpdateTargetSymbolsAsync(vm.SelectedTargetSource);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.StartsWith("Error:", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.Results);
        Assert.Empty(service.PlainScans);
    }

    // ---- indicators that compare two symbols (Correlation, CrossCorrelation) ---------------------------------------------------------------

    private const decimal CorrelationMarker = 0.5m;

    /// <summary>
    /// The plain scan (matches AAA) plus the two members a scan of two-symbol indicators uses: loading the comparison symbols' bars and the scan that is given them. The bars it
    /// serves are real <see cref="CoreCandleData"/> for the symbols in <see cref="Available"/> (any spelling of the same instrument); every call is recorded.
    /// </summary>
    private sealed class ComparisonScreenerService : PlainScreenerService, IScreenerService
    {
        public List<(string[] Spellings, TimeFrame Frame)> Loads { get; } = new();
        public List<IScreenerSecondaryCandles> SecondaryScans { get; } = new();
        public string[] Available { get; set; } = { "SPY" };
        public Exception? LoadFails { get; set; }
        public IScreenerSecondaryCandles? Carrier { get; private set; }

        public Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IScreenerSecondaryCandles? secondary, IProgress<int> progress, CancellationToken ct)
        {
            Assert.NotNull(secondary); // the 6-argument form is only called with a set; without one the tab keeps the 5-argument form
            SecondaryScans.Add(secondary!);
            return Task.FromResult(new List<string> { "AAA" });
        }

        public Task<IScreenerSecondaryCandles> LoadComparisonCandlesAsync(IReadOnlyList<string> spellings, TimeFrame timeFrame, IScreeningCondition condition, CancellationToken ct)
        {
            Loads.Add((spellings.ToArray(), timeFrame));
            if (LoadFails != null)
            {
                throw LoadFails;
            }

            var bars = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
            foreach (string spelling in spellings.Where(s => Available.Any(a => StockAnalyzer.Core.Common.SymbolHelper.IsSameSymbol(a, s))))
            {
                bars[spelling] = new CoreCandleData?[] { new CoreCandleData(new DateTime(2024, 1, 31), 1m, 1m, 1m, 1m, 1) };
            }

            Carrier = new ScreenerSecondaryCandles(bars);
            return Task.FromResult(Carrier);
        }
    }

    /// <summary>A value extractor that returns <see cref="CorrelationMarker"/> for every indicator side and records the comparison set each call was given.</summary>
    private sealed class SecondaryRecordingExtractor : IScreenerValueExtractor
    {
        public List<IScreenerSecondaryCandles?> Seen { get; } = new();

        public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles) => 0m;

        public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default) => 0m;

        public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
        {
            Seen.Add(null);
            return CorrelationMarker;
        }

        public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
        {
            Seen.Add(secondary);
            return CorrelationMarker;
        }
    }

    private static ScreenerIndicatorSideConfig CorrelationSide(string? symbol, TimeFrame frame = TimeFrame.D1)
    {
        var side = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Correlation,
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = frame,
            Parameters = new Dictionary<string, object> { ["Period"] = 20 },
        };
        if (symbol != null)
        {
            side.Parameters["ComparisonSymbol"] = symbol;
        }

        return side;
    }

    private static async Task<(ScreenerViewModel Vm, ComparisonScreenerService Service, SecondaryRecordingExtractor Extractor)> TwoSymbolScreenerAsync(params ScreenerIndicatorEntry[] entries)
    {
        var service = new ComparisonScreenerService();
        var extractor = new SecondaryRecordingExtractor();
        var market = new DummyMarketDataProvider(new List<string> { "AAA", "BBB" });
        market.Candles["AAA"] = new[]
        {
            new CandleData(new DateTime(2024, 1, 30), 100m, 100m, 100m, 100m, 5),
            new CandleData(new DateTime(2024, 1, 31), DailyClose, DailyClose, DailyClose, DailyClose, 5),
        };
        var vm = new ScreenerViewModel(service, null, market, null, valueExtractor: extractor);
        foreach (ScreenerIndicatorEntry entry in entries)
        {
            vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry);
        }

        await vm.LoadTargetSourcesAsync();
        await vm.UpdateTargetSymbolsAsync(vm.SelectedTargetSource);
        return (vm, service, extractor);
    }

    private static void Replace(ScreenerViewModel vm, params ScreenerIndicatorEntry[] entries)
    {
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        foreach (ScreenerIndicatorEntry entry in entries)
        {
            vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(entry);
        }
    }

    private static string Text(string key, params object[] args) => string.Format(LocalizationManager.Instance[key], args);

    [Fact]
    public async Task ANamedComparisonSymbolIsLoadedOnceAndTheScanIsGivenItsBars()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(CorrelationSide("spy")));

        await vm.ScanCommand.ExecuteAsync(null);

        (string[] spellings, TimeFrame frame) = Assert.Single(service.Loads);
        Assert.Equal(new[] { "SPY" }, spellings);
        Assert.Equal(TimeFrame.D1, frame);
        Assert.Same(service.Carrier, Assert.Single(service.SecondaryScans));
        Assert.Empty(service.PlainScans); // the 5-argument form is not used when there are comparison bars
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task WithoutANamedComparisonSymbolTheScanIsTheUnchangedPlainScanAndNothingIsLoaded()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(CorrelationSide(null)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(new[] { TimeFrame.D1 }, service.PlainScans);
        Assert.Empty(service.Loads);
        Assert.Empty(service.SecondaryScans);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
    }

    [Fact]
    public async Task ADisabledConditionNeverCausesALoad()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(
            Entry(PriceSide("Close", TimeFrame.D1)),
            Entry(CorrelationSide("SPY"), enabled: false));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(service.Loads);
        Assert.Equal(new[] { TimeFrame.D1 }, service.PlainScans);
    }

    [Fact]
    public async Task TwoSpellingsOfOneInstrumentAreOneLoadWithBothSpellings()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(CorrelationSide("7203.T")), Entry(CorrelationSide("7203-T")));
        service.Available = new[] { "7203.T" };

        await vm.ScanCommand.ExecuteAsync(null);

        (string[] spellings, _) = Assert.Single(service.Loads);
        Assert.Equal(new[] { "7203.T", "7203-T" }, spellings);
        Assert.Single(service.SecondaryScans);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
    }

    [Theory]
    [InlineData(TimeFrame.W1, "SPY")]
    [InlineData(TimeFrame.MN1, "SPY")]
    [InlineData(TimeFrame.W1, null)]
    public async Task ATwoSymbolSideOnWeeklyOrMonthlyBarsIsRefusedBeforeAnythingIsReadAndThePreviousResultStays(TimeFrame frame, string? symbol)
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Replace(vm, Entry(CorrelationSide(symbol, frame)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Text("Screener_Status_TwoTickerOnlyDaily", "Correlation"), vm.StatusMessage);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Assert.Single(service.PlainScans); // only the first scan
        Assert.Empty(service.Loads);
        Assert.Empty(service.SecondaryScans);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ANamedComparisonSymbolTogetherWithAWeeklySideIsRefusedBeforeAnythingIsRead()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(
            Entry(CorrelationSide("SPY")),
            Entry(PriceSide("Close", TimeFrame.W1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Text("Screener_Status_TwoTickerWithOtherFrames"), vm.StatusMessage);
        Assert.Empty(service.Loads);
        Assert.Empty(service.PlainScans);
        Assert.Empty(service.SecondaryScans);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ABlankDailyTwoSymbolSideMayBeCombinedWithAWeeklySide()
    {
        // Price vs Volume needs no comparison bars: the combination keeps running as the side-frames scan.
        (ScreenerViewModel vm, _, _) = await TwoSymbolScreenerAsync(Entry(CorrelationSide(null)), Entry(PriceSide("Close", TimeFrame.W1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.NotEqual(Text("Screener_Status_TwoTickerWithOtherFrames"), vm.StatusMessage);
        Assert.NotEqual(Text("Screener_Status_TwoTickerOnlyDaily", "Correlation"), vm.StatusMessage);
        // It reached the side-frames scan, which this fake (like every plain service) refuses loudly: the combination was not refused by the tab.
        Assert.StartsWith("Error:", vm.StatusMessage, StringComparison.Ordinal);
    }

    // ---- a type that NEEDS a comparison symbol, with none typed (G-1 / G1-T5) ---------------------------------------------------------

    /// <summary>A two-symbol side of a type that cannot give a value without a comparison symbol (the first one the registry has; today RollingBeta and its kin).</summary>
    private static ScreenerIndicatorSideConfig RequiredTypeSide(string? symbol, TimeFrame frame = TimeFrame.D1)
    {
        IndicatorType type = IndicatorFactory.Default.GetRegisteredTypes()
            .Where(t => IndicatorFactory.CreateStatic(t) is ICrossTickerIndicator && !ScreenerComparisonSymbols.IsComparisonSymbolOptional(t))
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .First();
        var side = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            CategoryType = ScreenerItemCategoryType.Indicator,
            OutputName = IndicatorResult.MainSeriesName,
            TimeFrame = frame,
            Parameters = new Dictionary<string, object>(),
        };
        if (symbol != null)
        {
            side.Parameters["ComparisonSymbol"] = symbol;
        }

        return side;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARequiredTypeWithoutAComparisonSymbolIsRefusedBeforeAnythingIsReadAndThePreviousResultStays(string? symbol)
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        ScreenerIndicatorSideConfig side = RequiredTypeSide(symbol);
        Replace(vm, Entry(side));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Text("Screener_Status_TwoTickerNeedsSymbol", side.DisplayName), vm.StatusMessage);
        Assert.Contains(side.IndicatorType.ToString(), vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Assert.Single(service.PlainScans); // only the first scan
        Assert.Empty(service.Loads);
        Assert.Empty(service.SecondaryScans);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ARequiredTypeWithAComparisonSymbolIsLoadedAndScannedLikeCorrelation()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(RequiredTypeSide("SPY")));

        await vm.ScanCommand.ExecuteAsync(null);

        (string[] spellings, _) = Assert.Single(service.Loads);
        Assert.Equal(new[] { "SPY" }, spellings);
        Assert.Single(service.SecondaryScans);
        Assert.NotEqual(Text("Screener_Status_TwoTickerNeedsSymbol", RequiredTypeSide("SPY").DisplayName), vm.StatusMessage);
    }

    [Fact]
    public async Task TheDailyQuestionComesFirst_ThenTheMissingSymbol_ThenTheOtherFrames()
    {
        // A required side without a symbol on weekly bars: the frame is the first fault.
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(RequiredTypeSide(null, TimeFrame.W1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(Text("Screener_Status_TwoTickerOnlyDaily", RequiredTypeSide(null).DisplayName), vm.StatusMessage);

        // Daily and without a symbol, next to a weekly side: the missing symbol is named before the frame mix is judged.
        Replace(vm, Entry(RequiredTypeSide(null)), Entry(PriceSide("Close", TimeFrame.W1)), Entry(CorrelationSide("SPY")));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(Text("Screener_Status_TwoTickerNeedsSymbol", RequiredTypeSide(null).DisplayName), vm.StatusMessage);

        Assert.Empty(service.Loads);
        Assert.Empty(service.PlainScans);
        Assert.Empty(service.SecondaryScans);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ADisabledRequiredSideWithoutASymbolDoesNotStopTheScan()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(
            Entry(PriceSide("Close", TimeFrame.D1)),
            Entry(RequiredTypeSide(null), enabled: false));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(service.PlainScans);
        Assert.NotEqual(Text("Screener_Status_TwoTickerNeedsSymbol", RequiredTypeSide(null).DisplayName), vm.StatusMessage);
    }

    [Fact]
    public async Task MoreThanTheMostComparisonSymbolsIsRefusedBeforeAnythingIsRead()
    {
        int most = WatchlistConstants.MaxRelativePerformanceTargets;
        ScreenerIndicatorEntry[] entries = Enumerable.Range(1, most + 1).Select(i => Entry(CorrelationSide($"S{i:D2}"))).ToArray();
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(entries);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Text("Screener_Status_TwoTickerTooManySymbols", most), vm.StatusMessage);
        Assert.Empty(service.Loads);
        Assert.Empty(service.SecondaryScans);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task TheMostComparisonSymbolsIsStillAccepted()
    {
        int most = WatchlistConstants.MaxRelativePerformanceTargets;
        ScreenerIndicatorEntry[] entries = Enumerable.Range(1, most).Select(i => Entry(CorrelationSide($"S{i:D2}"))).ToArray();
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(entries);
        service.Available = Enumerable.Range(1, most).Select(i => $"S{i:D2}").ToArray();

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(most, Assert.Single(service.Loads).Spellings.Length);
        Assert.Single(service.SecondaryScans);
    }

    [Fact]
    public async Task ASymbolWithoutBarsStopsTheScanNamingEveryMissingSymbolAndLeavesThePreviousResultAndTheScanFlag()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Replace(vm, Entry(CorrelationSide("SPY")), Entry(CorrelationSide("ZZZZ")), Entry(CorrelationSide("YYYY")));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Text("Screener_Status_ComparisonSymbolMissing", "ZZZZ, YYYY"), vm.StatusMessage);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray()); // the previous result is untouched
        Assert.Single(vm.ResultItems);
        Assert.Empty(service.SecondaryScans);
        Assert.Single(service.PlainScans); // only the first scan
        Assert.False(vm.IsScanning);
        Assert.True(vm.IsNotScanning);

        // the tab is not stuck: the next scan runs
        Replace(vm, Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(2, service.PlainScans.Count);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task AFailureWhileLoadingTheComparisonBarsLeavesThePreviousResultAndTheScanFlag()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, _) = await TwoSymbolScreenerAsync(Entry(PriceSide("Close", TimeFrame.D1)));
        await vm.ScanCommand.ExecuteAsync(null);
        Replace(vm, Entry(CorrelationSide("SPY")));
        service.LoadFails = new InvalidOperationException("boom");

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.StartsWith("Error:", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(new[] { "AAA" }, vm.Results.ToArray());
        Assert.Empty(service.SecondaryScans);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task TheResultColumnOfATwoSymbolSideIsReadWithTheSameComparisonBarsTheScanUsed()
    {
        (ScreenerViewModel vm, ComparisonScreenerService service, SecondaryRecordingExtractor extractor) = await TwoSymbolScreenerAsync(Entry(CorrelationSide("SPY")));

        await vm.ScanCommand.ExecuteAsync(null);

        WatchlistColumnMetadata column = IndicatorColumn(vm);
        WatchlistItemViewModel item = Assert.Single(vm.ResultItems);
        Assert.Equal(CorrelationMarker, item.DynamicIndicatorValues[column.MemberName]);
        Assert.Same(service.Carrier, Assert.Single(extractor.Seen));
        Assert.Same(service.Carrier, Assert.Single(service.SecondaryScans));
    }

    [Fact]
    public async Task APlainScanGivesTheResultColumnNoComparisonBars()
    {
        (ScreenerViewModel vm, _, SecondaryRecordingExtractor extractor) = await TwoSymbolScreenerAsync(Entry(PriceSide("Close", TimeFrame.D1)));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(extractor.Seen));
    }
}
