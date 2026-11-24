using System.Text.Json;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Owner decision U-01: light read-only tools share <see cref="McpLimits.MaxConcurrentReadOnlyCalls"/> slots; a heavy or execution tool takes the exclusive turn and then every slot, so it
/// runs alone. With one slot the executor is strictly one call at a time (pinned by the older executor tests). A call that cannot get its turn gives back everything it took.
/// </summary>
public sealed class ToolExecutorReaderWriterTests
{
    private const string OverlapToolName = "sa_overlap";
    private const string ProbeHeavyToolName = "sa_probe_heavy";

    /// <summary>A tool that reports that it started and then waits until the test releases it.</summary>
    private sealed class Held
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public bool IsStarted => _started.Task.IsCompleted;

        public void Release() => _gate.TrySetResult("{}");

        public FakeTool Tool(string name, bool heavy = false, bool execution = false)
            => new(name, async (_, _) => { _started.TrySetResult(); return await _gate.Task; }, isHeavy: heavy, isExecution: execution);
    }

    /// <summary>
    /// The two tools that prove, on the SAME executor, that no shared slot and no exclusive turn leaked: two calls of the light tool must be inside it at the same moment,
    /// and the heavy tool must then run.
    /// </summary>
    private sealed class FreeTurnProbe
    {
        private int _inside;
        private int _maximum;

        public IMcpTool[] Tools =>
            new IMcpTool[]
            {
                new FakeTool(OverlapToolName, async (_, _) =>
                {
                    int now = Interlocked.Increment(ref _inside);
                    int seen;
                    while (now > (seen = Volatile.Read(ref _maximum)) && Interlocked.CompareExchange(ref _maximum, now, seen) != seen)
                    {
                    }

                    await Task.Delay(300);
                    Interlocked.Decrement(ref _inside);
                    return "{}";
                }),
                Instant(ProbeHeavyToolName, heavy: true),
            };

        public async Task AssertEveryTurnIsFreeAsync(McpMessageDispatcher dispatcher, int firstId)
        {
            Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, OverlapToolName, id: firstId);
            Task<JsonDocument?> second = McpTestSupport.CallAsync(dispatcher, OverlapToolName, id: firstId + 1);
            (await first)!.Dispose();
            (await second)!.Dispose();

            Assert.Equal(2, Volatile.Read(ref _maximum));
            Assert.True(await SucceedsAsync(dispatcher, ProbeHeavyToolName, firstId + 2));
        }
    }

    private static FakeTool Instant(string name, bool heavy = false) => new(name, (_, _) => Task.FromResult("{}"), isHeavy: heavy);

    private static async Task<bool> SucceedsAsync(McpMessageDispatcher dispatcher, string tool, int id)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, id: id))!;
        return !McpTestSupport.IsError(response);
    }

    [Fact]
    public async Task TwoLightCallsRunTogetherByDefaultAndAThirdIsServerBusy()
    {
        var a = new Held();
        var b = new Held();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { a.Tool("sa_a"), b.Tool("sa_b"), Instant("sa_c") }, new McpLimits { QueueWaitSeconds = 0 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_a", id: 1);
        Task<JsonDocument?> second = McpTestSupport.CallAsync(dispatcher, "sa_b", id: 2);
        await Task.WhenAll(a.Started, b.Started).WaitAsync(TimeSpan.FromSeconds(30)); // both run at the same moment
        using JsonDocument third = (await McpTestSupport.CallAsync(dispatcher, "sa_c", id: 3))!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(third));
        a.Release();
        b.Release();
        (await first)!.Dispose();
        (await second)!.Dispose();
    }

    [Fact]
    public async Task OneSharedSlotMakesLightCallsStrictlyOneAtATime()
    {
        var a = new Held();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { a.Tool("sa_a"), Instant("sa_b") }, new McpLimits { QueueWaitSeconds = 0, MaxConcurrentReadOnlyCalls = 1 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_a", id: 1);
        await a.Started.WaitAsync(TimeSpan.FromSeconds(30));
        using JsonDocument busy = (await McpTestSupport.CallAsync(dispatcher, "sa_b", id: 2))!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(busy));
        a.Release();
        (await first)!.Dispose();
    }

    [Fact]
    public async Task AHeavyCallWaitsForTheRunningLightCallAndThenRunsAloneWhileALightCallIsRefused()
    {
        var light = new Held();
        var heavy = new Held();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { light.Tool("sa_light"), heavy.Tool("sa_heavy", heavy: true), Instant("sa_other") }, new McpLimits { QueueWaitSeconds = 2 });

        Task<JsonDocument?> lightCall = McpTestSupport.CallAsync(dispatcher, "sa_light", id: 1);
        await light.Started.WaitAsync(TimeSpan.FromSeconds(30));
        Task<JsonDocument?> heavyCall = McpTestSupport.CallAsync(dispatcher, "sa_heavy", id: 2);
        await Task.Delay(300);
        Assert.False(heavy.IsStarted); // it waits for the light call

        light.Release();
        await heavy.Started.WaitAsync(TimeSpan.FromSeconds(30));
        using JsonDocument refused = (await McpTestSupport.CallAsync(dispatcher, "sa_other", id: 3))!; // no shared slot is free while the heavy call runs

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(refused));
        heavy.Release();
        (await lightCall)!.Dispose();
        (await heavyCall)!.Dispose();
    }

    [Fact]
    public async Task AnExecutionToolIsExclusiveLikeAHeavyTool()
    {
        var light = new Held();
        var execution = new Held();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { light.Tool("sa_light"), execution.Tool("sa_execution", execution: true), Instant("sa_other") }, new McpLimits { QueueWaitSeconds = 0 });

        Task<JsonDocument?> lightCall = McpTestSupport.CallAsync(dispatcher, "sa_light", id: 1);
        await light.Started.WaitAsync(TimeSpan.FromSeconds(30));
        using JsonDocument executionRefused = (await McpTestSupport.CallAsync(dispatcher, "sa_execution", id: 2))!; // needs every slot, one is taken

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(executionRefused));
        light.Release();
        (await lightCall)!.Dispose();

        Task<JsonDocument?> executionCall = McpTestSupport.CallAsync(dispatcher, "sa_execution", id: 3);
        await execution.Started.WaitAsync(TimeSpan.FromSeconds(30));
        using JsonDocument lightRefused = (await McpTestSupport.CallAsync(dispatcher, "sa_other", id: 4))!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(lightRefused));
        execution.Release();
        (await executionCall)!.Dispose();
    }

    [Fact]
    public async Task AHeavyCallThatGivesUpWaitingReturnsEveryTurnItTook()
    {
        var holder = new Held();
        var probe = new FreeTurnProbe();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { holder.Tool("sa_holder"), Instant("sa_heavy", heavy: true) }.Concat(probe.Tools), new McpLimits { QueueWaitSeconds = 1 });

        Task<JsonDocument?> holding = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        await holder.Started.WaitAsync(TimeSpan.FromSeconds(30));
        using JsonDocument busy = (await McpTestSupport.CallAsync(dispatcher, "sa_heavy", id: 2))!; // took the exclusive turn and one free slot, then ran out of time

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(busy));
        holder.Release();
        (await holding)!.Dispose();
        await probe.AssertEveryTurnIsFreeAsync(dispatcher, firstId: 10);
    }

    [Fact]
    public async Task ACancelledQueuedHeavyCallNeverRunsAndReturnsEveryTurnItTook()
    {
        var holder = new Held();
        var probe = new FreeTurnProbe();
        int heavyRuns = 0;
        var heavy = new FakeTool("sa_heavy", (_, _) => { Interlocked.Increment(ref heavyRuns); return Task.FromResult("{}"); }, isHeavy: true);
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { holder.Tool("sa_holder"), heavy }.Concat(probe.Tools), new McpLimits { QueueWaitSeconds = 30 });

        Task<JsonDocument?> holding = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        await holder.Started.WaitAsync(TimeSpan.FromSeconds(30));
        Task<JsonDocument?> waiting = McpTestSupport.CallAsync(dispatcher, "sa_heavy", id: 2);
        await Task.Delay(300);
        await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", CancellationToken.None);

        Assert.Null(await waiting);
        Assert.Equal(0, heavyRuns);
        holder.Release();
        (await holding)!.Dispose();
        await probe.AssertEveryTurnIsFreeAsync(dispatcher, firstId: 10);
    }

    [Fact]
    public async Task ALightCallThatCannotBeCancelledKeepsOnlyItsOwnSlot()
    {
        using var release = new SemaphoreSlim(0);
        var stubborn = new FakeTool("sa_stubborn", async (_, _) => { await release.WaitAsync(); return "{}"; });
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { stubborn, Instant("sa_light"), Instant("sa_heavy", heavy: true) }, new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1, QueueWaitSeconds = 0 });

        using JsonDocument timedOut = (await McpTestSupport.CallAsync(dispatcher, "sa_stubborn", id: 1))!;
        bool lightRuns = await SucceedsAsync(dispatcher, "sa_light", 2); // the second slot is free
        using JsonDocument heavyBusy = (await McpTestSupport.CallAsync(dispatcher, "sa_heavy", id: 3))!; // but a heavy call needs both

        Assert.Equal("EXECUTION_TIMEOUT", McpTestSupport.ErrorCodeOf(timedOut));
        Assert.True(lightRuns);
        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(heavyBusy));
        release.Release();
        await McpTestSupport.WaitUntilAsync(() => SucceedsAsync(dispatcher, "sa_heavy", 4).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task QueuedHeavyCallIsNotStarvedByContinuouslyArrivingLightCalls()
    {
        var light1 = new Held();
        var heavy = new Held();
        var lightQueued = new Held();
        var probe = new FreeTurnProbe();
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { light1.Tool("sa_light_1"), heavy.Tool("sa_heavy", heavy: true), lightQueued.Tool("sa_light_queued") }.Concat(probe.Tools),
            new McpLimits { QueueWaitSeconds = 10, MaxConcurrentReadOnlyCalls = 2 });

        // 1. Light call 1 holds 1 of 2 read-only slots.
        Task<JsonDocument?> callLight1 = McpTestSupport.CallAsync(dispatcher, "sa_light_1", id: 1);
        await light1.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // 2. Heavy call enters _arrivalGate and waits for light1 to finish.
        Task<JsonDocument?> callHeavy = McpTestSupport.CallAsync(dispatcher, "sa_heavy", id: 2);
        await Task.Delay(150);
        Assert.False(heavy.IsStarted);

        // 3. Subsequent light calls arrive while heavy is waiting; they must queue behind heavy on _arrivalGate.
        Task<JsonDocument?> callLight2 = McpTestSupport.CallAsync(dispatcher, "sa_light_queued", id: 3);
        Task<JsonDocument?> callLight3 = McpTestSupport.CallAsync(dispatcher, "sa_light_queued", id: 4);
        await Task.Delay(150);
        Assert.False(lightQueued.IsStarted);

        // 4. Releasing light1 lets heavy acquire the exclusive turn BEFORE light2/light3 run.
        light1.Release();
        await heavy.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(lightQueued.IsStarted);

        heavy.Release();
        await lightQueued.Started.WaitAsync(TimeSpan.FromSeconds(10));
        lightQueued.Release();

        (await callLight1)!.Dispose();
        (await callHeavy)!.Dispose();
        (await callLight2)!.Dispose();
        (await callLight3)!.Dispose();
        await probe.AssertEveryTurnIsFreeAsync(dispatcher, firstId: 20);
    }
}
