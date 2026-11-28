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
/// The <c>excludeOwn</c> option of a read: the caller's own messages and analysis entries are left out, the cursor still moves past them, and a wait ends only when a record
/// that passes arrives. Alice is p1, Bob p2; their joins are events 1 and 2.
/// </summary>
public sealed class DiscussionExcludeOwnTests : IDisposable
{
    private static readonly DiscussionReadOptions OwnOut = new(ExcludeOwn: true);

    private readonly HubRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private (string Id, string Alice, string Bob) Open()
    {
        string id = _rig.NewSession();
        return (id, _rig.Join(id, "Alice").Token, _rig.Join(id, "Bob").Token);
    }

    private Task<DiscussionReadResult> ReadAsync(string id, string token, long after = 0, int? limit = null, int wait = 0, DiscussionReadOptions? options = null)
        => _rig.Hub.ReadAsync(id, token, after, limit, wait, CancellationToken.None, options);

    // ---- what is left out ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OwnMessagesAreLeftOutAndTheCursorMovesPastThem()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, alice, "mine");
        _rig.Post(id, bob, "theirs");

        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, options: OwnOut);

        Assert.Equal(new[] { "theirs" }, result.Messages.Select(m => m.Content));
        Assert.Equal((4L, 4L, false, false), (result.NextAfterSequence, result.LastSequence, result.HasMore, result.TimedOut));
        Assert.Equal(4L, result.Session.Participants.Single(p => p.Handle == "p1").LastReadSequence);
    }

    [Fact]
    public async Task AnOwnMessageAtTheEndStillMovesTheCursorToTheEnd()
    {
        (string id, string alice, _) = Open();
        _rig.Post(id, alice, "mine");

        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, options: OwnOut);

        Assert.Empty(result.Messages);
        Assert.Equal((3L, 3L, false), (result.NextAfterSequence, result.LastSequence, result.HasMore));
    }

    [Fact]
    public async Task OwnAnalysisEntriesAreLeftOutAndPeerAnalysisEntriesStay()
    {
        (string id, string alice, string bob) = Open();
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", """{"v":1}""", null);
        _rig.Hub.AttachCommit(id, bob, "sa_x", "{}", """{"v":2}""", null);

        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, options: OwnOut);

        DiscussionReadMessage only = Assert.Single(result.Messages);
        Assert.Equal((4L, DiscussionMessageKind.Analysis, "p2"), (only.Sequence, only.Kind, only.Handle));
        Assert.Equal(4L, result.NextAfterSequence);
    }

    [Theory]
    [InlineData(false, false, new long[] { 1, 2, 3, 4 })]
    [InlineData(true, false, new long[] { 1, 2, 4 })]
    [InlineData(false, true, new long[] { 3, 4 })]
    [InlineData(true, true, new long[] { 4 })]
    public async Task EventsAreRemovedOnlyByExcludeEventsAndOwnRecordsOnlyByExcludeOwn(bool excludeOwn, bool excludeEvents, long[] expected)
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, alice, "mine");
        _rig.Post(id, bob, "theirs");

        DiscussionReadResult result = await ReadAsync(id, alice, options: new DiscussionReadOptions(ExcludeEvents: excludeEvents, ExcludeOwn: excludeOwn));

        Assert.Equal(expected, result.Messages.Select(m => m.Sequence));
    }

    [Fact]
    public async Task LimitCountsOnlyTheRecordsThatPass()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, alice, "mine1");
        _rig.Post(id, alice, "mine2");
        _rig.Post(id, bob, "theirs1");
        _rig.Post(id, bob, "theirs2");

        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, limit: 1, options: OwnOut);

        Assert.Equal(new[] { "theirs1" }, result.Messages.Select(m => m.Content));
        Assert.Equal((5L, 6L, true), (result.NextAfterSequence, result.LastSequence, result.HasMore));
    }

    [Fact]
    public async Task AReaderThatAlreadyHasAPassingRecordBehindItsCursorDoesNotWait()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "theirs");

        // The manual clock never moves here, so a read that wrongly waits would never return: the real-time guard turns that hang into a failure.
        DiscussionReadResult result = await ReadAsync(id, alice, after: 2, wait: 20, options: OwnOut).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "theirs" }, result.Messages.Select(m => m.Content));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    // ---- the wait ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWaitIsNotEndedByTheCallersOwnPostButByAPeersPost()
    {
        (string id, string alice, string bob) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 20, options: OwnOut);
        await _rig.UntilWaitersAsync(1);

        _rig.Post(id, alice, "mine"); // wakes the reader, who finds nothing that passes and waits on
        await _rig.UntilReaderWaitsAgainAsync(id, readPosition: 3);
        Assert.False(waiting.IsCompleted);

        _rig.Post(id, bob, "theirs");
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "theirs" }, result.Messages.Select(m => m.Content));
        Assert.Equal((4L, false), (result.NextAfterSequence, result.TimedOut));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task AWaitThatOnlySeesTheCallersOwnPostTimesOutWithTimedOutTrue()
    {
        (string id, string alice, _) = Open();
        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 2, wait: 10, options: OwnOut);
        await _rig.UntilWaitersAsync(1);

        _rig.Post(id, alice, "mine");
        await _rig.UntilReaderWaitsAgainAsync(id, readPosition: 3);
        _rig.Time.Advance(TimeSpan.FromSeconds(10));
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(result.Messages);
        Assert.True(result.TimedOut);
        Assert.Equal((3L, 3L), (result.NextAfterSequence, result.LastSequence));
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    // ---- combined with tail and waitForTurn --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TailCountsOnlyTheRecordsThatPass()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, bob, "a");
        _rig.Post(id, alice, "b");
        _rig.Post(id, bob, "c");

        DiscussionReadResult withOwn = await ReadAsync(id, alice, options: new DiscussionReadOptions(Tail: 2));
        DiscussionReadResult withoutOwn = await ReadAsync(id, alice, options: new DiscussionReadOptions(Tail: 2, ExcludeOwn: true));

        Assert.Equal(new[] { "b", "c" }, withOwn.Messages.Select(m => m.Content));
        Assert.Equal(new[] { "a", "c" }, withoutOwn.Messages.Select(m => m.Content));
        Assert.Equal(5L, withoutOwn.NextAfterSequence);
    }

    [Fact]
    public async Task WaitForTurnAccumulatesOnlyPeerMessagesWhenOwnAreLeftOut()
    {
        string id = _rig.Hub.Create("round table", null, DiscussionTurnMode.RoundRobin).Session.Id;
        string alice = _rig.Join(id, "Alice").Token;
        string bob = _rig.Join(id, "Bob").Token;
        string carol = _rig.Join(id, "Carol").Token;
        _rig.Post(id, alice, "m0"); // floor -> Bob

        Task<DiscussionReadResult> waiting = ReadAsync(id, alice, after: 3, wait: 20, options: new DiscussionReadOptions(ExcludeEvents: true, WaitForTurn: true, ExcludeOwn: true));
        await _rig.UntilWaitersAsync(1);
        _rig.Post(id, bob, "m1"); // floor -> Carol
        await _rig.UntilWaitersAsync(1);
        Assert.False(waiting.IsCompleted);
        _rig.Post(id, carol, "m2"); // floor -> Alice
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.Equal(new[] { "m1", "m2" }, result.Messages.Select(m => m.Content));
        Assert.Equal("p1", result.Session.Floor!.Handle);
        Assert.Equal(0, _rig.Hub.GetDiagnostics().WaiterCount);
    }

    // ---- by handle ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AParticipantThatRejoinedKeepsItsHandleSoItsEarlierMessagesAreOwnToo()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, alice, "before leaving");
        _rig.Post(id, bob, "theirs");
        _rig.Hub.Leave(id, alice);
        DiscussionJoinResult again = _rig.Hub.Join(id, "Alice");
        Assert.Equal("p1", again.Handle);
        Assert.True(again.Rejoined);

        DiscussionReadResult without = await ReadAsync(id, again.ParticipantToken, options: new DiscussionReadOptions(ExcludeEvents: true, ExcludeOwn: true));
        DiscussionReadResult with = await ReadAsync(id, again.ParticipantToken, options: new DiscussionReadOptions(ExcludeEvents: true));

        Assert.Equal(new[] { "theirs" }, without.Messages.Select(m => m.Content));
        Assert.Equal(new[] { "before leaving", "theirs" }, with.Messages.Select(m => m.Content));
    }

    // ---- the kind table ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table that decides which kinds are the caller's own when they carry its handle. A new <see cref="DiscussionMessageKind"/> member fails this test until somebody adds
    /// it here AND decides about it in the hub: the compiler gives no enforcing warning (no warnings are errors in this solution).
    /// </summary>
    private static readonly Dictionary<DiscussionMessageKind, bool> OwnAuthoredKinds = new()
    {
        [DiscussionMessageKind.Message] = true,
        [DiscussionMessageKind.Analysis] = true,
        [DiscussionMessageKind.Event] = false,
    };

    [Fact]
    public async Task EveryMessageKindIsClassifiedInTheTableAndTheHubFollowsTheTable()
    {
        Assert.Equal(3, Enum.GetValues<DiscussionMessageKind>().Length);
        Assert.Equal(Enum.GetValues<DiscussionMessageKind>().Order(), OwnAuthoredKinds.Keys.Order());

        (string id, string alice, _) = Open();
        _rig.Post(id, alice, "mine");
        _rig.Hub.AttachCommit(id, alice, "sa_x", "{}", """{"v":1}""", null);

        DiscussionReadResult everything = await ReadAsync(id, alice, options: new DiscussionReadOptions(ExcludeOwn: false, ExcludeEvents: false, Tail: 50));
        DiscussionReadResult withoutOwn = await ReadAsync(id, alice, options: new DiscussionReadOptions(ExcludeOwn: true, Tail: 50));

        foreach (DiscussionMessageKind kind in Enum.GetValues<DiscussionMessageKind>())
        {
            bool presentWithoutOwn = withoutOwn.Messages.Any(m => m.Kind == kind);
            bool presentAtAll = everything.Messages.Any(m => m.Kind == kind);
            Assert.True(presentAtAll, $"The test set-up has no record of kind {kind}.");
            Assert.Equal(!OwnAuthoredKinds[kind], presentWithoutOwn);
        }
    }

    // ---- the gateway ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGatewayReadsTheArgumentAndAnAbsentArgumentEqualsFalseByteForByte()
    {
        (string id, string alice, string bob) = Open();
        _rig.Post(id, alice, "mine");
        _rig.Post(id, bob, "theirs");
        var gateway = new DiscussionHubGateway(_rig.Hub, _rig.Settings);

        async Task<string> ReadJsonAsync(string extra)
        {
            DiscussionReply reply = await gateway.ExecuteAsync(
                new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}","afterSequence":2{{extra}}}"""),
                CancellationToken.None);
            Assert.True(reply.IsOk, reply.Failure?.Message);
            return reply.ResultJson!;
        }

        string absent = await ReadJsonAsync(string.Empty);
        string explicitFalse = await ReadJsonAsync(""","excludeOwn":false""");
        string explicitNull = await ReadJsonAsync(""","excludeOwn":null""");
        string on = await ReadJsonAsync(""","excludeOwn":true""");

        Assert.Equal(absent, explicitFalse);
        Assert.Equal(absent, explicitNull);
        Assert.Contains("mine", absent, StringComparison.Ordinal);
        Assert.DoesNotContain("mine", on, StringComparison.Ordinal);
        Assert.Contains("theirs", on, StringComparison.Ordinal);

        DiscussionReply notBoolean = await gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}","excludeOwn":"yes"}"""),
            CancellationToken.None);
        Assert.False(notBoolean.IsOk);
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, DiscussionArgumentNames.ExcludeOwn), (notBoolean.Failure!.Code, notBoolean.Failure.Parameter));
    }

    [Fact]
    public void TheReadAllowListHoldsExcludeOwnAndAnOlderHubWouldRefuseIt()
    {
        Assert.True(DiscussionOperationArguments.IsKnown(DiscussionOperations.Read, DiscussionArgumentNames.ExcludeOwn));
        Assert.False(DiscussionOperationArguments.IsKnown(DiscussionOperations.Post, DiscussionArgumentNames.ExcludeOwn));
    }
}
