using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.McpServer.Tools.Discussion;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T4 of the discussion board: the two analysis-snapshot tools (spec 5.10). The inner tools here are real <see cref="IMcpTool"/> implementations written in the test, so
/// the order of the steps (a slow inner tool, a discussion that closes meanwhile) is proved with the real board and the real executor; the last tests use the real composition
/// and a real indicator tool on a throw-away data tree.
/// </summary>
public sealed class DiscussionAttachAnalysisTests : IDisposable
{
    private const string Numbers = """{"close":12345678901234567890.123456789,"name":"日本","rsi":[55.5,56.25]}""";

    private readonly List<IDisposable> _disposables = new();
    private int _requestId = 500;

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private sealed class Rig
    {
        public required McpMessageDispatcher Dispatcher { get; init; }

        public required DiscussionHub Hub { get; init; }

        public required CommandLog Log { get; init; }
    }

    /// <summary>The nine discussion tools on a real board and a real executor, plus the inner tools a test supplies.</summary>
    private async Task<Rig> StartAsync(IEnumerable<IMcpTool> inner, Action<DiscussionSettings>? configure = null)
    {
        var settings = new DiscussionSettings();
        configure?.Invoke(settings);
        var hub = new DiscussionHub(settings, startSweeper: false);
        var log = new CommandLog(settings);
        var gateway = new DiscussionHubGateway(hub, settings, log, McpDisclaimerConstants.ResearchNotAdvice);
        var identity = new ClientIdentity();
        McpToolRegistry? registry = null;
        var tools = new List<IMcpTool>(inner)
        {
            new DiscussionCreateTool(gateway, identity),
            new DiscussionJoinTool(gateway, identity),
            new DiscussionLeaveTool(gateway, identity),
            new DiscussionPostTool(gateway, identity, settings),
            new DiscussionReadTool(gateway, identity, settings),
            new DiscussionStatusTool(gateway, identity),
            new DiscussionCloseTool(gateway, identity),
            new DiscussionAttachAnalysisTool(gateway, identity, () => registry!),
            new DiscussionGetAnalysisTool(gateway, identity),
        };
        registry = new McpToolRegistry(tools);
        var sink = new LogSink(new StringWriter());
        var executor = new ToolExecutor(new McpLimits { QueueWaitSeconds = 0 }, new ErrorMessageSanitizer(Array.Empty<string?>()), sink, new CommandLogObserver(log));
        var dispatcher = new McpMessageDispatcher(sink, registry, executor, identity: identity);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        _disposables.Add(hub);
        _disposables.Add(log);
        return new Rig { Dispatcher = dispatcher, Hub = hub, Log = log };
    }

    private async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, tool, argumentsJson, Interlocked.Increment(ref _requestId)))!;

    private async Task<(string SessionId, string Alice, string Bob)> OpenAsync(Rig rig)
    {
        using JsonDocument created = await CallAsync(rig.Dispatcher, "sa_discussion_create", """{"title":"t"}""");
        string id = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        string alice = await JoinAsync(rig, id, "Alice");
        string bob = await JoinAsync(rig, id, "Bob");
        return (id, alice, bob);
    }

    private async Task<string> JoinAsync(Rig rig, string id, string name)
    {
        using JsonDocument joined = await CallAsync(rig.Dispatcher, "sa_discussion_join", $$"""{"sessionId":"{{id}}","displayName":"{{name}}"}""");
        return McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;
    }

    private static string Attach(string id, string token, string tool, string? arguments = null, string? note = null)
    {
        var text = new StringBuilder($$"""{"sessionId":"{{id}}","participantToken":"{{token}}","tool":"{{tool}}" """);
        if (arguments is not null)
        {
            text.Append($",\"arguments\":{arguments}");
        }

        if (note is not null)
        {
            text.Append($",\"note\":\"{note}\"");
        }

        return text.Append('}').ToString();
    }

    private static FakeTool Inner(string name, Func<JsonElement, Task<string>> run, string? schema = null)
        => new(name, (arguments, _) => run(arguments), schema);

    private static FakeTool Constant(string name, string result, string? schema = null) => Inner(name, _ => Task.FromResult(result), schema);

    // ---- The happy path -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASnapshotIsStoredVerbatimWithItsHashAndEveryParticipantFetchesTheSameBytes()
    {
        JsonElement? seen = null;
        FakeTool inner = Inner("sa_calc", arguments => { seen = arguments.Clone(); return Task.FromResult(Numbers); }, ToolSchema.Object().String("symbol", "s").Integer("n", "n").Build());
        Rig rig = await StartAsync(new IMcpTool[] { inner });
        (string id, string alice, string bob) = await OpenAsync(rig);

        using JsonDocument attached = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", """{"symbol":"7203.T","n":5}""", "daily"));
        JsonElement result = McpTestSupport.Structured(attached);
        using JsonDocument fetched = await CallAsync(rig.Dispatcher, "sa_discussion_get_analysis", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","contextId":1}""");
        JsonElement context = McpTestSupport.Structured(fetched);

        Assert.False(McpTestSupport.IsError(attached));
        Assert.Equal(1, result.GetProperty("contextId").GetInt32());
        Assert.False(result.GetProperty("reused").GetBoolean());
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Numbers))).ToLowerInvariant();
        Assert.Equal(expectedHash, result.GetProperty("sha256").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(Numbers), result.GetProperty("resultBytes").GetInt32());
        Assert.Equal(Numbers, context.GetProperty("result").GetRawText());
        Assert.Contains("12345678901234567890.123456789", context.GetProperty("result").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(expectedHash, context.GetProperty("sha256").GetString());
        Assert.Equal("""{"n":5,"symbol":"7203.T"}""", context.GetProperty("arguments").GetRawText());
        Assert.Equal(("sa_calc", "p1", "daily"), (context.GetProperty("tool").GetString(), context.GetProperty("attachedBy").GetString(), context.GetProperty("note").GetString()));
        // The inner tool received the arguments as the client sent them, not the canonical form.
        Assert.Equal("7203.T", seen!.Value.GetProperty("symbol").GetString());

        using JsonDocument without = await CallAsync(rig.Dispatcher, "sa_discussion_get_analysis", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","contextId":1,"includeResult":false}""");
        Assert.False(McpTestSupport.Structured(without).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task TheSameToolArgumentsAndResultAreReusedEvenWhenTheKeysComeInAnotherOrder()
    {
        Rig rig = await StartAsync(new IMcpTool[] { Constant("sa_calc", Numbers, ToolSchema.Object().String("a", "a").String("b", "b").Build()) });
        (string id, string alice, string bob) = await OpenAsync(rig);

        using JsonDocument first = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", """{"b":"2","a":"1"}"""));
        using JsonDocument second = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, bob, "sa_calc", """{ "a": "1", "b": "2" }"""));

        Assert.False(McpTestSupport.Structured(first).GetProperty("reused").GetBoolean());
        Assert.True(McpTestSupport.Structured(second).GetProperty("reused").GetBoolean());
        Assert.Equal(1, McpTestSupport.Structured(second).GetProperty("contextId").GetInt32());
        Assert.Equal(1, rig.Hub.Status(id).Sessions[0].ContextCount);
    }

    [Fact]
    public async Task AnAttachedContextAppearsAsAnAnalysisMessageAndCanBeReferencedByAPost()
    {
        Rig rig = await StartAsync(new IMcpTool[] { Constant("sa_calc", Numbers) });
        (string id, string alice, string bob) = await OpenAsync(rig);
        (await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", note: "daily"))).Dispose();

        using JsonDocument posted = await CallAsync(rig.Dispatcher, "sa_discussion_post", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","content":"I agree with context 1","contextIds":[1]}""");
        using JsonDocument read = await CallAsync(rig.Dispatcher, "sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","afterSequence":2}""");

        Assert.False(McpTestSupport.IsError(posted));
        JsonElement[] messages = McpTestSupport.Structured(read).GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("analysis", messages[0].GetProperty("kind").GetString());
        Assert.Equal("server", messages[0].GetProperty("origin").GetString());
        Assert.Equal("p1 attached context 1 (sa_calc): daily", messages[0].GetProperty("content").GetString());
        Assert.Equal(1, messages[1].GetProperty("contextIds")[0].GetInt32());
    }

    // ---- The whitelist ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyReadOnlyNonImageNonDiscussionToolsCanBeAttachedAndTheHintListsThemSorted()
    {
        int started = 0;
        FakeTool executionTool = new("sa_start_x", (_, _) => { started++; return Task.FromResult("{}"); }, isExecution: true);
        var imageTool = new FakeImageTool("sa_image_x", () => { started++; return new ToolImageResult("{}", new byte[] { 1 }); });
        FakeTool zebra = Inner("sa_zebra", _ => { started++; return Task.FromResult("{}"); });
        FakeTool alpha = Inner("sa_alpha", _ => { started++; return Task.FromResult("{}"); });
        Rig rig = await StartAsync(new IMcpTool[] { executionTool, imageTool, zebra, alpha });
        (string id, string alice, _) = await OpenAsync(rig);

        foreach (string refused in new[] { "sa_start_x", "sa_image_x", "sa_discussion_status", "sa_discussion_attach_analysis", "sa_discussion_get_analysis", "sa_unknown" })
        {
            using JsonDocument response = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, refused));
            JsonElement error = McpTestSupport.Structured(response);
            Assert.Equal("INVALID_ARGUMENT", error.GetProperty("code").GetString());
            Assert.Equal("tool", error.GetProperty("param").GetString());
            Assert.Equal("Allowed tools: sa_alpha, sa_zebra.", error.GetProperty("hint").GetString());
        }

        Assert.Equal(0, started);
        Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
    }

    [Fact]
    public async Task TheInnerToolsOwnSchemaIsCheckedAndItsErrorNamesTheArgumentUnderArguments()
    {
        int started = 0;
        FakeTool inner = Inner("sa_calc", _ => { started++; return Task.FromResult("{}"); }, ToolSchema.Object().String("symbol", "s", required: true).Build());
        Rig rig = await StartAsync(new IMcpTool[] { inner });
        (string id, string alice, _) = await OpenAsync(rig);

        using JsonDocument missing = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", "{}"));
        using JsonDocument unknown = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", """{"symbol":"x","colour":"red"}"""));
        using JsonDocument absent = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc"));

        Assert.All(new[] { missing, unknown, absent }, response => Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response)));
        Assert.Equal("arguments.symbol", McpTestSupport.Structured(missing).GetProperty("param").GetString());
        Assert.Equal("arguments.colour", McpTestSupport.Structured(unknown).GetProperty("param").GetString());
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task AnInnerFailureIsReturnedUnchangedAndNothingIsStored()
    {
        FakeTool failing = Inner("sa_calc", _ => throw new ToolException(ToolErrorCodes.SymbolNotFound, "No such symbol.", "symbol", "Check the list."));
        Rig rig = await StartAsync(new IMcpTool[] { failing });
        (string id, string alice, _) = await OpenAsync(rig);

        using JsonDocument response = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc"));

        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(("SYMBOL_NOT_FOUND", "symbol", "Check the list."), (error.GetProperty("code").GetString(), error.GetProperty("param").GetString(), error.GetProperty("hint").GetString()));
        Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
        Assert.Equal(2, rig.Hub.Status(id).Sessions[0].MessageCount); // the two joins only
    }

    [Fact]
    public async Task ABadArgumentsObjectIsRefusedBeforeTheInnerToolRuns()
    {
        int started = 0;
        Rig rig = await StartAsync(new IMcpTool[] { Inner("sa_calc", _ => { started++; return Task.FromResult("{}"); }, ToolSchema.Object().Integer("a", "a").String("k", "k").Build()) });
        (string id, string alice, _) = await OpenAsync(rig);
        string huge = "{\"k\":\"" + new string('a', 4096) + "\"}";

        using JsonDocument duplicate = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", """{"a":1,"a":2}"""));
        using JsonDocument large = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", huge));
        using JsonDocument array = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", "[1]"));

        Assert.Equal(("INVALID_ARGUMENT", "duplicate key"), (McpTestSupport.ErrorCodeOf(duplicate), McpTestSupport.Structured(duplicate).GetProperty("hint").GetString()));
        Assert.Equal(("LIMIT_EXCEEDED", "arguments"), (McpTestSupport.ErrorCodeOf(large), McpTestSupport.Structured(large).GetProperty("param").GetString()));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(array)); // the schema: arguments must be an object
        Assert.Equal(0, started);
    }

    // ---- The precheck and the commit ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePrecheckStopsBeforeTheExpensiveToolWhenTheAttachCouldNotSucceed()
    {
        int started = 0;
        Rig rig = await StartAsync(new IMcpTool[] { Inner("sa_calc", _ => { started++; return Task.FromResult(Numbers); }, ToolSchema.Object().Integer("n", "n").Build()) }, s => s.MaxContextsPerSession = 1);
        (string id, string alice, string bob) = await OpenAsync(rig);
        (await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", """{"n":1}"""))).Dispose();
        Assert.Equal(1, started);

        using JsonDocument full = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, bob, "sa_calc", """{"n":2}"""));
        using JsonDocument foreign = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, "abcdef.AAAAAAAAAAAAAAAAAAAAAA", "sa_calc"));
        using JsonDocument garbage = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, "garbage", "sa_calc"));
        (await CallAsync(rig.Dispatcher, "sa_discussion_close", $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}"}""")).Dispose();
        using JsonDocument closed = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, bob, "sa_calc", """{"n":3}"""));

        Assert.Equal(("LIMIT_EXCEEDED", "contexts"), (McpTestSupport.ErrorCodeOf(full), McpTestSupport.Structured(full).GetProperty("param").GetString()));
        Assert.Equal("HUB_RESET", McpTestSupport.ErrorCodeOf(foreign));
        Assert.Equal("PARTICIPANT_NOT_JOINED", McpTestSupport.ErrorCodeOf(garbage));
        Assert.Equal("DISCUSSION_CLOSED", McpTestSupport.ErrorCodeOf(closed));
        Assert.Equal(1, started); // the tool ran once, for the first attach only
    }

    [Fact]
    public async Task ASlowInnerToolDoesNotStopTheOtherParticipantsFromPostingAndReading()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTool slow = Inner("sa_slow", async _ => { started.SetResult(); return await gate.Task; });
        Rig rig = await StartAsync(new IMcpTool[] { slow });
        (string id, string alice, string bob) = await OpenAsync(rig);

        Task<JsonDocument> attaching = CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_slow"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Both answers must arrive while the gate is still closed: no board lock and no tool turn is held by the waiting attach.
        using JsonDocument post = await CallAsync(rig.Dispatcher, "sa_discussion_post", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","content":"anyone there?"}""").WaitAsync(TimeSpan.FromSeconds(10));
        using JsonDocument read = await CallAsync(rig.Dispatcher, "sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{alice}}","afterSequence":2}""").WaitAsync(TimeSpan.FromSeconds(10));
        using JsonDocument status = await CallAsync(rig.Dispatcher, "sa_discussion_status", "{}").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(attaching.IsCompleted);
        Assert.False(McpTestSupport.IsError(post));
        Assert.Equal("anyone there?", Assert.Single(McpTestSupport.Structured(read).GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
        Assert.False(McpTestSupport.IsError(status));
        Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);

        gate.SetResult("""{"done":true}""");
        using JsonDocument attached = await attaching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(McpTestSupport.IsError(attached));
        Assert.Equal(1, rig.Hub.Status(id).Sessions[0].ContextCount);
        // The transcript order is the order of the board: the post came before the analysis message.
        string[] kinds = (await rig.Hub.ReadAsync(id, alice)).Messages.Skip(2).Select(m => DiscussionNames.ToWire(m.Kind)).ToArray();
        Assert.Equal(new[] { "message", "analysis" }, kinds);
    }

    [Fact]
    public async Task ADiscussionThatClosesWhileTheToolRunsRefusesTheCommitAndStoresNothing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTool slow = Inner("sa_slow", async _ => { started.SetResult(); return await gate.Task; });
        Rig rig = await StartAsync(new IMcpTool[] { slow });
        (string id, string alice, string bob) = await OpenAsync(rig);
        long bytesBefore = rig.Hub.GetDiagnostics().TotalBytes;

        Task<JsonDocument> attaching = CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_slow"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await CallAsync(rig.Dispatcher, "sa_discussion_close", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}"}""")).Dispose();
        long bytesClosed = rig.Hub.GetDiagnostics().TotalBytes;
        gate.SetResult(Numbers);
        using JsonDocument attached = await attaching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("DISCUSSION_CLOSED", McpTestSupport.ErrorCodeOf(attached));
        Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
        Assert.Equal(bytesClosed, rig.Hub.GetDiagnostics().TotalBytes);
        Assert.True(bytesClosed > bytesBefore); // only the closing entry was added
    }

    [Fact]
    public async Task AResultOverTheContextSizeIsRefusedWithAHintAndNeverCut()
    {
        Rig rig = await StartAsync(new IMcpTool[] { Constant("sa_big", "\"" + new string('r', 1100) + "\"") }, s => s.MaxContextBytes = 1024);
        (string id, string alice, _) = await OpenAsync(rig);

        using JsonDocument response = await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_big"));

        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(("LIMIT_EXCEEDED", "result", "request a narrower window or fewer items"), (error.GetProperty("code").GetString(), error.GetProperty("param").GetString(), error.GetProperty("hint").GetString()));
        Assert.Equal(0, rig.Hub.Status(id).Sessions[0].ContextCount);
    }

    // ---- The command log ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAttachIsOneEntryOfTheCommandLogAndTheInnerToolIsNotRecordedAgain()
    {
        Rig rig = await StartAsync(new IMcpTool[] { Constant("sa_calc", Numbers) });
        (string id, string alice, _) = await OpenAsync(rig);
        (await CallAsync(rig.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calc", note: "daily"))).Dispose();

        IReadOnlyList<CommandLogEntry> commands = rig.Log.Tail(50);

        Assert.Equal(1, commands.Count(c => c.Tool == "sa_discussion_attach_analysis"));
        Assert.DoesNotContain(commands, c => c.Tool == "sa_calc");
        CommandLogEntry attach = commands.Single(c => c.Tool == "sa_discussion_attach_analysis");
        Assert.Equal("ok", attach.Outcome);
        Assert.Equal(new[] { "sessionId", "participantToken", "tool", "note" }, attach.ArgKeys);
        string everything = string.Join("\n", commands.Select(c => c.ToString()));
        Assert.DoesNotContain(alice, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901234567890", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolFlagsAreAsDocumented()
    {
        Rig rig = await StartAsync(Array.Empty<IMcpTool>());
        using JsonDocument list = JsonDocument.Parse((await rig.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        Assert.False(tools["sa_discussion_attach_analysis"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tools["sa_discussion_get_analysis"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(9, tools.Count);
    }

    // ---- The real composition with a real indicator tool ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARealIndicatorResultIsStoredByteForByteAndItsHashMatchesTheDirectCall()
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(300, "msft");
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(fixture)).Build(), new LogSink(new StringWriter()), enableDiscussion: true);
        _disposables.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        const string Args = """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"r","type":"RSI"}]}""";

        using JsonDocument created = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_create", """{"title":"msft"}""", 1))!;
        string id = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument aliceJoin = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_join", $$"""{"sessionId":"{{id}}","displayName":"Alice"}""", 2))!;
        using JsonDocument bobJoin = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_join", $$"""{"sessionId":"{{id}}","displayName":"Bob"}""", 3))!;
        string alice = McpTestSupport.Structured(aliceJoin).GetProperty("participantToken").GetString()!;
        string bob = McpTestSupport.Structured(bobJoin).GetProperty("participantToken").GetString()!;

        using JsonDocument direct = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_calculate_indicators", Args, 4))!;
        using JsonDocument attached = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_attach_analysis", Attach(id, alice, "sa_calculate_indicators", Args), 5))!;
        using JsonDocument fetched = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_get_analysis", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","contextId":1}""", 6))!;
        using JsonDocument again = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_attach_analysis", Attach(id, bob, "sa_calculate_indicators", Args), 7))!;

        Assert.False(McpTestSupport.IsError(direct), McpTestSupport.Result(direct).GetRawText());
        Assert.False(McpTestSupport.IsError(attached), McpTestSupport.Result(attached).GetRawText());
        string directText = McpTestSupport.Structured(direct).GetRawText();
        string storedText = McpTestSupport.Structured(fetched).GetProperty("result").GetRawText();
        Assert.Equal(directText, storedText);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directText))).ToLowerInvariant(), McpTestSupport.Structured(attached).GetProperty("sha256").GetString());
        // The same data again (the data tree did not change): reused, no second context.
        Assert.True(McpTestSupport.Structured(again).GetProperty("reused").GetBoolean());
    }

    [Fact]
    public async Task InTheRealCompositionTheImageToolAndTheDiscussionToolsAreRefusedAndTheHintNamesAnalysisTools()
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(30, "msft");
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(fixture)).Build(), new LogSink(new StringWriter()), enableDiscussion: true);
        _disposables.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument created = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_create", """{"title":"msft"}""", 1))!;
        string id = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument join = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_join", $$"""{"sessionId":"{{id}}","displayName":"Alice"}""", 2))!;
        string token = McpTestSupport.Structured(join).GetProperty("participantToken").GetString()!;

        using JsonDocument image = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_attach_analysis", Attach(id, token, "sa_render_chart_image"), 3))!;
        using JsonDocument self = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_discussion_attach_analysis", Attach(id, token, "sa_discussion_attach_analysis"), 4))!;

        string hint = McpTestSupport.Structured(image).GetProperty("hint").GetString()!;
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(image));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(self));
        Assert.Contains("sa_calculate_indicators", hint, StringComparison.Ordinal);
        Assert.Contains("sa_get_candles", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("sa_render_chart_image", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("sa_discussion_", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("sa_start_training", hint, StringComparison.Ordinal);
        string[] names = hint["Allowed tools: ".Length..^1].Split(", ");
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }
}
