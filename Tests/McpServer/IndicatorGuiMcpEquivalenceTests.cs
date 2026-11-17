using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// T3 (B1): for every registered indicator that does not compare two tickers, <c>sa_calculate_indicators</c> must give the values the chart's path gives
/// for the indicator's default settings, compared exactly (tolerance 0, same count, same order). The chart side is built from the services the chart
/// uses, with explicit values (a universe-breadth type also compared, with the built-in All Tickers group, in its own case): the default settings of <see cref="ScreenerCatalogProvider"/>, the bars of <see cref="TimeFrameManager"/> (count = the
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

    /// <summary>
    /// The types calculated from one ticker alone. Two other kinds are compared in their own cases: the two-ticker types (<see cref="ScreenerTwoTickerTypes"/>) and the
    /// universe-breadth types (<see cref="UniverseBreadthTypes"/>), which both need an input the default settings do not carry (a ComparisonSymbol, a UniverseGroupId)
    /// and which the tool refuses without it (INVALID_INDICATOR_PARAMETER). Responsibilities for the universe-breadth types: the refusal is tested in
    /// <c>CalculateIndicatorsToolTests.UniverseBreadthWithoutValidUniverseGroupId_ReturnsInvalidIndicatorParameter</c>, the calculation with an injected universe in
    /// <c>AnalysisPipelineUniverseBreadthTests</c> (Core.Tests), and the equality of the tool and the chart path for a valid group in the case below.
    /// </summary>
    public static IEnumerable<object[]> SingleTickerTypes()
        => RegisteredTypes().Where(type => !IsCrossTicker(type) && !IsUniverseBreadth(type)).Select(type => new object[] { type.ToString() });

    /// <summary>The universe-breadth types, taken from the registry through the one eligibility rule, not from a list of names.</summary>
    public static IEnumerable<object[]> UniverseBreadthTypes()
        => RegisteredTypes().Where(IsUniverseBreadth).Select(type => new object[] { type.ToString() });

    [Theory]
    [MemberData(nameof(SingleTickerTypes))]
    public async Task EveryIndicatorGivesTheValuesOfTheChartPath(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);

        Outcome mcp = await _host.McpAsync(type, parameters: null);
        Outcome chart = await _host.ChartAsync(type, secondarySymbol: null);

        AssertSame(mcp, chart, typeName);
    }

    /// <summary>
    /// The two-ticker types the Screener tab offers: those the one eligibility rule of the Screener lets through (<see cref="ScreenerIndicatorEligibility"/>), taken from the
    /// registry, not from a list of names. That is Correlation, CrossCorrelation and the seven types enabled in G-1 (RollingBeta, RollingAlpha, RollingRSquared,
    /// CointegrationSpread, PairRatio, PairSpread, Ratiocator); a type enabled later joins this theory without editing it. Tolerance 0: a type that cannot be
    /// computed on both paths is reported, never loosened.
    /// </summary>
    public static IEnumerable<object[]> ScreenerTwoTickerTypes()
        => RegisteredTypes().Where(type => IsCrossTicker(type) && !ScreenerIndicatorEligibility.IsTypeBlocked(type)).Select(type => new object[] { type.ToString() });

    [Theory]
    [MemberData(nameof(ScreenerTwoTickerTypes))]
    public async Task ATwoTickerIndicatorGivesTheValuesOfTheChartPathForTheSameComparisonSymbol(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);

        Outcome mcp = await _host.McpAsync(type, parameters: $"{{\"ComparisonSymbol\":\"{Secondary}\"}}");
        Outcome chart = await _host.ChartAsync(type, Secondary);

        Assert.True(chart.Successful, $"the {typeName} case must calculate on the chart path, or it compares nothing");
        AssertSame(mcp, chart, typeName);
    }

    /// <summary>
    /// A universe-breadth type given the built-in All Tickers group (<see cref="TickerGroupIds.AllTickers"/>, which every data tree has, here the fixture's two symbols)
    /// gives the tool and the chart path the same values. Tolerance 0, as in the other cases; a difference is reported, not loosened.
    /// </summary>
    [Theory]
    [MemberData(nameof(UniverseBreadthTypes))]
    public async Task AUniverseBreadthIndicatorGivesTheValuesOfTheChartPathForTheSameGroup(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);
        string groupId = TickerGroupIds.AllTickers.ToString("D");

        Outcome mcp = await _host.McpAsync(type, parameters: $"{{\"UniverseGroupId\":\"{groupId}\"}}");
        Outcome chart = await _host.ChartAsync(type, secondarySymbol: null, universeGroupId: groupId);

        Assert.True(chart.Successful, $"the {typeName} case must calculate on the chart path, or it compares nothing");
        AssertSame(mcp, chart, typeName);
    }

    [Fact]
    public void TheUniverseBreadthCasesCoverTheTwoTypesOfTheRegistry()
    {
        string[] names = UniverseBreadthTypes().Select(row => (string)row[0]).ToArray();

        Assert.Equal(
            new[] { nameof(IndicatorType.MarketBreadth), nameof(IndicatorType.McClellanOscillator) }.OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheScreenerTwoTickerCasesIncludeAllElevenTypesOfTheTab()
    {
        string[] names = ScreenerTwoTickerTypes().Select(row => (string)row[0]).ToArray();

        IndicatorType[] expected =
        {
            IndicatorType.Correlation, IndicatorType.CrossCorrelation, IndicatorType.RollingBeta, IndicatorType.RollingAlpha, IndicatorType.RollingRSquared,
            IndicatorType.CointegrationSpread, IndicatorType.GrangerCausality, IndicatorType.JohansenCointegration, IndicatorType.PairRatio, IndicatorType.PairSpread, IndicatorType.Ratiocator, IndicatorType.CrossTickerDtw,
            IndicatorType.RankCorrelation, IndicatorType.MansfieldRS
        };
        Assert.Equal(expected.Select(type => type.ToString()).OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRegisteredTypeIsCoveredByExactlyOneCase()
    {
        int compared = SingleTickerTypes().Count();
        int twoTicker = RegisteredTypes().Count(IsCrossTicker);
        int universeBreadth = UniverseBreadthTypes().Count();

        Assert.Equal(RegisteredTypes().Count(), compared + twoTicker + universeBreadth);
        Assert.True(compared > 0 && twoTicker > 0 && universeBreadth > 0);
        Assert.Empty(RegisteredTypes().Where(type => IsCrossTicker(type) && IsUniverseBreadth(type))); // the three kinds are disjoint
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

    /// <summary>
    /// U-11: an indicator with its own Weekly / Monthly frame gives the tool (<c>timeFrame</c> W1 / MN1 on the indicator) and the chart path (the Timeframe of the settings, calculated by
    /// the pipeline on the aggregated daily bars) the same values, tolerance 0. Only indicators without a recursive state are compared: a recursive one (EMA, RSI, MACD ...) depends on where the
    /// calculation starts, and the two sides load different amounts of history, so for those the sides converge but do not match exactly (stated in the specification).
    /// </summary>
    [Theory]
    [InlineData(nameof(IndicatorType.SMA), IndicatorTimeframe.Weekly, "W1")]
    [InlineData(nameof(IndicatorType.SMA), IndicatorTimeframe.Monthly, "MN1")]
    [InlineData(nameof(IndicatorType.WMA), IndicatorTimeframe.Weekly, "W1")]
    [InlineData(nameof(IndicatorType.BB), IndicatorTimeframe.Weekly, "W1")]
    [InlineData(nameof(IndicatorType.BB), IndicatorTimeframe.Monthly, "MN1")]
    [InlineData(nameof(IndicatorType.Momentum), IndicatorTimeframe.Weekly, "W1")]
    [InlineData(nameof(IndicatorType.ROC), IndicatorTimeframe.Monthly, "MN1")]
    public async Task ANonRecursiveIndicatorOnAHigherFrame_GivesTheToolAndTheChartPathTheSameValues(string typeName, IndicatorTimeframe timeframe, string toolFrame)
    {
        var type = Enum.Parse<IndicatorType>(typeName);

        Outcome mcp = await _host.McpAsync(type, parameters: null, ownTimeFrame: toolFrame);
        Outcome chart = await _host.ChartAsync(type, secondarySymbol: null, timeframe: timeframe);

        Assert.True(mcp.Successful && chart.Successful, $"{typeName} {toolFrame}: both sides must calculate (tool {mcp.Successful}, chart {chart.Successful})");
        Assert.Contains(mcp.Series.Values, values => values.Any(v => v is not null));
        AssertSame(mcp, chart, $"{typeName}@{toolFrame}");
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

    private static bool IsUniverseBreadth(IndicatorType type) => ScreenerIndicatorEligibility.IsUniverseBreadthType(type);

    /// <summary>What one side reports: whether it gave values, and the last <see cref="Tail"/> values of every output series.</summary>
    public sealed record Outcome(bool Successful, IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Series);

    // ---- hosting (one data tree, one runtime, one set of chart services for all cases) ---------------------------------------------------

    public sealed class Host : IDisposable
    {
        private const int Days = 700;

        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly McpServerRuntime _runtime;
        private readonly StringWriter _serverLog = new();
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
            _runtime = McpServerComposition.Create(configuration, new LogSink(_serverLog));
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
        public async Task<Outcome> McpAsync(IndicatorType type, string? parameters, string? ownTimeFrame = null)
        {
            string indicator = $"{{\"id\":\"{Id}\",\"type\":\"{type}\"" + (parameters is null ? string.Empty : $",\"parameters\":{parameters}")
                + (ownTimeFrame is null ? string.Empty : $",\"timeFrame\":\"{ownTimeFrame}\"") + "}";
            using JsonDocument response = (await McpTestSupport.CallAsync(
                _runtime.Dispatcher,
                "sa_calculate_indicators",
                $"{{\"symbol\":\"{Primary}\",\"timeFrame\":\"D1\",\"indicators\":[{indicator}],\"outputTailCount\":{Tail}}}"))!;
            // An INTERNAL_ERROR carries fixed text; the cause is only in the server log, which a one-off failure in a full run would otherwise lose.
            if (McpTestSupport.IsError(response))
            {
                // Built only on failure: the structured text and the whole log are not worth producing for the other calls.
                Assert.Fail($"{type}: " + McpTestSupport.Structured(response) + Environment.NewLine + "Server log:" + Environment.NewLine + _serverLog);
            }
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
        public async Task<Outcome> ChartAsync(IndicatorType type, string? secondarySymbol, string? universeGroupId = null, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart)
        {
            int barCount = await CalculatedBarCountAsync();
            IIndicatorFactory factory = _services.GetRequiredService<IIndicatorFactory>();
            IDataService dataService = _services.GetRequiredService<IDataService>();
            IAnalysisPipelineService pipeline = _services.GetRequiredService<IAnalysisPipelineService>();

            IReadOnlyList<CandleData> bars = await new TimeFrameManager(dataService).GetCandlesAsync(Primary, TimeFrame.D1, barCount);
            List<CoreCandleData> candles = bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();

            CoreIndicatorSettings settings = new ScreenerCatalogProvider().GetDefaultSettings(type, factory)!;
            settings.IsEnabled = true; // what adding an indicator to the chart does
            settings.Timeframe = timeframe;
            if (universeGroupId is not null)
            {
                ((IUniverseBreadthParameter)settings.ParameterObject!).UniverseGroupId = universeGroupId;
            }

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
