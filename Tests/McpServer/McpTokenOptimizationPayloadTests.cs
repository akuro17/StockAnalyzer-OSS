using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Master Task 2 tests: <c>MaxResponseTokens</c> budget gate, <c>compactReport</c> on optimization/walk-forward,
/// <c>compactRows</c> on chart series / signal events / backtest / volume profile, <c>fields</c> on <c>sa_list_tickers</c>,
/// and <c>snippetOnly</c> on <c>sa_search_notes</c>.
/// </summary>
public sealed class McpTokenOptimizationPayloadTests : IDisposable
{
    private const int Days = 400;
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";
    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private static readonly string Trading = $$"""{"entryLong":{{Entry}},"exitLong":{{Exit}}}""";
    private const string SimpleCondition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "AAPL");
    private readonly string _notesRoot = Path.Combine(Path.GetTempPath(), "sa_token_notes_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private NoteRepository? _repository;

    public McpTokenOptimizationPayloadTests()
    {
        Directory.CreateDirectory(_notesRoot);
        decimal[] msft = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", msft);
        decimal[] aapl = Enumerable.Range(0, Days).Select(i => Math.Round(150m + (decimal)(12 * Math.Cos(i / 7.0)), 2)).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", aapl);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        SqliteConnection.ClearAllPools();
        _fixture.Dispose();
        if (Directory.Exists(_notesRoot))
        {
            Directory.Delete(_notesRoot, recursive: true);
        }
    }

    private async Task<McpMessageDispatcher> StartWithCompactDefaultsAsync(bool compactDefaults, params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        string dbPath = Path.Combine(_notesRoot, NoteDatabaseConnectionManager.DatabaseFileName);
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            noteSource: new SqliteReadOnlyNoteSource(dbPath),
            compactDefaults: compactDefaults);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
        => await StartWithCompactDefaultsAsync(compactDefaults: false, overrides);

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private async Task AddNoteAsync(string body, int day)
    {
        if (_repository is null)
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, _notesRoot);
            await new NoteSchemaInitializer(manager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
            _repository = new NoteRepository(manager, NullLogger<NoteRepository>.Instance);
        }

        var created = new DateTime(2024, 3, day, 9, 0, 0, DateTimeKind.Local);
        await _repository.CreateAsync(new Note(Guid.NewGuid(), body, created, created));
    }

    // ---- MaxResponseTokens budget gate -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MaxResponseTokens_ReturnsLimitExceededWithHintWhenTextBudgetExceeded()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("AiIntegration:MaxResponseTokens", "1024"));

        // 200+ candles in JSON easily exceeds 4096 UTF-8 bytes (1024 tokens)
        using JsonDocument tooLarge = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":250}""");
        Assert.True(McpTestSupport.IsError(tooLarge));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(tooLarge));
        JsonElement error = McpTestSupport.Structured(tooLarge);
        Assert.Contains("tokens", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Small request well under 1024 tokens succeeds cleanly
        using JsonDocument small = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":5,"compactRows":true}""");
        Ok(small);
    }

    // ---- compactReport: sa_optimize_backtest & sa_run_walk_forward_analysis ------------------------------------------------------

    [Fact]
    public async Task OptimizeBacktest_CompactReport_CompactsMetricsAndPreservesDefaultByteIdentity()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string entry = """{"left":{"indicator":"SMA","parameters":{"Period":{"$param":"fast"}}},"comparison":"GreaterThan","right":{"price":"Close"}}""";
        string conditions = $$"""{"entryLong":{{entry}},"exitLong":{{Exit}}}""";
        string baseArgs = $$"""{"symbol":"MSFT","conditions":{{conditions}},"parameterRanges":[{"name":"fast","start":5,"end":10,"step":5}],"objective":"totalReturn"{{Period}}}""";
        string falseArgs = $$"""{"symbol":"MSFT","conditions":{{conditions}},"parameterRanges":[{"name":"fast","start":5,"end":10,"step":5}],"objective":"totalReturn","compactReport":false{{Period}}}""";
        string compactArgs = $$"""{"symbol":"MSFT","conditions":{{conditions}},"parameterRanges":[{"name":"fast","start":5,"end":10,"step":5}],"objective":"totalReturn","compactReport":true{{Period}}}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_optimize_backtest", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_optimize_backtest", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_optimize_backtest", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        JsonElement explicitFalse = Ok(falseDoc);
        Assert.Equal(omitted.GetRawText(), explicitFalse.GetRawText());

        JsonElement compact = Ok(compactDoc);
        JsonElement[] verboseRank = omitted.GetProperty("ranking").EnumerateArray().ToArray();
        JsonElement[] compactRank = compact.GetProperty("ranking").EnumerateArray().ToArray();
        Assert.Equal(verboseRank.Length, compactRank.Length);
        Assert.NotEmpty(compactRank);

        for (int i = 0; i < compactRank.Length; i++)
        {
            JsonElement vm = verboseRank[i].GetProperty("metrics");
            JsonElement cm = compactRank[i].GetProperty("metrics");
            var nonValid = new List<string>();
            foreach (string metricName in new[] { "totalPnL", "totalReturn", "maxDrawdown", "winRate", "profitFactor", "annualizedSharpe" })
            {
                JsonElement vObj = vm.GetProperty(metricName);
                string status = vObj.GetProperty("status").GetString()!;
                JsonElement cVal = cm.GetProperty(metricName);
                if (status == "Valid")
                {
                    Assert.Equal(JsonValueKind.Number, cVal.ValueKind);
                    Assert.Equal(vObj.GetProperty("value").GetDecimal(), cVal.GetDecimal());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, cVal.ValueKind);
                    nonValid.Add(metricName);
                }
            }

            if (nonValid.Count > 0)
            {
                JsonElement[] unavail = cm.GetProperty("unavailableMetrics").EnumerateArray().ToArray();
                Assert.Equal(nonValid, unavail.Select(u => u.GetProperty("name").GetString()!).ToArray());
            }
            else
            {
                Assert.False(cm.TryGetProperty("unavailableMetrics", out _));
            }
        }
    }

    [Fact]
    public async Task WalkForwardAnalysis_CompactReport_CompactsWindowMetricsAndPreservesDefaultByteIdentity()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string entry = """{"left":{"indicator":"SMA","parameters":{"Period":{"$param":"fast"}}},"comparison":"GreaterThan","right":{"price":"Close"}}""";
        string conditions = $$"""{"entryLong":{{entry}},"exitLong":{{Exit}}}""";
        string baseArgs = $$$"""{"symbol":"MSFT","conditions":{{{conditions}}},"parameterRanges":[{"name":"fast","start":5,"end":10,"step":5}],"objective":"totalReturn","windows":{"generator":{"mode":"Rolling","inSampleBars":80,"outOfSampleBars":40,"stepBars":40}},"minInSampleTrades":0{{{Period}}}}""";
        string falseArgs = baseArgs[..^1] + ""","compactReport":false}""";
        string compactArgs = baseArgs[..^1] + ""","compactReport":true}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_run_walk_forward_analysis", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_run_walk_forward_analysis", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_run_walk_forward_analysis", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());

        JsonElement compact = Ok(compactDoc);
        JsonElement[] verboseWindows = omitted.GetProperty("windows").EnumerateArray().ToArray();
        JsonElement[] compactWindows = compact.GetProperty("windows").EnumerateArray().ToArray();
        Assert.Equal(verboseWindows.Length, compactWindows.Length);
        Assert.NotEmpty(compactWindows);

        string[] metricKeys = { "isObjective", "isBarSharpe", "isTotalReturn", "isMaxDrawdown", "oosObjective", "oosBarSharpe", "oosTotalReturn", "oosMaxDrawdown" };
        for (int i = 0; i < compactWindows.Length; i++)
        {
            foreach (string key in metricKeys)
            {
                JsonElement vField = verboseWindows[i].GetProperty(key);
                JsonElement cField = compactWindows[i].GetProperty(key);
                if (vField.ValueKind == JsonValueKind.Null)
                {
                    Assert.Equal(JsonValueKind.Null, cField.ValueKind);
                }
                else if (vField.GetProperty("status").GetString() == "Valid")
                {
                    Assert.Equal(JsonValueKind.Number, cField.ValueKind);
                    Assert.Equal(vField.GetProperty("value").GetDecimal(), cField.GetDecimal());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, cField.ValueKind);
                }
            }
        }
    }

    // ---- compactRows: sa_calculate_chart_series, sa_extract_signal_events, sa_run_backtest, sa_calculate_volume_profile ---------

    [Theory]
    [InlineData("HeikinAshi", 8)]
    [InlineData("Renko", 14)]
    public async Task CalculateChartSeries_CompactRows_EmitsFixedSchemaRowsAndPreservesDefaultByteIdentity(string chartType, int expectedColCount)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string baseArgs = $$$"""{"symbol":"MSFT","timeFrame":"D1","chartType":"{{{chartType}}}","outputTailCount":20}""";
        string falseArgs = $$$"""{"symbol":"MSFT","timeFrame":"D1","chartType":"{{{chartType}}}","outputTailCount":20,"compactRows":false}""";
        string compactArgs = $$$"""{"symbol":"MSFT","timeFrame":"D1","chartType":"{{{chartType}}}","outputTailCount":20,"compactRows":true}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_calculate_chart_series", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_calculate_chart_series", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_calculate_chart_series", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());

        JsonElement compact = Ok(compactDoc);
        Assert.Empty(compact.GetProperty("elements").EnumerateArray());
        string[] cols = compact.GetProperty("elementColumns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(expectedColCount, cols.Length);

        JsonElement[] verboseElems = omitted.GetProperty("elements").EnumerateArray().ToArray();
        JsonElement[] rows = compact.GetProperty("elementRows").EnumerateArray().ToArray();
        Assert.Equal(verboseElems.Length, rows.Length);
        Assert.NotEmpty(rows);

        for (int i = 0; i < rows.Length; i++)
        {
            JsonElement[] cells = rows[i].EnumerateArray().ToArray();
            Assert.Equal(cols.Length, cells.Length);
            Assert.Equal(verboseElems[i].GetProperty("index").GetInt32(), cells[0].GetInt32());
            Assert.Equal(verboseElems[i].GetProperty("timestamp").GetString(), cells[1].GetString());
            Assert.Equal(verboseElems[i].GetProperty("close").GetDecimal(), cells[5].GetDecimal());
        }
    }

    [Fact]
    public async Task ExtractSignalEvents_CompactRows_EmitsEventRowsWithFourElementComparisonsAndPreservesByteIdentity()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string baseArgs = $$"""{"symbol":"MSFT","timeFrame":"D1","condition":{{SimpleCondition}},"forwardBars":[5],"maxEvents":10{{Period}}}""";
        string falseArgs = $$"""{"symbol":"MSFT","timeFrame":"D1","condition":{{SimpleCondition}},"forwardBars":[5],"maxEvents":10,"compactRows":false{{Period}}}""";
        string compactArgs = $$"""{"symbol":"MSFT","timeFrame":"D1","condition":{{SimpleCondition}},"forwardBars":[5],"maxEvents":10,"compactRows":true{{Period}}}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_extract_signal_events", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_extract_signal_events", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_extract_signal_events", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());

        JsonElement compact = Ok(compactDoc);
        Assert.Empty(compact.GetProperty("events").EnumerateArray());
        string[] cols = compact.GetProperty("eventColumns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(new[] { "barIndex", "timestamp", "close", "barsSincePreviousEvent", "comparisons", "forwardReturns", "forwardReturnsFromNextOpen", "lookAheadDiagnostic" }, cols);

        JsonElement[] verboseEvents = omitted.GetProperty("events").EnumerateArray().ToArray();
        JsonElement[] rows = compact.GetProperty("eventRows").EnumerateArray().ToArray();
        Assert.Equal(verboseEvents.Length, rows.Length);
        Assert.NotEmpty(rows);

        JsonElement[] firstCells = rows[0].EnumerateArray().ToArray();
        Assert.Equal(8, firstCells.Length);
        Assert.Equal(verboseEvents[0].GetProperty("barIndex").GetInt32(), firstCells[0].GetInt32());
        Assert.Equal(verboseEvents[0].GetProperty("close").GetDecimal(), firstCells[2].GetDecimal());
        JsonElement firstComparisonTuple = firstCells[4].EnumerateArray().First();
        Assert.Equal(4, firstComparisonTuple.GetArrayLength());
        Assert.True(firstCells[7].GetBoolean());
    }

    [Fact]
    public async Task RunBacktest_CompactRows_CompactsTradesAndEquityCurveAndPreservesByteIdentity()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string baseArgs = $$"""{"symbol":"MSFT","conditions":{{Trading}},"includeEquityCurve":"Bar","maxReturnedTrades":50{{Period}}}""";
        string falseArgs = $$"""{"symbol":"MSFT","conditions":{{Trading}},"includeEquityCurve":"Bar","maxReturnedTrades":50,"compactRows":false{{Period}}}""";
        string compactArgs = $$"""{"symbol":"MSFT","conditions":{{Trading}},"includeEquityCurve":"Bar","maxReturnedTrades":50,"compactRows":true{{Period}}}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_run_backtest", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_run_backtest", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_run_backtest", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());

        JsonElement compact = Ok(compactDoc);

        // Trades compacted
        JsonElement compactTrades = compact.GetProperty("trades");
        Assert.Empty(compactTrades.GetProperty("items").EnumerateArray());
        Assert.Equal(20, compactTrades.GetProperty("columns").GetArrayLength());
        JsonElement[] verboseTradeItems = omitted.GetProperty("trades").GetProperty("items").EnumerateArray().ToArray();
        JsonElement[] compactTradeRows = compactTrades.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(verboseTradeItems.Length, compactTradeRows.Length);
        Assert.NotEmpty(compactTradeRows);
        Assert.Equal(verboseTradeItems[0].GetProperty("tradeId").GetInt32(), compactTradeRows[0][0].GetInt32());
        Assert.Equal(verboseTradeItems[0].GetProperty("entryDate").GetString(), compactTradeRows[0][2].GetString());
        Assert.Equal(verboseTradeItems[0].GetProperty("closedNet").GetDecimal(), compactTradeRows[0][8].GetDecimal());

        // Equity curve compacted
        JsonElement compactCurve = compact.GetProperty("equityCurve");
        Assert.Empty(compactCurve.GetProperty("points").EnumerateArray());
        Assert.Equal(new[] { "date", "equity", "drawdownRatio", "reason" }, compactCurve.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
        JsonElement[] verboseCurvePoints = omitted.GetProperty("equityCurve").GetProperty("points").EnumerateArray().ToArray();
        JsonElement[] compactCurveRows = compactCurve.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(verboseCurvePoints.Length, compactCurveRows.Length);
        Assert.Equal(verboseCurvePoints[0].GetProperty("date").GetString(), compactCurveRows[0][0].GetString());
        Assert.Equal(verboseCurvePoints[0].GetProperty("equity").GetDecimal(), compactCurveRows[0][1].GetDecimal());
    }

    [Fact]
    public async Task CalculateVolumeProfile_CompactRows_EnforcesIncludeBinsAndEmitsBinRows()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string baseArgs = """{"symbol":"MSFT","timeFrame":"D1","binCount":10}""";
        string falseArgs = """{"symbol":"MSFT","timeFrame":"D1","binCount":10,"compactRows":false}""";
        string compactArgs = """{"symbol":"MSFT","timeFrame":"D1","binCount":10,"compactRows":true}""";

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_calculate_volume_profile", baseArgs);
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_calculate_volume_profile", falseArgs);
        using JsonDocument compactDoc = await CallAsync(dispatcher, "sa_calculate_volume_profile", compactArgs);

        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());

        JsonElement compact = Ok(compactDoc);
        Assert.Empty(compact.GetProperty("bins").EnumerateArray());
        Assert.Equal(new[] { "priceLow", "priceHigh", "volume" }, compact.GetProperty("binColumns").EnumerateArray().Select(c => c.GetString()!).ToArray());

        JsonElement[] verboseBins = omitted.GetProperty("bins").EnumerateArray().ToArray();
        JsonElement[] compactRows = compact.GetProperty("binRows").EnumerateArray().ToArray();
        Assert.Equal(verboseBins.Length, compactRows.Length);
        Assert.Equal(10, compactRows.Length);
        Assert.Equal(verboseBins[0].GetProperty("priceLow").GetDecimal(), compactRows[0][0].GetDecimal());
        Assert.Equal(verboseBins[0].GetProperty("priceHigh").GetDecimal(), compactRows[0][1].GetDecimal());
        Assert.Equal(verboseBins[0].GetProperty("volume").GetInt64(), compactRows[0][2].GetInt64());

        // Mutual exclusivity: compactRows: true + includeBins: false -> INVALID_ARGUMENT
        using JsonDocument conflict = await CallAsync(dispatcher, "sa_calculate_volume_profile", """{"symbol":"MSFT","timeFrame":"D1","includeBins":false,"compactRows":true}""");
        Assert.True(McpTestSupport.IsError(conflict));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(conflict));
        Assert.Equal("compactRows", McpTestSupport.Structured(conflict).GetProperty("param").GetString());
    }

    // ---- fields on sa_list_tickers -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListTickers_FieldsFiltersSummaryPropertiesAndValidatesArguments()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with
        {
            ShortName = "Microsoft",
            LongName = "Microsoft Corporation",
            Region = "US",
            Sector = "Technology",
            Industry = "Software",
            Currency = "USD",
        });

        McpMessageDispatcher dispatcher = await StartAsync();

        // Selected fields only
        using JsonDocument filtered = await CallAsync(dispatcher, "sa_list_tickers", """{"includeMetadataSummary":true,"fields":["sector","symbol"]}""");
        JsonElement msft = Ok(filtered).GetProperty("tickers").EnumerateArray().First(t => t.GetProperty("symbol").GetString() == "MSFT");
        string[] keys = msft.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "symbol", "hasMetadata", "sector" }, keys);
        Assert.Equal("Technology", msft.GetProperty("sector").GetString());

        // fields without includeMetadataSummary: true -> INVALID_ARGUMENT at fields
        using JsonDocument noSummary = await CallAsync(dispatcher, "sa_list_tickers", """{"fields":["sector"]}""");
        Assert.True(McpTestSupport.IsError(noSummary));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(noSummary));
        Assert.Equal("fields", McpTestSupport.Structured(noSummary).GetProperty("param").GetString());

        // Empty fields -> INVALID_ARGUMENT at fields
        using JsonDocument emptyFields = await CallAsync(dispatcher, "sa_list_tickers", """{"includeMetadataSummary":true,"fields":[]}""");
        Assert.True(McpTestSupport.IsError(emptyFields));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(emptyFields));

        // Unknown field -> INVALID_ARGUMENT at fields[0]
        using JsonDocument unknownField = await CallAsync(dispatcher, "sa_list_tickers", """{"includeMetadataSummary":true,"fields":["marketCap"]}""");
        Assert.True(McpTestSupport.IsError(unknownField));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(unknownField));
        Assert.Equal("fields[0]", McpTestSupport.Structured(unknownField).GetProperty("param").GetString());
    }

    // ---- snippetOnly on sa_search_notes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SearchNotes_SnippetOnlyTruncatesLongBodiesSafelyAndPreservesDefaultByteIdentity()
    {
        string shortBody = "Short note about earnings.";
        string prefix = new string('a', 250);
        string longBody = prefix + " targetword appears right here " + new string('b', 150);
        await AddNoteAsync(shortBody, day: 10);
        await AddNoteAsync(longBody, day: 11);

        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument omittedDoc = await CallAsync(dispatcher, "sa_search_notes", """{}""");
        using JsonDocument falseDoc = await CallAsync(dispatcher, "sa_search_notes", """{"snippetOnly":false}""");
        JsonElement omitted = Ok(omittedDoc);
        Assert.Equal(omitted.GetRawText(), Ok(falseDoc).GetRawText());
        Assert.False(omitted.GetProperty("notes")[0].TryGetProperty("bodyTruncated", out _));

        using JsonDocument snippetDoc = await CallAsync(dispatcher, "sa_search_notes", """{"query":"targetword","snippetOnly":true}""");
        JsonElement[] matchedNotes = Ok(snippetDoc).GetProperty("notes").EnumerateArray().ToArray();
        Assert.Single(matchedNotes);
        string returnedBody = matchedNotes[0].GetProperty("body").GetString()!;
        Assert.True(returnedBody.Length <= NoteToolSupport.BodySnippetMaxChars);
        Assert.Contains("targetword", returnedBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(matchedNotes[0].GetProperty("bodyTruncated").GetBoolean());

        // Short note stays untruncated with bodyTruncated: false
        using JsonDocument shortSnippetDoc = await CallAsync(dispatcher, "sa_search_notes", """{"query":"earnings","snippetOnly":true}""");
        JsonElement shortNote = Ok(shortSnippetDoc).GetProperty("notes").EnumerateArray().Single();
        Assert.Equal(shortBody, shortNote.GetProperty("body").GetString());
        Assert.False(shortNote.GetProperty("bodyTruncated").GetBoolean());

        // Surrogate-pair boundary safety test
        string emoji = "\uD83D\uDE80";
        string surrogateBody = new string('x', 39) + string.Concat(Enumerable.Repeat(emoji, 120));
        (string extracted, bool wasTruncated) = NoteToolSupport.ExtractSnippet(surrogateBody, new[] { "missing" }, maxChars: 200);
        Assert.True(wasTruncated);
        Assert.True(extracted.Length <= 200);
        Assert.False(char.IsLowSurrogate(extracted[0]));
        Assert.False(char.IsHighSurrogate(extracted[^1]));
    }

    // ---- CompactUsageHint & PNG IHDR vision-token accounting ---------------------------------------------------------------------

    [Fact]
    public async Task TokenLimitExceeded_UsesToolSpecificHintWithoutSuggestingInvalidArguments()
    {
        await AddNoteAsync(new string('n', 5000), day: 15);
        McpMessageDispatcher dispatcher = await StartAsync(("AiIntegration:MaxResponseTokens", "1024"));

        // sa_search_notes exceeds 1024 tokens and must suggest snippetOnly/limit, never compactRows/compactReport
        using JsonDocument notesExceeded = await CallAsync(dispatcher, "sa_search_notes", "{}");
        Assert.True(McpTestSupport.IsError(notesExceeded));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(notesExceeded));
        string hint = McpTestSupport.Structured(notesExceeded).GetProperty("hint").GetString()!;
        Assert.Contains("snippetOnly", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("compactRows", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("compactReport", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("includeSeries", hint, StringComparison.Ordinal);

        // Verify every CompactUsageHint across all 65 tools only mentions arguments declared in its own InputSchemaJson
        string emptyRoot = Path.Combine(Path.GetTempPath(), "sa_hint_check_" + Guid.NewGuid().ToString("N"));
        using McpServerRuntime full = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: true,
            enableDiscussion: true);

        string[] knownTokenOptions =
        {
            "compactRows", "compactReport", "includeSeries", "summary", "decimalPlaces", "count",
            "startDate", "endDate", "outputTailCount", "lookbackBars", "indicators", "symbols",
            "includeBooleanSeries", "maxEvents", "maxReturnedTrades", "includeEquityCurve",
            "maxReturnedTradesPerSymbol", "topK", "windows", "includeBins", "binCount",
            "snippetOnly", "includeParentContext", "limit", "includeMetadataSummary", "fields",
            "includeBarCoordinates", "width", "height", "barCount",
        };

        foreach (RegisteredTool registered in full.Tools.Tools)
        {
            if (registered.Tool.CompactUsageHint is not { } toolHint)
            {
                continue;
            }

            var props = registered.Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string arg in knownTokenOptions)
            {
                if (Regex.IsMatch(toolHint, $@"\b{Regex.Escape(arg)}\b", RegexOptions.CultureInvariant))
                {
                    Assert.True(props.Contains(arg), $"Tool '{registered.Tool.Name}' CompactUsageHint mentions '{arg}' which is not in its InputSchemaJson.");
                }
            }
        }
    }

    [Fact]
    public async Task MaxResponseTokens_ImageToolAccountsForPngIhdrAndFallbackVisionTokens()
    {
        // Valid 1200x800 PNG header -> ceil(960000 / 750) = 1280 vision tokens
        byte[] validPng = new byte[64];
        byte[] pngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        pngSig.CopyTo(validPng, 0);
        validPng[12] = (byte)'I';
        validPng[13] = (byte)'H';
        validPng[14] = (byte)'D';
        validPng[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(validPng.AsSpan(16, 4), 1200);
        BinaryPrimitives.WriteUInt32BigEndian(validPng.AsSpan(20, 4), 800);
        Assert.Equal(1280L, ToolExecutor.EstimateImageVisionTokens(validPng));

        // Zero-width/corrupted PNG -> falls back to ceil(len / 30), never 0
        byte[] zeroWidthPng = (byte[])validPng.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(zeroWidthPng.AsSpan(16, 4), 0);
        Assert.Equal(3L, ToolExecutor.EstimateImageVisionTokens(zeroWidthPng));
        Assert.Equal(500L, ToolExecutor.EstimateImageVisionTokens(new byte[15_000]));
        Assert.Equal(0L, ToolExecutor.EstimateImageVisionTokens(ReadOnlySpan<byte>.Empty));

        // Image tool with small JSON (~10 tokens) + 1280 vision tokens > MaxResponseTokens (1024) -> LIMIT_EXCEEDED
        var imageTool = new FakeImageTool("sa_img_budget", (_, _) => Task.FromResult(new ToolImageResult("{\"ok\":true}", validPng)));
        var limits = new McpLimits { MaxResponseTokens = 1024 };
        var log = new LogSink(TextWriter.Null);
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(Array.Empty<string?>()), log);
        var dispatcher = new McpMessageDispatcher(log, new McpToolRegistry(new IMcpTool[] { imageTool }), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument exceeded = (await McpTestSupport.CallAsync(dispatcher, "sa_img_budget"))!;
        Assert.True(McpTestSupport.IsError(exceeded));
        Assert.Equal(ToolErrorCodes.LimitExceeded, McpTestSupport.ErrorCodeOf(exceeded));
        Assert.Contains("tokens", McpTestSupport.Structured(exceeded).GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- --compact-defaults opt-in defaults & exclusions ----------------------------------------------------------------------

    [Fact]
    public async Task CompactDefaults_AppliesAllowlistedCompactAndRoundedDefaults_AndExplicitCallerValuesWin()
    {
        await AddNoteAsync("Long note body " + new string('x', 500), 10);
        McpMessageDispatcher compactDispatcher = await StartWithCompactDefaultsAsync(compactDefaults: true);

        // sa_get_capabilities discloses compactDefaults: true
        using JsonDocument caps = await CallAsync(compactDispatcher, "sa_get_capabilities", "{}");
        Assert.True(Ok(caps).GetProperty("compactDefaults").GetBoolean());

        // sa_get_candles defaults compactRows: true (candles empty, candleRows populated)
        using JsonDocument candlesCompact = await CallAsync(compactDispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":5}""");
        JsonElement candlesResult = Ok(candlesCompact);
        Assert.Empty(candlesResult.GetProperty("candles").EnumerateArray());
        Assert.Equal(5, candlesResult.GetProperty("candleRows").GetArrayLength());

        // Explicit compactRows: false overrides compact defaults
        using JsonDocument candlesExplicit = await CallAsync(compactDispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":5,"compactRows":false}""");
        JsonElement candlesExplicitResult = Ok(candlesExplicit);
        Assert.Equal(5, candlesExplicitResult.GetProperty("candles").GetArrayLength());
        Assert.False(candlesExplicitResult.TryGetProperty("candleRows", out _));

        // sa_calculate_indicators defaults includeSeries: false, summary: true, decimalPlaces: 4 when omitted
        using JsonDocument indCompact = await CallAsync(compactDispatcher, "sa_calculate_indicators", """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}]}""");
        JsonElement indSma = Ok(indCompact).GetProperty("results").GetProperty("sma");
        Assert.False(indSma.TryGetProperty("series", out _));
        Assert.True(indSma.TryGetProperty("summary", out _));

        // When lookbackOffsets is supplied without summary, compact defaults do NOT force summary: true
        using JsonDocument indPastOnly = await CallAsync(compactDispatcher, "sa_calculate_indicators", """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"lookbackOffsets":[0]}""");
        JsonElement indPastResult = Ok(indPastOnly).GetProperty("results").GetProperty("sma");
        Assert.False(indPastResult.TryGetProperty("series", out _));
        Assert.False(indPastResult.TryGetProperty("summary", out _));
        Assert.True(indPastResult.TryGetProperty("pastValues", out _));

        // sa_calculate_volume_profile: compactRows: true by default, EXCEPT when caller sets includeBins: false
        using JsonDocument vpCompact = await CallAsync(compactDispatcher, "sa_calculate_volume_profile", """{"symbol":"MSFT","timeFrame":"D1","binCount":10}""");
        JsonElement vpCompactResult = Ok(vpCompact);
        Assert.Empty(vpCompactResult.GetProperty("bins").EnumerateArray());
        Assert.True(vpCompactResult.TryGetProperty("binRows", out _));

        using JsonDocument vpNoBins = await CallAsync(compactDispatcher, "sa_calculate_volume_profile", """{"symbol":"MSFT","timeFrame":"D1","binCount":10,"includeBins":false}""");
        JsonElement vpNoBinsResult = Ok(vpNoBins);
        Assert.False(vpNoBinsResult.TryGetProperty("bins", out _));
        Assert.False(vpNoBinsResult.TryGetProperty("binRows", out _));

        // sa_search_notes: snippetOnly: true by default
        using JsonDocument notesCompact = await CallAsync(compactDispatcher, "sa_search_notes", """{}""");
        JsonElement note0 = Ok(notesCompact).GetProperty("notes")[0];
        Assert.True(note0.GetProperty("bodyTruncated").GetBoolean());

        // sa_render_chart_image: includeBarCoordinates: false by default
        using JsonDocument chartCompact = await CallAsync(compactDispatcher, "sa_render_chart_image", """{"symbol":"MSFT","timeFrame":"D1","barCount":30}""");
        JsonElement chartResult = Ok(chartCompact);
        Assert.False(chartResult.TryGetProperty("bars", out JsonElement barsArr) && barsArr.GetArrayLength() > 0);

        // sa_run_backtest: compactReport: true, compactRows: true, maxReturnedTrades: <= 20 by default
        using JsonDocument btCompact = await CallAsync(compactDispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","timeFrame":"D1","conditions":{{Trading}}{{Period}}}""");
        JsonElement btResult = Ok(btCompact);
        Assert.True(btResult.GetProperty("report").TryGetProperty("metricUnits", out _));
        Assert.Empty(btResult.GetProperty("trades").GetProperty("items").EnumerateArray());
        Assert.True(btResult.GetProperty("trades").TryGetProperty("rows", out _));
        Assert.True(btResult.GetProperty("trades").GetProperty("returnedTrades").GetInt32() <= RunBacktestTool.DefaultCompactMaxReturnedTrades);

        // CRITICAL SAFEGUARD: sa_evaluate_expressions MUST NOT apply decimalPlaces: 4 under --compact-defaults
        using JsonDocument calcCompact = await CallAsync(compactDispatcher, "sa_evaluate_expressions", """{"expressions":[{"name":"ratio","expression":"1 / 7"}]}""");
        JsonElement calcResult = Ok(calcCompact);
        Assert.False(calcResult.TryGetProperty("decimalPlaces", out _));
        decimal ratioVal = calcResult.GetProperty("results")[0].GetProperty("value").GetDecimal();
        Assert.NotEqual(0.1429m, ratioVal);
        Assert.True(Math.Abs(ratioVal - (1m / 7m)) < 0.0000000001m);

        using JsonDocument rejectedImplicitSeriesFalse = await CallAsync(
            compactDispatcher,
            "sa_calculate_indicators",
            """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"summary":false}""");
        AssertError(rejectedImplicitSeriesFalse, ToolErrorCodes.InvalidArgument, "includeSeries");

        using JsonDocument rejectedExplicitSeriesFalse = await CallAsync(
            compactDispatcher,
            "sa_calculate_indicators",
            """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"includeSeries":false,"summary":false}""");
        AssertError(rejectedExplicitSeriesFalse, ToolErrorCodes.InvalidArgument, "includeSeries");

        // Omitting includeSeries while passing outputTailCount under --compact-defaults implies includeSeries: true
        using JsonDocument tailImpliesSeries = await CallAsync(
            compactDispatcher,
            "sa_calculate_indicators",
            """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"outputTailCount":5}""");
        JsonElement tailResult = Ok(tailImpliesSeries);
        Assert.Equal(5, tailResult.GetProperty("returnedBarCount").GetInt32());
        JsonElement tailSma = tailResult.GetProperty("results").GetProperty("sma");
        Assert.Equal(5, tailSma.GetProperty("series").GetProperty("SMA").GetArrayLength());
        Assert.False(tailSma.TryGetProperty("summary", out _));

        // Explicit includeSeries: false + outputTailCount MUST still fail with INVALID_ARGUMENT
        using JsonDocument explicitFalseWithTail = await CallAsync(
            compactDispatcher,
            "sa_calculate_indicators",
            """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"includeSeries":false,"summary":true,"outputTailCount":5}""");
        AssertError(explicitFalseWithTail, ToolErrorCodes.InvalidArgument, "outputTailCount");

        // sa_calculate_indicators_batch still rejects outputTailCount when includeSeries is omitted
        using JsonDocument batchOmittedSeriesWithTail = await CallAsync(
            compactDispatcher,
            "sa_calculate_indicators_batch",
            """{"symbols":["MSFT"],"timeFrame":"D1","indicators":[{"id":"sma","type":"SMA","parameters":{"Period":5}}],"outputTailCount":2}""");
        AssertError(batchOmittedSeriesWithTail, ToolErrorCodes.InvalidArgument, "outputTailCount");

        // tools/list under --compact-defaults reflects active defaults in InputSchemaJson
        using JsonDocument compactList = JsonDocument.Parse((await compactDispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":99,"method":"tools/list"}""", CancellationToken.None))!);
        var schemasByName = compactList.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("inputSchema").GetProperty("properties"), StringComparer.Ordinal);

        Assert.Contains("Default false", schemasByName["sa_calculate_indicators"].GetProperty("includeSeries").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Default true", schemasByName["sa_calculate_indicators"].GetProperty("summary").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Equal(4, schemasByName["sa_calculate_indicators"].GetProperty("decimalPlaces").GetProperty("default").GetInt32());
        Assert.Equal(4, schemasByName["sa_calculate_indicators_batch"].GetProperty("decimalPlaces").GetProperty("default").GetInt32());
        Assert.Equal(4, schemasByName["sa_get_candles"].GetProperty("decimalPlaces").GetProperty("default").GetInt32());
        Assert.Contains("Default true.", schemasByName["sa_get_candles"].GetProperty("compactRows").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Equal(20, schemasByName["sa_run_backtest"].GetProperty("maxReturnedTrades").GetProperty("default").GetInt32());
        Assert.Contains("Default true.", schemasByName["sa_run_backtest"].GetProperty("compactReport").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Default true.", schemasByName["sa_run_backtest"].GetProperty("compactRows").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Default true.", schemasByName["sa_search_notes"].GetProperty("snippetOnly").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Default false.", schemasByName["sa_render_chart_image"].GetProperty("includeBarCoordinates").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Default true", schemasByName["sa_calculate_volume_profile"].GetProperty("compactRows").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Equal(4, schemasByName["sa_get_seasonality"].GetProperty("decimalPlaces").GetProperty("default").GetInt32());
        Assert.False(schemasByName["sa_evaluate_expressions"].GetProperty("decimalPlaces").TryGetProperty("default", out _));
    }

    [Fact]
    public async Task CompactDefaults_RunBacktest_CapsDefaultMaxReturnedTradesAtTwentyAndAllowsExplicitOverride()
    {
        using ReadOnlyDataTreeFixture fastFixture = ReadOnlyDataTreeFixture.Create(180, "FAST");
        decimal[] closes = Enumerable.Range(0, 180).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 1.2)), 2)).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(fastFixture.DailyDirectory, "FAST", closes);

        using McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(fastFixture)).Build(),
            new LogSink(new StringWriter()),
            compactDefaults: true);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument compactDoc = await CallAsync(runtime.Dispatcher, "sa_run_backtest", $$"""{"symbol":"FAST","timeFrame":"D1","conditions":{{Trading}}}""");
        JsonElement compactTrades = Ok(compactDoc).GetProperty("trades");
        int totalTrades = compactTrades.GetProperty("totalTrades").GetInt32();
        Assert.True(totalTrades > RunBacktestTool.DefaultCompactMaxReturnedTrades, $"Expected >{RunBacktestTool.DefaultCompactMaxReturnedTrades} trades, got {totalTrades}.");
        Assert.Equal(RunBacktestTool.DefaultCompactMaxReturnedTrades, compactTrades.GetProperty("returnedTrades").GetInt32());
        Assert.True(compactTrades.GetProperty("truncated").GetBoolean());
        Assert.Equal(RunBacktestTool.DefaultCompactMaxReturnedTrades, compactTrades.GetProperty("rows").GetArrayLength());

        using JsonDocument overrideDoc = await CallAsync(runtime.Dispatcher, "sa_run_backtest", $$"""{"symbol":"FAST","timeFrame":"D1","conditions":{{Trading}},"maxReturnedTrades":50}""");
        JsonElement overrideTrades = Ok(overrideDoc).GetProperty("trades");
        Assert.Equal(Math.Min(50, totalTrades), overrideTrades.GetProperty("returnedTrades").GetInt32());

        using JsonDocument zeroDoc = await CallAsync(runtime.Dispatcher, "sa_run_backtest", $$"""{"symbol":"FAST","timeFrame":"D1","conditions":{{Trading}},"maxReturnedTrades":0}""");
        JsonElement zeroTrades = Ok(zeroDoc).GetProperty("trades");
        Assert.Equal(0, zeroTrades.GetProperty("returnedTrades").GetInt32());
        Assert.Empty(zeroTrades.GetProperty("rows").EnumerateArray());
    }

    private static void AssertError(JsonDocument response, string expectedCode, string expectedParam)
    {
        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(expectedCode, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(expectedParam, McpTestSupport.Structured(response).GetProperty("param").GetString());
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
