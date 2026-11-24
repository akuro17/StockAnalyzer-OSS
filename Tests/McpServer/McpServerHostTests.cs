using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<Process, Task<string>> ErrorOutputs = new();

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

    private sealed class ScriptedInputStream : Stream
    {
        private readonly byte[] _initialBytes;
        private readonly Task _waitBeforeFault;
        private readonly Func<Exception> _createFault;
        private int _offset;
        private bool _faulted;

        public ScriptedInputStream(string initialLines, Task waitBeforeFault, Func<Exception>? createFault = null)
        {
            _initialBytes = new UTF8Encoding(false).GetBytes(initialLines);
            _waitBeforeFault = waitBeforeFault;
            _createFault = createFault ?? (() => new IOException("Simulated pipe disconnect."));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _initialBytes.Length)
            {
                int n = Math.Min(buffer.Length, _initialBytes.Length - _offset);
                _initialBytes.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            if (!_faulted)
            {
                _faulted = true;
                await _waitBeforeFault.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw _createFault();
            }

            return 0;
        }
    }

    private sealed class FailingOnNthWriteStream(int failOnWriteNumber) : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) >= failOnWriteNumber)
            {
                throw new IOException("Simulated broken output pipe.");
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task InProcess_AnIoExceptionOnInputCancelsInFlightWorkAndExitsCleanlyWithZero()
    {
        using var started = new SemaphoreSlim(0);
        using var cancelled = new SemaphoreSlim(0);
        var slow = new FakeTool("sa_slow_disconnect", async (_, ct) =>
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.Release();
                throw;
            }

            return "{}";
        });
        string initial = McpTestSupport.InitializeMessage + "\n" + McpTestSupport.CallMessage(5, "sa_slow_disconnect") + "\n";
        using var input = new ScriptedInputStream(initial, started.WaitAsync(TimeSpan.FromSeconds(10)));
        var output = new MemoryStream();
        var log = new StringWriter();

        int exitCode = await McpServerHost.RunAsync(
            input, output, McpTestSupport.CreateDispatcher(new IMcpTool[] { slow }), 1 << 20, new LogSink(log), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Transport I/O ended", log.ToString());
        Assert.Contains("exiting", log.ToString());
    }

    [Fact]
    public async Task InProcess_AnObjectDisposedExceptionOnInputCancelsInFlightWorkAndExitsCleanlyWithZero()
    {
        using var started = new SemaphoreSlim(0);
        using var cancelled = new SemaphoreSlim(0);
        var slow = new FakeTool("sa_slow_disposed", async (_, ct) =>
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.Release();
                throw;
            }

            return "{}";
        });
        string initial = McpTestSupport.InitializeMessage + "\n" + McpTestSupport.CallMessage(5, "sa_slow_disposed") + "\n";
        using var input = new ScriptedInputStream(initial, started.WaitAsync(TimeSpan.FromSeconds(10)), () => new ObjectDisposedException("stdin"));
        var output = new MemoryStream();
        var log = new StringWriter();

        int exitCode = await McpServerHost.RunAsync(
            input, output, McpTestSupport.CreateDispatcher(new IMcpTool[] { slow }), 1 << 20, new LogSink(log), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Transport stream closed", log.ToString());
        Assert.Contains("exiting", log.ToString());
    }

    [Fact]
    public async Task InProcess_AnIoExceptionOnImmediateWriteCancelsInFlightWorkAndExitsCleanlyWithZero()
    {
        using var started = new SemaphoreSlim(0);
        using var cancelled = new SemaphoreSlim(0);
        var slow = new FakeTool("sa_slow_write_fail", async (_, ct) =>
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.Release();
                throw;
            }

            return "{}";
        });
        // Line 1: initialize (write 1 succeeds), Line 2: slow call starts, wait until started, Line 3: ping (write 2 throws IOException)
        using var serverPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientPipe = new AnonymousPipeClientStream(PipeDirection.In, serverPipe.ClientSafePipeHandle);
        using var output = new FailingOnNthWriteStream(failOnWriteNumber: 2);
        var log = new StringWriter();

        Task<int> host = McpServerHost.RunAsync(
            clientPipe, output, McpTestSupport.CreateDispatcher(new IMcpTool[] { slow }), 1 << 20, new LogSink(log), CancellationToken.None);

        async Task SendAsync(string line) => await serverPipe.WriteAsync(new UTF8Encoding(false).GetBytes(line + "\n"));
        await SendAsync(McpTestSupport.InitializeMessage);
        await SendAsync(McpTestSupport.CallMessage(5, "sa_slow_write_fail"));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        await SendAsync("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");

        int exitCode = await host.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.True(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Transport I/O ended", log.ToString());
    }

    [Fact]
    public async Task InProcess_NormalEofDoesNotCancelRunningCall()
    {
        bool wasCancelled = false;
        var quick = new FakeTool("sa_quick_eof", async (_, ct) =>
        {
            await Task.Delay(150);
            wasCancelled = ct.IsCancellationRequested;
            return """{"ok":true}""";
        });
        string input = McpTestSupport.InitializeMessage + "\n" + McpTestSupport.CallMessage(4, "sa_quick_eof") + "\n";
        var output = new MemoryStream();

        int exitCode = await McpServerHost.RunAsync(
            Bytes(input), output, McpTestSupport.CreateDispatcher(new IMcpTool[] { quick }), 1 << 20, new LogSink(new StringWriter()), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(wasCancelled);
        string[] lines = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
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
                new[] { "sa_analyze_cycles", "sa_analyze_market_structure", "sa_analyze_portfolio_risk", "sa_build_template", "sa_calculate_chart_series", "sa_calculate_cross_asset_stats", "sa_calculate_drawing_values", "sa_calculate_indicators", "sa_calculate_indicators_batch", "sa_calculate_market_breadth", "sa_calculate_relative_performance", "sa_calculate_reverse_watch", "sa_calculate_volume_profile", "sa_check_portfolio_rl_preflight", "sa_check_rl_playback_preflight", "sa_detect_candle_patterns", "sa_detect_chart_patterns", "sa_detect_divergences", "sa_evaluate_expressions", "sa_extract_signal_events", "sa_filter_tickers", "sa_find_support_resistance", "sa_get_active_workspace", "sa_get_candles", "sa_get_capabilities", "sa_get_chart_drawings", "sa_get_model_explanation", "sa_get_portfolio_summary", "sa_get_seasonality", "sa_get_settings", "sa_get_symbol_coverage", "sa_get_template_detail", "sa_get_ticker_metadata", "sa_list_models", "sa_list_result_handles", "sa_list_templates", "sa_list_ticker_groups", "sa_list_tickers", "sa_list_upcoming_catalysts", "sa_optimize_backtest", "sa_project_price_path", "sa_render_chart_image", "sa_run_backtest", "sa_run_portfolio_backtest", "sa_run_screener", "sa_run_walk_forward_analysis", "sa_search_notes", "sa_validate_condition_tree" },
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

            string errorOutput = await ErrorOutputAsync(process).WaitAsync(cancellation.Token);
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

        // The error pipe is read from the first moment, in step with the server writing to it: a client that reads it only after the session ends stops the server as soon as the pipe is full
        // (the same defect as an unread output pipe). ErrorOutputAsync returns what was collected once the server has exited.
        ErrorOutputs.Add(process, process.StandardError.ReadToEndAsync());
        return process;
    }

    /// <summary>The whole error output of a server started by <see cref="StartServer"/>; it completes when the server has exited and its error pipe is closed.</summary>
    internal static Task<string> ErrorOutputAsync(Process process)
        => ErrorOutputs.TryGetValue(process, out Task<string>? output)
            ? output
            : throw new InvalidOperationException("The process was not started by StartServer.");

    /// <summary>The server assembly with its runtime configuration: the test output copy when complete, else the project's own build output.</summary>
    internal static string LocateServerAssembly()
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

    [Fact]
    public async Task RequestLineReader_StripsLeadingUtf8BomOnFirstLineOnly()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        byte[] firstLine = Encoding.UTF8.GetBytes(McpTestSupport.InitializeMessage + "\n");
        byte[] secondLine = Encoding.UTF8.GetBytes(ToolsList + "\n");
        var input = new MemoryStream();
        input.Write(bom);
        input.Write(firstLine);
        input.Write(secondLine);
        input.Position = 0;

        var output = new MemoryStream();
        int exitCode = await McpServerHost.RunAsync(
            input, output, McpTestSupport.CreateDispatcher(Array.Empty<IMcpTool>()), 1 << 20, new LogSink(new StringWriter()), CancellationToken.None);

        Assert.Equal(0, exitCode);
        string[] responses = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n').Split('\n');
        Assert.Equal(2, responses.Length);
        using JsonDocument init = JsonDocument.Parse(responses[0]);
        Assert.True(init.RootElement.TryGetProperty("result", out _));
        using JsonDocument list = JsonDocument.Parse(responses[1]);
        Assert.True(list.RootElement.TryGetProperty("result", out _));

        // Standalone BOM-only first line is skipped as whitespace; BOM on line 2 is not stripped and produces ParseError.
        var secondBomInput = new MemoryStream();
        secondBomInput.Write(bom);
        secondBomInput.Write(Encoding.UTF8.GetBytes("\n"));
        secondBomInput.Write(bom);
        secondBomInput.Write(firstLine);
        secondBomInput.Position = 0;
        var reader = new RequestLineReader(secondBomInput, 1 << 20);
        RequestLine? line1 = await reader.ReadAsync(CancellationToken.None);
        RequestLine? line2 = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(string.Empty, line1?.Text);
        Assert.StartsWith("\uFEFF", line2?.Text);
    }
}
