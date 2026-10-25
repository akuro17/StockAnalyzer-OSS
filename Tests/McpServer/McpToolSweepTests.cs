using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Run 9: the cross-cutting regression over the whole tool catalogue, through the real composition on a throw-away data tree and template folder.
/// The tool list the server reports is compared with the sweep tables, so a tool added later cannot escape the checks. Three concerns:
/// untrusted text in every place that names a symbol, a template or a resource (path traversal), arguments past every limit, and
/// Zero-Mutation (a session of every tool, successes and failures, leaves the data tree and the template folder byte-identical).
/// </summary>
public sealed class McpToolSweepTests : IDisposable
{
    private const int Days = 300;
    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string HighClose = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":150}""";
    private const string Slot = "@@";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");
    private readonly string _templateRoot = TestDataIsolationBootstrap.NewTempDirectory("McpSweepTemplates");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly Guid _templateId = Guid.NewGuid();

    public McpToolSweepTests()
    {
        var template = new ScreenerIndicatorTemplate { Id = _templateId, Name = "Sweep", Description = "Sweep template" };
        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 1m,
                LogicalOperator = LogicalOperator.And,
                IsEnabled = true,
            },
        });
        Directory.CreateDirectory(TemplateFolder);
        File.WriteAllText(Path.Combine(TemplateFolder, $"{_templateId}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));

        // The file the application's own template service would rename and rewrite when it reads: the server must leave it alone.
        File.WriteAllText(Path.Combine(_templateRoot, "indicator_templates.json"), "[]");
    }

    private string TemplateFolder => Path.Combine(_templateRoot, "Screener");

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        try { Directory.Delete(_templateRoot, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()), TemplateFolder);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    // ---- the sweep tables ------------------------------------------------------------------------------------------------------------

    private static readonly string Indicators = """[{"id":"r","type":"RSI"}]""";

    /// <summary>One call per tool that must succeed on the fixture tree.</summary>
    private static readonly (string Tool, string Arguments)[] Baselines =
    {
        ("sa_list_tickers", """{"limit":5,"includeMetadataSummary":true}"""),
        ("sa_list_ticker_groups", "{}"),
        ("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":5}"""),
        ("sa_get_ticker_metadata", """{"symbols":["MSFT","7203.T"]}"""),
        ("sa_get_capabilities", """{"indicatorTypeFilter":["RSI"],"includeScreenerCatalog":true}"""),
        ("sa_get_settings", "{}"),
        ("sa_calculate_indicators", """{"symbol":"MSFT","timeFrame":"D1","indicators":""" + Indicators + "}"),
        ("sa_run_screener", """{"conditionTree":""" + HighClose + ""","symbols":["MSFT"]}"""),
        ("sa_list_templates", "{}"),
        ("sa_run_backtest", """{"symbol":"MSFT","conditions":{"entryLong":""" + Entry + ""","exitLong":""" + Exit + "}}"),
        ("sa_calculate_reverse_watch", """{"symbol":"MSFT","timeFrame":"D1"}"""),
    };

    /// <summary>Every argument position that carries a symbol or a template id; the slot is replaced with each corpus entry.</summary>
    private static readonly (string Tool, string Arguments)[] NameSlots =
    {
        ("sa_get_candles", """{"symbol":"@@","timeFrame":"D1"}"""),
        ("sa_get_ticker_metadata", """{"symbols":["@@"]}"""),
        ("sa_calculate_indicators", """{"symbol":"@@","timeFrame":"D1","indicators":""" + Indicators + "}"),
        ("sa_run_screener", """{"conditionTree":""" + HighClose + ""","symbols":["@@"]}"""),
        ("sa_run_screener", """{"templateId":"@@"}"""),
        ("sa_run_backtest", """{"symbol":"@@","conditions":{"entryLong":""" + Entry + ""","exitLong":""" + Exit + "}}"),
        ("sa_calculate_reverse_watch", """{"symbol":"@@","timeFrame":"D1"}"""),
    };

    /// <summary>Raw values that can reach a file name or the file system: separators, drives, parent segments, control characters, empty and over-long text.</summary>
    public static IEnumerable<object[]> TraversalCorpus() => new[]
    {
        "../MSFT",
        "..\\MSFT",
        "..",
        "MSFT/../../x",
        "C:\\Windows\\win.ini",
        "\\\\server\\share\\MSFT",
        "/etc/passwd",
        "MSFT\u0000",
        "MSFT\u0000.parquet",
        "MSFT\r\nX",
        " ",
        "",
        new string('A', 33),
        new string('A', 5000),
    }.Select(value => new object[] { value });

    /// <summary>
    /// Well-formed but hostile-looking names: percent-encoded dots, Windows device names, wildcard and shell characters, a file extension.
    /// They are legal symbol text, so a tool may refuse them or answer "nothing stored"; what it may never do is return another symbol's data.
    /// </summary>
    public static IEnumerable<object[]> LookAlikeCorpus() => new[]
    {
        "%2e%2e%2fMSFT",
        "%2e%2e%5cMSFT",
        "MSFT.parquet",
        "CON",
        "NUL",
        "COM1",
        "AUX",
        "CON.meta",
        "*",
        "M*",
        "MSF?",
        "[A-Z]*",
        "{MSFT}",
        "MSFT;DROP",
        "MSFT|X",
        "MSFT<",
    }.Select(value => new object[] { value });

    /// <summary>The slot receives the value as JSON string content (quotes, backslashes and control characters escaped), exactly as a client would send it.</summary>
    private static string With(string arguments, string value)
    {
        string escaped = JsonSerializer.Serialize(value);
        return arguments.Replace(Slot, escaped[1..^1], StringComparison.Ordinal);
    }

    private static async Task<string> RawAsync(McpMessageDispatcher dispatcher, string method, string paramsJson, int id = 1)
        => (await dispatcher.DispatchAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""", CancellationToken.None))!;

    // ---- coverage guard ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryToolTheServerListsIsInTheSweepTables()
    {
        var dispatcher = await StartAsync();

        using JsonDocument listing = JsonDocument.Parse(await RawAsync(dispatcher, "tools/list", "{}"));
        string[] listed = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(listed, Baselines.Select(b => b.Tool).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(NameSlots, slot => Assert.Contains(slot.Tool, listed));
    }

    [Fact]
    public async Task EveryBaselineSucceedsAndStaysUnderTheResponseLimit()
    {
        var dispatcher = await StartAsync();
        int limit = new McpLimits().MaxResponseBytes;

        foreach ((string tool, string arguments) in Baselines)
        {
            string response = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, tool, arguments), CancellationToken.None))!;
            using JsonDocument document = JsonDocument.Parse(response);
            Assert.False(McpTestSupport.IsError(document), $"{tool}: {McpTestSupport.Structured(document)}");
            Assert.True(Encoding.UTF8.GetByteCount(response) < limit, tool);
        }
    }

    // ---- path traversal ------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TraversalCorpus))]
    public async Task TextThatCanReachTheFileSystemIsRefusedInEverySymbolAndTemplateSlot(string value)
    {
        var before = _fixture.Snapshot();
        var templatesBefore = ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot);
        var dispatcher = await StartAsync();
        string[] refusals = { "INVALID_ARGUMENT", "SYMBOL_NOT_FOUND", "TEMPLATE_NOT_FOUND" };

        foreach ((string tool, string template) in NameSlots)
        {
            string response = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, tool, With(template, value)), CancellationToken.None))!;
            using JsonDocument document = JsonDocument.Parse(response);

            Assert.True(McpTestSupport.IsError(document), $"{tool} accepted '{value}'");
            Assert.Contains(McpTestSupport.ErrorCodeOf(document), refusals);
            AssertNoLeak(response);
        }

        Assert.Equal(before, _fixture.Snapshot());
        Assert.Equal(templatesBefore, ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot));
    }

    [Theory]
    [MemberData(nameof(LookAlikeCorpus))]
    public async Task ALegalButHostileLookingNameEitherFailsCleanlyOrFindsNothingAndNeverReturnsAnotherSymbolsData(string value)
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology" });
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();
        string[] refusals = { "INVALID_ARGUMENT", "SYMBOL_NOT_FOUND", "TEMPLATE_NOT_FOUND" };

        foreach ((string tool, string template) in NameSlots)
        {
            string response = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, tool, With(template, value)), CancellationToken.None))!;
            using JsonDocument document = JsonDocument.Parse(response);

            if (McpTestSupport.IsError(document))
            {
                Assert.Contains(McpTestSupport.ErrorCodeOf(document), refusals);
            }
            else
            {
                JsonElement result = McpTestSupport.Structured(document);
                Assert.DoesNotContain("Microsoft", response, StringComparison.Ordinal);
                Assert.DoesNotContain("\"found\":true", response, StringComparison.Ordinal);
                if (result.TryGetProperty("matchedSymbols", out JsonElement matched))
                {
                    Assert.Equal(0, matched.GetArrayLength());
                }

                if (result.TryGetProperty("candles", out JsonElement candles))
                {
                    Assert.Equal(0, candles.GetArrayLength());
                }
            }

            AssertNoLeak(response);
        }

        Assert.Equal(before, _fixture.Snapshot());
    }

    private void AssertNoLeak(string response)
    {
        Assert.DoesNotContain(_fixture.Root, response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_templateRoot, response, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("stockanalyzer://catalog/../settings/current")]
    [InlineData("stockanalyzer://catalog/indicators/../../x")]
    [InlineData("stockanalyzer://%2e%2e/x")]
    [InlineData("stockanalyzer://catalog/indicators?x=1")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("C:\\\\Windows\\\\win.ini")]
    [InlineData("..\\\\x")]
    [InlineData("stockanalyzer://catalog/indicators\\u0000")]
    [InlineData("")]
    public async Task AResourceUriThatIsNotOneOfTheThreeIsRefusedAsAnErrorNeverAsContent(string uri)
    {
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        using JsonDocument document = JsonDocument.Parse(await RawAsync(dispatcher, "resources/read", $$"""{"uri":"{{uri}}"}"""));

        Assert.True(document.RootElement.TryGetProperty("error", out JsonElement error), "a non-resource URI must not return content");
        Assert.Contains(error.GetProperty("code").GetInt32(), new[] { -32002, -32602 });
        Assert.False(document.RootElement.TryGetProperty("result", out _));
        Assert.Equal(before, _fixture.Snapshot());
    }

    // ---- limits --------------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> OverLimitCalls()
    {
        var limits = new McpLimits();
        string Symbols(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => $"\"S{i}\""));
        string Many(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"id\":\"i{i}\",\"type\":\"RSI\"}}"));
        const string Frame = "\"symbol\":\"MSFT\",\"timeFrame\":\"D1\"";

        return new (string Tool, string Arguments, string Parameter)[]
        {
            ("sa_list_tickers", $"{{\"limit\":{limits.MaxTickersPerPage + 1}}}", "limit"),
            ("sa_get_candles", $"{{{Frame},\"count\":{limits.MaxCandlesPerRequest + 1}}}", "count"),
            ("sa_get_ticker_metadata", $"{{\"symbols\":[{Symbols(limits.MaxSymbolsPerMetadataCall + 1)}]}}", "symbols"),
            ("sa_calculate_indicators", $"{{{Frame},\"indicators\":{Indicators},\"lookbackBars\":{limits.MaxCandlesPerRequest + 1}}}", "lookbackBars"),
            ("sa_calculate_indicators", $"{{{Frame},\"indicators\":{Indicators},\"outputTailCount\":{limits.MaxOutputTailBars + 1}}}", "outputTailCount"),
            ("sa_calculate_indicators", $"{{{Frame},\"indicators\":[{Many(limits.MaxIndicatorsPerCall + 1)}]}}", "indicators"),
            ("sa_calculate_reverse_watch", $"{{{Frame},\"lookbackBars\":{limits.MaxCandlesPerRequest + 1}}}", "lookbackBars"),
            ("sa_calculate_reverse_watch", $"{{{Frame},\"outputTailCount\":{limits.MaxOutputTailBars + 1}}}", "outputTailCount"),
            ("sa_calculate_reverse_watch", $"{{{Frame},\"period\":{limits.MaxCandlesPerRequest + 1}}}", "period"),
            ("sa_run_screener", $"{{\"conditionTree\":{HighClose},\"maxReturnedMatches\":{limits.MaxReturnedMatches + 1}}}", "maxReturnedMatches"),
            ("sa_run_screener", $"{{\"conditionTree\":{HighClose},\"symbols\":[{Symbols(limits.MaxScreenerSymbols + 1)}]}}", "symbols"),
            ("sa_run_backtest", $"{{\"symbol\":\"MSFT\",\"conditions\":{{\"entryLong\":{Entry}}},\"maxReturnedTrades\":{limits.MaxReturnedTrades + 1}}}", "maxReturnedTrades"),
            // 32-bit and 64-bit overflow must be a refusal as well, never a wrap-around or an exception.
            ("sa_get_candles", $"{{{Frame},\"count\":2147483648}}", "count"),
            ("sa_list_tickers", "{\"limit\":9223372036854775807}", "limit"),
        }.Select(call => new object[] { call.Tool, call.Arguments, call.Parameter });
    }

    [Theory]
    [MemberData(nameof(OverLimitCalls))]
    public async Task AnArgumentPastItsLimitIsRefusedNamingTheArgument(string tool, string arguments, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":-1}""")]
    [InlineData("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":0}""")]
    [InlineData("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":1.5}""")]
    [InlineData("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":"5"}""")]
    [InlineData("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":99999999999999999999}""")]
    [InlineData("sa_list_tickers", """{"offset":-1}""")]
    [InlineData("sa_list_tickers", """{"limit":1e9}""")]
    [InlineData("sa_run_backtest", """{"symbol":"MSFT","conditions":{},"maxReturnedTrades":-5}""")]
    public async Task ANonsenseNumberIsRefusedAndNeverBecomesAnInternalError(string tool, string arguments)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Contains(McpTestSupport.ErrorCodeOf(response), new[] { "INVALID_ARGUMENT", "LIMIT_EXCEEDED" });
    }

    [Theory]
    [InlineData("sa_run_screener", "conditionTree")]
    [InlineData("sa_run_backtest", "conditions")]
    [InlineData("sa_calculate_indicators", "parameters")]
    public async Task ADeeplyNestedArgumentIsAnAnsweredErrorNotACrash(string tool, string where)
    {
        var dispatcher = await StartAsync();
        string nested = new string('[', 500) + new string(']', 500);
        string arguments = tool switch
        {
            "sa_run_screener" => $$"""{"{{where}}":{{nested}}}""",
            "sa_run_backtest" => $$"""{"symbol":"MSFT","{{where}}":{{nested}}}""",
            _ => $$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"r","type":"RSI","{{where}}":{{nested}}}]}""",
        };

        string response = (await dispatcher.DispatchAsync(McpTestSupport.CallMessage(1, tool, arguments), CancellationToken.None))!;

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.TryGetProperty("error", out _) || McpTestSupport.IsError(document), "the call must be refused");

        // The server is still usable afterwards.
        using JsonDocument ping = JsonDocument.Parse(await RawAsync(dispatcher, "ping", "{}", id: 2));
        Assert.Equal(2, ping.RootElement.GetProperty("id").GetInt32());
    }

    // ---- Zero-Mutation -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASessionOfEveryToolAndResourceIncludingFailuresLeavesTheDataTreeAndTheTemplateFolderUnchanged()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft", Sector = "Technology" });
        var dataBefore = _fixture.Snapshot();
        var templatesBefore = ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot);
        var dispatcher = await StartAsync();

        foreach ((string tool, string arguments) in Baselines)
        {
            (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!.Dispose();
        }

        foreach ((string tool, string arguments) in NameSlots)
        {
            (await McpTestSupport.CallAsync(dispatcher, tool, With(arguments, "NOSUCH")))!.Dispose();
            (await McpTestSupport.CallAsync(dispatcher, tool, With(arguments, "../x")))!.Dispose();
        }

        foreach (object[] call in OverLimitCalls())
        {
            (await McpTestSupport.CallAsync(dispatcher, (string)call[0], (string)call[1]))!.Dispose();
        }

        (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{_templateId}}","symbols":["MSFT"]}"""))!.Dispose();
        foreach (string uri in new[] { "stockanalyzer://catalog/indicators", "stockanalyzer://catalog/screener", "stockanalyzer://settings/current", "stockanalyzer://nothing" })
        {
            await RawAsync(dispatcher, "resources/read", $$"""{"uri":"{{uri}}"}""");
        }

        Assert.Equal(dataBefore, _fixture.Snapshot());
        Assert.Equal(templatesBefore, ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot));
        Assert.True(File.Exists(Path.Combine(_templateRoot, "indicator_templates.json")), "the legacy template file must not be renamed");
    }

    [Fact]
    public async Task RunningATemplateByIdChangesNothingInTheTemplateFolder()
    {
        var templatesBefore = ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot);
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", $$"""{"templateId":"{{_templateId}}","symbols":["MSFT"]}"""))!;

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal(templatesBefore, ReadOnlyDataTreeFixture.SnapshotOf(_templateRoot));
    }
}
