using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>Argument checking, error mapping, path scrubbing, serial execution, time limits and cancellation of tool calls.</summary>
public sealed class ToolInfrastructureTests
{
    private static readonly string ExampleSchema = ToolSchema.Object()
        .String("symbol", "s", required: true)
        .String("frame", "f", allowedValues: new[] { "D1", "W1" })
        .Integer("count", "c")
        .Boolean("flag", "b")
        .StringArray("symbols", "list", minItems: 1)
        .Build();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolException Rejects(string argumentsJson)
        => Assert.Throws<ToolException>(() => ToolArgumentValidator.Validate(Parse(ExampleSchema), Parse(argumentsJson)));

    [Fact]
    public void TheArgumentsOfAValidCallPass()
    {
        ToolArgumentValidator.Validate(Parse(ExampleSchema), Parse("""{"symbol":"MSFT","frame":"W1","count":5,"flag":true,"symbols":["A","B"]}"""));
    }

    [Fact]
    public void AnUnknownArgumentIsNamedBeforeAnythingElseIsChecked()
    {
        ToolException error = Rejects("""{"symbol":"MSFT","extra":1}""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("extra", error.Parameter);
    }

    [Theory]
    [InlineData("{}", "symbol")]
    [InlineData("""{"symbol":5}""", "symbol")]
    [InlineData("""{"symbol":null}""", "symbol")]
    [InlineData("""{"symbol":"A","frame":"M5"}""", "frame")]
    [InlineData("""{"symbol":"A","frame":1}""", "frame")]
    [InlineData("""{"symbol":"A","count":1.5}""", "count")]
    [InlineData("""{"symbol":"A","count":"5"}""", "count")]
    [InlineData("""{"symbol":"A","flag":"yes"}""", "flag")]
    [InlineData("""{"symbol":"A","symbols":[]}""", "symbols")]
    [InlineData("""{"symbol":"A","symbols":"A"}""", "symbols")]
    [InlineData("""{"symbol":"A","symbols":["A",3]}""", "symbols[1]")]
    public void AWrongShapeIsInvalidArgumentNamingTheArgument(string argumentsJson, string parameter)
    {
        ToolException error = Rejects(argumentsJson);

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(parameter, error.Parameter);
    }

    [Fact]
    public void AnEnumViolationListsTheAllowedValues()
    {
        ToolException error = Rejects("""{"symbol":"A","frame":"M5"}""");

        Assert.Contains("D1, W1", error.Hint);
    }

    [Fact]
    public void NoArgumentsAtAllIsAnEmptyObject()
    {
        string schema = ToolSchema.Object().Integer("count", "c").Build();

        ToolArgumentValidator.Validate(Parse(schema), default);
    }

    [Fact]
    public void TheSchemaBuilderWritesAnObjectThatForbidsExtraProperties()
    {
        JsonElement schema = Parse(ExampleSchema);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "symbol" }, schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
    }

    [Fact]
    public void TheErrorCatalogueIsTheClosedSetOfThePlan()
    {
        string[] expected =
        {
            "INVALID_ARGUMENT", "SYMBOL_NOT_FOUND", "UNSUPPORTED_TIMEFRAME", "UNKNOWN_INDICATOR", "INVALID_INDICATOR_PARAMETER", "INVALID_OUTPUT_SERIES",
            "CONDITION_LIMIT_EXCEEDED", "LIMIT_EXCEEDED", "INSUFFICIENT_MARKET_DATA", "TEMPLATE_NOT_FOUND", "TEMPLATE_MIGRATION_PENDING",
            "EXECUTION_TIMEOUT", "SERVER_BUSY", "FORBIDDEN_OPERATION", "DATA_UNAVAILABLE", "GROUP_NOT_FOUND", "INTERNAL_ERROR",
            // Added with the execution tools (plan 1, section 1.6); the codes above are unchanged.
            "EXECUTION_BUSY", "JOB_NOT_FOUND", "ARTIFACT_NOT_FOUND", "PYTHON_ENV_MISSING",
            // Added with the path-length check of the sandbox (decision 3-A of the MCP decisions program).
            "SANDBOX_PATH_TOO_LONG",
        };

        Assert.Equal(expected.OrderBy(c => c), ToolErrorCodes.All.OrderBy(c => c));
        Assert.DoesNotContain("INDICATOR_NOT_AVAILABLE_HEADLESS", ToolErrorCodes.All);
    }

    [Theory]
    [InlineData("Cannot read C:\\Users\\someone\\Data\\Daily\\MSFT.parquet now")]
    [InlineData("Cannot read C:/Users/someone/Data/Daily/MSFT.parquet now")]
    [InlineData("Cannot read \\\\server\\share\\Data\\MSFT.parquet now")]
    [InlineData("Cannot read /home/someone/Data/MSFT.parquet now")]
    [InlineData("Cannot read /Users/someone/Data/MSFT.parquet now")]
    public void PathShapedTextIsReplaced(string text)
    {
        string sanitized = new ErrorMessageSanitizer(Array.Empty<string?>()).Sanitize(text);

        Assert.Equal("Cannot read <path> now", sanitized);
    }

    [Fact]
    public void KnownRootsAreReplacedInEitherSlashStyleAndIgnoringCase()
    {
        string root = Path.Combine(Path.GetTempPath(), "SomeRootFolder", "Data");
        var sanitizer = new ErrorMessageSanitizer(new[] { root, null, " " });

        Assert.Equal("a <path>\\x.json b", sanitizer.Sanitize($"a {root}\\x.json b"));
        Assert.DoesNotContain("SomeRootFolder", sanitizer.Sanitize($"at {root.ToUpperInvariant()} and {root.Replace('\\', '/')}"));
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        Assert.Equal("Symbol 'MSFT' is not available.", new ErrorMessageSanitizer(new[] { AppContext.BaseDirectory }).Sanitize("Symbol 'MSFT' is not available."));
    }

    private static FakeTool Tool(string name, Func<JsonElement, CancellationToken, Task<string>> invoke, string? schema = null, bool heavy = false)
        => new(name, invoke, schema, heavy);

    [Fact]
    public async Task ASuccessCarriesTheStructuredJsonAsTextAndAsStructuredContent()
    {
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { Tool("sa_ok", (_, _) => Task.FromResult("""{"value":1.5,"name":"x"}""")) });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_ok"))!;

        JsonElement result = McpTestSupport.Result(response);
        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal("text", result.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("""{"value":1.5,"name":"x"}""", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1.5m, result.GetProperty("structuredContent").GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task AToolExceptionBecomesAnIsErrorResultWithCodeMessageParamAndHint()
    {
        var dispatcher = await McpTestSupport.InitializedAsync(new[]
        {
            Tool("sa_fail", (_, _) => throw new ToolException(ToolErrorCodes.SymbolNotFound, "Nope.", "symbol", "Try another.")),
        });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_fail"))!;

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("SYMBOL_NOT_FOUND", error.GetProperty("code").GetString());
        Assert.Equal("Nope.", error.GetProperty("message").GetString());
        Assert.Equal("symbol", error.GetProperty("param").GetString());
        Assert.Equal("Try another.", error.GetProperty("hint").GetString());
        Assert.Equal(McpTestSupport.Result(response).GetProperty("content")[0].GetProperty("text").GetString(), error.GetRawText());
    }

    [Fact]
    public async Task AnUnexpectedExceptionIsInternalErrorWithTheFixedMessageAndNoDetails()
    {
        var log = new StringWriter();
        var dispatcher = McpTestSupport.CreateDispatcher(
            new[] { Tool("sa_boom", (_, _) => throw new InvalidOperationException("secret C:\\Users\\someone\\x.txt detail")) }, log: log);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_boom"))!;

        Assert.Equal("INTERNAL_ERROR", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(ToolErrorCodes.UnexpectedErrorMessage, McpTestSupport.Structured(response).GetProperty("message").GetString());
        Assert.DoesNotContain("secret", response.RootElement.GetRawText());
        Assert.Contains("secret", log.ToString());
    }

    [Fact]
    public async Task ErrorTextIsScrubbedOfPathsAndKnownRoots()
    {
        string root = Path.Combine(Path.GetTempPath(), "ScrubMeRoot");
        var dispatcher = await McpTestSupport.InitializedAsync(
            new[] { Tool("sa_leak", (_, _) => throw new ToolException(ToolErrorCodes.DataUnavailable, $"Cannot read {root}\\a.parquet and C:\\Other\\b.txt", hint: $"see {root}")) },
            roots: new[] { root });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_leak"))!;

        JsonElement error = McpTestSupport.Structured(response);
        string text = error.GetProperty("message").GetString() + " " + error.GetProperty("hint").GetString();
        Assert.DoesNotContain("ScrubMeRoot", text);
        Assert.DoesNotContain("Other", text);
        Assert.Contains("<path>", text);
    }

    [Fact]
    public async Task AnUnknownArgumentFailsBeforeTheToolRuns()
    {
        bool ran = false;
        var dispatcher = await McpTestSupport.InitializedAsync(new[]
        {
            Tool("sa_strict", (_, _) => { ran = true; return Task.FromResult("{}"); }, ToolSchema.Object().Integer("count", "c").Build()),
        });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_strict", """{"count":1,"cuont":2}"""))!;

        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("cuont", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.False(ran);
    }

    [Fact]
    public async Task ToolsListShowsEveryToolWithItsSchemaAndTheReadOnlyAnnotationsInNameOrder()
    {
        var dispatcher = await McpTestSupport.InitializedAsync(new[]
        {
            Tool("sa_b", (_, _) => Task.FromResult("{}")),
            Tool("sa_a", (_, _) => Task.FromResult("{}"), ToolSchema.Object().Integer("n", "n").Build()),
        });

        using var response = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement[] tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sa_a", "sa_b" }, tools.Select(t => t.GetProperty("name").GetString()).ToArray());
        Assert.All(tools, tool =>
        {
            JsonElement annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
        });
    }

    [Fact]
    public async Task ACooperativeToolThatRunsPastTheLimitGetsExecutionTimeout()
    {
        var dispatcher = await McpTestSupport.InitializedAsync(
            new[] { Tool("sa_slow", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "{}"; }) },
            new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1 });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_slow"))!;

        Assert.Equal("EXECUTION_TIMEOUT", McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task AHeavyToolUsesTheHeavyLimit()
    {
        var dispatcher = await McpTestSupport.InitializedAsync(
            new[] { Tool("sa_heavy", async (_, _) => { await Task.Delay(1500); return "{}"; }, heavy: true) },
            new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 5 });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_heavy"))!;

        Assert.False(McpTestSupport.IsError(response));
    }

    [Fact]
    public async Task AToolThatCannotBeCancelledKeepsTheTurnSoTheNextCallIsServerBusyAndLaterOnesRecover()
    {
        using var release = new SemaphoreSlim(0);
        int runs = 0;
        var stubborn = Tool("sa_stubborn", async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            await release.WaitAsync(); // ignores the cancellation token
            return "{}";
        });
        var after = Tool("sa_after", (_, _) => Task.FromResult("{}"));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { stubborn, after }, new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1, QueueWaitSeconds = 0 });

        using var timedOut = (await McpTestSupport.CallAsync(dispatcher, "sa_stubborn", id: 1))!;
        using var busy = (await McpTestSupport.CallAsync(dispatcher, "sa_after", id: 2))!;

        Assert.Equal("EXECUTION_TIMEOUT", McpTestSupport.ErrorCodeOf(timedOut));
        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(busy));
        Assert.Equal(1, runs);

        release.Release();
        JsonDocument? later = null;
        await McpTestSupport.WaitUntilAsync(() =>
        {
            later?.Dispose();
            later = McpTestSupport.CallAsync(dispatcher, "sa_after", id: 3).GetAwaiter().GetResult();
            return later is not null && !McpTestSupport.IsError(later);
        });
        later?.Dispose();
    }

    [Fact]
    public async Task ASecondCallWaitsForItsTurnWithinTheQueueWaitAndThenRuns()
    {
        var order = new List<string>();
        var tool = Tool("sa_serial", async (arguments, _) =>
        {
            string label = arguments.GetProperty("label").GetString()!;
            lock (order) { order.Add("start " + label); }
            await Task.Delay(300);
            lock (order) { order.Add("end " + label); }
            return "{}";
        }, ToolSchema.Object().String("label", "l", required: true).Build());
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { tool }, new McpLimits { QueueWaitSeconds = 10 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_serial", """{"label":"a"}""", id: 1);
        Task<JsonDocument?> second = McpTestSupport.CallAsync(dispatcher, "sa_serial", """{"label":"b"}""", id: 2);
        using var firstResponse = (await first)!;
        using var secondResponse = (await second)!;

        Assert.False(McpTestSupport.IsError(firstResponse));
        Assert.False(McpTestSupport.IsError(secondResponse));
        Assert.Equal(4, order.Count);
        Assert.Equal(order[0].Replace("start", "end"), order[1]); // never two calls at once
    }

    [Fact]
    public async Task ACallThatWaitsLongerThanTheQueueWaitIsServerBusy()
    {
        var tool = Tool("sa_hold", async (_, _) => { await Task.Delay(2500); return "{}"; });
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { tool }, new McpLimits { QueueWaitSeconds = 1 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_hold", id: 1);
        using var second = (await McpTestSupport.CallAsync(dispatcher, "sa_hold", id: 2))!;
        using var firstResponse = (await first)!;

        Assert.Equal("SERVER_BUSY", McpTestSupport.ErrorCodeOf(second));
        Assert.False(McpTestSupport.IsError(firstResponse));
    }

    [Fact]
    public async Task ACancellationNotificationStopsARunningCallWithoutAnyResponseAndFreesTheTurn()
    {
        using var started = new SemaphoreSlim(0);
        var slow = Tool("sa_wait", async (_, ct) => { started.Release(); await Task.Delay(Timeout.Infinite, ct); return "{}"; });
        var quick = Tool("sa_quick", (_, _) => Task.FromResult("""{"done":true}"""));
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { slow, quick }, new McpLimits { QueueWaitSeconds = 10 });

        Task<JsonDocument?> call = McpTestSupport.CallAsync(dispatcher, "sa_wait", id: 41);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":41}}""", CancellationToken.None);

        Assert.Null(await call);
        using var next = (await McpTestSupport.CallAsync(dispatcher, "sa_quick", id: 42))!;
        Assert.False(McpTestSupport.IsError(next));
    }

    [Fact]
    public async Task ACancelledQueuedCallNeverRunsAndNeverAnswers()
    {
        using var started = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        int queuedRuns = 0;
        var holder = Tool("sa_holder", async (_, _) => { started.Release(); await release.WaitAsync(); return "{}"; });
        var queued = Tool("sa_queued", (_, _) => { Interlocked.Increment(ref queuedRuns); return Task.FromResult("{}"); });
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { holder, queued }, new McpLimits { QueueWaitSeconds = 30 });

        Task<JsonDocument?> first = McpTestSupport.CallAsync(dispatcher, "sa_holder", id: 1);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        Task<JsonDocument?> waiting = McpTestSupport.CallAsync(dispatcher, "sa_queued", id: 2);
        await Task.Delay(200);
        await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", CancellationToken.None);

        Assert.Null(await waiting);
        release.Release();
        using var firstResponse = (await first)!;
        Assert.False(McpTestSupport.IsError(firstResponse));
        Assert.Equal(0, queuedRuns);
    }

    [Fact]
    public async Task AResultLargerThanTheResponseLimitBecomesLimitExceeded()
    {
        var big = Tool("sa_big", (_, _) => Task.FromResult("{\"text\":\"" + new string('x', 70_000) + "\"}"));
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { big }, new McpLimits { MaxResponseBytes = 64 * 1024 });

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_big"))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        Assert.True(response.RootElement.GetRawText().Length < 1000);
    }
}
