using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.McpServer.Tools.Discussion;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The "no wire bump for a new optional argument" rule of wire version 4 holds only while a client forwards exactly the keys its caller sent: a key added for an omitted
/// argument, even with its default value, would be refused by every older hub that holds the strict check. This guard records what every discussion tool hands to the
/// gateway. A later change that adds a defaulting override fails here until it updates the exception lists below on purpose.
/// </summary>
public sealed class DiscussionForwardedKeysTests : IDisposable
{
    private readonly DiscussionSettings _settings = new();
    private readonly DiscussionHub _hub;

    public DiscussionForwardedKeysTests()
    {
        _hub = new DiscussionHub(_settings, startSweeper: false);
    }

    public void Dispose() => _hub.Dispose();

    /// <summary>A gateway that records every request it is given and passes it on to the real gateway of the board.</summary>
    private sealed class RecordingGateway(IDiscussionGateway inner) : IDiscussionGateway
    {
        public List<DiscussionRequest> Requests { get; } = new();

        public Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private static HashSet<string> KeysOf(DiscussionRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(request.ArgumentsJson);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> KeysOf(string callerArgumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(callerArgumentsJson);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<string> InvokeAsync(IMcpTool tool, string argumentsJson)
    {
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
        return await tool.InvokeAsync(arguments.RootElement.Clone(), CancellationToken.None);
    }

    /// <summary>
    /// What a tool may add to the keys of its caller, by tool name. Everything not listed is forbidden:
    /// the read tool adds compact and headerMode under --compact-defaults (both known to every hub of wire 4);
    /// the attach tool always writes arguments (the inner tool's arguments, an empty object when omitted) and result (the inner tool's result text), keys of the wire since wire 1.
    /// </summary>
    private static HashSet<string> AllowedAdditions(string toolName, bool compactDefaults) => toolName switch
    {
        "sa_discussion_read" when compactDefaults => [DiscussionArgumentNames.Compact, DiscussionArgumentNames.HeaderMode],
        "sa_discussion_attach_analysis" => [DiscussionArgumentNames.Arguments, DiscussionArgumentNames.Result],
        _ => [],
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryDiscussionToolForwardsExactlyTheKeysOfItsCallerExceptTheDeclaredAdditions(bool compactDefaults)
    {
        var gateway = new RecordingGateway(new DiscussionHubGateway(_hub, _settings));
        var identity = new ClientIdentity();
        McpToolRegistry? registry = null;
        var create = new DiscussionCreateTool(gateway, identity);
        var join = new DiscussionJoinTool(gateway, identity);
        var tools = new List<IMcpTool>
        {
            new WeightedTool("sa_light_inner", isHeavy: false, _ => Task.FromResult("""{"v":1}""")),
            create,
            join,
            new DiscussionLeaveTool(gateway, identity),
            new DiscussionPostTool(gateway, identity, _settings),
            new DiscussionReadTool(gateway, identity, _settings, compactDefaults),
            new DiscussionStatusTool(gateway, identity),
            new DiscussionCloseTool(gateway, identity),
            new DiscussionAttachAnalysisTool(gateway, identity, () => registry!),
            new DiscussionGetAnalysisTool(gateway, identity),
        };
        registry = new McpToolRegistry(tools);

        // Set-up: a discussion with a participant (the create and the join are checked like the others).
        using JsonDocument created = JsonDocument.Parse(await InvokeAsync(create, """{"title":"t"}"""));
        string id = created.RootElement.GetProperty("sessionId").GetString()!;
        Assert.Equal(KeysOf("""{"title":"t"}"""), KeysOf(gateway.Requests.Single()));
        gateway.Requests.Clear();

        string joinArguments = $$"""{"sessionId":"{{id}}","displayName":"Alice"}""";
        using JsonDocument joined = JsonDocument.Parse(await InvokeAsync(join, joinArguments));
        string token = joined.RootElement.GetProperty("participantToken").GetString()!;
        Assert.Equal(KeysOf(joinArguments), KeysOf(gateway.Requests.Single()));
        gateway.Requests.Clear();

        // Each case: the tool, the caller's arguments with ONLY the required keys plus the optional ones the case names, and the operation(s) expected.
        // The discussion is closed before the leave: a participant that has left can no longer close it.
        (string Tool, string Arguments)[] cases =
        [
            ("sa_discussion_post", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","content":"hello"}"""),
            ("sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}"""),
            ("sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","afterSequence":0,"limit":5,"excludeEvents":true}"""),
            ("sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","compact":false,"headerMode":"full"}"""),
            ("sa_discussion_status", "{}"),
            ("sa_discussion_status", $$"""{"sessionId":"{{id}}","commandTail":0}"""),
            ("sa_discussion_attach_analysis", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"sa_light_inner"}"""),
            ("sa_discussion_get_analysis", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":1}"""),
            ("sa_discussion_close", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}"""),
            ("sa_discussion_leave", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}"""),
        ];

        foreach ((string toolName, string arguments) in cases)
        {
            IMcpTool tool = tools.Single(t => t.Name == toolName);
            gateway.Requests.Clear();
            await InvokeAsync(tool, arguments);

            HashSet<string> caller = KeysOf(arguments);
            HashSet<string> allowed = new(caller, StringComparer.Ordinal);
            allowed.UnionWith(AllowedAdditions(toolName, compactDefaults));

            DiscussionRequest[] forwarded = toolName == "sa_discussion_attach_analysis"
                ? gateway.Requests.Where(request => request.Operation == DiscussionOperations.AttachCommit).ToArray()
                : gateway.Requests.ToArray();
            Assert.True(forwarded.Length == 1, $"{toolName}: expected one forwarded request, saw {gateway.Requests.Count}.");

            HashSet<string> sent = KeysOf(forwarded[0]);
            Assert.True(
                caller.IsSubsetOf(sent) && sent.IsSubsetOf(allowed),
                $"{toolName} ({(compactDefaults ? "compact defaults" : "plain")}) forwarded [{string.Join(", ", sent.Order(StringComparer.Ordinal))}] for the caller's [{string.Join(", ", caller.Order(StringComparer.Ordinal))}]; only [{string.Join(", ", AllowedAdditions(toolName, compactDefaults))}] may be added.");

            if (toolName == "sa_discussion_attach_analysis")
            {
                // The precheck forwards the identity keys only, taken from the caller's keys.
                DiscussionRequest precheck = gateway.Requests.Single(request => request.Operation == DiscussionOperations.AttachPrecheck);
                Assert.True(KeysOf(precheck).IsSubsetOf(caller));
            }
        }
    }
}
