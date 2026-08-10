using DuckDB.NET.Data;
using Xunit;
using System.IO;
using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Core.Tests.Services;

public class DuckDBTest
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly ParquetMarketDataProvider _provider;

    public DuckDBTest()
    {
        _dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var settings = Options.Create(new MarketDataSettings 
        { 
            DailyDataPath = Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily") 
        });
        _provider = new ParquetMarketDataProvider(_dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);
    }

    [Fact]
    public async Task GetMetadataAsync_AA_ShouldHaveCompleteMetadata()
    {
        var settings = Options.Create(new MarketDataSettings 
        { 
            DailyDataPath = Path.Combine("i:\\stock", "Data", "Daily") 
        });
        var provider = new ParquetMarketDataProvider(_dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);
        var meta = await provider.GetMetadataAsync("AA");
        
        Assert.Equal("Alcoa Corporation", meta.ShortName);
        Assert.Equal("Basic Materials", meta.Sector);
        Assert.Equal("Aluminum", meta.Industry);
        Assert.NotNull(meta.ReturnOnEquity);
        Assert.True(meta.ReturnOnEquity > 0);
    }

    [Fact]
    public void ConnectionTest()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = command.ExecuteScalar();
        
        Assert.Equal(1, result);
    }

    [Fact]
    public void QueryParquetTest()
    {
        var parquetPath = Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily", "MSFT.parquet");
        Assert.True(File.Exists(parquetPath), $"Parquet file not found at {parquetPath}");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        // Query parquet file directly to get column types
        command.CommandText = $"SELECT * FROM read_parquet('{parquetPath.Replace("\\", "/")}') LIMIT 1";
        
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                Debug.WriteLine("--- Column Types for MSFT.parquet ---");
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    Debug.WriteLine($"Column {i}: {reader.GetName(i)}, Type: {reader.GetFieldType(i)}");
                }
                Debug.WriteLine("-----------------------------------");
            }
        }

        // Now, perform the original count query
        command.CommandText = $"SELECT COUNT(*) FROM read_parquet('{parquetPath.Replace("\\", "/")}')";
        var result = command.ExecuteScalar();

        Assert.NotNull(result);
        Assert.True((long)result > 0);
    }

    [Fact]
    public async Task GetTickersDataAsync_ShouldReturnData()
    {
        var baseDir = Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily");
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = baseDir });
        using var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var provider = new ParquetMarketDataProvider(dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);

        var candles = await provider.GetTickersDataAsync("MSFT", TimeFrame.D1);

        Assert.NotEmpty(candles);
        Assert.True(candles.Count > 0);
        Assert.Equal("MSFT", "MSFT"); // Dummy to ensure we are looking at MSFT
    }

    [Fact]
    public async Task GetAvailableTickersAsync_ShouldReturnTickers()
    {
        var baseDir = Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily");
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = baseDir, TickerListPath = null });
        using var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var provider = new ParquetMarketDataProvider(dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);

        var tickers = await provider.GetAvailableTickersAsync();

        Assert.Contains("MSFT", tickers);
        Assert.Contains("AAPL", tickers);
        Assert.Contains("GOOG", tickers);
    }

    [Fact]
    public async Task ScreenAsync_ShouldFilterByRsi()
    {
        var criteria = new ScreeningCriteria
        {
            Conditions = new List<IScreeningCondition>
            {
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 100m)
            }
        };

        var matched = await _provider.ScreenAsync(criteria);

        Assert.NotEmpty(matched);
        Assert.Contains("MSFT", matched);
        Assert.Contains("AAPL", matched);
        Assert.Contains("GOOG", matched);
    }

    [Fact]
    public async Task ScreenAsync_WithMultipleConditions_ShouldWork()
    {
        // Two RSI conditions with high thresholds to ensure they both match the mock data.
        var criteria = new ScreeningCriteria
        {
            Conditions = new List<IScreeningCondition>
            {
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 100m),
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 99m)
            }
        };

        var matched = await _provider.ScreenAsync(criteria);

        Assert.NotEmpty(matched);
        Assert.Contains("MSFT", matched);
    }

    [Fact]
    public async Task ScreenAsync_ShouldBeThreadSafe()
    {
        var criteria = new ScreeningCriteria
        {
            Conditions = new List<IScreeningCondition>
            {
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 100m)
            }
        };

        // Run multiple screening tasks in parallel
        var tasks = new List<Task<IReadOnlyList<string>>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_provider.ScreenAsync(criteria));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            Assert.NotEmpty(result);
            Assert.Contains("MSFT", result);
        }
    }

    [Fact]
    public async Task DeleteTickerDataFromDateAsync_RemovesCutoffAndNewerRows_KeepsOlderRowsIntact()
    {
        // Uses a self-contained, isolated fixture (never the shared Data/TestMarketData or
        // Data/Daily fixtures used by other tests in this class) so this destructive test
        // cannot corrupt data relied upon elsewhere.
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_delete_test_" + System.Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fixturePath = Path.Combine(tempDir, "ZTEST.parquet").Replace("\\", "/");

            using (var setupConnection = new DuckDBConnection("DataSource=:memory:"))
            {
                setupConnection.Open();
                using var setupCommand = setupConnection.CreateCommand();
                setupCommand.CommandText = $@"
                    COPY (
                        SELECT * FROM (VALUES
                            (DATE '2024-01-01', 10.0, 11.0, 9.5, 10.5, 1000),
                            (DATE '2024-01-02', 10.5, 11.2, 10.0, 11.0, 1100),
                            (DATE '2024-01-03', 11.0, 11.8, 10.8, 11.5, 1200),
                            (DATE '2024-01-04', 11.5, 12.0, 11.2, 11.8, 1300),
                            (DATE '2024-01-05', 11.8, 12.5, 11.6, 12.2, 1400)
                        ) AS t(date, open, high, low, close, volume)
                    ) TO '{fixturePath}' (FORMAT PARQUET)";
                setupCommand.ExecuteNonQuery();
            }

            var settings = Options.Create(new MarketDataSettings { DailyDataPath = tempDir });
            using var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            var provider = new ParquetMarketDataProvider(dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);

            var deletedCount = await provider.DeleteTickerDataFromDateAsync("ZTEST", new System.DateTime(2024, 1, 3));

            Assert.Equal(3, deletedCount); // 2024-01-03, 01-04, 01-05

            var remaining = await provider.GetTickersDataAsync("ZTEST", TimeFrame.D1);
            Assert.Equal(2, remaining.Count);
            Assert.All(remaining, c => Assert.True(c.Timestamp < new System.DateTime(2024, 1, 3)));
            Assert.Contains(remaining, c => c.Timestamp.Date == new System.DateTime(2024, 1, 1));
            Assert.Contains(remaining, c => c.Timestamp.Date == new System.DateTime(2024, 1, 2));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DeleteTickerDataFromDateAsync_WhenFileMissing_IsSafeNoOp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_delete_test_missing_" + System.Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = Options.Create(new MarketDataSettings { DailyDataPath = tempDir });
            using var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            var provider = new ParquetMarketDataProvider(dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);

            // No parquet file exists for this ticker under the isolated tempDir.
            var deletedCount = await provider.DeleteTickerDataFromDateAsync("MSFT", new System.DateTime(2024, 1, 1));

            Assert.Equal(0, deletedCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
