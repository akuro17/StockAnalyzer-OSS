using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MP-1: the help and version commands. Parse is a pure function (table-driven); the texts are generated from the tool registry;
/// the optional schema hints (default, minimum, maximum) follow the limits that the argument checks use; a real child process
/// proves the streams and exit codes.
/// </summary>
public sealed class McpCommandLineTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(90);
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "SaMcpCommandLine-" + Guid.NewGuid().ToString("N"));
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

    // ---- AT-1.1 Parse ------------------------------------------------------------------------------------------------

    public static TheoryData<string[], string, bool> ParseCases() => new()
    {
        { Array.Empty<string>(), "run", false },
        { new[] { "--enable-execution" }, "run", true },
        { new[] { "--enable-execution", "--enable-execution" }, "run", true },
        { new[] { "--help" }, "help:<none>", false },
        { new[] { "-h" }, "help:<none>", false },
        { new[] { "-?" }, "help:<none>", false },
        { new[] { "/?" }, "help:<none>", false },
        { new[] { "--help", "sa_get_candles" }, Help("sa_get_candles"), false },
        { new[] { "/?", "sa_get_candles" }, Help("sa_get_candles"), false },
        { new[] { "--help", " padded " }, Help(" padded "), false },
        { new[] { "--help", "--enable-execution" }, "help:<none>", true },
        { new[] { "--enable-execution", "--help" }, "help:<none>", true },
        { new[] { "--enable-execution", "--help", "sa_get_candles" }, Help("sa_get_candles"), true },
        { new[] { "--help", "sa_get_candles", "--enable-execution" }, Help("sa_get_candles"), true },
        { new[] { "--version" }, "version", false },
        { new[] { "--bogus" }, Unknown("--bogus"), false },
        { new[] { "--HELP" }, Unknown("--HELP"), false },
        { new[] { "-H" }, Unknown("-H"), false },
        { new[] { "sa_get_candles" }, Unknown("sa_get_candles"), false },
        { new[] { "--help", "--help" }, Unknown("--help"), false },
        { new[] { "-h", "/?" }, Unknown("/?"), false },
        { new[] { "--help", "sa_get_candles", "-h" }, Unknown("-h"), false },
        { new[] { "--help", "sa_get_candles", "extra" }, Unknown("extra"), false },
        { new[] { "--help", "--bogus" }, Unknown("--bogus"), false },
        { new[] { "--version", "--enable-execution" }, Unknown("--enable-execution"), false },
        { new[] { "--enable-execution", "--version" }, Unknown("--version"), false },
        { new[] { "--version", "--help" }, Unknown("--help"), false },
        { new[] { "--help", "--version" }, Unknown("--version"), false },
        { new[] { "--help", "sa_get_candles", "--version" }, Unknown("--version"), false },
        { new[] { "--version", "--version" }, Unknown("--version"), false },
        { new[] { new string('x', 150) }, $"invalid:Unknown argument '{new string('x', 100)}...'. Run with --help.", false },
        { new[] { new string('x', 100) }, Unknown(new string('x', 100)), false },
    };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_ReadsTheArgumentsLeftToRightAndTheTokensAreCaseSensitive(string[] args, string expected, bool enableExecution)
    {
        McpCommandLineResult result = McpCommandLine.Parse(args);

        Assert.Equal(expected, Describe(result.Command));
        Assert.Equal(enableExecution, result.EnableExecution);
    }

    [Fact]
    public void Parse_ATokenAfterAHelpTokenThatStartsWithADashOrASlashIsNeverAToolName()
    {
        Assert.Equal("help:<none>", Describe(McpCommandLine.Parse(new[] { "--help", "--enable-execution" }).Command));
        Assert.Equal(Unknown("/tmp/x"), Describe(McpCommandLine.Parse(new[] { "--help", "/tmp/x" }).Command));
    }

    // ---- AT-1.2 / AT-1.3 texts ---------------------------------------------------------------------------------------

    [Fact]
    public void Overview_ListsEveryToolOfTheRegistryAndHidesExecutionToolsUntilTheyAreEnabled()
    {
        var registry = new McpToolRegistry(new IMcpTool[]
        {
            new FakeTool("sa_zeta", Noop, ToolSchema.Object().Build()),
            new FakeTool("sa_alpha", Noop, ToolSchema.Object().Build()),
            new FakeTool("sa_execute", Noop, ToolSchema.Object().Build(), isExecution: true),
        });

        string without = McpHelpText.Overview(registry, includeExecutionTools: false);
        string with = McpHelpText.Overview(registry, includeExecutionTools: true);

        Assert.Contains("  sa_alpha  Test tool", without);
        Assert.Contains("  sa_zeta   Test tool", without);
        Assert.DoesNotContain("sa_execute", without);
        Assert.Contains("  sa_execute  Test tool", with);
        Assert.True(without.IndexOf("sa_alpha", StringComparison.Ordinal) < without.IndexOf("sa_zeta", StringComparison.Ordinal), "names are sorted");
    }

    [Fact]
    public void Overview_HasTheDocumentedSectionsInOrder()
    {
        string text = McpHelpText.Overview(new McpToolRegistry(Array.Empty<IMcpTool>()), includeExecutionTools: false);

        int[] positions =
        {
            text.IndexOf("Usage: StockAnalyzer.McpServer [options]", StringComparison.Ordinal),
            text.IndexOf("Options:", StringComparison.Ordinal),
            text.IndexOf("Configuration:", StringComparison.Ordinal),
            text.IndexOf("Exit codes:", StringComparison.Ordinal),
            text.IndexOf("Tools (", StringComparison.Ordinal),
        };
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(0, positions[0]);
        Assert.Contains("appsettings.json", text);
        Assert.Contains("AiIntegration", text);
        foreach (string token in new[] { "--help", "-h", "-?", "/?", "--version", "--enable-execution" })
        {
            Assert.Contains(text.Split('\n'), line => line.TrimStart().StartsWith(token + " ", StringComparison.Ordinal));
        }

        Assert.Contains(text.Split('\n'), line => line.StartsWith("  0 ", StringComparison.Ordinal));
        Assert.Contains(text.Split('\n'), line => line.StartsWith("  2 ", StringComparison.Ordinal));
        Assert.Contains(text.Split('\n'), line => line.StartsWith("  64 ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("One sentence", "One sentence")]
    [InlineData("First. Second sentence.", "First")]
    [InlineData("Ends with a dot.", "Ends with a dot.")]
    [InlineData("Version 1.5 is fine. Next", "Version 1.5 is fine")]
    public void Summary_IsTheTextUpToTheFirstPeriodAndSpace(string description, string expected)
        => Assert.Equal(expected, McpHelpText.Summary(description));

    [Fact]
    public void Summary_CutsALongSentenceTo160CharactersPlusAnEllipsis()
    {
        string summary = McpHelpText.Summary(new string('a', 161) + ". tail");
        Assert.Equal(new string('a', 160) + "...", summary);
        Assert.Equal(new string('a', 160), McpHelpText.Summary(new string('a', 160) + ". tail"));
    }

    [Fact]
    public void Tool_ShowsEveryPropertyWithTypeRequirementAndTheHintsTheSchemaStates()
    {
        string schema = ToolSchema.Object()
            .String("symbol", "The symbol.", required: true)
            .String("frame", "The frame.", allowedValues: new[] { "D1", "W1" })
            .Integer("count", "How many.", defaultValue: 200, minimum: 1, maximum: 10000)
            .Boolean("flag", "A flag.")
            .Build();
        var registry = new McpToolRegistry(new IMcpTool[] { new FakeTool("sa_demo", Noop, schema) });

        Assert.True(McpHelpText.TryTool(registry, includeExecutionTools: false, "sa_demo", out string text, out string error));

        Assert.Equal(string.Empty, error);
        string[] lines = text.Split('\n');
        Assert.Equal("sa_demo", lines[0]);
        Assert.Contains("Test tool.", text);
        Assert.Contains("  symbol (string, required) The symbol.", lines);
        Assert.Contains("  frame (string, optional, allowed D1|W1) The frame.", lines);
        Assert.Contains("  count (integer, optional, default 200, minimum 1, maximum 10000) How many.", lines);
        Assert.Contains("  flag (boolean, optional) A flag.", lines);
    }

    [Fact]
    public void Tool_NamesAreMatchedExactlyAndAnExecutionToolIsUnknownUntilEnabled()
    {
        var registry = new McpToolRegistry(new IMcpTool[]
        {
            new FakeTool("sa_demo", Noop),
            new FakeTool("sa_execute", Noop, isExecution: true),
        });

        Assert.False(McpHelpText.TryTool(registry, false, "SA_DEMO", out _, out string caseError));
        Assert.Equal("Unknown tool 'SA_DEMO'. Run with --help.", caseError);
        Assert.False(McpHelpText.TryTool(registry, false, "sa_dem", out _, out _));
        Assert.False(McpHelpText.TryTool(registry, false, "sa_execute", out _, out string hiddenError));
        Assert.Equal("Unknown tool 'sa_execute'. Run with --help.", hiddenError);
        Assert.True(McpHelpText.TryTool(registry, true, "sa_execute", out _, out _));
    }

    [Fact]
    public void RealRegistry_EveryToolHasAHelpDetailWithAllItsSchemaPropertiesAndTheRequiredMarks()
    {
        McpServerRuntime runtime = CreateRuntime();
        string overview = McpHelpText.Overview(runtime.Tools, includeExecutionTools: false);

        Assert.NotEmpty(runtime.Tools.Tools);
        foreach (RegisteredTool registered in runtime.Tools.Tools)
        {
            Assert.Contains($"  {registered.Tool.Name}", overview);
            Assert.True(McpHelpText.TryTool(runtime.Tools, false, registered.Tool.Name, out string text, out _));
            string[] lines = text.Split('\n');
            var required = registered.Schema.TryGetProperty("required", out JsonElement names)
                ? names.EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in registered.Schema.GetProperty("properties").EnumerateObject())
            {
                string mark = required.Contains(property.Name) ? "required" : "optional";
                Assert.Contains(lines, line => line.StartsWith($"  {property.Name} (", StringComparison.Ordinal) && line.Contains(mark, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Texts_UseLineFeedOnlyAndEndWithOneAndHaveNoByteOrderMark()
    {
        McpServerRuntime runtime = CreateRuntime();
        string[] texts =
        {
            McpHelpText.Overview(runtime.Tools, false),
            McpHelpText.Version(),
        };
        string detail;
        Assert.True(McpHelpText.TryTool(runtime.Tools, false, "sa_get_candles", out detail, out _));

        foreach (string text in texts.Append(detail))
        {
            Assert.DoesNotContain('\r', text);
            Assert.EndsWith("\n", text);
            Assert.DoesNotContain('﻿', text);
            Assert.All(text.Split('\n'), line => Assert.Equal(line.TrimEnd(), line));
        }
    }

    // ---- AT-1.6 schema hints -----------------------------------------------------------------------------------------

    [Fact]
    public void ToolSchema_WithoutTheOptionalHintsTheOutputIsExactlyTheOldOutput()
    {
        string schema = ToolSchema.Object()
            .Integer("count", "How many.")
            .Integer("other", "Required one.", required: true)
            .Build();

        Assert.Equal(
            """{"type":"object","properties":{"count":{"type":"integer","description":"How many."},"other":{"type":"integer","description":"Required one."}},"required":["other"],"additionalProperties":false}""",
            schema);
    }

    [Fact]
    public void ToolSchema_StatedHintsUseTheStandardKeywordsAfterTheDescription()
    {
        Assert.Equal(
            """{"type":"object","properties":{"a":{"type":"integer","description":"d","default":5,"minimum":1,"maximum":9},"b":{"type":"integer","description":"d","minimum":0},"c":{"type":"integer","description":"d","default":0}},"additionalProperties":false}""",
            ToolSchema.Object().Integer("a", "d", defaultValue: 5, minimum: 1, maximum: 9).Integer("b", "d", minimum: 0).Integer("c", "d", defaultValue: 0).Build());
    }

    [Fact]
    public void ToolSchemas_EveryIntegerArgumentOfEveryToolStatesItsDefaultAndFollowsTheLimitsInForce()
    {
        const int Max = 4321;
        const int DefaultCount = 123;
        const int Trades = 77;
        const int Pages = 555;
        const int PageSize = 33;
        const int TailMax = 321;
        const int TailDefault = 21;
        const int Matches = 66;
        McpServerRuntime runtime = CreateRuntime(
            ("MaxCandlesPerRequest", Max),
            ("DefaultCandleCount", DefaultCount),
            ("MaxReturnedTrades", Trades),
            ("MaxTickersPerPage", Pages),
            ("DefaultTickerPageSize", PageSize),
            ("MaxOutputTailBars", TailMax),
            ("DefaultOutputTailBars", TailDefault),
            ("MaxReturnedMatches", Matches));

        (string Tool, string Argument, long? Default, long? Minimum, long? Maximum)[] expected =
        {
            ("sa_get_candles", "count", DefaultCount, 1, Max),
            ("sa_list_tickers", "offset", 0, 0, null),
            ("sa_list_tickers", "limit", PageSize, 1, Pages),
            ("sa_calculate_indicators", "lookbackBars", Max, 1, Max),
            ("sa_calculate_indicators", "outputTailCount", TailDefault, 1, TailMax),
            ("sa_calculate_reverse_watch", "period", null, 1, Max),
            ("sa_calculate_reverse_watch", "dataCount", null, 1, Max),
            ("sa_calculate_reverse_watch", "lookbackBars", Max, 1, Max),
            ("sa_calculate_reverse_watch", "outputTailCount", TailDefault, 1, TailMax),
            ("sa_run_backtest", "maxReturnedTrades", Trades, 0, Trades),
            ("sa_run_screener", "maxReturnedMatches", Matches, 1, Matches),
        };

        foreach ((string tool, string argument, long? defaultValue, long? minimum, long? maximum) in expected)
        {
            Assert.True(runtime.Tools.TryGet(tool, out RegisteredTool? registered), tool);
            JsonElement property = registered.Schema.GetProperty("properties").GetProperty(argument);
            Assert.Equal(minimum, Hint(property, "minimum"));
            Assert.Equal(maximum, Hint(property, "maximum"));
            if (defaultValue is not null)
            {
                Assert.Equal(defaultValue, Hint(property, "default"));
            }
            else
            {
                Assert.NotNull(Hint(property, "default"));
            }
        }

        // A new integer argument without hints is caught here: every integer argument of every tool states a default.
        foreach (RegisteredTool registered in runtime.Tools.Tools)
        {
            foreach (JsonProperty property in registered.Schema.GetProperty("properties").EnumerateObject())
            {
                if (property.Value.GetProperty("type").GetString() == "integer")
                {
                    Assert.True(property.Value.TryGetProperty("default", out _), $"{registered.Tool.Name}.{property.Name} states no default");
                    Assert.True(property.Value.TryGetProperty("minimum", out _), $"{registered.Tool.Name}.{property.Name} states no minimum");
                }
            }
        }
    }

    [Fact]
    public void ToolsList_TheStatedHintsAreTheOnlyDifferenceFromTheDescriptionText()
    {
        McpServerRuntime runtime = CreateRuntime();

        JsonElement count = runtime.Tools.Tools.Single(t => t.Tool.Name == "sa_get_candles").Schema.GetProperty("properties").GetProperty("count");

        Assert.Equal(
            new[] { "type", "description", "default", "minimum", "maximum" },
            count.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---- AT-1.4 / AT-1.7 child process -------------------------------------------------------------------------------

    [Fact]
    public async Task ChildProcess_HelpPrintsTheOverviewToStdoutOnlyAndExitsWithZero()
    {
        (string stdout, string stderr, int exitCode) = await RunAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.StartsWith("Usage: StockAnalyzer.McpServer [options]\n", stdout);
        Assert.DoesNotContain('\r', stdout);
        Assert.False(stdout.StartsWith('﻿'));
        foreach (string tool in new[] { "sa_list_tickers", "sa_get_candles", "sa_run_backtest", "sa_calculate_reverse_watch" })
        {
            Assert.Contains($"  {tool}", stdout);
        }

        Assert.Contains("Exit codes:", stdout);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task ChildProcess_EveryHelpTokenGivesTheSameOverview(string token)
    {
        (string reference, _, _) = await RunAsync("--help");

        (string stdout, string stderr, int exitCode) = await RunAsync(token);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(reference, stdout);
    }

    [Fact]
    public async Task ChildProcess_HelpForOneToolShowsTheArgumentsWithTheirDefaultsAndRanges()
    {
        (string stdout, string stderr, int exitCode) = await RunAsync("--help", "sa_get_candles");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.StartsWith("sa_get_candles\n", stdout);
        Assert.Contains("  count (integer, optional, default 200, minimum 1, maximum 10000) ", stdout);
        Assert.Contains("  symbol (string, required) ", stdout);
    }

    [Theory]
    [InlineData("--bogus", "Unknown argument '--bogus'. Run with --help.")]
    [InlineData("--version --help", "Unknown argument '--help'. Run with --help.")]
    [InlineData("--help sa_nosuch", "Unknown tool 'sa_nosuch'. Run with --help.")]
    [InlineData("--help SA_GET_CANDLES", "Unknown tool 'SA_GET_CANDLES'. Run with --help.")]
    public async Task ChildProcess_ABadCommandLineWritesOneLineToStderrAndExitsWith64(string arguments, string message)
    {
        (string stdout, string stderr, int exitCode) = await RunAsync(arguments.Split(' '));

        Assert.Equal(64, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(message + Environment.NewLine, stderr);
    }

    [Fact]
    public async Task ChildProcess_VersionPrintsTwoLinesAndTheSecondNamesTheSupportedProtocolRevisions()
    {
        (string stdout, string stderr, int exitCode) = await RunAsync("--version");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        string[] lines = stdout.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Empty, lines[2]);
        Assert.StartsWith("StockAnalyzer.McpServer ", lines[0]);
        Assert.True(lines[0].Length > "StockAnalyzer.McpServer ".Length);
        Assert.Equal("MCP protocol " + string.Join(", ", McpProtocolVersions.Supported), lines[1]);
    }

    [Fact]
    public async Task ChildProcess_TheExecutionFlagAloneStillStartsTheNormalSession()
    {
        using Process process = McpServerHostTests.StartServer("--enable-execution");
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
            await process.StandardInput.WriteLineAsync(McpTestSupport.InitializeMessage);
            using JsonDocument initialize = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(cancellation.Token))!);
            Assert.Equal(0, initialize.RootElement.GetProperty("id").GetInt32());

            process.StandardInput.Close();
            Assert.True(process.WaitForExit((int)ProcessTimeout.TotalMilliseconds));
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

    // ---- WP-3: the execution tools in the real executable --------------------------------------------------------------------

    private static readonly string[] ExecutionToolNames = { "sa_start_training", "sa_get_training_status", "sa_cancel_training", "sa_run_onnx_prediction", "sa_start_rl_training" };

    [Fact]
    public async Task ChildProcess_HelpListsTheExecutionToolsOnlyWhenTheyAreEnabled()
    {
        (string without, string withoutError, int withoutCode) = await RunAsync("--help");
        (string with, string withError, int withCode) = await RunAsync("--help", "--enable-execution");

        Assert.Equal(0, withoutCode);
        Assert.Equal(0, withCode);
        Assert.Equal(string.Empty, withoutError);
        Assert.Equal(string.Empty, withError);
        Assert.All(ExecutionToolNames, name => Assert.DoesNotContain(name, without));
        Assert.All(ExecutionToolNames, name => Assert.Contains(name, with));
        Assert.DoesNotContain("none are available", with);
    }

    [Fact]
    public async Task ChildProcess_HelpForAnExecutionToolNeedsTheFlag()
    {
        (string _, string error, int code) = await RunAsync("--help", "sa_start_training");
        (string text, string _, int enabledCode) = await RunAsync("--help", "sa_start_training", "--enable-execution");

        Assert.Equal(64, code);
        Assert.Equal(McpCommandLine.UnknownToolMessage("sa_start_training") + Environment.NewLine, error);
        Assert.Equal(0, enabledCode);
        Assert.Contains("  symbols (array, required)", text);
        Assert.Contains("  framework (string, required, allowed PyTorch|LightGBM|TensorFlow)", text);
        Assert.Contains("  nSplits (integer, optional, default 5)", text);
    }

    [Fact]
    public async Task ChildProcess_AnEnabledServerOffersTheExecutionToolsAndTheRealGatewayAnswersAnUnknownJob()
    {
        using Process process = McpServerHostTests.StartServer("--enable-execution");
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
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
            Assert.Empty(ExecutionToolNames.Except(names));

            foreach (string tool in new[] { "sa_get_training_status", "sa_cancel_training" })
            {
                using JsonDocument call = await Ask(McpTestSupport.CallMessage(2, tool, """{"jobId":"20260101-120000000-abcdef12"}"""));
                Assert.True(McpTestSupport.IsError(call));
                Assert.Equal("JOB_NOT_FOUND", McpTestSupport.ErrorCodeOf(call));
            }

            using JsonDocument capabilities = await Ask(McpTestSupport.CallMessage(3, "sa_get_capabilities"));
            Assert.True(McpTestSupport.Structured(capabilities).GetProperty("execution").GetProperty("enabled").GetBoolean());

            process.StandardInput.Close();
            Assert.True(process.WaitForExit((int)ProcessTimeout.TotalMilliseconds));
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

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static string Unknown(string token) => "invalid:" + McpCommandLine.UnknownArgumentMessage(token);

    private static string Help(string tool) => "help:" + tool;

    private static string Describe(McpCommand command) => command switch
    {
        McpCommand.Run => "run",
        McpCommand.Version => "version",
        McpCommand.Help help => "help:" + (help.ToolName ?? "<none>"),
        McpCommand.Invalid invalid => "invalid:" + invalid.Message,
        _ => throw new InvalidOperationException(),
    };

    private static Task<string> Noop(JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult("{}");

    private static long? Hint(JsonElement property, string key) => property.TryGetProperty(key, out JsonElement value) ? value.GetInt64() : null;

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(params string[] arguments)
    {
        using Process process = McpServerHostTests.StartServer(arguments);
        try
        {
            using var cancellation = new CancellationTokenSource(ProcessTimeout);
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            return (await stdout, await stderr, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private McpServerRuntime CreateRuntime(params (string Key, int Value)[] limits)
    {
        McpServerRuntime runtime = McpServerComposition.Create(McpTestSupport.EmptyDataConfiguration(_emptyRoot, limits), new LogSink(new StringWriter(), quiet: true));
        _runtimes.Add(runtime);
        return runtime;
    }
}
