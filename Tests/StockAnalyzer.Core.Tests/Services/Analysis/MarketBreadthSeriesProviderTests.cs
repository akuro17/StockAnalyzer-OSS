#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Services.Analysis;

public sealed class MarketBreadthSeriesProviderTests
{
    private readonly ITestOutputHelper _output;

    public MarketBreadthSeriesProviderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class StubGroupResolver : ITickerGroupResolver
    {
        private IReadOnlyList<string> _symbols;

        public int ResolveCallCount { get; private set; }

        /// <summary>Replaces the members the next resolution returns (the saved workspace was edited).</summary>
        public void SetSymbols(IReadOnlyList<string> symbols) => _symbols = symbols;

        public StubGroupResolver(IReadOnlyList<string> symbols)
        {
            _symbols = symbols;
        }

        public Task<GroupMembers> ResolveAsync(Guid groupId, int maxFilterEvaluationSymbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCallCount++;
            return Task.FromResult(new GroupMembers(
                _symbols,
                UnregisteredCount: 0,
                UnknownRuleFields: Array.Empty<string>(),
                Kind: TickerGroupKind.Watchlist));
        }
    }

    private sealed class StubDataService : IDataService
    {
        private readonly IReadOnlyDictionary<(string Symbol, TimeFrame TimeFrame), IReadOnlyList<CandleData>> _series;
        private readonly Func<string, Exception>? _exceptionFactory;

        public int LoadCallCount { get; private set; }

        public StubDataService(
            IReadOnlyDictionary<(string Symbol, TimeFrame TimeFrame), IReadOnlyList<CandleData>> series,
            Func<string, Exception>? exceptionFactory = null)
        {
            _series = series;
            _exceptionFactory = exceptionFactory;
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
            => LoadCandlesAsync(symbol, timeFrame, count, includePartialBar: true, CancellationToken.None);

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(
            string symbol,
            TimeFrame timeFrame,
            int count,
            bool includePartialBar,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCallCount++;

            if (_exceptionFactory is not null)
            {
                throw _exceptionFactory(symbol);
            }

            if (_series.TryGetValue((symbol, timeFrame), out IReadOnlyList<CandleData>? candles))
            {
                if (count > 0 && candles.Count > count)
                {
                    return Task.FromResult<IReadOnlyList<CandleData>>(candles.Skip(candles.Count - count).ToArray());
                }
                return Task.FromResult(candles);
            }

            return Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static List<CandleData> BuildTradingSeries(DateTime startDate, int barCount, decimal basePrice, decimal step, long baseVolume)
    {
        var candles = new List<CandleData>(barCount);
        DateTime date = startDate;
        while (candles.Count < barCount)
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                int i = candles.Count;
                decimal close = basePrice + step * i + ((i % 3) - 1) * 0.5m;
                decimal open = close - step * 0.4m;
                decimal high = Math.Max(open, close) + 1.0m;
                decimal low = Math.Min(open, close) - 1.0m;
                candles.Add(new CandleData(date, open, high, low, close, baseVolume + i * 100));
            }
            date = date.AddDays(1);
        }

        return candles;
    }

    [Fact]
    public async Task GetAsync_ThreeSymbols_MatchesDirectCalculatorResult()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var start = new DateTime(2025, 1, 2);
        var d1Data = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = BuildTradingSeries(start, 35, 150m, 0.8m, 1_000_000),
            ["MSFT"] = BuildTradingSeries(start, 35, 300m, -0.3m, 800_000),
            ["NVDA"] = BuildTradingSeries(start, 35, 100m, 1.2m, 2_000_000)
        };

        var store = d1Data.ToDictionary(kv => (kv.Key, TimeFrame.D1), kv => kv.Value);
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(store);
        var provider = new MarketBreadthSeriesProvider(resolver, dataService);

        // Deliberately supply caller overrides for OutputTailCount/IncludeSeries/TimeFrame;
        // provider must calculate full-history series for the requested timeFrame argument.
        var callerParams = new MarketBreadthParameters
        {
            AdRatioBars = 5,
            McClellanFast = 6,
            McClellanSlow = 12,
            MaBars = 10,
            HighLowBars = 15,
            OutputTailCount = 5,
            IncludeSeries = new HashSet<string> { "adLine" },
            TimeFrame = TimeFrame.MN1
        };

        MarketBreadthResult? actual = await provider.GetAsync(groupId, TimeFrame.D1, callerParams, CancellationToken.None);

        MarketBreadthResult expected = MarketBreadthCalculator.Calculate(
            d1Data,
            new MarketBreadthParameters
            {
                AdRatioBars = 5,
                McClellanFast = 6,
                McClellanSlow = 12,
                MaBars = 10,
                HighLowBars = 15,
                OutputTailCount = null,
                IncludeSeries = null,
                TimeFrame = TimeFrame.D1
            });

        Assert.NotNull(actual);
        Assert.Equal(expected.DateCount, actual.DateCount);
        Assert.Equal(expected.SymbolCount, actual.SymbolCount);
        Assert.Equal(expected.Series.Dates, actual.Series.Dates);
        Assert.Equal(expected.Series.Advancers, actual.Series.Advancers);
        Assert.Equal(expected.Series.Decliners, actual.Series.Decliners);
        Assert.Equal(expected.Series.AdLine, actual.Series.AdLine);
        Assert.Equal(expected.Series.AdRatio, actual.Series.AdRatio);
        Assert.Equal(expected.Series.McClellan, actual.Series.McClellan);
        Assert.Equal(expected.Series.SummationIndex, actual.Series.SummationIndex);
        Assert.Equal(expected.Series.PctAboveMa, actual.Series.PctAboveMa);
        Assert.Equal(expected.Series.NewHighs, actual.Series.NewHighs);
        Assert.Equal(expected.Series.NewLows, actual.Series.NewLows);
        Assert.Equal(expected.Series.NetNewHighs, actual.Series.NetNewHighs);
        Assert.Equal(expected.Series.Trin, actual.Series.Trin);
        Assert.Equal(expected.Series.Cvi, actual.Series.Cvi);
    }

    [Fact]
    public async Task GetAsync_WeeklyFromDailyFallback_MatchesAggregatedCalculatorResult()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var start = new DateTime(2025, 1, 6);
        var d1Data = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = BuildTradingSeries(start, 60, 120m, 0.5m, 500_000),
            ["MSFT"] = BuildTradingSeries(start, 60, 220m, 0.2m, 400_000),
            ["NVDA"] = BuildTradingSeries(start, 60, 90m, -0.1m, 700_000)
        };

        var store = d1Data.ToDictionary(kv => (kv.Key, TimeFrame.D1), kv => kv.Value);
        var provider = new MarketBreadthSeriesProvider(new StubGroupResolver(symbols), new StubDataService(store));

        var parameters = new MarketBreadthParameters
        {
            AdRatioBars = 3,
            McClellanFast = 3,
            McClellanSlow = 6,
            MaBars = 4,
            HighLowBars = 5
        };

        MarketBreadthResult? actual = await provider.GetAsync(groupId, TimeFrame.W1, parameters, CancellationToken.None);

        var weeklyUniverse = d1Data.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<CandleData>)TimeFrameAggregator.AggregateToPeriodStart(kv.Value, TimeFrame.W1),
            StringComparer.OrdinalIgnoreCase);

        MarketBreadthResult expected = MarketBreadthCalculator.Calculate(
            weeklyUniverse,
            new MarketBreadthParameters
            {
                AdRatioBars = 3,
                McClellanFast = 3,
                McClellanSlow = 6,
                MaBars = 4,
                HighLowBars = 5,
                TimeFrame = TimeFrame.W1
            });

        Assert.NotNull(actual);
        Assert.Equal(expected.DateCount, actual.DateCount);
        Assert.Equal(expected.Series.Dates, actual.Series.Dates);
        Assert.Equal(expected.Series.AdRatio, actual.Series.AdRatio);
        Assert.Equal(expected.Series.PctAboveMa, actual.Series.PctAboveMa);
    }

    [Fact]
    public async Task GetAsync_SameCacheKeySecondCall_DoesNotReloadData()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var start = new DateTime(2025, 2, 3);
        var store = symbols.ToDictionary(
            s => (s, TimeFrame.D1),
            s => (IReadOnlyList<CandleData>)BuildTradingSeries(start, 20, 100m, 0.4m, 100_000));

        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(store);
        var provider = new MarketBreadthSeriesProvider(resolver, dataService);
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        MarketBreadthResult? first = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        Assert.NotNull(first);
        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Equal(symbols.Length, dataService.LoadCallCount);

        MarketBreadthResult? second = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        Assert.Same(first, second);
        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Equal(symbols.Length, dataService.LoadCallCount);

        // Changing a cache key dimension triggers fresh calculation.
        MarketBreadthResult? differentParams = await provider.GetAsync(
            groupId,
            TimeFrame.D1,
            new MarketBreadthParameters { MaBars = 6, HighLowBars = 5 });
        Assert.NotNull(differentParams);
        Assert.Equal(2, resolver.ResolveCallCount);
        Assert.Equal(symbols.Length * 2, dataService.LoadCallCount);

        // ClearCache drops cached entries.
        provider.ClearCache();
        MarketBreadthResult? afterClear = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        Assert.NotNull(afterClear);
        Assert.NotSame(first, afterClear);
        Assert.Equal(3, resolver.ResolveCallCount);
    }

    [Fact]
    public async Task GetAsync_MoreThanDefaultCacheCapacity_EvictsLeastRecentlyUsed()
    {
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var store = symbols.ToDictionary(
            s => (s, TimeFrame.D1),
            s => (IReadOnlyList<CandleData>)BuildTradingSeries(new DateTime(2025, 1, 2), 20, 100m, 0.2m, 100_000));

        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(store);
        var provider = new MarketBreadthSeriesProvider(resolver, dataService);

        var groupIds = Enumerable.Range(0, MarketBreadthSeriesProvider.DefaultCacheCapacity + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        foreach (Guid id in groupIds)
        {
            Assert.NotNull(await provider.GetAsync(id, TimeFrame.D1, parameters));
        }

        int callsAfterWarmup = resolver.ResolveCallCount;
        Assert.Equal(MarketBreadthSeriesProvider.DefaultCacheCapacity + 1, callsAfterWarmup);

        // Re-reading groupIds[1] (still in capacity-8 LRU) must hit cache.
        await provider.GetAsync(groupIds[1], TimeFrame.D1, parameters);
        Assert.Equal(callsAfterWarmup, resolver.ResolveCallCount);

        // Re-reading groupIds[0] (evicted) must miss and reload.
        await provider.GetAsync(groupIds[0], TimeFrame.D1, parameters);
        Assert.Equal(callsAfterWarmup + 1, resolver.ResolveCallCount);
    }

    [Fact]
    public async Task GetAsync_EmptyGroupOrEmptyGuid_ReturnsNullWithoutLoadingCandles()
    {
        var resolver = new StubGroupResolver(Array.Empty<string>());
        var dataService = new StubDataService(new Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>>());
        var provider = new MarketBreadthSeriesProvider(resolver, dataService);
        var parameters = new MarketBreadthParameters();

        Assert.Null(await provider.GetAsync(Guid.Empty, TimeFrame.D1, parameters));
        Assert.Equal(0, resolver.ResolveCallCount);

        Assert.Null(await provider.GetAsync(Guid.NewGuid(), TimeFrame.D1, parameters));
        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Equal(0, dataService.LoadCallCount);
    }

    [Fact]
    public async Task GetAsync_ExceedingSymbolLimit_LogsSingleLineAndReturnsNull()
    {
        Assert.Equal(500, MarketBreadthSeriesProvider.DefaultMaxBreadthSymbols);

        var symbols = new[] { "A", "B", "C", "D" };
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(new Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>>());
        var logger = new CapturingLogger<MarketBreadthSeriesProvider>();
        var provider = new MarketBreadthSeriesProvider(
            resolver,
            dataService,
            logger: logger,
            maxBreadthSymbols: 3);

        MarketBreadthResult? result = await provider.GetAsync(Guid.NewGuid(), TimeFrame.D1, new MarketBreadthParameters());

        Assert.Null(result);
        Assert.Equal(0, dataService.LoadCallCount);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("4", warning.Message);
        Assert.Contains("3", warning.Message);
    }

    [Fact]
    public async Task GetAsync_WhenDataLoadThrows_LogsSingleLineAndReturnsNull()
    {
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(
            new Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>>(),
            _ => new InvalidOperationException("Corrupted parquet read"));
        var logger = new CapturingLogger<MarketBreadthSeriesProvider>();
        var provider = new MarketBreadthSeriesProvider(resolver, dataService, logger: logger);

        MarketBreadthResult? result = await provider.GetAsync(Guid.NewGuid(), TimeFrame.D1, new MarketBreadthParameters());

        Assert.Null(result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("3", warning.Message);
        Assert.Contains(nameof(InvalidOperationException), warning.Message);
    }

    [Fact]
    public async Task GetAsync_WhenCancelled_PropagatesOperationCanceledException()
    {
        var symbols = new[] { "AAPL", "MSFT" };
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(new Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>>());
        var provider = new MarketBreadthSeriesProvider(resolver, dataService);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetAsync(Guid.NewGuid(), TimeFrame.D1, new MarketBreadthParameters(), cts.Token));
    }

    [Fact]
    public void ReadOnlyCoreServiceCollectionExtensions_RegistersMarketBreadthSeriesProvider()
    {
        var services = new ServiceCollection();
        services.AddStockAnalyzerReadOnlyCore(new ConfigurationBuilder().Build());

        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMarketBreadthSeriesProvider));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(MarketBreadthSeriesProvider), descriptor.ImplementationType);
    }

    [Fact]
    public async Task GetAsync_FiftySymbolsTimingBaseline_ReportsElapsedTime()
    {
        var groupId = Guid.NewGuid();
        var symbols = Enumerable.Range(1, 50).Select(i => $"SYM{i:D3}").ToArray();
        var start = new DateTime(2024, 1, 2);
        var store = symbols.ToDictionary(
            s => (s, TimeFrame.D1),
            s => (IReadOnlyList<CandleData>)BuildTradingSeries(start, 250, 100m, 0.25m, 100_000));

        var provider = new MarketBreadthSeriesProvider(new StubGroupResolver(symbols), new StubDataService(store));
        var parameters = new MarketBreadthParameters();

        var sw = Stopwatch.StartNew();
        MarketBreadthResult? uncached = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        sw.Stop();
        double coldMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        MarketBreadthResult? cached = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        sw.Stop();
        double warmMs = sw.Elapsed.TotalMilliseconds;

        Assert.NotNull(uncached);
        Assert.Same(uncached, cached);
        Assert.Equal(50, uncached.SymbolCount);
        _output.WriteLine($"50 symbols x 250 bars: cold={coldMs:F2} ms, warm={warmMs:F2} ms");
    }

    [Fact]
    public async Task GetAsync_WeeklyWithEndDate_TrimsDailyBarsBeforeAggregationAndExcludesPostCutoffDays()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT" };
        // Three Mon-Fri weeks: 2025-01-06..2025-01-24.
        // In week 3 (2025-01-20..24), Mon-Wed advance (+2 per day) while Thu-Fri plunge (-30 per day),
        // so the Wednesday cutoff bar is advancing whereas the full week bar is strongly declining.
        List<CandleData> BuildWeekPattern(decimal basePrice)
        {
            var dates = new[]
            {
                new DateTime(2025, 1, 6), new DateTime(2025, 1, 7), new DateTime(2025, 1, 8), new DateTime(2025, 1, 9), new DateTime(2025, 1, 10),
                new DateTime(2025, 1, 13), new DateTime(2025, 1, 14), new DateTime(2025, 1, 15), new DateTime(2025, 1, 16), new DateTime(2025, 1, 17),
                new DateTime(2025, 1, 20), new DateTime(2025, 1, 21), new DateTime(2025, 1, 22), new DateTime(2025, 1, 23), new DateTime(2025, 1, 24)
            };
            var closes = new[]
            {
                basePrice, basePrice + 1m, basePrice + 2m, basePrice + 3m, basePrice + 4m,
                basePrice + 5m, basePrice + 6m, basePrice + 7m, basePrice + 8m, basePrice + 9m,
                basePrice + 11m, basePrice + 13m, basePrice + 15m, basePrice - 20m, basePrice - 50m
            };
            return dates.Zip(closes, (d, c) => new CandleData(d, c - 0.5m, c + 1m, c - 1m, c, 100_000)).ToList();
        }

        var d1Data = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = BuildWeekPattern(100m),
            ["MSFT"] = BuildWeekPattern(200m)
        };

        // Populate conflicting full-week pre-aggregated W1 data to prove EndDate reads D1 and trims before aggregating.
        var store = new Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>>();
        foreach ((string sym, IReadOnlyList<CandleData> daily) in d1Data)
        {
            store[(sym, TimeFrame.D1)] = daily;
            store[(sym, TimeFrame.W1)] = TimeFrameAggregator.AggregateToPeriodStart(daily, TimeFrame.W1);
        }

        var provider = new MarketBreadthSeriesProvider(new StubGroupResolver(symbols), new StubDataService(store));
        var cutoffWednesday = new DateOnly(2025, 1, 22);

        MarketBreadthResult? asOfResult = await provider.GetAsync(
            groupId,
            TimeFrame.W1,
            new MarketBreadthParameters
            {
                AdRatioBars = 2,
                McClellanFast = 2,
                McClellanSlow = 3,
                MaBars = 2,
                HighLowBars = 2,
                EndDate = cutoffWednesday
            });

        MarketBreadthResult? fullResult = await provider.GetAsync(
            groupId,
            TimeFrame.W1,
            new MarketBreadthParameters
            {
                AdRatioBars = 2,
                McClellanFast = 2,
                McClellanSlow = 3,
                MaBars = 2,
                HighLowBars = 2,
                EndDate = null
            });

        Assert.NotNull(asOfResult);
        Assert.NotNull(fullResult);
        Assert.NotSame(asOfResult, fullResult);
        Assert.Equal(3, asOfResult!.DateCount);
        Assert.Equal("2025-01-20", asOfResult.Series.Dates[^1]);
        // At Wednesday cutoff both symbols are up (+15 vs +9 previous week close), excluding Thu/Fri plunge:
        Assert.Equal(2, asOfResult.Series.Advancers[^1]);
        Assert.Equal(0, asOfResult.Series.Decliners[^1]);
        // Uncut weekly bar reflects full week Friday close (-50 vs +9 previous week close):
        Assert.Equal(0, fullResult!.Series.Advancers[^1]);
        Assert.Equal(2, fullResult.Series.Decliners[^1]);
    }

    private static Dictionary<(string, TimeFrame), IReadOnlyList<CandleData>> DailyStore(IEnumerable<string> symbols)
        => symbols.ToDictionary(
            s => (s, TimeFrame.D1),
            s => (IReadOnlyList<CandleData>)BuildTradingSeries(new DateTime(2025, 2, 3), 20, 100m, 0.4m, 100_000));

    [Fact]
    public async Task GetAsync_WhenTheSavedWorkspaceChanges_ReturnsTheBreadthOfTheNewMembers()
    {
        var groupId = Guid.NewGuid();
        var threeSymbols = new[] { "AAPL", "MSFT", "NVDA" };
        var fourSymbols = new[] { "AAPL", "MSFT", "NVDA", "TSLA" };
        var workspace = new VersionedWorkspaceSource { Version = "v1" };
        var resolver = new StubGroupResolver(threeSymbols);
        var dataService = new StubDataService(DailyStore(fourSymbols));
        var provider = new MarketBreadthSeriesProvider(resolver, dataService, workspaceSource: workspace);
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        MarketBreadthResult? before = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        Assert.Equal(3, before!.SymbolCount);

        // A fourth symbol is added to the watchlist and the workspace is saved: the file has a new version.
        resolver.SetSymbols(fourSymbols);
        workspace.Version = "v2";
        MarketBreadthResult? after = await provider.GetAsync(groupId, TimeFrame.D1, parameters);

        Assert.Equal(4, after!.SymbolCount);
        Assert.Equal(2, resolver.ResolveCallCount);
    }

    [Fact]
    public async Task GetAsync_WhenTheWorkspaceVersionIsUnchanged_DoesNotResolveOrReloadAgain()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var workspace = new VersionedWorkspaceSource { Version = "v1" };
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(DailyStore(symbols));
        var provider = new MarketBreadthSeriesProvider(resolver, dataService, workspaceSource: workspace);
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        MarketBreadthResult? first = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        MarketBreadthResult? second = await provider.GetAsync(groupId, TimeFrame.D1, parameters);

        Assert.Same(first, second);
        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Equal(symbols.Length, dataService.LoadCallCount);
    }

    [Fact]
    public async Task GetAsync_WhenTheSourceCannotTellAVersion_KeepsTheCachingOfTheGroupAndParameters()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var resolver = new StubGroupResolver(symbols);
        var dataService = new StubDataService(DailyStore(symbols));
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        // No source at all, a source that keeps the default (null) version, and a source that returns null: all key the cache as before.
        foreach (IReadOnlyWorkspaceSource? source in new IReadOnlyWorkspaceSource?[] { null, new LoadOnlyWorkspaceSource(), new VersionedWorkspaceSource() })
        {
            resolver = new StubGroupResolver(symbols);
            var provider = new MarketBreadthSeriesProvider(resolver, dataService, workspaceSource: source);

            MarketBreadthResult? first = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
            MarketBreadthResult? second = await provider.GetAsync(groupId, TimeFrame.D1, parameters);

            Assert.Same(first, second);
            Assert.Equal(1, resolver.ResolveCallCount);
        }
    }

    [Fact]
    public async Task GetAsync_AfterClearCache_RecomputesEvenWithTheSameWorkspaceVersion()
    {
        var groupId = Guid.NewGuid();
        var symbols = new[] { "AAPL", "MSFT", "NVDA" };
        var resolver = new StubGroupResolver(symbols);
        var provider = new MarketBreadthSeriesProvider(
            resolver, new StubDataService(DailyStore(symbols)), workspaceSource: new VersionedWorkspaceSource { Version = "v1" });
        var parameters = new MarketBreadthParameters { MaBars = 5, HighLowBars = 5 };

        MarketBreadthResult? first = await provider.GetAsync(groupId, TimeFrame.D1, parameters);
        provider.ClearCache();
        MarketBreadthResult? second = await provider.GetAsync(groupId, TimeFrame.D1, parameters);

        Assert.NotSame(first, second);
        Assert.Equal(2, resolver.ResolveCallCount);
    }
}
