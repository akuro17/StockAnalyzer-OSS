using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>Protocol conformance of the hand-written layer (D3-B): lifecycle, error codes, framing.</summary>
public sealed class McpMessageDispatcherTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private static McpMessageDispatcher Create() => McpTestSupport.CreateDispatcher(Array.Empty<IMcpTool>());

    private static async Task<JsonDocument?> SendAsync(McpMessageDispatcher dispatcher, string message)
    {
        string? response = await dispatcher.DispatchAsync(message, CancellationToken.None);
        if (response is null)
        {
            return null;
        }

        Assert.DoesNotContain('\n', response);
        Assert.DoesNotContain('\r', response);
        return JsonDocument.Parse(response);
    }

    private static async Task<McpMessageDispatcher> InitializedAsync()
    {
        var dispatcher = Create();
        (await SendAsync(dispatcher, Initialize))!.Dispose();
        return dispatcher;
    }

    private static int ErrorCode(JsonDocument response) => response.RootElement.GetProperty("error").GetProperty("code").GetInt32();

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-11-25")]
    [InlineData("2024-11-05", "2025-11-25")]
    [InlineData("2026-07-28", "2025-11-25")]
    [InlineData("1999-01-01", "2025-11-25")]
    public async Task Initialize_AnswersTheRequestedVersionWhenSupportedElseTheNewestSupported(string requested, string expected)
    {
        var dispatcher = Create();
        string message = $$$$"""{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"{{{{requested}}}}","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

        using var response = (await SendAsync(dispatcher, message))!;

        JsonElement result = response.RootElement.GetProperty("result");
        Assert.Equal(expected, result.GetProperty("protocolVersion").GetString());
        Assert.Equal("a", response.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Initialize_DeclaresOnlyTheToolsCapabilityAndNamesTheServer()
    {
        using var response = (await SendAsync(Create(), Initialize))!;

        JsonElement result = response.RootElement.GetProperty("result");
        Assert.Equal(new[] { "tools" }, result.GetProperty("capabilities").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("StockAnalyzer.McpServer", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("serverInfo").GetProperty("version").GetString()));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"capabilities":{},"clientInfo":{"name":"t"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"t"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{}}}""")]
    public async Task Initialize_WithMissingOrMistypedFields_IsInvalidParams(string message)
    {
        using var response = (await SendAsync(Create(), message))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
    }

    [Fact]
    public async Task Initialize_Twice_IsInvalidRequest()
    {
        var dispatcher = await InitializedAsync();

        using var response = (await SendAsync(dispatcher, Initialize))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
    }

    [Fact]
    public async Task Ping_IsAnsweredBeforeInitializationWithAnEmptyResult()
    {
        using var response = (await SendAsync(Create(), """{"jsonrpc":"2.0","id":7,"method":"ping"}"""))!;

        Assert.Equal(JsonValueKind.Object, response.RootElement.GetProperty("result").ValueKind);
        Assert.Empty(response.RootElement.GetProperty("result").EnumerateObject());
        Assert.Equal(7, response.RootElement.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("tools/list")]
    [InlineData("tools/call")]
    public async Task ServedRequestsBeforeInitialize_AreInvalidRequests(string method)
    {
        using var response = (await SendAsync(Create(), $$$"""{"jsonrpc":"2.0","id":1,"method":"{{{method}}}","params":{"name":"x"}}"""))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
    }

    // Antigravity sends server/discover before initialize and falls back to initialize when the server says the method does not exist.
    [Theory]
    [InlineData("server/discover")]
    [InlineData("resources/list")]
    [InlineData("nonsense")]
    public async Task UnservedMethodsBeforeInitialize_AreMethodNotFoundNotInvalidRequest(string method)
    {
        using var response = (await SendAsync(Create(), $$$"""{"jsonrpc":"2.0","id":1,"method":"{{{method}}}","params":{}}"""))!;

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ErrorCode(response));
    }

    [Fact]
    public async Task AfterAnUnservedFirstRequestAndAnEarlyNotification_InitializeStillSucceeds()
    {
        var dispatcher = Create();

        using var discover = (await SendAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{}}"""))!;
        string? early = await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""", CancellationToken.None);
        using var initialize = (await SendAsync(dispatcher, Initialize))!;
        string? initialized = await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);
        using var list = (await SendAsync(dispatcher, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}"""))!;

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ErrorCode(discover));
        Assert.Null(early);
        Assert.Equal("2025-06-18", initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Null(initialized);
        Assert.Equal(JsonValueKind.Array, list.RootElement.GetProperty("result").GetProperty("tools").ValueKind);
    }

    [Fact]
    public async Task ToolsList_WithoutRegisteredTools_IsEmpty()
    {
        var dispatcher = await InitializedAsync();

        using var response = (await SendAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))!;

        Assert.Equal(0, response.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ToolsList_WithACursor_IsInvalidParams()
    {
        var dispatcher = await InitializedAsync();

        using var response = (await SendAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"cursor":"x"}}"""))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sa_nothing","arguments":{}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sa_nothing"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":""}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":5}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sa_nothing","arguments":[]}}""")]
    public async Task ToolsCall_UnknownToolOrBadShape_IsInvalidParams(string message)
    {
        var dispatcher = await InitializedAsync();

        using var response = (await SendAsync(dispatcher, message))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
        Assert.Equal(3, response.RootElement.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("prompts/list")]
    [InlineData("logging/setLevel")]
    [InlineData("completion/complete")]
    [InlineData("server/discover")]
    [InlineData("nonsense")]
    public async Task MethodsOfUndeclaredCapabilities_AreMethodNotFound(string method)
    {
        var dispatcher = await InitializedAsync();

        using var response = (await SendAsync(dispatcher, $$$"""{"jsonrpc":"2.0","id":4,"method":"{{{method}}}"}"""))!;

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ErrorCode(response));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/unknown"}""")]
    public async Task Notifications_GetNoResponse(string message)
    {
        var dispatcher = await InitializedAsync();

        Assert.Null(await SendAsync(dispatcher, message));
    }

    [Fact]
    public async Task ResponsesFromTheClient_AreIgnored()
    {
        Assert.Null(await SendAsync(Create(), """{"jsonrpc":"2.0","id":1,"result":{}}"""));
        Assert.Null(await SendAsync(Create(), """{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"x"}}"""));
    }

    [Theory]
    [InlineData("{not json", JsonRpcErrorCodes.ParseError)]
    [InlineData("", JsonRpcErrorCodes.ParseError)]
    [InlineData("[]", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("42", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", JsonRpcErrorCodes.InvalidRequest)]
    public async Task MalformedMessages_GetTheStandardJsonRpcError(string message, int expectedCode)
    {
        using var response = (await SendAsync(Create(), message))!;

        Assert.Equal(expectedCode, ErrorCode(response));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    [Theory]
    [InlineData("""{"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    public async Task InvalidRequestsWithADetectableId_EchoThatId(string message)
    {
        using var response = (await SendAsync(Create(), message))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
        Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ParamsThatAreNotAnObject_AreInvalidParamsAndKeepTheRequestId()
    {
        using var response = (await SendAsync(Create(), """{"jsonrpc":"2.0","id":"x-1","method":"ping","params":[1]}"""))!;

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ErrorCode(response));
        Assert.Equal("x-1", response.RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"abc\"")]
    [InlineData("9007199254740993")]
    [InlineData("1.5")]
    public async Task TheRequestIdIsEchoedExactly(string idJson)
    {
        using var response = (await SendAsync(Create(), $$$"""{"jsonrpc":"2.0","id":{{{idJson}}},"method":"ping"}"""))!;

        Assert.Equal(idJson, response.RootElement.GetProperty("id").GetRawText());
    }

    [Fact]
    public async Task ResponsesStayOnOneLineEvenWhenTheEchoedIdContainsEscapes()
    {
        string message = "{\"jsonrpc\":\"2.0\",\"id\":\"a\\nb\\u2028c\",\"method\":\"ping\"}";

        string? response = await Create().DispatchAsync(message, CancellationToken.None);

        Assert.DoesNotContain('\n', response!);
        Assert.Equal("a\nb\u2028c", JsonDocument.Parse(response!).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void TheSupportedVersionsAreNewestFirstAndAllDefineStructuredContent()
    {
        Assert.Equal(new[] { "2025-11-25", "2025-06-18" }, McpProtocolVersions.Supported);
        Assert.Equal("2025-11-25", McpProtocolVersions.Negotiate("anything"));
        Assert.Equal("2025-06-18", McpProtocolVersions.Negotiate("2025-06-18"));
    }
}
