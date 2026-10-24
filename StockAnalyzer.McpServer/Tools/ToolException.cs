namespace StockAnalyzer.McpServer.Tools;

/// <summary>A failure a tool reports to the client as an isError result: a catalogue code, a message, and optionally the argument and a hint.</summary>
internal sealed class ToolException : Exception
{
    public ToolException(string code, string message, string? parameter = null, string? hint = null) : base(message)
    {
        Code = code;
        Parameter = parameter;
        Hint = hint;
    }

    public string Code { get; }

    public string? Parameter { get; }

    public string? Hint { get; }
}
