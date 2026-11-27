using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class TimeFrameCacheSettingsTests
{
    private const int TestBars = 30;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public void TimeFrameCache_OutsideHardBand_ThrowsNamingTheKey(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TimeFrameCacheSettingsValidator.Validate(value));

        Assert.Contains(TimeFrameCacheSettingsValidator.MaxEntriesKey, ex.Message);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(256)]
    [InlineData(512)]
    public void TimeFrameCache_InsideStandardBand_IsSilent(int value)
    {
        Assert.Empty(TimeFrameCacheSettingsValidator.Validate(value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(513)]
    [InlineData(4096)]
    public void TimeFrameCache_BetweenBands_ReturnsOneWarningNamingTheKey(int value)
    {
        string warning = Assert.Single(TimeFrameCacheSettingsValidator.Validate(value));

        Assert.Contains(TimeFrameCacheSettingsValidator.MaxEntriesKey, warning);
    }

    [Fact]
    public void TimeFrameCache_ShippedDefault_IsInsideStandardBand()
    {
        Assert.True(TimeFrameCacheLimits.MaxEntries.IsWithinStandard(TimeFrameCacheLimits.DefaultMaxEntries));
        Assert.Equal(256, new MarketDataSettings().TimeFrameCacheMaxEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TimeFrameManager_NonPositiveCapacity_ThrowsArgumentOutOfRangeException(int capacity)
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(TestBars, "AAPL");
        using var db = new DuckDBConnectionManager();
        var dataService = new ParquetDataService(db, Options.Create(fixture.Settings));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeFrameManager(dataService, capacity));
    }

    [Fact]
    public void TimeFrameManager_CreateConfigured_AboveHardMax_ThrowsNamingKey()
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(TestBars, "AAPL");
        using var db = new DuckDBConnectionManager();
        var dataService = new ParquetDataService(db, Options.Create(fixture.Settings));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TimeFrameManager.CreateConfigured(dataService, 5000));
        Assert.Contains(TimeFrameCacheSettingsValidator.MaxEntriesKey, ex.Message);
    }

    [Fact]
    public async Task TimeFrameManager_Configured256Entries_RetainsMoreThan20SymbolsWithoutReload()
    {
        string[] symbols = new string[25];
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = $"SYM{i:D2}";
        }

        using var fixture = ReadOnlyDataTreeFixture.Create(TestBars, symbols);
        using var db = new DuckDBConnectionManager();
        using var pool = new ReadOnlyDuckDbConnectionPool(poolSize: 2);
        var parquet = new ParquetDataService(db, Options.Create(fixture.Settings), readPool: pool);
        var counting = new CountingDataService(parquet);
        var manager = new TimeFrameManager(counting, TimeFrameCacheLimits.DefaultMaxEntries);

        for (int i = 0; i < symbols.Length; i++)
        {
            var bars = await manager.GetCandlesAsync(symbols[i], TimeFrame.D1, 20);
            Assert.Equal(20, bars.Count);
        }

        int afterWarmup = counting.LoadCount;
        Assert.Equal(25, afterWarmup);

        // With legacy default 20, SYM00..SYM04 would have been evicted; with 256 all 25 stay cached.
        for (int i = 0; i < symbols.Length; i++)
        {
            var bars = await manager.GetCandlesAsync(symbols[i], TimeFrame.D1, 20);
            Assert.Equal(20, bars.Count);
        }

        Assert.Equal(afterWarmup, counting.LoadCount);
    }

    [Fact]
    public void ReadOnlyDiContainer_RegistersPoolAndTimeFrameFactory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataSettings:TimeFrameCacheMaxEntries"] = "300",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddStockAnalyzerReadOnlyCore(config);

        Assert.Contains(services, sd => sd.ServiceType == typeof(IReadOnlyParquetConnectionPool) && sd.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, sd => sd.ServiceType == typeof(ITimeFrameManager) && sd.Lifetime == ServiceLifetime.Singleton);
    }

    [Theory]
    [InlineData(33_554_431L)]
    [InlineData(536_870_913L)]
    public void RenderingSettings_OutsideHardBand_ThrowsNamingTheKey(long bytes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RenderingSettingsValidator.Validate(bytes));

        Assert.Contains(RenderingSettingsValidator.MaxGpuResourceSizeBytesKey, ex.Message);
    }

    [Theory]
    [InlineData(67_108_864L)]
    [InlineData(RenderingLimits.DefaultMaxGpuResourceSizeBytes)]
    [InlineData(268_435_456L)]
    public void RenderingSettings_InsideStandardBand_IsSilent(long bytes)
    {
        Assert.Empty(RenderingSettingsValidator.Validate(bytes));
    }

    [Theory]
    [InlineData(33_554_432L)]
    [InlineData(536_870_912L)]
    public void RenderingSettings_BetweenBands_EmitsWarningNamingTheKey(long bytes)
    {
        string warning = Assert.Single(RenderingSettingsValidator.Validate(bytes));

        Assert.Contains(RenderingSettingsValidator.MaxGpuResourceSizeBytesKey, warning);
    }

    private sealed class CountingDataService : IDataService
    {
        private readonly IDataService _inner;
        private int _loadCount;

        public CountingDataService(IDataService inner) => _inner = inner;
        public int LoadCount => Volatile.Read(ref _loadCount);

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            Interlocked.Increment(ref _loadCount);
            return _inner.LoadCandlesAsync(symbol, timeFrame, count);
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count, bool includePartialBar, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            return _inner.LoadCandlesAsync(symbol, timeFrame, count, includePartialBar, cancellationToken);
        }
    }
}
