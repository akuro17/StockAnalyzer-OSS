using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_list_tickers, sa_get_candles and sa_get_ticker_metadata through the real composition on a throw-away data tree.</summary>
public sealed class DataToolsTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, "msft", "7203.T", "zzz.empty");
    private readonly List<McpServerRuntime> _runtimes = new();

    public DataToolsTests()
    {
        File.Delete(Path.Combine(_fixture.DailyDirectory, "ZZZ-EMPTY.parquet")); // listed, but nothing stored
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, argumentsJson))!;

    private static string[] Symbols(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("tickers").EnumerateArray().Select(t => t.GetProperty("symbol").GetString()!).ToArray();

    // ---- sa_list_tickers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListTickers_ReturnsCanonicalSymbolsSortedOrdinally()
    {
        using var response = await CallAsync(await StartAsync(), "sa_list_tickers");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { "7203-T", "MSFT", "ZZZ-EMPTY" }, Symbols(response));
        Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, result.GetProperty("offset").GetInt32());
        Assert.Equal(100, result.GetProperty("limit").GetInt32());
        Assert.False(result.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData(0, 2, new[] { "7203-T", "MSFT" }, true)]
    [InlineData(2, 2, new[] { "ZZZ-EMPTY" }, false)]
    [InlineData(10, 2, new string[0], false)]
    public async Task ListTickers_Pages(int offset, int limit, string[] expected, bool hasMore)
    {
        using var response = await CallAsync(await StartAsync(), "sa_list_tickers", $$$"""{"offset":{{{offset}}},"limit":{{{limit}}}}""");

        Assert.Equal(expected, Symbols(response));
        Assert.Equal(hasMore, McpTestSupport.Structured(response).GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, McpTestSupport.Structured(response).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListTickers_SearchMatchesTheSymbolIgnoringCaseAndTheStoredShortName()
    {
        await _fixture.SeedMetadataAsync("7203-T", TickerMetadata.Unknown with { ShortName = "Toyota Motor Corp" });
        var dispatcher = await StartAsync();

        using var bySymbol = await CallAsync(dispatcher, "sa_list_tickers", """{"searchQuery":"msf"}""");
        using var byName = await CallAsync(dispatcher, "sa_list_tickers", """{"searchQuery":"TOYOTA"}""");
        using var none = await CallAsync(dispatcher, "sa_list_tickers", """{"searchQuery":"nothing-matches"}""");

        Assert.Equal(new[] { "MSFT" }, Symbols(bySymbol));
        Assert.Equal(new[] { "7203-T" }, Symbols(byName));
        Assert.Empty(Symbols(none));
        Assert.Equal(0, McpTestSupport.Structured(none).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListTickers_SummaryAddsTheStoredIdentityFieldsOnlyToTheReturnedPage()
    {
        await _fixture.SeedMetadataAsync("7203-T", TickerMetadata.Unknown with { ShortName = "Toyota", Sector = "Autos", Currency = "JPY" });
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_list_tickers", """{"includeMetadataSummary":true,"limit":2}""");

        JsonElement[] tickers = McpTestSupport.Structured(response).GetProperty("tickers").EnumerateArray().ToArray();
        Assert.Equal(2, tickers.Length);
        Assert.True(tickers[0].GetProperty("hasMetadata").GetBoolean());
        Assert.Equal("Toyota", tickers[0].GetProperty("shortName").GetString());
        Assert.Equal("Autos", tickers[0].GetProperty("sector").GetString());
        Assert.Equal("JPY", tickers[0].GetProperty("currency").GetString());
        Assert.False(tickers[1].GetProperty("hasMetadata").GetBoolean());
        Assert.False(tickers[1].TryGetProperty("shortName", out _));
    }

    [Fact]
    public async Task ListTickers_RejectsBadPagingAndTooLargeRequests()
    {
        var dispatcher = await StartAsync(("MaxTickersPerPage", "5"), ("DefaultTickerPageSize", "5"), ("MaxSymbolsPerMetadataCall", "2"));

        async Task<string> CodeOf(string arguments)
        {
            using var response = await CallAsync(dispatcher, "sa_list_tickers", arguments);
            return McpTestSupport.ErrorCodeOf(response);
        }

        Assert.Equal("INVALID_ARGUMENT", await CodeOf("""{"offset":-1}"""));
        Assert.Equal("INVALID_ARGUMENT", await CodeOf("""{"limit":0}"""));
        Assert.Equal("INVALID_ARGUMENT", await CodeOf("""{"searchQuery":"   "}"""));
        Assert.Equal("INVALID_ARGUMENT", await CodeOf("""{"unknown":1}"""));
        Assert.Equal("LIMIT_EXCEEDED", await CodeOf("""{"limit":6}"""));
        Assert.Equal("LIMIT_EXCEEDED", await CodeOf("""{"limit":3,"includeMetadataSummary":true}"""));
    }

    // ---- sa_get_candles -------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<CandleData>> StoredDailyAsync(string symbol, int count)
    {
        using var db = new DuckDBConnectionManager();
        return await new ParquetDataService(db, Options.Create(_fixture.Settings)).LoadCandlesAsync(symbol, TimeFrame.D1, count);
    }

    private static JsonElement[] Candles(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("candles").EnumerateArray().ToArray();

    [Fact]
    public async Task GetCandles_ReturnsTheStoredBarsExactlyAndInChronologicalOrder()
    {
        var stored = await StoredDailyAsync("MSFT", 20);
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"msft","timeFrame":"D1"}""");

        JsonElement result = McpTestSupport.Structured(response);
        JsonElement[] candles = Candles(response);
        Assert.Equal("MSFT", result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal("bars", result.GetProperty("periodUnit").GetString());
        Assert.Equal("price", result.GetProperty("units").GetProperty("price").GetString());
        Assert.Equal("shares", result.GetProperty("units").GetProperty("volume").GetString());
        Assert.Equal(20, result.GetProperty("barCount").GetInt32());
        Assert.Equal(200, result.GetProperty("requestedCount").GetInt32());
        Assert.Equal("2024-01-01", result.GetProperty("earliestAvailableTimestamp").GetString());
        Assert.Equal(stored.Count, candles.Length);
        for (int i = 0; i < stored.Count; i++)
        {
            Assert.Equal(stored[i].Timestamp.ToString("yyyy-MM-dd"), candles[i].GetProperty("timestamp").GetString());
            Assert.Equal(stored[i].Open, candles[i].GetProperty("open").GetDecimal());
            Assert.Equal(stored[i].High, candles[i].GetProperty("high").GetDecimal());
            Assert.Equal(stored[i].Low, candles[i].GetProperty("low").GetDecimal());
            Assert.Equal(stored[i].Close, candles[i].GetProperty("close").GetDecimal());
            Assert.Equal(stored[i].Volume, candles[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task GetCandles_CountSelectsTheLatestBarsAndDotAndDashAreTheSameSymbol()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", """{"symbol":"7203.t","timeFrame":"D1","count":5}""");

        JsonElement[] candles = Candles(response);
        Assert.Equal("7203-T", McpTestSupport.Structured(response).GetProperty("symbol").GetString());
        Assert.Equal(new[] { "2024-01-16", "2024-01-17", "2024-01-18", "2024-01-19", "2024-01-20" }, candles.Select(c => c.GetProperty("timestamp").GetString()).ToArray());
        Assert.False(McpTestSupport.Structured(response).GetProperty("filteredByDate").GetBoolean());
    }

    [Fact]
    public async Task GetCandles_DatesFilterInsideTheLoadedBarsInclusively()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","startDate":"2024-01-05","endDate":"2024-01-07"}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { "2024-01-05", "2024-01-06", "2024-01-07" }, Candles(response).Select(c => c.GetProperty("timestamp").GetString()).ToArray());
        Assert.True(result.GetProperty("filteredByDate").GetBoolean());
        Assert.Equal(3, result.GetProperty("matchedBarCount").GetInt32());
        Assert.False(result.GetProperty("truncatedByLoadCap").GetBoolean());
    }

    [Fact]
    public async Task GetCandles_DatesThenCountKeepTheLatestMatchingBars()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","startDate":"2024-01-05","count":2}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { "2024-01-19", "2024-01-20" }, Candles(response).Select(c => c.GetProperty("timestamp").GetString()).ToArray());
        Assert.Equal(16, result.GetProperty("matchedBarCount").GetInt32());
        Assert.Equal(2, result.GetProperty("barCount").GetInt32());
    }

    [Fact]
    public async Task GetCandles_ReportsWhenTheLoadCapCutsOffTheRequestedStart()
    {
        var dispatcher = await StartAsync(("MaxCandlesPerRequest", "5"), ("DefaultCandleCount", "3"), ("MaxOutputTailBars", "3"), ("DefaultOutputTailBars", "3"));

        using var response = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","startDate":"2024-01-02"}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("truncatedByLoadCap").GetBoolean());
        Assert.Equal(5, result.GetProperty("loadedBarCount").GetInt32());
        Assert.Equal(3, result.GetProperty("barCount").GetInt32());
    }

    [Fact]
    public async Task GetCandles_DoesNotReportATruncationWhenTheHistoryItselfStartsLater()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","startDate":"2020-01-01"}""");

        Assert.False(McpTestSupport.Structured(response).GetProperty("truncatedByLoadCap").GetBoolean());
    }

    [Fact]
    public async Task GetCandles_WeeklyAndMonthlyBarsAreAggregatedAndTheLeadingPartialBarIsDroppedWhenSurplus()
    {
        var dispatcher = await StartAsync();

        using var two = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"W1","count":2}""");
        using var all = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"W1","count":3}""");
        using var month = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"MN1","count":1}""");

        Assert.Equal(2, Candles(two).Length);
        Assert.True(McpTestSupport.Structured(two).GetProperty("droppedLeadingBar").GetBoolean());
        Assert.Equal(3, Candles(all).Length);
        Assert.False(McpTestSupport.Structured(all).GetProperty("droppedLeadingBar").GetBoolean());
        string[] stamps = Candles(all).Select(c => c.GetProperty("timestamp").GetString()!).ToArray();
        Assert.Equal(stamps.OrderBy(s => s, StringComparer.Ordinal), stamps);
        Assert.Equal("W1", McpTestSupport.Structured(two).GetProperty("timeFrame").GetString());
        Assert.Single(Candles(month));
        Assert.Equal("2024-01-01", Candles(month)[0].GetProperty("timestamp").GetString());
    }

    [Theory]
    [InlineData("M1")]
    [InlineData("M5")]
    [InlineData("M15")]
    [InlineData("M30")]
    [InlineData("H1")]
    [InlineData("H4")]
    public async Task GetCandles_IntradayFramesAreUnsupported(string frame)
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", $$$"""{"symbol":"MSFT","timeFrame":"{{{frame}}}"}""");

        Assert.Equal("UNSUPPORTED_TIMEFRAME", McpTestSupport.ErrorCodeOf(response));
        Assert.Contains("D1", McpTestSupport.Structured(response).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task GetCandles_RejectsBadInput()
    {
        var dispatcher = await StartAsync(("MaxCandlesPerRequest", "5"), ("DefaultCandleCount", "3"), ("MaxOutputTailBars", "3"), ("DefaultOutputTailBars", "3"));

        async Task<(string Code, string? Param)> Failure(string arguments)
        {
            using var response = await CallAsync(dispatcher, "sa_get_candles", arguments);
            JsonElement error = McpTestSupport.Structured(response);
            return (error.GetProperty("code").GetString()!, error.TryGetProperty("param", out JsonElement p) ? p.GetString() : null);
        }

        Assert.Equal(("INVALID_ARGUMENT", "timeFrame"), await Failure("""{"symbol":"MSFT","timeFrame":"X1"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbol"), await Failure("""{"symbol":"../MSFT","timeFrame":"D1"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbol"), await Failure("""{"symbol":"C:\\x","timeFrame":"D1"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbol"), await Failure("""{"symbol":"a/b","timeFrame":"D1"}"""));
        Assert.Equal(("SYMBOL_NOT_FOUND", "symbol"), await Failure("""{"symbol":"NOSUCH","timeFrame":"D1"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "count"), await Failure("""{"symbol":"MSFT","timeFrame":"D1","count":0}"""));
        Assert.Equal(("LIMIT_EXCEEDED", "count"), await Failure("""{"symbol":"MSFT","timeFrame":"D1","count":6}"""));
        Assert.Equal(("INVALID_ARGUMENT", "startDate"), await Failure("""{"symbol":"MSFT","timeFrame":"D1","startDate":"2024/01/05"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "endDate"), await Failure("""{"symbol":"MSFT","timeFrame":"D1","endDate":"2024-13-45"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "startDate"), await Failure("""{"symbol":"MSFT","timeFrame":"D1","startDate":"2024-01-10","endDate":"2024-01-09"}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbol"), await Failure("""{"symbol":"","timeFrame":"D1"}"""));
    }

    [Fact]
    public async Task GetCandles_AListedSymbolWithoutStoredBarsIsASuccessWithNoCandles()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_candles", """{"symbol":"zzz.empty","timeFrame":"D1"}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(McpTestSupport.IsError(response));
        Assert.Equal(0, result.GetProperty("barCount").GetInt32());
        Assert.Empty(Candles(response));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("earliestAvailableTimestamp").ValueKind);
    }

    // ---- sa_get_ticker_metadata -----------------------------------------------------------------------------------

    private static JsonElement[] Results(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("results").EnumerateArray().ToArray();

    [Fact]
    public async Task GetTickerMetadata_NothingStoredIsFoundFalseWithTheLatestBarDate()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_ticker_metadata", """{"symbols":["msft"]}""");

        JsonElement only = Results(response).Single();
        Assert.Equal("MSFT", only.GetProperty("symbol").GetString());
        Assert.False(only.GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("metadata").ValueKind);
        Assert.Equal("2024-01-20", only.GetProperty("timeSeriesLastUpdated").GetString());
    }

    [Fact]
    public async Task GetTickerMetadata_ReturnsTheStoredFieldsIncludingTheUsersOwnEntries()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with
        {
            ShortName = "Microsoft",
            Sector = "Technology",
            CurrentPrice = 123.45m,
            Tag = "core holding",
            Notes = "my private note",
            Reminder = "check earnings",
            Long = 100.5m,
            ExitLong = 130m,
            StopLossLong = 90m,
        });
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, "sa_get_ticker_metadata", """{"symbols":["MSFT"]}""");

        JsonElement only = Results(response).Single();
        JsonElement metadata = only.GetProperty("metadata");
        Assert.True(only.GetProperty("found").GetBoolean());
        Assert.Equal("Microsoft", metadata.GetProperty("shortName").GetString());
        Assert.Equal("Technology", metadata.GetProperty("sector").GetString());
        Assert.Equal(123.45m, metadata.GetProperty("currentPrice").GetDecimal());
        Assert.Equal("core holding", metadata.GetProperty("tag").GetString());
        Assert.Equal("my private note", metadata.GetProperty("notes").GetString());
        Assert.Equal("check earnings", metadata.GetProperty("reminder").GetString());
        Assert.Equal(100.5m, metadata.GetProperty("long").GetDecimal());
        Assert.Equal(130m, metadata.GetProperty("exitLong").GetDecimal());
        Assert.Equal(90m, metadata.GetProperty("stopLossLong").GetDecimal());
        Assert.False(metadata.TryGetProperty("trailingPE", out _)); // no stored value -> omitted
    }

    [Fact]
    public async Task GetTickerMetadata_DuplicatesAfterCanonicalisationAreReturnedOnceInOrder()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_ticker_metadata", """{"symbols":["7203.t","msft","7203-T"," MSFT "]}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { "7203-T", "MSFT" }, Results(response).Select(r => r.GetProperty("symbol").GetString()).ToArray());
        Assert.Equal(4, result.GetProperty("requestedCount").GetInt32());
        Assert.Equal(2, result.GetProperty("uniqueCount").GetInt32());
    }

    [Fact]
    public async Task GetTickerMetadata_ASymbolOutsideTheTickerListIsNotFoundWithoutFailingTheCall()
    {
        using var response = await CallAsync(await StartAsync(), "sa_get_ticker_metadata", """{"symbols":["msft","NOSUCH"]}""");

        JsonElement[] results = Results(response);
        Assert.False(McpTestSupport.IsError(response));
        Assert.False(results[1].GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, results[1].GetProperty("timeSeriesLastUpdated").ValueKind);
    }

    [Fact]
    public async Task GetTickerMetadata_RejectsBadInputAndReportsUnreadableFilesAsDataUnavailable()
    {
        var dispatcher = await StartAsync(("MaxSymbolsPerMetadataCall", "2"));
        File.WriteAllBytes(Path.Combine(_fixture.MetadataDirectory, "MSFT.meta.parquet"), new byte[] { 1, 2, 3, 4 });

        async Task<(string Code, string? Param)> Failure(string arguments)
        {
            using var response = await CallAsync(dispatcher, "sa_get_ticker_metadata", arguments);
            JsonElement error = McpTestSupport.Structured(response);
            return (error.GetProperty("code").GetString()!, error.TryGetProperty("param", out JsonElement p) ? p.GetString() : null);
        }

        Assert.Equal(("INVALID_ARGUMENT", "symbols"), await Failure("""{"symbols":[]}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbols"), await Failure("""{}"""));
        Assert.Equal(("LIMIT_EXCEEDED", "symbols"), await Failure("""{"symbols":["A","B","C"]}"""));
        Assert.Equal(("INVALID_ARGUMENT", "symbols[1]"), await Failure("""{"symbols":["MSFT","../x"]}"""));
        Assert.Equal(("DATA_UNAVAILABLE", "symbols"), await Failure("""{"symbols":["MSFT"]}"""));
    }

    // ---- shared guarantees ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ASessionOfEveryToolIncludingFailuresLeavesTheDataTreeUnchanged()
    {
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with { ShortName = "Microsoft" });
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        foreach ((string tool, string arguments) in new[]
        {
            ("sa_list_tickers", """{"includeMetadataSummary":true,"searchQuery":"micro"}"""),
            ("sa_list_tickers", """{"unknown":1}"""),
            ("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1"}"""),
            ("sa_get_candles", """{"symbol":"MSFT","timeFrame":"W1","count":2}"""),
            ("sa_get_candles", """{"symbol":"MSFT","timeFrame":"MN1","startDate":"2024-01-01"}"""),
            ("sa_get_candles", """{"symbol":"NOSUCH","timeFrame":"D1"}"""),
            ("sa_get_candles", """{"symbol":"../x","timeFrame":"D1"}"""),
            ("sa_get_ticker_metadata", """{"symbols":["MSFT","7203.T","NOSUCH"]}"""),
        })
        {
            (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!.Dispose();
        }

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task AnIncompleteDataFolderGivesEmptyResultsAndCreatesNothing()
    {
        Directory.Delete(_fixture.Settings.WeeklyDataPath!, recursive: true);
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        using var tickers = await CallAsync(dispatcher, "sa_list_tickers");
        using var candles = await CallAsync(dispatcher, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1"}""");

        Assert.Empty(Symbols(tickers));
        Assert.Equal("SYMBOL_NOT_FOUND", McpTestSupport.ErrorCodeOf(candles));
        Assert.Equal(before, _fixture.Snapshot());
        Assert.False(Directory.Exists(_fixture.Settings.WeeklyDataPath));
    }

    [Fact]
    public async Task EveryToolIsListedWithAnEnglishDescriptionAndAStrictSchema()
    {
        var dispatcher = await StartAsync();

        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement[] tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sa_calculate_indicators", "sa_calculate_reverse_watch", "sa_get_candles", "sa_get_capabilities", "sa_get_settings", "sa_get_ticker_metadata", "sa_list_templates", "sa_list_tickers", "sa_run_backtest", "sa_run_screener" }, tools.Select(t => t.GetProperty("name").GetString()).ToArray());
        Assert.All(tools, tool =>
        {
            Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
            Assert.All(tool.GetProperty("description").GetString()!, c => Assert.True(c < 128, "descriptions are English (ASCII)"));
        });
        JsonElement candlesSchema = tools.Single(t => t.GetProperty("name").GetString() == "sa_get_candles").GetProperty("inputSchema");
        Assert.Equal(new[] { "symbol", "timeFrame" }, candlesSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal(Enum.GetNames<TimeFrame>(), candlesSchema.GetProperty("properties").GetProperty("timeFrame").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    // ---- TickerMetadataJson ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryPublicPropertyOfTheMetadataRecordIsWrittenUnlessExplicitlyExcluded()
    {
        object boxed = TickerMetadata.Unknown;
        foreach (PropertyInfo property in typeof(TickerMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object sample = type == typeof(string) ? "s"
                : type == typeof(decimal) ? 1.5m
                : type == typeof(long) ? 7L
                : type == typeof(bool) ? true
                : type == typeof(DateTime) ? new DateTime(2024, 1, 2, 3, 4, 5)
                : throw new NotSupportedException($"Teach this test and TickerMetadataJson about {type.Name} ({property.Name}).");
            property.SetValue(boxed, sample);
        }

        string json = ToolJson.Build(writer => TickerMetadataJson.WriteAll(writer, (TickerMetadata)boxed));

        using var document = JsonDocument.Parse(json);
        string[] written = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        string[] expected = TickerMetadataJson.AllFields.Where(f => !TickerMetadataJson.ExcludedFields.Contains(f)).Select(TickerMetadataJson.JsonName).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), written.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(typeof(TickerMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length, TickerMetadataJson.AllFields.Count);
        Assert.Equal("2024-01-02T03:04:05", document.RootElement.GetProperty("metadataLastUpdated").GetString());
    }

    [Fact]
    public void TheExclusionListOnlyNamesRealProperties_AndTheOwnerChoseToHideNothing()
    {
        Assert.Empty(TickerMetadataJson.ExcludedFields.Except(TickerMetadataJson.AllFields));
        Assert.Empty(TickerMetadataJson.ExcludedFields);
        Assert.Contains(nameof(TickerMetadata.Notes), TickerMetadataJson.AllFields);
        Assert.Contains(nameof(TickerMetadata.Reminder), TickerMetadataJson.AllFields);
        Assert.Contains(nameof(TickerMetadata.Tag), TickerMetadataJson.AllFields);
        Assert.Contains(nameof(TickerMetadata.Long), TickerMetadataJson.AllFields);
    }
}
