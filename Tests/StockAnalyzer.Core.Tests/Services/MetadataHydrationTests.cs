using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class MetadataHydrationTests
{
    [Fact]
    public async Task GetMetadataAsync_ShouldCacheResults()
    {
        // Arrange
        var mockPython = new Mock<IPythonService>();
        var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = "i:\\stock" });
        
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
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = "i:\\stock" });
        
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
