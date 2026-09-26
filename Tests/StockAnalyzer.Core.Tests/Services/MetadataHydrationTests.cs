using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class MetadataHydrationTests
{
    // Regression test (sa_minimal_fix): ParquetMarketDataProvider used to unconditionally register
    // itself into UserStrategyMetadataRepository's persistence path via a process-wide AppDomain
    // "MarketDataProvider" slot, purely as a side effect of construction. Since xunit runs every test
    // in one shared process, any test constructing a real provider - even one pointed at an isolated
    // temp directory - made that instance reachable from unrelated tests elsewhere in the same run
    // (via UserStrategyMetadataRepository.SaveStrategy's background disk write), risking real on-disk
    // writes. UserStrategyMetadataRepository.MarketDataProvider now requires an explicit, one-time
    // assignment at the real app's composition root (App.axaml.cs) instead - this confirms plain
    // construction alone never sets it.
    [Fact]
    public void Constructor_DoesNotAutoRegisterWithUserStrategyMetadataRepository()
    {
        var originalProvider = UserStrategyMetadataRepository.MarketDataProvider;
        try
        {
            var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            var settings = Options.Create(new MarketDataSettings { DailyDataPath = PathDiscovery.ResolveDataPath(null, "Data/Daily") });

            var provider = new ParquetMarketDataProvider(dbManager, new Mock<IPythonService>().Object, settings);

            Assert.NotSame(provider, UserStrategyMetadataRepository.MarketDataProvider);
        }
        finally
        {
            UserStrategyMetadataRepository.MarketDataProvider = originalProvider;
        }
    }

    [Fact]
    public async Task GetMetadataAsync_ShouldCacheResults()
    {
        // Arrange
        var mockPython = new Mock<IPythonService>();
        var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = PathDiscovery.ResolveDataPath(null, "Data/Daily") });
        
        var provider = new ParquetMarketDataProvider(dbManager, mockPython.Object, settings);

        var expectedMeta = new TickerMetadata("Test Corp", "Full Test Corp", "US", "Tech", "Software", "USD", 150m, 145m);
        
        int callCount = 0;
        mockPython.Setup(p => p.RunAsync(It.IsAny<Func<Python.Runtime.PyModule, TickerMetadata>>(), It.IsAny<CancellationToken>()))
                  .Callback(() => callCount++)
                  .ReturnsAsync(expectedMeta);

        // Act - Fetch from Python first (caches in memory/disk)
        var res1 = await provider.FetchMetadataFromPythonAsync("TEST");
        // GetMetadataAsync should now return from memory cache without calling Python
        var res2 = await provider.GetMetadataAsync("TEST");

        // Assert
        Assert.Equal(expectedMeta.ShortName, res1.ShortName);
        Assert.Equal(expectedMeta.ShortName, res2.ShortName);
        Assert.Equal(1, callCount); // Should be called only once due to cache
    }

    [Fact]
    public async Task GetMetadataAsync_ShouldRespectSemaphore()
    {
        // Arrange
        var mockPython = new Mock<IPythonService>();
        var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = PathDiscovery.ResolveDataPath(null, "Data/Daily") });
        
        var provider = new ParquetMarketDataProvider(dbManager, mockPython.Object, settings);

        int activeCalls = 0;
        int maxSimultaneousCalls = 0;
        var lockObj = new object();

        mockPython.Setup(p => p.RunAsync(It.IsAny<Func<Python.Runtime.PyModule, TickerMetadata>>(), It.IsAny<CancellationToken>()))
                  .Returns(async () => {
                      lock(lockObj) {
                          activeCalls++;
                          maxSimultaneousCalls = Math.Max(maxSimultaneousCalls, activeCalls);
                      }
                      await Task.Delay(100); // Simulate network delay
                      lock(lockObj) {
                          activeCalls--;
                      }
                      return TickerMetadata.Unknown;
                  });

        // Act - Request 10 different tickers simultaneously via Python fetch
        var tasks = new List<Task<TickerMetadata>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(provider.FetchMetadataFromPythonAsync($"TICKER{i}"));
        }
        await Task.WhenAll(tasks);

        // Assert
        Assert.True(maxSimultaneousCalls <= 1, $"Max simultaneous calls was {maxSimultaneousCalls}, expected <= 1");
    }

    [Fact]
    public void UserStrategyMetadataRepository_ShouldSaveAndRetrieveSignalFlags()
    {
        // Arrange
        var repo = UserStrategyMetadataRepository.Instance;
        string ticker = "TEST_FLAGS_" + Guid.NewGuid().ToString("N");

        // Act
        repo.SaveStrategy(ticker, 100m, 120m, 90m, null, null, null, "Test signal flags", isLong: true, isTPLong: false, isSLLong: true, isShort: false, isTPShort: true, isSLShort: false);
        var strategy = repo.GetStrategy(ticker);

        // Assert
        Assert.NotNull(strategy);
        Assert.True(strategy.IsLong);
        Assert.False(strategy.IsTPLong);
        Assert.True(strategy.IsSLLong);
        Assert.False(strategy.IsShort);
        Assert.True(strategy.IsTPShort);
        Assert.False(strategy.IsSLShort);
    }

    [Fact]
    public void SaveStrategy_RaisesStrategyChanged_WithTheAffectedTicker()
    {
        // Regression test (sa_minimal_fix): the Tickers grid's Notes column previously only picked
        // up a Notes-tab-driven cache update after an app restart, because SaveStrategy updated the
        // in-memory cache silently - nothing told an already-displayed WatchlistItemViewModel to
        // re-read it. StrategyChanged is the fix's notification hook.
        var repo = UserStrategyMetadataRepository.Instance;
        string ticker = "TEST_STRATEGY_CHANGED_" + Guid.NewGuid().ToString("N");
        string? receivedTicker = null;
        int callCount = 0;
        void Handler(string t) { receivedTicker = t; callCount++; }

        repo.StrategyChanged += Handler;
        try
        {
            // Act
            repo.SaveStrategy(ticker, null, null, null, null, null, null, "new article preview");

            // Assert
            Assert.Equal(1, callCount);
            Assert.Equal(ticker, receivedTicker);
        }
        finally
        {
            repo.StrategyChanged -= Handler;
        }
    }

    [Fact]
    public async Task SaveMetadataAsync_AndGetMetadataAsync_ShouldRoundTripReminderThroughDisk()
    {
        // Arrange: isolate the metadata parquet directory so this test never touches the real
        // Data/Metadata folder used by the running application.
        var tempMetadataDir = Path.Combine(Path.GetTempPath(), "sa_metadata_reminder_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempMetadataDir);
        try
        {
            var mockPython = new Mock<IPythonService>();
            var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            var settings = Options.Create(new MarketDataSettings
            {
                DailyDataPath = PathDiscovery.ResolveDataPath(null, "Data/Daily"),
                MetadataPath = tempMetadataDir
            });
            var provider = new ParquetMarketDataProvider(dbManager, mockPython.Object, settings);

            var ticker = "REMINDER_TEST_" + Guid.NewGuid().ToString("N");
            var meta = new TickerMetadata(ticker, ticker, "US", "Tech", "Software", "USD")
            {
                Reminder = "Review Q3 earnings call notes"
            };

            // Act
            await provider.SaveMetadataAsync(ticker, meta);
            provider.InvalidateMetadataCache(ticker);
            var reloaded = await provider.GetMetadataAsync(ticker);

            // Assert
            Assert.Equal("Review Q3 earnings call notes", reloaded.Reminder);
        }
        finally
        {
            try { Directory.Delete(tempMetadataDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void UserStrategyMetadataRepository_RegisterLoadedStrategy_ShouldIncludeReminder()
    {
        // Arrange
        var repo = UserStrategyMetadataRepository.Instance;
        string ticker = "TEST_REMINDER_" + Guid.NewGuid().ToString("N");

        // Act: mirrors how LoadMetadataFromDiskAsync hydrates the in-memory cache from disk.
        repo.RegisterLoadedStrategy(ticker, 100m, 120m, 90m, null, null, null, "Some notes",
            reminder: "Check dividend announcement");
        var strategy = repo.GetStrategy(ticker);

        // Assert
        Assert.NotNull(strategy);
        Assert.Equal("Check dividend announcement", strategy!.Reminder);
    }

    [Fact]
    public void WatchlistColumnRegistry_ShouldContainSignalColumns()
    {
        var cols = StockAnalyzer.Core.Models.Watchlist.WatchlistColumnRegistry.AllColumns;
        Assert.Contains(cols, c => c.MemberName == "IsLong" && c.HeaderKey == "Col_IsLong");
        Assert.Contains(cols, c => c.MemberName == "IsTPLong" && c.HeaderKey == "Col_IsTPLong");
        Assert.Contains(cols, c => c.MemberName == "IsSLLong" && c.HeaderKey == "Col_IsSLLong");
        Assert.Contains(cols, c => c.MemberName == "IsShort" && c.HeaderKey == "Col_IsShort");
        Assert.Contains(cols, c => c.MemberName == "IsTPShort" && c.HeaderKey == "Col_IsTPShort");
        Assert.Contains(cols, c => c.MemberName == "IsSLShort" && c.HeaderKey == "Col_IsSLShort");
    }
}
