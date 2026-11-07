using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T3 of the discussion board: the additive changes of the shared tool pipeline. A tool may opt out of the single tool turn (and then never blocks another
/// call), an optional observer hears about every call without ever seeing a value, the dispatcher takes its annotations from <c>IsReadOnly</c> and keeps the client's
/// label. The default behaviour of every existing tool is pinned separately by <see cref="ToolsListBaselineTests"/> and the existing executor tests.
/// </summary>
public sealed class ToolTurnAndObserverTests
{
    private sealed class RecordingObserver : IToolCallObserver
    {
        public List<ToolCallRecord> Calls { get; } = new();

        public void OnToolCall(ToolCallRecord call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }
        }
    }

    private sealed class ThrowingObserver : IToolCallObserver
    {
        public int Calls;

        public void OnToolCall(ToolCallRecord call)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("secret detail that must not reach the log");
        }
    }

    /// <summary>The smallest possible tool: it overrides none of the members that have defaults.</summary>
    private sealed class PlainTool : IMcpTool
    {
        public string Name => "sa_plain";

        public string Description => "Plain.";

        public string InputSchemaJson => ToolSchema.Object().Build();

        public bool IsHeavy => false;

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult("{}");
    }

    private sealed class ExecutionPlainTool : IMcpTool
    {
        public string Name => "sa_plain_execution";

        public string Description => "Plain.";

        public string InputSchemaJson => ToolSchema.Object().Build();

        public bool IsHeavy => false;

        public bool IsExecution => true;

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult("{}");
    }

    private static McpMessageDispatcher Dispatcher(IEnumerable<IMcpTool> tools, McpLimits limits, IToolCallObserver? observer = null, StringWriter? log = null, ClientIdentity? identity = null)
    {
        var sink = new LogSink(log ?? new StringWriter());
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), sink, observer);
        return new McpMessageDispatcher(sink, new McpToolRegistry(tools), executor, identity: identity);
    }

    private static async Task<McpMessageDispatcher> InitializedAsync(IEnumerable<IMcpTool> tools, McpLimits limits, IToolCallObserver? observer = null, StringWriter? log = null)
    {
        McpMessageDispatcher dispatcher = Dispatcher(tools, limits, observer, log);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return dispatcher;
    }

    private static FakeTool Instant(string name, bool usesTurn = true)
        => new(name, (_, _) => Task.FromResult("{}"), usesExecutionTurn: usesTurn);

    // ---- The tool turn --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AToolThatDoesNotUseTheTurnNeverBlocksANormalToolAndTheNormalToolStillTakesItsTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new FakeTool("sa_waiting", async (_, _) => { started.SetResult(); return await gate.Task; }, usesExecutionTurn: false);
        var normal = Instant("sa_normal");
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { waiting, normal }, new McpLimits { QueueWaitSeconds = 0 });

        Task<JsonDocument?> long1 = McpTestSupport.CallAsync(dispatcher, "sa_waiting", id: 1);
        await started.Task;
        using JsonDocument during = (await McpTestSupport.CallAsync(dispatcher, "sa_normal", id: 2))!;

        Assert.False(McpTestSupport.IsError(during));
        Assert.False(long1.IsCompleted);

        gate.SetResult("{}");
        using JsonDocument finished = (await long1)!;
        Assert.False(McpTestSupport.IsError(finished));
    }

    [Fact]
    public async Task ANormalToolStillGetsServerBusyWhileAnotherNormalToolHoldsTheTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new FakeTool("sa_holder", async (_, _) => { started.SetResult(); return await gate.Task; });
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { holder, Instant("sa_other") }, new McpLimits { QueueWaitSeconds = 0 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        await started.Task;
        using JsonDocument busy = (await McpTestSupport.CallAsync(dispatcher, "sa_other", id: 2))!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(busy));
        gate.SetResult("{}");
        (await first)!.Dispose();
    }

    [Fact]
    public async Task AToolThatDoesNotUseTheTurnNeverReleasesATurnItDidNotTake()
    {
        // The turn is a semaphore of size one: a release without a wait would let two normal calls run together afterwards.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new FakeTool("sa_holder", async (_, _) => { started.SetResult(); return await gate.Task; });
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { Instant("sa_free", usesTurn: false), holder, Instant("sa_other") }, new McpLimits { QueueWaitSeconds = 0 });
        for (int i = 0; i < 3; i++)
        {
            (await McpTestSupport.CallAsync(dispatcher, "sa_free", id: 10 + i))!.Dispose();
        }

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        await started.Task;
        using JsonDocument busy = (await McpTestSupport.CallAsync(dispatcher, "sa_other", id: 2))!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(busy));
        gate.SetResult("{}");
        (await first)!.Dispose();
    }

    [Fact]
    public async Task AToolThatDoesNotUseTheTurnIsStillBoundByTheToolTimeLimit()
    {
        var free = new FakeTool("sa_free", async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "{}"; }, usesExecutionTurn: false);
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { free }, new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1 });

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_free"))!;

        Assert.Equal("EXECUTION_TIMEOUT", McpTestSupport.ErrorCodeOf(response));
    }

    // ---- Annotations and the interface defaults ---------------------------------------------------------------------------------

    [Fact]
    public void ADefaultToolUsesTheTurnAndIsReadOnlyAndAnExecutionToolIsNot()
    {
        IMcpTool plain = new PlainTool();
        IMcpTool execution = new ExecutionPlainTool();

        Assert.True(plain.UsesExecutionTurn);
        Assert.True(plain.IsReadOnly);
        Assert.True(execution.UsesExecutionTurn);
        Assert.False(execution.IsReadOnly);
    }

    [Fact]
    public async Task TheToolListTakesReadOnlyAndIdempotentHintsFromIsReadOnly()
    {
        var plain = new PlainTool();
        var execution = new ExecutionPlainTool();
        var stateful = new FakeTool("sa_stateful", (_, _) => Task.FromResult("{}"), isReadOnly: false);
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { plain, execution, stateful }, new McpLimits());

        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        var hints = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("annotations"));

        Assert.True(hints["sa_plain"].GetProperty("readOnlyHint").GetBoolean());
        Assert.True(hints["sa_plain"].GetProperty("idempotentHint").GetBoolean());
        Assert.False(hints["sa_plain_execution"].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(hints["sa_plain_execution"].GetProperty("idempotentHint").GetBoolean());
        Assert.False(hints["sa_stateful"].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(hints["sa_stateful"].GetProperty("idempotentHint").GetBoolean());
        Assert.All(hints.Values, annotations =>
        {
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
        });
    }

    // ---- The observer -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheObserverHearsExactlyOnceAboutEveryOutcomeAndSeesNoArgumentValue()
    {
        const string Secret = "tok-SECRET-value";
        var observer = new RecordingObserver();
        string schema = ToolSchema.Object().String("label", "l", required: true).String("extra", "e").Build();
        var ok = new FakeTool("sa_ok", (_, _) => Task.FromResult("{}"), schema);
        var failing = new FakeTool("sa_failing", (_, _) => throw new ToolException(ToolErrorCodes.LimitExceeded, "too big"), schema);
        var crashing = new FakeTool("sa_crashing", (_, _) => throw new InvalidOperationException("boom"), schema);
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { ok, failing, crashing }, new McpLimits(), observer);

        string arguments = $$"""{"label":"{{Secret}}","extra":"x"}""";
        (await McpTestSupport.CallAsync(dispatcher, "sa_ok", arguments, id: 1))!.Dispose();
        (await McpTestSupport.CallAsync(dispatcher, "sa_failing", arguments, id: 2))!.Dispose();
        (await McpTestSupport.CallAsync(dispatcher, "sa_crashing", arguments, id: 3))!.Dispose();
        (await McpTestSupport.CallAsync(dispatcher, "sa_ok", """{"extra":"x"}""", id: 4))!.Dispose(); // required argument missing

        Assert.Equal(4, observer.Calls.Count);
        Assert.Equal(new[] { "sa_ok", "sa_failing", "sa_crashing", "sa_ok" }, observer.Calls.Select(call => call.ToolName));
        Assert.Equal(new[] { "1", "2", "3", "4" }, observer.Calls.Select(call => call.RequestId));
        Assert.Equal(new[] { ToolCallOutcomes.Ok, ToolErrorCodes.LimitExceeded, ToolErrorCodes.InternalError, ToolErrorCodes.InvalidArgument }, observer.Calls.Select(call => call.Outcome));
        Assert.Equal(new[] { "label", "extra" }, observer.Calls[0].ArgumentKeys);
        Assert.Equal(Encoding.UTF8.GetByteCount(arguments), observer.Calls[0].ArgumentBytes);
        Assert.Equal(new[] { "extra" }, observer.Calls[3].ArgumentKeys);
        Assert.All(observer.Calls, call =>
        {
            Assert.DoesNotContain(Secret, call.ToString(), StringComparison.Ordinal);
            Assert.True(call.Elapsed >= TimeSpan.Zero);
        });
    }

    [Fact]
    public async Task TheObserverHearsAboutServerBusyATimeoutAndACancelledCall()
    {
        var observer = new RecordingObserver();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new FakeTool("sa_holder", async (_, _) => { started.SetResult(); return await gate.Task; });
        var hang = new FakeTool("sa_hang", async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "{}"; }, usesExecutionTurn: false);
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { holder, Instant("sa_other"), hang }, new McpLimits { QueueWaitSeconds = 0, ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1 }, observer);

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        await started.Task;
        (await McpTestSupport.CallAsync(dispatcher, "sa_other", id: 2))!.Dispose();
        gate.SetResult("{}");
        (await first)!.Dispose(); // finishes well inside its time limit, before the slow steps below
        (await McpTestSupport.CallAsync(dispatcher, "sa_hang", id: 3))!.Dispose();
        Task<JsonDocument?> cancelled = McpTestSupport.CallAsync(dispatcher, "sa_hang", id: 4);
        await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":4}}""", CancellationToken.None);
        Assert.Null(await cancelled);

        Dictionary<string, string> outcomes = observer.Calls.ToDictionary(call => call.RequestId, call => call.Outcome);
        Assert.Equal(ToolErrorCodes.ServerBusy, outcomes["2"]);
        Assert.Equal(ToolErrorCodes.ExecutionTimeout, outcomes["3"]);
        Assert.Equal(ToolCallOutcomes.Cancelled, outcomes["4"]);
        Assert.Equal(ToolCallOutcomes.Ok, outcomes["1"]);
        Assert.Equal(4, observer.Calls.Count);
    }

    [Fact]
    public async Task AnObserverThatThrowsNeverChangesTheResultAndIsReportedOnceWithoutItsMessage()
    {
        var observer = new ThrowingObserver();
        var log = new StringWriter();
        McpMessageDispatcher dispatcher = await InitializedAsync(new IMcpTool[] { new FakeTool("sa_ok", (_, _) => Task.FromResult("""{"value":1}""")) }, new McpLimits(), observer, log);

        using JsonDocument first = (await McpTestSupport.CallAsync(dispatcher, "sa_ok", id: 1))!;
        using JsonDocument second = (await McpTestSupport.CallAsync(dispatcher, "sa_ok", id: 2))!;

        Assert.False(McpTestSupport.IsError(first));
        Assert.Equal(1, McpTestSupport.Structured(second).GetProperty("value").GetInt32());
        Assert.Equal(2, observer.Calls);
        string text = log.ToString();
        Assert.Equal(1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Contains("observer failed", StringComparison.Ordinal)));
        Assert.DoesNotContain("secret detail", text, StringComparison.Ordinal);
    }

    // ---- The client label -------------------------------------------------------------------------------------------------------

    private static string InitializeWithName(string jsonEncodedName)
        => """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":""" + jsonEncodedName + ""","version":"1"}}}""";

    [Fact]
    public async Task TheDispatcherKeepsTheClientNameAsTheLabelOnceTheClientInitializes()
    {
        var identity = new ClientIdentity();
        McpMessageDispatcher dispatcher = Dispatcher(Array.Empty<IMcpTool>(), new McpLimits(), identity: identity);
        Assert.Null(identity.Label);

        _ = await dispatcher.DispatchAsync(InitializeWithName("\"antigravity-client\""), CancellationToken.None);

        Assert.Equal("antigravity-client", identity.Label);
    }

    [Fact]
    public void TheLabelReplacesControlCharactersTrimsAndCutsWithoutSplittingASurrogatePair()
    {
        var identity = new ClientIdentity();

        identity.Set("  a\tb\nc  ");
        Assert.Equal("a b c", identity.Label);

        identity.Set(new string('x', 200));
        Assert.Equal(64, identity.Label!.Length);

        // 63 letters and then an emoji (two UTF-16 characters): the cut at 64 would keep only its first half.
        identity.Set(new string('y', 63) + "\U0001F600" + "tail");
        Assert.Equal(new string('y', 63), identity.Label);
    }

    [Fact]
    public async Task ADispatcherWithoutAnIdentityStillInitializes()
    {
        McpMessageDispatcher dispatcher = Dispatcher(Array.Empty<IMcpTool>(), new McpLimits());

        string? response = await dispatcher.DispatchAsync(InitializeWithName("\"c\""), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(response!);
        Assert.True(document.RootElement.TryGetProperty("result", out _));
    }
}
