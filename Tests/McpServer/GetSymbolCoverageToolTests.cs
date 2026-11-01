using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class GetSymbolCoverageToolTests : IDisposable
{
    private const int Days = 300;
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(McpLimits? limits = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        if (limits is not null)
        {
            values["AiIntegration:MaxSymbolsPerMetadataCall"] = limits.MaxSymbolsPerMetadataCall.ToString();
        }

        McpServerRuntime runtime = McpServerComposition.Create(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            Path.GetTempPath());

        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    [Fact]
    public async Task RegisteredSymbolReturnsFoundWithAccurateTimeFramesAndMatchesCandles()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft Corporation" });
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["MSFT"]}"""))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());

        JsonElement root = McpTestSupport.Structured(response);
        JsonElement results = root.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());

        JsonElement item = results[0];
        Assert.Equal("MSFT", item.GetProperty("symbol").GetString());
        Assert.True(item.GetProperty("found").GetBoolean());
        Assert.True(item.GetProperty("hasMetadata").GetBoolean());
        Assert.NotNull(item.GetProperty("timeSeriesLastUpdated").GetString());

        JsonElement timeFrames = item.GetProperty("timeFrames");
        Assert.True(timeFrames.GetArrayLength() >= 1);

        JsonElement d1 = default;
        bool hasD1 = false;
        foreach (JsonElement tf in timeFrames.EnumerateArray())
        {
            if (tf.GetProperty("timeFrame").GetString() == "D1")
            {
                d1 = tf;
                hasD1 = true;
                break;
            }
        }
        Assert.True(hasD1);
        Assert.Equal(Days, d1.GetProperty("barCount").GetInt32());

        // Cross-check with sa_get_candles (loading full history)
        using JsonDocument candleResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_get_candles", $$"""{"symbol":"MSFT","timeFrame":"D1","count":{{Days}}}"""))!;
        Assert.False(McpTestSupport.IsError(candleResponse));
        JsonElement candleRoot = McpTestSupport.Structured(candleResponse);
        string earliestCandleDate = candleRoot.GetProperty("earliestAvailableTimestamp").GetString()!;
        Assert.Equal(earliestCandleDate, d1.GetProperty("earliestTimestamp").GetString());
    }

    [Fact]
    public async Task UnregisteredSymbolReturnsFoundFalseWithoutFailingTool()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["UNKNOWN999"]}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement root = McpTestSupport.Structured(response);
        JsonElement results = root.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());

        JsonElement item = results[0];
        Assert.Equal("UNKNOWN999", item.GetProperty("symbol").GetString());
        Assert.False(item.GetProperty("found").GetBoolean());
        Assert.False(item.GetProperty("hasMetadata").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("timeSeriesLastUpdated").ValueKind);
        Assert.Equal(0, item.GetProperty("timeFrames").GetArrayLength());
    }

    [Fact]
    public async Task PreservesInputOrderAndFoldsDuplicates()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["7203-T","msft","7203-T","MSFT"]}"""))!;
        Assert.False(McpTestSupport.IsError(response));

        JsonElement results = McpTestSupport.Structured(response).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("7203-T", results[0].GetProperty("symbol").GetString());
        Assert.Equal("MSFT", results[1].GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task EmptySymbolsRefusedWithInvalidArgument()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":[]}"""))!;
        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task SymbolsExceedingLimitRefusedWithLimitExceeded()
    {
        var dispatcher = await StartAsync(new McpLimits { MaxSymbolsPerMetadataCall = 2 });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["A","B","C"]}"""))!;
        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("symbols", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task DuplicateSymbolsExceedingRawLimitRefusedWithLimitExceeded()
    {
        var dispatcher = await StartAsync(new McpLimits { MaxSymbolsPerMetadataCall = 2 });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["A","A","A"]}"""))!;
        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("symbols", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task MalformedSymbolAtSpecificIndexReportsIndexedParameterPath()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["MSFT","invalid:colon"]}"""))!;
        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("symbols[1]", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }
}
