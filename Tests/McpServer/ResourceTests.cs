using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Resources;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>MCP resources: the protocol methods on a dispatcher with plain resources, and the three real resources through the real composition.</summary>
public sealed class ResourceTests : IDisposable
{
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private static McpMessageDispatcher DispatcherWith(params McpResource[] resources)
    {
        var sink = new LogSink(new StringWriter());
        var executor = new ToolExecutor(new StockAnalyzer.McpServer.Limits.McpLimits(), new ErrorMessageSanitizer(Array.Empty<string?>()), sink);
        return new McpMessageDispatcher(sink, new McpToolRegistry(Array.Empty<IMcpTool>()), executor, resources.Length == 0 ? null : new McpResourceRegistry(resources));
    }

    private static McpResource Plain(string uri, string text = "{}") => new(uri, "Name " + uri, "Description of " + uri, McpResourceRegistry.JsonMimeType, () => text);

    private static async Task<JsonDocument> SendAsync(McpMessageDispatcher dispatcher, string method, string paramsJson = "{}", int id = 7)
        => JsonDocument.Parse((await dispatcher.DispatchAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""", CancellationToken.None))!);

    private static async Task<McpMessageDispatcher> InitializedWithAsync(params McpResource[] resources)
    {
        McpMessageDispatcher dispatcher = DispatcherWith(resources);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        await dispatcher.DispatchAsync(Initialized, CancellationToken.None);
        return dispatcher;
    }

    private async Task<McpMessageDispatcher> StartRealAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        }).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static string ReadText(JsonDocument response) => response.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;

    private static int ErrorCode(JsonDocument response) => response.RootElement.GetProperty("error").GetProperty("code").GetInt32();

    // ---- protocol -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InitializeDeclaresTheResourcesCapabilityOnlyWhenThereAreResources()
    {
        using var with = await SendAsync(DispatcherWith(Plain("stockanalyzer://a")), "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t"}}""");
        using var without = await SendAsync(DispatcherWith(), "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t"}}""");

        Assert.Equal(new[] { "resources", "tools" }, with.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "tools" }, without.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task ListReturnsEveryResourceWithUriNameDescriptionAndMimeType()
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a"), Plain("stockanalyzer://b"));

        using var response = await SendAsync(dispatcher, "resources/list");

        JsonElement[] resources = response.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
        Assert.Equal(new[] { "stockanalyzer://a", "stockanalyzer://b" }, resources.Select(r => r.GetProperty("uri").GetString()).ToArray());
        Assert.All(resources, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("description").GetString()));
            Assert.Equal("application/json", r.GetProperty("mimeType").GetString());
        });
    }

    [Fact]
    public async Task ReadReturnsTheContentWithItsUriAndMimeType()
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a", """{"x":1}"""));

        using var response = await SendAsync(dispatcher, "resources/read", """{"uri":"stockanalyzer://a"}""");

        JsonElement content = response.RootElement.GetProperty("result").GetProperty("contents")[0];
        Assert.Equal("stockanalyzer://a", content.GetProperty("uri").GetString());
        Assert.Equal("application/json", content.GetProperty("mimeType").GetString());
        Assert.Equal("""{"x":1}""", content.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"uri":"stockanalyzer://nothing"}""")]
    [InlineData("""{"uri":"STOCKANALYZER://A"}""")]
    [InlineData("""{"uri":"file:///etc/passwd"}""")]
    [InlineData("""{"uri":"stockanalyzer://a/../a"}""")]
    public async Task AnUnknownUriIsResourceNotFound(string paramsJson)
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a"));

        using var response = await SendAsync(dispatcher, "resources/read", paramsJson);

        Assert.Equal(JsonRpcErrorCodes.ResourceNotFound, ErrorCode(response));
        Assert.Equal(-32002, ErrorCode(response));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"uri":""}""")]
    [InlineData("""{"uri":5}""")]
    [InlineData("""{"uri":null}""")]
    public async Task AReadWithoutAUsableUriIsInvalidParams(string paramsJson)
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a"));

        using var response = await SendAsync(dispatcher, "resources/read", paramsJson);

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("resources/templates/list")]
    public async Task ACursorIsInvalidBecauseThereIsNoPagination(string method)
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a"));

        using var response = await SendAsync(dispatcher, method, """{"cursor":"x"}""");

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
    }

    [Fact]
    public async Task TheTemplateListIsEmpty()
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync(Plain("stockanalyzer://a"));

        using var response = await SendAsync(dispatcher, "resources/templates/list");

        Assert.Empty(response.RootElement.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray());
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("resources/read")]
    [InlineData("resources/templates/list")]
    public async Task BeforeInitializeTheResourceMethodsNeedTheHandshake(string method)
    {
        using var response = await SendAsync(DispatcherWith(Plain("stockanalyzer://a")), method, """{"uri":"stockanalyzer://a"}""");

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("resources/read")]
    [InlineData("resources/templates/list")]
    [InlineData("resources/subscribe")]
    public async Task WithoutResourcesTheResourceMethodsAreUnknown(string method)
    {
        McpMessageDispatcher dispatcher = await InitializedWithAsync();

        using var response = await SendAsync(dispatcher, method, """{"uri":"stockanalyzer://a"}""");

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ErrorCode(response));
    }

    [Fact]
    public async Task AResourceThatThrowsIsAnInternalErrorWithoutTheMessage()
    {
        var failing = new McpResource("stockanalyzer://boom", "Boom", "Fails.", McpResourceRegistry.JsonMimeType, () => throw new InvalidOperationException(@"C:\secret\path"));
        McpMessageDispatcher dispatcher = await InitializedWithAsync(failing);

        using var response = await SendAsync(dispatcher, "resources/read", """{"uri":"stockanalyzer://boom"}""");

        Assert.Equal(JsonRpcErrorCodes.InternalError, ErrorCode(response));
        Assert.DoesNotContain("secret", response.RootElement.GetRawText());
    }

    // ---- the real resources ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheServerOffersTheThreeCatalogueAndSettingsResources()
    {
        McpMessageDispatcher dispatcher = await StartRealAsync();

        using var response = await SendAsync(dispatcher, "resources/list");

        Assert.Equal(
            new[] { "stockanalyzer://catalog/indicators", "stockanalyzer://catalog/screener", "stockanalyzer://settings/current" },
            response.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray());
    }

    [Fact]
    public async Task TheIndicatorCatalogueResourceEqualsTheDetailedCapabilitiesResult()
    {
        McpMessageDispatcher dispatcher = await StartRealAsync();

        using var resource = await SendAsync(dispatcher, "resources/read", """{"uri":"stockanalyzer://catalog/indicators"}""");
        using var tool = (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"includeDetails":true}"""))!;

        Assert.Equal(McpTestSupport.Structured(tool).GetRawText(), ReadText(resource));
        Assert.True(JsonDocument.Parse(ReadText(resource)).RootElement.GetProperty("indicatorCount").GetInt32() > 150);
    }

    [Fact]
    public async Task TheScreenerCatalogueResourceEqualsTheScreenerPartOfTheCapabilitiesResult()
    {
        McpMessageDispatcher dispatcher = await StartRealAsync();

        using var resource = await SendAsync(dispatcher, "resources/read", """{"uri":"stockanalyzer://catalog/screener"}""");
        using var tool = (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"indicatorTypeFilter":["RSI"],"includeScreenerCatalog":true}"""))!;

        Assert.Equal(McpTestSupport.Structured(tool).GetProperty("screener").GetRawText(), ReadText(resource));
    }

    [Fact]
    public async Task TheSettingsResourceEqualsTheSettingsToolResultAndHoldsNoPathOrCredential()
    {
        McpMessageDispatcher dispatcher = await StartRealAsync();

        using var resource = await SendAsync(dispatcher, "resources/read", """{"uri":"stockanalyzer://settings/current"}""");
        using var tool = (await McpTestSupport.CallAsync(dispatcher, "sa_get_settings"))!;

        string text = ReadText(resource);
        Assert.Equal(McpTestSupport.Structured(tool).GetRawText(), text);
        Assert.DoesNotContain(_fixture.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"[A-Za-z]:\\", text);
    }

    [Fact]
    public async Task ReadingEveryResourceIncludingAFailureLeavesTheDataTreeUnchanged()
    {
        var before = _fixture.Snapshot();
        McpMessageDispatcher dispatcher = await StartRealAsync();

        foreach (string uri in new[] { "stockanalyzer://catalog/indicators", "stockanalyzer://catalog/screener", "stockanalyzer://settings/current", "stockanalyzer://nothing" })
        {
            (await SendAsync(dispatcher, "resources/read", $$"""{"uri":"{{uri}}"}""")).Dispose();
        }

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task EveryResourceFitsTheResponseLimit()
    {
        McpMessageDispatcher dispatcher = await StartRealAsync();
        int limit = new StockAnalyzer.McpServer.Limits.McpLimits().MaxResponseBytes;

        foreach (string uri in new[] { "stockanalyzer://catalog/indicators", "stockanalyzer://catalog/screener", "stockanalyzer://settings/current" })
        {
            string response = (await dispatcher.DispatchAsync($$$"""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"{{{uri}}}"}}""", CancellationToken.None))!;
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(response) < limit, uri);
        }
    }
}
