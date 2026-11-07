#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T2, hub side of spec 5.10: the precheck, the commit (verbatim result, hash, reuse, limits, re-validation) and the read of a snapshot.</summary>
public sealed class DiscussionAnalysisStoreTests
{
    private const string Tool = "sa_calculate_indicators";
    private const string Arguments = """{"indicators":["RSI"],"symbol":"7203.T"}""";

    // A price-like number far beyond double precision: it must come back exactly as it went in.
    private const string Result = """{"close":12345678901234567890.123456789,"name":"日本","rsi":[55.5,56.25]}""";

    private static string Sha256Of(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static (HubRig Rig, string Id, string Alice, string Bob) Start(Action<DiscussionSettings>? configure = null)
    {
        var rig = new HubRig(configure);
        string id = rig.NewSession();
        string alice = rig.Join(id, "Alice").Token;
        string bob = rig.Join(id, "Bob").Token;
        return (rig, id, alice, bob);
    }

    [Fact]
    public void ASnapshotIsStoredVerbatimWithItsHashAndSharedWithTheOtherParticipant()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            rig.Hub.AttachPrecheck(id, alice);
            DiscussionAttachResult attached = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, "daily RSI");
            DiscussionAnalysisResult seen = rig.Hub.GetAnalysis(id, bob, attached.ContextId);

            Assert.Equal(1, attached.ContextId);
            Assert.False(attached.Reused);
            Assert.Equal(Sha256Of(Result), attached.Sha256);
            Assert.Equal(Encoding.UTF8.GetByteCount(Result), attached.ResultBytes);
            Assert.Equal(Result, seen.ResultJson);
            Assert.Contains("12345678901234567890.123456789", seen.ResultJson, StringComparison.Ordinal);
            Assert.Equal((Tool, Arguments, "p1", "daily RSI"), (seen.Tool, seen.ArgumentsJson, seen.AttachedByHandle, seen.Note));
            Assert.Equal(attached.Sha256, seen.Sha256);
            Assert.Equal(attached.ResultBytes, seen.ResultBytes);
            Assert.Equal(rig.Time.GetUtcNow(), seen.CreatedUtc);
        }
    }

    [Fact]
    public void AnAnalysisMessageNamesTheContextAndCarriesTheNoteAndTheServerOrigin()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            DiscussionAttachResult plain = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            DiscussionAttachResult noted = rig.Hub.AttachCommit(id, bob, Tool, """{"symbol":"6758.T"}""", "{}", "second look");
            var transcript = rig.Transcript(id, alice);

            Assert.Equal(3, plain.Sequence);
            Assert.Equal(4, noted.Sequence);
            Assert.Equal("p1 attached context 1 (sa_calculate_indicators)", transcript[2].Content);
            Assert.Equal("p2 attached context 2 (sa_calculate_indicators): second look", transcript[3].Content);
            Assert.All(transcript.Skip(2), m => Assert.Equal(DiscussionMessageKind.Analysis, m.Kind));
            Assert.Equal(new long[] { 2 }, transcript[3].ContextIds);
            Assert.Equal("server", DiscussionNames.OriginOf(transcript[2].Kind));
        }
    }

    [Fact]
    public void TheSameToolArgumentsAndResultAreReusedWithoutANewContextOrMessage()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            DiscussionAttachResult first = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            long messages = rig.Hub.Status(id).Sessions[0].MessageCount;
            long bytes = rig.Hub.GetDiagnostics().TotalBytes;

            DiscussionAttachResult again = rig.Hub.AttachCommit(id, bob, Tool, Arguments, Result, "ignored note");

            Assert.True(again.Reused);
            Assert.Equal(first.ContextId, again.ContextId);
            Assert.Equal(first.Sequence, again.Sequence);
            Assert.Equal(first.Sha256, again.Sha256);
            Assert.Equal(messages, rig.Hub.Status(id).Sessions[0].MessageCount);
            Assert.Equal(1, rig.Hub.Status(id).Sessions[0].ContextCount);
            Assert.Equal(bytes, rig.Hub.GetDiagnostics().TotalBytes);
        }
    }

    [Fact]
    public void AChangedResultADifferentArgumentOrADifferentToolIsNeverReused()
    {
        (HubRig rig, string id, string alice, _) = Start();
        using (rig)
        {
            rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);

            DiscussionAttachResult newData = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result.Replace("55.5", "57.5", StringComparison.Ordinal), null);
            DiscussionAttachResult otherArguments = rig.Hub.AttachCommit(id, alice, Tool, """{"indicators":["MACD"],"symbol":"7203.T"}""", Result, null);
            DiscussionAttachResult otherTool = rig.Hub.AttachCommit(id, alice, "sa_get_candles", Arguments, Result, null);

            Assert.All(new[] { newData, otherArguments, otherTool }, r => Assert.False(r.Reused));
            Assert.Equal(new long[] { 2, 3, 4 }, new[] { newData.ContextId, otherArguments.ContextId, otherTool.ContextId });
        }
    }

    [Fact]
    public void ReuseStillWorksWhenTheContextLimitIsReachedButANewSnapshotIsRefused()
    {
        (HubRig rig, string id, string alice, _) = Start(s => s.MaxContextsPerSession = 1);
        using (rig)
        {
            rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);

            DiscussionAttachResult reused = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            DiscussionException precheck = HubRig.Refused(() => rig.Hub.AttachPrecheck(id, alice));
            DiscussionException fresh = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, """{"symbol":"x"}""", Result, null));

            Assert.True(reused.Reused);
            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "contexts"), (precheck.Code, precheck.Parameter));
            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "contexts"), (fresh.Code, fresh.Parameter));
        }
    }

    [Fact]
    public void AResultOverTheContextSizeIsRefusedNeverCutAndTheEdgeIsAccepted()
    {
        (HubRig rig, string id, string alice, _) = Start(s => s.MaxContextBytes = 1024);
        using (rig)
        {
            string edge = new('r', 1024);
            string over = new('r', 1025);
            string wide = new('あ', 342); // 342 x 3 bytes = 1026 bytes in 342 characters

            DiscussionAttachResult stored = rig.Hub.AttachCommit(id, alice, Tool, Arguments, edge, null);
            DiscussionException big = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, Arguments, over, null));
            DiscussionException bytes = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, Arguments, wide, null));

            Assert.Equal(1024, stored.ResultBytes);
            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "result"), (big.Code, big.Parameter));
            Assert.Equal("request a narrower window or fewer items", big.Hint);
            Assert.Equal("result", bytes.Parameter);
            Assert.Equal(1, rig.Hub.Status(id).Sessions[0].ContextCount);
        }
    }

    [Fact]
    public void ArgumentsAndNoteAndToolAreChecked()
    {
        (HubRig rig, string id, string alice, _) = Start();
        using (rig)
        {
            string edgeArguments = "{\"k\":\"" + new string('a', 4096 - 8) + "\"}"; // exactly 4096 bytes
            rig.Hub.AttachCommit(id, alice, Tool, edgeArguments, "{}", new string('n', 500));

            Assert.Equal(4096, Encoding.UTF8.GetByteCount(edgeArguments));
            Assert.Equal("arguments", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, edgeArguments + " ", "{}", null)).Parameter);
            DiscussionException longNote = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, "{}", "{}", new string('n', 501)));
            Assert.Equal(("note", DiscussionErrorCodes.LimitExceeded), (longNote.Parameter, longNote.Code));
            Assert.Equal("note", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, "{}", "{}", "bell\a")).Parameter);
            rig.Hub.AttachCommit(id, alice, Tool, "{\"n\":1}", "{}", "multi\nline");
            Assert.Equal("tool", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, "", "{}", "{}", null)).Parameter);
            Assert.Equal("tool", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, new string('t', 65), "{}", "{}", null)).Parameter);
            Assert.Equal("arguments", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, null, "{}", null)).Parameter);
            Assert.Equal("result", HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, "{}", null, null)).Parameter);
        }
    }

    [Fact]
    public void TheCommitChecksTheStateAgainBecauseTheSessionMayHaveClosedWhileTheToolRan()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            rig.Hub.AttachPrecheck(id, alice); // the expensive tool starts here ...
            rig.Hub.Close(id, bob); // ... and the discussion is closed meanwhile
            long bytes = rig.Hub.GetDiagnostics().TotalBytes;

            DiscussionException error = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null));

            Assert.Equal(DiscussionErrorCodes.DiscussionClosed, error.Code);
            Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
            Assert.Equal(bytes, rig.Hub.GetDiagnostics().TotalBytes);
        }
    }

    [Fact]
    public void ALeftParticipantAndAForeignTokenCannotAttachButMayReadASnapshot()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            DiscussionAttachResult attached = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            rig.Hub.Leave(id, bob);

            Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Hub.AttachCommit(id, bob, Tool, "{}", "{}", null)));
            Assert.Equal(DiscussionErrorCodes.ParticipantNotJoined, HubRig.CodeOf(() => rig.Hub.AttachCommit(id, "nonsense", Tool, "{}", "{}", null)));
            Assert.Equal(Result, rig.Hub.GetAnalysis(id, bob, attached.ContextId).ResultJson);
        }
    }

    [Fact]
    public void ASnapshotCanBeReadAfterTheDiscussionClosedButNotAfterItWasRemoved()
    {
        (HubRig rig, string id, string alice, _) = Start();
        using (rig)
        {
            DiscussionAttachResult attached = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            rig.Hub.Close(id, alice);

            Assert.Equal(Result, rig.Hub.GetAnalysis(id, alice, attached.ContextId).ResultJson);
            rig.Time.Advance(rig.Settings.ClosedRetentionSeconds);
            Assert.Equal(DiscussionErrorCodes.DiscussionNotFound, HubRig.CodeOf(() => rig.Hub.GetAnalysis(id, alice, attached.ContextId)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void AnUnknownContextIdIsInvalid(long contextId)
    {
        (HubRig rig, string id, string alice, _) = Start();
        using (rig)
        {
            rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);

            DiscussionException error = HubRig.Refused(() => rig.Hub.GetAnalysis(id, alice, contextId));

            Assert.Equal((DiscussionErrorCodes.InvalidArgument, "contextId"), (error.Code, error.Parameter));
        }
    }

    [Fact]
    public void TheMessageLimitAndTheMemoryBudgetApplyToSnapshotsToo()
    {
        (HubRig rig, string id, string alice, _) = Start(s => { s.MaxMessagesPerSession = 10; s.MaxPostsPerMinute = 120; });
        using (rig)
        {
            for (int i = 0; i < 8; i++)
            {
                rig.Post(id, alice, "m" + i); // 2 joins + 8 = 10: full
            }

            DiscussionException full = HubRig.Refused(() => rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null));

            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "messages"), (full.Code, full.Parameter));
            Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
        }

        (HubRig small, string id2, string token, _) = Start(s => { s.MaxTotalBytes = 1048576; s.MaxContextBytes = 524288; s.MaxContextsPerSession = 5; });
        using (small)
        {
            string half = "\"" + new string('h', 524000) + "\"";
            small.Hub.AttachCommit(id2, token, Tool, """{"n":1}""", half, null);
            small.Hub.AttachCommit(id2, token, Tool, """{"n":2}""", half.Replace('h', 'i'), null);

            DiscussionException budget = HubRig.Refused(() => small.Hub.AttachCommit(id2, token, Tool, """{"n":3}""", half.Replace('h', 'j'), null));

            Assert.Equal((DiscussionErrorCodes.LimitExceeded, "memoryBudget"), (budget.Code, budget.Parameter));
            DiscussionDiagnostics diagnostics = small.Hub.GetDiagnostics();
            Assert.Equal(diagnostics.RecomputedBytes, diagnostics.TotalBytes);
        }
    }

    [Fact]
    public void ASnapshotCountsEverythingItStoresInTheByteTotalAndReleasesItAllOnPurge()
    {
        (HubRig rig, string id, string alice, _) = Start();
        using (rig)
        {
            rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, "note");
            DiscussionDiagnostics stored = rig.Hub.GetDiagnostics();

            long expected = Encoding.UTF8.GetByteCount("p1 (Alice) joined") + Encoding.UTF8.GetByteCount("p2 (Bob) joined")
                + Encoding.UTF8.GetByteCount("p1 attached context 1 (sa_calculate_indicators): note")
                + Encoding.UTF8.GetByteCount(Result) + Encoding.UTF8.GetByteCount(Arguments) + Encoding.UTF8.GetByteCount("note");
            Assert.Equal(expected, stored.TotalBytes);
            Assert.Equal(expected, stored.RecomputedBytes);

            rig.Hub.Close(id, alice);
            rig.Time.Advance(rig.Settings.ClosedRetentionSeconds);
            rig.Hub.Sweep();
            Assert.Equal(new DiscussionDiagnostics(0, 0, 0, 0), rig.Hub.GetDiagnostics());
        }
    }

    [Fact]
    public void ASnapshotCanBeReferencedByAPostAndTheNumberReturnsUnchanged()
    {
        (HubRig rig, string id, string alice, string bob) = Start();
        using (rig)
        {
            DiscussionAttachResult attached = rig.Hub.AttachCommit(id, alice, Tool, Arguments, Result, null);
            rig.Hub.Post(id, bob, new DiscussionPostRequest("I checked it", ContextIds: new[] { attached.ContextId }));

            DiscussionReadMessage post = rig.Transcript(id, alice).Last();
            DiscussionAnalysisResult context = rig.Hub.GetAnalysis(id, alice, post.ContextIds.Single());

            Assert.Equal(attached.Sha256, context.Sha256);
            Assert.Equal(Result, context.ResultJson);
        }
    }
}
