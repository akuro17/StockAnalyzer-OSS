namespace StockAnalyzer.McpServer.Protocol;

/// <summary>JSON-RPC 2.0 error codes used at protocol level (tool failures are results with isError, not these).</summary>
internal static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>The MCP code for a resource URI the server does not have.</summary>
    public const int ResourceNotFound = -32002;
}
