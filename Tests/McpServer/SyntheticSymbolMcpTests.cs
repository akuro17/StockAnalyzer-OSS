using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Verifies custom formula ticker symbols (+, -, /, with * rejected) across
/// SymbolResolver, ReadOnlyStockAnalyzerGateway, and MCP tools.
/// </summary>
public sealed class SyntheticSymbolMcpTests : IDisposable
{
    private const string SymbolA = "MSFT";
    private const string SymbolB = "7203-T";
    private const string OffList = "OFFLIST";
    private const int Days = 60;

    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string RsiIndicator = """[{"id":"r","type":"RSI"}]""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, SymbolA, "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public SyntheticSymbolMcpTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcv(_fixture.DailyDirectory, OffList, Days);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, argumentsJson))!;

    #region 1. SymbolResolver Unit Tests

    [Theory]
    [InlineData("7203.T / 7267.T", "7203-T / 7267-T")]
    [InlineData("7203-T/7267-T", "7203-T / 7267-T")]
    [InlineData("AAPL + MSFT", "AAPL + MSFT")]
    [InlineData("AAPL+MSFT", "AAPL + MSFT")]
    [InlineData("7203.T - 7267.T", "7203-T - 7267-T")]
    [InlineData("7203 - 7267", "7203-T - 7267-T")]
    [InlineData("MSFT", "MSFT")]
    [InlineData("7203.t", "7203-T")]
    public void Canonical_SupportedSymbols_ReturnsExpectedCanonical(string input, string expected)
    {
        string result = SymbolResolver.Canonical(input, "symbol");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("AAPL * MSFT")]
    [InlineData("7203.T * 7267.T")]
    [InlineData("7203 * 7267")]
    public void Canonical_MultiplicationOperator_ThrowsInvalidArgument(string input)
    {
        var ex = Assert.Throws<ToolException>(() => SymbolResolver.Canonical(input, "symbol"));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("'*'", ex.Message);
    }

    [Theory]
    [InlineData("///")]
    [InlineData("a/b")]
    [InlineData("AAPL + ")]
    [InlineData("+ MSFT")]
    [InlineData("AAPL + MSFT + GOOG")]
    public void Canonical_MalformedSymbols_ThrowsInvalidArgument(string input)
    {
        var ex = Assert.Throws<ToolException>(() => SymbolResolver.Canonical(input, "symbol"));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task ResolveAsync_BothLegsAvailable_ResolvesCanonical()
    {
        var dispatcher = await StartAsync();
        _runtimes[0].Tools.TryGet("sa_get_candles", out var registered);
        IReadOnlyStockAnalyzerGateway gateway = ((GetCandlesTool)registered.Tool).GetGatewayForTesting();
        var resolver = new SymbolResolver(gateway);

        string resolved = await resolver.ResolveAsync($"{SymbolA} + {SymbolB}", "symbol", CancellationToken.None);
        Assert.Equal($"{SymbolA} + {SymbolB}", resolved);
    }

    [Fact]
    public async Task ResolveAsync_MissingLeg_ThrowsSymbolNotFound()
    {
        var dispatcher = await StartAsync();
        _runtimes[0].Tools.TryGet("sa_get_candles", out var registered);
        IReadOnlyStockAnalyzerGateway gateway = ((GetCandlesTool)registered.Tool).GetGatewayForTesting();
        var resolver = new SymbolResolver(gateway);

        var exLeft = await Assert.ThrowsAsync<ToolException>(() => resolver.ResolveAsync($"NOTEXIST + {SymbolB}", "symbol", CancellationToken.None));
        Assert.Equal(ToolErrorCodes.SymbolNotFound, exLeft.Code);
        Assert.Contains("NOTEXIST", exLeft.Message);

        var exRight = await Assert.ThrowsAsync<ToolException>(() => resolver.ResolveAsync($"{SymbolA} + NOTEXIST", "symbol", CancellationToken.None));
        Assert.Equal(ToolErrorCodes.SymbolNotFound, exRight.Code);
        Assert.Contains("NOTEXIST", exRight.Message);
    }

    [Fact]
    public async Task ResolveStoredAsync_OffListLegWithAllowUnregistered_ResolvesAsUnregistered()
    {
        var dispatcher = await StartAsync();
        _runtimes[0].Tools.TryGet("sa_get_candles", out var registered);
        IReadOnlyStockAnalyzerGateway gateway = ((GetCandlesTool)registered.Tool).GetGatewayForTesting();
        var resolver = new SymbolResolver(gateway);

        ResolvedSymbol resolved = await resolver.ResolveStoredAsync($"{SymbolA} + {OffList}", "symbol", CancellationToken.None);
        Assert.Equal($"{SymbolA} + {OffList}", resolved.Symbol);
        Assert.False(resolved.IsRegistered);
    }

    #endregion

    #region 2. Tool Integration Tests

    [Fact]
    public async Task GetCandlesTool_SyntheticAddition_ReturnsCalculatedCandles()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbol":"{{{SymbolA}}} + {{{SymbolB}}}","timeFrame":"D1","count":10}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_get_candles", args);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal($"{SymbolA} + {SymbolB}", result.GetProperty("symbol").GetString());
        Assert.True(result.GetProperty("barCount").GetInt32() > 0);

        JsonElement candles = result.GetProperty("candles");
        Assert.True(candles.GetArrayLength() > 0);
        JsonElement first = candles[0];
        Assert.True(first.GetProperty("open").GetDecimal() > 0m);
        Assert.True(first.GetProperty("high").GetDecimal() >= first.GetProperty("low").GetDecimal());
    }

    [Fact]
    public async Task GetCandlesTool_SyntheticMultiplication_ReturnsError()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbol":"{{{SymbolA}}} * {{{SymbolB}}}","timeFrame":"D1","count":10}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_get_candles", args);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task CalculateIndicatorsTool_SyntheticSymbol_ComputesIndicators()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbol":"{{{SymbolA}}} - {{{SymbolB}}}","timeFrame":"D1","indicators":{{{RsiIndicator}}}}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_calculate_indicators", args);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal($"{SymbolA} - {SymbolB}", result.GetProperty("symbol").GetString());
        Assert.True(result.GetProperty("results").TryGetProperty("r", out _));
    }

    [Fact]
    public async Task RunBacktestTool_SyntheticSymbol_ExecutesSuccessfully()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbol":"{{{SymbolA}}} + {{{SymbolB}}}","conditions":{"entryLong":{{{Entry}}},"exitLong":{{{Exit}}}}}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_run_backtest", args);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal($"{SymbolA} + {SymbolB}", result.GetProperty("symbol").GetString());
        Assert.True(result.TryGetProperty("report", out _) || result.TryGetProperty("equity", out _));
    }

    [Fact]
    public async Task GetTickerMetadataTool_SyntheticSymbol_ReturnsFoundFalse()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbols":["{{{SymbolA}}} + {{{SymbolB}}}"]}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_get_ticker_metadata", args);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement first = result.GetProperty("results")[0];
        Assert.Equal($"{SymbolA} + {SymbolB}", first.GetProperty("symbol").GetString());
        Assert.False(first.GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("metadata").ValueKind);
    }

    [Fact]
    public async Task GetSymbolCoverageTool_SyntheticSymbol_ReturnsCalculatedCoverage()
    {
        var dispatcher = await StartAsync();
        string args = $$$"""{"symbols":["{{{SymbolA}}} + {{{SymbolB}}}"]}""";
        using JsonDocument response = await CallAsync(dispatcher, "sa_get_symbol_coverage", args);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement first = result.GetProperty("results")[0];
        Assert.Equal($"{SymbolA} + {SymbolB}", first.GetProperty("symbol").GetString());
        Assert.False(first.GetProperty("found").GetBoolean());
        Assert.False(first.GetProperty("hasMetadata").GetBoolean());

        JsonElement timeFrames = first.GetProperty("timeFrames");
        Assert.True(timeFrames.GetArrayLength() > 0);
        JsonElement d1 = timeFrames.EnumerateArray().First(tf => tf.GetProperty("timeFrame").GetString() == "D1");
        Assert.True(d1.GetProperty("barCount").GetInt32() > 0);
    }

    #endregion
}

internal static class GetCandlesToolTestingExtensions
{
    public static IReadOnlyStockAnalyzerGateway GetGatewayForTesting(this GetCandlesTool tool)
    {
        var field = typeof(GetCandlesTool).GetField("_loader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var loader = field!.GetValue(tool)!;
        var gatewayField = loader.GetType().GetField("_gateway", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IReadOnlyStockAnalyzerGateway)gatewayField!.GetValue(loader)!;
    }
}
