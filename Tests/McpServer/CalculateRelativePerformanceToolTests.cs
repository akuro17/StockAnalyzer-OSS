using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_calculate_relative_performance through the real composition on a throw-away data tree; values are compared with the chart's own aligner and calculators.</summary>
public sealed class CalculateRelativePerformanceToolTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Primary = "MSFT";
    private const string Other = "AAPL";
    private const string Tiny = "TINY";
    private const string Tool = "sa_calculate_relative_performance";
    private const int MaxSecondsForAnOversizedList = 3;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl", "tiny");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<ServiceProvider> _providers = new();

    public CalculateRelativePerformanceToolTests()
    {
        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Tiny, Enumerable.Range(0, 10).Select(i => 50m + i).ToList());
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        foreach (ServiceProvider provider in _providers)
        {
            provider.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] limitOverrides)
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

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(limitOverrides), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, Tool, argumentsJson))!;

    private static string Args(string extra = "", string symbol = Primary, string timeFrame = "D1")
        => $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}";

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string Code(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static decimal?[] Values(JsonElement result, string symbol)
        => result.GetProperty("series").GetProperty(symbol).GetProperty("values").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetDecimal()).ToArray();

    /// <summary>The wire format of a date (yyyy-MM-dd), independent of the machine's culture.</summary>
    private static string Day(IFormattable date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private IReadOnlyStockAnalyzerGateway Gateway()
    {
        ServiceProvider provider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
    }

    /// <summary>The chart's path for the same stored bars: its aligner, then the shared calculator.</summary>
    private async Task<(ComparisonAlignedData Aligned, ComparisonSeriesResult Values)> ChartPathAsync(ComparisonMode mode, int tail, int period = 20)
    {
        IReadOnlyStockAnalyzerGateway gateway = Gateway();
        var aligner = new ComparisonDataAligner(new WindowedDataService(new CandleWindowLoader(gateway)));
        ComparisonAlignedData aligned = await aligner.AlignAsync(Primary, new[] { Other }, TimeFrame.D1, StoredDays);
        return (aligned, ComparisonSeriesCalculator.Calculate(aligned, aligned.Timestamps.Length - tail, tail, mode, period));
    }

    // ---- parity with the chart's path ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Performance")]
    [InlineData("Ratio")]
    [InlineData("Spread")]
    [InlineData("ZScore")]
    public async Task EveryModeGivesTheValuesOfTheChartsOwnAlignerAndCalculator(string modeName)
    {
        var mode = Enum.Parse<ComparisonMode>(modeName);
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"],\"mode\":\"{modeName}\",\"zScorePeriod\":7,\"outputTailCount\":60")));
        (ComparisonAlignedData aligned, ComparisonSeriesResult expected) = await ChartPathAsync(mode, 60, period: 7);

        Assert.Equal(modeName, result.GetProperty("mode").GetString());
        Assert.Equal(aligned.Timestamps.Length, result.GetProperty("alignedBarCount").GetInt32());
        Assert.Equal(60, result.GetProperty("returnedBarCount").GetInt32());
        Assert.Equal(expected.Series[Primary].Values, Values(result, Primary));
        Assert.Equal(expected.Series[Other].Values, Values(result, Other));
        Assert.Equal(
            aligned.Timestamps.Skip(aligned.Timestamps.Length - 60).Select(t => Day(t)).ToArray(),
            Strings(result.GetProperty("timestamps")));
    }

    [Fact]
    public async Task PerformanceIsThePercentageChangeOfTheCloseFromTheFirstReturnedBar()
    {
        IReadOnlyList<CandleData> bars = await Gateway().GetCandlesAsync(Primary, TimeFrame.D1, StoredDays);
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args(",\"outputTailCount\":50")));

        CandleData[] window = bars.Skip(bars.Count - 50).ToArray();
        decimal?[] expected = window.Select(b => (decimal?)((b.Close - window[0].Close) / window[0].Close * 100m)).ToArray();
        Assert.Equal(expected, Values(result, Primary));
        Assert.Equal(0m, Values(result, Primary)[0]);
        Assert.Equal("percent", result.GetProperty("unit").GetString());
        Assert.Equal(Day(window[0].Timestamp), result.GetProperty("baselineDate").GetString());
        JsonElement primary = result.GetProperty("series").GetProperty(Primary);
        Assert.True(primary.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(window[0].Close, primary.GetProperty("basePrice").GetDecimal());
        Assert.Equal(Day(window[0].Timestamp), primary.GetProperty("baseDate").GetString());
    }

    [Fact]
    public async Task SpreadAndRatioAreTheComparisonCloseAgainstThePrimaryClose()
    {
        IReadOnlyStockAnalyzerGateway gateway = Gateway();
        CandleData[] primary = (await gateway.GetCandlesAsync(Primary, TimeFrame.D1, StoredDays)).TakeLast(30).ToArray();
        CandleData[] other = (await gateway.GetCandlesAsync(Other, TimeFrame.D1, StoredDays)).TakeLast(30).ToArray();
        Assert.Equal(primary.Select(b => b.Timestamp), other.Select(b => b.Timestamp));
        var dispatcher = await StartAsync();

        JsonElement spread = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"],\"mode\":\"Spread\",\"outputTailCount\":30")));
        JsonElement ratio = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"],\"mode\":\"Ratio\",\"outputTailCount\":30")));

        Assert.Equal(other.Zip(primary, (o, p) => (decimal?)(o.Close - p.Close)).ToArray(), Values(spread, Other));
        Assert.Equal(other.Zip(primary, (o, p) => (decimal?)(o.Close / p.Close)).ToArray(), Values(ratio, Other));
        Assert.All(Values(spread, Primary), v => Assert.Equal(0m, v));
        Assert.All(Values(ratio, Primary), v => Assert.Equal(1m, v));
        Assert.Equal("price-difference", spread.GetProperty("unit").GetString());
        Assert.Equal("ratio", ratio.GetProperty("unit").GetString());
        Assert.False(spread.TryGetProperty("baselineDate", out _));
    }

    [Fact]
    public async Task ZScoreIsTheDistanceFromTheMeanInPopulationDeviationsAndIsUndefinedDuringWarmUp()
    {
        CandleData[] bars = (await Gateway().GetCandlesAsync(Primary, TimeFrame.D1, 30)).ToArray();
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args(",\"mode\":\"ZScore\",\"zScorePeriod\":5,\"lookbackBars\":30,\"outputTailCount\":30")));

        decimal?[] values = Values(result, Primary);
        Assert.Equal(30, values.Length);
        Assert.All(values.Take(4), v => Assert.Null(v));
        for (int i = 4; i < values.Length; i++)
        {
            double[] window = bars.Skip(i - 4).Take(5).Select(b => (double)b.Close).ToArray();
            double mean = window.Average();
            double deviation = Math.Sqrt(window.Sum(c => (c - mean) * (c - mean)) / window.Length);
            Assert.Equal((window[^1] - mean) / deviation, (double)values[i]!.Value, 9);
        }

        Assert.Equal(5, result.GetProperty("zScorePeriod").GetInt32());
        Assert.Equal("sigma", result.GetProperty("unit").GetString());
    }

    // ---- defaults, window and end date ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlySymbolAndTimeFrameGiveThePrimarySymbolAlone()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args()));

        Assert.Equal("Performance", result.GetProperty("mode").GetString());
        Assert.Equal(200, result.GetProperty("returnedBarCount").GetInt32());
        Assert.Equal(StoredDays, result.GetProperty("alignedBarCount").GetInt32());
        Assert.Equal(new[] { Primary }, result.GetProperty("series").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Empty(result.GetProperty("excludedSymbols").EnumerateArray());
        Assert.Empty(result.GetProperty("ignoredSymbols").EnumerateArray());
        Assert.False(result.TryGetProperty("zScorePeriod", out _));
        Assert.False(result.TryGetProperty("endDate", out _));
    }

    [Fact]
    public async Task TheResultCarriesExactlyTheDocumentedProperties()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args()));

        Assert.Equal(
            new[] { "alignedBarCount", "baselineDate", "excludedSymbols", "ignoredSymbols", "lookbackBars", "mode", "returnedBarCount", "series", "symbol", "timeFrame", "timestamps", "unit", "warnings" },
            result.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task AnEndDateCutsThePrimaryAndTheComparisonSymbolAtThatDay()
    {
        IReadOnlyStockAnalyzerGateway gateway = Gateway();
        IReadOnlyList<CandleData> all = await gateway.GetCandlesAsync(Primary, TimeFrame.D1, StoredDays);
        DateOnly endDate = DateOnly.FromDateTime(all[^41].Timestamp);
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"],\"endDate\":\"{Day(endDate)}\",\"outputTailCount\":20")));

        string[] timestamps = Strings(result.GetProperty("timestamps"));
        Assert.Equal(Day(endDate), timestamps[^1]);
        Assert.Equal(Day(endDate), result.GetProperty("endDate").GetString());
        Assert.Equal(StoredDays - 40, result.GetProperty("alignedBarCount").GetInt32());
        IReadOnlyList<CandleData> otherUpTo = await gateway.GetCandlesUpToAsync(Other, TimeFrame.D1, endDate, 20);
        decimal baseClose = otherUpTo[0].Close;
        Assert.Equal(otherUpTo.Select(b => (decimal?)((b.Close - baseClose) / baseClose * 100m)).ToArray(), Values(result, Other));
    }

    [Fact]
    public async Task WeeklyBarsAreSupported()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"]", timeFrame: "W1")));

        Assert.Equal("W1", result.GetProperty("timeFrame").GetString());
        Assert.True(result.GetProperty("returnedBarCount").GetInt32() > 10);
        Assert.All(Values(result, Primary), v => Assert.NotNull(v));
    }

    [Fact]
    public async Task ASingleBarGivesZeroPerformanceAndAnUndefinedZScoreWithAWarning()
    {
        var dispatcher = await StartAsync();

        JsonElement performance = Ok(await CallAsync(dispatcher, Args(",\"lookbackBars\":1")));
        JsonElement zScore = Ok(await CallAsync(dispatcher, Args(",\"lookbackBars\":1,\"mode\":\"ZScore\"")));

        Assert.Equal(new decimal?[] { 0m }, Values(performance, Primary));
        Assert.Equal(new decimal?[] { null }, Values(zScore, Primary));
        Assert.Contains($"{Primary}: no valid value in the returned window.", Strings(zScore.GetProperty("warnings")));
    }

    // ---- symbols ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePrimarySymbolAndRepeatsAreIgnoredAndReported()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Primary}\",\"aapl\",\"{Other}\"]")));

        Assert.Equal(new[] { Primary, Other }, result.GetProperty("series").EnumerateObject().Select(p => p.Name).ToArray());
        JsonElement[] ignored = result.GetProperty("ignoredSymbols").EnumerateArray().ToArray();
        Assert.Equal(new[] { Primary, Other }, ignored.Select(i => i.GetProperty("symbol").GetString()).ToArray());
        Assert.Equal(new[] { "same as the primary symbol", "listed more than once" }, ignored.Select(i => i.GetProperty("reason").GetString()).ToArray());
        Assert.False(result.GetProperty("series").GetProperty(Other).GetProperty("isPrimary").GetBoolean());
    }

    [Fact]
    public async Task AMostlyMissingComparisonSymbolIsExcludedWithAWarning()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Tiny}\",\"{Other}\"]")));

        Assert.Equal(new[] { Primary, Other }, result.GetProperty("series").EnumerateObject().Select(p => p.Name).ToArray());
        JsonElement excluded = Assert.Single(result.GetProperty("excludedSymbols").EnumerateArray());
        Assert.Equal(Tiny, excluded.GetProperty("symbol").GetString());
        Assert.Contains(Strings(result.GetProperty("warnings")), w => w.StartsWith(Tiny, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoreDifferentComparisonSymbolsThanTheLimitIsRefusedNotCut()
    {
        var dispatcher = await StartAsync(("MaxComparisonTargets", "1"));

        using JsonDocument tooMany = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\",\"{Tiny}\"]"));
        using JsonDocument withinLimit = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Primary}\",\"{Other}\",\"{Other}\"]"));

        Assert.Equal("LIMIT_EXCEEDED", Code(tooMany));
        Assert.Equal("comparisonSymbols", Param(tooMany));
        Ok(withinLimit);
    }

    [Fact]
    public async Task AHugeListOfDifferentSymbolsIsRefusedWithoutScanningItAll()
    {
        const int differentSymbols = 40_000;
        var dispatcher = await StartAsync();
        string names = string.Join(",", Enumerable.Range(0, differentSymbols).Select(i => $"\"S{i}\""));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        using JsonDocument response = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[{names}]"));
        timer.Stop();

        Assert.Equal("LIMIT_EXCEEDED", Code(response));
        Assert.Equal("comparisonSymbols", Param(response));
        // The refusal happens at the first symbol over the limit, so the work does not grow with the list (a full scan took many seconds).
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(MaxSecondsForAnOversizedList), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task TheLimitIsCheckedBeforeAnyStoredTickerLookup()
    {
        var dispatcher = await StartAsync(("MaxComparisonTargets", "1"));

        using JsonDocument overLimit = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\",\"NOSUCH\"]"));
        using JsonDocument malformed = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\",\"a/b\"]"));

        Assert.Equal("LIMIT_EXCEEDED", Code(overLimit));
        Assert.Equal("comparisonSymbols", Param(overLimit));
        Assert.Equal("INVALID_ARGUMENT", Code(malformed));
        Assert.Equal("comparisonSymbols[1]", Param(malformed));
    }

    // ---- refusals --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownSymbolsAreRefusedAtTheirOwnArgument()
    {
        var dispatcher = await StartAsync();

        using JsonDocument primary = await CallAsync(dispatcher, Args(symbol: "NOSUCH"));
        using JsonDocument comparison = await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\",\"NOSUCH\"]"));

        Assert.Equal("SYMBOL_NOT_FOUND", Code(primary));
        Assert.Equal("symbol", Param(primary));
        Assert.Equal("SYMBOL_NOT_FOUND", Code(comparison));
        Assert.Equal("comparisonSymbols[1]", Param(comparison));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(501)]
    public async Task AZScorePeriodOutsideTwoToFiveHundredIsRefusedInEveryMode(int period)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, Args($",\"zScorePeriod\":{period}"));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal("zScorePeriod", Param(response));
    }

    [Theory]
    [InlineData("\"mode\":\"performance\"")]
    [InlineData("\"mode\":\"Other\"")]
    [InlineData("\"comparisonSymbols\":\"AAPL\"")]
    [InlineData("\"endDate\":\"2025-13-01\"")]
    [InlineData("\"outputTailCount\":0")]
    [InlineData("\"surprise\":1")]
    public async Task MalformedArgumentsAreRefused(string fragment)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, Args("," + fragment));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    [Fact]
    public async Task AnEndDateBeforeTheFirstStoredBarIsInsufficientMarketData()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, Args(",\"endDate\":\"1990-01-01\""));

        Assert.Equal("INSUFFICIENT_MARKET_DATA", Code(response));
        Assert.Equal("endDate", Param(response));
    }

    [Fact]
    public async Task TheToolChangesNothingInTheDataTree()
    {
        var before = _fixture.Snapshot();
        var dispatcher = await StartAsync();

        Ok(await CallAsync(dispatcher, Args($",\"comparisonSymbols\":[\"{Other}\"],\"mode\":\"ZScore\"")));

        Assert.Equal(before, _fixture.Snapshot());
    }
}
