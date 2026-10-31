using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class CalculateVolumeProfileToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "VPTEST";
    private const string FlatSymbol = "VPFLAT";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "vptest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + (i % 10) * 2m).ToList();
    private readonly List<decimal> _flatCloses = Enumerable.Repeat(100m, Days).ToList();

    public CalculateVolumeProfileToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
        SeedZeroRangeParquet(_fixture.DailyDirectory, FlatSymbol, 100m, Days);
    }

    private static void SeedZeroRangeParquet(string dir, string ticker, decimal price, int days)
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        var date = new DateTime(2024, 1, 1);
        for (int i = 0; i < days; i++)
        {
            rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {price}, {price}, {price}, {price}, 1000)"));
            date = date.AddDays(1);
        }

        using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            COPY (
                SELECT * FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS t(date, open, high, low, close, volume)
            ) TO '{fixturePath}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private List<CoreCandleData> Candles()
    {
        var date = new DateTime(2024, 1, 1);
        return _closes.Select((close, i) => new CoreCandleData(date.AddDays(i), close - 0.2m, close + 0.5m, close - 0.5m, close, 1000)).ToList();
    }

    private readonly StringWriter _logWriter = new();

    private async Task<McpMessageDispatcher> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(_logWriter));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_volume_profile", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

    private JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), $"{McpTestSupport.Structured(response)}\nServer log:\n{_logWriter}");
        return McpTestSupport.Structured(response);
    }

    private static string ErrorCode(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    [Fact]
    public async Task VolumeProfile_VolumeBasis_EqualsDirectCoreVolumeAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"Volume\",\"binCount\":20,\"valueAreaPercent\":0.70"));

        Assert.Equal("VPTEST", result.GetProperty("symbol").GetString());
        Assert.Equal("Volume", result.GetProperty("basis").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        // Direct Core calculation
        var coreCandles = Candles();
        var expectedBins = VolumeAnalysis.CalculateProfile(coreCandles, 20, VolumeDistributionMode.Proportional);
        var (expectedVah, expectedVal) = VolumeAnalysis.CalculateValueArea(expectedBins, 0.70);

        decimal expectedPoc = 0m;
        long maxVol = -1;
        for (int i = 0; i < expectedBins.Count; i++)
        {
            if (expectedBins[i].TotalVolume > maxVol)
            {
                maxVol = expectedBins[i].TotalVolume;
                expectedPoc = expectedBins[i].Price;
            }
        }

        Assert.Equal(expectedVah, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(expectedVal, result.GetProperty("valueArea").GetProperty("low").GetDecimal());
        Assert.Equal(expectedPoc, result.GetProperty("poc").GetProperty("price").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(expectedBins.Count, bins.Length);
        for (int i = 0; i < expectedBins.Count; i++)
        {
            Assert.Equal(expectedBins[i].LowerBound, bins[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(expectedBins[i].UpperBound, bins[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(expectedBins[i].TotalVolume, bins[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_TimeAtPriceBasis_EqualsDirectCoreTimeAtPriceAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"TimeAtPrice\",\"binCount\":15,\"valueAreaPercent\":0.70"));

        Assert.Equal("VPTEST", result.GetProperty("symbol").GetString());
        Assert.Equal("TimeAtPrice", result.GetProperty("basis").GetString());

        var coreCandles = Candles();
        var expectedBins = TimeAtPriceAnalysis.CalculateProfile(coreCandles, 15, PriceType.Close);
        var (expectedVah, expectedVal) = TimeAtPriceAnalysis.CalculateValueArea(expectedBins, 0.70);

        decimal expectedPoc = 0m;
        long maxVol = -1;
        for (int i = 0; i < expectedBins.Count; i++)
        {
            if (expectedBins[i].TotalVolume > maxVol)
            {
                maxVol = expectedBins[i].TotalVolume;
                expectedPoc = expectedBins[i].Price;
            }
        }

        Assert.Equal(expectedVah, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(expectedVal, result.GetProperty("valueArea").GetProperty("low").GetDecimal());
        Assert.Equal(expectedPoc, result.GetProperty("poc").GetProperty("price").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(expectedBins.Count, bins.Length);
        for (int i = 0; i < expectedBins.Count; i++)
        {
            Assert.Equal(expectedBins[i].LowerBound, bins[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(expectedBins[i].UpperBound, bins[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(expectedBins[i].TotalVolume, bins[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_MutationTest_FutureBarsDoNotAffectHistoricalOutput()
    {
        var coreCandles = Candles();
        var cutIndex = 50;
        var endDate = coreCandles[cutIndex].Timestamp.Date.ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement resultBefore = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"binCount\":20"));

        // Mutate candles strictly after endDate in the data store
        var modifiedCloses = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < modifiedCloses.Count; i++)
        {
            modifiedCloses[i] = 9999m + i;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, modifiedCloses);

        // Call again with the same endDate
        JsonElement resultAfter = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"binCount\":20"));

        Assert.Equal(resultBefore.GetProperty("poc").GetProperty("price").GetDecimal(), resultAfter.GetProperty("poc").GetProperty("price").GetDecimal());
        Assert.Equal(resultBefore.GetProperty("valueArea").GetProperty("high").GetDecimal(), resultAfter.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(resultBefore.GetProperty("valueArea").GetProperty("low").GetDecimal(), resultAfter.GetProperty("valueArea").GetProperty("low").GetDecimal());

        JsonElement[] binsBefore = resultBefore.GetProperty("bins").EnumerateArray().ToArray();
        JsonElement[] binsAfter = resultAfter.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(binsBefore.Length, binsAfter.Length);
        for (int i = 0; i < binsBefore.Length; i++)
        {
            Assert.Equal(binsBefore[i].GetProperty("volume").GetInt64(), binsAfter[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_RangeBars_SlicesTrailingBars()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"rangeBars\":30,\"binCount\":20"));

        Assert.Equal(30, result.GetProperty("barCount").GetInt32());
    }

    [Fact]
    public async Task VolumeProfile_RangeZero_DegeneratesToSingleBin()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"allowUnregistered\":true", symbol: FlatSymbol));

        Assert.Equal(FlatSymbol, result.GetProperty("symbol").GetString());
        Assert.Equal(1, result.GetProperty("binCount").GetInt32());
        Assert.Equal(100m, result.GetProperty("poc").GetProperty("price").GetDecimal());
        Assert.Equal(100m, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(100m, result.GetProperty("valueArea").GetProperty("low").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Single(bins);
        Assert.Equal(100m, bins[0].GetProperty("priceLow").GetDecimal());
        Assert.Equal(100m, bins[0].GetProperty("priceHigh").GetDecimal());
        Assert.Equal(Days * 1000, bins[0].GetProperty("volume").GetInt64());
    }

    [Fact]
    public async Task VolumeProfile_InvalidArguments_ThrowsToolException()
    {
        var dispatcher = await StartAsync();

        // binCount < 2
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":1")));
        // binCount > max (200)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":201")));
        // rangeBars > max (10000)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"rangeBars\":10001")));
        // valueAreaPercent <= 0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"valueAreaPercent\":0.0")));
        // valueAreaPercent > 1.0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"valueAreaPercent\":1.01")));
        // invalid basis
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"basis\":\"InvalidBasis\"")));
        // timeFrame required
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_calculate_volume_profile", $"{{\"symbol\":\"{Symbol}\"}}")!));
    }
}
