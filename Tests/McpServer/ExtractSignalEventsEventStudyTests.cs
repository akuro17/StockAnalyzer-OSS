using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T5 (decision D-5): <c>includeEventStudy</c> of sa_extract_signal_events through the real composition. The figures are recomputed here from the forward returns
/// of the events themselves; the population is every event of the call, not only the returned ones.
/// </summary>
public sealed class ExtractSignalEventsEventStudyTests : IDisposable
{
    private const int Days = 300;
    private const string RisingBar = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Never = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":1000000000}""";
    private const string Always = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""";
    private const string Period = ",\"startDate\":\"2024-03-01\",\"endDate\":\"2024-09-30\"";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()), gatewayWrapper: gatewayWrapper);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> ExtractAsync(McpMessageDispatcher dispatcher, string condition, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_extract_signal_events", $$"""{"symbol":"MSFT","timeFrame":"D1","condition":{{condition}}{{extra}}}"""))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static decimal?[] Returns(JsonElement result, string basis, string horizon)
        => result.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty(basis).GetProperty(horizon))
            .Select(v => v.ValueKind == JsonValueKind.Null ? (decimal?)null : v.GetDecimal())
            .ToArray();

    private static decimal? Number(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    // ---- omitted = unchanged ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheArgumentTheAnswerHasNoEventStudy_AndFalseGivesTheSameBytes()
    {
        var dispatcher = await StartAsync();
        const string Extra = Period + ",\"forwardBars\":[1,5]";

        using var omitted = await ExtractAsync(dispatcher, RisingBar, Extra);
        using var explicitFalse = await ExtractAsync(dispatcher, RisingBar, Extra + ",\"includeEventStudy\":false");

        Assert.False(Result(omitted).TryGetProperty("eventStudy", out _));
        Assert.Equal(McpTestSupport.Structured(omitted).GetRawText(), McpTestSupport.Structured(explicitFalse).GetRawText());
    }

    [Fact]
    public async Task EveryOtherKeyIsUnchangedWhenTheStudyIsRequested()
    {
        var dispatcher = await StartAsync();
        const string Extra = Period + ",\"forwardBars\":[1,5]";

        using var plain = await ExtractAsync(dispatcher, RisingBar, Extra);
        using var withStudy = await ExtractAsync(dispatcher, RisingBar, Extra + ",\"includeEventStudy\":true");

        JsonElement before = Result(plain);
        JsonElement after = Result(withStudy);
        Assert.Equal(before.EnumerateObject().Select(p => p.Name), after.EnumerateObject().Select(p => p.Name).Where(n => n != "eventStudy"));
        foreach (JsonProperty property in before.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), after.GetProperty(property.Name).GetRawText());
        }
    }

    // ---- the figures ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFiguresEqualTheOnesRecomputedFromTheForwardReturnsOfTheEvents_ForBothBasesAndEveryHorizon()
    {
        var dispatcher = await StartAsync();
        using var response = await ExtractAsync(dispatcher, RisingBar, Period + ",\"forwardBars\":[1,5],\"includeEventStudy\":true");

        JsonElement result = Result(response);
        JsonElement study = result.GetProperty("eventStudy");
        Assert.Equal(result.GetProperty("eventCount").GetInt32(), study.GetProperty("eventCount").GetInt32());
        Assert.True(study.GetProperty("eventCount").GetInt32() >= 5);
        Assert.True(study.GetProperty("lookAheadDiagnostic").GetBoolean());
        Assert.Equal(EventStudySummary.OverlapNote, study.GetProperty("overlapNote").GetString());

        foreach ((string basis, string eventKey) in new[] { ("fromClose", "forwardReturns"), ("fromNextOpen", "forwardReturnsFromNextOpen") })
        {
            foreach (string horizon in new[] { "1", "5" })
            {
                decimal[] values = Returns(result, eventKey, horizon).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                JsonElement stats = study.GetProperty(basis).GetProperty(horizon);
                int eventCount = study.GetProperty("eventCount").GetInt32();

                Assert.True(values.Length > 0);
                Assert.Equal(values.Length, stats.GetProperty("count").GetInt32());
                Assert.Equal(eventCount - values.Length, stats.GetProperty("missingCount").GetInt32());
                decimal mean = values.Sum() / values.Length;
                Assert.Equal(mean, Number(stats.GetProperty("mean")));
                Assert.Equal(values.Min(), Number(stats.GetProperty("min")));
                Assert.Equal(values.Max(), Number(stats.GetProperty("max")));
                Assert.Equal((decimal)values.Count(v => v > 0m) / values.Length, Number(stats.GetProperty("positiveRatio")));
                decimal[] sorted = values.OrderBy(v => v).ToArray();
                decimal median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2m;
                Assert.Equal(median, Number(stats.GetProperty("median")));
                double variance = values.Sum(v => (double)((v - mean) * (v - mean))) / values.Length;
                Assert.InRange(Math.Abs((double)Number(stats.GetProperty("stdDev"))! - Math.Sqrt(variance)), 0d, 1e-9);
            }
        }

        // The window ends at endDate and no bar after it is read, so the longer horizon is missing for at least as many events as the shorter one.
        Assert.True(study.GetProperty("fromClose").GetProperty("5").GetProperty("missingCount").GetInt32() >= study.GetProperty("fromClose").GetProperty("1").GetProperty("missingCount").GetInt32());
    }

    [Fact]
    public async Task TheStatisticsCoverEveryEvent_AlsoThoseMaxEventsAndNewestFirstCutOff()
    {
        var dispatcher = await StartAsync();
        const string Extra = Period + ",\"forwardBars\":[1,5],\"includeEventStudy\":true";

        using var all = await ExtractAsync(dispatcher, RisingBar, Extra);
        using var cut = await ExtractAsync(dispatcher, RisingBar, Extra + ",\"maxEvents\":2,\"order\":\"NewestFirst\"");

        JsonElement full = Result(all);
        JsonElement truncated = Result(cut);
        Assert.True(full.GetProperty("eventCount").GetInt32() > 2);
        Assert.True(truncated.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, truncated.GetProperty("returnedEventCount").GetInt32());
        Assert.Equal(full.GetProperty("eventStudy").GetRawText(), truncated.GetProperty("eventStudy").GetRawText());
        Assert.Equal(full.GetProperty("eventCount").GetInt32(), truncated.GetProperty("eventStudy").GetProperty("eventCount").GetInt32());
    }

    [Fact]
    public async Task NoEvents_GiveEventCountZeroAndNullFigures()
    {
        var dispatcher = await StartAsync();

        using var response = await ExtractAsync(dispatcher, Never, Period + ",\"forwardBars\":[1,5],\"includeEventStudy\":true");

        JsonElement study = Result(response).GetProperty("eventStudy");
        Assert.Equal(0, study.GetProperty("eventCount").GetInt32());
        foreach (string basis in new[] { "fromClose", "fromNextOpen" })
        {
            foreach (string horizon in new[] { "1", "5" })
            {
                JsonElement stats = study.GetProperty(basis).GetProperty(horizon);
                Assert.Equal(0, stats.GetProperty("count").GetInt32());
                Assert.Equal(0, stats.GetProperty("missingCount").GetInt32());
                Assert.All(new[] { "mean", "median", "stdDev", "min", "max", "positiveRatio" }, key => Assert.Equal(JsonValueKind.Null, stats.GetProperty(key).ValueKind));
            }
        }
    }

    [Fact]
    public async Task AHorizonLongerThanTheEvaluatedWindow_IsAllMissing()
    {
        var dispatcher = await StartAsync();
        const string ShortWindow = ",\"startDate\":\"2024-09-15\",\"endDate\":\"2024-09-30\"";

        using var response = await ExtractAsync(dispatcher, Always, ShortWindow + ",\"forwardBars\":[60],\"includeEventStudy\":true");

        JsonElement study = Result(response).GetProperty("eventStudy");
        int eventCount = study.GetProperty("eventCount").GetInt32();
        Assert.True(eventCount > 0);
        JsonElement stats = study.GetProperty("fromClose").GetProperty("60");
        Assert.Equal(0, stats.GetProperty("count").GetInt32());
        Assert.Equal(eventCount, stats.GetProperty("missingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("mean").ValueKind);
    }

    [Fact]
    public async Task TheEventModeDecidesThePopulation()
    {
        var dispatcher = await StartAsync();
        const string Extra = ",\"forwardBars\":[1],\"includeEventStudy\":true";

        using var every = await ExtractAsync(dispatcher, Always, Extra + ",\"eventMode\":\"EveryTrueBar\"");
        using var firstOfRun = await ExtractAsync(dispatcher, Always, Extra + ",\"eventMode\":\"FirstTrueBarOfRun\"");

        JsonElement everyResult = Result(every);
        JsonElement firstResult = Result(firstOfRun);
        Assert.Equal(everyResult.GetProperty("eventCount").GetInt32(), everyResult.GetProperty("eventStudy").GetProperty("eventCount").GetInt32());
        Assert.Equal(firstResult.GetProperty("eventCount").GetInt32(), firstResult.GetProperty("eventStudy").GetProperty("eventCount").GetInt32());
        Assert.True(everyResult.GetProperty("eventCount").GetInt32() > 1);
        Assert.Equal(1, firstResult.GetProperty("eventCount").GetInt32()); // one unbroken run: only its first bar
    }

    // ---- refusals and schema ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(",\"forwardBars\":[]")]
    public async Task TheStudyWithoutHorizonsIsRefusedWithTheFixedMessage_BeforeAnyBarIsLoaded(string forward)
    {
        CalculateIndicatorsBatchToolTests.CountingGateway? counter = null;
        var dispatcher = await StartAsync(inner =>
        {
            (IReadOnlyStockAnalyzerGateway gateway, counter) = CalculateIndicatorsBatchToolTests.CountingGateway.Create(inner);
            return gateway;
        });

        using var response = await ExtractAsync(dispatcher, RisingBar, Period + forward + ",\"includeEventStudy\":true");

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("includeEventStudy", error.GetProperty("param").GetString());
        Assert.Equal("'includeEventStudy' requires 'forwardBars' to be specified.", error.GetProperty("message").GetString());
        Assert.NotNull(counter);
        Assert.Empty(counter!.BarReads);
    }

    [Fact]
    public async Task TheSchemaAdvertisesTheBoolean_AndTheDescriptionExplainsTheFigures()
    {
        var dispatcher = await StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_extract_signal_events");
        JsonElement property = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("includeEventStudy");

        Assert.Equal("boolean", property.GetProperty("type").GetString());
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("stockanalyzer://docs/tools/sa_extract_signal_events", description, StringComparison.Ordinal);
        string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_extract_signal_events.md"));
        Assert.Contains("positiveRatio", doc);
        Assert.Contains("not a significance test", doc);
    }
}
