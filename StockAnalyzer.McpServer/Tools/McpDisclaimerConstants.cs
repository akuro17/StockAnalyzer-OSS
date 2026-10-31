namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Single source of truth for non-advisory and predictive model disclaimers emitted by MCP tools.
/// </summary>
internal static class McpDisclaimerConstants
{
    /// <summary>
    /// Explicit warning text that numbers are model outputs for research and do not constitute financial advice.
    /// </summary>
    public const string ResearchNotAdvice = "These numbers are model outputs for research, not advice.";
}
