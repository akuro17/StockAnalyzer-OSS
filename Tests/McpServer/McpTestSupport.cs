using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>A tool whose behaviour a test supplies (a real implementation of the tool interface, not a mock of project data types).</summary>
internal sealed class FakeTool : IMcpTool
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _invoke;

    public FakeTool(string name, Func<JsonElement, CancellationToken, Task<string>> invoke, string? schemaJson = null, bool isHeavy = false, bool isExecution = false)
    {
        Name = name;
        _invoke = invoke;
        InputSchemaJson = schemaJson ?? ToolSchema.Object().Build();
        IsHeavy = isHeavy;
        IsExecution = isExecution;
    }

    public bool IsExecution { get; }

    public string Name { get; }

    public string Description => "Test tool.";

    public string InputSchemaJson { get; }

    public bool IsHeavy { get; }

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => _invoke(arguments, cancellationToken);
}

internal static class McpTestSupport
{
    /// <summary>A configuration whose data folders do not exist (the server then reports an incomplete data folder and serves no data), with optional <c>AiIntegration</c> limit overrides.</summary>
    public static IConfiguration EmptyDataConfiguration(string emptyRoot, params (string Key, int Value)[] limits)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = Path.Combine(emptyRoot, "Daily"),
            ["MarketDataSettings:WeeklyDataPath"] = Path.Combine(emptyRoot, "Weekly"),
            ["MarketDataSettings:MonthlyDataPath"] = Path.Combine(emptyRoot, "Monthly"),
            ["MarketDataSettings:MetadataPath"] = Path.Combine(emptyRoot, "Metadata"),
            ["MarketDataSettings:TickerListPath"] = Path.Combine(emptyRoot, "Tickers.csv"),
        };
        foreach ((string key, int value) in limits)
        {
            values[$"AiIntegration:{key}"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public const string InitializeMessage = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    public static McpMessageDispatcher CreateDispatcher(IEnumerable<IMcpTool> tools, McpLimits? limits = null, IEnumerable<string?>? roots = null, StringWriter? log = null)
    {
        limits ??= new McpLimits();
        var sink = new LogSink(log ?? new StringWriter());
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(roots ?? Array.Empty<string?>()), sink);
        return new McpMessageDispatcher(sink, new McpToolRegistry(tools), executor);
    }

    public static async Task<McpMessageDispatcher> InitializedAsync(IEnumerable<IMcpTool> tools, McpLimits? limits = null, IEnumerable<string?>? roots = null)
    {
        McpMessageDispatcher dispatcher = CreateDispatcher(tools, limits, roots);
        await dispatcher.DispatchAsync(InitializeMessage, CancellationToken.None);
        return dispatcher;
    }

    public static string CallMessage(int id, string tool, string argumentsJson = "{}")
        => $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argumentsJson}}}}}""";

    /// <summary>Calls a tool and returns the whole JSON-RPC response; null when the call was cancelled.</summary>
    public static async Task<JsonDocument?> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson = "{}", int id = 1)
    {
        string? response = await dispatcher.DispatchAsync(CallMessage(id, tool, argumentsJson), CancellationToken.None);
        return response is null ? null : JsonDocument.Parse(response);
    }

    public static JsonElement Result(JsonDocument response) => response.RootElement.GetProperty("result");

    public static JsonElement Structured(JsonDocument response) => Result(response).GetProperty("structuredContent");

    public static bool IsError(JsonDocument response) => Result(response).GetProperty("isError").GetBoolean();

    public static string ErrorCodeOf(JsonDocument response) => Structured(response).GetProperty("code").GetString()!;

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 30)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "condition not reached in time");
            await Task.Delay(10);
        }
    }
}
