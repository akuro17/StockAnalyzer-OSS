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
    public const string ActionToolName = "sa_invoke_action";

    private readonly bool _readOnlyOnly;

    public InvokeToolProxyTool(bool readOnlyOnly = true)
    {
        _readOnlyOnly = readOnlyOnly;
    }

    public string Name => _readOnlyOnly ? ToolName : ActionToolName;

    public string Description => _readOnlyOnly
        ? "Invokes a read-only active StockAnalyzer tool discovered via sa_search_tools using the target tool's exact execution lock, timeout, and output contract."
        : "Invokes a state-changing or execution active StockAnalyzer tool discovered via sa_search_tools using the target tool's exact execution lock, timeout, and output contract.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public bool IsReadOnly => _readOnlyOnly;

    public string? ExampleArgumentsJson => _readOnlyOnly
        ? "{\"toolName\":\"sa_get_capabilities\",\"arguments\":{\"category\":\"Oscillator\"}}"
        : "{\"toolName\":\"sa_discussion_create\",\"arguments\":{\"title\":\"Market check\"}}";

    public string InputSchemaJson => ToolSchema.Object()
        .String("toolName", _readOnlyOnly
            ? "Exact read-only tool name discovered via sa_search_tools (isReadOnly: true, for example sa_run_backtest)."
            : "Exact state-changing or execution tool name discovered via sa_search_tools (isReadOnly: false, for example sa_start_training or sa_discussion_create).", required: true)
        .FreeObject("arguments", "JSON object of arguments conforming to the target tool's inputSchema (default {}).")
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{Name} must be unwrapped and dispatched by ToolExecutor.");
}
