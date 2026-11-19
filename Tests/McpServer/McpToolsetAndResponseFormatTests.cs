using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task 1: CLI <c>--toolset</c> profile partition, CLI <c>--response-format</c> (<c>dual</c> | <c>text-only</c>),
/// and runtime filtering/serialization behavior.
/// </summary>
public sealed class McpToolsetAndResponseFormatTests : IDisposable
{
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "SaMcpToolset-" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();

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

    private McpServerRuntime CreateRuntime(
        bool enableExecution = false,
        bool enableDiscussion = false,
        IReadOnlyList<string>? toolsets = null,
        McpResponseFormat responseFormat = McpResponseFormat.Dual)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),
            new LogSink(new StringWriter(), quiet: true),
            enableExecution: enableExecution,
            enableDiscussion: enableDiscussion,
            toolsets: toolsets,
            responseFormat: responseFormat);
        _runtimes.Add(runtime);
        return runtime;
    }

    // ---- Partition completeness invariant -----------------------------------------------------------------------------

    [Fact]
    public void ToolsetCatalog_SixProfilesFormExactPartitionOfAll47ReadOnlyTools()
    {
        McpServerRuntime full = CreateRuntime();
        string[] allReadOnlyNames = full.Tools.Tools.Select(t => t.Tool.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(47, allReadOnlyNames.Length);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string profile, IReadOnlySet<string> members) in McpToolsetCatalog.Profiles)
        {
            Assert.NotEmpty(members);
            foreach (string name in members)
            {
                Assert.True(seen.Add(name), $"Tool '{name}' appears in multiple profiles (duplicate in '{profile}').");
            }
        }

        Assert.Equal(allReadOnlyNames, seen.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ---- McpToolsetCatalog.TryParse -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("all", new[] { "all" })]
    [InlineData("core", new[] { "core" })]
    [InlineData("core,technical", new[] { "core", "technical" })]
    [InlineData("core,core,ml", new[] { "core", "ml" })]
    [InlineData("core,technical,screener,backtest,portfolio,ml", new[] { "core", "technical", "screener", "backtest", "portfolio", "ml" })]
    public void TryParse_AcceptsValidProfilesAndFoldsDuplicates(string raw, string[] expected)
    {
        Assert.True(McpToolsetCatalog.TryParse(raw, out IReadOnlyList<string> parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("core,")]
    [InlineData(",core")]
    [InlineData("core,,ml")]
    [InlineData("CORE")]
    [InlineData(" core")]
    [InlineData("core ")]
    [InlineData("unknown")]
    [InlineData("--help")]
    public void TryParse_RejectsEmptyMalformedOrUnknownProfiles(string raw)
    {
        Assert.False(McpToolsetCatalog.TryParse(raw, out IReadOnlyList<string> parsed));
        Assert.Empty(parsed);
    }

    // ---- McpCommandLine.Parse -----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsToolsetAndResponseFormatOptions()
    {
        McpCommandLineResult parsed = McpCommandLine.Parse(new[] { "--toolset", "core,technical", "--response-format", "text-only", "--enable-execution" });

        Assert.IsType<McpCommand.Run>(parsed.Command);
        Assert.True(parsed.EnableExecution);
        Assert.Equal(new[] { "core", "technical" }, parsed.Toolsets);
        Assert.Equal(McpResponseFormat.TextOnly, parsed.ResponseFormat);
    }

    [Fact]
    public void Parse_DefaultsAreNullToolsetsAndDualFormat()
    {
        McpCommandLineResult parsed = McpCommandLine.Parse(Array.Empty<string>());

        Assert.IsType<McpCommand.Run>(parsed.Command);
        Assert.Null(parsed.Toolsets);
        Assert.Equal(McpResponseFormat.Dual, parsed.ResponseFormat);
    }

    [Fact]
    public void Parse_AllowsHelpCombinedWithToolset()
    {
        McpCommandLineResult parsed = McpCommandLine.Parse(new[] { "--help", "--toolset", "core" });
        var help = Assert.IsType<McpCommand.Help>(parsed.Command);
        Assert.Null(help.ToolName);
        Assert.Equal(new[] { "core" }, parsed.Toolsets);

        McpCommandLineResult withTool = McpCommandLine.Parse(new[] { "--help", "sa_get_candles", "--toolset", "core" });
        var helpTool = Assert.IsType<McpCommand.Help>(withTool.Command);
        Assert.Equal("sa_get_candles", helpTool.ToolName);
        Assert.Equal(new[] { "core" }, withTool.Toolsets);
    }

    [Theory]
    [InlineData(new[] { "--toolset" }, "Invalid --toolset value ''.")]
    [InlineData(new[] { "--toolset", "" }, "Invalid --toolset value ''.")]
    [InlineData(new[] { "--toolset", "core," }, "Invalid --toolset value 'core,'.")]
    [InlineData(new[] { "--toolset", "bogus" }, "Invalid --toolset value 'bogus'.")]
    [InlineData(new[] { "--toolset", "--help" }, "Invalid --toolset value '--help'.")]
    [InlineData(new[] { "--toolset", "core", "--toolset", "ml" }, "Unknown argument '--toolset'.")]
    [InlineData(new[] { "--toolset", "core", "--version" }, "Unknown argument '--version'.")]
    [InlineData(new[] { "--version", "--toolset", "core" }, "Unknown argument '--toolset'.")]
    [InlineData(new[] { "--response-format" }, "Invalid --response-format value ''.")]
    [InlineData(new[] { "--response-format", "json" }, "Invalid --response-format value 'json'.")]
    [InlineData(new[] { "--response-format", "TEXT-ONLY" }, "Invalid --response-format value 'TEXT-ONLY'.")]
    [InlineData(new[] { "--response-format", "dual", "--response-format", "text-only" }, "Unknown argument '--response-format'.")]
    [InlineData(new[] { "--response-format", "text-only", "--version" }, "Unknown argument '--version'.")]
    public void Parse_RejectsBadToolsetOrResponseFormatArguments(string[] args, string expectedPrefix)
    {
        McpCommandLineResult parsed = McpCommandLine.Parse(args);
        var invalid = Assert.IsType<McpCommand.Invalid>(parsed.Command);
        Assert.StartsWith(expectedPrefix, invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TruncatesLongInvalidValuesInErrorMessage()
    {
        string overLong = new('x', 150);
        var invalidToolset = Assert.IsType<McpCommand.Invalid>(McpCommandLine.Parse(new[] { "--toolset", overLong }).Command);
        Assert.Contains($"'{new string('x', 100)}...'", invalidToolset.Message, StringComparison.Ordinal);

        var invalidFormat = Assert.IsType<McpCommand.Invalid>(McpCommandLine.Parse(new[] { "--response-format", overLong }).Command);
        Assert.Contains($"'{new string('x', 100)}...'", invalidFormat.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpOverview_DocumentsToolsetAndResponseFormatOptions()
    {
        McpServerRuntime runtime = CreateRuntime();
        string overview = McpHelpText.Overview(runtime.Tools, includeExecutionTools: false);

        Assert.Contains("--toolset <names>", overview);
        Assert.Contains("--response-format <mode>", overview);
    }

    // ---- Runtime tools/list filtering ---------------------------------------------------------------------------------

    [Fact]
    public async Task ToolsList_FiltersReadOnlyToolsByToolsetWhilePreservingExecutionAndDiscussionFlags()
    {
        McpServerRuntime coreOnly = CreateRuntime(toolsets: new[] { "core" });
        await coreOnly.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument coreList = JsonDocument.Parse((await coreOnly.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None))!);
        string[] coreNames = coreList.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(McpToolsetCatalog.Profiles["core"].OrderBy(n => n, StringComparer.Ordinal).ToArray(), coreNames);

        McpServerRuntime coreAndMl = CreateRuntime(toolsets: new[] { "core", "ml" });
        Assert.Equal(14, coreAndMl.Tools.Tools.Count);

        McpServerRuntime coreWithExecAndDisc = CreateRuntime(enableExecution: true, enableDiscussion: true, toolsets: new[] { "core" });
        Assert.Equal(10 + 9 + 9, coreWithExecAndDisc.Tools.Tools.Count);

        McpServerRuntime allExplicit = CreateRuntime(toolsets: new[] { "all" });
        Assert.Equal(47, allExplicit.Tools.Tools.Count);
    }

    // ---- Response format (Dual vs TextOnly) ---------------------------------------------------------------------------

    [Fact]
    public async Task ResponseFormatTextOnly_OmitsStructuredContentForSuccessAndError()
    {
        var okTool = new FakeTool("sa_ok", (_, _) => Task.FromResult("""{"value":42}"""));
        var failTool = new FakeTool("sa_fail", (_, _) => throw new ToolException(ToolErrorCodes.InvalidArgument, "Bad input.", "arg"));
        var limits = new McpLimits();
        var log = new LogSink(new StringWriter());
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), log, responseFormat: McpResponseFormat.TextOnly);
        var dispatcher = new McpMessageDispatcher(log, new McpToolRegistry(new IMcpTool[] { okTool, failTool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument okResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_ok"))!;
        JsonElement okResult = McpTestSupport.Result(okResponse);
        Assert.False(okResult.GetProperty("isError").GetBoolean());
        Assert.Equal("""{"value":42}""", okResult.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(okResult.TryGetProperty("structuredContent", out _));

        using JsonDocument failResponse = (await McpTestSupport.CallAsync(dispatcher, "sa_fail", id: 2))!;
        JsonElement failResult = McpTestSupport.Result(failResponse);
        Assert.True(failResult.GetProperty("isError").GetBoolean());
        Assert.Contains("INVALID_ARGUMENT", failResult.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(failResult.TryGetProperty("structuredContent", out _));
    }

    [Fact]
    public async Task ResponseFormatTextOnly_AccountsForSingleJsonCopyInImageSizeCheck()
    {
        // StructuredJson is 35 KB; PNG is 15 KB (Base64 ~20 KB).
        // Under Dual (2 * 35 KB + 20 KB = 90 KB > 64 KB limit) it fails LIMIT_EXCEEDED.
        // Under TextOnly (1 * 35 KB + 20 KB = 55 KB < 64 KB limit) it succeeds.
        string bigJson = "{\"pad\":\"" + new string('a', 35_000) + "\"}";
        byte[] png = new byte[15_000];
        var imageTool = new FakeImageTool("sa_img", (_, _) => Task.FromResult(new ToolImageResult(bigJson, png)));
        var limits = new McpLimits { MaxResponseBytes = 64 * 1024 };

        var dualLog = new LogSink(new StringWriter());
        var dualDispatcher = new McpMessageDispatcher(
            dualLog,
            new McpToolRegistry(new IMcpTool[] { imageTool }),
            new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), dualLog, responseFormat: McpResponseFormat.Dual));
        await dualDispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument dualResp = (await McpTestSupport.CallAsync(dualDispatcher, "sa_img"))!;
        Assert.True(McpTestSupport.IsError(dualResp));
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(dualResp));

        var textOnlyLog = new LogSink(new StringWriter());
        var textOnlyDispatcher = new McpMessageDispatcher(
            textOnlyLog,
            new McpToolRegistry(new IMcpTool[] { imageTool }),
            new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), textOnlyLog, responseFormat: McpResponseFormat.TextOnly));
        await textOnlyDispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument textResp = (await McpTestSupport.CallAsync(textOnlyDispatcher, "sa_img"))!;
        JsonElement result = McpTestSupport.Result(textResp);
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.False(result.TryGetProperty("structuredContent", out _));
        Assert.Equal(2, result.GetProperty("content").GetArrayLength());
        Assert.Equal("image", result.GetProperty("content")[1].GetProperty("type").GetString());
    }

    // ---- Real child process integration -------------------------------------------------------------------------------

    [Fact]
    public async Task ChildProcess_RunsWithToolsetAndTextOnlyResponseFormat()
    {
        using Process process = McpServerHostTests.StartServer("--toolset", "core", "--response-format", "text-only");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            async Task<JsonDocument> Ask(string line)
            {
                await process.StandardInput.WriteLineAsync(line);
                return JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(cancellation.Token))!);
            }

            using (await Ask(McpTestSupport.InitializeMessage))
            {
            }

            using JsonDocument list = await Ask("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            string[] names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(10, names.Length);
            Assert.Contains("sa_get_settings", names);
            Assert.DoesNotContain("sa_run_backtest", names);

            using JsonDocument call = await Ask(McpTestSupport.CallMessage(2, "sa_get_settings"));
            JsonElement callResult = McpTestSupport.Result(call);
            Assert.False(callResult.GetProperty("isError").GetBoolean());
            Assert.False(callResult.TryGetProperty("structuredContent", out _));
            Assert.True(Encoding.UTF8.GetByteCount(callResult.GetProperty("content")[0].GetProperty("text").GetString()!) > 0);

            process.StandardInput.Close();
            Assert.True(process.WaitForExit(60_000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed class FakeImageTool : IMcpImageTool
    {
        private readonly Func<JsonElement, CancellationToken, Task<ToolImageResult>> _invoke;

        public FakeImageTool(string name, Func<JsonElement, CancellationToken, Task<ToolImageResult>> invoke)
        {
            Name = name;
            _invoke = invoke;
        }

        public string Name { get; }

        public string Description => "Test image tool.";

        public string InputSchemaJson => ToolSchema.Object().Build();

        public bool IsHeavy => false;

        public Task<ToolImageResult> InvokeImageAsync(JsonElement arguments, CancellationToken cancellationToken)
            => _invoke(arguments, cancellationToken);
    }
}
