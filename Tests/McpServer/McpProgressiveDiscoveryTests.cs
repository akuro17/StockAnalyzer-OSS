using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
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
        string request = $$$$"""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"{{{{toolName}}}}","arguments":{{{{argumentsJson}}}}}}""";
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
            Assert.Equal(48, names.Length);
        }

        McpServerRuntime readOnlyProgressiveRuntime = CreateRuntime(progressiveDiscovery: true, enableExecution: false, enableDiscussion: false);
        using (JsonDocument readOnlyProgDoc = await DispatchJsonAsync(readOnlyProgressiveRuntime, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))
        {
            JsonElement[] readOnlyTools = readOnlyProgDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { InvokeToolProxyTool.ToolName, SearchToolsTool.ToolName }, readOnlyTools.Select(t => t.GetProperty("name").GetString()!).ToArray());
            Assert.All(readOnlyTools, t =>
            {
                JsonElement annotations = t.GetProperty("annotations");
                Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
                Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            });
            string readOnlySearchDesc = readOnlyTools
                .Single(t => t.GetProperty("name").GetString() == SearchToolsTool.ToolName)
                .GetProperty("description").GetString()!;
            Assert.Contains(InvokeToolProxyTool.ToolName, readOnlySearchDesc, StringComparison.Ordinal);
            Assert.DoesNotContain(InvokeToolProxyTool.ActionToolName, readOnlySearchDesc, StringComparison.Ordinal);
        }

        McpServerRuntime progressiveRuntime = CreateRuntime(progressiveDiscovery: true, enableExecution: true, enableDiscussion: true);
        await progressiveRuntime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string raw = (await progressiveRuntime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!;
        Assert.True(Encoding.UTF8.GetByteCount(raw) < 4000, $"Expected compact tools/list (<4000 bytes), got {Encoding.UTF8.GetByteCount(raw)} bytes.");

        using JsonDocument progDoc = JsonDocument.Parse(raw);
        var progTools = progDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { InvokeToolProxyTool.ActionToolName, InvokeToolProxyTool.ToolName, SearchToolsTool.ToolName },
            progTools.Select(t => t.GetProperty("name").GetString()!).ToArray());
        string nonReadOnlySearchDesc = progTools
            .Single(t => t.GetProperty("name").GetString() == SearchToolsTool.ToolName)
            .GetProperty("description").GetString()!;
        Assert.Contains(InvokeToolProxyTool.ToolName, nonReadOnlySearchDesc, StringComparison.Ordinal);
        Assert.Contains(InvokeToolProxyTool.ActionToolName, nonReadOnlySearchDesc, StringComparison.Ordinal);
        Assert.All(nonReadOnlySearchDesc, ch => Assert.True(ch < 128));
        Assert.True(nonReadOnlySearchDesc.Length <= 160, $"Expected summary-safe description <= 160 chars, got {nonReadOnlySearchDesc.Length}.");

        foreach ((bool execFlag, bool discFlag) in new[] { (true, false), (false, true) })
        {
            McpServerRuntime singleFlagRuntime = CreateRuntime(progressiveDiscovery: true, enableExecution: execFlag, enableDiscussion: discFlag);
            using JsonDocument singleDoc = await DispatchJsonAsync(singleFlagRuntime, """{"jsonrpc":"2.0","id":4,"method":"tools/list"}""");
            string singleSearchDesc = singleDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == SearchToolsTool.ToolName)
                .GetProperty("description").GetString()!;
            Assert.Contains(InvokeToolProxyTool.ToolName, singleSearchDesc, StringComparison.Ordinal);
            Assert.Contains(InvokeToolProxyTool.ActionToolName, singleSearchDesc, StringComparison.Ordinal);
        }
        var annotationsByName = progTools.ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("annotations"), StringComparer.Ordinal);
        Assert.False(annotationsByName[InvokeToolProxyTool.ActionToolName].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(annotationsByName[InvokeToolProxyTool.ActionToolName].GetProperty("idempotentHint").GetBoolean());
        Assert.True(annotationsByName[InvokeToolProxyTool.ToolName].GetProperty("readOnlyHint").GetBoolean());
        Assert.True(annotationsByName[InvokeToolProxyTool.ToolName].GetProperty("idempotentHint").GetBoolean());
        Assert.True(annotationsByName[SearchToolsTool.ToolName].GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public async Task ExternalDocs_ExistWithRequiredSectionsForAllMetaTools()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, enableExecution: true, enableDiscussion: true);
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
        Assert.Contains("48 additional active tools are discoverable", overviewNoExec, StringComparison.Ordinal);
        Assert.Contains($"  {InvokeToolProxyTool.ActionToolName}", overviewNoExec, StringComparison.Ordinal);
        Assert.Contains($"  {InvokeToolProxyTool.ToolName}", overviewNoExec, StringComparison.Ordinal);
        Assert.Contains($"  {SearchToolsTool.ToolName}", overviewNoExec, StringComparison.Ordinal);

        string overviewWithExec = McpHelpText.Overview(runtime.Tools, includeExecutionTools: true);
        Assert.Contains("57 additional active tools are discoverable", overviewWithExec, StringComparison.Ordinal);

        Assert.True(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: false, "sa_run_backtest", out string backtestHelp, out _));
        Assert.Contains("sa_run_backtest", backtestHelp, StringComparison.Ordinal);
        Assert.True(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: false, InvokeToolProxyTool.ActionToolName, out string actionHelp, out _));
        Assert.Contains(InvokeToolProxyTool.ActionToolName, actionHelp, StringComparison.Ordinal);
        Assert.False(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: false, "sa_start_training", out _, out _));
        Assert.True(McpHelpText.TryTool(runtime.Tools, includeExecutionTools: true, "sa_start_training", out _, out _));
    }

    [Fact]
    public async Task SearchTools_BrowseScoringSeparatorFoldingDocUriMatchAnyDefaultAndZeroProfileHints()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, toolsets: new[] { "core", "technical-extended", "backtest" });

        // Browse with whitespace query (equivalent to omitted) + docUri always present and resolvable
        (bool errBrowse, _, JsonElement browse) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"   ","limit":26}""");
        Assert.False(errBrowse);
        Assert.Equal(26, browse.GetProperty("totalAvailableTools").GetInt32());
        Assert.Equal(26, browse.GetProperty("matchedCount").GetInt32());
        Assert.Equal(26, browse.GetProperty("returnedCount").GetInt32());
        Assert.False(browse.GetProperty("hasMore").GetBoolean());
        Assert.False(browse.TryGetProperty("availableProfiles", out _));
        JsonElement firstEntry = browse.GetProperty("tools")[0];
        Assert.False(firstEntry.TryGetProperty("inputSchema", out _));
        string firstName = firstEntry.GetProperty("name").GetString()!;
        string firstDocUri = firstEntry.GetProperty("docUri").GetString()!;
        Assert.Equal($"stockanalyzer://docs/tools/{firstName}", firstDocUri);
        using (JsonDocument docRes = await DispatchJsonAsync(runtime, $$$$"""{"jsonrpc":"2.0","id":11,"method":"resources/read","params":{"uri":"{{{{firstDocUri}}}}"}}"""))
        {
            Assert.True(docRes.RootElement.TryGetProperty("result", out _));
        }

        // Bidirectional hyphen <-> underscore equivalence
        (bool errWf, _, JsonElement wf) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"walk-forward"}""");
        Assert.False(errWf);
        Assert.Contains(wf.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "sa_run_walk_forward_analysis");

        (bool errExt, _, JsonElement ext) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"technical_extended"}""");
        Assert.False(errExt);
        Assert.Equal(10, ext.GetProperty("matchedCount").GetInt32());

        // Multi-word query defaults to MatchAny and ranks multi-token matches ahead of single-token matches
        (bool errAny, _, JsonElement anySearch) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"backtest drawdown","limit":50}""");
        Assert.False(errAny);
        string[] anyNames = anySearch.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.True(anyNames.Length > 1);
        Assert.Equal("sa_run_backtest", anyNames[0]);

        (bool errAll, _, JsonElement allSearch) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"backtest drawdown","matchMode":"All"}""");
        Assert.False(errAll);
        int allMatched = allSearch.GetProperty("matchedCount").GetInt32();
        Assert.True(allMatched >= 1 && allMatched < anySearch.GetProperty("matchedCount").GetInt32());
        Assert.Equal("sa_run_backtest", allSearch.GetProperty("tools")[0].GetProperty("name").GetString());

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

        // Duplicates within 10 + typo -> deduplicated match + unmatchedToolNames (non-error) + auto includeSchema + outputSchema + docUri
        (bool errInspect, _, JsonElement inspect) = await CallToolAsync(
            runtime,
            SearchToolsTool.ToolName,
            """{"toolNames":["sa_get_capabilities","sa_get_capabilities","sa_typo_tool"],"includeExamples":true}""");
        Assert.False(errInspect);
        Assert.Equal(1, inspect.GetProperty("matchedCount").GetInt32());
        Assert.Equal("sa_typo_tool", inspect.GetProperty("unmatchedToolNames")[0].GetString());
        JsonElement capEntry = inspect.GetProperty("tools")[0];
        Assert.Equal("stockanalyzer://docs/tools/sa_get_capabilities", capEntry.GetProperty("docUri").GetString());
        Assert.True(capEntry.TryGetProperty("inputSchema", out _));
        Assert.True(capEntry.TryGetProperty("outputSchema", out _));
        Assert.True(capEntry.TryGetProperty("exampleArguments", out _));

        // 7 toolNames with omitted limit returns all 7 with inputSchema (no silent truncation at 5), and explicit limit 7 succeeds while 11 fails
        const string SevenToolNames = """["sa_get_candles","sa_calculate_indicators","sa_get_capabilities","sa_analyze_market_structure","sa_find_support_resistance","sa_detect_candle_patterns","sa_detect_divergences"]""";
        (bool errSeven, _, JsonElement seven) = await CallToolAsync(runtime, SearchToolsTool.ToolName, $$"""{"toolNames":{{SevenToolNames}}}""");
        Assert.False(errSeven);
        Assert.Equal(7, seven.GetProperty("matchedCount").GetInt32());
        Assert.Equal(7, seven.GetProperty("returnedCount").GetInt32());
        Assert.False(seven.GetProperty("hasMore").GetBoolean());
        Assert.All(seven.GetProperty("tools").EnumerateArray(), entry => Assert.True(entry.TryGetProperty("inputSchema", out _)));

        (bool errSevenExplicit, _, JsonElement sevenExplicit) = await CallToolAsync(runtime, SearchToolsTool.ToolName, $$"""{"toolNames":{{SevenToolNames}},"limit":7}""");
        Assert.False(errSevenExplicit);
        Assert.Equal(7, sevenExplicit.GetProperty("returnedCount").GetInt32());

        (bool errElevenLimit, _, JsonElement elevenLimit) = await CallToolAsync(runtime, SearchToolsTool.ToolName, $$"""{"toolNames":{{SevenToolNames}},"limit":11}""");
        Assert.True(errElevenLimit);
        Assert.Equal(ToolErrorCodes.LimitExceeded, elevenLimit.GetProperty("code").GetString());

        // TextOnly mode omits outputSchema in sa_search_tools as well
        McpServerRuntime textOnlyRuntime = CreateRuntime(progressiveDiscovery: true, responseFormat: McpResponseFormat.TextOnly);
        (bool errTextOnly, _, JsonElement textOnlyInspect) = await CallToolAsync(
            textOnlyRuntime,
            SearchToolsTool.ToolName,
            """{"toolNames":["sa_get_capabilities"]}""");
        Assert.False(errTextOnly);
        Assert.False(textOnlyInspect.GetProperty("tools")[0].TryGetProperty("outputSchema", out _));

        // includeSchema: true with query & limit 6 -> LIMIT_EXCEEDED
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
        string imgRequest = """{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"sa_invoke_tool","arguments":{"toolName":"sa_render_chart_image","arguments":{"symbol":"SYNTH","timeFrame":"D1"}}}}""";
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

        // Strict readOnly vs action proxy boundary enforcement
        (bool errActionOnReadOnlyProxy, _, JsonElement actionOnReadOnlyErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_discussion_create","arguments":{"title":"Blocked"}}""");
        Assert.True(errActionOnReadOnlyProxy);
        Assert.Equal(ToolErrorCodes.InvalidArgument, actionOnReadOnlyErr.GetProperty("code").GetString());
        Assert.Equal("toolName", actionOnReadOnlyErr.GetProperty("param").GetString());
        Assert.Contains(InvokeToolProxyTool.ActionToolName, actionOnReadOnlyErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errReadOnlyOnActionProxy, _, JsonElement readOnlyOnActionErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            """{"toolName":"sa_get_capabilities","arguments":{}}""");
        Assert.True(errReadOnlyOnActionProxy);
        Assert.Equal(ToolErrorCodes.InvalidArgument, readOnlyOnActionErr.GetProperty("code").GetString());
        Assert.Equal("toolName", readOnlyOnActionErr.GetProperty("param").GetString());
        Assert.Contains(InvokeToolProxyTool.ToolName, readOnlyOnActionErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        // Recursion blocked for all meta-tools on both proxies
        foreach (string proxyName in new[] { InvokeToolProxyTool.ToolName, InvokeToolProxyTool.ActionToolName })
        {
            foreach (string meta in new[] { InvokeToolProxyTool.ToolName, InvokeToolProxyTool.ActionToolName, SearchToolsTool.ToolName })
            {
                (bool errRec, _, JsonElement recErr) = await CallToolAsync(
                    runtime,
                    proxyName,
                    $$"""{"toolName":"{{meta}}"}""");
                Assert.True(errRec);
                Assert.Equal(ToolErrorCodes.InvalidArgument, recErr.GetProperty("code").GetString());
            }
        }

        // Unknown argument hints point to sa_search_tools on both proxy & direct hidden call,
        // and both schema and runtime ToolException.Parameter get "arguments." prefix under proxy.
        (bool errInner, _, JsonElement innerErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_candles","arguments":{"symbol":"SYNTH","bogusArg":1}}""");
        Assert.True(errInner);
        Assert.Equal("arguments.bogusArg", innerErr.GetProperty("param").GetString());
        Assert.Contains("sa_search_tools", innerErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errRuntimeParam, _, JsonElement runtimeParamErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_candles","arguments":{"symbol":"SYNTH","timeFrame":"D1","startDate":"not-a-date"}}""");
        Assert.True(errRuntimeParam);
        Assert.Equal(ToolErrorCodes.InvalidArgument, runtimeParamErr.GetProperty("code").GetString());
        Assert.Equal("arguments.startDate", runtimeParamErr.GetProperty("param").GetString());

        (bool errDirect, _, JsonElement directErr) = await CallToolAsync(
            runtime,
            "sa_get_candles",
            """{"symbol":"SYNTH","bogusArg":1}""");
        Assert.True(errDirect);
        Assert.Equal("bogusArg", directErr.GetProperty("param").GetString());
        Assert.Contains("sa_search_tools", directErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        var loggedTools = commandLog.Read().Select(entry => entry.Tool).ToList();
        Assert.Contains("sa_get_capabilities", loggedTools);
        Assert.Contains("sa_render_chart_image", loggedTools);
        Assert.Contains("sa_get_candles", loggedTools);
        Assert.Contains(InvokeToolProxyTool.ToolName, loggedTools);
        Assert.Contains(InvokeToolProxyTool.ActionToolName, loggedTools);

        // Non-parameter runtime errors (e.g., MaxResponseTokens exceeded) keep param omitted (null)
        McpServerRuntime tightBudget = CreateRuntime(progressiveDiscovery: true, limitOverrides: ("MaxResponseTokens", 1024));
        (bool errTokenLimit, _, JsonElement tokenLimitErr) = await CallToolAsync(
            tightBudget,
            InvokeToolProxyTool.ToolName,
            """{"toolName":"sa_get_capabilities","arguments":{"includeDetails":true}}""");
        Assert.True(errTokenLimit);
        Assert.Equal(ToolErrorCodes.LimitExceeded, tokenLimitErr.GetProperty("code").GetString());
        Assert.False(tokenLimitErr.TryGetProperty("param", out _));
    }

    [Fact]
    public async Task PromptsResourcesAndDiscussionAttachAnalysisWorkUnderProgressiveDiscovery()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, enableDiscussion: true);
        McpServerRuntime nonProgressiveRuntime = CreateRuntime(progressiveDiscovery: false);

        // Prompts stay intact and guide callers through sa_search_tools + sa_invoke_tool in progressive mode only
        using JsonDocument promptsDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":30,"method":"prompts/list"}""");
        Assert.Equal(3, promptsDoc.RootElement.GetProperty("result").GetProperty("prompts").GetArrayLength());

        using JsonDocument progPromptDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":31,"method":"prompts/get","params":{"name":"technical_diagnosis","arguments":{"symbol":"MSFT"}}}""");
        string progPromptText = progPromptDoc.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains(SearchToolsTool.ToolName, progPromptText, StringComparison.Ordinal);
        Assert.Contains(InvokeToolProxyTool.ToolName, progPromptText, StringComparison.Ordinal);
        string[] progLines = progPromptText.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Write the report in the language of the user's request.", progLines[^1]);
        Assert.Equal("This is an analysis of stored data, not a recommendation to trade.", progLines[^2]);

        using JsonDocument nonProgPromptDoc = await DispatchJsonAsync(nonProgressiveRuntime, """{"jsonrpc":"2.0","id":32,"method":"prompts/get","params":{"name":"technical_diagnosis","arguments":{"symbol":"MSFT"}}}""");
        string nonProgPromptText = nonProgPromptDoc.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.DoesNotContain(SearchToolsTool.ToolName, nonProgPromptText, StringComparison.Ordinal);
        Assert.DoesNotContain(InvokeToolProxyTool.ToolName, nonProgPromptText, StringComparison.Ordinal);

        // Doc resource works for hidden active tool
        using JsonDocument resDoc = await DispatchJsonAsync(runtime, """{"jsonrpc":"2.0","id":33,"method":"resources/read","params":{"uri":"stockanalyzer://docs/tools/sa_get_candles"}}""");
        Assert.True(resDoc.RootElement.TryGetProperty("result", out _));

        // DiscussionAttachAnalysis excludes meta-tools while accepting regular read-only tools via sa_invoke_action
        (bool _, _, JsonElement createRes) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            """{"toolName":"sa_discussion_create","arguments":{"title":"Check progressive","topic":"Check progressive"}}""");
        string sessionId = createRes.GetProperty("sessionId").GetString()!;

        (bool _, _, JsonElement joinRes) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            $$$$"""{"toolName":"sa_discussion_join","arguments":{"sessionId":"{{{{sessionId}}}}","displayName":"Analyst"}}""");
        string token = joinRes.GetProperty("participantToken").GetString()!;

        (bool errAttachMeta, _, JsonElement attachMetaErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            $$$$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{{{sessionId}}}}","participantToken":"{{{{token}}}}","tool":"sa_search_tools"}}""");
        Assert.True(errAttachMeta);
        Assert.Equal(ToolErrorCodes.InvalidArgument, attachMetaErr.GetProperty("code").GetString());
        Assert.DoesNotContain(SearchToolsTool.ToolName, attachMetaErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errAttachUnknownArg, _, JsonElement attachUnknownErr) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            $$$$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{{{sessionId}}}}","participantToken":"{{{{token}}}}","tool":"sa_get_capabilities","arguments":{"bogus":1}}}""");
        Assert.True(errAttachUnknownArg);
        Assert.Equal("arguments.arguments.bogus", attachUnknownErr.GetProperty("param").GetString());
        Assert.Contains(SearchToolsTool.ToolName, attachUnknownErr.GetProperty("hint").GetString(), StringComparison.Ordinal);

        (bool errAttachOk, _, _) = await CallToolAsync(
            runtime,
            InvokeToolProxyTool.ActionToolName,
            $$$$"""{"toolName":"sa_discussion_attach_analysis","arguments":{"sessionId":"{{{{sessionId}}}}","participantToken":"{{{{token}}}}","tool":"sa_evaluate_expressions","arguments":{"expressions":[{"name":"x","expression":"1 + 2"}]}}}""");
        Assert.False(errAttachOk);
    }

    [Fact]
    public async Task SearchTools_UsesDomainAliasesAndShortTokenWordBoundaries_WithStrictAsciiAliases()
    {
        McpServerRuntime runtime = CreateRuntime(progressiveDiscovery: true, enableExecution: true, enableDiscussion: true);
        var allToolNames = runtime.Tools.CallableTools.Select(t => t.Tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach ((string toolName, var aliases) in McpToolSearchAliases.ByToolName)
        {
            Assert.Contains(toolName, allToolNames);
            Assert.NotEmpty(aliases);
            foreach (string alias in aliases)
            {
                Assert.False(string.IsNullOrEmpty(alias));
                Assert.All(alias, c => Assert.True((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_', $"Invalid alias char '{c}' in '{alias}' for '{toolName}'."));
            }
        }

        foreach (string indicatorQuery in new[] { "rsi", "macd", "bollinger", "ema", "atr" })
        {
            (bool err, _, JsonElement root) = await CallToolAsync(runtime, SearchToolsTool.ToolName, $$"""{"query":"{{indicatorQuery}}","limit":20}""");
            Assert.False(err);
            string[] names = root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
            Assert.NotEmpty(names);
            Assert.Equal("sa_calculate_indicators", names[0]);
            if (indicatorQuery == "atr")
            {
                Assert.DoesNotContain("sa_calculate_cross_asset_stats", names);
            }

            if (indicatorQuery == "ema")
            {
                Assert.DoesNotContain("sa_project_price_path", names);
            }
        }

        (bool errSharpe, _, JsonElement sharpeRoot) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"sharpe"}""");
        Assert.False(errSharpe);
        string[] sharpeNames = sharpeRoot.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("sa_run_backtest", sharpeNames);
        Assert.Contains("sa_analyze_portfolio_risk", sharpeNames);

        (bool errCandle, _, JsonElement candleRoot) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"candlestick"}""");
        Assert.False(errCandle);
        Assert.Equal("sa_detect_candle_patterns", candleRoot.GetProperty("tools")[0].GetProperty("name").GetString());

        (bool errBos, _, JsonElement bosRoot) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"bos"}""");
        Assert.False(errBos);
        Assert.Equal("sa_analyze_market_structure", bosRoot.GetProperty("tools")[0].GetProperty("name").GetString());

        // Acronym CamelCase split: "sma" matches "SMACross" / "fft" matches "FFTCycle" even without an exact alias hit
        (bool errFft, _, JsonElement fftRoot) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"fft"}""");
        Assert.False(errFft);
        Assert.Contains(fftRoot.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "sa_analyze_cycles");

        (bool errCap, _, JsonElement capErr) = await CallToolAsync(runtime, SearchToolsTool.ToolName, """{"query":"sa","includeSchema":true,"limit":6}""");
        Assert.True(errCap);
        Assert.Equal(ToolErrorCodes.LimitExceeded, capErr.GetProperty("code").GetString());
        Assert.Contains("limit <= 5", capErr.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.Contains("toolNames", capErr.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.Contains("includeSchema: false", capErr.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    private sealed class InMemoryCommandLog : ICommandLog
    {
        private readonly List<CommandLogEntry> _entries = new();

        public void Record(string tool, IReadOnlyList<string> argumentKeys, int argumentBytes, string outcome, long milliseconds, string requestId)
            => _entries.Add(new CommandLogEntry(_entries.Count + 1, DateTimeOffset.UtcNow, tool, argumentKeys, argumentBytes, outcome, milliseconds, requestId));

        public IReadOnlyList<CommandLogEntry> Tail(int count) => _entries.TakeLast(count).ToList();

        public IReadOnlyList<CommandLogEntry> Read() => _entries.ToList();

        public void Dispose() { }
    }

    private sealed class SyntheticCandleGateway : EmptyReadOnlyStockAnalyzerGateway
    {
        private readonly StockAnalyzer.Core.ReadOnly.IReadOnlyStockAnalyzerGateway _inner;
        private static readonly IReadOnlyList<CandleData> Bars = new List<CandleData>
        {
            new(new DateTime(2024, 1, 1), 100m, 105m, 99m, 104m, 1000),
            new(new DateTime(2024, 1, 2), 104m, 108m, 103m, 107m, 1500),
            new(new DateTime(2024, 1, 3), 107m, 110m, 105m, 109m, 1200),
        };

        public SyntheticCandleGateway(StockAnalyzer.Core.ReadOnly.IReadOnlyStockAnalyzerGateway inner) => _inner = inner;

        public override Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "SYNTH" });

        public override Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
            => _inner.GetStoredTickerMetadataAsync(symbol, cancellationToken);

        public override Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(symbol, "SYNTH", StringComparison.Ordinal) ? Bars : (IReadOnlyList<CandleData>)Array.Empty<CandleData>());

        public override Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(symbol, "SYNTH", StringComparison.Ordinal) ? Bars : (IReadOnlyList<CandleData>)Array.Empty<CandleData>());
    }
}
