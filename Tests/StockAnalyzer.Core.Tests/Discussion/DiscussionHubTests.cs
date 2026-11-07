#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// Task T2 of the discussion board: the hub's rules, one section per part of the specification (5.3 state machine, 5.4 participants, 5.6 post pipeline in its exact
/// order, 5.7 reads and waits, 5.11 audit). Time is a manual clock, so every expiry and wait is deterministic.
/// </summary>
public sealed class DiscussionHubTests
{
    private const string Alice = "Alice";
    private const string Bob = "Bob";

    // ---- Create -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACreatedDiscussionIsActiveFreeAndEmptyWithTheHubEpoch()
    {
        using var rig = new HubRig();

        DiscussionCreateResult created = rig.Hub.Create("7203 check", "Is the trend up?", DiscussionTurnMode.Free);

        DiscussionSessionHeader header = created.Session;
        Assert.Matches("^S[a-z2-7]{6}$", header.Id);
        Assert.Equal("7203 check", header.Title);
        Assert.Equal("Is the trend up?", header.Topic);
        Assert.Equal(DiscussionSessionState.Active, header.State);
        Assert.Null(header.CloseReason);
        Assert.Equal(DiscussionTurnMode.Free, header.TurnMode);
        Assert.Null(header.Floor);
        Assert.Equal(0, header.MessageCount);
        Assert.Equal(300, header.RemainingMessages);
        Assert.Empty(header.Participants);
        Assert.Equal(rig.Hub.Epoch, header.HubEpoch);
        Assert.Equal(rig.Time.GetUtcNow(), created.CreatedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    public void ABadTitleIsRefusedAsInvalid(string? title)
    {
        using var rig = new HubRig();

        DiscussionException error = HubRig.Refused(() => rig.Hub.Create(title, null, DiscussionTurnMode.Free));

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("title", error.Parameter);
    }

    [Fact]
    public void ATitleAndATopicBeyondTheirLengthAreLimitErrorsAndTheEdgeIsAccepted()
    {
        using var rig = new HubRig();

        rig.Hub.Create(new string('t', 100), new string('x', 2000), DiscussionTurnMode.Free);
        DiscussionException title = HubRig.Refused(() => rig.Hub.Create(new string('t', 101), null, DiscussionTurnMode.Free));
        DiscussionException topic = HubRig.Refused(() => rig.Hub.Create("t", new string('x', 2001), DiscussionTurnMode.Free));

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "title"), (title.Code, title.Parameter));
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "topic"), (topic.Code, topic.Parameter));
    }

    [Fact]
    public void ATopicMayHoldLineBreaksButNotOtherControlCharacters()
    {
        using var rig = new HubRig();

        rig.Hub.Create("t", "line one\r\n\tline two", DiscussionTurnMode.Free);
        DiscussionException error = HubRig.Refused(() => rig.Hub.Create("t", "bell\a", DiscussionTurnMode.Free));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "topic"), (error.Code, error.Parameter));
    }

    [Fact]
    public void OnlyActiveDiscussionsCountTowardTheActiveLimit()
    {
        using var rig = new HubRig(s => { s.MaxActiveSessions = 2; s.ClosedRetentionSeconds = 3600; });
        string first = rig.NewSession();
        string second = rig.NewSession();
        (string token, _) = rig.Join(first, Alice);

        DiscussionException full = HubRig.Refused(() => rig.NewSession());
        rig.Hub.Close(first, token);
        string third = rig.NewSession();

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "sessions"), (full.Code, full.Parameter));
        Assert.NotEqual(second, third);
        Assert.Equal(3, rig.Hub.Status().Sessions.Count); // the closed one is still kept for reading
    }

    // ---- Join / leave -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void JoiningIssuesHandlesInOrderTheProtocolTextAndAServerEvent()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();

        DiscussionJoinResult alice = rig.Hub.Join(id, "  Alice  ");
        DiscussionJoinResult bob = rig.Hub.Join(id, Bob);

        Assert.Equal("p1", alice.Handle);
        Assert.Equal("p2", bob.Handle);
        Assert.False(alice.Rejoined);
        Assert.Equal(DiscussionProtocolText.Text, alice.Protocol);
        Assert.Equal(29, alice.ParticipantToken.Length);
        Assert.StartsWith(rig.Hub.Epoch + ".", alice.ParticipantToken, StringComparison.Ordinal);
        Assert.Equal(1, alice.LastSequence);
        Assert.Equal(2, bob.LastSequence);
        IReadOnlyList<DiscussionReadMessage> transcript = rig.Transcript(id, alice.ParticipantToken);
        Assert.Equal(new[] { "p1 (Alice) joined", "p2 (Bob) joined" }, transcript.Select(m => m.Content));
        Assert.All(transcript, m => { Assert.Equal(DiscussionMessageKind.Event, m.Kind); Assert.Null(m.Handle); Assert.Null(m.DisplayName); });
        Assert.Equal(new[] { "Alice", "Bob" }, bob.Session.Participants.Select(p => p.DisplayName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    [InlineData("a\u0007b")]
    public void ABadDisplayNameIsRefusedAsInvalid(string? name)
    {
        using var rig = new HubRig();
        string id = rig.NewSession();

        DiscussionException error = HubRig.Refused(() => rig.Hub.Join(id, name));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "displayName"), (error.Code, error.Parameter));
    }

    [Fact]
    public void ADisplayNameBeyondSixtyFourCharactersAfterTrimmingIsALimitError()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();

        rig.Hub.Join(id, " " + new string('n', 64) + " ");
        DiscussionException error = HubRig.Refused(() => rig.Hub.Join(id, new string('m', 65)));

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "displayName"), (error.Code, error.Parameter));
    }

    [Fact]
    public void ANameInUseByAnActiveParticipantIsRefusedButCaseDiffers()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        rig.Join(id, Alice);

        DiscussionException same = HubRig.Refused(() => rig.Hub.Join(id, Alice));
        DiscussionJoinResult lower = rig.Hub.Join(id, "alice");

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, same.Code);
        Assert.Equal("name in use by an active participant", same.Hint);
        Assert.Equal("p2", lower.Handle);
    }

    [Fact]
    public void JoiningAnUnknownOrNullSessionIsNotFound()
    {
        using var rig = new HubRig();

        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Hub.Join("Sunknow", Alice)));
        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Hub.Join(null, Alice)));
    }

    [Fact]
    public void TheParticipantLimitCountsOnlyParticipantsWhoHaveNotLeft()
    {
        using var rig = new HubRig(s => s.MaxParticipantsPerSession = 2);
        string id = rig.NewSession();
        (string aliceToken, _) = rig.Join(id, Alice);
        rig.Join(id, Bob);

        DiscussionException full = HubRig.Refused(() => rig.Hub.Join(id, "Carol"));
        rig.Hub.Leave(id, aliceToken);
        DiscussionJoinResult carol = rig.Hub.Join(id, "Carol");

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "participants"), (full.Code, full.Parameter));
        Assert.Equal("p3", carol.Handle);
    }

    [Fact]
    public void TheJoinCounterBoundsChurnAndEveryRejoinIsCountedButNeverRefused()
    {
        using var rig = new HubRig(s => s.MaxParticipantsPerSession = 2); // joins allowed: 4
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        rig.Join(id, Bob);

        for (int i = 0; i < 3; i++)
        {
            rig.Hub.Leave(id, token);
            token = rig.Hub.Join(id, Alice).ParticipantToken; // joins 3, 4, 5: a rejoin is counted, not checked
        }

        rig.Hub.Leave(id, token);
        DiscussionException error = HubRig.Refused(() => rig.Hub.Join(id, "Carol"));

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "joins"), (error.Code, error.Parameter));
    }

    [Fact]
    public void AnAwayOrLeftParticipantRejoinsWithTheSameHandleANewTokenAndTheOldTokenDies()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string oldToken, string handle) = rig.Join(id, Alice);
        rig.Join(id, Bob);
        rig.Time.Advance(rig.Settings.AwaySeconds); // Alice is now away

        DiscussionJoinResult back = rig.Hub.Join(id, Alice);

        Assert.True(back.Rejoined);
        Assert.Equal(handle, back.Handle);
        Assert.NotEqual(oldToken, back.ParticipantToken);
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(id, oldToken, "old")));
        rig.Post(id, back.ParticipantToken, "new");
        Assert.Contains("p1 (Alice) rejoined", rig.Transcript(id, back.ParticipantToken).Select(m => m.Content));
        Assert.Equal(2, back.Session.Participants.Count);
    }

    [Fact]
    public void AParticipantTurnsAwayAfterTheAwayTimeAndBackOnAnyAccess()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        rig.Time.Advance(rig.Settings.AwaySeconds - 1);
        DiscussionParticipantState before = rig.Hub.Status(id).Sessions[0].Participants[0].State;
        rig.Time.Advance(1);
        DiscussionParticipantState away = rig.Hub.Status(id).Sessions[0].Participants[0].State;
        rig.Hub.ReadAsync(id, token).GetAwaiter().GetResult();
        DiscussionParticipantState after = rig.Hub.Status(id).Sessions[0].Participants[0].State;

        Assert.Equal(DiscussionParticipantState.Active, before);
        Assert.Equal(DiscussionParticipantState.Away, away);
        Assert.Equal(DiscussionParticipantState.Active, after);
    }

    [Fact]
    public void LeavingIsIdempotentAppendsOneEventAndALeftTokenStillReadsButNeverSpeaks()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);

        DiscussionLeaveResult first = rig.Hub.Leave(id, alice);
        DiscussionLeaveResult second = rig.Hub.Leave(id, alice);

        Assert.False(first.AlreadyLeft);
        Assert.True(second.AlreadyLeft);
        Assert.Equal(1, rig.Transcript(id, bob).Count(m => m.Content == "p1 (Alice) left"));
        Assert.Equal(3, rig.Transcript(id, alice).Count); // a left participant may still read the transcript
        DiscussionException post = HubRig.Refused(() => rig.Post(id, alice, "hello"));
        Assert.Equal((DiscussionErrorCodes.ParticipantNotJoined, "left"), (post.Code, post.Hint));
        Assert.Equal("left", HubRig.Refused(() => rig.Hub.Close(id, alice)).Hint);
        Assert.Equal("left", HubRig.Refused(() => rig.Hub.AttachPrecheck(id, alice)).Hint);
    }

    // ---- Close and the state machine (5.3) --------------------------------------------------------------------------------------------

    [Fact]
    public void ClosingRecordsTheEventRefusesLaterChangesAndKeepsTheTranscriptReadable()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);
        rig.Post(id, alice, "one");

        DiscussionCloseResult closed = rig.Hub.Close(id, bob);

        Assert.False(closed.AlreadyClosed);
        Assert.Equal(DiscussionSessionState.Closed, closed.State);
        Assert.Equal(DiscussionCloseReason.Participant, closed.CloseReason);
        Assert.Equal(4, closed.MessageCount);
        Assert.Equal("closed by p2", rig.Transcript(id, alice).Last().Content);
        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, HubRig.CodeOf(() => rig.Post(id, alice, "two")));
        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, HubRig.CodeOf(() => rig.Hub.Join(id, "Carol")));
        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, HubRig.CodeOf(() => rig.Hub.AttachPrecheck(id, alice)));
        Assert.False(rig.Hub.Leave(id, alice).AlreadyLeft); // accepted, no effect
        Assert.True(rig.Hub.Close(id, alice).AlreadyClosed);
        Assert.Equal(4, rig.Transcript(id, alice).Count);
    }

    [Fact]
    public void ADiscussionIsClosedForIdleExactlyAtTheIdleTimeAndNotBefore()
    {
        using var rig = new HubRig(s => s.IdleTimeoutSeconds = 60);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        rig.Time.Advance(59);
        Assert.Equal(DiscussionSessionState.Active, rig.Hub.Status(id).Sessions[0].State); // status is not activity
        rig.Time.Advance(1);

        DiscussionSessionHeader header = rig.Hub.Status(id).Sessions[0];
        Assert.Equal(DiscussionSessionState.Closed, header.State);
        Assert.Equal(DiscussionCloseReason.Idle, header.CloseReason);
        Assert.Equal("closed: idle", rig.Transcript(id, token).Last().Content);
    }

    [Fact]
    public void ActivityOfAJoinedParticipantKeepsTheDiscussionAliveAndAgeEndsItAnyway()
    {
        using var rig = new HubRig(s => { s.IdleTimeoutSeconds = 60; s.MaxSessionAgeSeconds = 300; });
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        for (int i = 0; i < 5; i++)
        {
            rig.Time.Advance(50);
            rig.Post(id, token, "still here " + i);
        }

        rig.Time.Advance(50); // t = 300: the last activity was 50 s ago, but the age limit is reached
        DiscussionSessionHeader header = rig.Hub.Status(id).Sessions[0];

        Assert.Equal(DiscussionCloseReason.MaxAge, header.CloseReason);
        Assert.Equal("closed: maxAge", rig.Transcript(id, token).Last().Content);
    }

    [Fact]
    public void WhenBothLimitsAreReachedTheIdleReasonWins()
    {
        using var rig = new HubRig(s => { s.IdleTimeoutSeconds = 60; s.MaxSessionAgeSeconds = 300; });
        string id = rig.NewSession();
        rig.Time.Advance(300);

        Assert.Equal(DiscussionCloseReason.Idle, rig.Hub.Status(id).Sessions[0].CloseReason);
    }

    [Fact]
    public void AReadByAJoinedParticipantCountsAsActivity()
    {
        using var rig = new HubRig(s => s.IdleTimeoutSeconds = 60);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        rig.Time.Advance(59);
        rig.Hub.ReadAsync(id, token).GetAwaiter().GetResult();
        rig.Time.Advance(59);

        Assert.Equal(DiscussionSessionState.Active, rig.Hub.Status(id).Sessions[0].State);
    }

    [Fact]
    public void AClosedDiscussionIsRemovedAfterTheRetentionTimeAndNotBefore()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        rig.Hub.Close(id, token);

        rig.Time.Advance(rig.Settings.ClosedRetentionSeconds - 1);
        Assert.Single(rig.Transcript(id, token).Where(m => m.Content.StartsWith("closed by", StringComparison.Ordinal)));
        rig.Time.Advance(1);

        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Transcript(id, token)));
        Assert.Equal(0, rig.Hub.GetDiagnostics().SessionCount);
    }

    [Fact]
    public void WithoutRetentionTheCloseReplyStillHoldsTheEventAndTheNextCallFindsNothing()
    {
        using var rig = new HubRig(s => s.ClosedRetentionSeconds = 0);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionCloseResult closed = rig.Hub.Close(id, token);

        Assert.Equal(DiscussionSessionState.Closed, closed.State);
        Assert.Equal(2, closed.MessageCount);
        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Transcript(id, token)));
    }

    [Fact]
    public void TheBackgroundSweeperClosesAndThenRemovesWithoutAnyCall()
    {
        using var rig = new HubRig(s => { s.IdleTimeoutSeconds = 60; s.ClosedRetentionSeconds = 0; }, sweeper: true);
        rig.NewSession();

        rig.Time.Advance(59);
        Assert.Equal(1, rig.Hub.GetDiagnostics().SessionCount);
        rig.Time.Advance(1); // tick at 60: closed
        Assert.Equal(1, rig.Hub.GetDiagnostics().SessionCount);
        rig.Time.Advance(30); // tick at 90: removed

        Assert.Equal(0, rig.Hub.GetDiagnostics().SessionCount);
        Assert.Contains(rig.Hub.Status(auditTail: 20).Audit, e => e.Event == DiscussionAuditEvents.SessionClosed && e.Detail == "idle");
        Assert.Contains(rig.Hub.Status(auditTail: 20).Audit, e => e.Event == DiscussionAuditEvents.SessionPurged);
    }

    [Fact]
    public void ARestartedHubHasNoSessionsAndAnOldTokenIsAHubReset()
    {
        using var first = new HubRig();
        using var second = new HubRig();
        string id = first.NewSession();
        (string token, _) = first.Join(id, Alice);

        Assert.NotEqual(first.Hub.Epoch, second.Hub.Epoch);
        Assert.Empty(second.Hub.Status().Sessions);
        Assert.Equal(DiscussionErrorCodes.HubReset, HubRig.CodeOf(() => second.Post(id, token, "hello")));
        Assert.Equal(DiscussionErrorCodes.HubReset, HubRig.CodeOf(() => second.Hub.ReadAsync(id, token).GetAwaiter().GetResult()));
    }

    // ---- Post pipeline (5.6) in its exact order -----------------------------------------------------------------------------------------

    [Fact]
    public void Step1TheTokenIsCheckedFirstAndDistinguishesGarbageForeignSessionsAndRestarts()
    {
        using var rig = new HubRig();
        string one = rig.NewSession();
        string two = rig.NewSession();
        (string token, _) = rig.Join(one, Alice);
        rig.Join(two, Bob);

        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(one, "garbage", "x")));
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(one, null!, "x")));
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(one, rig.Hub.Epoch + ".AAAAAAAAAAAAAAAAAAAAAA", "x")));
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(two, token, "x"))); // the token of another session
        Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Post(one, rig.Hub.Epoch + "." + new string('A', 60), "x"))); // too long to be a token
        string foreignEpoch = (rig.Hub.Epoch[0] == 'a' ? "b" : "a") + rig.Hub.Epoch[1..] + ".AAAAAAAAAAAAAAAAAAAAAA";
        Assert.Equal(DiscussionErrorCodes.HubReset, HubRig.CodeOf(() => rig.Post(one, foreignEpoch, "x")));
    }

    [Fact]
    public void Step2ARepeatedRequestIdReturnsTheStoredAnswerBeforeAnyOtherCheck()
    {
        using var rig = new HubRig(s => s.MaxPostsPerMinute = 1);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        DiscussionPostResult first = rig.Post(id, token, "once", "req-1");

        DiscussionPostResult again = rig.Post(id, token, "different text that is ignored", "req-1");
        rig.Hub.Close(id, token);

        Assert.False(first.Duplicate);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Sequence, again.Sequence);
        Assert.Equal(first.CreatedUtc, again.CreatedUtc);
        // Still the stored answer when the post would now fail for rate or because the discussion is closed.
        Assert.True(rig.Post(id, token, "x", "req-1").Duplicate);
        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, HubRig.CodeOf(() => rig.Post(id, token, "x", "req-2")));
    }

    [Fact]
    public void Step2OnlyTheLast64RequestIdsAreRemembered()
    {
        using var rig = new HubRig(s => { s.MaxPostsPerMinute = 120; s.MaxMessagesPerSession = 200; });
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        for (int i = 0; i < 65; i++)
        {
            rig.Post(id, token, "m" + i, "r" + i);
        }

        long before = rig.Hub.Status(id).Sessions[0].MessageCount;
        Assert.True(rig.Post(id, token, "x", "r64").Duplicate);
        Assert.False(rig.Post(id, token, "x", "r0").Duplicate); // r0 was forgotten, so it is posted again

        Assert.Equal(before + 1, rig.Hub.Status(id).Sessions[0].MessageCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public void ABadRequestIdIsInvalidAndATooLongOneIsALimitError(string requestId)
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionException bad = HubRig.Refused(() => rig.Post(id, token, "x", requestId));
        DiscussionException tooLong = HubRig.Refused(() => rig.Post(id, token, "x", new string('r', 65)));
        rig.Post(id, token, "x", new string('r', 64));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "clientRequestId"), (bad.Code, bad.Parameter));
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "clientRequestId"), (tooLong.Code, tooLong.Parameter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Step4EmptyOrBlankContentIsInvalid(string? content)
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionException error = HubRig.Refused(() => rig.Hub.Post(id, token, new DiscussionPostRequest(content)));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "content"), (error.Code, error.Parameter));
    }

    [Fact]
    public void Step4ContentIsStoredByteExactAndTheSizeIsCountedInUtf8Bytes()
    {
        using var rig = new HubRig(s => s.MaxMessageBytes = 256);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        string exact = "  padded\twith\r\nbreaks  ";
        string edge = new('a', 256);
        string multibyte = new('あ', 86); // 86 x 3 bytes = 258 bytes, but only 86 characters

        rig.Post(id, token, exact);
        rig.Post(id, token, edge);
        DiscussionException big = HubRig.Refused(() => rig.Post(id, token, edge + "a"));
        DiscussionException wide = HubRig.Refused(() => rig.Post(id, token, multibyte));

        Assert.Equal(exact, rig.Transcript(id, token).First(m => m.Kind == DiscussionMessageKind.Message).Content);
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "content"), (big.Code, big.Parameter));
        Assert.Contains("sa_discussion_attach_analysis", big.Hint, StringComparison.Ordinal);
        Assert.Equal(DiscussionErrorCodes.LimitExceeded, wide.Code);
    }

    [Theory]
    [InlineData("bell\a")]
    [InlineData("nul\0")]
    [InlineData("del\u007f")]
    public void Step4ControlCharactersAreRefusedNeverRewritten(string content)
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionException error = HubRig.Refused(() => rig.Post(id, token, content));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "content"), (error.Code, error.Parameter));
    }

    [Fact]
    public void Step4UnpairedSurrogatesAreRefusedInContentNamesAndTitles()
    {
        // Built at run time: a lone surrogate in an attribute argument would be replaced by the compiler's UTF-8 encoding.
        string lonelyHigh = "high " + (char)0xD800 + " end";
        string lonelyLow = "low " + (char)0xDC00 + " end";
        string swapped = "swapped " + (char)0xDC00 + (char)0xD800;
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        foreach (string text in new[] { lonelyHigh, lonelyLow, swapped })
        {
            Assert.Equal("content", HubRig.Refused(() => rig.Post(id, token, text)).Parameter);
            Assert.Equal("displayName", HubRig.Refused(() => rig.Hub.Join(id, text)).Parameter);
            Assert.Equal("title", HubRig.Refused(() => rig.Hub.Create(text, null, DiscussionTurnMode.Free)).Parameter);
            Assert.Equal("topic", HubRig.Refused(() => rig.Hub.Create("t", text, DiscussionTurnMode.Free)).Parameter);
        }

        Assert.Equal(1, rig.Hub.GetDiagnostics().SessionCount); // none of the refused creates left a session behind
    }

    [Fact]
    public void Step4APairedSurrogateEmojiIsAccepted()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        rig.Post(id, token, "up \U0001F4C8");

        Assert.Equal("up \U0001F4C8", rig.Transcript(id, token).Last().Content);
    }

    [Fact]
    public void Step4TheSizeIsCheckedBeforeTheCharacters()
    {
        using var rig = new HubRig(s => s.MaxMessageBytes = 256);
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionException error = HubRig.Refused(() => rig.Post(id, token, new string('a', 300) + "\a"));

        Assert.Equal(DiscussionErrorCodes.LimitExceeded, error.Code);
    }

    [Fact]
    public void Step3AClosedDiscussionIsReportedBeforeTheContentIsLookedAt()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        rig.Hub.Close(id, token);

        Assert.Equal(DiscussionErrorCodes.DiscussionClosed, HubRig.CodeOf(() => rig.Hub.Post(id, token, new DiscussionPostRequest(null))));
    }

    [Fact]
    public void Step5ReferencesMustPointIntoTheTranscriptAndTheContexts()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        rig.Hub.AttachCommit(id, token, "sa_x", "{}", """{"v":1}""", null);
        long last = rig.Hub.Status(id).Sessions[0].MessageCount;

        DiscussionPostRequest Make(long? reply = null, long[]? contexts = null, long? basedOn = null) => new("hello", ReplyToSequence: reply, ContextIds: contexts, BasedOnSequence: basedOn);

        rig.Hub.Post(id, token, Make(reply: last, contexts: new long[] { 1 }, basedOn: last));
        Assert.Equal("replyToSequence", HubRig.Refused(() => rig.Hub.Post(id, token, Make(reply: 0))).Parameter);
        Assert.Equal("replyToSequence", HubRig.Refused(() => rig.Hub.Post(id, token, Make(reply: 99))).Parameter);
        Assert.Equal("contextIds", HubRig.Refused(() => rig.Hub.Post(id, token, Make(contexts: new long[] { 2 }))).Parameter);
        Assert.Equal("contextIds", HubRig.Refused(() => rig.Hub.Post(id, token, Make(contexts: new long[] { 0 }))).Parameter);
        Assert.Equal("contextIds", HubRig.Refused(() => rig.Hub.Post(id, token, Make(contexts: new long[] { 1, 1 }))).Parameter);
        Assert.Equal("contextIds", HubRig.Refused(() => rig.Hub.Post(id, token, Make(contexts: Enumerable.Repeat(1L, 9).ToArray()))).Parameter);
        Assert.Equal("basedOnSequence", HubRig.Refused(() => rig.Hub.Post(id, token, Make(basedOn: -1))).Parameter);
        Assert.Equal("basedOnSequence", HubRig.Refused(() => rig.Hub.Post(id, token, Make(basedOn: 999))).Parameter);
        Assert.Equal(new long[] { 1 }, rig.Transcript(id, token).Last().ContextIds);
    }

    [Fact]
    public void Step7TheRateWindowSlidesAndTheRetryHintCountsDown()
    {
        using var rig = new HubRig(s => s.MaxPostsPerMinute = 3);
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);
        for (int i = 0; i < 3; i++)
        {
            rig.Post(id, alice, "m" + i);
        }

        rig.Time.Advance(20);
        DiscussionException limited = HubRig.Refused(() => rig.Post(id, alice, "m3"));
        rig.Post(id, bob, "bob is not limited by alice");
        rig.Time.Advance(39);
        DiscussionException almost = HubRig.Refused(() => rig.Post(id, alice, "m3"));
        rig.Time.Advance(1);
        DiscussionPostResult allowed = rig.Post(id, alice, "m3");

        Assert.Equal(DiscussionErrorCodes.RateLimited, limited.Code);
        Assert.Equal("retry after 40 s", limited.Hint);
        Assert.Equal("retry after 1 s", almost.Hint);
        Assert.True(allowed.Sequence > 0);
    }

    [Fact]
    public void Step7TheRateIsCheckedBeforeCapacityAndOnceTheWindowPassedOnlyCapacityRefuses()
    {
        using var rig = new HubRig(s => { s.MaxPostsPerMinute = 1; s.MaxMessagesPerSession = 10; });
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);
        for (int i = 0; i < 7; i++)
        {
            rig.Post(id, bob, "fill" + i);
            rig.Time.Advance(61);
        }

        rig.Post(id, alice, "the tenth message"); // 2 joins + 7 + 1 = 10: the session is full
        DiscussionException limited = HubRig.Refused(() => rig.Post(id, alice, "again at once"));
        rig.Time.Advance(61);
        DiscussionException full = HubRig.Refused(() => rig.Post(id, alice, "again later"));

        Assert.Equal(DiscussionErrorCodes.RateLimited, limited.Code);
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "messages"), (full.Code, full.Parameter));
    }

    [Fact]
    public void Step8TheMessageLimitRejectsAndTheClosingEventStillFitsOnePastIt()
    {
        using var rig = new HubRig(s => { s.MaxMessagesPerSession = 10; s.MaxPostsPerMinute = 120; });
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice); // 1 event
        for (int i = 0; i < 9; i++)
        {
            rig.Post(id, token, "m" + i);
        }

        DiscussionException full = HubRig.Refused(() => rig.Post(id, token, "one too many"));
        DiscussionCloseResult closed = rig.Hub.Close(id, token);

        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "messages"), (full.Code, full.Parameter));
        Assert.Equal(11, closed.MessageCount);
        Assert.Equal(0, closed.Session.RemainingMessages);
        Assert.Equal("closed by p1", rig.Hub.ReadAsync(id, token, 0, 50).GetAwaiter().GetResult().Messages.Last().Content);
    }

    [Fact]
    public void Step8TheMemoryBudgetRejectsWithTheCounterStillExact()
    {
        using var rig = new HubRig(s =>
        {
            s.MaxTotalBytes = 1048576;
            s.MaxMessageBytes = 65536;
            s.MaxMessagesPerSession = 100;
            s.MaxPostsPerMinute = 120;
            s.MaxContextBytes = 1024;
        });
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);
        string block = new('b', 65536);
        DiscussionException? refused = null;

        for (int i = 0; i < 40 && refused is null; i++)
        {
            try
            {
                rig.Post(id, token, block);
            }
            catch (DiscussionException error)
            {
                refused = error;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "memoryBudget"), (refused!.Code, refused.Parameter));
        DiscussionDiagnostics diagnostics = rig.Hub.GetDiagnostics();
        Assert.Equal(diagnostics.RecomputedBytes, diagnostics.TotalBytes);
        Assert.True(diagnostics.TotalBytes <= 1048576);
        Assert.True(diagnostics.TotalBytes > 1048576 - 65536 - 100);
    }

    [Fact]
    public void Step9SequencesAreGapFreeNewerMessagesCountsPeersAndTheAnswerCarriesNoFloor()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);

        DiscussionPostResult a = rig.Hub.Post(id, alice, new DiscussionPostRequest("a", BasedOnSequence: 2));
        rig.Post(id, bob, "b1");
        rig.Post(id, bob, "b2");
        DiscussionPostResult stale = rig.Hub.Post(id, alice, new DiscussionPostRequest("a2", BasedOnSequence: a.Sequence));
        DiscussionPostResult none = rig.Post(id, alice, "a3");

        Assert.Equal(3, a.Sequence);
        Assert.Equal(0, a.NewerMessages);
        Assert.Equal(2, stale.NewerMessages); // b1 and b2 came after the state alice reasoned about
        Assert.Equal(0, none.NewerMessages);
        Assert.Null(a.Floor);
        Assert.Equal(Enumerable.Range(1, 7).Select(n => (long)n), rig.Transcript(id, alice).Select(m => m.Sequence));
    }

    [Fact]
    public void PassIsRefusedInAFreeDiscussionBecauseThereIsNoFloor()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        DiscussionException error = HubRig.Refused(() => rig.Hub.Post(id, token, new DiscussionPostRequest(null, Pass: true)));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "pass"), (error.Code, error.Parameter));
    }

    // ---- Read (5.7) --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReadReturnsMessagesAfterTheCursorInOrderWithPagingAndLabels()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);
        rig.Post(id, alice, "one");
        rig.Post(id, bob, "two");

        DiscussionReadResult page = await rig.Hub.ReadAsync(id, bob, afterSequence: 1, limit: 2);
        DiscussionReadResult rest = await rig.Hub.ReadAsync(id, bob, afterSequence: page.NextAfterSequence);
        DiscussionReadResult beyond = await rig.Hub.ReadAsync(id, bob, afterSequence: 99);

        Assert.Equal(new long[] { 2, 3 }, page.Messages.Select(m => m.Sequence));
        Assert.True(page.HasMore);
        Assert.Equal(3, page.NextAfterSequence);
        Assert.Equal(4, page.LastSequence);
        Assert.Equal(new long[] { 4 }, rest.Messages.Select(m => m.Sequence));
        Assert.False(rest.HasMore);
        Assert.Empty(beyond.Messages);
        Assert.Equal(99, beyond.NextAfterSequence);
        Assert.False(beyond.TimedOut);
        DiscussionReadMessage one = (await rig.Hub.ReadAsync(id, bob)).Messages.First(m => m.Content == "one");
        Assert.Equal(("p1", "Alice", DiscussionMessageKind.Message), (one.Handle, one.DisplayName, one.Kind));
        Assert.Equal(DiscussionNames.OriginOf(DiscussionMessageKind.Message), "peer");
        Assert.Equal(DiscussionNames.OriginOf(DiscussionMessageKind.Event), "server");
        Assert.Equal(DiscussionNames.OriginOf(DiscussionMessageKind.Analysis), "server");
        Assert.Equal(rig.Time.GetUtcNow(), one.CreatedUtc);
    }

    [Fact]
    public async Task AReadMovesTheParticipantsLastReadMarkerButNeverBeyondTheTranscript()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        rig.Post(id, alice, "one");

        await rig.Hub.ReadAsync(id, alice, afterSequence: 1);
        long middle = rig.Hub.Status(id).Sessions[0].Participants[0].LastReadSequence;
        await rig.Hub.ReadAsync(id, alice, afterSequence: 500);

        Assert.Equal(2, middle);
        Assert.Equal(2, rig.Hub.Status(id).Sessions[0].Participants[0].LastReadSequence);
    }

    [Fact]
    public async Task ReadArgumentsAreCheckedAgainstTheirLimits()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);

        await rig.Hub.ReadAsync(id, alice, 0, 50, 20);
        await rig.Hub.ReadAsync(id, alice, 0, 1, 0);
        DiscussionException zero = await Assert.ThrowsAsync<DiscussionException>(() => rig.Hub.ReadAsync(id, alice, 0, 0));
        DiscussionException over = await Assert.ThrowsAsync<DiscussionException>(() => rig.Hub.ReadAsync(id, alice, 0, 51));
        DiscussionException wait = await Assert.ThrowsAsync<DiscussionException>(() => rig.Hub.ReadAsync(id, alice, 0, 5, 21));
        DiscussionException negativeWait = await Assert.ThrowsAsync<DiscussionException>(() => rig.Hub.ReadAsync(id, alice, 0, 5, -1));
        DiscussionException after = await Assert.ThrowsAsync<DiscussionException>(() => rig.Hub.ReadAsync(id, alice, -1));

        Assert.Equal(("limit", DiscussionErrorCodes.LimitExceeded), (zero.Parameter, zero.Code));
        Assert.Equal(("limit", DiscussionErrorCodes.LimitExceeded), (over.Parameter, over.Code));
        Assert.Equal(("waitSeconds", DiscussionErrorCodes.LimitExceeded), (wait.Parameter, wait.Code));
        Assert.Equal("waitSeconds", negativeWait.Parameter);
        Assert.Equal(("afterSequence", DiscussionErrorCodes.InvalidArgument), (after.Parameter, after.Code));
    }

    [Fact]
    public async Task AWaitingReadReturnsAsSoonAsAnotherParticipantPosts()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);

        Task<DiscussionReadResult> waiting = rig.Hub.ReadAsync(id, alice, afterSequence: 2, waitSeconds: 20);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, rig.Hub.GetDiagnostics().WaiterCount);
        rig.Post(id, bob, "hello");
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "hello" }, result.Messages.Select(m => m.Content));
        Assert.False(result.TimedOut);
        Assert.Equal(0, rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task AWaitingReadThatSeesNothingTimesOutOnTheManualClockAndLeavesNoWaiter()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);

        Task<DiscussionReadResult> waiting = rig.Hub.ReadAsync(id, alice, afterSequence: 1, waitSeconds: 20);
        rig.Time.Advance(19);
        Assert.False(waiting.IsCompleted);
        rig.Time.Advance(1);
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.TimedOut);
        Assert.Empty(result.Messages);
        Assert.Equal(1, result.NextAfterSequence);
        Assert.Equal(0, rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ACancelledWaitingReadThrowsAndLeavesNoWaiter()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        using var cancel = new CancellationTokenSource();

        Task<DiscussionReadResult> waiting = rig.Hub.ReadAsync(id, alice, 1, waitSeconds: 20, cancellationToken: cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ClosingWakesAWaitingReadWithTheClosingEvent()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);

        Task<DiscussionReadResult> waiting = rig.Hub.ReadAsync(id, alice, 2, waitSeconds: 20);
        rig.Hub.Close(id, bob);
        DiscussionReadResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "closed by p2" }, result.Messages.Select(m => m.Content));
        Assert.Equal(DiscussionSessionState.Closed, result.Session.State);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task AReadOnAClosedDiscussionNeverWaits()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        rig.Hub.Close(id, alice);

        DiscussionReadResult result = await rig.Hub.ReadAsync(id, alice, afterSequence: 2, waitSeconds: 20).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(result.Messages);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task DisposingTheHubReleasesWaitingReadersAndZeroesEveryCounter()
    {
        var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        Task<DiscussionReadResult> waiting = rig.Hub.ReadAsync(id, alice, 1, waitSeconds: 20);

        rig.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new DiscussionDiagnostics(0, 0, 0, 0), rig.Hub.GetDiagnostics());
    }

    // ---- Status and the audit ring (5.11) -----------------------------------------------------------------------------------------------

    [Fact]
    public void StatusListsDiscussionsOrOneAndRefusesAnUnknownOne()
    {
        using var rig = new HubRig();
        string one = rig.NewSession("one");
        rig.Time.Advance(1);
        string two = rig.NewSession("two");

        DiscussionStatusResult all = rig.Hub.Status();
        DiscussionStatusResult single = rig.Hub.Status(two);

        Assert.Equal(new[] { one, two }, all.Sessions.Select(s => s.Id));
        Assert.Equal(new[] { two }, single.Sessions.Select(s => s.Id));
        Assert.Equal(rig.Hub.Epoch, all.HubEpoch);
        Assert.Equal(rig.Time.GetUtcNow(), all.ServerTimeUtc);
        Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Hub.Status("Snothing")));
        Assert.Equal(DiscussionErrorCodes.LimitExceeded, HubRig.CodeOf(() => rig.Hub.Status(auditTail: 101)));
        Assert.Equal(DiscussionErrorCodes.LimitExceeded, HubRig.CodeOf(() => rig.Hub.Status(auditTail: -1)));
        rig.Hub.Status(auditTail: 100);
    }

    [Fact]
    public void TheAuditRingIsBoundedKeepsTheNewestAndRecordsRejectionsWithoutAnyContent()
    {
        using var rig = new HubRig(s => { s.AuditCapacity = 100; s.MaxPostsPerMinute = 120; s.MaxMessagesPerSession = 2000; });
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, "Secret Name");
        for (int i = 0; i < 110; i++)
        {
            rig.Post(id, token, "secret content " + i);
        }

        HubRig.Refused(() => rig.Post(id, token, new string('x', 20000)));
        IReadOnlyList<DiscussionAuditEntry> audit = rig.Hub.Status(auditTail: 100).Audit;

        Assert.Equal(100, audit.Count);
        Assert.Equal(audit.Select(e => e.N).OrderBy(n => n), audit.Select(e => e.N));
        Assert.Equal(Enumerable.Range(0, 99).Select(_ => 1L), audit.Zip(audit.Skip(1), (a, b) => b.N - a.N)); // consecutive, newest kept
        Assert.Equal(DiscussionAuditEvents.PostRejected, audit[^1].Event);
        Assert.Equal(DiscussionErrorCodes.LimitExceeded, audit[^1].Code);
        string everything = string.Join("\n", audit.Select(e => e.ToString()));
        Assert.DoesNotContain("secret", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Name", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAuditRingRecordsTheLifecycleInOrder()
    {
        using var rig = new HubRig(s => s.MaxPostsPerMinute = 1);
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);
        rig.Post(id, alice, "x");
        HubRig.Refused(() => rig.Post(id, alice, "y")); // rate limited
        rig.Hub.Leave(id, alice);
        string again = rig.Hub.Join(id, Alice).ParticipantToken;
        rig.Hub.Close(id, again);

        string[] events = rig.Hub.Status(auditTail: 100).Audit.Select(e => e.Event).ToArray();

        Assert.Equal(
            new[]
            {
                DiscussionAuditEvents.HubStarted, DiscussionAuditEvents.SessionCreated, DiscussionAuditEvents.ParticipantJoined, DiscussionAuditEvents.MessagePosted,
                DiscussionAuditEvents.PostRejected, DiscussionAuditEvents.RateLimited, DiscussionAuditEvents.ParticipantLeft, DiscussionAuditEvents.ParticipantRejoined,
                DiscussionAuditEvents.SessionClosed,
            },
            events);
        Assert.Equal(rig.Hub.Epoch, rig.Hub.Status(auditTail: 100).Audit[0].Detail);
    }

    [Fact]
    public void AttachCommitRefusesUnpairedSurrogate()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string token, _) = rig.Join(id, Alice);

        // Build an unpaired surrogate at run time
        string unpaired = "bad: " + new string(new[] { '\uD800' });
        DiscussionException ex = HubRig.Refused(() => rig.Hub.AttachCommit(id, token, "registered_tool", "{}", unpaired, "note"));

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("result", ex.Parameter);
    }

    [Fact]
    public void DuplicateClientRequestIdRefreshesFloorInRoundRobin()
    {
        using var rig = new HubRig(s => { s.FloorTimeoutSeconds = 30; s.MaxParticipantsPerSession = 4; });
        string id = rig.Hub.Create("title", null, DiscussionTurnMode.RoundRobin).Session.Id;
        (string alice, _) = rig.Join(id, Alice);
        (string bob, _) = rig.Join(id, Bob);

        // Alice posts, passing floor to Bob with 30s
        DiscussionPostResult p1 = rig.Post(id, alice, "first", "req-1");
        Assert.NotNull(p1.Floor);
        Assert.Equal("p2", p1.Floor.Handle);
        Assert.Equal(30, p1.Floor.SecondsLeft);

        // Advance time by 10s
        rig.Time.Advance(10);

        // Alice retries same req-1
        DiscussionPostResult retry = rig.Post(id, alice, "first", "req-1");
        Assert.True(retry.Duplicate);
        Assert.Equal(p1.Sequence, retry.Sequence);
        Assert.NotNull(retry.Floor);
        Assert.Equal("p2", retry.Floor.Handle);
        Assert.Equal(20, retry.Floor.SecondsLeft);
    }

    [Fact]
    public async Task ReadAsyncLimitsWaitersAndSupersedesSameParticipant()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        (string alice, _) = rig.Join(id, Alice);

        // Start waiting read for Alice (after sequence 1 = join event)
        using var cts1 = new CancellationTokenSource();
        Task<DiscussionReadResult> read1 = rig.Hub.ReadAsync(id, alice, afterSequence: 1, waitSeconds: 5, cancellationToken: cts1.Token);

        // Alice reads again with waitSeconds: 5 -> supersedes read1 immediately
        using var cts2 = new CancellationTokenSource();
        Task<DiscussionReadResult> read2 = rig.Hub.ReadAsync(id, alice, afterSequence: 1, waitSeconds: 5, cancellationToken: cts2.Token);

        DiscussionReadResult res1 = await read1;
        Assert.Empty(res1.Messages);
        Assert.False(res1.TimedOut);

        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read2);
    }

    [Fact]
    public void ClosedSessionsAreBoundedByRetentionMultiplier()
    {
        // MaxActiveSessions = 2, ClosedSessionRetentionMultiplier = 4 -> max closed = 8
        using var rig = new HubRig(s => { s.MaxActiveSessions = 2; s.ClosedRetentionSeconds = 3600; });

        for (int i = 0; i < 15; i++)
        {
            string id = rig.NewSession();
            (string token, _) = rig.Join(id, Alice);
            rig.Hub.Close(id, token);
        }

        // Only at most 8 closed sessions should remain
        var status = rig.Hub.Status();
        Assert.InRange(status.Sessions.Count, 1, 8);
    }
}
