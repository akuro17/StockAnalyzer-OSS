#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// Task T7 with real named pipes: the election of the hub, the sharing of one board, the loss of the hub and the edges of the wire. Every test uses a private pipe name
/// (a random suffix), so none of them can meet the board of a running server.
/// </summary>
public sealed class DiscussionHubElectionTests : IDisposable
{
    private static readonly string DataFolder = Path.Combine(Path.GetTempPath(), "dhub-election-data");

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..12];

    private DiscussionGatewayRouter NewRouter(string suffix, string? dataFolder = null, bool sharing = true, ICommandLog? commandLog = null, DiscussionSettings? settings = null)
    {
        var router = new DiscussionGatewayRouter(
            settings ?? new DiscussionSettings(), TimeProvider.System, commandLog, protocolSuffix: null, dataFolder ?? DataFolder, new DiscussionTransportOptions(suffix, sharing));
        _disposables.Add(router);
        return router;
    }

    private static Task<DiscussionReply> Ask(IDiscussionGateway gateway, string operation, string arguments = "{}", CancellationToken cancellationToken = default)
        => gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), cancellationToken);

    private static async Task<JsonDocument> Ok(IDiscussionGateway gateway, string operation, string arguments = "{}")
    {
        DiscussionReply reply = await Ask(gateway, operation, arguments);
        Assert.True(reply.IsOk, reply.Failure?.Code + " " + reply.Failure?.Message);
        return JsonDocument.Parse(reply.ResultJson!);
    }

    private static string Code(DiscussionReply reply) => Assert.IsType<DiscussionFailure>(reply.Failure).Code;

    // ---- The race ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EightRoutersStartedAtTheSameInstantElectExactlyOneHubAndShareOneBoard_FiftyTimes()
    {
        for (int round = 0; round < 50; round++)
        {
            string suffix = NewSuffix();
            var routers = Enumerable.Range(0, 8).Select(_ => NewRouter(suffix, settings: new DiscussionSettings { MaxParticipantsPerSession = 8 })).ToArray();
            using var gate = new ManualResetEventSlim(false);

            // Every router starts with the same first operation at the same instant: the election runs inside it.
            Task<DiscussionReply>[] first = routers.Select(r => Task.Run(() =>
            {
                gate.Wait();
                return Ask(r, DiscussionOperations.Status);
            })).ToArray();
            gate.Set();
            DiscussionReply[] replies = await Task.WhenAll(first);

            Assert.All(replies, reply => Assert.True(reply.IsOk, reply.Failure?.Code + " " + reply.Failure?.Message));
            Assert.Equal(1, routers.Count(r => r.Role == "hub"));
            Assert.Equal(7, routers.Count(r => r.Role == "remote"));

            // All eight see one board: one creates, everyone joins and posts, and each one reads what all the others wrote.
            using JsonDocument created = await Ok(routers[0], DiscussionOperations.Create, """{"title":"race"}""");
            string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
            var tokens = new string[8];
            for (int i = 0; i < 8; i++)
            {
                using JsonDocument joined = await Ok(routers[i], DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"agent {{i}}"}""");
                tokens[i] = joined.RootElement.GetProperty("participantToken").GetString()!;
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                using JsonDocument posted = await Ok(routers[i], DiscussionOperations.Post, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{tokens[i]}}","content":"from {{i}}"}""");
            }));

            for (int reader = 0; reader < 8; reader += 3)
            {
                using JsonDocument read = await Ok(routers[reader], DiscussionOperations.Read, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{tokens[reader]}}","afterSequence":0,"limit":50}""");
                string[] contents = read.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(m => m.GetProperty("content").GetString()!).Where(c => c.StartsWith("from ", StringComparison.Ordinal)).OrderBy(c => c, StringComparer.Ordinal).ToArray();
                Assert.Equal(Enumerable.Range(0, 8).Select(i => $"from {i}").ToArray(), contents);
            }

            foreach (DiscussionGatewayRouter router in routers)
            {
                router.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheStatusOfEveryRoleShowsTheSharingKeyAndTheCommandsOfItsOwnProcess()
    {
        string suffix = NewSuffix();
        var hubLog = new CommandLog(new DiscussionSettings(), TimeProvider.System, Path.Combine(Path.GetTempPath(), "dhub-election-log-" + suffix));
        var remoteLog = new CommandLog(new DiscussionSettings(), TimeProvider.System, Path.Combine(Path.GetTempPath(), "dhub-election-log2-" + suffix));
        _disposables.Add(hubLog);
        _disposables.Add(remoteLog);
        hubLog.Record("sa_discussion_create", new[] { "title" }, 20, "ok", 1, "1");
        remoteLog.Record("sa_discussion_status", Array.Empty<string>(), 2, "ok", 1, "9");
        DiscussionGatewayRouter hub = NewRouter(suffix, commandLog: hubLog);
        DiscussionGatewayRouter remote = NewRouter(suffix, commandLog: remoteLog);

        using JsonDocument hubStatus = await Ok(hub, DiscussionOperations.Status, """{"commandTail":10}""");
        using JsonDocument remoteStatus = await Ok(remote, DiscussionOperations.Status, """{"commandTail":10}""");

        Assert.Equal("hub", hubStatus.RootElement.GetProperty("role").GetString());
        Assert.Equal("remote", remoteStatus.RootElement.GetProperty("role").GetString());
        Assert.True(hubStatus.RootElement.GetProperty("sharing").GetBoolean());
        Assert.True(remoteStatus.RootElement.GetProperty("sharing").GetBoolean());
        string key = DiscussionSharingKey.DataHash8(DataFolder);
        Assert.Equal(key, hubStatus.RootElement.GetProperty("sharingKey").GetString());
        Assert.Equal(key, remoteStatus.RootElement.GetProperty("sharingKey").GetString());
        Assert.Equal(hubStatus.RootElement.GetProperty("hubEpoch").GetString(), remoteStatus.RootElement.GetProperty("hubEpoch").GetString());
        Assert.Equal("sa_discussion_create", Assert.Single(hubStatus.RootElement.GetProperty("commands").EnumerateArray()).GetProperty("tool").GetString());
        Assert.Equal("sa_discussion_status", Assert.Single(remoteStatus.RootElement.GetProperty("commands").EnumerateArray()).GetProperty("tool").GetString());
    }

    [Fact]
    public async Task ABadCommandTailIsRefusedByTheBoardWhateverTheRole()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter hub = NewRouter(suffix);
        DiscussionGatewayRouter remote = NewRouter(suffix);
        await Ok(hub, DiscussionOperations.Status);
        await Ok(remote, DiscussionOperations.Status);

        Assert.Equal(DiscussionErrorCodes.LimitExceeded, Code(await Ask(hub, DiscussionOperations.Status, """{"commandTail":101}""")));
        Assert.Equal(DiscussionErrorCodes.LimitExceeded, Code(await Ask(remote, DiscussionOperations.Status, """{"commandTail":101}""")));
        Assert.Equal(DiscussionErrorCodes.InvalidArgument, Code(await Ask(remote, DiscussionOperations.Status, """{"commandTail":"x"}""")));
    }

    // ---- Where the board is not shared ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutSharingTheBoardIsLocalNoPipeIsCreatedAndTheStatusSaysSo()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter first = NewRouter(suffix, sharing: false);
        DiscussionGatewayRouter second = NewRouter(suffix, sharing: false);

        using JsonDocument created = await Ok(first, DiscussionOperations.Create, """{"title":"mine"}""");
        using JsonDocument status = await Ok(first, DiscussionOperations.Status);
        using JsonDocument other = await Ok(second, DiscussionOperations.Status);

        Assert.Equal("hub", status.RootElement.GetProperty("role").GetString());
        Assert.False(status.RootElement.GetProperty("sharing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("sharingKey").ValueKind);
        Assert.Single(status.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Empty(other.RootElement.GetProperty("sessions").EnumerateArray()); // a separate board
        Assert.Null(first.PipeName);
        Assert.False(await DiscussionPipeClient.ProbeAsync(DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, suffix), CancellationToken.None));
    }

    [Fact]
    public async Task ServersWithDifferentDataFoldersDoNotMeet()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter one = NewRouter(suffix, Path.Combine(DataFolder, "one"));
        DiscussionGatewayRouter two = NewRouter(suffix, Path.Combine(DataFolder, "two"));

        using JsonDocument created = await Ok(one, DiscussionOperations.Create, """{"title":"only in one"}""");
        using JsonDocument status = await Ok(two, DiscussionOperations.Status);

        Assert.Equal("hub", one.Role);
        Assert.Equal("hub", two.Role);
        Assert.Empty(status.RootElement.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task TwoSpellingsOfOneFolderMeet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string suffix = NewSuffix();
        DiscussionGatewayRouter one = NewRouter(suffix, DataFolder);
        DiscussionGatewayRouter two = NewRouter(suffix, DataFolder.ToUpperInvariant() + Path.DirectorySeparatorChar);

        using JsonDocument created = await Ok(one, DiscussionOperations.Create, """{"title":"shared"}""");
        using JsonDocument status = await Ok(two, DiscussionOperations.Status);

        Assert.Equal("remote", two.Role);
        Assert.Single(status.RootElement.GetProperty("sessions").EnumerateArray());
    }

    // ---- The hub goes away ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheHubIsGoneWhileThreeRoutersCallExactlyOneNewHubIsElectedAndOldTokensAreAHubReset()
    {
        for (int round = 0; round < 5; round++)
        {
            string suffix = NewSuffix();
            DiscussionGatewayRouter oldHub = NewRouter(suffix);
            using JsonDocument created = await Ok(oldHub, DiscussionOperations.Create, """{"title":"before"}""");
            string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
            var remotes = Enumerable.Range(0, 3).Select(_ => NewRouter(suffix)).ToArray();
            var tokens = new string[3];
            for (int i = 0; i < 3; i++)
            {
                using JsonDocument joined = await Ok(remotes[i], DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"agent {{i}}"}""");
                tokens[i] = joined.RootElement.GetProperty("participantToken").GetString()!;
                Assert.Equal("remote", remotes[i].Role);
            }

            oldHub.Dispose();
            using var gate = new ManualResetEventSlim(false);
            Task<DiscussionReply>[] calls = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
            {
                gate.Wait();
                return Ask(remotes[i], DiscussionOperations.Read, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{tokens[i]}}"}""");
            })).ToArray();
            gate.Set();
            DiscussionReply[] replies = await Task.WhenAll(calls);

            Assert.All(replies, reply => Assert.Equal(DiscussionErrorCodes.HubReset, Code(reply)));
            Assert.Equal(1, remotes.Count(r => r.Role == "hub"));
            Assert.Equal(2, remotes.Count(r => r.Role == "remote"));
            foreach (DiscussionGatewayRouter router in remotes)
            {
                router.Dispose();
            }
        }
    }

    [Fact]
    public async Task AReadOrStatusAfterTheHubWentAwayElectsAgainOnceAndAnswersFromTheNewBoard()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter oldHub = NewRouter(suffix);
        DiscussionGatewayRouter survivor = NewRouter(suffix);
        await Ok(oldHub, DiscussionOperations.Status); // the election is lazy: the first operation makes this one the hub
        using JsonDocument before = await Ok(survivor, DiscussionOperations.Status);
        string oldEpoch = before.RootElement.GetProperty("hubEpoch").GetString()!;
        Assert.Equal("remote", survivor.Role);

        oldHub.Dispose();
        using JsonDocument after = await Ok(survivor, DiscussionOperations.Status);

        Assert.Equal("hub", survivor.Role);
        Assert.Equal("hub", after.RootElement.GetProperty("role").GetString());
        Assert.NotEqual(oldEpoch, after.RootElement.GetProperty("hubEpoch").GetString());
        Assert.Empty(after.RootElement.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task AnOperationThatCouldBeAppliedTwiceIsNotRepeatedAfterTheLinkBroke()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter oldHub = NewRouter(suffix);
        DiscussionGatewayRouter survivor = NewRouter(suffix);
        using JsonDocument created = await Ok(oldHub, DiscussionOperations.Create, """{"title":"old board"}""");
        string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await Ok(survivor, DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"survivor"}""");
        string oldToken = joined.RootElement.GetProperty("participantToken").GetString()!;
        Assert.Equal("remote", survivor.Role);

        oldHub.Dispose();
        DiscussionReply create = await Ask(survivor, DiscussionOperations.Create, """{"title":"must not be created twice"}""");
        DiscussionReply post = await Ask(survivor, DiscussionOperations.Post, """{"sessionId":"abcdefg","participantToken":"x.y","content":"hello"}""");

        Assert.Equal(DiscussionErrorCodes.HubUnavailable, Code(create));
        Assert.Equal(DiscussionErrorCodes.HubUnavailable, Code(post));
        Assert.Equal("remote", survivor.Role); // no election was run for them

        // A post with a request id may be repeated: the new board refuses its token as the token of an older board.
        DiscussionReply repeated = await Ask(survivor, DiscussionOperations.Post, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{oldToken}}","content":"hello","clientRequestId":"r1"}""");
        Assert.Equal(DiscussionErrorCodes.HubReset, Code(repeated));
        Assert.Equal("hub", survivor.Role);
    }

    [Fact]
    public async Task AShutDownRouterAnswersHubUnavailableAndTheNameIsFreeForTheNextHub()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter hub = NewRouter(suffix);
        await Ok(hub, DiscussionOperations.Status);
        hub.Dispose();

        Assert.Equal(DiscussionErrorCodes.HubUnavailable, Code(await Ask(hub, DiscussionOperations.Status)));
        DiscussionGatewayRouter next = NewRouter(suffix);
        using JsonDocument status = await Ok(next, DiscussionOperations.Status);
        Assert.Equal("hub", next.Role);
    }

    // ---- The first instance ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASecondFirstPipeInstanceOnAnExistingNameFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, NewSuffix());
        using DiscussionPipeHost? winner = DiscussionPipeHost.TryClaim(name);
        Assert.NotNull(winner);

        // The claim helper reports the loss as null ...
        Assert.Null(DiscussionPipeHost.TryClaim(name));

        // ... and the operating system itself refuses it: this is the behaviour the election relies on.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var second = new NamedPipeServerStream(
                name, PipeDirection.InOut, DiscussionLimits.MaxHubConnections, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        });
    }

    [Fact]
    public void AnInstanceWithoutTheFirstPipeInstanceFlagWouldHaveBeenAccepted_WhichIsWhyTheFlagIsUsed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, NewSuffix());
        using DiscussionPipeHost? winner = DiscussionPipeHost.TryClaim(name);
        Assert.NotNull(winner);

        using var second = new NamedPipeServerStream(
            name, PipeDirection.InOut, DiscussionLimits.MaxHubConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Assert.NotNull(second); // two listeners on one name: without the flag two processes could both think they are the hub
    }

    // ---- The wire against a live host --------------------------------------------------------------------------------------------------------

    private sealed class ScriptedBoard : IDiscussionGateway
    {
        public Func<DiscussionRequest, CancellationToken, Task<DiscussionReply>> Handler { get; set; } = (_, _) => Task.FromResult(DiscussionReply.Ok("{}"));

        public int Calls;

        public Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Handler(request, cancellationToken);
        }
    }

    private (string Name, DiscussionPipeHost Host, ScriptedBoard Board) StartHost()
    {
        string name = DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, NewSuffix());
        DiscussionPipeHost host = DiscussionPipeHost.TryClaim(name)!;
        var board = new ScriptedBoard();
        host.Start(board);
        _disposables.Add(host);
        return (name, host, board);
    }

    private static async Task<byte[]?> RawExchangeAsync(string pipeName, byte[] request, int maxReply = 1 << 20)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await client.WriteAsync(request);
        await client.FlushAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await DiscussionWire.ReadLineAsync(client, maxReply, limit.Token);
    }

    [Fact]
    public async Task ARequestIsAnsweredWithTheResultOfTheBoardAndTheCallerLabelArrives()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        DiscussionRequest? seen = null;
        board.Handler = (request, _) =>
        {
            seen = request;
            return Task.FromResult(DiscussionReply.Ok("""{"sequence":3}"""));
        };

        DiscussionReply reply = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("post", "Antigravity", """{"content":"こんにちは 😀"}"""), CancellationToken.None);

        Assert.True(reply.IsOk);
        Assert.Equal("""{"sequence":3}""", reply.ResultJson);
        Assert.Equal("post", seen!.Operation);
        Assert.Equal("Antigravity", seen.CallerLabel);
        using JsonDocument arguments = JsonDocument.Parse(seen.ArgumentsJson);
        Assert.Equal("こんにちは 😀", arguments.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AFailureOfTheBoardArrivesWithCodeMessageParameterAndHint()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        board.Handler = (_, _) => Task.FromResult(DiscussionReply.Fail(DiscussionErrorCodes.NotYourTurn, "Wait.", "content", "Read first."));

        DiscussionReply reply = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("post", "x", "{}"), CancellationToken.None);

        Assert.Equal(new DiscussionFailure(DiscussionErrorCodes.NotYourTurn, "Wait.", "content", "Read first."), reply.Failure);
    }

    [Fact]
    public async Task AnUnknownOperationAndTheFullStatusAreRefusedOnTheWireWithoutReachingTheBoard()
    {
        (string name, _, ScriptedBoard board) = StartHost();

        DiscussionReply unknown = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("purge", "x", "{}"), CancellationToken.None);
        DiscussionReply status = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("status", "x", "{}"), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, Code(unknown));
        Assert.Equal("operation", unknown.Failure!.Parameter);
        Assert.Equal(DiscussionErrorCodes.InvalidArgument, Code(status));
        Assert.Equal(0, board.Calls);
    }

    [Fact]
    public async Task AMalformedLineAndANonObjectLineAreAnInvalidArgumentAndTheHostKeepsServing()
    {
        (string name, _, ScriptedBoard board) = StartHost();

        byte[]? malformed = await RawExchangeAsync(name, Encoding.UTF8.GetBytes("{not json\n"));
        byte[]? array = await RawExchangeAsync(name, Encoding.UTF8.GetBytes("[1,2,3]\n"));
        DiscussionReply fine = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.InvalidArgument, Code(DiscussionWire.ParseReply(malformed!)));
        Assert.Equal(DiscussionErrorCodes.InvalidArgument, Code(DiscussionWire.ParseReply(array!)));
        Assert.True(fine.IsOk);
        Assert.Equal(1, board.Calls);
    }

    [Fact]
    public async Task ARequestOfAnotherWireVersionIsAnsweredWithTheDifferentVersionHint()
    {
        (string name, _, ScriptedBoard board) = StartHost();

        byte[]? reply = await RawExchangeAsync(name, Encoding.UTF8.GetBytes("""{"wire":2,"op":"read","caller":"x","args":{}}""" + "\n"));

        DiscussionFailure failure = DiscussionWire.ParseReply(reply!).Failure!;
        Assert.Equal(DiscussionErrorCodes.HubUnavailable, failure.Code);
        Assert.Contains("restart all clients", failure.Hint, StringComparison.Ordinal);
        Assert.Equal(0, board.Calls);
    }

    [Fact]
    public async Task AReplyOfAnotherWireVersionBecomesHubUnavailableWithTheHintAtTheClient()
    {
        string name = DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, NewSuffix());
        using var impostor = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        Task serve = Task.Run(async () =>
        {
            await impostor.WaitForConnectionAsync();
            await DiscussionWire.ReadLineAsync(impostor, 1 << 20, CancellationToken.None);
            await impostor.WriteAsync(Encoding.UTF8.GetBytes("""{"wire":2,"ok":true,"result":{}}""" + "\n"));
            await impostor.FlushAsync();
            await Task.Delay(200);
        });

        var error = await Assert.ThrowsAsync<DiscussionWireException>(() => DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None));
        await serve;

        Assert.Equal(DiscussionWireProblem.VersionMismatch, error.Problem);
    }

    [Fact]
    public async Task ARequestLineOverTheLimitIsAnsweredWithLimitExceededAndTheHostKeepsServing()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        byte[] huge = new byte[DiscussionLimits.WireMaxLineBytes + 1024];
        Array.Fill(huge, (byte)'a');

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task write = Task.Run(async () =>
        {
            try
            {
                await client.WriteAsync(huge, limit.Token);
            }
            catch (IOException)
            {
                // the host stops reading once it has seen enough
            }
        });
        byte[]? reply = await DiscussionWire.ReadLineAsync(client, 1 << 20, limit.Token);
        client.Dispose();
        await write;
        DiscussionReply fine = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.LimitExceeded, Code(DiscussionWire.ParseReply(reply!)));
        Assert.True(fine.IsOk);
        Assert.Equal(1, board.Calls);
    }

    [Fact]
    public async Task ARequestTooLargeForTheLinkIsRefusedByTheClientBeforeAnythingIsSent()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        string content = new('a', DiscussionLimits.WireMaxLineBytes);

        DiscussionReply reply = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("post", "x", $$"""{"content":"{{content}}"}"""), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.LimitExceeded, Code(reply));
        Assert.Equal(0, board.Calls);
    }

    [Fact]
    public async Task AReplyOverTheLimitBecomesLimitExceededInsteadOfBreakingTheLink()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        board.Handler = (_, _) => Task.FromResult(DiscussionReply.Ok($$"""{"content":"{{new string('b', DiscussionLimits.WireMaxLineBytes)}}"}"""));

        DiscussionReply reply = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.LimitExceeded, Code(reply));
    }

    [Fact]
    public async Task APeerThatConnectsAndSaysNothingOrLeavesInTheMiddleOfALineDoesNotHarmTheHost()
    {
        (string name, _, ScriptedBoard board) = StartHost();

        for (int i = 0; i < 5; i++)
        {
            using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await silent.ConnectAsync(2000);
        }

        using (var half = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await half.ConnectAsync(2000);
            await half.WriteAsync(Encoding.UTF8.GetBytes("""{"wire":1,"op":"re"""));
            await half.FlushAsync();
        }

        DiscussionReply fine = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);

        Assert.True(fine.IsOk);
        Assert.Equal(1, board.Calls);
    }

    [Fact]
    public async Task ABoardThatThrowsIsAnInternalErrorAndTheHostKeepsServing()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        board.Handler = (_, _) => throw new InvalidOperationException("secret details");

        DiscussionReply broken = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);
        board.Handler = (_, _) => Task.FromResult(DiscussionReply.Ok("{}"));
        DiscussionReply fine = await DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);

        Assert.Equal(DiscussionErrorCodes.InternalError, Code(broken));
        Assert.DoesNotContain("secret", broken.Failure!.Message, StringComparison.Ordinal);
        Assert.True(fine.IsOk);
    }

    [Fact]
    public async Task FortyCallsAtOnceAreAllAnsweredThoughTheHubServesThirtyTwoConnections()
    {
        (string name, _, ScriptedBoard board) = StartHost();
        board.Handler = async (_, _) =>
        {
            await Task.Delay(30);
            return DiscussionReply.Ok("{}");
        };

        DiscussionReply[] replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None)));

        Assert.All(replies, reply => Assert.True(reply.IsOk, reply.Failure?.Message));
        Assert.Equal(40, board.Calls);
    }

    // ---- Every wait is bounded --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectingToANameNobodyListensOnTimesOutQuicklyInsteadOfWaitingForEver()
    {
        string name = DiscussionSharingKey.PipeName(Environment.UserName, DataFolder, NewSuffix());
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None));
        bool probe = await DiscussionPipeClient.ProbeAsync(name, CancellationToken.None);

        Assert.False(probe);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task CancellingACallThatIsWaitingForTheReplyEndsItAndTheHubDropsItsWaiter()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter hub = NewRouter(suffix);
        DiscussionGatewayRouter remote = NewRouter(suffix);
        using JsonDocument created = await Ok(hub, DiscussionOperations.Create, """{"title":"wait"}""");
        string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await Ok(remote, DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"waiter"}""");
        string token = joined.RootElement.GetProperty("participantToken").GetString()!;
        DiscussionHub board = hub.LocalHub!;
        using var cancellation = new CancellationTokenSource();

        Task<DiscussionReply> waiting = Ask(remote, DiscussionOperations.Read, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","afterSequence":99,"waitSeconds":20}""", cancellation.Token);
        await WaitUntilAsync(() => board.GetDiagnostics().WaiterCount == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await WaitUntilAsync(() => board.GetDiagnostics().WaiterCount == 0);
        Assert.Equal(0, board.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ALongPollOverThePipeIsWokenByAPostFromAnotherProcess()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter hub = NewRouter(suffix);
        DiscussionGatewayRouter remote = NewRouter(suffix);
        using JsonDocument created = await Ok(hub, DiscussionOperations.Create, """{"title":"poll"}""");
        string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument reader = await Ok(remote, DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"reader"}""");
        using JsonDocument writer = await Ok(hub, DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"writer"}""");
        string readerToken = reader.RootElement.GetProperty("participantToken").GetString()!;
        string writerToken = writer.RootElement.GetProperty("participantToken").GetString()!;
        long last = writer.RootElement.GetProperty("lastSequence").GetInt64(); // after both joins: nothing newer exists, so the read waits

        Task<DiscussionReply> waiting = Ask(remote, DiscussionOperations.Read, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{readerToken}}","afterSequence":{{last}},"waitSeconds":20}""");
        await WaitUntilAsync(() => hub.LocalHub!.GetDiagnostics().WaiterCount == 1);
        using JsonDocument posted = await Ok(hub, DiscussionOperations.Post, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{writerToken}}","content":"wake up"}""");
        DiscussionReply reply = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reply.IsOk);
        using JsonDocument read = JsonDocument.Parse(reply.ResultJson!);
        Assert.Contains(read.RootElement.GetProperty("messages").EnumerateArray(), m => m.GetProperty("content").GetString() == "wake up");
        Assert.False(read.RootElement.GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public async Task ADisposedHubClosesTheOpenConnectionsAndFreesTheName()
    {
        (string name, DiscussionPipeHost host, ScriptedBoard board) = StartHost();
        var release = new TaskCompletionSource();
        board.Handler = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return DiscussionReply.Ok("{}");
        };

        Task<DiscussionReply> pending = DiscussionPipeClient.CallAsync(name, new DiscussionRequest("read", "x", "{}"), CancellationToken.None);
        await WaitUntilAsync(() => board.Calls == 1);
        host.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        using DiscussionPipeHost? next = DiscussionPipeHost.TryClaim(name);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task AWaitingReadOverThePipeWhenHubIsDisposedReElectsAndReturnsHubReset()
    {
        string suffix = NewSuffix();
        DiscussionGatewayRouter hub = NewRouter(suffix);
        DiscussionGatewayRouter remote = NewRouter(suffix);
        using JsonDocument created = await Ok(hub, DiscussionOperations.Create, """{"title":"poll"}""");
        string sessionId = created.RootElement.GetProperty("sessionId").GetString()!;
        using JsonDocument reader = await Ok(remote, DiscussionOperations.Join, $$"""{"sessionId":"{{sessionId}}","displayName":"reader"}""");
        string readerToken = reader.RootElement.GetProperty("participantToken").GetString()!;
        long last = reader.RootElement.GetProperty("lastSequence").GetInt64();

        Task<DiscussionReply> waiting = Ask(remote, DiscussionOperations.Read, $$"""{"sessionId":"{{sessionId}}","participantToken":"{{readerToken}}","afterSequence":{{last}},"waitSeconds":20}""");
        await WaitUntilAsync(() => hub.LocalHub!.GetDiagnostics().WaiterCount == 1);

        hub.Dispose();

        DiscussionReply reply = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(reply.IsOk);
        Assert.Equal(DiscussionErrorCodes.HubReset, Code(reply));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < end)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition did not become true in time");
    }
}
