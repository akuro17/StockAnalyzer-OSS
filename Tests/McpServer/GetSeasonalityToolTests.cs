using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class GetSeasonalityToolTests : IDisposable
{
    private const int TotalDays = 365 * 3; // 3 full calendar years: 2022, 2023, 2024
    private const string Symbol = "SEASTEST";
    private static readonly DateTime StartDate = new(2022, 1, 1);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(TotalDays, "msft", "seastest");
    private readonly List<McpServerRuntime> _runtimes = new();

    private readonly List<decimal> _closes = Enumerable.Range(0, TotalDays)
        .Select(i => 100m + ((i % 30) * 1.5m) + ((i / 365) * 10m))
        .ToList();

    public GetSeasonalityToolTests()
    {
        SeedData(_fixture.DailyDirectory, Symbol, StartDate, _closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private static void SeedData(string dir, string ticker, DateTime startDate, IReadOnlyList<decimal> closes)
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>(closes.Count);
        var date = startDate;
        foreach (decimal close in closes)
        {
            var open = close - 0.2m;
            var high = close + 0.5m;
            var low = close - 0.5m;
            rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {open}, {high}, {low}, {close}, 1000)"));
            date = date.AddDays(1);
        }

        using var connection = new DuckDBConnection("DataSource=:memory:");
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

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_get_seasonality", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string ErrorCode(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    [Fact]
    public async Task GetSeasonality_MatchesDirectTableAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"years\":3,\"startMonth\":1"));

        Assert.Equal(Symbol, result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal(1, result.GetProperty("startMonth").GetInt32());

        // Direct Core analysis
        var points = _closes.Select((c, i) => new SeasonalityPoint(StartDate.AddDays(i), c)).ToList();
        var seriesInput = new SeasonalitySeriesInput(0, Symbol, SeasonalityRadiusMode.PercentVsYearStart, points);
        var parameters = new SeasonalityChartParameters(3, StartMonth: 1);
        var chartResult = SeasonalityChartEngine.Analyze(new[] { seriesInput }, parameters);
        var expectedTable = SeasonalityMonthlyReturnsEngine.Build(chartResult);

        int[] expectedYears = expectedTable.Years.ToArray();
        int[] actualYears = result.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()).ToArray();
        Assert.Equal(expectedYears, actualYears);

        var rows = result.GetProperty("rows");
        Assert.Equal(SeasonalityMonthlyReturnsTable.RowCount, rows.GetArrayLength());

        for (int r = 0; r < SeasonalityMonthlyReturnsTable.RowCount; r++)
        {
            var row = rows[r];
            var cells = row.GetProperty("cells");
            Assert.Equal(expectedYears.Length, cells.GetArrayLength());

            for (int y = 0; y < expectedYears.Length; y++)
            {
                var cell = cells[y];
                Assert.Equal(expectedYears[y], cell.GetProperty("year").GetInt32());

                decimal? expReturn = expectedTable.Cell(r, y).Return;
                if (expReturn.HasValue)
                {
                    Assert.Equal(expReturn.Value, cell.GetProperty("returnRatio").GetDecimal());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, cell.GetProperty("returnRatio").ValueKind);
                }
            }

            decimal? expAvg = expectedTable.RowAverages[r];
            if (expAvg.HasValue)
            {
                Assert.Equal(expAvg.Value, row.GetProperty("average").GetDecimal());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("average").ValueKind);
            }
        }
    }

    [Fact]
    public async Task GetSeasonality_StartMonthShift_MatchesDirectTable()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"years\":3,\"startMonth\":4"));

        Assert.Equal(4, result.GetProperty("startMonth").GetInt32());

        var rows = result.GetProperty("rows");
        Assert.Equal(SeasonalityMonthlyReturnsTable.RowCount, rows.GetArrayLength());
        Assert.Equal("Apr", rows[0].GetProperty("label").GetString());
        Assert.Equal("Mar", rows[11].GetProperty("label").GetString());
        Assert.Equal("Annual", rows[12].GetProperty("label").GetString());

        // Direct table with startMonth = 4
        var points = _closes.Select((c, i) => new SeasonalityPoint(StartDate.AddDays(i), c)).ToList();
        var seriesInput = new SeasonalitySeriesInput(0, Symbol, SeasonalityRadiusMode.PercentVsYearStart, points);
        var parameters = new SeasonalityChartParameters(3, StartMonth: 4);
        var chartResult = SeasonalityChartEngine.Analyze(new[] { seriesInput }, parameters);
        var expectedTable = SeasonalityMonthlyReturnsEngine.Build(chartResult);

        for (int r = 0; r < SeasonalityMonthlyReturnsTable.RowCount; r++)
        {
            decimal? expAvg = expectedTable.RowAverages[r];
            decimal? actAvg = rows[r].GetProperty("average").ValueKind == JsonValueKind.Null
                ? null
                : rows[r].GetProperty("average").GetDecimal();
            Assert.Equal(expAvg, actAvg);
        }
    }

    [Fact]
    public async Task GetSeasonality_AsOfInvariance_MutationAfterEndDate_DoesNotAlterOutput()
    {
        var dispatcher1 = await StartAsync();
        string endDateStr = "2023-12-31";

        JsonElement res1 = Result(await CallAsync(dispatcher1, $",\"years\":2,\"endDate\":\"{endDateStr}\""));

        // Mutate closes in 2024 (after day 730)
        var mutatedCloses = new List<decimal>(_closes);
        for (int i = 730; i < TotalDays; i++)
        {
            mutatedCloses[i] = 99999m;
        }
        SeedData(_fixture.DailyDirectory, Symbol, StartDate, mutatedCloses);

        var dispatcher2 = await StartAsync();
        JsonElement res2 = Result(await CallAsync(dispatcher2, $",\"years\":2,\"endDate\":\"{endDateStr}\""));

        Assert.Equal(res1.ToString(), res2.ToString());
    }

    [Fact]
    public async Task GetSeasonality_Validation()
    {
        var dispatcher = await StartAsync();

        // startMonth out of range
        string errMonth0 = ErrorCode(await CallAsync(dispatcher, ",\"startMonth\":0"));
        Assert.Equal("INVALID_ARGUMENT", errMonth0);

        string errMonth13 = ErrorCode(await CallAsync(dispatcher, ",\"startMonth\":13"));
        Assert.Equal("INVALID_ARGUMENT", errMonth13);

        // 64-bit integer overflow on startMonth
        string errMonthOverflow = ErrorCode(await CallAsync(dispatcher, ",\"startMonth\":4294967320"));
        Assert.Equal("INVALID_ARGUMENT", errMonthOverflow);

        // years out of range
        string errYears0 = ErrorCode(await CallAsync(dispatcher, ",\"years\":0"));
        Assert.Equal("INVALID_ARGUMENT", errYears0);

        string errYearsLimit = ErrorCode(await CallAsync(dispatcher, ",\"years\":51"));
        Assert.Equal("LIMIT_EXCEEDED", errYearsLimit);
    }
}
