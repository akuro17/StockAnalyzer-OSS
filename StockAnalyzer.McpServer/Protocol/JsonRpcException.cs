namespace StockAnalyzer.McpServer.Protocol;

/// <summary>A protocol-level failure that becomes a JSON-RPC error response.</summary>
internal sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message) : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
