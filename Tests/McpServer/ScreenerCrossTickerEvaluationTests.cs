using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// T7 (MP-D) at the Core level: a Screener side that names a two-ticker indicator reads the comparison symbol's bars from the set the scan passes
/// (<see cref="IScreenerSecondaryCandles"/>). For every registered two-ticker indicator the Screener's value is compared exactly with the chart's path
/// (<see cref="IAnalysisPipelineService"/> with the same bars): the types the Screener may evaluate at all (<see cref="BacktestIndicatorEligibility"/>)
/// must agree, and the types it refuses must stay refused. Without the set (every caller that does not pass it) a two-ticker side has no value.
/// The primary and the comparison symbol follow seeded random walks, so the compared values are not degenerate.
/// </summary>
public sealed class ScreenerCrossTickerEvaluationTests : IClassFixture<ScreenerCrossTickerEvaluationTests.Host>
{
    private const string Primary = "AAA";
    private const string Comparison = "BBB";
    private const int Days = 400;
    private const int PastWindowStart = 300;

    private readonly Host _host;

    public ScreenerCrossTickerEvaluationTests(Host host)
    {
        _host = host;
    }

    public static IEnumerable<object[]> TwoTickerTypes()
        => Enum.GetValues<IndicatorType>().Distinct().Where(type => IndicatorFactory.Default.IsRegistered(type) && IndicatorFactory.Default.Create(type) is ICrossTickerIndicator)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal).Select(type => new object[] { type.ToString() });

    // ---- AT-D.1: the Screener's value is the chart path's value ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TwoTickerTypes))]
    public async Task EveryTwoTickerIndicatorGivesTheChartPathsValueOrStaysRefused(string typeName)
    {
        var type = Enum.Parse<IndicatorType>(typeName);
        IReadOnlyList<CandleData> primary = await _host.BarsAsync(Primary);
        IScreenerSecondaryCandles secondary = await _host.SecondaryAsync(Comparison);
        CoreIndicatorSettings settings = _host.Settings(type, Comparison);

        string[] seriesNames = IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default).ToArray();
        Assert.NotEmpty(seriesNames);

        if (BacktestIndicatorEligibility.IsTypeBlocked(type))
        {
            // The one eligibility rule of the Screener and the Backtest is not widened by this feature.
            foreach (string series in seriesNames)
            {
                Assert.Null(ScreenerValueExtractor.Default.ExtractValueNullable(Side(type, settings, series, offset: 0), primary, default, secondary));
            }

            return;
        }

        IIndicatorResult? chart = await _host.ChartResultAsync(primary, settings, Comparison);
        Assert.NotNull(chart);
        int compared = 0;
        foreach (string series in seriesNames.Where(name => BacktestIndicatorEligibility.IsAllowed(type, name)))
        {
            Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(chart!, type, series, settings.PriceSource, out IReadOnlyList<decimal?> values), $"{type}.{series}");
            foreach (int offset in new[] { 0, 3 })
            {
                decimal? expected = primary.Count - 1 - offset < values.Count ? values[primary.Count - 1 - offset] : null;
                decimal? actual = ScreenerValueExtractor.Default.ExtractValueNullable(Side(type, settings, series, offset), primary, default, secondary);
                Assert.True(expected == actual, $"{type}.{series}[-{offset}]: chart {expected?.ToString() ?? "null"}, screener {actual?.ToString() ?? "null"}");
                if (expected.HasValue)
                {
                    compared++;
                }
            }
        }

        Assert.True(compared > 0, $"{type} gave no value on either path: the comparison proved nothing");
    }

    [Fact]
    public void TheTypesTheScreenerEvaluatesAndTheTypesItRefusesAreBothPresent()
    {
        var types = TwoTickerTypes().Select(row => Enum.Parse<IndicatorType>((string)row[0])).ToArray();

        Assert.Contains(IndicatorType.Correlation, types);
        Assert.Contains(types, type => !BacktestIndicatorEligibility.IsTypeBlocked(type));
        Assert.Contains(types, BacktestIndicatorEligibility.IsTypeBlocked);
    }

    // ---- AT-D.2: no set, no change ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheSetATwoTickerSideHasNoValueAndTheSetOfAnEarlierCallDoesNotLeak()
    {
        IReadOnlyList<CandleData> primary = await _host.BarsAsync(Primary);
        IScreenerSecondaryCandles secondary = await _host.SecondaryAsync(Comparison);
        ScreenerIndicatorSideConfig side = Side(IndicatorType.Correlation, _host.Settings(IndicatorType.Correlation, Comparison), IndicatorResult.MainSeriesName, offset: 0);

        // The extractor keeps one indicator instance per thread: the call with the set is followed, on this thread, by calls without it.
        Assert.NotNull(ScreenerValueExtractor.Default.ExtractValueNullable(side, primary, default, secondary));
        Assert.Null(ScreenerValueExtractor.Default.ExtractValueNullable(side, primary, default));
        Assert.Null(ScreenerValueExtractor.Default.ExtractValueNullable(side, primary, default, (IScreenerSecondaryCandles?)null));
        Assert.NotNull(ScreenerValueExtractor.Default.ExtractValueNullable(side, primary, default, secondary));

        var entry = new ScreenerIndicatorEntry { LeftHand = side, Operator = ComparisonOperator.GreaterThan, RightNumericValue = -2m, IsEnabled = true };
        Assert.False(entry.IsMet(primary, default));
        Assert.True(entry.IsMet(primary, default, secondary));
    }

    // ---- AT-D.4: nothing after the evaluated symbol's last bar is read ----------------------------------------------------------------

    [Fact]
    public async Task TheComparisonSymbolsBarsAfterTheEvaluatedSymbolsLastBarDoNotChangeTheValue()
    {
        IReadOnlyList<CandleData> full = await _host.BarsAsync(Primary);
        IReadOnlyList<CandleData> past = full.Take(PastWindowStart).ToList();
        IReadOnlyList<CoreCandleData?> comparisonBars = ((ScreenerSecondaryCandles)await _host.SecondaryAsync(Comparison)).Get(Comparison)!;
        ScreenerIndicatorSideConfig side = Side(IndicatorType.Correlation, _host.Settings(IndicatorType.Correlation, Comparison), IndicatorResult.MainSeriesName, offset: 0);

        decimal? baseline = Value(side, past, comparisonBars.Take(PastWindowStart));
        decimal? withFuture = Value(side, past, comparisonBars);
        decimal? wildFuture = Value(side, past, comparisonBars.Take(PastWindowStart).Concat(comparisonBars.Skip(PastWindowStart).Select(Wild)));
        decimal? changedInside = Value(side, past, comparisonBars.Take(PastWindowStart - 5).Concat(new[] { Wild(comparisonBars[PastWindowStart - 5]) }).Concat(comparisonBars.Skip(PastWindowStart - 4).Take(4)));

        Assert.NotNull(baseline);
        Assert.Equal(baseline, withFuture);
        Assert.Equal(baseline, wildFuture);
        Assert.NotEqual(baseline, changedInside); // the same edit inside the window is seen, so the two checks above are not vacuous
    }

    private static CoreCandleData? Wild(CoreCandleData? bar)
        => bar is { } b ? new CoreCandleData(b.Timestamp, b.Open * 7m, b.High * 7m, b.Low * 7m, b.Close * 7m + 31m, b.Volume) : null;

    private static decimal? Value(ScreenerIndicatorSideConfig side, IReadOnlyList<CandleData> primary, IEnumerable<CoreCandleData?> comparison)
        => ScreenerValueExtractor.Default.ExtractValueNullable(
            side, primary, default, new ScreenerSecondaryCandles(new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase) { [Comparison] = comparison.ToList() }));

    // ---- AT-D.5: the conditions that do not read secondary data are untouched ---------------------------------------------------------

    [Fact]
    public void OnlyTheEntryAndTheTreeOverrideTheSecondaryFormOfIsMetAsync()
    {
        var overriding = new List<string>();
        foreach (Type type in typeof(IScreeningCondition).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(IScreeningCondition).IsAssignableFrom(t)))
        {
            InterfaceMapping map = type.GetInterfaceMap(typeof(IScreeningCondition));
            for (int index = 0; index < map.InterfaceMethods.Length; index++)
            {
                MethodInfo method = map.InterfaceMethods[index];
                if (method.Name == nameof(IScreeningCondition.IsMetAsync) && method.GetParameters().Length == 3
                    && map.TargetMethods[index] is { } target && target.DeclaringType == type)
                {
                    overriding.Add(type.Name);
                }
            }
        }

        Assert.Equal(new[] { nameof(ScreenerConditionTreeCondition), nameof(ScreenerIndicatorEntry) }, overriding.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AConditionThatDoesNotOverrideTheSecondaryFormRunsItsOwnLogicWhenTheSetIsPassed()
    {
        IReadOnlyList<CandleData> primary = await _host.BarsAsync(Primary);
        IScreenerSecondaryCandles secondary = await _host.SecondaryAsync(Comparison);
        IScreeningCondition condition = new CountingCondition();

        Assert.True(await condition.IsMetAsync(primary, default, secondary));
        Assert.Equal(1, ((CountingCondition)condition).MetadataFormCalls);
    }

    private sealed class CountingCondition : IScreeningCondition
    {
        public int MetadataFormCalls { get; private set; }

        public bool IsMet(IReadOnlyList<CandleData> candles) => true;

        public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, TickerMetadata metadata)
        {
            MetadataFormCalls++;
            return ValueTask.FromResult(true);
        }
    }

    // ---- the comparison symbols a condition names -------------------------------------------------------------------------------------

    [Fact]
    public void TheComparisonSymbolOfASideIsItsComparisonSymbolParameterTrimmedAndNothingElse()
    {
        ScreenerIndicatorSideConfig WithParameter(object? value) => new()
        {
            IndicatorType = IndicatorType.Correlation,
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["comparisonsymbol"] = value! },
        };

        Assert.Equal("CCC", ScreenerComparisonSymbols.Of(WithParameter(" CCC ")));
        Assert.Equal("DDD", ScreenerComparisonSymbols.Of(WithParameter(System.Text.Json.JsonDocument.Parse("\"DDD\"").RootElement)));
        Assert.Null(ScreenerComparisonSymbols.Of(WithParameter("  ")));
        Assert.Null(ScreenerComparisonSymbols.Of(WithParameter(System.Text.Json.JsonDocument.Parse("5").RootElement)));
        Assert.Null(ScreenerComparisonSymbols.Of(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { ["Period"] = 5 } }));
        Assert.Null(ScreenerComparisonSymbols.Of(new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close" }));
        Assert.Null(ScreenerComparisonSymbols.Of(null));
    }

    // ---- hosting ---------------------------------------------------------------------------------------------------------------------

    private static ScreenerIndicatorSideConfig Side(IndicatorType type, CoreIndicatorSettings settings, string series, int offset)
        => new()
        {
            IndicatorType = type,
            Parameters = IndicatorSideResolver.ParameterValues(settings.ParameterObject),
            OutputName = series,
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };

    public sealed class Host : IDisposable
    {
        private readonly ReadOnlyDataTreeFixture _fixture;
        private readonly ServiceProvider _services;

        public Host()
        {
            _fixture = ReadOnlyDataTreeFixture.Create(Days, Primary, Comparison);
            Reseed(Primary, seed: 41);
            Reseed(Comparison, seed: 59);
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
                ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
                ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
                ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
                ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
            }).Build();
            _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
        }

        public void Dispose()
        {
            _services.Dispose();
            _fixture.Dispose();
        }

        private void Reseed(string symbol, int seed)
        {
            File.Delete(Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet"));
            var random = new Random(seed);
            var closes = new List<decimal>(Days);
            decimal close = 100m;
            for (int day = 0; day < Days; day++)
            {
                close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
                closes.Add(Math.Round(close, 4));
            }

            ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, closes);
        }

        public Task<IReadOnlyList<CandleData>> BarsAsync(string symbol)
            => _services.GetRequiredService<IDataService>().LoadCandlesAsync(symbol, TimeFrame.D1, Days);

        public async Task<IScreenerSecondaryCandles> SecondaryAsync(string symbol)
            => await ScreenerSecondaryCandles.LoadAsync(_services.GetRequiredService<IDataService>(), new[] { symbol }, TimeFrame.D1, Days, CancellationToken.None);

        /// <summary>The default settings of <paramref name="type"/> as the chart adds it, naming <paramref name="comparison"/> as the second ticker.</summary>
        public CoreIndicatorSettings Settings(IndicatorType type, string comparison)
        {
            CoreIndicatorSettings settings = new ScreenerCatalogProvider().GetDefaultSettings(type, _services.GetRequiredService<IIndicatorFactory>())!;
            settings.IsEnabled = true;
            ((ICrossTickerParameter)settings.ParameterObject!).ComparisonSymbol = comparison;
            return settings;
        }

        /// <summary>The chart's calculation: the pipeline over the same bars, with the comparison symbol's bars as the secondary series.</summary>
        public async Task<IIndicatorResult?> ChartResultAsync(IReadOnlyList<CandleData> primary, CoreIndicatorSettings settings, string comparison)
        {
            List<CoreCandleData> candles = primary.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();
            IReadOnlyList<CoreCandleData?> bars = ((ScreenerSecondaryCandles)await SecondaryAsync(comparison)).Get(comparison)!;
            var secondary = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase) { [comparison] = bars };
            Dictionary<string, IIndicatorResult> results = await _services.GetRequiredService<IAnalysisPipelineService>().CalculateIndicatorsAsync(candles, new[] { settings }, secondary);
            results.TryGetValue(settings.Id, out IIndicatorResult? result);
            return result is { IsSuccessful: true } ? result : null;
        }
    }
}
