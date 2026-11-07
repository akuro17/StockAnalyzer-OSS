using StockAnalyzer.Core.Discussion;

namespace StockAnalyzer.McpServer.Execution;

/// <summary>
/// Hands every tool call the executor handled to the command log. The log itself (ring, optional file) lives in Core: this assembly may not write files. A
/// <see cref="ToolCallRecord"/> holds argument names and sizes only, and so does the log.
/// </summary>
internal sealed class CommandLogObserver : IToolCallObserver
{
    private readonly ICommandLog _log;

    public CommandLogObserver(ICommandLog log)
    {
        _log = log;
    }

    public void OnToolCall(ToolCallRecord call)
        => _log.Record(call.ToolName, call.ArgumentKeys, call.ArgumentBytes, call.Outcome, (long)call.Elapsed.TotalMilliseconds, call.RequestId);
}
