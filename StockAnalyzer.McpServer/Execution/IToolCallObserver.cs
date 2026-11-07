namespace StockAnalyzer.McpServer.Execution;

/// <summary>
/// What an observer learns about one finished tool call. It carries no argument value: only the argument names and the size of the arguments, so a log built from it
/// can never leak a token or a message body.
/// </summary>
/// <param name="ToolName">The tool that was called.</param>
/// <param name="RequestId">The raw JSON text of the JSON-RPC id.</param>
/// <param name="ArgumentKeys">The top-level argument names in the order they were sent.</param>
/// <param name="ArgumentBytes">The UTF-8 size of the arguments object as received.</param>
/// <param name="Outcome"><see cref="ToolCallOutcomes.Ok"/>, the error code of the result, or <see cref="ToolCallOutcomes.Cancelled"/>.</param>
/// <param name="Elapsed">Wall time from the start of the call to its end, the wait for the turn included.</param>
internal sealed record ToolCallRecord(string ToolName, string RequestId, IReadOnlyList<string> ArgumentKeys, int ArgumentBytes, string Outcome, TimeSpan Elapsed);

/// <summary>The outcome words of <see cref="ToolCallRecord.Outcome"/> that are not an error code.</summary>
internal static class ToolCallOutcomes
{
    public const string Ok = "ok";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Told once about every tool call the executor handled: successes, validation errors, SERVER_BUSY, timeouts and cancellations. A failure inside an observer is
/// swallowed by the executor and never changes the result of the call.
/// </summary>
internal interface IToolCallObserver
{
    void OnToolCall(ToolCallRecord call);
}
