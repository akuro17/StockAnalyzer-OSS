using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class CalculateMarketBreadthToolTests : IDisposable
{
    private const int StoredDays = 200;
    private const string Sym1 = "MSFT";
    private const string Sym2 = "AAPL";
    private const string Tool = "sa_calculate_market_breadth";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] limitOverrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(limitOverrides), new LogSink(new StringWriter()), _fixture.DailyDirectory);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonElement> CallAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static async Task<JsonElement> CallExpectingErrorAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.True(McpTestSupport.IsError(response), "Expected tool error but call succeeded");
        return McpTestSupport.Structured(response).Clone();
    }

    [Fact]
    public async Task BasicCalculation_SucceedsWithAllBreadthSeries()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"timeFrame":"D1"}""";
        JsonElement result = await CallAsync(dispatcher, args);

        Assert.True(result.TryGetProperty("dateCount", out JsonElement dateCountElem));
        Assert.True(dateCountElem.GetInt32() > 0);

        Assert.True(result.TryGetProperty("symbolCount", out JsonElement symbolCountElem));
        Assert.Equal(2, symbolCountElem.GetInt32());

        Assert.True(result.TryGetProperty("series", out JsonElement series));
        Assert.True(series.TryGetProperty("dates", out JsonElement dates));
        Assert.Equal(dateCountElem.GetInt32(), dates.GetArrayLength());

        Assert.True(series.TryGetProperty("advancers", out JsonElement adv));
        Assert.Equal(dateCountElem.GetInt32(), adv.GetArrayLength());

        Assert.True(series.TryGetProperty("decliners", out JsonElement dec));
        Assert.Equal(dateCountElem.GetInt32(), dec.GetArrayLength());

        Assert.True(series.TryGetProperty("adLine", out JsonElement adLine));
        Assert.Equal(dateCountElem.GetInt32(), adLine.GetArrayLength());

        Assert.True(series.TryGetProperty("cvi", out JsonElement cvi));
        Assert.Equal(dateCountElem.GetInt32(), cvi.GetArrayLength());

        Assert.True(result.TryGetProperty("notes", out JsonElement notes));
        Assert.True(notes.GetArrayLength() > 0);
    }

    [Fact]
    public async Task IncludeFilter_OnlyOutputsRequestedSeries()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"include":["adLine","trin"]}""";
        JsonElement result = await CallAsync(dispatcher, args);

        JsonElement series = result.GetProperty("series");
        Assert.True(series.TryGetProperty("dates", out _));
        Assert.True(series.TryGetProperty("adLine", out _));
        Assert.True(series.TryGetProperty("trin", out _));

        // Unrequested series should not be written to JSON
        Assert.False(series.TryGetProperty("advancers", out _));
        Assert.False(series.TryGetProperty("decliners", out _));
        Assert.False(series.TryGetProperty("mcClellan", out _));
    }

    [Fact]
    public async Task OutputTailCount_LimitsReturnedDates()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"outputTailCount":15}""";
        JsonElement result = await CallAsync(dispatcher, args);

        int dateCount = result.GetProperty("dateCount").GetInt32();
        Assert.Equal(15, dateCount);
        Assert.Equal(15, result.GetProperty("series").GetProperty("dates").GetArrayLength());
    }

    [Fact]
    public async Task LimitExceeded_WhenSymbolCountExceedsMaxBreadthSymbols()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("MaxBreadthSymbols", "2"));
        // Request 3 symbols when limit is 2
        string args = """{"symbols":["MSFT","AAPL","7203.T"]}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);

        Assert.Equal("LIMIT_EXCEEDED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownSeriesInInclude_ReturnsInvalidArgument()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"include":["bogus_series_name"]}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);

        Assert.Equal("INVALID_ARGUMENT", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConflictingSymbolsAndGroupId_ReturnsInvalidArgument()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = """{"symbols":["MSFT"],"groupId":"00000000-0000-0000-0000-000000000000"}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);

        Assert.Equal("INVALID_ARGUMENT", error.GetProperty("code").GetString());
    }
}
