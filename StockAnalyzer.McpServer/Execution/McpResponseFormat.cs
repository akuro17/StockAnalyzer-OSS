namespace StockAnalyzer.McpServer.Execution;

/// <summary>
/// Selects whether MCP <c>tools/call</c> results emit both <c>content[0].text</c> and <c>structuredContent</c> (<see cref="Dual"/>, default)
/// or omit <c>structuredContent</c> to reduce response token usage (<see cref="TextOnly"/>).
/// </summary>
public enum McpResponseFormat
{
    Dual = 0,
    TextOnly = 1,
}
