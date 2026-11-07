using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_indicators with <c>completeBarsOnly</c> (W1 and MN1). Not given, the answer is the one that existed before the argument (golden: <see cref="PointInTimeIndicatorsTests"/>) and no
/// partial-bar key is written. Given (true or false), referenceDate, lastBarPeriodEnd, lastBarPartial and droppedPartialBar follow droppedLeadingBar; true leaves a running last period out by
/// loading up to the day before its start, which is exactly an explicit endDate. The series holds one bar per calendar day from 2024-01-01 to Monday 2025-02-03, so the last week
/// (starting 2025-02-03) and the last month (starting 2025-02-01) are running.
/// </summary>
public sealed class CalculateIndicatorsCompleteBarsTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Primary = "MSFT";
    private const string Comparison = "AAPL";
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private static readonly string[] PartialKeys = { "completeBarsOnly", "referenceDate", "lastBarPeriodEnd", "lastBarPartial", "droppedPartialBar" };

    private readonly World _truth = new(ZigZag(StoredDays), null);

    public void Dispose() => _truth.Dispose();

    /// <summary>The closes <c>ParquetFixtureBuilder.SeedDailyOhlcv</c> writes (an alternating up and down sequence).</summary>
    private static List<decimal> ZigZag(int days)
    {
        var closes = new List<decimal>(days);
        decimal close = 100m;
        for (int i = 0; i < days; i++)
        {
            close += i % 2 == 0 ? 1.0m : -0.5m;
            closes.Add(close);
        }

        return closes;
    }

    private static string Arguments(string frame, string? completeBarsOnly, string? endDate, string indicators = """[{"id":"s","type":"SMA","parameters":{"Period":5}}]""")
        => "{" + $"\"symbol\":\"{Primary}\",\"timeFrame\":\"{frame}\",\"indicators\":{indicators},\"outputTailCount\":3"
            + (completeBarsOnly is null ? string.Empty : $",\"completeBarsOnly\":{completeBarsOnly}")
            + (endDate is null ? string.Empty : $",\"endDate\":\"{endDate}\"") + "}";

    // ---- not given: nothing changes -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("W1")]
    [InlineData("MN1")]
    public async Task WithoutTheArgumentNoPartialBarKeyIsWritten(string frame)
    {
        string answer = await _truth.CallAsync(Arguments(frame, null, null));

        foreach (string key in PartialKeys)
        {
            Assert.DoesNotContain($"\"{key}\"", answer, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task ADailyCallIgnoresTheArgumentAndTheAnswerIsUnchanged(string value)
    {
        string without = await _truth.CallAsync(Arguments("D1", null, null));
        string with = await _truth.CallAsync(Arguments("D1", value, null));

        Assert.Equal(without, with);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task ADailyCallWithAnIndicatorOnWeeklyBarsStillWritesNoKeyAndIsUnchanged(string value)
    {
        // The argument concerns the bars of the call's own W1 or MN1 frame only: an indicator with its own coarser time frame does not make a D1 call report anything.
        const string indicators = """[{"id":"s","type":"SMA","parameters":{"Period":5}},{"id":"w","type":"SMA","parameters":{"Period":3},"timeFrame":"W1"},{"id":"m","type":"SMA","parameters":{"Period":3},"timeFrame":"MN1"}]""";

        string without = await _truth.CallAsync(Arguments("D1", null, null, indicators));
        string with = await _truth.CallAsync(Arguments("D1", value, null, indicators));

        Assert.Equal(without, with);
        foreach (string key in PartialKeys)
        {
            Assert.DoesNotContain($"\"{key}\"", with, StringComparison.Ordinal);
        }
    }

    // ---- the meaning of the keys --------------------------------------------------------------------------------------------------------

    [Theory]
    // frame, completeBarsOnly, endDate, referenceDate, latestTimestamp, lastBarPeriodEnd, lastBarPartial, droppedPartialBar
    [InlineData("W1", "false", null, "2025-02-03", "2025-02-03", "2025-02-09", true, false)]
    [InlineData("W1", "true", null, "2025-02-03", "2025-01-27", "2025-02-02", false, true)]
    [InlineData("MN1", "false", null, "2025-02-03", "2025-02-01", "2025-02-28", true, false)]
    [InlineData("MN1", "true", null, "2025-02-03", "2025-01-01", "2025-01-31", false, true)]
    [InlineData("W1", "false", "2030-01-01", "2025-02-03", "2025-02-03", "2025-02-09", true, false)]
    [InlineData("W1", "true", "2030-01-01", "2025-02-03", "2025-01-27", "2025-02-02", false, true)]
    [InlineData("MN1", "true", "2030-01-01", "2025-02-03", "2025-01-01", "2025-01-31", false, true)]
    [InlineData("W1", "false", "2024-12-18", "2024-12-18", "2024-12-16", "2024-12-22", true, false)]
    [InlineData("W1", "true", "2024-12-18", "2024-12-18", "2024-12-09", "2024-12-15", false, true)]
    [InlineData("MN1", "false", "2024-12-18", "2024-12-18", "2024-12-01", "2024-12-31", true, false)]
    [InlineData("MN1", "true", "2024-12-18", "2024-12-18", "2024-11-01", "2024-11-30", false, true)]
    public async Task TheKeysReportTheReferenceDateAndTheLastBarAndTrueLeavesARunningPeriodOut(
        string frame, string value, string? endDate, string reference, string latest, string periodEnd, bool partial, bool dropped)
    {
        string answer = await _truth.CallAsync(Arguments(frame, value, endDate));

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        Assert.Equal(value == "true", root.GetProperty("completeBarsOnly").GetBoolean());
        Assert.Equal(reference, root.GetProperty("referenceDate").GetString());
        Assert.Equal(latest, root.GetProperty("latestTimestamp").GetString());
        Assert.Equal(periodEnd, root.GetProperty("lastBarPeriodEnd").GetString());
        Assert.Equal(partial, root.GetProperty("lastBarPartial").GetBoolean());
        Assert.Equal(dropped, root.GetProperty("droppedPartialBar").GetBoolean());
        Assert.False(root.GetProperty("droppedLeadingBar").GetBoolean());
        // endDate is echoed only when the caller gave one, and as given (never the date used to leave the running period out)
        Assert.Equal(endDate, root.TryGetProperty("endDate", out JsonElement echoed) ? echoed.GetString() : null);
    }

    [Fact]
    public async Task TheKeysFollowDroppedLeadingBarInTheDocumentedOrder()
    {
        string answer = await _truth.CallAsync(Arguments("W1", "true", null));

        string[] names = JsonNode.Parse(answer)!.AsObject().Select(property => property.Key).ToArray();
        int dropped = Array.IndexOf(names, "droppedLeadingBar");
        Assert.Equal(PartialKeys, names.Skip(dropped + 1).Take(PartialKeys.Length).ToArray());
    }

    // ---- acceptance: true equals an explicit endDate ------------------------------------------------------------------------------------

    private const string WeeklyIndicators =
        """[{"id":"s","type":"SMA","parameters":{"Period":5}},{"id":"p","type":"PairRatio","parameters":{"ComparisonSymbol":"AAPL"}},{"id":"m","type":"SMA","parameters":{"Period":3},"timeFrame":"MN1"}]""";

    private const string MonthlyIndicators =
        """[{"id":"s","type":"SMA","parameters":{"Period":5}},{"id":"p","type":"PairRatio","parameters":{"ComparisonSymbol":"AAPL"}}]""";

    [Theory]
    // frame, endDate given by the caller, the day before the start of the running period
    [InlineData("W1", null, "2025-02-02", WeeklyIndicators)]
    [InlineData("W1", "2024-12-18", "2024-12-15", WeeklyIndicators)]
    [InlineData("MN1", null, "2025-01-31", MonthlyIndicators)]
    [InlineData("MN1", "2024-12-18", "2024-11-30", MonthlyIndicators)]
    public async Task TrueGivesTheAnswerOfAnExplicitEndDateOnTheDayBeforeTheRunningPeriodApartFromTheKeys(string frame, string? endDate, string effectiveEnd, string indicators)
    {
        JsonObject withFlag = JsonNode.Parse(await _truth.CallAsync(Arguments(frame, "true", endDate, indicators)))!.AsObject();
        JsonObject explicitEnd = JsonNode.Parse(await _truth.CallAsync(Arguments(frame, null, effectiveEnd, indicators)))!.AsObject();

        foreach (string key in PartialKeys)
        {
            Assert.True(withFlag.Remove(key), key);
        }

        withFlag.Remove("endDate");
        explicitEnd.Remove("endDate");
        Assert.Equal(explicitEnd.ToJsonString(), withFlag.ToJsonString());
        Assert.True(explicitEnd["results"]!["p"]!["isSuccessful"]!.GetValue<bool>(), explicitEnd["results"]!["p"]!.ToJsonString());
    }

    [Fact]
    public async Task FalseGivesTheAnswerWithoutTheArgumentApartFromTheKeys()
    {
        JsonObject withFlag = JsonNode.Parse(await _truth.CallAsync(Arguments("W1", "false", null, WeeklyIndicators)))!.AsObject();
        JsonObject without = JsonNode.Parse(await _truth.CallAsync(Arguments("W1", null, null, WeeklyIndicators)))!.AsObject();

        foreach (string key in PartialKeys)
        {
            Assert.True(withFlag.Remove(key), key);
        }

        Assert.Equal(without.ToJsonString(), withFlag.ToJsonString());
    }

    // ---- nothing after the cut reaches the answer (comparison symbol and own-timeFrame bars) -------------------------------------------

    [Fact]
    public async Task TheComparisonSymbolAndTheOwnTimeFrameBarsAreCutWhereTheRunningPeriodBegins()
    {
        // Every close after Sunday 2025-02-02 (the running week) is ten times larger in the second tree; with true the answer must not notice.
        using var altered = new World(ZigZag(StoredDays), new DateOnly(2025, 2, 2));
        string arguments = Arguments("W1", "true", null, WeeklyIndicators);

        string truth = await _truth.CallAsync(arguments);
        string other = await altered.CallAsync(arguments);

        Assert.Equal(truth, other);
        // the check can fail: with false the running week is read, so the trees differ
        string truthRunning = await _truth.CallAsync(Arguments("W1", "false", null, WeeklyIndicators));
        string otherRunning = await altered.CallAsync(Arguments("W1", "false", null, WeeklyIndicators));
        Assert.NotEqual(truthRunning, otherRunning);
    }

    [Fact]
    public async Task ASaturdayEndDateOverAFridayLastStoredDayLeavesTheRunningMonthAndWeekPartial()
    {
        // The documented calendar rule (SA_AI_INTEGRATION partial_period): the reference date is never later than the last stored day, so a Saturday end date (2026-10-31) completes neither
        // October 2026 (which ends that Saturday) nor the week that ends Sunday 2026-11-01. The series runs one bar per calendar day up to Friday 2026-10-30.
        using var friday = new World(ZigZag(1034), null);

        foreach ((string frame, string latestComplete, string runningStart) in new[] { ("MN1", "2026-09-01", "2026-10-01"), ("W1", "2026-10-19", "2026-10-26") })
        {
            string kept = await friday.CallAsync(Arguments(frame, "false", "2026-10-31"));
            string dropped = await friday.CallAsync(Arguments(frame, "true", "2026-10-31"));

            using JsonDocument keptDocument = JsonDocument.Parse(kept);
            using JsonDocument droppedDocument = JsonDocument.Parse(dropped);
            Assert.Equal("2026-10-30", keptDocument.RootElement.GetProperty("referenceDate").GetString());
            Assert.True(keptDocument.RootElement.GetProperty("lastBarPartial").GetBoolean(), frame);
            Assert.Equal(runningStart, keptDocument.RootElement.GetProperty("latestTimestamp").GetString());
            Assert.True(droppedDocument.RootElement.GetProperty("droppedPartialBar").GetBoolean(), frame);
            Assert.False(droppedDocument.RootElement.GetProperty("lastBarPartial").GetBoolean(), frame);
            Assert.Equal(latestComplete, droppedDocument.RootElement.GetProperty("latestTimestamp").GetString());
        }
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("W1")]
    [InlineData("MN1")]
    public async Task AHistoryHoldingOnlyARunningPeriodHasNoCompletePeriodAndIsInsufficientMarketData(string frame)
    {
        // three days, Monday 2024-01-01 .. Wednesday 2024-01-03: the first week and the first month are both running
        using var shortHistory = new World(ZigZag(3), null);

        using JsonDocument response = await shortHistory.CallRawAsync(Arguments(frame, "true", null));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InsufficientMarketData, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("completeBarsOnly", McpTestSupport.Structured(response).GetProperty("param").GetString());
        // with false the running period is calculated (and reported) as before
        string kept = await shortHistory.CallAsync(Arguments(frame, "false", null));
        using JsonDocument document = JsonDocument.Parse(kept);
        Assert.True(document.RootElement.GetProperty("lastBarPartial").GetBoolean());
    }

    [Fact]
    public async Task ANonBooleanValueIsInvalidArgumentNamingTheArgument()
    {
        using JsonDocument response = await _truth.CallRawAsync(Arguments("W1", "\"yes\"", null));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("completeBarsOnly", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task TheDescriptionAndTheSchemaNameTheArgument()
    {
        using JsonDocument list = JsonDocument.Parse((await _truth.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "sa_calculate_indicators");
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("completeBarsOnly (W1 and MN1 only, ignored for D1; not given = the running last period is calculated as before and nothing about it is reported)", description, StringComparison.Ordinal);
        Assert.Contains("the alignment only steps back one closed period", description, StringComparison.Ordinal);
        JsonElement schema = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("completeBarsOnly");
        Assert.Equal("boolean", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("default", out _), "absent must not be the same as false");
    }

    // ---- hosting ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>A throw-away data tree with the primary and the comparison symbol (the same closes), optionally with every close after a date multiplied by ten.</summary>
    private sealed class World : IDisposable
    {
        private const decimal Wild = 10m;

        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;

        public World(IReadOnlyList<decimal> closes, DateOnly? alterAfter)
        {
            _fixture = ReadOnlyDataTreeFixture.Create(closes.Count, Primary, Comparison);
            if (alterAfter is { } lastKept)
            {
                int firstChanged = lastKept.DayNumber - DateOnly.FromDateTime(FirstDay).DayNumber + 1;
                List<decimal> altered = closes.Select((close, index) => index >= firstChanged ? close * Wild : close).ToList();
                foreach (string symbol in new[] { Primary, Comparison })
                {
                    string file = Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet");
                    File.Delete(file);
                    ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, altered);
                }
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
            _runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
            _runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
        }

        public McpMessageDispatcher Dispatcher => _runtime.Dispatcher;

        /// <summary>The structured answer of sa_calculate_indicators as text; the call must succeed.</summary>
        public async Task<string> CallAsync(string argumentsJson)
        {
            using JsonDocument response = await CallRawAsync(argumentsJson);
            Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
            return McpTestSupport.Structured(response).GetRawText();
        }

        public async Task<JsonDocument> CallRawAsync(string argumentsJson)
            => (await McpTestSupport.CallAsync(_runtime.Dispatcher, "sa_calculate_indicators", argumentsJson))!;

        public void Dispose()
        {
            _runtime.Dispose();
            _fixture.Dispose();
        }
    }
}
