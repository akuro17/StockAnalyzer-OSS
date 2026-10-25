using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class TimeFrameParquetGeneratorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DuckDBConnectionManager _dbManager;
    private readonly IOptions<MarketDataSettings> _settings;
    private readonly TimeFrameParquetGenerator _generator;

    public TimeFrameParquetGeneratorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sa_tf_gen_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        var dailyDir = Path.Combine(_tempRoot, "Daily");
        var weeklyDir = Path.Combine(_tempRoot, "Weekly");
        var monthlyDir = Path.Combine(_tempRoot, "Monthly");
        Directory.CreateDirectory(dailyDir);
        Directory.CreateDirectory(weeklyDir);
        Directory.CreateDirectory(monthlyDir);

        _settings = Options.Create(new MarketDataSettings
        {
            DailyDataPath = dailyDir,
            WeeklyDataPath = weeklyDir,
            MonthlyDataPath = monthlyDir,
        });

        _dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        _generator = new TimeFrameParquetGenerator(_dbManager, _settings, NullLogger<TimeFrameParquetGenerator>.Instance);
    }

    public void Dispose()
    {
        _dbManager.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    private static List<CandleData> CreateDailyBars(DateTime startDate, int count)
    {
        var bars = new List<CandleData>();
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            var date = startDate.AddDays(i);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            decimal open = price;
            decimal high = price + 5m;
            decimal low = price - 2m;
            decimal close = price + 1m;
            long volume = 1000L + i * 10;
            bars.Add(new CandleData(date, open, high, low, close, volume));
            price += 1m;
        }
        return bars;
    }

    [Fact]
    public async Task GenerateForSymbolAsync_WithValidBars_GeneratesWeeklyAndMonthlyParquetFiles()
    {
        const string symbol = "TEST1";
        // 4 weeks of daily bars starting Monday 2024-01-08
        var dailyBars = CreateDailyBars(new DateTime(2024, 1, 8), 28);

        bool success = await _generator.GenerateForSymbolAsync(symbol, dailyBars);
        Assert.True(success);

        var weeklyFile = Path.Combine(_settings.Value.WeeklyDataPath!, $"{symbol}.parquet");
        var monthlyFile = Path.Combine(_settings.Value.MonthlyDataPath!, $"{symbol}.parquet");

        Assert.True(File.Exists(weeklyFile), "Weekly Parquet file must exist.");
        Assert.True(File.Exists(monthlyFile), "Monthly Parquet file must exist.");

        var expectedWeekly = TimeFrameAggregator.AggregateDailyToWeekly(dailyBars);
        var expectedMonthly = TimeFrameAggregator.AggregateDailyToMonthly(dailyBars);

        // Verify weekly contents
        using (await _dbManager.AcquireLockAsync("TestVerify"))
        {
            var conn = (DbConnection)_dbManager.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{weeklyFile.Replace('\\', '/')}') ORDER BY date ASC;";
            using var reader = await cmd.ExecuteReaderAsync();

            int count = 0;
            while (await reader.ReadAsync())
            {
                Assert.True(count < expectedWeekly.Count);
                var exp = expectedWeekly[count];
                var expDate = TimeFrameAggregator.PeriodStart(exp.Timestamp, TimeFrame.W1).Date;

                Assert.Equal(expDate, reader.GetDateTime(0).Date);
                Assert.Equal(DayOfWeek.Monday, reader.GetDateTime(0).DayOfWeek);
                Assert.Equal((float)exp.Open, Convert.ToSingle(reader.GetValue(1)));
                Assert.Equal((float)exp.High, Convert.ToSingle(reader.GetValue(2)));
                Assert.Equal((float)exp.Low, Convert.ToSingle(reader.GetValue(3)));
                Assert.Equal((float)exp.Close, Convert.ToSingle(reader.GetValue(4)));
                Assert.Equal(exp.Volume, Convert.ToInt64(reader.GetValue(5)));
                count++;
            }
            Assert.Equal(expectedWeekly.Count, count);
        }

        // Verify monthly contents
        using (await _dbManager.AcquireLockAsync("TestVerifyMonthly"))
        {
            var conn = (DbConnection)_dbManager.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{monthlyFile.Replace('\\', '/')}') ORDER BY date ASC;";
            using var reader = await cmd.ExecuteReaderAsync();

            int count = 0;
            while (await reader.ReadAsync())
            {
                Assert.True(count < expectedMonthly.Count);
                var exp = expectedMonthly[count];
                var expDate = TimeFrameAggregator.PeriodStart(exp.Timestamp, TimeFrame.MN1).Date;

                Assert.Equal(expDate, reader.GetDateTime(0).Date);
                Assert.Equal(1, reader.GetDateTime(0).Day);
                Assert.Equal((float)exp.Open, Convert.ToSingle(reader.GetValue(1)));
                Assert.Equal((float)exp.High, Convert.ToSingle(reader.GetValue(2)));
                Assert.Equal((float)exp.Low, Convert.ToSingle(reader.GetValue(3)));
                Assert.Equal((float)exp.Close, Convert.ToSingle(reader.GetValue(4)));
                Assert.Equal(exp.Volume, Convert.ToInt64(reader.GetValue(5)));
                count++;
            }
            Assert.Equal(expectedMonthly.Count, count);
        }
    }

    [Fact]
    public async Task GenerateForSymbolAsync_WithEmptyBars_ReturnsFalse_AndPreservesExistingFiles()
    {
        const string symbol = "EMPTYTEST";
        var weeklyFile = Path.Combine(_settings.Value.WeeklyDataPath!, $"{symbol}.parquet");
        var monthlyFile = Path.Combine(_settings.Value.MonthlyDataPath!, $"{symbol}.parquet");

        await File.WriteAllTextAsync(weeklyFile, "PRIOR_WEEKLY");
        await File.WriteAllTextAsync(monthlyFile, "PRIOR_MONTHLY");

        bool success = await _generator.GenerateForSymbolAsync(symbol, Array.Empty<CandleData>());
        Assert.False(success);

        Assert.Equal("PRIOR_WEEKLY", await File.ReadAllTextAsync(weeklyFile));
        Assert.Equal("PRIOR_MONTHLY", await File.ReadAllTextAsync(monthlyFile));
    }

    [Fact]
    public async Task GenerateAsync_MissingDailyFile_ReturnsFailureCount()
    {
        int failed = await _generator.GenerateAsync("NON_EXISTENT_SYMBOL");
        Assert.Equal(1, failed);
    }

    [Fact]
    public async Task GenerateAsync_AllSymbols_GeneratesFromDailyParquetFolder()
    {
        // Setup two tickers in Daily folder
        var ticker1Bars = CreateDailyBars(new DateTime(2024, 2, 5), 14);
        var ticker2Bars = CreateDailyBars(new DateTime(2024, 3, 4), 14);

        await WriteDailyParquetAsync("SYM1", ticker1Bars);
        await WriteDailyParquetAsync("SYM2", ticker2Bars);

        int failed = await _generator.GenerateAsync(null);
        Assert.Equal(0, failed);

        Assert.True(File.Exists(Path.Combine(_settings.Value.WeeklyDataPath!, "SYM1.parquet")));
        Assert.True(File.Exists(Path.Combine(_settings.Value.MonthlyDataPath!, "SYM1.parquet")));
        Assert.True(File.Exists(Path.Combine(_settings.Value.WeeklyDataPath!, "SYM2.parquet")));
        Assert.True(File.Exists(Path.Combine(_settings.Value.MonthlyDataPath!, "SYM2.parquet")));
    }

    private async Task WriteDailyParquetAsync(string symbol, IReadOnlyList<CandleData> bars)
    {
        var dailyPath = Path.Combine(_settings.Value.DailyDataPath!, $"{symbol}.parquet");
        using (await _dbManager.AcquireLockAsync("WriteTestDaily"))
        {
            var conn = (DbConnection)_dbManager.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TEMPORARY TABLE temp_test_daily (date DATE, open FLOAT, high FLOAT, low FLOAT, close FLOAT, volume BIGINT);";
            await cmd.ExecuteNonQueryAsync();

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("INSERT INTO temp_test_daily VALUES ");
                for (int i = 0; i < bars.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var b = bars[i];
                    sb.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"('{b.Timestamp:yyyy-MM-dd}',{(float)b.Open},{(float)b.High},{(float)b.Low},{(float)b.Close},{b.Volume})"));
                }
                sb.Append(';');
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = sb.ToString();
                await insertCmd.ExecuteNonQueryAsync();

                using var copyCmd = conn.CreateCommand();
                copyCmd.CommandText = $"COPY (SELECT * FROM temp_test_daily ORDER BY date ASC) TO '{dailyPath.Replace('\\', '/')}' (FORMAT PARQUET);";
                await copyCmd.ExecuteNonQueryAsync();
            }
            finally
            {
                using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = "DROP TABLE IF EXISTS temp_test_daily;";
                await dropCmd.ExecuteNonQueryAsync();
            }
        }
    }
}
