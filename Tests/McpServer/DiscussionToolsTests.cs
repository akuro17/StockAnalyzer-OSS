using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T5 of the discussion board: the seven tools through the real composition (<c>--enable-discussion</c>) with the real board behind them. The flag, the schemas, the
/// results of every tool, the error paths, the wait that must not stop other tools, the command log, the start-up checks and the help text.
/// </summary>
public sealed class DiscussionToolsTests : IDisposable
{
    private static readonly string[] DiscussionToolNames =
    {
        "sa_discussion_attach_analysis", "sa_discussion_close", "sa_discussion_create", "sa_discussion_get_analysis", "sa_discussion_join", "sa_discussion_leave", "sa_discussion_post", "sa_discussion_read", "sa_discussion_status",
    };

    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "sa_discussion_tools_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private int _requestId = 100;

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        if (Directory.Exists(_emptyRoot))
        {
            Directory.Delete(_emptyRoot, recursive: true);
        }
    }

    private async Task<McpServerRuntime> StartAsync(bool enableDiscussion = true, params (string Key, int Value)[] settings)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, settings), new LogSink(new StringWriter()), enableDiscussion: enableDiscussion);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime;
    }

    private async Task<JsonDocument> CallAsync(McpServerRuntime runtime, string tool, string argumentsJson = "{}")
        => (await McpTestSupport.CallAsync(runtime.Dispatcher, tool, argumentsJson, Interlocked.Increment(ref _requestId)))!;

    private async Task<(string SessionId, string Token, string Handle)> OpenAndJoinAsync(McpServerRuntime runtime, string name = "Alice", string turnMode = "free")
    {
        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", $$"""{"title":"7203 check","turnMode":"{{turnMode}}"}""");
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        (string token, string handle) = await JoinAsync(runtime, sessionId, name);
        return (sessionId, token, handle);
    }

    private async Task<(string Token, string Handle)> JoinAsync(McpServerRuntime runtime, string sessionId, string name)
    {
        using JsonDocument joined = await CallAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"{{name}}"}""");
        JsonElement result = McpTestSupport.Structured(joined);
        return (result.GetProperty("participantToken").GetString()!, result.GetProperty("handle").GetString()!);
    }

    private static string Error(JsonDocument response) => McpTestSupport.ErrorCodeOf(response);

    // ---- The flag ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheFlagNoDiscussionToolExistsAndTheDiscussionSettingsAreNeverRead()
    {
        McpServerRuntime runtime = await StartAsync(enableDiscussion: false, ("Discussion:MaxWaitSeconds", 99));

        using JsonDocument list = JsonDocument.Parse((await runtime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        string[] names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        using JsonDocument call = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_create", """{"title":"x"}"""))!;

        Assert.Empty(names.Intersect(DiscussionToolNames));
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, call.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Unknown tool", call.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void WithTheFlagABadDiscussionSettingStopsTheStartUpAndNamesTheKey()
    {
        var error = Assert.Throws<DiscussionLimitsException>(() => McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxWaitSeconds", 99)), new LogSink(new StringWriter()), enableDiscussion: true));

        Assert.Contains("AiIntegration:Discussion:MaxWaitSeconds is 99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWaitMustBeShorterThanTheToolTimeAndAFullReadPageMustFitTheResponseLimit()
    {
        var wait = Assert.Throws<DiscussionLimitsException>(() => McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxWaitSeconds", 30), ("ToolTimeoutSeconds", 30)), new LogSink(new StringWriter()), enableDiscussion: true));
        var page = Assert.Throws<DiscussionLimitsException>(() => McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxMessageBytes", 65536), ("Discussion:MaxReadLimit", 200)), new LogSink(new StringWriter()), enableDiscussion: true));

        Assert.Contains("MaxWaitSeconds", wait.Message, StringComparison.Ordinal);
        Assert.Contains("MaxReadLimit", page.Message, StringComparison.Ordinal);

        // The same values are fine when the host allows them.
        McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxWaitSeconds", 29), ("ToolTimeoutSeconds", 30)), new LogSink(new StringWriter()), enableDiscussion: true).Dispose();
    }

    [Fact]
    public async Task TheNineToolsAreListedWithHonestAnnotationsAndTheirTurnRules()
    {
        McpServerRuntime runtime = await StartAsync();

        using JsonDocument list = JsonDocument.Parse((await runtime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Where(t => t.GetProperty("name").GetString()!.StartsWith("sa_discussion_", StringComparison.Ordinal))
            .ToDictionary(t => t.GetProperty("name").GetString()!);

        Assert.Equal(DiscussionToolNames, tools.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach ((string name, JsonElement tool) in tools)
        {
            JsonElement annotations = tool.GetProperty("annotations");
            bool readOnly = name == "sa_discussion_status";
            Assert.Equal(readOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(readOnly, annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.False(tool.TryGetProperty("outputSchema", out _));
            Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        }

        Assert.All(runtime.Tools.Tools.Where(t => t.Tool.Name.StartsWith("sa_discussion_", StringComparison.Ordinal)), t =>
        {
            bool attach = t.Tool.Name == "sa_discussion_attach_analysis"; // the only one that takes the turn: it runs an analysis tool
            Assert.Equal(attach, t.Tool.UsesExecutionTurn);
            Assert.Equal(attach, t.Tool.IsHeavy);
            Assert.False(t.Tool.IsExecution);
        });
    }

    [Fact]
    public async Task EveryExampleArgumentsTextIsAcceptedByItsOwnSchema()
    {
        McpServerRuntime runtime = await StartAsync();

        foreach (RegisteredTool registered in runtime.Tools.Tools.Where(t => t.Tool.Name.StartsWith("sa_discussion_", StringComparison.Ordinal)))
        {
            using JsonDocument example = JsonDocument.Parse(registered.Tool.ExampleArgumentsJson!);
            ToolArgumentValidator.Validate(registered.Schema, example.RootElement); // throws on a mismatch
            Assert.DoesNotContain('\n', registered.Tool.ExampleArgumentsJson!);
        }
    }

    // ---- A whole discussion -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoParticipantsHaveAConversationFromCreateToClose()
    {
        McpServerRuntime runtime = await StartAsync();
        const string Japanese = "RSI は 62 です。トレンドは上向きと読みます。\U0001F4C8";

        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", """{"title":"7203.T trend","topic":"Is the trend up?"}""");
        JsonElement createdResult = McpTestSupport.Structured(created);
        string sessionId = createdResult.GetProperty("sessionId").GetString()!;
        Assert.Matches("^S[a-z2-7]{6}$", sessionId);
        Assert.Equal("free", createdResult.GetProperty("turnMode").GetString());
        Assert.Equal("active", createdResult.GetProperty("state").GetString());
        Assert.Equal(20, createdResult.GetProperty("limits").GetProperty("maxWaitSeconds").GetInt32());
        Assert.Equal(6, createdResult.GetProperty("limits").GetProperty("maxParticipants").GetInt32());

        using JsonDocument aliceJoin = await CallAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Alice"}""");
        JsonElement alice = McpTestSupport.Structured(aliceJoin);
        string aliceToken = alice.GetProperty("participantToken").GetString()!;
        Assert.Equal("p1", alice.GetProperty("handle").GetString());
        Assert.False(alice.GetProperty("rejoined").GetBoolean());
        string protocol = alice.GetProperty("protocol").GetString()!;
        Assert.StartsWith("How to take part", protocol, StringComparison.Ordinal);
        Assert.EndsWith(McpDisclaimerConstants.ResearchNotAdvice, protocol, StringComparison.Ordinal);
        Assert.Equal(DiscussionProtocolText.Text + " " + McpDisclaimerConstants.ResearchNotAdvice, protocol);

        (string bobToken, _) = await JoinAsync(runtime, sessionId, "Bob");
        using JsonDocument posted = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{aliceToken}}","content":"{{Japanese}}","basedOnSequence":2,"clientRequestId":"r1"}""");
        JsonElement postResult = McpTestSupport.Structured(posted);
        Assert.Equal(3, postResult.GetProperty("sequence").GetInt32());
        Assert.Equal(0, postResult.GetProperty("newerMessages").GetInt32());
        Assert.False(postResult.GetProperty("duplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, postResult.GetProperty("floor").ValueKind);

        using JsonDocument again = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{aliceToken}}","content":"ignored","clientRequestId":"r1"}""");
        Assert.True(McpTestSupport.Structured(again).GetProperty("duplicate").GetBoolean());

        using JsonDocument read = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bobToken}}","afterSequence":2}""");
        JsonElement readResult = McpTestSupport.Structured(read);
        JsonElement message = Assert.Single(readResult.GetProperty("messages").EnumerateArray());
        Assert.Equal(Japanese, message.GetProperty("content").GetString());
        Assert.Equal("peer", message.GetProperty("origin").GetString());
        Assert.Equal("message", message.GetProperty("kind").GetString());
        Assert.Equal("p1", message.GetProperty("handle").GetString());
        Assert.Equal("Alice", message.GetProperty("displayName").GetString());
        Assert.Equal(3, readResult.GetProperty("nextAfterSequence").GetInt32());
        Assert.False(readResult.GetProperty("hasMore").GetBoolean());
        Assert.False(readResult.GetProperty("timedOut").GetBoolean());
        // The text block holds the Japanese as it is, not as \u escapes (a result must not be larger than its text).
        Assert.Contains("RSI は 62", Result(read).GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);

        using JsonDocument leave = await CallAsync(runtime, "sa_discussion_leave", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{aliceToken}}"}""");
        Assert.False(McpTestSupport.Structured(leave).GetProperty("alreadyLeft").GetBoolean());

        using JsonDocument closed = await CallAsync(runtime, "sa_discussion_close", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bobToken}}"}""");
        JsonElement closeResult = McpTestSupport.Structured(closed);
        Assert.False(closeResult.GetProperty("alreadyClosed").GetBoolean());
        Assert.Equal("closed", closeResult.GetProperty("state").GetString());
        Assert.Equal("participant", closeResult.GetProperty("closeReason").GetString());
        Assert.Equal(closeResult.GetProperty("session").GetProperty("messageCount").GetInt32(), closeResult.GetProperty("messageCount").GetInt32());

        using JsonDocument afterClose = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bobToken}}","content":"late"}""");
        Assert.Equal("DISCUSSION_CLOSED", Error(afterClose));
    }

    private static JsonElement Result(JsonDocument response) => McpTestSupport.Result(response);

    [Fact]
    public async Task StatusListsTheDiscussionsTheLimitsTheAuditAndTheCommandsWithoutAnyValue()
    {
        McpServerRuntime runtime = await StartAsync();
        const string SecretBody = "private market opinion that must not be logged";
        (string sessionId, string token, _) = await OpenAndJoinAsync(runtime);
        (await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"{{SecretBody}}"}""")).Dispose();
        (await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}"}""")).Dispose(); // missing content: INVALID_ARGUMENT

        using JsonDocument status = await CallAsync(runtime, "sa_discussion_status", """{"auditTail":50,"commandTail":50}""");
        JsonElement result = McpTestSupport.Structured(status);

        Assert.Equal("hub", result.GetProperty("role").GetString());
        if (OperatingSystem.IsWindows())
        {
            // The pipe hub exists on Windows: this process holds the board, shares it, and the key is the short code of the data folder.
            Assert.True(result.GetProperty("sharing").GetBoolean());
            Assert.Matches("^[0-9a-f]{8}$", result.GetProperty("sharingKey").GetString());
        }
        else
        {
            Assert.False(result.GetProperty("sharing").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("sharingKey").ValueKind);
        }

        Assert.Equal(sessionId, Assert.Single(result.GetProperty("sessions").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(300, result.GetProperty("effectiveLimits").GetProperty("maxMessagesPerSession").GetInt32());
        Assert.False(result.GetProperty("effectiveLimits").GetProperty("commandLogToFile").GetBoolean());
        string[] audit = result.GetProperty("audit").EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToArray();
        Assert.Equal(new[] { "HubStarted", "SessionCreated", "ParticipantJoined", "MessagePosted", "PostRejected" }, audit);

        JsonElement[] commands = result.GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "sa_discussion_create", "sa_discussion_join", "sa_discussion_post", "sa_discussion_post" },
            commands.Select(c => c.GetProperty("tool").GetString()).ToArray());
        Assert.Equal(new[] { "ok", "ok", "ok", "INVALID_ARGUMENT" }, commands.Select(c => c.GetProperty("outcome").GetString()).ToArray());
        Assert.Equal(new[] { "sessionId", "participantToken", "content" }, commands[2].GetProperty("argKeys").EnumerateArray().Select(k => k.GetString()).ToArray());
        string everything = Result(status).GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain(token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("private market", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", string.Join("\n", result.GetProperty("audit").EnumerateArray().Select(e => e.GetRawText())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfOneDiscussionAndOfAnUnknownOne()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, _, _) = await OpenAndJoinAsync(runtime);

        using JsonDocument one = await CallAsync(runtime, "sa_discussion_status", $$"""{"sessionId":"{{sessionId}}"}""");
        using JsonDocument unknown = await CallAsync(runtime, "sa_discussion_status", """{"sessionId":"Snothing"}""");
        using JsonDocument tail = await CallAsync(runtime, "sa_discussion_status", """{"auditTail":101}""");
        using JsonDocument commandTail = await CallAsync(runtime, "sa_discussion_status", """{"commandTail":101}""");

        JsonElement result = McpTestSupport.Structured(one);
        Assert.Equal(sessionId, result.GetProperty("session").GetProperty("id").GetString());
        Assert.False(result.TryGetProperty("sessions", out _));
        Assert.Empty(result.GetProperty("audit").EnumerateArray());
        Assert.Empty(result.GetProperty("commands").EnumerateArray());
        Assert.Equal("DISCUSSION_NOT_FOUND", Error(unknown));
        Assert.Equal("LIMIT_EXCEEDED", Error(tail));
        Assert.Equal(("LIMIT_EXCEEDED", "commandTail"), (Error(commandTail), McpTestSupport.Structured(commandTail).GetProperty("param").GetString()));
    }

    // ---- Error paths through the tools -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSchemaRefusesMissingUnknownAndWronglyTypedArgumentsBeforeTheBoardIsAsked()
    {
        McpServerRuntime runtime = await StartAsync();

        using JsonDocument missing = await CallAsync(runtime, "sa_discussion_join", """{"sessionId":"Sabcdef"}""");
        using JsonDocument unknown = await CallAsync(runtime, "sa_discussion_create", """{"title":"t","colour":"red"}""");
        using JsonDocument wrongType = await CallAsync(runtime, "sa_discussion_read", """{"sessionId":"S","participantToken":"t","limit":"ten"}""");
        using JsonDocument badMode = await CallAsync(runtime, "sa_discussion_create", """{"title":"t","turnMode":"sideways"}""");

        Assert.All(new[] { missing, unknown, wrongType, badMode }, response => Assert.Equal("INVALID_ARGUMENT", Error(response)));
        Assert.Equal("displayName", McpTestSupport.Structured(missing).GetProperty("param").GetString());
    }

    [Fact]
    public async Task TheBoardsErrorsReachTheClientWithTheirCodesParametersAndHints()
    {
        McpServerRuntime runtime = await StartAsync(true, ("Discussion:MaxPostsPerMinute", 1));
        (string sessionId, string token, _) = await OpenAndJoinAsync(runtime);
        string foreign = "abcdef.AAAAAAAAAAAAAAAAAAAAAA";

        using JsonDocument unknownSession = await CallAsync(runtime, "sa_discussion_join", """{"sessionId":"Snothing","displayName":"Bob"}""");
        using JsonDocument notJoined = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"garbage","content":"x"}""");
        using JsonDocument reset = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{foreign}}","content":"x"}""");
        (await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"first"}""")).Dispose();
        using JsonDocument rate = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"second"}""");
        using JsonDocument limit = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","limit":51}""");
        using JsonDocument wait = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","waitSeconds":21}""");

        Assert.Equal("DISCUSSION_NOT_FOUND", Error(unknownSession));
        Assert.Equal("PARTICIPANT_NOT_JOINED", Error(notJoined));
        Assert.Equal("HUB_RESET", Error(reset));
        Assert.Equal("RATE_LIMITED", Error(rate));
        Assert.StartsWith("retry after ", McpTestSupport.Structured(rate).GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.Equal(("LIMIT_EXCEEDED", "limit"), (Error(limit), McpTestSupport.Structured(limit).GetProperty("param").GetString()));
        Assert.Equal(("LIMIT_EXCEEDED", "waitSeconds"), (Error(wait), McpTestSupport.Structured(wait).GetProperty("param").GetString()));
        Assert.All(new[] { unknownSession, notJoined, reset, rate, limit, wait }, response => Assert.True(McpTestSupport.IsError(response)));
        Assert.DoesNotContain(token, Result(rate).GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoundRobinDiscussionRefusesAnOutOfTurnPostThroughTheToolAndPassMovesTheFloor()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, string alice, _) = await OpenAndJoinAsync(runtime, "Alice", "roundRobin");
        (string bob, _) = await JoinAsync(runtime, sessionId, "Bob");

        using JsonDocument early = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}","content":"my turn?"}""");
        using JsonDocument passed = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","pass":true}""");
        using JsonDocument now = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}","content":"now it is"}""");

        Assert.Equal("NOT_YOUR_TURN", Error(early));
        Assert.Equal("floor", McpTestSupport.Structured(early).GetProperty("param").GetString());
        Assert.Equal("p1 (Alice) speaks next; 60 s until the floor passes", McpTestSupport.Structured(early).GetProperty("hint").GetString());
        Assert.Equal("p2", McpTestSupport.Structured(passed).GetProperty("floor").GetProperty("handle").GetString());
        Assert.False(McpTestSupport.IsError(now));
        Assert.Equal("p1", McpTestSupport.Structured(now).GetProperty("floor").GetProperty("handle").GetString());
    }

    // ---- The wait does not stop other tools ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWaitingReadNeverBlocksAnotherToolAndIsWokenByAPost()
    {
        McpServerRuntime runtime = await StartAsync(true, ("QueueWaitSeconds", 0)); // a call that cannot get the turn at once would be SERVER_BUSY
        (string sessionId, string alice, _) = await OpenAndJoinAsync(runtime);
        (string bob, _) = await JoinAsync(runtime, sessionId, "Bob");

        Task<JsonDocument> waiting = CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","afterSequence":2,"waitSeconds":10}""");
        await McpTestSupport.WaitUntilAsync(() => !waiting.IsCompleted, 5);
        using JsonDocument settings = await CallAsync(runtime, "sa_get_settings");
        using JsonDocument status = await CallAsync(runtime, "sa_discussion_status");
        using JsonDocument post = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}","content":"wake up"}""");
        using JsonDocument result = await waiting.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(McpTestSupport.IsError(settings));
        Assert.False(McpTestSupport.IsError(status));
        Assert.False(McpTestSupport.IsError(post));
        Assert.Equal("wake up", Assert.Single(McpTestSupport.Structured(result).GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
        Assert.False(McpTestSupport.Structured(result).GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public async Task AWaitingReadThatSeesNothingReportsTimedOut()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, string alice, _) = await OpenAndJoinAsync(runtime);

        using JsonDocument result = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","afterSequence":1,"waitSeconds":1}""");

        Assert.True(McpTestSupport.Structured(result).GetProperty("timedOut").GetBoolean());
        Assert.Empty(McpTestSupport.Structured(result).GetProperty("messages").EnumerateArray());
    }

    // ---- The command log in the real composition ------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryToolIncludingTheDataToolsIsRecordedWhileTheFlagIsOn()
    {
        McpServerRuntime runtime = await StartAsync();
        (await CallAsync(runtime, "sa_get_settings")).Dispose();
        (await CallAsync(runtime, "sa_get_candles", """{"symbol":"NOPE"}""")).Dispose();

        using JsonDocument status = await CallAsync(runtime, "sa_discussion_status", """{"commandTail":10}""");

        JsonElement[] commands = McpTestSupport.Structured(status).GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sa_get_settings", "sa_get_candles" }, commands.Select(c => c.GetProperty("tool").GetString()).ToArray());
        Assert.Equal("ok", commands[0].GetProperty("outcome").GetString());
        Assert.NotEqual("ok", commands[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NothingIsCreatedOnDiskForTheBoardOrTheCommandLogByDefault()
    {
        bool existed = Directory.Exists(DiscussionTempFolder.Root);
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, string token, _) = await OpenAndJoinAsync(runtime);
        (await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"hello"}""")).Dispose();

        Assert.Equal(existed, Directory.Exists(DiscussionTempFolder.Root));
        Assert.False(Directory.Exists(_emptyRoot) && Directory.EnumerateFileSystemEntries(_emptyRoot, "*", SearchOption.AllDirectories).Any());
    }

    // ---- Help and the command line -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHelpListsTheDiscussionToolsOnlyWithTheFlagAndShowsTheLimitsOfTheArguments()
    {
        McpServerRuntime with = await StartAsync();
        McpServerRuntime without = await StartAsync(enableDiscussion: false);

        string overviewWith = McpHelpText.Overview(with.Tools, includeExecutionTools: false);
        string overviewWithout = McpHelpText.Overview(without.Tools, includeExecutionTools: false);

        Assert.All(DiscussionToolNames, name => Assert.Contains(name, overviewWith, StringComparison.Ordinal));
        Assert.All(DiscussionToolNames, name => Assert.DoesNotContain(name, overviewWithout, StringComparison.Ordinal));
        Assert.Contains(overviewWithout.Split('\n'), line => line.TrimStart().StartsWith("--enable-discussion ", StringComparison.Ordinal));
        Assert.True(McpHelpText.TryTool(with.Tools, includeExecutionTools: false, "sa_discussion_read", out string text, out string error), error);
        Assert.Contains("  limit (integer, optional, default 20, minimum 1, maximum 50)", text);
        Assert.Contains("  waitSeconds (integer, optional, default 0, minimum 0, maximum 20)", text);
        Assert.Contains("  turnMode", McpHelpText.TryTool(with.Tools, false, "sa_discussion_create", out string create, out _) ? create : string.Empty);
        Assert.False(McpHelpText.TryTool(without.Tools, includeExecutionTools: false, "sa_discussion_read", out _, out _));
    }

    [Theory]
    [InlineData("--enable-discussion")]
    [InlineData("--enable-discussion", "--enable-execution")]
    [InlineData("--enable-execution", "--enable-discussion")]
    [InlineData("--enable-discussion", "--enable-discussion")]
    public void TheFlagParsesAloneAndWithTheExecutionFlagInAnyOrder(params string[] args)
    {
        McpCommandLineResult result = McpCommandLine.Parse(args);

        Assert.IsType<McpCommand.Run>(result.Command);
        Assert.True(result.EnableDiscussion);
        Assert.Equal(args.Contains("--enable-execution"), result.EnableExecution);
    }

    [Fact]
    public void TheFlagIsCaseSensitiveWorksWithHelpAndIsRefusedWithVersion()
    {
        Assert.IsType<McpCommand.Invalid>(McpCommandLine.Parse(new[] { "--Enable-Discussion" }).Command);
        Assert.IsType<McpCommand.Invalid>(McpCommandLine.Parse(new[] { "--enable-discussion", "--version" }).Command);
        Assert.IsType<McpCommand.Invalid>(McpCommandLine.Parse(new[] { "--version", "--enable-discussion" }).Command);
        McpCommandLineResult help = McpCommandLine.Parse(new[] { "--help", "sa_discussion_read", "--enable-discussion" });
        Assert.Equal(new McpCommand.Help("sa_discussion_read"), help.Command);
        Assert.True(help.EnableDiscussion);
        Assert.False(McpCommandLine.Parse(Array.Empty<string>()).EnableDiscussion);
        Assert.False(McpCommandLine.Parse(new[] { "--enable-execution" }).EnableDiscussion);
    }
}
