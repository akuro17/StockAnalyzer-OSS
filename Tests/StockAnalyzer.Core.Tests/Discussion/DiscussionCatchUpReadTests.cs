#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// MCP discussion optimization, Run C: reading without the server events (<c>excludeEvents</c>), reading only the newest messages (<c>tail</c>), the wait that is repeated
/// until a message that passes the filter arrives, and the summary view of the gateway.
/// </summary>
public sealed class DiscussionCatchUpReadTests : IDisposable
{
    private static readonly DiscussionReadOptions NoEvents = new(ExcludeEvents: true);

    private readonly HubRig _rig = new();
    private readonly DiscussionHubGateway _gateway;

    public DiscussionCatchUpReadTests()
    {
        _gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>Alice and Bob joined (events 1 and 2); nothing else yet.</summary>
    private (string Id, string Alice, string Bob) Open()
    {
        string id = _rig.NewSession();
        return (id, _rig.Join(id, "Alice").Token, _rig.Join(id, "Bob").Token);
    }

    private Task<DiscussionReadResult> ReadAsync(string id, string token, long after = 0, int? limit = null, int wait = 0, DiscussionReadOptions? options = null, CancellationToken cancel = default)
        => _rig.Hub.ReadAsync(id, token, after, limit, wait, cancel, options);

    // ---- excludeEvents, forward ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExcludeEventsLeavesOutTheServerEventsButKeepsPeerMessagesAndAnalysisEntries()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "hello");
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", """{"v":1}""", null);

        DiscussionReadResult result = await ReadAsync(id, alice, options: NoEvents);

        Assert.Equal(new long[] { 3, 4 }, result.Messages.Select(m => m.Sequence));
        Assert.Equal(new[] { DiscussionMessageKind.Message, DiscussionMessageKind.Analysis }, result.Messages.Select(m => m.Kind));
        Assert.Equal((4L, 4L, false, false), (result.NextAfterSequence, result.LastSequence, result.HasMore, result.TimedOut));
    }

    [Fact]
    public async Task ARunOfEventsAtTheEndMovesTheCursorToTheEndSoTheNextCallMakesProgress()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "hello");
        _rig.Join(id, "Carol");
        _rig.Join(id, "Dave");

        DiscussionReadResult first = await ReadAsync(id, alice, after: 3, options: NoEvents);

        Assert.Empty(first.Messages);
        Assert.Equal((5L, 5L, false), (first.NextAfterSequence, first.LastSequence, first.HasMore));
        Assert.Equal(5, first.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
        DiscussionReadResult again = await ReadAsync(id, alice, after: first.NextAfterSequence, options: NoEvents);
        Assert.Equal((5L, false, false), (again.NextAfterSequence, again.HasMore, again.TimedOut));
    }

    [Fact]
    public async Task ReadingForwardPageByPageWithTheCursorEndsAndReturnsEveryPassingMessageOnce()
    {
        using var rig = new HubRig(settings => settings.MaxParticipantsPerSession = 16);
        string id = rig.NewSession();
        string alice = rig.Join(id, "Alice").Token;
        string bob = rig.Join(id, "Bob").Token;
        for (int i = 0; i < 5; i++)
        {
            rig.Post(id, bob, $"m{i}");
            rig.Join(id, $"Guest{i}"); // an event between two messages
        }

        var seen = new List<string>();
        long after = 0;
        int calls = 0;
        while (true)
        {
            DiscussionReadResult page = await rig.Hub.ReadAsync(id, alice, after, 2, 0, default, NoEvents);
            seen.AddRange(page.Messages.Select(m => m.Content));
            Assert.True(page.NextAfterSequence >= after, "the cursor never goes back");
            after = page.NextAfterSequence;
            calls++;
            if (!page.HasMore)
            {
                break;
            }

            Assert.True(calls < 20, "the loop must end");
        }

        Assert.Equal(new[] { "m0", "m1", "m2", "m3", "m4" }, seen);
        Assert.Equal(rig.Hub.ReadAsync(id, alice).GetAwaiter().GetResult().LastSequence, after);
    }

    [Fact]
    public async Task WhenTheLimitIsReachedBeforeTrailingEventsTheNextCallIsEmptyAndEndsTheRead()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "only");
        _rig.Join(id, "Carol");

        DiscussionReadResult first = await ReadAsync(id, alice, limit: 1, options: NoEvents);
        DiscussionReadResult second = await ReadAsync(id, alice, first.NextAfterSequence, limit: 1, options: NoEvents);

        Assert.Equal(new long[] { 3 }, first.Messages.Select(m => m.Sequence));
        Assert.Equal((3L, true), (first.NextAfterSequence, first.HasMore));
        Assert.Equal((0, 4L, false), (second.Messages.Count, second.NextAfterSequence, second.HasMore));
    }

    [Fact]
    public async Task AnAfterSequenceBeyondTheEndKeepsTheCursorWhereItWasPut()
    {
        (string id, string alice, _) = Open();

        DiscussionReadResult result = await ReadAsync(id, alice, after: 99, options: NoEvents);

        Assert.Empty(result.Messages);
        Assert.Equal((99L, false), (result.NextAfterSequence, result.HasMore));
        Assert.Equal(2, result.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
    }

    [Fact]
    public async Task OptionsThatChangeNothingTakeTheUnchangedPath()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "hello");

        DiscussionReadResult plain = await ReadAsync(id, alice);
        DiscussionReadResult withDefaults = await ReadAsync(id, alice, options: new DiscussionReadOptions());

        Assert.Equal(plain.Messages.Select(m => m.Sequence), withDefaults.Messages.Select(m => m.Sequence));
        Assert.Equal((plain.NextAfterSequence, plain.LastSequence, plain.HasMore), (withDefaults.NextAfterSequence, withDefaults.LastSequence, withDefaults.HasMore));
        Assert.Equal(3, withDefaults.Messages.Count);
    }

    // ---- tail ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TailReturnsTheNewestMessagesOldestFirstAndMovesTheReadPositionToTheEnd()
    {
        (string id, string alice, string bob) = Open();
        for (int i = 0; i < 4; i++)
        {
            _rig.Post(id, bob, $"m{i}");
        }

        DiscussionReadResult result = await ReadAsync(id, alice, options: new DiscussionReadOptions(Tail: 2));

        Assert.Equal(new[] { "m2", "m3" }, result.Messages.Select(m => m.Content));
        Assert.Equal((6L, 6L, false), (result.NextAfterSequence, result.LastSequence, result.HasMore));
        Assert.Equal(6, result.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
    }

    [Fact]
    public async Task TailWithExcludeEventsCountsOnlyMessagesThatPass()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "a");
        _rig.Join(id, "Carol");
        _rig.Post(id, bob, "b");
        _rig.Join(id, "Dave");

        DiscussionReadResult result = await ReadAsync(id, alice, options: new DiscussionReadOptions(ExcludeEvents: true, Tail: 2));

        Assert.Equal(new[] { "a", "b" }, result.Messages.Select(m => m.Content));
        Assert.Equal(6L, result.NextAfterSequence);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(50, 3)]
    public async Task TailLongerThanTheTranscriptReturnsAllOfIt(int tail, int expected)
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "x");

        DiscussionReadResult result = await ReadAsync(id, alice, options: new DiscussionReadOptions(Tail: tail));

        Assert.Equal(expected, result.Messages.Count);
        Assert.Equal(3L, result.Messages[^1].Sequence);
    }

    [Fact]
    public async Task TailWithAnExplicitZeroAfterSequenceIsAccepted()
    {
        (string id, string alice, _) = Open();

        DiscussionReadResult result = await ReadAsync(id, alice, after: 0, options: new DiscussionReadOptions(Tail: 1));

        Assert.Single(result.Messages);
    }

    [Theory]
    [InlineData(0, 0, null, 0, "LIMIT_EXCEEDED", "tail")]
    [InlineData(51, 0, null, 0, "LIMIT_EXCEEDED", "tail")]
    [InlineData(2, 1, null, 0, "INVALID_ARGUMENT", "afterSequence")]
    [InlineData(2, 0, 5, 0, "INVALID_ARGUMENT", "limit")]
    [InlineData(2, 0, null, 3, "INVALID_ARGUMENT", "waitSeconds")]
    public async Task TailThatBreaksARuleIsRefusedAndTheReaderIsNotMoved(int tail, long after, int? limit, int wait, string code, string parameter)
    {
        (string id, string alice, string bob) = Open();

        DiscussionException error = await Assert.ThrowsAsync<DiscussionException>(() => ReadAsync(id, alice, after, limit, wait, new DiscussionReadOptions(Tail: tail)));

        Assert.Equal((code, parameter), (error.Code, error.Parameter));
        DiscussionReadResult seen = await ReadAsync(id, bob);
        Assert.Equal(0, seen.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
    }

    // ---- The wait with a filter ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondEventDoesNotEndTheWaitButAPeerMessageDoes()
    {
        (string id, string alice, string bob) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);
        Assert.False(waiting.IsCompleted);

        _rig.Join(id, "Carol"); // an event: wakes the reader, who looks again and waits on
        await _rig.UntilReaderWaitsAgainAsync(id, readPosition: 3);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, _rig.Hub.GetDiagnostics().WaiterCount);

        _rig.Post(id, bob, "now");
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "now" }, result.Messages.Select(m => m.Content));
        Assert.Equal((4L, false), (result.NextAfterSequence, result.TimedOut));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task TheDeadlineIsFixedOnceSoEventsDoNotExtendTheWait()
    {
        (string id, string alice, _) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);
        await _rig.UntilWaitersAsync(1);
        _rig.Time.Advance(TimeSpan.FromSeconds(10));
        _rig.Join(id, "Carol");
        await _rig.UntilReaderWaitsAgainAsync(id, readPosition: 3);
        Assert.False(waiting.IsCompleted);

        _rig.Time.Advance(TimeSpan.FromSeconds(9));
        await Task.Yield();
        Assert.False(waiting.IsCompleted);
        _rig.Time.Advance(TimeSpan.FromSeconds(1));
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.TimedOut);
        Assert.Empty(result.Messages);
        Assert.Equal(3L, result.NextAfterSequence);
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ASecondFilteredWaitOfTheSameReaderEndsTheFirstAtOnceInsteadOfWakingEachOtherForever()
    {
        (string id, string alice, string bob) = Open();
        Task<DiscussionReadResult> first = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);
        await _rig.UntilWaitersAsync(1);
        Task<DiscussionReadResult> second = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);

        // The first wait was taken over: it ends with one last look (nothing to show, no time-out) and does not register again.
        DiscussionReadResult taken = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(taken.Messages);
        Assert.False(taken.TimedOut);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, _rig.Hub.GetDiagnostics().WaiterCount);

        _rig.Post(id, bob, "now");
        DiscussionReadResult result = await second.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "now" }, result.Messages.Select(m => m.Content));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ADiscussionThatClosesDuringTheWaitReturnsAtOnceWithoutTimedOut()
    {
        (string id, string alice, string bob) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);

        _rig.Hub.Close(id, bob);
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(result.Messages);
        Assert.False(result.TimedOut);
        Assert.Equal(DiscussionSessionState.Closed, result.Session.State);
        Assert.Equal(3L, result.NextAfterSequence);
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task AReaderThatIsAlreadyBehindAMessageThatPassesDoesNotWait()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "ready");

        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "ready" }, result.Messages.Select(m => m.Content));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ACancelledFilteredWaitLeavesNoWaiter()
    {
        (string id, string alice, _) = Open();
        using var cancel = new CancellationTokenSource();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents, cancel: cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ADisposedBoardEndsAFilteredWaitWithTheDisposedError()
    {
        (string id, string alice, _) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: NoEvents);

        _rig.Hub.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---- The gateway: arguments ---------------------------------------------------------------------------------------------------------

    private async Task<string> GatewayReadAsync(string id, string token, string extra)
    {
        DiscussionReply reply = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"{{extra}} }"""), CancellationToken.None);
        Assert.True(reply.IsOk, reply.Failure?.Message);
        return reply.ResultJson!;
    }

    private async Task<DiscussionFailure> GatewayFailureAsync(string id, string token, string extra)
    {
        DiscussionReply reply = await _gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"{{extra}} }"""), CancellationToken.None);
        Assert.False(reply.IsOk);
        return reply.Failure!;
    }

    [Fact]
    public async Task TheGatewayPassesExcludeEventsAndTailToTheBoard()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "one");
        _rig.Post(id, bob, "two");

        using JsonDocument forward = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","excludeEvents":true"""));
        using JsonDocument tail = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","tail":1"""));

        Assert.Equal(new[] { "one", "two" }, forward.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        Assert.Equal(new[] { "two" }, tail.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        Assert.Equal(4, tail.RootElement.GetProperty("nextAfterSequence").GetInt32());
    }

    [Theory]
    [InlineData(""","excludeEvents":"yes" """, "excludeEvents")]
    [InlineData(""","tail":"3" """, "tail")]
    [InlineData(""","tail":1.5""", "tail")]
    [InlineData(""","tail":99999999999""", "tail")]
    [InlineData(""","view":"table" """, "view")]
    [InlineData(""","view":2""", "view")]
    [InlineData(""","tail":2,"limit":5""", "limit")]
    [InlineData(""","tail":2,"afterSequence":1""", "afterSequence")]
    [InlineData(""","tail":2,"waitSeconds":1""", "waitSeconds")]
    [InlineData(""","tail":0""", "tail")]
    public async Task ABadArgumentIsRefusedWithItsName(string extra, string parameter)
    {
        (string id, string alice, _) = Open();

        DiscussionFailure failure = await GatewayFailureAsync(id, alice, extra);

        Assert.Equal(parameter, failure.Parameter);
    }

    [Fact]
    public async Task TailWithAnExplicitZeroAfterSequenceAndNoLimitPassesThroughTheGateway()
    {
        (string id, string alice, _) = Open();

        using JsonDocument document = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","tail":1,"afterSequence":0,"waitSeconds":0"""));

        Assert.Equal(1, document.RootElement.GetProperty("messages").GetArrayLength());
    }

    // ---- The gateway: summary view ------------------------------------------------------------------------------------------------------

    private static string Text(int length, char c = 'a') => new(c, length);

    [Fact]
    public async Task TheSummaryViewWritesOnlyTheSurveyFieldsAndAlwaysTheOrigin()
    {
        (string id, string alice, string bob) = Open();
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", """{"v":1}""", null);
        _rig.Hub.Post(id, bob, new DiscussionPostRequest("reply text", ReplyToSequence: 3, ContextIds: new long[] { 1 }, Interpretation: new DiscussionInterpretation(Summary: "my summary", Direction: "Bearish", Conclusion: "c")));

        using JsonDocument document = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary" """));

        JsonElement[] messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sequence", "kind", "origin", "summary", "contentTruncated" }, messages[0].EnumerateObject().Select(p => p.Name)); // join event: no handle
        Assert.Equal("server", messages[0].GetProperty("origin").GetString());
        Assert.Equal("p1 (Alice) joined", messages[0].GetProperty("summary").GetString());
        JsonElement analysis = messages[2];
        Assert.Equal(new[] { "sequence", "kind", "origin", "handle", "contextIds", "summary", "contentTruncated" }, analysis.EnumerateObject().Select(p => p.Name));
        Assert.Equal("peer", messages[3].GetProperty("origin").GetString());
        Assert.Equal(
            new[] { "sequence", "kind", "origin", "handle", "replyToSequence", "contextIds", "direction", "summary", "contentTruncated" },
            messages[3].EnumerateObject().Select(p => p.Name));
        Assert.Equal("my summary", messages[3].GetProperty("summary").GetString());
        Assert.Equal("Bearish", messages[3].GetProperty("direction").GetString());
        Assert.False(messages[3].GetProperty("contentTruncated").GetBoolean());
        Assert.DoesNotContain("reply text", document.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("displayName", document.RootElement.GetProperty("messages").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(new[] { "messages", "nextAfterSequence", "lastSequence", "hasMore", "timedOut", "session" }, document.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheSummaryViewCutsLongContentAtTheLimitAndSaysSo()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, Text(DiscussionExtensionLimits.SummaryViewContentChars));
        _rig.Post(id, bob, Text(DiscussionExtensionLimits.SummaryViewContentChars + 1));

        using JsonDocument document = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary","afterSequence":2 """));

        JsonElement[] messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(DiscussionExtensionLimits.SummaryViewContentChars, messages[0].GetProperty("summary").GetString()!.Length);
        Assert.False(messages[0].GetProperty("contentTruncated").GetBoolean());
        Assert.Equal(DiscussionExtensionLimits.SummaryViewContentChars, messages[1].GetProperty("summary").GetString()!.Length);
        Assert.True(messages[1].GetProperty("contentTruncated").GetBoolean());
    }

    [Fact]
    public async Task TheSummaryViewNeverSplitsASurrogatePair()
    {
        (string id, string alice, string bob) = Open();
        int limit = DiscussionExtensionLimits.SummaryViewContentChars;
        _rig.Post(id, bob, Text(limit - 1) + "\U0001F4C8" + "tail"); // the pair sits at characters limit-1 and limit

        using JsonDocument document = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary","afterSequence":2 """));

        JsonElement message = document.RootElement.GetProperty("messages")[0];
        Assert.Equal(limit - 1, message.GetProperty("summary").GetString()!.Length);
        Assert.True(message.GetProperty("contentTruncated").GetBoolean());
    }

    [Fact]
    public async Task TheSummaryViewLeavesOutAnEmptySummaryAndCompactLeavesOutTheFalseFlag()
    {
        (string id, string alice, string bob) = Open();
        _rig.Hub.Post(id, bob, new DiscussionPostRequest(null, Interpretation: new DiscussionInterpretation(Findings: new[] { "f" })));
        _rig.Post(id, bob, "short");

        using JsonDocument plain = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary","afterSequence":2 """));
        using JsonDocument compact = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary","afterSequence":2,"compact":true,"headerMode":"minimal" """));

        JsonElement[] plainMessages = plain.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sequence", "kind", "origin", "handle", "contentTruncated" }, plainMessages[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("short", plainMessages[1].GetProperty("summary").GetString());
        JsonElement[] compactMessages = compact.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sequence", "kind", "origin", "handle" }, compactMessages[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "sequence", "kind", "origin", "handle", "summary" }, compactMessages[1].EnumerateObject().Select(p => p.Name));
        Assert.False(compact.RootElement.TryGetProperty("timedOut", out _));
        Assert.Equal(new[] { "id", "state", "messageCount", "remainingMessages", "contextCount" }, compact.RootElement.GetProperty("session").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheFullViewIsTheDefaultAndExplicitFullIsTheSame()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "hello");

        string plain = await GatewayReadAsync(id, alice, "");
        string full = await GatewayReadAsync(id, alice, ""","view":"full" """);

        Assert.Equal(plain, full);
        Assert.Contains("\"displayName\"", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryViewTogetherWithTailAndExcludeEventsGivesTheNewestSummaries()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "first");
        _rig.Join(id, "Carol");
        _rig.Post(id, bob, "second");

        using JsonDocument document = JsonDocument.Parse(await GatewayReadAsync(id, alice, ""","view":"summary","excludeEvents":true,"tail":2,"headerMode":"none" """));

        Assert.Equal(new[] { "first", "second" }, document.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("summary").GetString()));
        Assert.False(document.RootElement.TryGetProperty("session", out _));
    }
}
