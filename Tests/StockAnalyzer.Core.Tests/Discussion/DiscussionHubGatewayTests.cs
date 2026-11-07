#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T5, Core side: the text gateway of the process that holds the board (arguments in as JSON, results out as JSON, failures as replies).</summary>
public sealed class DiscussionHubGatewayTests : IDisposable
{
    private const string Suffix = "Research only.";

    private readonly HubRig _rig = new();
    private readonly CommandLog _commands;
    private readonly DiscussionHubGateway _gateway;

    public DiscussionHubGatewayTests()
    {
        _commands = new CommandLog(_rig.Settings, _rig.Time);
        _gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings, _commands, Suffix);
    }

    public void Dispose()
    {
        _commands.Dispose();
        _rig.Dispose();
    }

    private async Task<JsonDocument> OkAsync(string operation, string arguments)
    {
        DiscussionReply reply = await _gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), CancellationToken.None);
        Assert.True(reply.IsOk, reply.Failure?.Message);
        Assert.Null(reply.Failure);
        return JsonDocument.Parse(reply.ResultJson!);
    }

    private async Task<DiscussionFailure> FailedAsync(string operation, string arguments)
    {
        DiscussionReply reply = await _gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), CancellationToken.None);
        Assert.False(reply.IsOk);
        Assert.Null(reply.ResultJson);
        return reply.Failure!;
    }

    private async Task<(string SessionId, string Token)> OpenAsync(string name = "Alice")
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"t"}""");
        string id = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await OkAsync(DiscussionOperations.Join, $$"""{"sessionId":"{{id}}","displayName":"{{name}}"}""");
        return (id, joined.RootElement.GetProperty("participantToken").GetString()!);
    }

    [Fact]
    public async Task CreateReturnsTheSessionTheStateAndTheLimitsOfTheDiscussion()
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"7203","topic":"up?","turnMode":"roundRobin"}""");

        JsonElement root = created.RootElement;
        Assert.Equal("roundRobin", root.GetProperty("turnMode").GetString());
        Assert.Equal("up?", root.GetProperty("topic").GetString());
        Assert.Equal("active", root.GetProperty("state").GetString());
        Assert.Equal(_rig.Hub.Epoch, root.GetProperty("hubEpoch").GetString());
        Assert.Equal("2026-11-07T03:00:00.0000000Z", root.GetProperty("createdUtc").GetString());
        Assert.Equal(
            new[] { "maxParticipants", "maxMessages", "maxMessageBytes", "maxContexts", "maxContextBytes", "maxWaitSeconds", "maxPostsPerMinute", "idleTimeoutSeconds", "maxSessionAgeSeconds" },
            root.GetProperty("limits").EnumerateObject().Select(p => p.Name));
        Assert.Equal(6, root.GetProperty("limits").GetProperty("maxParticipants").GetInt32());
    }

    [Fact]
    public async Task ATopicThatWasNotGivenIsNull()
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"t"}""");

        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("topic").ValueKind);
        Assert.Equal("free", created.RootElement.GetProperty("turnMode").GetString());
    }

    [Fact]
    public async Task JoinAppendsTheSuffixToTheProtocolAndCarriesTheSessionHeader()
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"t"}""");
        string id = created.RootElement.GetProperty("sessionId").GetString()!;

        using JsonDocument joined = await OkAsync(DiscussionOperations.Join, $$"""{"sessionId":"{{id}}","displayName":"Alice"}""");

        JsonElement root = joined.RootElement;
        Assert.Equal(DiscussionProtocolText.Text + " " + Suffix, root.GetProperty("protocol").GetString());
        Assert.Equal("p1", root.GetProperty("handle").GetString());
        Assert.Equal(1, root.GetProperty("lastSequence").GetInt32());
        JsonElement participant = Assert.Single(root.GetProperty("session").GetProperty("participants").EnumerateArray());
        Assert.Equal(("p1", "Alice", "active", 0), (participant.GetProperty("handle").GetString(), participant.GetProperty("displayName").GetString(), participant.GetProperty("state").GetString(), participant.GetProperty("lastReadSequence").GetInt32()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("session").GetProperty("floor").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("session").GetProperty("closeReason").ValueKind);
    }

    [Fact]
    public async Task WithoutASuffixTheProtocolIsExactlyTheFixedText()
    {
        var plain = new DiscussionHubGateway(_rig.Hub, _rig.Settings);
        string id = _rig.NewSession();

        DiscussionReply reply = await plain.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Join, "", $$"""{"sessionId":"{{id}}","displayName":"Alice"}"""), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        Assert.Equal(DiscussionProtocolText.Text, document.RootElement.GetProperty("protocol").GetString());
    }

    [Fact]
    public async Task NonAsciiTextIsWrittenAsItIsSoAResultIsNoLargerThanItsText()
    {
        (string id, string token) = await OpenAsync();
        string text = new string('あ', 100) + "\U0001F4C8";
        _rig.Post(id, token, text);

        DiscussionReply reply = await _gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Read, "", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","afterSequence":1}"""), CancellationToken.None);

        // Characters of the basic plane stay as they are; the encoder always escapes a character outside it (the emoji) as a surrogate pair.
        Assert.Contains(new string('あ', 100), reply.ResultJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u3042", reply.ResultJson!, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        Assert.Equal(text, document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(reply.ResultJson!) < Encoding.UTF8.GetByteCount(text) + 1500);
    }

    [Fact]
    public async Task PostReadAndCloseReturnTheDocumentedFields()
    {
        (string id, string token) = await OpenAsync();

        using JsonDocument posted = await OkAsync(DiscussionOperations.Post, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","content":"hello","basedOnSequence":1,"replyToSequence":1}""");
        using JsonDocument read = await OkAsync(DiscussionOperations.Read, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");
        using JsonDocument closed = await OkAsync(DiscussionOperations.Close, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");

        Assert.Equal(new[] { "sequence", "createdUtc", "newerMessages", "duplicate", "floor" }, posted.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "timedOut", "session" }, read.RootElement.EnumerateObject().Select(p => p.Name));
        JsonElement message = read.RootElement.GetProperty("messages")[1];
        Assert.Equal(
            new[] { "sequence", "handle", "displayName", "kind", "origin", "content", "replyToSequence", "contextIds", "createdUtc" },
            message.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, message.GetProperty("replyToSequence").GetInt32());
        Assert.Equal(JsonValueKind.Null, read.RootElement.GetProperty("messages")[0].GetProperty("replyToSequence").ValueKind);
        Assert.Equal(new[] { "alreadyClosed", "state", "closeReason", "messageCount", "session" }, closed.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("participant", closed.RootElement.GetProperty("closeReason").GetString());
    }

    [Fact]
    public async Task ALeaveReturnsTheFlagAndTheHeader()
    {
        (string id, string token) = await OpenAsync();

        using JsonDocument left = await OkAsync(DiscussionOperations.Leave, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");

        Assert.Equal(new[] { "alreadyLeft", "session" }, left.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("left", left.RootElement.GetProperty("session").GetProperty("participants")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task StatusHoldsTheProcessFieldsTheLimitsTheAuditAndTheCommandsOfThisProcess()
    {
        (string id, _) = await OpenAsync();
        _commands.Record("sa_discussion_join", new[] { "sessionId" }, 20, "ok", 1, "5");

        using JsonDocument all = await OkAsync(DiscussionOperations.Status, """{"auditTail":2,"commandTail":5}""");
        using JsonDocument one = await OkAsync(DiscussionOperations.Status, $$"""{"sessionId":"{{id}}"}""");

        JsonElement root = all.RootElement;
        Assert.Equal(
            new[] { "hubEpoch", "role", "sharing", "sharingKey", "sessions", "effectiveLimits", "audit", "commands", "serverTimeUtc" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, root.GetProperty("audit").GetArrayLength());
        Assert.Equal("sa_discussion_join", Assert.Single(root.GetProperty("commands").EnumerateArray()).GetProperty("tool").GetString());
        Assert.Equal(1, root.GetProperty("sessions").GetArrayLength());
        Assert.Equal(20, root.GetProperty("effectiveLimits").GetProperty("maxPostsPerMinute").GetInt32());
        Assert.Equal(id, one.RootElement.GetProperty("session").GetProperty("id").GetString());
        Assert.Empty(one.RootElement.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public async Task StatusWithoutACommandLogReportsNoCommands()
    {
        var gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);

        DiscussionReply reply = await gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Status, "", """{"commandTail":5}"""), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        Assert.Empty(document.RootElement.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public async Task AWaitingReadThroughTheGatewayIsWokenByAPostAndCanBeCancelled()
    {
        (string id, string alice) = await OpenAsync();
        string bob = _rig.Join(id, "Bob").Token;
        string args = $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}","afterSequence":2,"waitSeconds":20}""";

        Task<DiscussionReply> waiting = _gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Read, "", args), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        _rig.Post(id, bob, "wake up");
        DiscussionReply reply = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        using JsonDocument document = JsonDocument.Parse(reply.ResultJson!);
        Assert.Equal("wake up", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        using var cancel = new CancellationTokenSource();
        Task<DiscussionReply> cancelled = _gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Read, "", args.Replace("\"afterSequence\":2", "\"afterSequence\":3", StringComparison.Ordinal)), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    // ---- Analysis snapshots ---------------------------------------------------------------------------------------------------------------

    private static string CommitArguments(string id, string token, string arguments, string result, string? note = null)
        => JsonSerializer.Serialize(new { sessionId = id, participantToken = token, tool = "sa_x", arguments = JsonDocument.Parse(arguments).RootElement, result, note });

    [Fact]
    public async Task ASnapshotIsStoredWithCanonicalArgumentsReusedAcrossKeyOrderAndReturnedByteExact()
    {
        (string id, string token) = await OpenAsync();
        const string Result = """{"close":12345678901234567890.123456789,"name":"日本"}""";

        using JsonDocument precheck = await OkAsync(DiscussionOperations.AttachPrecheck, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");
        using JsonDocument first = await OkAsync(DiscussionOperations.AttachCommit, CommitArguments(id, token, """{"b":2,"a":1}""", Result, "daily"));
        using JsonDocument again = await OkAsync(DiscussionOperations.AttachCommit, CommitArguments(id, token, """{ "a": 1, "b": 2 }""", Result));
        using JsonDocument fetched = await OkAsync(DiscussionOperations.GetAnalysis, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":1}""");
        using JsonDocument without = await OkAsync(DiscussionOperations.GetAnalysis, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":1,"includeResult":false}""");

        Assert.True(precheck.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "contextId", "reused", "sha256", "resultBytes", "sequence" }, first.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(first.RootElement.GetProperty("reused").GetBoolean());
        Assert.True(again.RootElement.GetProperty("reused").GetBoolean());
        Assert.Equal(1, again.RootElement.GetProperty("contextId").GetInt32());
        Assert.Equal(
            new[] { "contextId", "tool", "arguments", "sha256", "attachedBy", "note", "createdUtc", "resultBytes", "result" },
            fetched.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("""{"a":1,"b":2}""", fetched.RootElement.GetProperty("arguments").GetRawText());
        Assert.Equal(Result, fetched.RootElement.GetProperty("result").GetRawText()); // the stored text, character for character
        Assert.Equal("daily", fetched.RootElement.GetProperty("note").GetString());
        Assert.Equal("p1", fetched.RootElement.GetProperty("attachedBy").GetString());
        Assert.False(without.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ASnapshotWithoutArgumentsStoresAnEmptyObjectAndANullNoteIsNull()
    {
        (string id, string token) = await OpenAsync();
        string commit = JsonSerializer.Serialize(new { sessionId = id, participantToken = token, tool = "sa_x", result = "{}" });

        using JsonDocument attached = await OkAsync(DiscussionOperations.AttachCommit, commit);
        using JsonDocument fetched = await OkAsync(DiscussionOperations.GetAnalysis, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":{{attached.RootElement.GetProperty("contextId").GetInt32()}}}""");

        Assert.Equal("{}", fetched.RootElement.GetProperty("arguments").GetRawText());
        Assert.Equal(JsonValueKind.Null, fetched.RootElement.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task ASnapshotRefusesAResultThatIsNotJsonBadArgumentsAndAMissingContextId()
    {
        (string id, string token) = await OpenAsync();

        DiscussionFailure notJson = await FailedAsync(DiscussionOperations.AttachCommit, JsonSerializer.Serialize(new { sessionId = id, participantToken = token, tool = "sa_x", result = "not json" }));
        DiscussionFailure duplicate = await FailedAsync(DiscussionOperations.AttachCommit, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"sa_x","arguments":{"a":1,"a":2},"result":"{}"}""");
        DiscussionFailure arrayArguments = await FailedAsync(DiscussionOperations.AttachCommit, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"sa_x","arguments":[1],"result":"{}"}""");
        DiscussionFailure noContext = await FailedAsync(DiscussionOperations.GetAnalysis, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");
        DiscussionFailure unknownContext = await FailedAsync(DiscussionOperations.GetAnalysis, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","contextId":9}""");

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "result"), (notJson.Code, notJson.Parameter));
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "arguments", "duplicate key"), (duplicate.Code, duplicate.Parameter, duplicate.Hint));
        Assert.Equal("arguments", arrayArguments.Parameter);
        Assert.Equal("contextId", noContext.Parameter);
        Assert.Equal("contextId", unknownContext.Parameter);
    }

    [Fact]
    public async Task APrecheckFailureIsAReplyWithTheBoardsCode()
    {
        (string id, string token) = await OpenAsync();
        _rig.Hub.Close(id, token);

        DiscussionFailure closed = await FailedAsync(DiscussionOperations.AttachPrecheck, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"}""");

        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, closed.Code);
    }

    // ---- Failures ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABoardRefusalBecomesAFailureReplyWithCodeParameterAndHint()
    {
        DiscussionFailure unknown = await FailedAsync(DiscussionOperations.Join, """{"sessionId":"Snothing","displayName":"Bob"}""");
        (string id, string token) = await OpenAsync();
        DiscussionFailure notYours = await FailedAsync(DiscussionOperations.Post, $$"""{"sessionId":"{{id}}","participantToken":"x","content":"c"}""");
        DiscussionFailure big = await FailedAsync(DiscussionOperations.Read, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","limit":51}""");

        Assert.Equal((DiscussionErrorCodes.DiscussionNotFound, "sessionId"), (unknown.Code, unknown.Parameter));
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, notYours.Code);
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "limit"), (big.Code, big.Parameter));
        Assert.NotNull(notYours.Hint);
    }

    [Theory]
    [InlineData(DiscussionOperations.Create, """{"title":5}""", "title")]
    [InlineData(DiscussionOperations.Create, """{"title":"t","turnMode":"sideways"}""", "turnMode")]
    [InlineData(DiscussionOperations.Create, """{"title":"t","turnMode":3}""", "turnMode")]
    [InlineData(DiscussionOperations.Join, """{"sessionId":["a"],"displayName":"x"}""", "sessionId")]
    [InlineData(DiscussionOperations.Post, """{"sessionId":"S","participantToken":"t","content":"c","pass":"yes"}""", "pass")]
    [InlineData(DiscussionOperations.Post, """{"sessionId":"S","participantToken":"t","content":"c","replyToSequence":1.5}""", "replyToSequence")]
    [InlineData(DiscussionOperations.Post, """{"sessionId":"S","participantToken":"t","content":"c","contextIds":"1"}""", "contextIds")]
    [InlineData(DiscussionOperations.Post, """{"sessionId":"S","participantToken":"t","content":"c","contextIds":[1,"2"]}""", "contextIds")]
    [InlineData(DiscussionOperations.Read, """{"sessionId":"S","participantToken":"t","limit":99999999999}""", "limit")]
    [InlineData(DiscussionOperations.Read, """{"sessionId":"S","participantToken":"t","waitSeconds":"3"}""", "waitSeconds")]
    public async Task AWronglyTypedArgumentIsInvalidAndNamesTheArgument(string operation, string arguments, string parameter)
    {
        DiscussionFailure failure = await FailedAsync(operation, arguments);

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, parameter), (failure.Code, failure.Parameter));
    }

    [Theory]
    [InlineData("", "unknownOperation")]
    [InlineData("{not json", DiscussionOperations.Create)]
    [InlineData("[1,2]", DiscussionOperations.Create)]
    [InlineData("5", DiscussionOperations.Create)]
    public async Task ABadOperationOrBadJsonIsInvalid(string arguments, string operation)
    {
        DiscussionFailure failure = await FailedAsync(operation, arguments);

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, failure.Code);
    }

    [Fact]
    public async Task AnEmptyArgumentTextIsAnEmptyObject()
    {
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Create, "");

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "title"), (failure.Code, failure.Parameter));
    }

    [Fact]
    public async Task ANullArgumentCountsAsNotGiven()
    {
        using JsonDocument created = await OkAsync(DiscussionOperations.Create, """{"title":"t","topic":null,"turnMode":null}""");

        Assert.Equal("free", created.RootElement.GetProperty("turnMode").GetString());
    }

    [Fact]
    public async Task AShutDownBoardAnswersHubUnavailableAndTheCancellationIsNeverSwallowed()
    {
        var rig = new HubRig();
        var gateway = new DiscussionHubGateway(rig.Hub, rig.Settings);
        rig.Dispose();

        DiscussionReply reply = await gateway.ExecuteAsync(new DiscussionRequest(DiscussionOperations.Create, "", """{"title":"t"}"""), CancellationToken.None);

        Assert.False(reply.IsOk);
        Assert.Equal(DiscussionErrorCodes.HubUnavailable, reply.Failure!.Code);
    }

    [Fact]
    public async Task TheStatusRefusesACommandTailBeyondTheLimit()
    {
        DiscussionFailure failure = await FailedAsync(DiscussionOperations.Status, """{"commandTail":101}""");

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "commandTail"), (failure.Code, failure.Parameter));
    }

    [Fact]
    public void TheReplyFactoriesKeepTheTwoShapesApart()
    {
        DiscussionReply ok = DiscussionReply.Ok("{}");
        DiscussionReply fail = DiscussionReply.Fail("CODE", "message", "param", "hint");

        Assert.True(ok.IsOk);
        Assert.Equal("{}", ok.ResultJson);
        Assert.False(fail.IsOk);
        Assert.Equal(new DiscussionFailure("CODE", "message", "param", "hint"), fail.Failure);
        Assert.Throws<ArgumentNullException>(() => DiscussionReply.Ok(null!));
    }
}
