#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T8 of the discussion board: the round-robin floor of spec 5.5 (rotation, timeout, pass, leave of the holder, a lone participant, no effect on attach/read/status).</summary>
public sealed class DiscussionFloorTests
{
    private static (HubRig Rig, string Id) Start(Action<DiscussionSettings>? configure = null)
    {
        var rig = new HubRig(configure);
        string id = rig.Hub.Create("round table", null, DiscussionTurnMode.RoundRobin).Session.Id;
        return (rig, id);
    }

    private static string FloorHolder(HubRig rig, string id) => rig.Hub.Status(id).Sessions[0].Floor?.Handle ?? "(none)";

    private static DiscussionPostResult Pass(HubRig rig, string id, string token, string? requestId = null)
        => rig.Hub.Post(id, token, new DiscussionPostRequest(null, Pass: true, ClientRequestId: requestId));

    // ---- The floor exists only in a round-robin discussion -----------------------------------------------------------------------------

    [Fact]
    public void ARoundRobinDiscussionStartsWithoutAFloorAndTheFirstParticipantTakesIt()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            Assert.Equal(DiscussionTurnMode.RoundRobin, rig.Hub.Status(id).Sessions[0].TurnMode);
            Assert.Null(rig.Hub.Status(id).Sessions[0].Floor);

            DiscussionJoinResult alice = rig.Hub.Join(id, "Alice");
            DiscussionJoinResult bob = rig.Hub.Join(id, "Bob");

            Assert.Equal(new DiscussionFloorInfo("p1", 60), alice.Session.Floor);
            Assert.Equal(new DiscussionFloorInfo("p1", 60), bob.Session.Floor);
        }
    }

    [Fact]
    public void AFreeDiscussionNeverReportsAFloor()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        string token = rig.Join(id, "Alice").Token;

        DiscussionPostResult posted = rig.Post(id, token, "hello");

        Assert.Null(rig.Hub.Status(id).Sessions[0].Floor);
        Assert.Null(posted.Floor);
    }

    // ---- Rotation -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFloorRotatesInJoinOrderAndWrapsRound()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            string c = rig.Join(id, "Carol").Token;

            DiscussionPostResult first = rig.Post(id, a, "one");
            DiscussionPostResult second = rig.Post(id, b, "two");
            DiscussionPostResult third = rig.Post(id, c, "three");
            DiscussionPostResult fourth = rig.Post(id, a, "four");

            Assert.Equal("p2", first.Floor!.Handle);
            Assert.Equal("p3", second.Floor!.Handle);
            Assert.Equal("p1", third.Floor!.Handle);
            Assert.Equal("p2", fourth.Floor!.Handle);
            Assert.Equal(60, fourth.Floor.SecondsLeft);
        }
    }

    [Fact]
    public void ALateParticipantIsAppendedAtTheEndOfTheOrder()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            rig.Post(id, a, "one"); // floor -> Bob
            string c = rig.Join(id, "Carol").Token;

            Assert.Equal("p2", FloorHolder(rig, id));
            rig.Post(id, b, "two"); // floor -> Carol, not back to Alice
            Assert.Equal("p3", FloorHolder(rig, id));
            rig.Post(id, c, "three");
            Assert.Equal("p1", FloorHolder(rig, id));
        }
    }

    [Fact]
    public void ANonHolderIsToldWhoSpeaksNextAndHowLongTheFloorHasLeft()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;

            DiscussionException first = HubRig.Refused(() => rig.Post(id, b, "too early"));
            rig.Time.Advance(25);
            DiscussionException later = HubRig.Refused(() => rig.Post(id, b, "still early"));
            rig.Time.Advance(34.5);
            DiscussionException last = HubRig.Refused(() => rig.Post(id, b, "almost"));

            Assert.Equal((DiscussionErrorCodes.NotYourTurn, "floor"), (first.Code, first.Parameter));
            Assert.Equal("p1 (Alice) speaks next; 60 s until the floor passes", first.Hint);
            Assert.Equal("p1 (Alice) speaks next; 35 s until the floor passes", later.Hint);
            Assert.Equal("p1 (Alice) speaks next; 1 s until the floor passes", last.Hint);
            Assert.Equal("p1", FloorHolder(rig, id));
            rig.Post(id, a, "my turn");
        }
    }

    // ---- Timeout ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenTheHolderIsSilentForTheTimeoutTheFloorPassesOneStepAndTheNextSpeakerSucceeds()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;

            rig.Time.Advance(59);
            Assert.Equal("p1", FloorHolder(rig, id));
            rig.Time.Advance(1);
            DiscussionPostResult posted = rig.Post(id, b, "Alice is silent");

            Assert.Contains("floor passed from p1 to p2 (timeout)", rig.Transcript(id, a).Select(m => m.Content));
            Assert.Equal("p1", posted.Floor!.Handle); // Bob spoke, so the floor wraps back to Alice
        }
    }

    [Fact]
    public void TheHolderWhoseTimeRanOutLosesTheTurnAndIsToldWhoHasItNow()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");
            rig.Time.Advance(60);

            DiscussionException error = HubRig.Refused(() => rig.Post(id, a, "too late"));

            Assert.Equal(DiscussionErrorCodes.NotYourTurn, error.Code);
            Assert.Equal("p2 (Bob) speaks next; 60 s until the floor passes", error.Hint);
            Assert.Equal("p2", FloorHolder(rig, id));
        }
    }

    [Fact]
    public void ALongSilenceMovesTheFloorOnlyOneStepPerEvaluation()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");
            rig.Join(id, "Carol");
            rig.Time.Advance(500);

            DiscussionException first = HubRig.Refused(() => rig.Post(id, a, "x"));
            Assert.Equal("p2", FloorHolder(rig, id));
            rig.Time.Advance(60);
            HubRig.Refused(() => rig.Post(id, a, "y"));
            Assert.Equal("p3", FloorHolder(rig, id)); // two evaluations, two steps

            Assert.Contains("p2 (Bob)", first.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AReadAndAStatusEvaluateTheTimeoutToo()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");

            rig.Time.Advance(60);
            DiscussionReadResult read = await rig.Hub.ReadAsync(id, a);
            Assert.Equal("p2", read.Session.Floor!.Handle);
            Assert.Equal("floor passed from p1 to p2 (timeout)", read.Messages.Last().Content);

            rig.Time.Advance(60);
            DiscussionSessionHeader status = rig.Hub.Status(id).Sessions[0];
            Assert.Equal("p1", status.Floor!.Handle);
            Assert.Equal(60, status.Floor.SecondsLeft);
            Assert.Equal(4, rig.Hub.Status(id).Sessions[0].MessageCount); // 2 joins + 2 timeout events
        }
    }

    [Fact]
    public void ALoneParticipantKeepsTheFloorAndNoEventIsWrittenWhenItsTimeRunsOut()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;

            rig.Time.Advance(300);
            DiscussionPostResult posted = rig.Post(id, a, "I am alone");
            rig.Time.Advance(300);
            DiscussionSessionHeader status = rig.Hub.Status(id).Sessions[0];

            Assert.Equal("p1", posted.Floor!.Handle);
            Assert.Equal(60, posted.Floor.SecondsLeft);
            Assert.Equal("p1", status.Floor!.Handle);
            Assert.Equal(2, status.MessageCount); // the join event and the post
            Assert.Equal(0, status.Floor.SecondsLeft);
        }
    }

    // ---- Leave --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenTheHolderLeavesTheFloorMovesOnAndItsClockRestarts()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");
            rig.Join(id, "Carol");
            rig.Time.Advance(40);

            DiscussionLeaveResult left = rig.Hub.Leave(id, a);

            Assert.Equal(new DiscussionFloorInfo("p2", 60), left.Session.Floor);
        }
    }

    [Fact]
    public void ALeftParticipantIsSkippedInTheOrderAndTheLastLeaverEmptiesTheFloor()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            string c = rig.Join(id, "Carol").Token;
            rig.Hub.Leave(id, b); // Bob does not hold the floor: nothing moves
            Assert.Equal("p1", FloorHolder(rig, id));

            rig.Post(id, a, "one");
            Assert.Equal("p3", FloorHolder(rig, id)); // Bob is skipped
            rig.Hub.Leave(id, c);
            Assert.Equal("p1", FloorHolder(rig, id));
            rig.Hub.Leave(id, a);

            Assert.Equal("(none)", FloorHolder(rig, id));
            DiscussionJoinResult dave = rig.Hub.Join(id, "Dave");
            Assert.Equal("p4", dave.Session.Floor!.Handle);
        }
    }

    [Fact]
    public void ARejoiningParticipantKeepsItsPlaceInTheOrder()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            rig.Join(id, "Carol");
            rig.Hub.Leave(id, b);
            string back = rig.Hub.Join(id, "Bob").ParticipantToken;

            rig.Post(id, a, "one");
            Assert.Equal("p2", FloorHolder(rig, id));
            rig.Post(id, back, "Bob is back");
            Assert.Equal("p3", FloorHolder(rig, id));
        }
    }

    // ---- Pass ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APassWritesAnEventMovesTheFloorAndDoesNotCountTowardTheRate()
    {
        (HubRig rig, string id) = Start(s => s.MaxPostsPerMinute = 1);
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;

            DiscussionPostResult passed = Pass(rig, id, a);
            Assert.Equal("p2", passed.Floor!.Handle);
            rig.Post(id, b, "Bob speaks");
            DiscussionPostResult spoke = rig.Post(id, a, "Alice may still speak: the pass was not counted");

            Assert.Equal("p1 (Alice) passed", rig.Transcript(id, a).First(m => m.Content.EndsWith("passed", StringComparison.Ordinal)).Content);
            Assert.True(spoke.Sequence > passed.Sequence);
            Assert.Equal(DiscussionMessageKind.Event, rig.Transcript(id, a).Single(m => m.Sequence == passed.Sequence).Kind);
        }
    }

    [Fact]
    public void APassByANonHolderIsRefusedAndAPassWithContentIsInvalid()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;

            Assert.Equal(DiscussionErrorCodes.NotYourTurn, HubRig.CodeOf(() => Pass(rig, id, b)));
            DiscussionException withContent = HubRig.Refused(() => rig.Hub.Post(id, a, new DiscussionPostRequest("text", Pass: true)));
            DiscussionException withReply = HubRig.Refused(() => rig.Hub.Post(id, a, new DiscussionPostRequest(null, Pass: true, ReplyToSequence: 1)));

            Assert.Equal((DiscussionErrorCodes.InvalidArgument, "pass"), (withContent.Code, withContent.Parameter));
            Assert.Equal("pass", withReply.Parameter);
            Assert.Equal("p1", FloorHolder(rig, id));
        }
    }

    [Fact]
    public void APassConsumesOneMessageOfTheLimitAndIsRefusedWhenTheSessionIsFull()
    {
        (HubRig rig, string id) = Start(s => { s.MaxMessagesPerSession = 10; s.MaxPostsPerMinute = 120; s.FloorTimeoutSeconds = 600; });
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            for (int i = 0; i < 4; i++)
            {
                rig.Post(id, a, "a" + i);
                rig.Post(id, b, "b" + i);
            }

            Assert.Equal(10, rig.Hub.Status(id).Sessions[0].MessageCount);
            DiscussionException full = HubRig.Refused(() => Pass(rig, id, a));

            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "messages"), (full.Code, full.Parameter));
        }
    }

    [Fact]
    public void AnIdempotentPassIsAnsweredFromMemoryBeforeTheTurnIsLookedAt()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");
            DiscussionPostResult first = Pass(rig, id, a, "pass-1");

            DiscussionPostResult again = Pass(rig, id, a, "pass-1"); // it is no longer Alice's turn, yet the stored answer returns

            Assert.True(again.Duplicate);
            Assert.Equal(first.Sequence, again.Sequence);
        }
    }

    // ---- What the floor does not touch --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AttachReadAndStatusNeverRequireOrConsumeTheFloor()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token; // Alice holds the floor

            rig.Hub.AttachPrecheck(id, b);
            rig.Hub.AttachCommit(id, b, "sa_x", "{}", "{}", null);
            await rig.Hub.ReadAsync(id, b);
            rig.Hub.Status(id);
            rig.Hub.GetAnalysis(id, b, 1);

            Assert.Equal(new DiscussionFloorInfo("p1", 60), rig.Hub.Status(id).Sessions[0].Floor);
            Assert.NotNull(a);
        }
    }

    [Fact]
    public void ARefusedTurnDoesNotUseUpTheRateWindowOrTheMessageLimit()
    {
        (HubRig rig, string id) = Start(s => s.MaxPostsPerMinute = 1);
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            string b = rig.Join(id, "Bob").Token;
            long before = rig.Hub.Status(id).Sessions[0].MessageCount;

            HubRig.Refused(() => rig.Post(id, b, "not yet"));
            HubRig.Refused(() => rig.Post(id, b, "not yet either"));
            rig.Post(id, a, "go");
            DiscussionPostResult bobs = rig.Post(id, b, "now Bob can post once");

            Assert.Equal(before + 2, bobs.Sequence);
        }
    }

    [Fact]
    public void ACloseEndsTheFloorAndAClosedDiscussionHasNoTimeoutEvent()
    {
        (HubRig rig, string id) = Start();
        using (rig)
        {
            string a = rig.Join(id, "Alice").Token;
            rig.Join(id, "Bob");
            rig.Hub.Close(id, a);
            long count = rig.Hub.Status(id).Sessions[0].MessageCount;

            rig.Time.Advance(120);

            Assert.Equal(count, rig.Hub.Status(id).Sessions[0].MessageCount);
        }
    }
}
