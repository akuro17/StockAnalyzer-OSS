using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T4 (decision D-2f): the optional <c>compactReport</c> of the tools that write the backtest report through <see cref="BacktestReportJson"/>
/// (sa_run_backtest: the whole report; sa_run_portfolio_backtest: the five metrics of every symbol result). A Valid metric becomes its bare number, any other null plus an entry of
/// <c>unavailableMetrics</c>; <c>metricUnits</c> names the unit of each Valid one. Without the argument (or with false) the answer is byte for byte the one it always was.
/// </summary>
public sealed class BacktestCompactReportTests : IDisposable
{
    private const int Days = 400;
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";
    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string NeverTrades = """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1000000000}}""";
    private static readonly string Trading = $$"""{"entryLong":{{Entry}},"exitLong":{{Exit}}}""";
    private static readonly string[] PortfolioMetrics = { "totalReturn", "winRate", "profitFactor", "annualizedSharpe", "maxDrawdown" };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "AAPL", "EMPTY");
    private readonly List<McpServerRuntime> _runtimes = new();

    public BacktestCompactReportTests()
    {
        decimal[] msft = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", msft);
        decimal[] aapl = Enumerable.Range(0, Days).Select(i => Math.Round(150m + (decimal)(15 * Math.Cos(i / 8.0)), 2)).ToArray();
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "AAPL", aapl);
        string emptyParquet = Path.Combine(_fixture.DailyDirectory, "EMPTY.parquet");
        if (File.Exists(emptyParquet))
        {
            File.Delete(emptyParquet);
        }
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
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
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonElement> OkAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static Task<JsonElement> RunAsync(McpMessageDispatcher dispatcher, string conditions, string extra = "")
        => OkAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{conditions}}{{Period}}{{extra}}}""");

    private static Task<JsonElement> PortfolioAsync(McpMessageDispatcher dispatcher, string extra = "")
        => OkAsync(dispatcher, "sa_run_portfolio_backtest", $$"""{"symbols":["MSFT","AAPL","EMPTY"],"conditions":{{Trading}}{{Period}}{{extra}}}""");

    private static bool IsMetric(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out _) && value.TryGetProperty("unit", out _);

    /// <summary>
    /// The compact metrics must say exactly what the verbose ones say: a Valid one its value (and its unit in <paramref name="units"/>), any other null and an entry of <paramref name="unavailable"/>.
    /// Returns (valid count, non-valid count).
    /// </summary>
    private static (int Valid, int NonValid) AssertSameMetric(string name, JsonElement verbose, JsonElement compact, JsonElement units, IReadOnlyDictionary<string, JsonElement> unavailable)
    {
        string status = verbose.GetProperty("status").GetString()!;
        if (status == "Valid")
        {
            Assert.Equal(JsonValueKind.Number, compact.ValueKind);
            Assert.Equal(verbose.GetProperty("value").GetDecimal(), compact.GetDecimal());
            Assert.Equal(verbose.GetProperty("unit").GetString(), units.GetProperty(name).GetString());
            Assert.DoesNotContain(name, unavailable.Keys);
            return (1, 0);
        }

        Assert.Equal(JsonValueKind.Null, compact.ValueKind);
        Assert.False(units.TryGetProperty(name, out _), $"{name} is not Valid and must not have a unit entry");
        JsonElement entry = unavailable[name];
        Assert.Equal(status, entry.GetProperty("status").GetString());
        Assert.Equal(verbose.GetProperty("reason").GetString(), entry.GetProperty("reason").GetString());
        Assert.Equal(new[] { "name", "status", "reason" }, entry.EnumerateObject().Select(p => p.Name).ToArray());
        return (0, 1);
    }

    private static IReadOnlyDictionary<string, JsonElement> Unavailable(JsonElement owner)
        => owner.TryGetProperty("unavailableMetrics", out JsonElement list)
            ? list.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>();

    // ---- the writer, exact JSON -------------------------------------------------------------------------------------------------

    private static BacktestReport ReportWithEveryMetricUnavailableExcept(IReadOnlyDictionary<string, MetricValue> valid)
    {
        var report = new BacktestReport();
        foreach (PropertyInfo property in typeof(BacktestReport).GetProperties().Where(p => p.PropertyType == typeof(MetricValue) || p.PropertyType == typeof(MetricValue?)))
        {
            MetricValue value = valid.TryGetValue(property.Name, out MetricValue chosen)
                ? chosen
                : MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.EmptyInput);
            property.SetValue(report, value);
        }

        return report;
    }

    private static string WriteCompact(BacktestReport report, bool compact)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            BacktestReportJson.Write(writer, report, compact);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void AReportWithOneValidAndOtherMetricsNotValidIsWrittenAsNumberNullListAndUnits()
    {
        BacktestReport report = ReportWithEveryMetricUnavailableExcept(new Dictionary<string, MetricValue>
        {
            [nameof(BacktestReport.TotalReturn)] = MetricValue.Valid(0.5m, MetricUnit.ReturnRatio),
            [nameof(BacktestReport.AnnualizedSharpe)] = MetricValue.Valid(1.4286m, MetricUnit.Dimensionless),
            [nameof(BacktestReport.ProfitFactor)] = MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.NoLosingTrades),
        });

        using JsonDocument compact = JsonDocument.Parse(WriteCompact(report, compact: true));
        JsonElement root = compact.RootElement;

        Assert.Equal(0.5m, root.GetProperty("totalReturn").GetDecimal());
        Assert.Equal("1.4286", root.GetProperty("annualizedSharpe").GetRawText());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("profitFactor").ValueKind);
        Assert.Equal("""{"totalReturn":"ReturnRatio","annualizedSharpe":"Dimensionless"}""", root.GetProperty("metricUnits").GetRawText());

        JsonElement unavailable = root.GetProperty("unavailableMetrics");
        JsonElement profitFactor = unavailable.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "profitFactor");
        Assert.Equal("""{"name":"profitFactor","status":"PositiveInfinity","reason":"NoLosingTrades"}""", profitFactor.GetRawText());
        int metricCount = typeof(BacktestReport).GetProperties().Count(p => p.PropertyType == typeof(MetricValue) || p.PropertyType == typeof(MetricValue?));
        Assert.Equal(metricCount - 2, unavailable.GetArrayLength());
        Assert.Equal(new[] { "unavailableMetrics", "metricUnits" }, root.EnumerateObject().Select(p => p.Name).TakeLast(2).ToArray());
    }

    [Fact]
    public void AMetricThatIsLeftAtItsDefaultStateIsNeverListedAsValid()
    {
        // default(MetricValue) is Status Valid without a value, a state the constructor refuses: it is no figure, so it is null and listed as NotApplicable, never as Valid.
        var report = new BacktestReport();
        foreach (PropertyInfo property in typeof(BacktestReport).GetProperties().Where(p => p.PropertyType == typeof(MetricValue)))
        {
            property.SetValue(report, default(MetricValue));
        }

        using JsonDocument compact = JsonDocument.Parse(WriteCompact(report, compact: true));

        JsonElement unavailable = compact.RootElement.GetProperty("unavailableMetrics");
        Assert.True(unavailable.GetArrayLength() > 0);
        Assert.All(unavailable.EnumerateArray(), entry =>
        {
            Assert.Equal("NotApplicable", entry.GetProperty("status").GetString());
            Assert.Equal("None", entry.GetProperty("reason").GetString());
        });
        Assert.Equal("{}", compact.RootElement.GetProperty("metricUnits").GetRawText());
    }

    [Fact]
    public void WhenEveryMetricIsValidThereIsNoUnavailableListAndTheUnitsAreStillWritten()
    {
        var all = typeof(BacktestReport).GetProperties()
            .Where(p => p.PropertyType == typeof(MetricValue) || p.PropertyType == typeof(MetricValue?))
            .ToDictionary(p => p.Name, _ => MetricValue.Valid(2m, MetricUnit.Count));
        BacktestReport report = ReportWithEveryMetricUnavailableExcept(all);

        using JsonDocument compact = JsonDocument.Parse(WriteCompact(report, compact: true));

        Assert.False(compact.RootElement.TryGetProperty("unavailableMetrics", out _));
        Assert.Equal(all.Count, compact.RootElement.GetProperty("metricUnits").EnumerateObject().Count());
    }

    [Fact]
    public void TheDefaultWritesTheVerboseFormByteForByte_AndFalseIsTheDefault()
    {
        BacktestReport report = ReportWithEveryMetricUnavailableExcept(new Dictionary<string, MetricValue>
        {
            [nameof(BacktestReport.TotalReturn)] = MetricValue.Valid(0.5m, MetricUnit.ReturnRatio),
        });

        string implicitDefault;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                BacktestReportJson.Write(writer, report);
            }

            implicitDefault = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        Assert.Equal(implicitDefault, WriteCompact(report, compact: false));
        Assert.Contains("\"totalReturn\":{\"status\":\"Valid\",\"value\":0.5,\"unit\":\"ReturnRatio\",\"reason\":\"None\"}", implicitDefault, StringComparison.Ordinal);
        Assert.DoesNotContain("metricUnits", implicitDefault, StringComparison.Ordinal);
    }

    // ---- sa_run_backtest --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunBacktest_OmittedAndFalseAreByteIdentical_AndOnlyTheReportDiffersWhenTrue()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement omitted = await RunAsync(dispatcher, Trading);
        JsonElement off = await RunAsync(dispatcher, Trading, ",\"compactReport\":false");
        JsonElement on = await RunAsync(dispatcher, Trading, ",\"compactReport\":true");

        Assert.Equal(omitted.GetRawText(), off.GetRawText());
        Assert.NotEqual(omitted.GetProperty("report").GetRawText(), on.GetProperty("report").GetRawText());
        Assert.Equal(omitted.EnumerateObject().Select(p => p.Name).ToArray(), on.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (JsonProperty property in omitted.EnumerateObject().Where(p => p.Name != "report"))
        {
            Assert.Equal(property.Value.GetRawText(), on.GetProperty(property.Name).GetRawText());
        }
    }

    [Theory]
    [InlineData("trading")]
    [InlineData("never")]
    public async Task RunBacktest_EveryCompactMetricSaysWhatTheVerboseOneSays(string scenario)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string conditions = scenario == "trading" ? Trading : NeverTrades;

        JsonElement verbose = (await RunAsync(dispatcher, conditions)).GetProperty("report");
        JsonElement compact = (await RunAsync(dispatcher, conditions, ",\"compactReport\":true")).GetProperty("report");

        JsonElement units = compact.GetProperty("metricUnits");
        IReadOnlyDictionary<string, JsonElement> unavailable = Unavailable(compact);
        int valid = 0;
        int nonValid = 0;
        foreach (JsonProperty property in verbose.EnumerateObject())
        {
            if (IsMetric(property.Value))
            {
                (int v, int n) = AssertSameMetric(property.Name, property.Value, compact.GetProperty(property.Name), units, unavailable);
                valid += v;
                nonValid += n;
            }
            else
            {
                Assert.Equal(property.Value.GetRawText(), compact.GetProperty(property.Name).GetRawText());
            }
        }

        Assert.Equal(valid, units.EnumerateObject().Count());
        Assert.Equal(nonValid, unavailable.Count);
        Assert.True(scenario == "never" ? nonValid > 0 : valid > 0, "the scenario must exercise the kind of metric it is named for");
        Assert.Equal(verbose.EnumerateObject().Select(p => p.Name).Concat(nonValid > 0 ? new[] { "unavailableMetrics", "metricUnits" } : new[] { "metricUnits" }).ToArray(), compact.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task RunBacktest_ARunWithoutTradesHasMostMetricsNotValid_AndATradingRunHasValidOnes()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement never = (await RunAsync(dispatcher, NeverTrades, ",\"compactReport\":true")).GetProperty("report");
        JsonElement trading = (await RunAsync(dispatcher, Trading, ",\"compactReport\":true")).GetProperty("report");

        Assert.True(never.GetProperty("unavailableMetrics").GetArrayLength() > 0);
        Assert.True(trading.GetProperty("metricUnits").EnumerateObject().Any());
        Assert.True(trading.GetProperty("unavailableMetrics").GetArrayLength() + trading.GetProperty("metricUnits").EnumerateObject().Count() > 0);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public async Task RunBacktest_RefusesANonBooleanCompactReport(string value)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"MSFT","conditions":{{Trading}},"compactReport":{{value}}}"""))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("compactReport", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    // ---- sa_run_portfolio_backtest ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Portfolio_OmittedAndFalseAreByteIdentical_AndTheCompositeIsUnchangedWhenTrue()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement omitted = await PortfolioAsync(dispatcher);
        JsonElement off = await PortfolioAsync(dispatcher, ",\"compactReport\":false");
        JsonElement on = await PortfolioAsync(dispatcher, ",\"compactReport\":true");

        Assert.Equal(omitted.GetRawText(), off.GetRawText());
        Assert.NotEqual(omitted.GetProperty("symbolResults").GetRawText(), on.GetProperty("symbolResults").GetRawText());
        Assert.Equal(omitted.GetProperty("composite").GetRawText(), on.GetProperty("composite").GetRawText());
        Assert.Equal(omitted.GetProperty("notes").GetRawText(), on.GetProperty("notes").GetRawText());
    }

    [Fact]
    public async Task Portfolio_EverySymbolResultSaysInCompactFormWhatItSaysInTheVerboseForm()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement verbose = await PortfolioAsync(dispatcher);
        JsonElement compact = await PortfolioAsync(dispatcher, ",\"compactReport\":true");

        JsonElement[] verboseSleeves = verbose.GetProperty("symbolResults").EnumerateArray().ToArray();
        JsonElement[] compactSleeves = compact.GetProperty("symbolResults").EnumerateArray().ToArray();
        Assert.Equal(verboseSleeves.Length, compactSleeves.Length);
        Assert.Equal(3, compactSleeves.Length);
        for (int i = 0; i < verboseSleeves.Length; i++)
        {
            JsonElement before = verboseSleeves[i];
            JsonElement after = compactSleeves[i];
            string symbol = before.GetProperty("symbol").GetString()!;
            JsonElement units = after.GetProperty("metricUnits");
            IReadOnlyDictionary<string, JsonElement> unavailable = Unavailable(after);

            foreach (string metric in PortfolioMetrics)
            {
                JsonElement written = before.GetProperty(metric);
                // The verbose sleeve metric is {status, value, unit, reason} (a sleeve without a report is a NotApplicable one).
                AssertSameMetric(metric, written, after.GetProperty(metric), units, unavailable);
            }

            // Everything that is not one of the five metrics (and not the two new keys) is unchanged, in place.
            string[] verboseKeys = before.EnumerateObject().Select(p => p.Name).ToArray();
            string[] expectedKeys = verboseKeys.ToList().Concat(new[] { "metricUnits" }).ToArray();
            string[] actualKeys = after.EnumerateObject().Select(p => p.Name).Where(n => n != "unavailableMetrics").ToArray();
            Assert.Equal(expectedKeys.OrderBy(n => n, StringComparer.Ordinal), actualKeys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (string key in verboseKeys.Except(PortfolioMetrics))
            {
                Assert.Equal(before.GetProperty(key).GetRawText(), after.GetProperty(key).GetRawText());
            }

            Assert.True(symbol.Length > 0);
        }
    }

    [Fact]
    public async Task Portfolio_ASleeveWithoutDataListsAllFiveMetricsAsNotApplicableAndHasNoUnits()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement compact = await PortfolioAsync(dispatcher, ",\"compactReport\":true");

        JsonElement empty = compact.GetProperty("symbolResults").EnumerateArray().Single(s => s.GetProperty("symbol").GetString() == "EMPTY");
        Assert.Equal("{}", empty.GetProperty("metricUnits").GetRawText());
        JsonElement[] list = empty.GetProperty("unavailableMetrics").EnumerateArray().ToArray();
        Assert.Equal(PortfolioMetrics, list.Select(e => e.GetProperty("name").GetString()).ToArray());
        Assert.All(list, entry =>
        {
            Assert.Equal("NotApplicable", entry.GetProperty("status").GetString());
            Assert.Equal("None", entry.GetProperty("reason").GetString());
        });
        Assert.All(PortfolioMetrics, metric => Assert.Equal(JsonValueKind.Null, empty.GetProperty(metric).ValueKind));
    }

    [Fact]
    public async Task Portfolio_ASleeveThatRanHasTheNumbersAndUnitsOfItsStandaloneBacktest()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement compact = await PortfolioAsync(dispatcher, ",\"compactReport\":true");
        string sleeveCapital = compact.GetProperty("symbolResults").EnumerateArray().Single(s => s.GetProperty("symbol").GetString() == "MSFT").GetProperty("sleeveCapital").GetRawText();
        JsonElement standalone = (await RunAsync(dispatcher, Trading, ",\"account\":{\"initialCapital\":" + sleeveCapital + "}")).GetProperty("report");

        JsonElement msft = compact.GetProperty("symbolResults").EnumerateArray().Single(s => s.GetProperty("symbol").GetString() == "MSFT");
        Assert.Equal(standalone.GetProperty("totalReturn").GetProperty("value").GetDecimal(), msft.GetProperty("totalReturn").GetDecimal());
        Assert.Equal(standalone.GetProperty("totalReturn").GetProperty("unit").GetString(), msft.GetProperty("metricUnits").GetProperty("totalReturn").GetString());
    }

    // ---- schema and scope -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sa_run_backtest", true)]
    [InlineData("sa_run_portfolio_backtest", true)]
    [InlineData("sa_optimize_backtest", false)]
    [InlineData("sa_run_walk_forward_analysis", false)]
    public async Task OnlyTheToolsThatWriteTheReportThroughTheReportWriterOfferCompactReport(string tool, bool offered)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement entry = McpTestSupport.Result(list).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == tool);
        JsonElement properties = entry.GetProperty("inputSchema").GetProperty("properties");

        Assert.Equal(offered, properties.TryGetProperty("compactReport", out JsonElement property));
        if (offered)
        {
            Assert.Equal("boolean", property.GetProperty("type").GetString());
            Assert.Contains("compactReport", entry.GetProperty("description").GetString(), StringComparison.Ordinal);
            if (entry.GetProperty("inputSchema").TryGetProperty("required", out JsonElement required))
            {
                Assert.DoesNotContain(required.EnumerateArray().Select(r => r.GetString()), name => name == "compactReport");
            }
        }
    }
}
