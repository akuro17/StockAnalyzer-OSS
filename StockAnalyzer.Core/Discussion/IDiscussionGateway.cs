using System;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>The operation names a gateway understands (spec 5.14). The wire between processes uses the same names.</summary>
public static class DiscussionOperations
{
    public const string Create = "create";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Post = "post";
    public const string Read = "read";
    public const string Status = "status";
    public const string Close = "close";

    /// <summary>Step P2 of an analysis snapshot: could an attach succeed (token, open discussion, room for a context)? Nothing is reserved.</summary>
    public const string AttachPrecheck = "attach_precheck";

    /// <summary>Step P4 of an analysis snapshot: store the result the calling process computed in the meantime.</summary>
    public const string AttachCommit = "attach_commit";

    public const string GetAnalysis = "get_analysis";

    /// <summary>
    /// Only the part of the status that belongs to the board itself (the pipe carries this one instead of <see cref="Status"/>): the process that asks adds its own part,
    /// its role, sharing key and command log, as data (<see cref="DiscussionStatusComposer"/>).
    /// </summary>
    public const string StatusHubPart = "status_hub";

    /// <summary>The operations another process may ask of a board over the pipe. <see cref="Status"/> is not among them: a status always shows the asking process.</summary>
    public static bool IsWireOperation(string operation) => operation is Create or Join or Leave or Post or Read or StatusHubPart or Close or AttachPrecheck or AttachCommit or GetAnalysis;

    /// <summary>The operations that may be asked twice without a second effect (spec 5.12): the router retries only these after a pipe failure. A post is one of them only with a <c>clientRequestId</c>.</summary>
    public static bool IsRepeatable(string operation, bool hasClientRequestId) => operation is Read or StatusHubPart or GetAnalysis || (operation == Post && hasClientRequestId);
}

/// <summary>One board operation as text: the operation name, who asks (the client's label) and the arguments as one JSON object.</summary>
public sealed record DiscussionRequest(string Operation, string CallerLabel, string ArgumentsJson);

/// <summary>The error part of a refused operation: a code of <see cref="DiscussionErrorCodes"/>, an English message, and optionally the argument and a hint.</summary>
public sealed record DiscussionFailure(string Code, string Message, string? Parameter, string? Hint);

/// <summary>The answer to a <see cref="DiscussionRequest"/>: the result as one JSON object, or the failure.</summary>
public sealed class DiscussionReply
{
    private DiscussionReply(string? resultJson, DiscussionFailure? failure)
    {
        ResultJson = resultJson;
        Failure = failure;
    }

    public bool IsOk => Failure is null;

    public string? ResultJson { get; }

    public DiscussionFailure? Failure { get; }

    public static DiscussionReply Ok(string resultJson)
    {
        ArgumentNullException.ThrowIfNull(resultJson);
        return new DiscussionReply(resultJson, null);
    }

    public static DiscussionReply Fail(string code, string message, string? parameter = null, string? hint = null)
        => new(null, new DiscussionFailure(code, message, parameter, hint));
}

/// <summary>
/// What the MCP tools talk to. The tools check the shape of the arguments; the gateway (and the hub behind it) checks their meaning and every limit, because
/// the request may come from another process. Text in, text out: nothing here depends on the protocol layer.
/// </summary>
public interface IDiscussionGateway
{
    Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken);
}
