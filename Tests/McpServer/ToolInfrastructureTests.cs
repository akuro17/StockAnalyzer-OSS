using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
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
            // Added with the arithmetic-failure handling of the backtest tools (owner decision D-8 = C, 2026-11-04).
            "BACKTEST_ARITHMETIC_OVERFLOW",
            // Added with the discussion board (--enable-discussion), appended at the end in this order.
            "DISCUSSION_NOT_FOUND", "DISCUSSION_CLOSED", "PARTICIPANT_NOT_JOINED", "NOT_YOUR_TURN", "RATE_LIMITED", "HUB_RESET", "HUB_UNAVAILABLE",
        };

        Assert.Equal(expected.OrderBy(c => c), ToolErrorCodes.All.OrderBy(c => c));
        Assert.Equal(expected.TakeLast(7), ToolErrorCodes.All.TakeLast(7)); // appended at the END, in the documented order
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

    [Theory]
    [InlineData("Cannot read /opt/app/data/7203.parquet now")]
    [InlineData("Cannot read /srv/data/7203.parquet now")]
    [InlineData("Cannot read /etc/stockanalyzer/app.yaml now")]
    [InlineData("Cannot read /mnt/nas/Data/MSFT.parquet now")]
    [InlineData("Cannot read /media/usb/Data/MSFT.parquet now")]
    [InlineData("Cannot read /usr/lib/onnx/libx.so now")]
    [InlineData("Cannot read /root/.cache/m.onnx now")]
    [InlineData("Cannot read /proc/1/status now")]
    [InlineData("Cannot read /run/user/1000/x.sock now")]
    [InlineData("Cannot read /var/lib/sa/m.onnx now")]
    [InlineData("Cannot read file:///i:/stock/tickers.json now")]
    [InlineData("Cannot read file:///opt/app/x.json now")]
    public void UnixAndFileUriPathShapesAreReplaced(string text)
    {
        Assert.Equal("Cannot read <path> now", new ErrorMessageSanitizer(Array.Empty<string?>()).Sanitize(text));
    }

    [Theory]
    [InlineData("Cannot read \"/opt/app/m.onnx\" now", "Cannot read \"<path>\" now")]
    [InlineData("Cannot read path=/opt/app/m.onnx now", "Cannot read path=<path> now")]
    [InlineData("see (/srv/data/x.parquet)", "see (<path>")]
    public void APathInsideQuotesOrAfterAnEqualsSignIsReplacedWhereItStarts(string text, string expected)
    {
        Assert.Equal(expected, new ErrorMessageSanitizer(Array.Empty<string?>()).Sanitize(text));
    }

    [Theory]
    [InlineData("P/E or EV/EBITDA ratio")]
    [InlineData("pair 7203.T/9984.T is not registered")]
    [InlineData("date 2024/01/05 is not a trading day")]
    [InlineData("Choose buy/sell or Up/Down/Neutral")]
    [InlineData("rate 1/2 and 3/4 of the window")]
    [InlineData("the /etc folder")]
    [InlineData("opt/app is relative")]
    [InlineData("Use sa_get_candles with timeFrame D1/W1/MN1")]
    public void OrdinaryTextWithSlashesIsLeftAlone(string text)
    {
        Assert.Equal(text, new ErrorMessageSanitizer(Array.Empty<string?>()).Sanitize(text));
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
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { stubborn, after }, new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1, QueueWaitSeconds = 0, MaxConcurrentReadOnlyCalls = 1 });

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
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { tool }, new McpLimits { QueueWaitSeconds = 10, MaxConcurrentReadOnlyCalls = 1 });

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
        var dispatcher = await McpTestSupport.InitializedAsync(new[] { tool }, new McpLimits { QueueWaitSeconds = 1, MaxConcurrentReadOnlyCalls = 1 });

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
        var dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { holder, queued }, new McpLimits { QueueWaitSeconds = 30, MaxConcurrentReadOnlyCalls = 1 });

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

    private sealed class RecordingObserver : IToolCallObserver
    {
        public readonly List<ToolCallRecord> Records = new();
        public void OnToolCall(ToolCallRecord record) => Records.Add(record);
    }

    [Fact]
    public async Task APreExecutionExceptionReturnsInternalErrorInsteadOfHangingAndNotifiesObserver()
    {
        const string badSchema = """{"type":"object","properties":{"x":{"type":"unsupported_schema_type"}},"additionalProperties":false}""";
        var badTool = Tool("sa_bad_schema", (_, _) => Task.FromResult("{}"), badSchema);
        var observer = new RecordingObserver();
        var sink = new LogSink(new StringWriter());
        var executor = new ToolExecutor(new McpLimits(), new ErrorMessageSanitizer(Array.Empty<string?>()), sink, observer);
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(new[] { badTool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_bad_schema", """{"x":1}"""))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InternalError, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(ToolErrorCodes.UnexpectedErrorMessage, McpTestSupport.Structured(response).GetProperty("message").GetString());
        ToolCallRecord record = Assert.Single(observer.Records);
        Assert.Equal(ToolErrorCodes.InternalError, record.Outcome);
    }

    [Theory]
    [InlineData(McpResponseFormat.Dual)]
    [InlineData(McpResponseFormat.TextOnly)]
    public async Task AnOversizedErrorEnvelopeIsBoundedByMaxResponseBytesAndMaxResponseTokens(McpResponseFormat format)
    {
        var limits = new McpLimits { MaxResponseBytes = 65_536, MaxResponseTokens = 1_024 };
        string bigMessage = string.Concat(Enumerable.Repeat("M😀\"\\\n", 1_500));
        string bigParam = string.Concat(Enumerable.Repeat("p😀", 1_500));
        string bigHint = string.Concat(Enumerable.Repeat("h😀", 1_500));

        var sink = new LogSink(new StringWriter());
        var tools = new IMcpTool[]
        {
            Tool("sa_huge_err", (_, _) => throw new ToolException(ToolErrorCodes.InvalidArgument, bigMessage, bigParam, bigHint)),
            Tool("sa_strict_huge", (_, _) => Task.FromResult("{}"), ToolSchema.Object().Integer("count", "c").Build()),
        };
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), sink, responseFormat: format);
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(tools), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        // 1. Huge ToolException fields
        string rawCall = McpTestSupport.CallMessage(10, "sa_huge_err");
        string rawResult = (await dispatcher.DispatchAsync(rawCall, CancellationToken.None))!;
        using var doc = JsonDocument.Parse(rawResult);
        JsonElement callResult = doc.RootElement.GetProperty("result");
        string callResultRaw = callResult.GetRawText();
        int envelopeBytes = System.Text.Encoding.UTF8.GetByteCount(callResultRaw);
        Assert.True(envelopeBytes <= limits.MaxResponseBytes, $"Envelope {envelopeBytes} > {limits.MaxResponseBytes}");
        Assert.True(((envelopeBytes + 3L) / 4L) <= limits.MaxResponseTokens, $"Tokens {((envelopeBytes + 3L) / 4L)} > {limits.MaxResponseTokens}");

        Assert.True(callResult.GetProperty("isError").GetBoolean());
        string payloadText = callResult.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var payloadDoc = JsonDocument.Parse(payloadText);
        JsonElement errObj = payloadDoc.RootElement;
        Assert.Equal(ToolErrorCodes.InvalidArgument, errObj.GetProperty("code").GetString());
        string returnedMessage = errObj.GetProperty("message").GetString()!;
        Assert.DoesNotContain('\uFFFD', returnedMessage);
        if (errObj.TryGetProperty("param", out JsonElement p))
        {
            string s = p.GetString()!;
            Assert.True(s.Length >= 16);
            Assert.DoesNotContain('\uFFFD', s);
        }

        if (errObj.TryGetProperty("hint", out JsonElement h))
        {
            string s = h.GetString()!;
            Assert.True(s.Length >= 16);
            Assert.DoesNotContain('\uFFFD', s);
        }

        // 2. Oversized unknown argument name echoed by ToolArgumentValidator
        string hugeKey = new('k', 10_000);
        string rawUnknown = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(11, "sa_strict_huge", $"{{\"{hugeKey}\":1}}"), CancellationToken.None))!;
        using var unknownDoc = JsonDocument.Parse(rawUnknown);
        JsonElement unknownResult = unknownDoc.RootElement.GetProperty("result");
        string unknownResultRaw = unknownResult.GetRawText();
        int unknownBytes = System.Text.Encoding.UTF8.GetByteCount(unknownResultRaw);
        Assert.True(unknownBytes <= limits.MaxResponseBytes);
        Assert.True(((unknownBytes + 3L) / 4L) <= limits.MaxResponseTokens);
        using var unknownPayload = JsonDocument.Parse(unknownResult.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(ToolErrorCodes.InvalidArgument, unknownPayload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnErrorEnvelopeOmitsHintAndParamWhenFewerThanSixteenCharsFit()
    {
        // Tight token budget (1024 tokens = 4096 bytes) in Dual mode: ~1980 chars in message fills budget so param/hint cannot fit >= 16 chars and must be omitted.
        var limits = new McpLimits { MaxResponseBytes = 65_536, MaxResponseTokens = 1_024 };
        string nearLimitMessage = new('m', 1_980);
        string longParam = new('p', 500);
        string longHint = new('h', 500);

        var sink = new LogSink(new StringWriter());
        var tool = Tool("sa_tight_err", (_, _) => throw new ToolException(ToolErrorCodes.InvalidArgument, nearLimitMessage, longParam, longHint));
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), sink, responseFormat: McpResponseFormat.Dual);
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(new[] { tool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        string raw = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(12, "sa_tight_err"), CancellationToken.None))!;
        using var doc = JsonDocument.Parse(raw);
        string resultRaw = doc.RootElement.GetProperty("result").GetRawText();
        int bytes = System.Text.Encoding.UTF8.GetByteCount(resultRaw);
        Assert.True(((bytes + 3L) / 4L) <= limits.MaxResponseTokens);

        JsonElement errObj = McpTestSupport.Structured(doc);
        Assert.Equal(ToolErrorCodes.InvalidArgument, errObj.GetProperty("code").GetString());
        Assert.False(errObj.TryGetProperty("hint", out _));
        Assert.False(errObj.TryGetProperty("param", out _));
    }

    private const long BytesPerEstimatedToken = 4L;
    private const int SweepHalfWidthChars = 40;
    private const int SweepStepChars = 4;

    [Fact]
    public async Task AnErrorEnvelopeOmitsSurrogateHintWhenBackingOffDropsLengthBelowSixteen()
    {
        var limits = new McpLimits { MaxResponseBytes = 65_536, MaxResponseTokens = 1_024 };
        const string surrogateHint = "0123456789ab😀😀xyz";
        int currentLength = 0;

        var sink = new LogSink(new StringWriter());
        var tool = Tool("sa_surrogate_err", (_, _) => throw new ToolException(
            ToolErrorCodes.InvalidArgument,
            new string('m', currentLength),
            parameter: null,
            hint: surrogateHint));
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), sink, responseFormat: McpResponseFormat.TextOnly);
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(new[] { tool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        bool sawFullOrSeventeen = false;
        bool sawOmittedAfterTightening = false;
        // The hint is backed off where the message plus the fixed overhead of the call result (code, hint, JSON-RPC wrapper) reaches the token budget: measure that
        // overhead with an empty message instead of hard-coding the boundary, and sweep around it.
        currentLength = 0;
        using JsonDocument baseline = JsonDocument.Parse((await dispatcher.DispatchAsync(McpTestSupport.CallMessage(0, "sa_surrogate_err"), CancellationToken.None))!);
        int overheadBytes = System.Text.Encoding.UTF8.GetByteCount(baseline.RootElement.GetProperty("result").GetRawText());
        int boundary = (int)(limits.MaxResponseTokens * BytesPerEstimatedToken) - overheadBytes;

        for (int len = boundary - SweepHalfWidthChars; len <= boundary + SweepHalfWidthChars; len += SweepStepChars)
        {
            currentLength = len;
            string raw = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(len, "sa_surrogate_err"), CancellationToken.None))!;
            using var doc = JsonDocument.Parse(raw);
            string resultRaw = doc.RootElement.GetProperty("result").GetRawText();
            int bytes = System.Text.Encoding.UTF8.GetByteCount(resultRaw);
            Assert.True(((bytes + 3L) / 4L) <= limits.MaxResponseTokens);

            // TextOnly mode emits no structuredContent: the error envelope is the JSON text of content[0] (the executor built above uses McpResponseFormat.TextOnly on purpose:
            // the sweep sits at the token limit of ONE copy of the envelope, Dual mode would carry it twice).
            using JsonDocument envelope = JsonDocument.Parse(McpTestSupport.Result(doc).GetProperty("content")[0].GetProperty("text").GetString()!);
            JsonElement errObj = envelope.RootElement;
            if (errObj.TryGetProperty("hint", out JsonElement hintProp))
            {
                string actualHint = hintProp.GetString()!;
                Assert.True(actualHint.Length >= 16, $"Truncated hint length {actualHint.Length} dropped below 16 chars: {actualHint}");
                Assert.DoesNotContain('\uFFFD', actualHint);
                sawFullOrSeventeen = true;
            }
            else if (sawFullOrSeventeen)
            {
                sawOmittedAfterTightening = true;
            }
        }

        Assert.True(sawFullOrSeventeen);
        Assert.True(sawOmittedAfterTightening);
    }

    [Fact]
    public async Task DataUnavailableExceptionWithBlankMessageFallsBackToGenericMessage()
    {
        var sink = new LogSink(new StringWriter());
        var tool = Tool("sa_blank_unavail", (_, _) => throw new DataUnavailableException("   "));
        var executor = new ToolExecutor(new McpLimits(), new ErrorMessageSanitizer(Array.Empty<string?>()), sink);
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(new[] { tool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using var response = (await McpTestSupport.CallAsync(dispatcher, "sa_blank_unavail"))!;

        Assert.True(McpTestSupport.IsError(response));
        JsonElement errObj = McpTestSupport.Structured(response);
        Assert.Equal(ToolErrorCodes.DataUnavailable, errObj.GetProperty("code").GetString());
        Assert.Equal("Stored data is temporarily unreadable.", errObj.GetProperty("message").GetString());
        Assert.Equal("Retry in a moment.", errObj.GetProperty("hint").GetString());
    }
}
