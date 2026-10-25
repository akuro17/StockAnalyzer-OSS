using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class CalculateChartSeriesToolTests : IDisposable
{
    private const int Days = 300;
    private const string Wave = "WAVE";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "wave");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => Math.Round(100m + 10m * (decimal)Math.Sin(i / 9.0), 2)).ToList();

    public CalculateChartSeriesToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Wave, _closes);
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

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string chartType, string extra = "", string symbol = Wave, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_chart_series", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\",\"chartType\":\"{chartType}\"{extra}}}"))!;

    private static JsonElement Result(JsonDocument response)
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

    private List<CoreCandleData> Candles()
    {
        var date = new DateTime(2024, 1, 1);
        return _closes.Select((close, i) => new CoreCandleData(date.AddDays(i), close - 0.2m, close + 0.5m, close - 0.5m, close, 1000)).ToList();
    }

    [Fact]
    public async Task HeikinAshi_EqualsDirectConverterResult()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "HeikinAshi", ",\"outputTailCount\":50"));

        var expectedBuffer = new List<CoreCandleData>();
        HeikinAshiConverter.Convert(Candles(), expectedBuffer);
        var expectedTail = expectedBuffer.Skip(expectedBuffer.Count - 50).ToArray();

        Assert.Equal("HeikinAshi", result.GetProperty("chartType").GetString());
        Assert.Equal(Days, result.GetProperty("sourceBarCount").GetInt32());
        Assert.Equal(Days, result.GetProperty("elementCount").GetInt32());
        Assert.Equal(Days - 50, result.GetProperty("outputStartElementIndex").GetInt32());

        JsonElement[] elements = result.GetProperty("elements").EnumerateArray().ToArray();
        Assert.Equal(50, elements.Length);

        for (int i = 0; i < expectedTail.Length; i++)
        {
            Assert.Equal(expectedTail[i].Open, elements[i].GetProperty("open").GetDecimal());
            Assert.Equal(expectedTail[i].High, elements[i].GetProperty("high").GetDecimal());
            Assert.Equal(expectedTail[i].Low, elements[i].GetProperty("low").GetDecimal());
            Assert.Equal(expectedTail[i].Close, elements[i].GetProperty("close").GetDecimal());
            string expectedDir = expectedTail[i].Close >= expectedTail[i].Open ? "Bullish" : "Bearish";
            Assert.Equal(expectedDir, elements[i].GetProperty("direction").GetString());
        }
    }

    [Fact]
    public async Task Renko_EqualsDirectCalculatorWithConfiguredReversalAndBrickSize()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "Renko", ",\"sizingMode\":\"Fixed\",\"brickSize\":2.0,\"reversal\":2,\"includeMultiWave\":true,\"outputTailCount\":10"));

        Assert.Equal("Renko", result.GetProperty("chartType").GetString());
        Assert.Equal(2.0m, result.GetProperty("effectiveSize").GetDecimal());

        JsonElement[] elements = result.GetProperty("elements").EnumerateArray().ToArray();
        Assert.NotEmpty(elements);
        Assert.True(elements.Length <= 10);

        foreach (var el in elements)
        {
            Assert.True(el.TryGetProperty("runCount", out _));
            Assert.True(el.TryGetProperty("reversalPrice", out _));
            Assert.True(el.TryGetProperty("nextTargetPrice", out _));
            string dir = el.GetProperty("direction").GetString()!;
            Assert.Contains(dir, new[] { "Up", "Down" });
        }

        Assert.True(result.TryGetProperty("multiWaveSignals", out JsonElement signals));
        Assert.Equal(JsonValueKind.Array, signals.ValueKind);
    }

    [Fact]
    public async Task Kagi_OutputsTotalColumnsAndColumnMetrics()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "Kagi", ",\"sizingMode\":\"Fixed\",\"reversalAmount\":3.0,\"outputTailCount\":15"));

        Assert.Equal("Kagi", result.GetProperty("chartType").GetString());
        Assert.Equal(3.0m, result.GetProperty("effectiveSize").GetDecimal());
        Assert.True(result.GetProperty("totalColumns").GetInt32() > 0);

        JsonElement[] elements = result.GetProperty("elements").EnumerateArray().ToArray();
        Assert.NotEmpty(elements);

        foreach (var el in elements)
        {
            Assert.True(el.TryGetProperty("columnIndex", out _));
            Assert.True(el.TryGetProperty("isYang", out _));
            Assert.True(el.TryGetProperty("lineState", out _));
            Assert.True(el.TryGetProperty("columnSpan", out _));
            Assert.True(el.TryGetProperty("reversalPrice", out _));
        }
    }

    [Fact]
    public async Task PointAndFigure_OutputsPatternsWhenRequested()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "PointAndFigure", ",\"sizingMode\":\"Fixed\",\"boxSize\":1.5,\"reversal\":3,\"includePatterns\":true,\"outputTailCount\":20"));

        Assert.Equal("PointAndFigure", result.GetProperty("chartType").GetString());
        Assert.Equal(1.5m, result.GetProperty("effectiveSize").GetDecimal());

        JsonElement[] elements = result.GetProperty("elements").EnumerateArray().ToArray();
        Assert.NotEmpty(elements);

        foreach (var el in elements)
        {
            string colType = el.GetProperty("columnType").GetString()!;
            Assert.Contains(colType, new[] { "X", "O" });
            Assert.True(el.TryGetProperty("boxCount", out _));
            Assert.True(el.TryGetProperty("reversalPrice", out _));
            Assert.True(el.TryGetProperty("nextTargetPrice", out _));
        }

        Assert.True(result.TryGetProperty("pnfAnalysis", out JsonElement pnfAnalysis));
        Assert.True(pnfAnalysis.TryGetProperty("signals", out _));
        Assert.True(pnfAnalysis.TryGetProperty("activeTrendlines", out _));
    }

    [Fact]
    public async Task ThreeLineBreak_OutputsRunCountAndReversalPrice()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "ThreeLineBreak", ",\"lineCount\":3,\"minimumMove\":0.5,\"outputTailCount\":20"));

        Assert.Equal("ThreeLineBreak", result.GetProperty("chartType").GetString());

        JsonElement[] elements = result.GetProperty("elements").EnumerateArray().ToArray();
        Assert.NotEmpty(elements);

        foreach (var el in elements)
        {
            Assert.True(el.TryGetProperty("runCount", out _));
            Assert.True(el.TryGetProperty("reversalPrice", out _));
            Assert.True(el.TryGetProperty("nextTargetPrice", out _));
            Assert.True(el.TryGetProperty("startDate", out _));
            Assert.True(el.TryGetProperty("endDate", out _));
        }
    }

    [Fact]
    public async Task EndDate_FiltersBarsUpToSpecifiedDate()
    {
        var dispatcher = await StartAsync();
        string cutoff = "2024-02-15";
        DateTime cutoffDate = DateTime.Parse(cutoff);

        JsonElement result = Result(await CallAsync(dispatcher, "HeikinAshi", $",\"endDate\":\"{cutoff}\""));

        var expectedCount = Candles().Count(c => c.Timestamp <= cutoffDate);
        Assert.Equal(cutoff, result.GetProperty("endDate").GetString());
        Assert.Equal(expectedCount, result.GetProperty("sourceBarCount").GetInt32());
        Assert.Equal(expectedCount, result.GetProperty("elementCount").GetInt32());
    }

    [Fact]
    public async Task Refusals_ValidateInputsAndAliases()
    {
        var dispatcher = await StartAsync();
        var refusals = new (string Arguments, string Code, string Parameter)[]
        {
            ("""{"symbol":"NOPE","timeFrame":"D1","chartType":"HeikinAshi"}""", "SYMBOL_NOT_FOUND", "symbol"),
            ("""{"symbol":"WAVE","timeFrame":"H1","chartType":"HeikinAshi"}""", "UNSUPPORTED_TIMEFRAME", "timeFrame"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"InvalidChart"}""", "INVALID_ARGUMENT", "chartType"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","manualSize":1.0,"brickSize":2.0}""", "INVALID_ARGUMENT", "brickSize"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"HeikinAshi","sizingMode":"Fixed"}""", "INVALID_ARGUMENT", "sizingMode"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","lineCount":3}""", "INVALID_ARGUMENT", "lineCount"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"HeikinAshi","includeMultiWave":true}""", "INVALID_ARGUMENT", "includeMultiWave"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","includePatterns":true}""", "INVALID_ARGUMENT", "includePatterns"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","brickSize":0}""", "INVALID_ARGUMENT", "brickSize"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","atrPeriod":501}""", "LIMIT_EXCEEDED", "atrPeriod"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"HeikinAshi","outputTailCount":501}""", "LIMIT_EXCEEDED", "outputTailCount"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"HeikinAshi","endDate":"invalid-date"}""", "INVALID_ARGUMENT", "endDate"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"HeikinAshi","endDate":"2020-01-01"}""", "INSUFFICIENT_MARKET_DATA", "endDate"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","brickSize":2.0,"brickPercent":1.0}""", "INVALID_ARGUMENT", "brickPercent"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","sizingMode":"Fixed","brickPercent":1.0}""", "INVALID_ARGUMENT", "brickPercent"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","sizingMode":"Percentage","brickSize":2.0}""", "INVALID_ARGUMENT", "brickSize"),
            ("""{"symbol":"WAVE","timeFrame":"D1","chartType":"Renko","manualSize":0.0001}""", "INVALID_ARGUMENT", "chartType"),
        };

        foreach ((string arguments, string code, string parameter) in refusals)
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_chart_series", arguments))!;
            Assert.Equal(code, Code(response));
            Assert.Equal(parameter, Param(response));
        }
    }

    [Fact]
    public async Task Renko_AutoInfersFixedModeWhenBrickSizeSpecifiedWithoutSizingMode()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, "Renko", ",\"brickSize\":2.5,\"outputTailCount\":5"));

        Assert.Equal("Renko", result.GetProperty("chartType").GetString());
        Assert.Equal(2.5m, result.GetProperty("effectiveSize").GetDecimal());
    }

    [Fact]
    public void GatewayDefaultImplementation_DelegatesToService()
    {
        IReadOnlyStockAnalyzerGateway gateway = new MinimalFakeGateway();
        var result = gateway.CalculateChartSeries(
            new[] { new CoreCandleData(DateTime.UtcNow, 100, 105, 95, 102, 1000) },
            new ChartSeriesRequest(ChartType.HeikinAshi));
        Assert.Equal(1, result.ElementCount);
    }

    private sealed class MinimalFakeGateway : IReadOnlyStockAnalyzerGateway
    {
        public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null) => throw new NotImplementedException();
        public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task RunChangesNothingInTheDataTree()
    {
        var dispatcher = await StartAsync();
        var before = _fixture.Snapshot();

        Result(await CallAsync(dispatcher, "Renko", ",\"sizingMode\":\"Fixed\",\"brickSize\":2.0"));

        Assert.Equal(before, _fixture.Snapshot());
    }
}
