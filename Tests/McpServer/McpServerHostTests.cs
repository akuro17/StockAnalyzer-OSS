using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>Framing, concurrency and stdout purity of the transport, in process and as a real child process over redirected stdio.</summary>
public sealed class McpServerHostTests
{
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
    private const string ToolsCall = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sa_nothing","arguments":{}}}""";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private static Stream Bytes(string text) => new MemoryStream(new UTF8Encoding(false).GetBytes(text));

    [Fact]
    public async Task InProcess_ASessionProducesOneJsonLinePerRequestAndNothingElseOnTheOutput()
    {
        string input = string.Join("\n", McpTestSupport.InitializeMessage, "", Initialized, ToolsList, "   ", ToolsCall) + "\n";
        var output = new MemoryStream();
        var log = new StringWriter();

        int exitCode = await McpServerHost.RunAsync(Bytes(input), output, McpTestSupport.CreateDispatcher(Array.Empty<IMcpTool>(), log: log), 1 << 20, new LogSink(log), CancellationToken.None);

        byte[] bytes = output.ToArray();
        Assert.Equal(0, exitCode);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "stdout must not start with a BOM");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.EndsWith("\n", text);
        string[] lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Equal("2.0", JsonDocument.Parse(line).RootElement.GetProperty("jsonrpc").GetString()));
        Assert.Equal(new[] { 0, 2, 3 }, lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetInt32()).ToArray());
        Assert.DoesNotContain("INFO", text);
        Assert.Contains("Started", log.ToString());
        Assert.Contains("exiting", log.ToString());
    }

    [Fact]
    public async Task InProcess_ACancelledTokenEndsTheLoopWithoutReadingInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new MemoryStream();

        int exitCode = await McpServerHost.RunAsync(
            Bytes(McpTestSupport.InitializeMessage + "\n"), output, McpTestSupport.CreateDispatcher(Array.Empty<IMcpTool>()), 1 << 20, new LogSink(new StringWriter()), cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task InProcess_ARequestLineOverTheLimitIsRejectedAndTheNextLineIsStillServed()
    {
        string tooLong = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', 5000) + "\"}}";
        string input = string.Join("\n", McpTestSupport.InitializeMessage, tooLong, """{"jsonrpc":"2.0","id":9,"method":"ping"}""") + "\n";
        var output = new MemoryStream();

        await McpServerHost.RunAsync(Bytes(input), output, McpTestSupport.CreateDispatcher(Array.Empty<IMcpTool>()), 4096, new LogSink(new StringWriter()), CancellationToken.None);

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        using var rejected = JsonDocument.Parse(lines[1]);
        Assert.Equal(-32600, rejected.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, rejected.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(9, JsonDocument.Parse(lines[2]).RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task InProcess_APingIsAnsweredWhileAToolCallRunsAndCancellationStopsTheCall()
    {
        using var started = new SemaphoreSlim(0);
        var slow = new FakeTool("sa_slow", async (_, ct) =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return "{}";
        });
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientEnd = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var output = new MemoryStream();
        Task<int> host = McpServerHost.RunAsync(
            clientEnd, output, McpTestSupport.CreateDispatcher(new IMcpTool[] { slow }), 1 << 20, new LogSink(new StringWriter()), CancellationToken.None);

        async Task SendAsync(string line) => await server.WriteAsync(new UTF8Encoding(false).GetBytes(line + "\n"));
        string[] OutputLines() => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await SendAsync(McpTestSupport.InitializeMessage);
        await SendAsync(McpTestSupport.CallMessage(5, "sa_slow"));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        await SendAsync("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");
        await McpTestSupport.WaitUntilAsync(() => OutputLines().Length >= 2);

        Assert.Equal(new[] { 0, 6 }, OutputLines().Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetInt32()).ToArray());

        await SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""");
        server.Close();
        Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, OutputLines().Length); // the cancelled call never answers
    }

    [Fact]
    public async Task InProcess_WhenTheInputEndsTheHostWaitsForARunningCallAndAnswersIt()
    {
        var quick = new FakeTool("sa_quick", async (_, _) =>
        {
            await Task.Delay(300);
            return """{"ok":true}""";
        });
        string input = McpTestSupport.InitializeMessage + "\n" + McpTestSupport.CallMessage(4, "sa_quick") + "\n";
        var output = new MemoryStream();

        await McpServerHost.RunAsync(Bytes(input), output, McpTestSupport.CreateDispatcher(new IMcpTool[] { quick }), 1 << 20, new LogSink(new StringWriter()), CancellationToken.None);

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        using var call = JsonDocument.Parse(lines[1]);
        Assert.True(call.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ChildProcess_HandshakeToolsListUnknownToolPingAndExitOnInputClose()
    {
        using Process process = StartServer();
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
            var stdout = process.StandardOutput;
            var stdin = process.StandardInput;

            await stdin.WriteLineAsync(McpTestSupport.InitializeMessage);
            using var initialize = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal("2025-06-18", initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());

            await stdin.WriteLineAsync(Initialized);
            await stdin.WriteLineAsync(ToolsList);
            using var tools = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(2, tools.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(
                new[] { "sa_analyze_cycles", "sa_analyze_market_structure", "sa_calculate_chart_series", "sa_calculate_indicators", "sa_calculate_relative_performance", "sa_calculate_reverse_watch", "sa_calculate_volume_profile", "sa_check_portfolio_rl_preflight", "sa_check_rl_playback_preflight", "sa_detect_candle_patterns", "sa_detect_chart_patterns", "sa_detect_divergences", "sa_extract_signal_events", "sa_filter_tickers", "sa_find_support_resistance", "sa_get_candles", "sa_get_capabilities", "sa_get_seasonality", "sa_get_settings", "sa_get_ticker_metadata", "sa_list_templates", "sa_list_ticker_groups", "sa_list_tickers", "sa_optimize_backtest", "sa_project_price_path", "sa_run_backtest", "sa_run_screener", "sa_run_walk_forward_analysis" },
                tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray());

            await stdin.WriteLineAsync(ToolsCall);
            using var call = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(-32602, call.RootElement.GetProperty("error").GetProperty("code").GetInt32());

            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");
            using var ping = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(4, ping.RootElement.GetProperty("id").GetInt32());

            stdin.Close();
            Assert.True(process.WaitForExit((int)ProcessTimeout.TotalMilliseconds), "the server must exit when stdin closes");
            Assert.Equal(0, process.ExitCode);
            Assert.Null(await stdout.ReadLineAsync(cancellation.Token));

            string errorOutput = await process.StandardError.ReadToEndAsync(cancellation.Token);
            Assert.Contains("Started", errorOutput);
            Assert.Contains("exiting", errorOutput);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task ChildProcess_ARealToolCallOnTheRealDataFolderAnswersWithBothContentAndStructuredContent()
    {
        using Process process = StartServer();
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
            await process.StandardInput.WriteLineAsync(McpTestSupport.InitializeMessage);
            await process.StandardOutput.ReadLineAsync(cancellation.Token);

            await process.StandardInput.WriteLineAsync(McpTestSupport.CallMessage(7, "sa_list_tickers", """{"limit":3}"""));
            using var response = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(cancellation.Token))!);

            JsonElement result = response.RootElement.GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean());
            Assert.Equal(result.GetProperty("structuredContent").GetRawText(), JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetRawText());
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task ChildProcess_ADiscoverProbeAndAnEarlyNotificationBeforeInitializeFallBackToTheNormalHandshake()
    {
        using Process process = StartServer();
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
            var stdin = process.StandardInput;
            var stdout = process.StandardOutput;

            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{}}""");
            using var discover = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(-32601, discover.RootElement.GetProperty("error").GetProperty("code").GetInt32());

            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""");
            await stdin.WriteLineAsync(McpTestSupport.InitializeMessage);
            using var initialize = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(0, initialize.RootElement.GetProperty("id").GetInt32());
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            await stdin.WriteLineAsync(Initialized);
            await stdin.WriteLineAsync(ToolsList);
            using var tools = JsonDocument.Parse((await stdout.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(2, tools.RootElement.GetProperty("id").GetInt32());
            Assert.True(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task ChildProcess_AMalformedLineGetsAParseErrorAndTheServerKeepsRunning()
    {
        using Process process = StartServer();
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);

            await process.StandardInput.WriteLineAsync("{not json");
            using var parseError = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(-32700, parseError.RootElement.GetProperty("error").GetProperty("code").GetInt32());

            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}""");
            using var ping = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(9, ping.RootElement.GetProperty("id").GetInt32());
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    internal static Process StartServer(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(LocateServerAssembly());
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo)!;
        process.StandardInput.NewLine = "\n";
        return process;
    }

    /// <summary>The server assembly with its runtime configuration: the test output copy when complete, else the project's own build output.</summary>
    private static string LocateServerAssembly()
    {
        const string AssemblyFile = "StockAnalyzer.McpServer.dll";
        string local = Path.Combine(AppContext.BaseDirectory, AssemblyFile);
        if (File.Exists(local) && File.Exists(Path.ChangeExtension(local, ".runtimeconfig.json")))
        {
            return local;
        }

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Name;
        string own = Path.Combine(TestRepositoryRoot.Find(), "StockAnalyzer.McpServer", "bin", configuration, "net8.0", AssemblyFile);
        Assert.True(File.Exists(own), "McpServer build output not found; build the solution first.");
        return own;
    }
}
