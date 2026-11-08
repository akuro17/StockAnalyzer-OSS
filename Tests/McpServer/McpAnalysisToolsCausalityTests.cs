using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Master Prompt 2 (Feature 2) causality verification suite:
/// Proves that FFT (sa_analyze_cycles, sa_project_price_path[Fft]), SSA (sa_project_price_path[Ssa], SSA indicators),
/// candle patterns (sa_detect_candle_patterns), chart patterns (sa_detect_chart_patterns), and divergences
/// (sa_detect_divergences) never alter past confirmed results when future data is added or mutated.
/// </summary>
public sealed class McpAnalysisToolsCausalityTests : IDisposable
{
    private const string Symbol = "CAUSAL";
    private const int Days = 700;
    private const int WildDays = 90;
    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly DateOnly CleanAsOf = new(2025, 8, 31);
    private static readonly DateOnly EarlierAsOf = new(2025, 8, 15);

    private readonly World _truth;
    private readonly World _alteredAfterClean;

    public McpAnalysisToolsCausalityTests()
    {
        decimal[] walk = GeneratePredictableSeries();
        _truth = new World(walk);
        _alteredAfterClean = new World(Altered(walk, CleanAsOf));
    }

    public void Dispose()
    {
        _truth.Dispose();
        _alteredAfterClean.Dispose();
    }

    private static decimal[] GeneratePredictableSeries()
    {
        var random = new Random(42);
        var closes = new decimal[Days];
        decimal close = 100m;
        for (int i = 0; i < Days; i++)
        {
            // Superimposed sine waves + small random step to create rich cycles, patterns, and divergences
            double cycle = Math.Sin(i * 2 * Math.PI / 30.0) * 15.0 + Math.Sin(i * 2 * Math.PI / 10.0) * 5.0;
            close = Math.Max(10m, 100m + (decimal)cycle + (decimal)(random.NextDouble() * 4.0 - 2.0));
            closes[i] = Math.Round(close, 4);
        }

        return closes;
    }

    private static decimal[] Altered(decimal[] original, DateOnly lastKeptDay)
    {
        int firstChanged = lastKeptDay.DayNumber - DateOnly.FromDateTime(FirstDay).DayNumber + 1;
        var closes = (decimal[])original.Clone();
        for (int i = firstChanged; i < Math.Min(Days, firstChanged + WildDays); i++)
        {
            closes[i] = i % 2 == 0 ? 5000m : 20m;
        }

        return closes;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 1. FFT Analysis Causality (sa_analyze_cycles & sa_project_price_path[method=Fft])
    // ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnalyzeCycles_AsOfInvariance_YieldsIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","endDate":"{{CleanAsOf:yyyy-MM-dd}}"}""";

        string truthJson = await _truth.CallAsync("sa_analyze_cycles", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_analyze_cycles", args);

        Assert.Equal(truthJson, alteredJson);

        using var doc = JsonDocument.Parse(truthJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("dominantPeriodBars").GetDouble() > 0);
        Assert.True(root.GetProperty("dominantPower").GetDouble() > 0);
    }

    [Fact]
    public async Task ProjectPricePath_Fft_AsOfInvariance_YieldsIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","method":"Fft","endDate":"{{CleanAsOf:yyyy-MM-dd}}","horizonBars":20}""";

        string truthJson = await _truth.CallAsync("sa_project_price_path", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_project_price_path", args);

        Assert.Equal(truthJson, alteredJson);

        using var doc = JsonDocument.Parse(truthJson);
        var path = doc.RootElement.GetProperty("projectedPath");
        Assert.Equal(20, path.GetArrayLength());
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 2. SSA Analysis & Indicators Causality (sa_project_price_path[method=Ssa] & 7 SSA Indicators)
    // ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectPricePath_Ssa_AsOfInvariance_YieldsIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","method":"Ssa","endDate":"{{CleanAsOf:yyyy-MM-dd}}","horizonBars":15}""";

        string truthJson = await _truth.CallAsync("sa_project_price_path", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_project_price_path", args);

        Assert.Equal(truthJson, alteredJson);

        using var doc = JsonDocument.Parse(truthJson);
        var path = doc.RootElement.GetProperty("projectedPath");
        Assert.Equal(15, path.GetArrayLength());
    }

    [Theory]
    [InlineData("SSA")]
    [InlineData("SSAAnomaly")]
    [InlineData("SSACycle")]
    [InlineData("SSAEntropy")]
    [InlineData("SSAResidualBand")]
    [InlineData("SSASNR")]
    [InlineData("SSASqueeze")]
    public async Task SsaIndicators_PrefixInvariance_ValuesAtEarlierDateDoNotChangeWhenLaterBarsAdded(string indicatorName)
    {
        string argsEarlier = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","indicators":[{"id":"ind1","type":"{{indicatorName}}"}],"endDate":"{{EarlierAsOf:yyyy-MM-dd}}","lookbackBars":2000,"outputTailCount":2000}""";
        string argsLater = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","indicators":[{"id":"ind1","type":"{{indicatorName}}"}],"endDate":"{{CleanAsOf:yyyy-MM-dd}}","lookbackBars":2000,"outputTailCount":2000}""";

        string earlierJson = await _truth.CallAsync("sa_calculate_indicators", argsEarlier);
        string laterJson = await _truth.CallAsync("sa_calculate_indicators", argsLater);

        using var docEarlier = JsonDocument.Parse(earlierJson);
        using var docLater = JsonDocument.Parse(laterJson);

        var timestampsEarlier = docEarlier.RootElement.GetProperty("timestamps").EnumerateArray().Select(x => x.GetString()!).ToList();
        var timestampsLater = docLater.RootElement.GetProperty("timestamps").EnumerateArray().Select(x => x.GetString()!).ToList();

        var seriesEarlier = docEarlier.RootElement.GetProperty("results").GetProperty("ind1").GetProperty("series");
        var seriesLater = docLater.RootElement.GetProperty("results").GetProperty("ind1").GetProperty("series");

        foreach (var property in seriesEarlier.EnumerateObject())
        {
            var arrEarlier = property.Value.EnumerateArray().ToList();
            var arrLater = seriesLater.GetProperty(property.Name).EnumerateArray().ToList();

            var laterMap = new Dictionary<string, decimal?>();
            for (int j = 0; j < timestampsLater.Count; j++)
            {
                var v = arrLater[j].ValueKind == JsonValueKind.Null ? (decimal?)null : arrLater[j].GetDecimal();
                laterMap[timestampsLater[j]] = v;
            }

            for (int i = 0; i < timestampsEarlier.Count; i++)
            {
                string ts = timestampsEarlier[i];
                Assert.True(laterMap.ContainsKey(ts), $"Later results must contain timestamp {ts}");

                var vEarlier = arrEarlier[i].ValueKind == JsonValueKind.Null ? (decimal?)null : arrEarlier[i].GetDecimal();
                var vLater = laterMap[ts];

                Assert.Equal(vEarlier, vLater);
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 3. Candle Patterns Causality (sa_detect_candle_patterns)
    // ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetectCandlePatterns_AsOfInvariance_YieldsIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","endDate":"{{CleanAsOf:yyyy-MM-dd}}"}""";

        string truthJson = await _truth.CallAsync("sa_detect_candle_patterns", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_detect_candle_patterns", args);

        Assert.Equal(truthJson, alteredJson);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 4. Chart Patterns Causality (sa_detect_chart_patterns)
    // ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetectChartPatterns_AsOfInvariance_AllFamiliesYieldIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","endDate":"{{CleanAsOf:yyyy-MM-dd}}"}""";

        string truthJson = await _truth.CallAsync("sa_detect_chart_patterns", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_detect_chart_patterns", args);

        Assert.Equal(truthJson, alteredJson);

        using var doc = JsonDocument.Parse(truthJson);
        if (doc.RootElement.TryGetProperty("patterns", out var patterns))
        {
            foreach (var p in patterns.EnumerateArray())
            {
                if (p.TryGetProperty("confirmedAtDate", out var confDate))
                {
                    Assert.True(DateOnly.Parse(confDate.GetString()!) <= CleanAsOf);
                }
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 5. Divergence Causality (sa_detect_divergences & DivergenceCrossDetector)
    // ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetectDivergences_AsOfInvariance_YieldsIdenticalResultsRegardlessOfFutureBars()
    {
        string args = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","indicator":"RSI","pivotOrder":5,"endDate":"{{CleanAsOf:yyyy-MM-dd}}"}""";

        string truthJson = await _truth.CallAsync("sa_detect_divergences", args);
        string alteredJson = await _alteredAfterClean.CallAsync("sa_detect_divergences", args);

        Assert.Equal(truthJson, alteredJson);
    }

    [Fact]
    public void DivergenceCrossDetector_CausalIndicatorMatching_GuaranteesCausality()
    {
        var highs = new decimal[] { 100, 102, 105, 103, 101, 104, 108, 106, 102, 100 };
        var lows = new decimal[] { 90, 92, 95, 93, 91, 94, 98, 96, 92, 90 };
        var indicator = new decimal?[] { 50m, 55m, 65m, 60m, 52m, 58m, 62m, 57m, 51m, 48m };

        // Test with causalIndicatorMatching: true
        var causalSignals = DivergenceCrossDetector.DetectDivergences(
            highs, lows, indicator, pivotOrder: 2, causalIndicatorMatching: true);

        foreach (var signal in causalSignals)
        {
            Assert.True(signal.IndicatorEndIndex <= signal.PriceEndIndex,
                $"Causal matching requires indicatorEndIndex ({signal.IndicatorEndIndex}) <= priceEndIndex ({signal.PriceEndIndex})");
        }
    }

    [Fact]
    public void DivergenceCrossDetector_FindNearestCausal_RespectsMaxTolerance()
    {
        var pivots = new List<(int Index, decimal Value)>
        {
            (10, 100m),
            (30, 110m),
            (50, 120m)
        };

        // targetIndex = 55
        // Without maxTolerance (or 0): matches pivot at 50 (dist = 5)
        var p1 = DivergenceCrossDetector.FindNearestCausal(pivots, 55, maxTolerance: 0);
        Assert.NotNull(p1);
        Assert.Equal(50, p1.Value.Index);

        // targetIndex = 55, with maxTolerance = 4:
        // Pivot at 50 is distance 5 > 4, so should be skipped and return null
        var p2 = DivergenceCrossDetector.FindNearestCausal(pivots, 55, maxTolerance: 4);
        Assert.Null(p2);

        // targetIndex = 55, with maxTolerance = 10:
        // Pivot at 50 is distance 5 <= 10, so matched
        var p3 = DivergenceCrossDetector.FindNearestCausal(pivots, 55, maxTolerance: 10);
        Assert.NotNull(p3);
        Assert.Equal(50, p3.Value.Index);

        // targetIndex = 55, if pivot at 50 didn't exist:
        var pivotsOlderOnly = new List<(int Index, decimal Value)>
        {
            (10, 100m),
            (30, 110m)
        };
        // Pivot at 30 is distance 25 > 20 -> null
        var p4 = DivergenceCrossDetector.FindNearestCausal(pivotsOlderOnly, 55, maxTolerance: 20);
        Assert.Null(p4);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Test Infrastructure
    // ----------------------------------------------------------------------------------------------------------------

    private sealed class World : IDisposable
    {
        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;
        private readonly ServiceProvider _services;

        public World(IReadOnlyList<decimal> closes)
        {
            _fixture = ReadOnlyDataTreeFixture.Create(closes.Count, Symbol);
            File.Delete(Path.Combine(_fixture.DailyDirectory, $"{Symbol}.parquet"));
            StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, closes);

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
                ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
                ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
                ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
                ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
            }).Build();
            _runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
            _runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
            _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
            Gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        }

        public IReadOnlyStockAnalyzerGateway Gateway { get; }

        public async Task<string> CallAsync(string tool, string argumentsJson)
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(_runtime.Dispatcher, tool, argumentsJson))!;
            Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
            return McpTestSupport.Structured(response).GetRawText();
        }

        public void Dispose()
        {
            _runtime.Dispose();
            _services.Dispose();
            _fixture.Dispose();
        }
    }
}
