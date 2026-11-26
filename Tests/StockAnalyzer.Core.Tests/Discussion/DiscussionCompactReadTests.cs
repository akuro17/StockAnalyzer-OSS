#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// MCP discussion optimization, Run A. The <c>compact</c> and <c>headerMode</c> arguments of a read (the output rules of the gateway) and the interpretation-only post
/// (the board). Every test of the new forms is paired with a check that the answer without the new arguments is unchanged.
/// </summary>
public sealed class DiscussionCompactReadTests : IDisposable
{
    private static readonly string[] LegacyMessageKeys =
    {
        "sequence", "handle", "displayName", "kind", "origin", "content", "replyToSequence", "contextIds", "createdUtc", "hasStructuredInput", "hasInterpretation", "interpretation",
    };

    private readonly HubRig _rig = new();
    private readonly DiscussionHubGateway _gateway;

    public DiscussionCompactReadTests()
    {
        _gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    private async Task<string> ReadTextAsync(string id, string token, string extra = "")
    {
        DiscussionReply reply = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"{{extra}} }"""), CancellationToken.None);
        Assert.True(reply.IsOk, reply.Failure?.Message);
        return reply.ResultJson!;
    }

    private (string Id, string Alice, string Bob) TwoParticipants(DiscussionTurnMode mode = DiscussionTurnMode.Free)
    {
        string id = _rig.Hub.Create("Compact", "a topic", mode).Session.Id;
        string alice = _rig.Join(id, "Alice").Token;
        string bob = _rig.Join(id, "Bob").Token;
        return (id, alice, bob);
    }

    // ---- Unchanged default --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadWithoutTheNewArgumentsEqualsReadWithTheirLegacyValuesAndKeepsTheLegacyShape()
    {
        (string id, string alice, string bob) = TwoParticipants();
        _rig.Post(id, bob, "hello");

        string plain = await ReadTextAsync(id, alice);
        string explicitLegacy = await ReadTextAsync(id, alice, ""","compact":false,"headerMode":"full" """.TrimEnd());

        Assert.Equal(plain, explicitLegacy);
        using JsonDocument document = JsonDocument.Parse(plain);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "timedOut", "session" }, root.EnumerateObject().Select(p => p.Name));
        Assert.All(root.GetProperty("messages").EnumerateArray(), message => Assert.Equal(LegacyMessageKeys, message.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("a topic", root.GetProperty("session").GetProperty("topic").GetString());
        Assert.Equal(2, root.GetProperty("session").GetProperty("participants").GetArrayLength());
    }

    // ---- compact ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompactLeavesOutNullEmptyAndFalseFieldsAndKeepsEveryPresentValueAsInTheFullForm()
    {
        (string id, string alice, string bob) = TwoParticipants();
        _rig.Post(id, bob, "plain message");

        string full = await ReadTextAsync(id, alice);
        string compact = await ReadTextAsync(id, alice, ""","compact":true""");

        using JsonDocument fullDocument = JsonDocument.Parse(full);
        using JsonDocument compactDocument = JsonDocument.Parse(compact);
        JsonElement[] fullMessages = fullDocument.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        JsonElement[] compactMessages = compactDocument.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(fullMessages.Length, compactMessages.Length);

        // The two join events: server events have no handle, no name, no reply, no contexts, no flags.
        JsonElement joinEvent = compactMessages[0];
        Assert.Equal(new[] { "sequence", "kind", "origin", "content", "createdUtc" }, joinEvent.EnumerateObject().Select(p => p.Name));
        Assert.Equal("server", joinEvent.GetProperty("origin").GetString());

        // The peer message keeps its handle, its name and its content; the null/empty/false keys are gone.
        JsonElement peer = compactMessages[2];
        Assert.Equal(new[] { "sequence", "handle", "displayName", "kind", "origin", "content", "createdUtc" }, peer.EnumerateObject().Select(p => p.Name));
        Assert.Equal("p2", peer.GetProperty("handle").GetString());
        Assert.Equal("peer", peer.GetProperty("origin").GetString());

        // Every key a compact message carries has the value it has in the full form.
        for (int i = 0; i < compactMessages.Length; i++)
        {
            foreach (JsonProperty property in compactMessages[i].EnumerateObject())
            {
                Assert.Equal(fullMessages[i].GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
            }
        }

        // The header is not compacted, and timedOut (false) is left out.
        Assert.False(compactDocument.RootElement.TryGetProperty("timedOut", out _));
        Assert.Equal(fullDocument.RootElement.GetProperty("session").GetRawText(), compactDocument.RootElement.GetProperty("session").GetRawText());
        Assert.Equal(
            new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "session" },
            compactDocument.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(compact.Length < full.Length);
    }

    [Fact]
    public async Task CompactWritesRepliesContextsFlagsAndInterpretationsWhenTheyArePresent()
    {
        (string id, string alice, string bob) = TwoParticipants();
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", """{"v":1}""", null);
        _rig.Hub.Post(
            id,
            bob,
            new DiscussionPostRequest("with everything", ReplyToSequence: 1, ContextIds: new long[] { 1 }, Interpretation: new DiscussionInterpretation(Summary: "s", Direction: "Bullish")));

        string compact = await ReadTextAsync(id, alice, ""","compact":true""");

        using JsonDocument document = JsonDocument.Parse(compact);
        JsonElement[] messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        JsonElement analysis = messages.Single(m => m.GetProperty("kind").GetString() == "analysis");
        Assert.True(analysis.GetProperty("hasStructuredInput").GetBoolean());
        Assert.False(analysis.TryGetProperty("hasInterpretation", out _));
        Assert.Equal(1, analysis.GetProperty("contextIds")[0].GetInt64());

        JsonElement full = messages[^1];
        Assert.Equal(
            new[] { "sequence", "handle", "displayName", "kind", "origin", "content", "replyToSequence", "contextIds", "createdUtc", "hasStructuredInput", "hasInterpretation", "interpretation" },
            full.EnumerateObject().Select(p => p.Name));
        Assert.True(full.GetProperty("hasInterpretation").GetBoolean());
        Assert.Equal("Bullish", full.GetProperty("interpretation").GetProperty("direction").GetString());
    }

    [Fact]
    public async Task CompactKeepsAnEmptyMessageListAndWritesTimedOutOnlyWhenTrue()
    {
        (string id, string alice, _) = TwoParticipants();

        Task<string> waiting = ReadTextAsync(id, alice, ""","afterSequence":2,"waitSeconds":3,"compact":true,"headerMode":"minimal" """.TrimEnd());
        await _rig.UntilWaitersAsync(1);
        _rig.Time.Advance(TimeSpan.FromSeconds(3));
        string timedOut = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        using JsonDocument document = JsonDocument.Parse(timedOut);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "timedOut", "session" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, root.GetProperty("messages").GetArrayLength());
        Assert.True(root.GetProperty("timedOut").GetBoolean());
        Assert.Equal(2, root.GetProperty("nextAfterSequence").GetInt64());
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);

        string nothingNew = await ReadTextAsync(id, alice, ""","afterSequence":2,"compact":true""");
        using JsonDocument quiet = JsonDocument.Parse(nothingNew);
        Assert.Equal(0, quiet.RootElement.GetProperty("messages").GetArrayLength());
        Assert.False(quiet.RootElement.TryGetProperty("timedOut", out _));
    }

    // ---- headerMode ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MinimalHeaderHoldsOnlyTheChangingPartAndWritesANullFloorWithoutCompactAndNotWithIt()
    {
        (string id, string alice, _) = TwoParticipants();

        using JsonDocument full = JsonDocument.Parse(await ReadTextAsync(id, alice, ""","headerMode":"minimal" """.TrimEnd()));
        JsonElement session = full.RootElement.GetProperty("session");
        Assert.Equal(new[] { "id", "state", "floor", "messageCount", "remainingMessages", "contextCount" }, session.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, session.GetProperty("id").GetString());
        Assert.Equal("active", session.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("floor").ValueKind);
        Assert.Equal(2, session.GetProperty("messageCount").GetInt32());
        Assert.Equal(_rig.Settings.MaxMessagesPerSession - 2, session.GetProperty("remainingMessages").GetInt32());
        Assert.Equal(0, session.GetProperty("contextCount").GetInt32());

        using JsonDocument compact = JsonDocument.Parse(await ReadTextAsync(id, alice, ""","headerMode":"minimal","compact":true""".TrimEnd()));
        Assert.Equal(
            new[] { "id", "state", "messageCount", "remainingMessages", "contextCount" },
            compact.RootElement.GetProperty("session").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task MinimalHeaderOfARoundRobinDiscussionCarriesTheFloor()
    {
        (string id, string alice, _) = TwoParticipants(DiscussionTurnMode.RoundRobin);

        using JsonDocument document = JsonDocument.Parse(await ReadTextAsync(id, alice, ""","headerMode":"minimal","compact":true""".TrimEnd()));

        JsonElement floor = document.RootElement.GetProperty("session").GetProperty("floor");
        Assert.Equal("p1", floor.GetProperty("handle").GetString());
        Assert.Equal(_rig.Settings.FloorTimeoutSeconds, floor.GetProperty("secondsLeft").GetInt32());
    }

    [Fact]
    public async Task MinimalHeaderOfAClosedDiscussionCarriesTheCloseReason()
    {
        (string id, string alice, _) = TwoParticipants();
        _rig.Hub.Close(id, alice);

        using JsonDocument document = JsonDocument.Parse(await ReadTextAsync(id, alice, ""","headerMode":"minimal","compact":true""".TrimEnd()));

        JsonElement session = document.RootElement.GetProperty("session");
        Assert.Equal(new[] { "id", "state", "closeReason", "messageCount", "remainingMessages", "contextCount" }, session.EnumerateObject().Select(p => p.Name));
        Assert.Equal("closed", session.GetProperty("state").GetString());
        Assert.Equal("participant", session.GetProperty("closeReason").GetString());
    }

    [Fact]
    public async Task HeaderModeNoneLeavesTheSessionOut()
    {
        (string id, string alice, _) = TwoParticipants();

        using JsonDocument document = JsonDocument.Parse(await ReadTextAsync(id, alice, ""","headerMode":"none" """.TrimEnd()));

        Assert.Equal(new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "timedOut" }, document.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(""","headerMode":"short" """, "headerMode")]
    [InlineData(""","headerMode":"" """, "headerMode")]
    [InlineData(""","headerMode":3""", "headerMode")]
    [InlineData(""","compact":"yes" """, "compact")]
    public async Task AnInvalidFormattingArgumentIsRefusedBeforeTheBoardIsTouched(string extra, string parameter)
    {
        (string id, string alice, string bob) = TwoParticipants();

        DiscussionReply reply = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}"{{extra.TrimEnd()}} }"""), CancellationToken.None);

        Assert.False(reply.IsOk);
        Assert.Equal(DiscussionErrorCodes.InvalidArgument, reply.Failure!.Code);
        Assert.Equal(parameter, reply.Failure.Parameter);

        // The refused read did not move Alice's read position (Bob's header shows it).
        DiscussionReadResult seenByBob = await _rig.Hub.ReadAsync(id, bob);
        Assert.Equal(0, seenByBob.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
    }

    [Fact]
    public async Task ANullFormattingArgumentCountsAsOmitted()
    {
        (string id, string alice, _) = TwoParticipants();

        string withNull = await ReadTextAsync(id, alice, ""","compact":null,"headerMode":null""");

        Assert.Equal(await ReadTextAsync(id, alice), withNull);
    }

    // ---- interpretation-only post -------------------------------------------------------------------------------------------------------

    private static DiscussionInterpretation Interpretation(string summary = "Trend is upward") => new(Summary: summary, Direction: DiscussionInterpretationDirection.Bullish);

    [Fact]
    public async Task APostWithoutContentButWithAnInterpretationIsStoredWithAnEmptyContent()
    {
        (string id, string alice, string bob) = TwoParticipants();

        DiscussionPostResult posted = _rig.Hub.Post(id, bob, new DiscussionPostRequest(null, Interpretation: Interpretation()));

        Assert.True(posted.HasInterpretation);
        using JsonDocument full = JsonDocument.Parse(await ReadTextAsync(id, alice, $$""","afterSequence":{{posted.Sequence - 1}}"""));
        JsonElement message = full.RootElement.GetProperty("messages")[0];
        Assert.Equal(string.Empty, message.GetProperty("content").GetString());
        Assert.True(message.GetProperty("hasInterpretation").GetBoolean());
        Assert.Equal("Trend is upward", message.GetProperty("interpretation").GetProperty("summary").GetString());

        using JsonDocument compact = JsonDocument.Parse(await ReadTextAsync(id, alice, $$""","afterSequence":{{posted.Sequence - 1}},"compact":true"""));
        Assert.False(compact.RootElement.GetProperty("messages")[0].TryGetProperty("content", out _));
        DiscussionDiagnostics diagnostics = _rig.Hub.GetDiagnostics();
        Assert.Equal(diagnostics.TotalBytes, diagnostics.RecomputedBytes);
    }

    [Fact]
    public void APostWithoutContentAndWithoutAnInterpretationIsRefusedAsBefore()
    {
        (string id, _, string bob) = TwoParticipants();

        DiscussionException error = HubRig.Refused(() => _rig.Hub.Post(id, bob, new DiscussionPostRequest(null)));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "content", "content is required and must contain a visible character."), (error.Code, error.Parameter, error.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnExplicitEmptyOrBlankContentIsRefusedEvenWithAnInterpretation(string content)
    {
        (string id, _, string bob) = TwoParticipants();

        DiscussionException error = HubRig.Refused(() => _rig.Hub.Post(id, bob, new DiscussionPostRequest(content, Interpretation: Interpretation())));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "content"), (error.Code, error.Parameter));
    }

    [Fact]
    public void AnInterpretationAloneIsBoundedByTheMessageSizeLimit()
    {
        using var small = new HubRig(settings => settings.MaxMessageBytes = DiscussionLimits.MessageBytesBand.Minimum);
        string id = small.NewSession();
        string token = small.Join(id, "Alice").Token;
        DiscussionInterpretation big = new(Findings: Enumerable.Repeat(new string('a', DiscussionEvidenceLimits.MaxInterpretationItemChars), DiscussionEvidenceLimits.MaxInterpretationListItems).ToArray());

        DiscussionException error = HubRig.Refused(() => small.Hub.Post(id, token, new DiscussionPostRequest(null, Interpretation: big)));

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "interpretation"), (error.Code, error.Parameter));
        Assert.Equal(1, small.Hub.ReadAsync(id, token).GetAwaiter().GetResult().LastSequence);
    }

    [Fact]
    public void AGivenContentKeepsEveryExistingLimitAndIsNotBoundedTogetherWithTheInterpretation()
    {
        using var small = new HubRig(settings => settings.MaxMessageBytes = DiscussionLimits.MessageBytesBand.Minimum);
        string id = small.NewSession();
        string token = small.Join(id, "Alice").Token;
        string content = new('c', small.Settings.MaxMessageBytes - 10);
        DiscussionInterpretation medium = new(Findings: Enumerable.Repeat(new string('a', 60), 2).ToArray());

        // Each part fits the limit on its own; the sum does not. Before this change the post was accepted, so it must still be.
        DiscussionPostResult posted = small.Hub.Post(id, token, new DiscussionPostRequest(content, Interpretation: medium));

        Assert.True(posted.HasInterpretation);
        DiscussionException tooLong = HubRig.Refused(() => small.Hub.Post(id, token, new DiscussionPostRequest(new string('c', small.Settings.MaxMessageBytes + 1))));
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "content"), (tooLong.Code, tooLong.Parameter));
    }

    [Fact]
    public void ARepeatedInterpretationOnlyPostWithTheSameRequestIdIsADuplicate()
    {
        (string id, _, string bob) = TwoParticipants();
        var request = new DiscussionPostRequest(null, ClientRequestId: "r-1", Interpretation: Interpretation());

        DiscussionPostResult first = _rig.Hub.Post(id, bob, request);
        DiscussionPostResult again = _rig.Hub.Post(id, bob, request);

        Assert.False(first.Duplicate);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Sequence, again.Sequence);
    }

    [Fact]
    public async Task ThroughTheGatewayContentThatIsLeftOutOrNullMeansNoContent()
    {
        (string id, _, string bob) = TwoParticipants();
        string interpretation = """{"summary":"s","direction":"Neutral"}""";

        foreach (string content in new[] { string.Empty, ""","content":null""" })
        {
            DiscussionReply reply = await _gateway.ExecuteAsync(
                new DiscussionRequest(DiscussionOperations.Post, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}"{{content}},"interpretation":{{interpretation}} }"""), CancellationToken.None);
            Assert.True(reply.IsOk, reply.Failure?.Message);
        }

        DiscussionReply blank = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Post, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","content":"","interpretation":{{interpretation}} }"""), CancellationToken.None);
        Assert.Equal("content", blank.Failure!.Parameter);
    }
}
