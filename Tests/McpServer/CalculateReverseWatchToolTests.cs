using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_calculate_reverse_watch through the real composition. WAVE is a sine-shaped close series; its curve is compared with the application's own
/// service called directly on the same bars. The geometry table is pinned to the service's sectors by reading every point against its labels.
/// </summary>
public sealed class CalculateReverseWatchToolTests : IDisposable
{
    private const int Days = 300;
    private const string Wave = "WAVE";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "wave");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => Math.Round(100m + 10m * (decimal)Math.Sin(i / 9.0), 2)).ToList();

    public CalculateReverseWatchToolTests()
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

    /// <summary>A call with the required arguments; <paramref name="extra"/> is appended as further properties, each starting with a comma.</summary>
    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Wave, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_reverse_watch", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

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
    public async Task ThePointsAreTheApplicationsServiceOnTheSameBars()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, ",\"period\":20,\"dataCount\":60,\"outputTailCount\":10"));

        ReverseWatchCurveData direct = new ReverseWatchAnalysisService().Calculate(
            Candles().Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(), period: 20, stockCode: Wave, dataCount: 60);
        JsonElement[] recent = result.GetProperty("recent").EnumerateArray().ToArray();
        ReverseWatchCurvePoint[] expected = direct.Points.Skip(direct.Points.Count - 10).ToArray();

        Assert.Equal(Days, result.GetProperty("calculatedBarCount").GetInt32());
        Assert.Equal(20, result.GetProperty("period").GetInt32());
        Assert.True(result.GetProperty("isMaBased").GetBoolean());
        Assert.False(result.GetProperty("isLogScaleVolume").GetBoolean());
        Assert.Equal(60, result.GetProperty("dataCount").GetInt32());
        Assert.Equal(direct.Points.Count, result.GetProperty("pointCount").GetInt32());
        Assert.Equal(direct.Bounds.MinPrice, result.GetProperty("bounds").GetProperty("minPrice").GetDecimal());
        Assert.Equal(direct.Bounds.MaxPrice, result.GetProperty("bounds").GetProperty("maxPrice").GetDecimal());
        Assert.Equal(direct.Bounds.MinVolume, result.GetProperty("bounds").GetProperty("minVolume").GetDecimal());
        Assert.Equal(direct.Bounds.MaxVolume, result.GetProperty("bounds").GetProperty("maxVolume").GetDecimal());
        Assert.True(result.GetProperty("bounds").TryGetProperty("rawMinPrice", out _));
        Assert.True(result.GetProperty("bounds").TryGetProperty("rawMaxPrice", out _));
        Assert.True(result.GetProperty("bounds").TryGetProperty("centerPrice", out _));
        Assert.True(result.GetProperty("bounds").TryGetProperty("rawMinVolume", out _));
        Assert.True(result.GetProperty("bounds").TryGetProperty("rawMaxVolume", out _));
        Assert.True(result.GetProperty("bounds").TryGetProperty("centerVolume", out _));
        Assert.Equal("linear", result.GetProperty("bounds").GetProperty("volumeUnit").GetString());
        Assert.Equal(10, recent.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Date, DateTime.Parse(recent[i].GetProperty("date").GetString()!));
            Assert.Equal(expected[i].Phase.ToString(), recent[i].GetProperty("phase").GetString());
            Assert.Equal(expected[i].PriceAverage, recent[i].GetProperty("priceAverage").GetDecimal());
            Assert.Equal(expected[i].VolumeAverage, recent[i].GetProperty("volumeAverage").GetDecimal());
            Assert.Equal(expected[i].Close, recent[i].GetProperty("close").GetDecimal());
            Assert.Equal(expected[i].Volume, recent[i].GetProperty("volume").GetInt64());
        }

        Assert.All(recent, point => Assert.Matches("^Phase[1-8]$", point.GetProperty("phase").GetString()!));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(ReverseWatchPhaseGeometry.TryGet(expected[i].Phase, out string price, out string volume));
            Assert.Equal(price, recent[i].GetProperty("pricePosition").GetString());
            Assert.Equal(volume, recent[i].GetProperty("volumePosition").GetString());
        }
    }

    [Fact]
    public async Task TheDefaultsAreTheChartsAndAnEmptyCallUsesThem()
    {
        var dispatcher = await StartAsync();

        JsonElement empty = Result(await CallAsync(dispatcher));

        Assert.Equal(ChartConstants.DefaultReverseWatchPeriod, empty.GetProperty("period").GetInt32());
        Assert.Equal(ChartConstants.DefaultReverseWatchDataCount, empty.GetProperty("dataCount").GetInt32());
        Assert.True(empty.GetProperty("isMaBased").GetBoolean());
        Assert.False(empty.GetProperty("isLogScaleVolume").GetBoolean());
        Assert.Equal(25, ChartConstants.DefaultReverseWatchPeriod);
    }

    [Fact]
    public async Task EndDateHistoricalCutoffMatchesApplicationServiceOnBarsUpToDate()
    {
        var dispatcher = await StartAsync();
        string cutoff = "2024-02-15";
        DateTime cutoffDate = DateTime.Parse(cutoff);

        JsonElement result = Result(await CallAsync(dispatcher, $",\"endDate\":\"{cutoff}\",\"period\":10,\"dataCount\":30"));

        var barsUpTo = Candles().Where(c => c.Timestamp <= cutoffDate).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        ReverseWatchCurveData direct = new ReverseWatchAnalysisService().Calculate(barsUpTo, period: 10, stockCode: Wave, dataCount: 30);

        Assert.Equal(cutoff, result.GetProperty("endDate").GetString());
        Assert.Equal(barsUpTo.Count, result.GetProperty("calculatedBarCount").GetInt32());
        Assert.Equal(direct.Points.Count, result.GetProperty("pointCount").GetInt32());
        Assert.Equal(direct.Bounds.MinPrice, result.GetProperty("bounds").GetProperty("minPrice").GetDecimal());
    }

    [Fact]
    public async Task ARequestThatCannotRunIsRefusedNamingTheArgument()
    {
        var dispatcher = await StartAsync();
        var refusals = new (string Arguments, string Code, string Parameter)[]
        {
            ("""{"symbol":"NOPE","timeFrame":"D1"}""", "SYMBOL_NOT_FOUND", "symbol"),
            ("""{"symbol":"WAVE","timeFrame":"H1"}""", "UNSUPPORTED_TIMEFRAME", "timeFrame"),
            ("""{"symbol":"WAVE","timeFrame":"D1","period":0}""", "INVALID_ARGUMENT", "period"),
            ("""{"symbol":"WAVE","timeFrame":"D1","period":10001}""", "LIMIT_EXCEEDED", "period"),
            ("""{"symbol":"WAVE","timeFrame":"D1","dataCount":0}""", "INVALID_ARGUMENT", "dataCount"),
            ("""{"symbol":"WAVE","timeFrame":"D1","dataCount":1}""", "INVALID_ARGUMENT", "dataCount"),
            ("""{"symbol":"WAVE","timeFrame":"D1","isMaBased":"yes"}""", "INVALID_ARGUMENT", "isMaBased"),
            ("""{"symbol":"WAVE","timeFrame":"D1","window":5}""", "INVALID_ARGUMENT", "window"),
            ("""{"symbol":"WAVE","timeFrame":"D1","outputTailCount":5001}""", "LIMIT_EXCEEDED", "outputTailCount"),
            ("""{"symbol":"WAVE","timeFrame":"D1","lookbackBars":0}""", "INVALID_ARGUMENT", "lookbackBars"),
            ("""{"symbol":"WAVE","timeFrame":"D1","lookbackBars":10001}""", "LIMIT_EXCEEDED", "lookbackBars"),
            ("""{"symbol":"WAVE","timeFrame":"D1","period":25,"dataCount":25,"lookbackBars":40}""", "INVALID_ARGUMENT", "lookbackBars"),
            ("""{"symbol":"WAVE","timeFrame":"D1","endDate":"invalid-date"}""", "INVALID_ARGUMENT", "endDate"),
            ("""{"symbol":"WAVE","timeFrame":"D1","endDate":"2020-01-01"}""", "INSUFFICIENT_MARKET_DATA", "endDate"),
        };

        foreach ((string arguments, string code, string parameter) in refusals)
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_reverse_watch", arguments))!;

            Assert.Equal(code, Code(response));
            Assert.Equal(parameter, Param(response));
        }
    }

    [Fact]
    public async Task FlatDataSingularityOutputsPhase0AndNeutralDescription()
    {
        const string flatSymbol = "msft";
        var flatCloses = Enumerable.Repeat(100m, Days).ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, flatSymbol, flatCloses);

        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"period\":10,\"dataCount\":10,\"outputTailCount\":5", symbol: flatSymbol));

        JsonElement[] recent = result.GetProperty("recent").EnumerateArray().ToArray();
        Assert.NotEmpty(recent);
        foreach (var point in recent)
        {
            Assert.Equal("Phase0", point.GetProperty("phase").GetString());
            Assert.Equal("Neutral (No Variation)", point.GetProperty("phaseDescription").GetString());
            Assert.Equal(0m, point.GetProperty("angle").GetDecimal());
            Assert.Equal(0, point.GetProperty("phaseNumber").GetInt32());
            Assert.Equal(ReverseWatchPhaseGeometry.Middle, point.GetProperty("pricePosition").GetString());
            Assert.Equal(ReverseWatchPhaseGeometry.Middle, point.GetProperty("volumePosition").GetString());
        }
    }

    [Fact]
    public async Task APeriodLongerThanTheStoredBarsIsRefusedNamingPeriod()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, $",\"period\":{Days + 1}");

        Assert.Equal("INSUFFICIENT_MARKET_DATA", Code(response));
        Assert.Equal("period", Param(response));
    }

    [Fact]
    public async Task ARunChangesNothingInTheDataTree()
    {
        var dispatcher = await StartAsync();
        var before = _fixture.Snapshot();

        Result(await CallAsync(dispatcher, ",\"period\":20"));

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task TheToolIsListedAsReadOnlyAndNamesOnlyItsOwnArguments()
    {
        var dispatcher = await StartAsync();

        using JsonDocument listing = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_calculate_reverse_watch");

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(new[] { "symbol", "timeFrame" }, tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Contains("no meaning is attached to a phase", tool.GetProperty("description").GetString());
        Assert.DoesNotContain("onfluence", tool.GetProperty("description").GetString());
    }

    [Fact]
    public void EveryPhaseHasItsOwnGeometryAndNonePhaseHasNone()
    {
        var positions = Enum.GetValues<ReverseWatchPhase>()
            .Where(phase => phase != ReverseWatchPhase.None)
            .Select(phase => ReverseWatchPhaseGeometry.TryGet(phase, out string price, out string volume) ? (price, volume) : throw new Xunit.Sdk.XunitException($"{phase} has no geometry."))
            .ToList();

        Assert.Equal(8, positions.Count);
        Assert.Equal(8, positions.Distinct().Count());
        Assert.DoesNotContain((ReverseWatchPhaseGeometry.Middle, ReverseWatchPhaseGeometry.Middle), positions);
        Assert.False(ReverseWatchPhaseGeometry.TryGet(ReverseWatchPhase.None, out _, out _));
    }

    [Fact]
    public void TheGeometryTableAgreesWithWhereTheApplicationsServicePutsEveryPoint()
    {
        // Independent of the service's sector code: a point's phase must be one whose labels match the signs of its offsets from the window centre.
        // High = positive offset, Low = negative, Middle = inside the 22.5-degree wedge around the axis (|offset| <= r * sin 22.5 degrees).
        const int dataCount = 80;
        // The shared fixture has a constant volume, which would keep every point on the vertical axis: vary the volume out of phase with the price.
        List<CandleData> bars = Candles()
            .Select((c, i) => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, 1000L + (long)(600.0 * Math.Sin(i / 5.0 + 1.0))))
            .ToList();
        ReverseWatchCurveData curve = new ReverseWatchAnalysisService().Calculate(bars, period: 5, stockCode: Wave, dataCount: dataCount);
        List<ReverseWatchCurvePoint> window = curve.Points.Skip(curve.Points.Count - dataCount).ToList();
        decimal centreP = (window.Max(p => p.PriceAverage) + window.Min(p => p.PriceAverage)) / 2m;
        decimal centreV = (window.Max(p => p.VolumeAverage) + window.Min(p => p.VolumeAverage)) / 2m;
        double rangeP = (double)((window.Max(p => p.PriceAverage) - window.Min(p => p.PriceAverage)) / 2m);
        double rangeV = (double)((window.Max(p => p.VolumeAverage) - window.Min(p => p.VolumeAverage)) / 2m);
        if (rangeP == 0) { rangeP = 1; }
        if (rangeV == 0) { rangeV = 1; }

        var seen = new HashSet<ReverseWatchPhase>();
        foreach (ReverseWatchCurvePoint point in window)
        {
            double y = (double)(point.PriceAverage - centreP) / rangeP;
            double x = (double)(point.VolumeAverage - centreV) / rangeV;
            double wedge = Math.Sqrt(x * x + y * y) * Math.Sin(22.5 * Math.PI / 180.0) + 1e-9;
            Assert.True(ReverseWatchPhaseGeometry.TryGet(point.Phase, out string price, out string volume), $"{point.Phase} on {point.Date:yyyy-MM-dd}");
            AssertPosition(price, y, wedge, "price", point);
            AssertPosition(volume, x, wedge, "volume", point);
            seen.Add(point.Phase);
        }

        Assert.True(seen.Count >= 6, $"The series should visit most phases so that the table is exercised; visited {string.Join(",", seen)}.");
    }

    private static void AssertPosition(string label, double offset, double wedge, string axis, ReverseWatchCurvePoint point)
    {
        bool matches = label switch
        {
            ReverseWatchPhaseGeometry.High => offset > 0,
            ReverseWatchPhaseGeometry.Low => offset < 0,
            _ => Math.Abs(offset) <= wedge,
        };
        Assert.True(matches, $"{point.Phase} on {point.Date:yyyy-MM-dd}: {axis} is labelled {label} but its offset is {offset:F4}.");
    }
}
