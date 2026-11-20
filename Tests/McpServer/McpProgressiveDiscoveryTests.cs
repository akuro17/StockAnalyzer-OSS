using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Phase 2 Progressive Discovery verification: <c>--progressive-discovery</c>, <c>sa_search_tools</c>,
/// <c>sa_invoke_tool</c>, single-turn proxy unwrapping, deterministic search scoring, help/prompt/resource
/// continuity, privilege gates, and command-log attribution boundaries.
/// </summary>
public sealed class McpProgressiveDiscoveryTests : IDisposable
{
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "SaMcpProgDisc-" + Guid.NewGuid().ToString("N"));
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
        bool progressiveDiscovery = true,
        bool enableExecution = false,
        bool enableDiscussion = false,
        IReadOnlyList<string>? toolsets = null,
        McpResponseFormat responseFormat = McpResponseFormat.Dual,
        ICommandLog? commandLog = null,
        Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper = null,
        params (string Key, int Value)[] limitOverrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, limitOverrides),
            new LogSink(new StringWriter(), quiet: true),
            enableExecution: enableExecution,
            enableDiscussion: enableDiscussion,
            toolsets: toolsets,
            responseFormat: responseFormat,
            commandLog: commandLog,
            gatewayWrapper: gatewayWrapper,
            progressiveDiscovery: progressiveDiscovery);
        _runtimes.Add(runtime);
        return runtime;
    }

    private static async Task<JsonDocument> DispatchJsonAsync(McpServerRuntime runtime, string requestJson)
    {
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string? raw = await runtime.Dispatcher.DispatchAsync(requestJson, CancellationToken.None);
        Assert.NotNull(raw);
        return JsonDocument.Parse(raw!);
    }

    private static async Task<(bool IsError, string Text, JsonElement Root)> CallToolAsync(
        McpServerRuntime runtime,
        string toolName,
        string argumentsJson = "{}")
    {
        string request = $$"""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"{{toolName}}","arguments":{{argumentsJson}}}}""";
        using JsonDocument doc = await DispatchJsonAsync(runtime, request);
        JsonElement result = doc.RootElement.GetProperty("result");
        bool isError = result.TryGetProperty("isError", out JsonElement err) && err.GetBoolean();
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using JsonDocument parsedText = JsonDocument.Parse(text);
        return (isError, text, parsedText.RootElement.Clone());
    }

    [Fact]
    public async Task DefaultMode_OmitsMetaTools_AndProgressiveMode_ReducesToolsListToFixedMetaPair()
    {
        McpServerRuntime defaultRuntime = CreateRuntime(progressiveDiscovery: false);
        using (JsonDocument defDoc = await DispatchJsonAsync(defaultRuntime, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))
        {
            string[] names = defDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToArray();
            Assert.DoesNotContain(SearchToolsTool.ToolName, names);
            Assert.DoesNotContain(InvokeToolProxyTool.ToolName, names);
            Assert.Equal(47, names.Length);
        }

        McpServerRuntime progressiveRuntime = CreateRuntime(progressiveDiscovery: true, enableExecution: true, enableDiscussion: true);
        await progressiveRuntime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string raw = (await progressiveRuntime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        Assert.True(Encoding.UTF8.GetByteCount(raw) < 4000, $"Expected compact tools/list (<4000 bytes), got {Encoding.UTF8.GetByteCount(raw)} bytes.");

        using JsonDocument progDoc = JsonDocument.Parse(raw);
        string[] listedNames = progDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(new[] { InvokeToolProxyTool.ToolName, SearchToolsTool.ToolName }, listedNames);
    }

    [Fact]
    public async Task ExternalDocs_ExistWithRequiredSectionsForBothMetaTools()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true);
        using JsonDocument listDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        string repoReadme = File.ReadAllText(Path.Combine(TestRepositoryRoot.Find(), "docs", "MCP", "README.md"));

        foreach (JsonElement tool in listDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            Assert.Contains(name, repoReadme, StringComparison.Ordinal);

            string docPath = Path.Combine(AppContext.BaseDirectory, "docs", "MCP", "tools", $"{name}.md");
            Assert.True(File.Exists(docPath), $"Missing output doc: {docPath}");
            string docText = File.ReadAllText(docPath);
            Assert.Contains("## Contract & Constraints", docText, StringComparison.Ordinal);
            Assert.Contains("## Arguments (`inputSchema`)", docText, StringComparison.Ordinal);
            Assert.Contains("## Example Call", docText, StringComparison.Ordinal);
            Assert.Contains("## Common Errors", docText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HelpText_ReportsDiscoverableCountInOverview_AndResolvesHiddenToolInTryTool()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, enableExecution: true);

        string overviewNoExec = McpHelpText.Overview(runtime.Tools, includeExecutionTools: false);
        Assert.Contains(McpCommandLine.ProgressiveDiscoveryToken, overviewNoExec, StringComparison.Ordinal);
        Assert.Contains("47 additional active tools are discoverable", overviewNoExec, StringComparison.Ordinal);
        Assert.Contains($"  {InvokeToolProxyTool.ToolName}", overviewNoExec, StringComparison.Ordinal);
        Assert.Contains($"  {SearchToolsTool.ToolName}", overviewNoExec, StringComparison.Ordinal);

        string overviewWithExec = McpHelpText.Overview(runtime.Tools, includeExecutionTools: true);
        Assert.Contains("56 additional active tools are discoverable", overviewWithExec, StringComparison.Ordinal);

        Assert.True(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: false, "sa_run_backtest", out string backtestHelp, out _));
        Assert.Contains("sa_run_backtest", backtestHelp, StringComparison.Ordinal);
        Assert.False(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: false, "sa_start_training", out _, out _));
        Assert.True(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: true, "sa_start_training", out _, out _));
    }

    [Fact]
    public async Task SearchTools_BrowseScoringSeparatorFoldingAndZeroProfileHints()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, toolsets: new[] { "core", "technical-extended", "backtest" });

        // Browse with whitespace query (equivalent to omitted)
        (bool errBrowse, _, JsonElement browse) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"   ","limit":25}""");
        Assert.False(errBrowse);
        Assert.Equal(25, browse.GetProperty("totalAvailableTools").GetInt32());
        Assert.Equal(25, browse.GetProperty("matchedCount").GetInt32());
        Assert.Equal(25, browse.GetProperty("returnedCount").GetInt32());
        Assert.False(browse.GetProperty("hasMore").GetBoolean());
        Assert.False(browse.TryGetProperty("availableProfiles", out _));
        Assert.False(browse.GetProperty("tools")[0].TryGetProperty("inputSchema", out _));

        // Bidirectional hyphen <-> underscore equivalence
        (bool errWf, _, JsonElement wf) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"walk-forward"}""");
        Assert.False(errWf);
        Assert.Contains(wf.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "sa_run_walk_forward_analysis");

        (bool errExt, _, JsonElement ext) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"technical_extended"}""");
        Assert.False(errExt);
        Assert.Equal(10, ext.GetProperty("matchedCount").GetInt32());

        // Filtered out profile -> non-error with availableProfiles
        (bool errZero, _, JsonElement zero) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"profile":"execution"}""");
        Assert.False(errZero);
        Assert.Equal(0, zero.GetProperty("matchedCount").GetInt32());
        string[] available = zero.GetProperty("availableProfiles").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "backtest", "core", "technical-extended" }, available);
    }

    [Fact]
    public async Task SearchTools_ToolNamesUnmatchedReportingDeduplicationAndBounds()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true);

        // >10 even with duplicates -> LIMIT_EXCEEDED
        string elevenDuplicates = string.Join(",", Enumerable.Repeat("\"sa_get_capabilities\"", 11));
        (bool errTooMany, _, JsonElement tooMany) = await CallToolAsync(runtime, SearchToolsTool.ToolName, $"{{\"toolNames\":[{elevenDuplicates}]}}");
        Assert.True(errTooMany);
        Assert.Equal(ToolErrorCodes.LimitExceeded, tooMany.GetProperty("code").GetString());

        // Duplicates within 10 + typo -> deduplicated match + unmatchedToolNames (non-error) + auto includeSchema + outputSchema
        (bool errInspect, _, JsonElement inspect) = await CallToolAsync(
            runtime,
            SearchToolsTool.ToolName,
            """{"toolNames":["sa_get_capabilities","sa_get_capabilities","sa_typo_tool"],"includeExamples":true}""");
        Assert.False(errInspect);
        Assert.Equal(1, inspect.GetProperty("matchedCount").GetInt32());
        Assert.Equal("sa_typo_tool", inspect.GetProperty("unmatchedToolNames")[0].GetString());
        JsonElement capEntry = inspect.GetProperty("tools")[0];
        Assert.True(capEntry.TryGetProperty("inputSchema", out _));
        Assert.True(capEntry.TryGetProperty("outputSchema", out _));
        Assert.True(capEntry.TryGetProperty("exampleArguments", out _));

        // TextOnly mode omits outputSchema in sa_search_tools as well
        McpServerRuntime textOnlyRuntime = CreateRuntime(progressiveDiscovery: true, responseFormat: McpResponseFormat.TextOnly);
        (bool errTextOnly, _, JsonElement textOnlyInspect) = await CallToolAsync(
            textOnlyRuntime,
            SearchToolsTool.ToolName,
            """{"toolNames":["sa_get_capabilities"]}""");
        Assert.False(errTextOnly);
        Assert.False(textOnlyInspect.GetProperty("tools")[0].TryGetProperty("outputSchema", out _));

        // includeSchema: true with limit 6 -> LIMIT_EXCEEDED
        (bool errCap, _, JsonElement capLimit) = await CallToolAsync(
            runtime,
            SearchToolsTool.ToolName,
            """{"query":"sa","includeSchema":true,"limit":6}""");
        Assert.True(errCap);
        Assert.Equal(ToolErrorCodes.LimitExceeded, capLimit.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvokeTool_PreservesParityImageOutputPrivilegeGatesAndHints()
    {
        var commandLog = new InMemoryCommandLog();
        McpServerRuntime runtime = CreateRuntime(
            progressiveDiscovery: true,
            enableExecution: false,
            enableDiscussion: true,
            commandLog: commandLog,
            gatewayWrapper: gw => new SyntheticCandleGateway(gw));

        // Proxy call with omitted arguments defaults to {}
        (bool errCap, _, JsonElement capViaProxy) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_capabilities"}""");
        Assert.False(errCap);
        Assert.True(capViaProxy.TryGetProperty("supportedTimeFrames", out _));

        // arguments: null fails outer schema check and attributes to sa_invoke_tool
        (bool errNullArgs, _, JsonElement nullArgsErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_capabilities","arguments":null}""");
        Assert.True(errNullArgs);
        Assert.Equal(ToolErrorCodes.InvalidArgument, nullArgsErr.GetProperty("code").GetString());

        // PNG image output preserved through sa_invoke_tool
        string imgRequest = """{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"sa_invoke_tool","arguments":{"toolName":"sa_render_chart_image","arguments":{"symbol":"SYNTH"}}}}""";
        using (JsonDocument imgDoc = await DispatchJsonAsync(runtime, imgRequest))
        {
            JsonElement result = imgDoc.RootElement.GetProperty("result");
            Assert.False(result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean());
            Assert.Contains(result.GetProperty("content").EnumerateArray(), c => c.GetProperty("type").GetString() == "image");
        }

        // Disabled execution tool rejected
        (bool errExec, _, JsonElement execErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_start_training","arguments":{}}""");
        Assert.True(errExec);
        Assert.Equal(ToolErrorCodes.InvalidArgument, execErr.GetProperty("code").GetString());

        // Recursion blocked for both meta-tools
        foreach (string meta in new[] { InvokeToolProxyTool.ToolName, SearchToolsTool.ToolName })
        {
            (bool errRec, _, JsonElement recErr) = await CallToolAsync(
                runtime,
                InvokeToolProxyTool.ToolName,
                $$"""{"toolName":"{{meta}}"}""");
            Assert.True(errRec);
            Assert.Equal(ToolErrorCodes.InvalidArgument, recErr.GetProperty("code").GetString());
        }

        // Complement 5 + 6: unknown argument hints point to sa_search_tools on both proxy & direct hidden call,
        // and command log attributes resolved target failures to target tool name while lookup failures stay on sa_invoke_tool.
        (bool errInner, _, JsonElement innerErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_candles","arguments":{"symbol":"SYNTH","bogusArg":1}}""");
        Assert.True(errInner);
        Assert.Equal("arguments.bogusArg", innerErr.GetProperty("parameter").GetString());
        Assert.Contains("sa_search_tools", innerErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errDirect, _, JsonElement directErr) = await CallToolAsync(
            runtime,
            "sa_get_candles",
            """{"symbol":"SYNTH","bogusArg":1}""");
        Assert.True(errDirect);
        Assert.Contains("sa_search_tools", directErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        var loggedTools = commandLog.Read().Select(entry => entry.ToolName).ToList();
        Assert.Contains("sa_get_capabilities", loggedTools);
        Assert.Contains("sa_render_chart_image", loggedTools);
        Assert.Contains("sa_get_candles", loggedTools);
        Assert.Contains(InvokeToolProxyTool.ToolName, loggedTools);
    }

    [Fact]
    public async Task PromptsResourcesAndDiscussionAttachAnalysisWorkUnderProgressiveDiscovery()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, enableDiscussion: true);

        // Prompts stay intact
        using JsonDocument promptsDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":30,"method":"prompts/list"}""");
        Assert.Equal(3, promptsDoc.RootElement.GetProperty("result").GetProperty("prompts").GetArrayLength());

        // Doc resource works for hidden active tool
        using JsonDocument resDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":31,"method":"resources/read","params":{"uri":"stockanalyzer://docs/tools/sa_get_candles"}}""");
        Assert.True(resDoc.RootElement.TryGetProperty("result", out _));

        // DiscussionAttachAnalysis excludes meta-tools while accepting regular read-only tools via sa_invoke_tool
        (bool _, _, JsonElement createRes) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_discussion_create","arguments":{"topic":"Check progressive","analysisType":"Technical"}}""");
        string sessionId = createRes.GetProperty("sessionId").GetString()!;

        (bool _, _, JsonElement joinRes) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            $$"""{"toolName":"sa_discussion_join","arguments":{"sessionId":"{{sessionId}}","displayName":"Analyst"}}""");
        string token = joinRes.GetProperty("participantToken").GetString()!;

        (bool errAttachMeta, _, JsonElement attachMetaErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            $$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{sessionId}}","participantToken":"{{token}}","tool":"sa_search_tools"}}""");
        Assert.True(errAttachMeta);
        Assert.Equal(ToolErrorCodes.InvalidArgument, attachMetaErr.GetProperty("code").GetString());
        Assert.DoesNotContain(SearchToolsTool.ToolName, attachMetaErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errAttachUnknownArg, _, JsonElement attachUnknownErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            $$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{sessionId}}","participantToken":"{{token}}","tool":"sa_get_capabilities","arguments":{"bogus":1}}}""");
        Assert.True(errAttachUnknownArg);
        Assert.Contains(SearchToolsTool.ToolName, attachUnknownErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errAttachOk, _, _) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            $$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{sessionId}}","participantToken":"{{token}}","tool":"sa_evaluate_expressions","arguments":{"expressions":[{"name":"x","formula":"1 + 2"}]}}}""");
        Assert.False(errAttachOk);
    }

    private sealed class InMemoryCommandLog : ICommandLog
    {
        private readonly List<CommandLogEntry> _entries = new();

        public void Record(CommandLogEntry entry) => _entries.Add(entry);

        public IReadOnlyList<CommandLogEntry> Read() => _entries.ToList();
    }

    private sealed class SyntheticCandleGateway : StockAnalyzer.Core.ReadOnly.IReadOnlyStockAnalyzerGateway
    {
        private readonly StockAnalyzer.Core.ReadOnly.IReadOnlyStockAnalyzerGateway _inner;
        private static readonly IReadOnlyList<CandleData> Bars = new List<CandleData>
        {
            new() { Timestamp = new DateTime(2024, 1, 1), Open = 100m, High = 105m, Low = 99m, Close = 104m, Volume = 1000 },
            new() { Timestamp = new DateTime(2024, 1, 2), Open = 104m, High = 108m, Low = 103m, Close = 107m, Volume = 1500 },
            new() { Timestamp = new DateTime(2024, 1, 3), Open = 107m, High = 110m, Low = 105m, Close = 109m, Volume = 1200 },
        };

        public SyntheticCandleGateway(StockAnalyzer.Core.ReadOnly.IReadOnlyStockAnalyzerGateway inner) => _inner = inner;

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "SYNTH" });

        public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
            => _inner.GetStoredTickerMetadataAsync(symbol, cancellationToken);

        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(symbol, "SYNTH", StringComparison.Ordinal) ? Bars : (IReadOnlyList<CandleData>)Array.Empty<CandleData>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<CandleData>>> GetCandlesBatchAsync(IEnumerable<string> symbols, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
            => _inner.GetCandlesBatchAsync(symbols, timeFrame, count, cancellationToken);

        public Task<IReadOnlyList<CandleData>> GetCandlesRangeAsync(string symbol, TimeFrame timeFrame, DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => _inner.GetCandlesRangeAsync(symbol, timeFrame, from, to, cancellationToken);
    }
}
