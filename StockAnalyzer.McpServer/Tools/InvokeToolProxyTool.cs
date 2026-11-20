using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Proxy meta-tool exposed in progressive discovery mode (<c>--progressive-discovery</c>).
/// <see cref="Execution.ToolExecutor"/> unwraps the target tool name and arguments before acquiring any execution turn lock,
/// preserving exact target timeouts, concurrency locks, <see cref="IMcpImageTool"/> binary image blocks, and command-log attribution.
/// </summary>
internal sealed class InvokeToolProxyTool : IMcpTool
{
    public const string ToolName = "sa_invoke_tool";

    public string Name => ToolName;

    public string Description =>
        "Invokes an active StockAnalyzer tool discovered via sa_search_tools using the target tool's exact execution lock, timeout, and output contract.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public bool IsReadOnly => false;

    public string? ExampleArgumentsJson =>
        "{\"toolName\":\"sa_get_capabilities\",\"arguments\":{\"category\":\"Oscillator\"}}";

    public string InputSchemaJson => ToolSchema.Object()
        .String("toolName", "Exact tool name discovered via sa_search_tools (for example sa_run_backtest).", required: true)
        .FreeObject("arguments", "JSON object of arguments conforming to the target tool's inputSchema (default {}).")
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{ToolName} must be unwrapped and dispatched by ToolExecutor.");
}
