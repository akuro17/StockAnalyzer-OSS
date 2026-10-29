using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_indicators with an <c>endDate</c> (plan 02, T1). Without the argument the answer is byte-identical to the answer captured before the argument existed
/// (golden). With it the latest bar is the last bar dated on or before the date, the values equal a direct pipeline call over the bars up to that date, and nothing
/// stored after the date (in the symbol or in a comparison symbol) can change the answer: two data trees hold the same series, one has every close after the date
/// multiplied by ten.
/// </summary>
public sealed class PointInTimeIndicatorsTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Primary = "MSFT";
    private const string Comparison = "AAPL";

    /// <summary>A Wednesday in the middle of a week and of a month; the series starts 2024-01-01 and has one bar per calendar day.</summary>
    private const string MidPeriodDate = "2024-12-18";

    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly IndicatorFactory Factory = new();
    private static readonly ScreenerCatalogProvider CatalogProvider = new();

    private const string ApostropheEscape = "\\u0027";

    private const string GoldenArguments = """{"symbol":"MSFT","timeFrame":"{FRAME}","indicators":[{"id":"s","type":"SMA","parameters":{"Period":5}},{"id":"r","type":"RSI"}],"outputTailCount":3}""";

    /// <summary>The structured answers of the call above on this data tree, captured from the build before <c>endDate</c> existed.</summary>
    private static readonly Dictionary<string, string> Golden = new()
    {
        // The JSON writer escapes the apostrophe of the MN1 RSI message as backslash-u0027; the captured text below holds a plain apostrophe in its place (see ApostropheEscape).
        ["D1"] = """{"symbol":"MSFT","timeFrame":"D1","periodUnit":"bars","timestampFormat":"yyyy-MM-dd","lookbackBars":10000,"calculatedBarCount":400,"returnedBarCount":3,"earliestCalculatedTimestamp":"2024-01-01","latestTimestamp":"2025-02-03","droppedLeadingBar":false,"timestamps":["2025-02-01","2025-02-02","2025-02-03"],"warnings":[],"results":{"s":{"type":"SMA","displayName":"Simple Moving Average (5)","isSuccessful":true,"warnings":[],"priceSource":"Close","nonCausalSeries":[],"series":{"SMA":[199.3,199.7,199.8]},"latestValues":{"SMA":199.8},"leadingNullCount":{"SMA":4}},"r":{"type":"RSI","displayName":"Relative Strength Index (14)","isSuccessful":true,"warnings":[],"priceSource":"Close","nonCausalSeries":[],"series":{"RSI":[64.99999999999920473833107554,68.2926829268285654184280773,64.999999999999314289683427373]},"latestValues":{"RSI":64.999999999999314289683427373},"leadingNullCount":{"RSI":14}}}}""",
        ["W1"] = """{"symbol":"MSFT","timeFrame":"W1","periodUnit":"bars","timestampFormat":"yyyy-MM-dd","lookbackBars":10000,"calculatedBarCount":58,"returnedBarCount":3,"earliestCalculatedTimestamp":"2024-01-01","latestTimestamp":"2025-02-03","droppedLeadingBar":false,"timestamps":["2025-01-20","2025-01-27","2025-02-03"],"warnings":[],"results":{"s":{"type":"SMA","displayName":"Simple Moving Average (5)","isSuccessful":true,"warnings":[],"priceSource":"Close","nonCausalSeries":[],"series":{"SMA":[194.8,196.7,198.0]},"latestValues":{"SMA":198.0},"leadingNullCount":{"SMA":4}},"r":{"type":"RSI","displayName":"Relative Strength Index (14)","isSuccessful":true,"warnings":[],"priceSource":"Close","nonCausalSeries":[],"series":{"RSI":[100.0,100.0,97.88091110173570341055318354]},"latestValues":{"RSI":97.88091110173570341055318354},"leadingNullCount":{"RSI":14}}}}""",
        ["MN1"] = """{"symbol":"MSFT","timeFrame":"MN1","periodUnit":"bars","timestampFormat":"yyyy-MM-dd","lookbackBars":10000,"calculatedBarCount":14,"returnedBarCount":3,"earliestCalculatedTimestamp":"2024-01-01","latestTimestamp":"2025-02-01","droppedLeadingBar":false,"timestamps":["2024-12-01","2025-01-01","2025-02-01"],"warnings":[],"results":{"s":{"type":"SMA","displayName":"Simple Moving Average (5)","isSuccessful":true,"warnings":[],"priceSource":"Close","nonCausalSeries":[],"series":{"SMA":[176.5,184.3,190.6]},"latestValues":{"SMA":190.6},"leadingNullCount":{"SMA":4}},"r":{"type":"RSI","displayName":"Relative Strength Index (14)","isSuccessful":false,"errorMessage":"No value could be calculated: every value is null (the stored history may be shorter than the indicator's warm-up, or the indicator found nothing to report).","warnings":[],"priceSource":"Close","nonCausalSeries":[]}}}""",
    };

    private readonly World _truth = new(ZigZag(StoredDays), null);
    private readonly World _alteredAfterMid = new(ZigZag(StoredDays), DateOnly.Parse(MidPeriodDate));

    public void Dispose()
    {
        _truth.Dispose();
        _alteredAfterMid.Dispose();
    }

    /// <summary>The closes <c>ParquetFixtureBuilder.SeedDailyOhlcv</c> writes (an alternating up and down sequence), so the data tree is the one the golden was captured on.</summary>
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

    /// <summary>The captured text as the writer produces it: the apostrophe is escaped as a JSON unicode escape.</summary>
    private static string GoldenText(string frame) => Golden[frame].Replace("'", ApostropheEscape, StringComparison.Ordinal);

    private static string GoldenCall(string frame) => GoldenArguments.Replace("{FRAME}", frame, StringComparison.Ordinal);

    public static TheoryData<string> Frames => new() { "D1", "W1", "MN1" };

    // ---- nothing changes without the argument ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task WithoutAnEndDateTheAnswerIsByteIdenticalToTheOneCapturedBeforeTheArgumentExisted(string frame)
    {
        string answer = await _truth.CallAsync(GoldenCall(frame));

        Assert.Equal(GoldenText(frame), answer);
        Assert.DoesNotContain("endDate", answer, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task AnEndDateAfterTheLastStoredBarGivesTheAnswerWithoutOneApartFromTheEchoedDate(string frame)
    {
        string withDate = await _truth.CallAsync(GoldenCall(frame).Replace("\"outputTailCount\":3", "\"outputTailCount\":3,\"endDate\":\"2030-01-01\"", StringComparison.Ordinal));

        Assert.Contains("\"timeFrame\":\"" + frame + "\",\"endDate\":\"2030-01-01\",\"periodUnit\"", withDate, StringComparison.Ordinal);
        Assert.Equal(GoldenText(frame), withDate.Replace("\"endDate\":\"2030-01-01\",", string.Empty, StringComparison.Ordinal));
    }

    // ---- the meaning of the date -------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task TheLatestBarIsTheLastOneOnOrBeforeTheDateAndTheValuesEqualADirectPipelineCallOverTheBarsUpToIt(string frame)
    {
        TimeFrame timeFrame = Enum.Parse<TimeFrame>(frame);
        DateOnly endDate = DateOnly.Parse(MidPeriodDate);
        IReadOnlyList<CandleData> upTo = await _truth.Gateway.GetCandlesUpToAsync(Primary, timeFrame, endDate, 10000);
        CoreIndicatorSettings rsi = CatalogProvider.GetDefaultSettings(IndicatorType.RSI, Factory)!;
        rsi.IsEnabled = true;
        ((CoreRsiParameter)rsi.ParameterObject!).Period = 5; // short enough for the twelve monthly bars up to the date
        Dictionary<string, IIndicatorResult> direct = await _truth.Gateway.CalculateIndicatorsAsync(upTo.Select(ToCore).ToList(), new[] { rsi });

        string answer = await _truth.CallAsync($$$"""{"symbol":"MSFT","timeFrame":"{{{frame}}}","indicators":[{"id":"s","type":"RSI","parameters":{"Period":5}}],"outputTailCount":5,"endDate":"{{{MidPeriodDate}}}"}""");

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        Assert.Equal(MidPeriodDate, root.GetProperty("endDate").GetString());
        Assert.Equal(upTo.Count, root.GetProperty("calculatedBarCount").GetInt32());
        Assert.Equal(upTo.TakeLast(5).Select(bar => bar.Timestamp.ToString("yyyy-MM-dd")).ToArray(), root.GetProperty("timestamps").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.True(DateOnly.Parse(root.GetProperty("latestTimestamp").GetString()!) <= endDate);
        decimal?[] expectedTail = direct[rsi.Id].GetSeries("Main").TakeLast(5).ToArray();
        decimal?[] actualTail = root.GetProperty("results").GetProperty("s").GetProperty("series").GetProperty("RSI").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();
        Assert.Equal(expectedTail, actualTail);
        Assert.NotNull(expectedTail[^1]);
        Assert.Equal(expectedTail[^1], root.GetProperty("results").GetProperty("s").GetProperty("latestValues").GetProperty("RSI").GetDecimal());
    }

    [Fact]
    public async Task AWeeklyBarThatStraddlesTheDateHoldsOnlyTheDaysUpToIt()
    {
        // 2024-12-18 is a Wednesday: the week of Monday 2024-12-16 has three days up to it (16, 17, 18) and the answer must end with that partial week.
        string answer = await _truth.CallAsync($$$"""{"symbol":"MSFT","timeFrame":"W1","indicators":[{"id":"s","type":"SMA"}],"outputTailCount":2,"endDate":"{{{MidPeriodDate}}}"}""");

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("2024-12-16", document.RootElement.GetProperty("latestTimestamp").GetString());
        Assert.False(document.RootElement.GetProperty("droppedLeadingBar").GetBoolean());
    }

    // ---- nothing after the date can change the answer ----------------------------------------------------------------------------------

    private const string MutationIndicators =
        """[{"id":"s","type":"SMA"},{"id":"r","type":"RSI"},{"id":"m","type":"MACD"},{"id":"p","type":"PairRatio","parameters":{"ComparisonSymbol":"AAPL"}}]""";

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task NothingStoredAfterTheDateInTheSymbolOrInAComparisonSymbolChangesTheAnswer(string frame)
    {
        string arguments = $$$"""{"symbol":"MSFT","timeFrame":"{{{frame}}}","indicators":{{{MutationIndicators}}},"outputTailCount":30,"endDate":"{{{MidPeriodDate}}}"}""";

        // the trees must be equal up to the date for the comparison to mean something, and different after it
        DateOnly endDate = DateOnly.Parse(MidPeriodDate);
        Assert.Equal(await _truth.Gateway.GetCandlesUpToAsync(Primary, TimeFrame.D1, endDate, 10000), await _alteredAfterMid.Gateway.GetCandlesUpToAsync(Primary, TimeFrame.D1, endDate, 10000));
        Assert.NotEqual(await _truth.Gateway.GetCandlesAsync(Primary, TimeFrame.D1, 10000), await _alteredAfterMid.Gateway.GetCandlesAsync(Primary, TimeFrame.D1, 10000));

        string truth = await _truth.CallAsync(arguments);
        string altered = await _alteredAfterMid.CallAsync(arguments);

        Assert.Equal(truth, altered);
        Assert.Contains("\"endDate\":\"" + MidPeriodDate + "\"", truth, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckCanFail_WithoutTheDateTheAlteredTreeGivesAnotherAnswer()
    {
        string arguments = $$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":{{{MutationIndicators}}},"outputTailCount":30}""";

        string truth = await _truth.CallAsync(arguments);
        string altered = await _alteredAfterMid.CallAsync(arguments);

        Assert.NotEqual(truth, altered);
    }

    [Fact]
    public async Task TheComparisonSymbolIsCutAtTheSameDateAsTheSymbol()
    {
        string answer = await _truth.CallAsync($$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"p","type":"PairRatio","parameters":{"ComparisonSymbol":"AAPL"}}],"outputTailCount":3,"endDate":"{{{MidPeriodDate}}}"}""");

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement pair = document.RootElement.GetProperty("results").GetProperty("p");
        Assert.True(pair.GetProperty("isSuccessful").GetBoolean(), pair.GetRawText());
        Assert.Equal(MidPeriodDate, document.RootElement.GetProperty("latestTimestamp").GetString());
        // both symbols hold the same closes, so the ratio of the same date is 1 on every returned bar
        Assert.All(pair.GetProperty("series").EnumerateObject().First().Value.EnumerateArray(), value => Assert.Equal(1m, value.GetDecimal()));
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2024/12/18")]
    [InlineData("18-12-2024")]
    [InlineData("not a date")]
    public async Task AnEndDateInAnotherFormIsInvalidArgumentNamingIt(string endDate)
    {
        using JsonDocument response = await _truth.CallRawAsync($$$"""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}],"endDate":"{{{endDate}}}"}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("endDate", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task AnEndDateBeforeTheFirstStoredBarIsInsufficientMarketDataNamingTheDate(string frame)
    {
        using JsonDocument response = await _truth.CallRawAsync($$$"""{"symbol":"MSFT","timeFrame":"{{{frame}}}","indicators":[{"id":"s","type":"SMA"}],"endDate":"2023-12-31"}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InsufficientMarketData, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("endDate", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Contains("2023-12-31", McpTestSupport.Structured(response).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOfDateIsRefusedWithAHintThatNamesEndDate()
    {
        using JsonDocument response = await _truth.CallRawAsync("""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"s","type":"SMA"}],"asOfDate":"2024-12-18"}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("asOfDate", error.GetProperty("param").GetString());
        string hint = error.GetProperty("hint").GetString()!;
        Assert.Contains("endDate", hint, StringComparison.Ordinal);
        Assert.Contains("asOfDate", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMisnamedArgumentHintAppliesToTopLevelNamesOnly()
    {
        using JsonDocument response = await _truth.CallRawAsync("""{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"s","type":"SMA","asOfDate":"2024-12-18"}]}""");

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("indicators[0].asOfDate", error.GetProperty("param").GetString());
        Assert.Contains("tools/list", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherToolWithoutHintsKeepsTheGenericUnknownArgumentHint()
    {
        using JsonDocument response = await _truth.CallToolRawAsync("sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","asOfDate":"2024-12-18"}""");

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("asOfDate", error.GetProperty("param").GetString());
        Assert.Equal("Call tools/list to see the accepted arguments.", error.GetProperty("hint").GetString());
    }

    private static CoreCandleData ToCore(CandleData bar) => new(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

    // ---- hosting -----------------------------------------------------------------------------------------------------------------------

    /// <summary>A throw-away data tree with the primary and the comparison symbol (the same closes), optionally with every close after a date multiplied by ten.</summary>
    private sealed class World : IDisposable
    {
        private const decimal Wild = 10m;

        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;
        private readonly ServiceProvider _services;

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
            _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
            Gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        }

        public IReadOnlyStockAnalyzerGateway Gateway { get; }

        /// <summary>The structured answer of sa_calculate_indicators as text; the call must succeed.</summary>
        public async Task<string> CallAsync(string argumentsJson)
        {
            using JsonDocument response = await CallRawAsync(argumentsJson);
            Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
            return McpTestSupport.Structured(response).GetRawText();
        }

        public Task<JsonDocument> CallRawAsync(string argumentsJson) => CallToolRawAsync("sa_calculate_indicators", argumentsJson);

        public async Task<JsonDocument> CallToolRawAsync(string tool, string argumentsJson)
            => (await McpTestSupport.CallAsync(_runtime.Dispatcher, tool, argumentsJson))!;

        public void Dispose()
        {
            _runtime.Dispose();
            _services.Dispose();
            _fixture.Dispose();
        }
    }
}
