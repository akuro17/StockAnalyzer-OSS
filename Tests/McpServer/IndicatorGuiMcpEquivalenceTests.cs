using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// T3 (B1): for every registered indicator that does not compare two tickers, <c>sa_calculate_indicators</c> must give the values the chart's path gives
/// for the indicator's default settings, compared exactly (tolerance 0, same count, same order). The chart side is built from the services the chart
/// uses, with explicit values: the default settings of <see cref="ScreenerCatalogProvider"/>, the bars of <see cref="TimeFrameManager"/> (count = the
/// bars the tool reports it calculated over), and <see cref="IAnalysisPipelineService.CalculateIndicatorsAsync"/>. The types come from the registry,
/// so a new indicator is compared without touching this file. Two-ticker indicators are compared in their own case (<c>Correlation</c> against a
/// second stored symbol, aligned as the chart aligns it). Not covered: the chart view model's own property wiring (no UI is created here).
/// A calculation that cannot give a value (not enough bars, a degenerate input) must fail on both sides; a failure on one side only is a difference.
/// </summary>
public sealed class IndicatorGuiMcpEquivalenceTests : IClassFixture<IndicatorGuiMcpEquivalenceTests.Host>
{
    private const int Tail = 5;
    private const string Primary = "MSFT";
    private const string Secondary = "AAPL";
    private const string Id = "x";

    private readonly Host _host;

    public IndicatorGuiMcpEquivalenceTests(Host host)
    {
        _host = host;
    }

    public static IEnumerable<object[]> SingleTickerTypes()
        => RegisteredTypes().Where(type => !IsCrossTicker(type)).Select(type => new object[] { type.ToString() });

    [Theory]
    [MemberData(nameof(SingleTickerTypes))]
    public async Task EveryIndicatorGivesTheValuesOfTheChartPath(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);

        Outcome mcp = await _host.McpAsync(type, parameters: null);
        Outcome chart = await _host.ChartAsync(type, secondarySymbol: null);

        AssertSame(mcp, chart, typeName);
    }

    [Fact]
    public async Task ATwoTickerIndicatorGivesTheValuesOfTheChartPathForTheSameComparisonSymbol()
    {
        Outcome mcp = await _host.McpAsync(IndicatorType.Correlation, parameters: $"{{\"ComparisonSymbol\":\"{Secondary}\"}}");
        Outcome chart = await _host.ChartAsync(IndicatorType.Correlation, Secondary);

        Assert.True(chart.Successful, "the Correlation case must calculate on the chart path, or it compares nothing");
        AssertSame(mcp, chart, nameof(IndicatorType.Correlation));
    }

    [Fact]
    public void EveryRegisteredTypeIsCoveredByExactlyOneCase()
    {
        int compared = SingleTickerTypes().Count();
        int twoTicker = RegisteredTypes().Count(IsCrossTicker);

        Assert.Equal(RegisteredTypes().Count(), compared + twoTicker);
        Assert.True(compared > 0 && twoTicker > 0);
        Assert.Contains(RegisteredTypes(), type => type == IndicatorType.Correlation && IsCrossTicker(type));
    }

    [Fact]
    public async Task TheComparisonIsNotMostlyBothFailMostIndicatorsCalculateOnTheTestData()
    {
        int calculated = 0;
        int total = 0;
        foreach (object[] row in SingleTickerTypes())
        {
            total++;
            if ((await _host.McpAsync(Enum.Parse<IndicatorType>((string)row[0]), parameters: null)).Successful)
            {
                calculated++;
            }
        }

        // If the test data made most indicators fail on both sides, "both failed" would hide differences.
        Assert.True(calculated * 4 >= total * 3, $"only {calculated} of {total} indicators calculated on the test data");
    }

    [Theory]
    [InlineData(nameof(IndicatorType.SMA))]
    [InlineData(nameof(IndicatorType.RSI))]
    [InlineData(nameof(IndicatorType.MACD))]
    public async Task TheComparisonIsNotVacuousCommonIndicatorsCalculateAndCarryValues(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);

        Outcome mcp = await _host.McpAsync(type, parameters: null);
        Outcome chart = await _host.ChartAsync(type, secondarySymbol: null);

        Assert.True(mcp.Successful && chart.Successful, $"{typeName} must calculate on both sides");
        Assert.All(mcp.Series, pair => Assert.Equal(Tail, pair.Value.Count));
        Assert.Contains(mcp.Series.Values, values => values.Any(v => v is not null));
        AssertSame(mcp, chart, typeName);
    }

    // ---- comparison ----------------------------------------------------------------------------------------------------------------

    private static void AssertSame(Outcome mcp, Outcome chart, string typeName)
    {
        Assert.True(mcp.Successful == chart.Successful, $"{typeName}: the tool {(mcp.Successful ? "calculated" : "failed")} but the chart path {(chart.Successful ? "calculated" : "failed")}");
        if (!mcp.Successful)
        {
            return; // both cannot give a value: the same kind of failure
        }

        Assert.Equal(chart.Series.Keys.OrderBy(n => n, StringComparer.Ordinal), mcp.Series.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach ((string name, IReadOnlyList<decimal?> expected) in chart.Series)
        {
            Assert.True(expected.SequenceEqual(mcp.Series[name]), $"{typeName}.{name}: chart [{string.Join(", ", expected)}] tool [{string.Join(", ", mcp.Series[name])}]");
        }
    }

    private static IEnumerable<IndicatorType> RegisteredTypes()
    {
        IIndicatorFactory factory = IndicatorFactory.Default;
        return Enum.GetValues<IndicatorType>().Distinct().Where(factory.IsRegistered).OrderBy(type => type.ToString(), StringComparer.Ordinal).ToList();
    }

    private static bool IsCrossTicker(IndicatorType type) => IndicatorFactory.Default.Create(type) is ICrossTickerIndicator;

    /// <summary>What one side reports: whether it gave values, and the last <see cref="Tail"/> values of every output series.</summary>
    public sealed record Outcome(bool Successful, IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Series);

    // ---- hosting (one data tree, one runtime, one set of chart services for all cases) ---------------------------------------------------

    public sealed class Host : IDisposable
    {
        private const int Days = 700;

        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;
        private readonly ServiceProvider _services;

        public Host()
        {
            _fixture = ReadOnlyDataTreeFixture.Create(Days, Primary, Secondary);
            Reseed(Primary, seed: 11);
            Reseed(Secondary, seed: 23);
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
        }

        public void Dispose()
        {
            _runtime.Dispose();
            _services.Dispose();
            _fixture.Dispose();
        }

        // A random walk instead of the fixture's zigzag: most indicators then give distinct, non-degenerate values.
        private void Reseed(string symbol, int seed)
        {
            string file = Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet");
            File.Delete(file);
            var random = new Random(seed);
            var closes = new List<decimal>(Days);
            decimal close = 100m;
            for (int i = 0; i < Days; i++)
            {
                close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
                closes.Add(Math.Round(close, 4));
            }

            StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, closes);
        }

        /// <summary>The tool's side: one indicator with default (or the given) parameters, the last <see cref="Tail"/> bars.</summary>
        public async Task<Outcome> McpAsync(IndicatorType type, string? parameters)
        {
            string indicator = $"{{\"id\":\"{Id}\",\"type\":\"{type}\"" + (parameters is null ? string.Empty : $",\"parameters\":{parameters}") + "}";
            using JsonDocument response = (await McpTestSupport.CallAsync(
                _runtime.Dispatcher,
                "sa_calculate_indicators",
                $"{{\"symbol\":\"{Primary}\",\"timeFrame\":\"D1\",\"indicators\":[{indicator}],\"outputTailCount\":{Tail}}}"))!;
            Assert.False(McpTestSupport.IsError(response), $"{type}: " + McpTestSupport.Structured(response));
            JsonElement result = McpTestSupport.Structured(response).GetProperty("results").GetProperty(Id);
            bool successful = result.GetProperty("isSuccessful").GetBoolean();
            var series = new Dictionary<string, IReadOnlyList<decimal?>>(StringComparer.Ordinal);
            if (successful)
            {
                foreach (JsonProperty property in result.GetProperty("series").EnumerateObject())
                {
                    series[property.Name] = property.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (decimal?)null : v.GetDecimal()).ToList();
                }
            }

            return new Outcome(successful, series);
        }

        /// <summary>The chart's side: the bars the tool calculated over, the default settings, the pipeline, the comparison symbol aligned as the chart aligns it.</summary>
        public async Task<Outcome> ChartAsync(IndicatorType type, string? secondarySymbol)
        {
            int barCount = await CalculatedBarCountAsync();
            IIndicatorFactory factory = _services.GetRequiredService<IIndicatorFactory>();
            IDataService dataService = _services.GetRequiredService<IDataService>();
            IAnalysisPipelineService pipeline = _services.GetRequiredService<IAnalysisPipelineService>();

            IReadOnlyList<CandleData> bars = await new TimeFrameManager(dataService).GetCandlesAsync(Primary, TimeFrame.D1, barCount);
            List<CoreCandleData> candles = bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();

            CoreIndicatorSettings settings = new ScreenerCatalogProvider().GetDefaultSettings(type, factory)!;
            settings.IsEnabled = true; // what adding an indicator to the chart does
            IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondary = null;
            if (secondarySymbol is not null)
            {
                ((ICrossTickerParameter)settings.ParameterObject!).ComparisonSymbol = secondarySymbol;
                ComparisonAlignedData aligned = await new ComparisonDataAligner(dataService).AlignAsync(Primary, new[] { secondarySymbol }, TimeFrame.D1, candles.Count);
                var map = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, CandleData?[] series) in aligned.Series)
                {
                    map[name] = series.Select(c => c is { } bar ? new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume) : null).ToList();
                }

                secondary = map;
            }

            Dictionary<string, IIndicatorResult> results = await pipeline.CalculateIndicatorsAsync(candles, new[] { settings }, secondary);
            results.TryGetValue(settings.Id, out IIndicatorResult? result);

            var series2 = new Dictionary<string, IReadOnlyList<decimal?>>(StringComparer.Ordinal);
            if (result is { IsSuccessful: true })
            {
                foreach (string name in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
                {
                    if (IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, settings.PriceSource, out IReadOnlyList<decimal?> values))
                    {
                        series2[name] = Enumerable.Range(candles.Count - Math.Min(Tail, candles.Count), Math.Min(Tail, candles.Count))
                            .Select(i => i < values.Count ? values[i] : null).ToList();
                    }
                }
            }

            // Like the tool: a calculation that gave no value anywhere is a failure, not a success with empty series.
            bool anyValue = result is { IsSuccessful: true } && series2.Count > 0 && AnyValueInWholeSeries(result, type, factory, settings.PriceSource);
            return new Outcome(anyValue, anyValue ? series2 : new Dictionary<string, IReadOnlyList<decimal?>>());
        }

        private static bool AnyValueInWholeSeries(IIndicatorResult result, IndicatorType type, IIndicatorFactory factory, PriceType priceSource)
            => IndicatorOutputSeriesResolver.GetDisplayNames(type, factory).Any(name =>
                IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, priceSource, out IReadOnlyList<decimal?> values) && values.Any(v => v is not null));

        // How many bars the tool calculated over (it reports it); the chart side loads exactly as many.
        private async Task<int> CalculatedBarCountAsync()
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(
                _runtime.Dispatcher,
                "sa_calculate_indicators",
                $"{{\"symbol\":\"{Primary}\",\"timeFrame\":\"D1\",\"indicators\":[{{\"id\":\"{Id}\",\"type\":\"SMA\"}}],\"outputTailCount\":1}}"))!;
            return McpTestSupport.Structured(response).GetProperty("calculatedBarCount").GetInt32();
        }
    }
}
