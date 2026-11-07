using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.McpServer.Tools.Discussion;

/// <summary>
/// The shared part of the discussion tools (offered only with <c>--enable-discussion</c>). A tool here is a schema and a mapping: it checks the shape of the arguments
/// (the executor does, against the schema), hands them to the gateway as text and returns the gateway's JSON result, or turns the gateway's failure into a
/// <see cref="ToolException"/>. The meaning of the arguments and every limit are checked by the board behind the gateway.
/// </summary>
internal abstract class DiscussionToolBase : IMcpTool
{
    private readonly IDiscussionGateway _gateway;
    private readonly ClientIdentity? _identity;

    protected DiscussionToolBase(IDiscussionGateway gateway, ClientIdentity? identity)
    {
        _gateway = gateway;
        _identity = identity;
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string InputSchemaJson { get; }

    public abstract string? ExampleArgumentsJson { get; }

    /// <summary>The operation name the gateway understands.</summary>
    protected abstract string Operation { get; }

    public bool IsHeavy => false;

    /// <summary>The board's tools never take the single tool turn: a read may wait for other participants and must not stop every other tool.</summary>
    public bool UsesExecutionTurn => false;

    /// <summary>A discussion tool changes the in-memory board and is not idempotent, except the status.</summary>
    public virtual bool IsReadOnly => false;

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => DiscussionToolCalls.ExecuteAsync(_gateway, Operation, _identity, arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : "{}", cancellationToken);
}

/// <summary>The one place where a gateway failure becomes a tool failure.</summary>
internal static class DiscussionToolCalls
{
    public static async Task<string> ExecuteAsync(IDiscussionGateway gateway, string operation, ClientIdentity? identity, string argumentsJson, CancellationToken cancellationToken)
    {
        var request = new DiscussionRequest(operation, identity?.Label ?? string.Empty, argumentsJson);
        DiscussionReply reply = await gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Failure is { } failure)
        {
            throw new ToolException(failure.Code, failure.Message, failure.Parameter, failure.Hint);
        }

        return reply.ResultJson!;
    }

    /// <summary>A refusal raised by Core code that runs in the tool layer (the canonical form of the arguments) as a tool failure.</summary>
    public static ToolException ToToolException(DiscussionException error) => new(error.Code, error.Message, error.Parameter, error.Hint);
}
