using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// T5 (proposal C): nothing after an as-of date may change what is available AT that date. Three data trees hold the same random-walk daily series;
/// two of them are changed after a chosen day (wild prices instead of the walk). Compared on every level between the stored days and the answer:
/// the bars read up to the date (D1, W1, MN1; also through the loader the prediction tool uses), the answer of <c>sa_get_candles</c> with an
/// <c>endDate</c>, and the values of every registered indicator that claims to be causal, calculated over the whole series and over the series cut
/// at the date (the values at and before the date must be the same). A control proves the check can fail: an indicator declared non-causal does
/// change when the days after the date are added.
/// </summary>
public sealed class AsOfInvarianceTests : IDisposable
{
    private const string Symbol = "ASOF";
    private const int Days = 700;
    private const int WildDays = 90;

    private static readonly DateTime FirstDay = new(2024, 1, 1);

    /// <summary>2025-08-31: a Sunday and the last day of a month, so no weekly or monthly bar straddles it.</summary>
    private static readonly DateOnly CleanAsOf = new(2025, 8, 31);

    /// <summary>2025-08-27: a Wednesday, in the middle of a week and of a month.</summary>
    private static readonly DateOnly MidPeriodAsOf = new(2025, 8, 27);

    private readonly World _truth;
    private readonly World _alteredAfterClean;
    private readonly World _alteredAfterMid;

    public AsOfInvarianceTests()
    {
        decimal[] walk = RandomWalk();
        _truth = new World(walk);
        _alteredAfterClean = new World(Altered(walk, CleanAsOf));
        _alteredAfterMid = new World(Altered(walk, MidPeriodAsOf));
    }

    public void Dispose()
    {
        _truth.Dispose();
        _alteredAfterClean.Dispose();
        _alteredAfterMid.Dispose();
    }

    private static decimal[] RandomWalk()
    {
        var random = new Random(5);
        var closes = new decimal[Days];
        decimal close = 100m;
        for (int i = 0; i < Days; i++)
        {
            close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
            closes[i] = Math.Round(close, 4);
        }

        return closes;
    }

    /// <summary>The same walk up to and including <paramref name="lastKeptDay"/>; wild prices (about 10 to 40 times higher, then a crash) on the days after it.</summary>
    private static decimal[] Altered(decimal[] walk, DateOnly lastKeptDay)
    {
        int firstChanged = lastKeptDay.DayNumber - DateOnly.FromDateTime(FirstDay).DayNumber + 1;
        var closes = (decimal[])walk.Clone();
        for (int i = firstChanged; i < Math.Min(Days, firstChanged + WildDays); i++)
        {
            closes[i] = i % 5 == 0 ? 6m : 1000m + i % 7 * 300m;
        }

        return closes;
    }

    private static CoreCandleData ToCore(CandleData bar) => new(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

    // ---- the bars read up to the date ------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> FramesAndDates()
        => new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }.SelectMany(frame => new[] { "2025-08-31", "2025-08-27" }.Select(date => new object[] { frame, date }));

    private World AlteredFor(DateOnly date) => date == CleanAsOf ? _alteredAfterClean : _alteredAfterMid;

    [Theory]
    [MemberData(nameof(FramesAndDates))]
    public async Task TheBarsReadUpToADateDoNotDependOnWhatFollowsIt(TimeFrame frame, string date)
    {
        DateOnly asOf = DateOnly.Parse(date);

        var truth = await _truth.Gateway.GetCandlesUpToAsync(Symbol, frame, asOf, 400);
        var altered = await AlteredFor(asOf).Gateway.GetCandlesUpToAsync(Symbol, frame, asOf, 400);

        Assert.True(truth.Count > 10);
        Assert.Equal(truth, altered);
        Assert.All(truth, bar => Assert.True(DateOnly.FromDateTime(bar.Timestamp) <= asOf));
    }

    [Theory]
    [MemberData(nameof(FramesAndDates))]
    public async Task TheLoaderOfThePredictionToolGivesTheSameBarsWhateverFollowsTheDate(TimeFrame frame, string date)
    {
        DateOnly asOf = DateOnly.Parse(date);

        var truth = await new CandleWindowLoader(_truth.Gateway).LoadUpToAsync(Symbol, frame, asOf, 300, CancellationToken.None);
        var altered = await new CandleWindowLoader(AlteredFor(asOf).Gateway).LoadUpToAsync(Symbol, frame, asOf, 300, CancellationToken.None);

        Assert.Equal(truth, altered);
    }

    [Theory]
    [MemberData(nameof(FramesAndDates))]
    public async Task SaGetCandlesWithAnEndDateAnswersTheSameWhateverFollowsTheDate(TimeFrame frame, string date)
    {
        string arguments = $"{{\"symbol\":\"{Symbol}\",\"timeFrame\":\"{frame}\",\"endDate\":\"{date}\",\"count\":50}}";
        DateOnly asOf = DateOnly.Parse(date);

        string truth = await _truth.CallAsync("sa_get_candles", arguments);
        string altered = await AlteredFor(asOf).CallAsync("sa_get_candles", arguments);

        Assert.Equal(truth, altered); // the whole structured answer: bars, counts, referenceDate, lastBarPartial ...
        Assert.Contains("\"candles\"", truth);
    }

    // ---- indicators ------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<IndicatorType> RegisteredSingleTickerTypes()
    {
        IIndicatorFactory factory = IndicatorFactory.Default;
        return Enum.GetValues<IndicatorType>().Distinct().Where(factory.IsRegistered).Where(type => factory.Create(type) is not ICrossTickerIndicator)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>The series of one indicator (default settings) over the given bars, by output name; null when it could not calculate.</summary>
    private async Task<Dictionary<string, IReadOnlyList<decimal?>>?> SeriesAsync(World world, IndicatorType type, IReadOnlyList<CandleData> bars)
    {
        IIndicatorFactory factory = IndicatorFactory.Default;
        CoreIndicatorSettings settings = new ScreenerCatalogProvider().GetDefaultSettings(type, factory)!;
        settings.IsEnabled = true;

        Dictionary<string, IIndicatorResult> results = await world.Gateway.CalculateIndicatorsAsync(bars.Select(ToCore).ToList(), new[] { settings });
        if (!results.TryGetValue(settings.Id, out IIndicatorResult? result) || !result.IsSuccessful)
        {
            return null;
        }

        var series = new Dictionary<string, IReadOnlyList<decimal?>>(StringComparer.Ordinal);
        foreach (string name in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            if (IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, settings.PriceSource, out IReadOnlyList<decimal?> values))
            {
                series[name] = values;
            }
        }

        return series;
    }

    /// <summary>Output names whose values at or before the cut differ between the calculation over the whole series and over the cut series.</summary>
    private static List<string> Differences(Dictionary<string, IReadOnlyList<decimal?>> whole, Dictionary<string, IReadOnlyList<decimal?>> cut, int cutCount, Func<string, bool> include)
    {
        var differing = new List<string>();
        foreach ((string name, IReadOnlyList<decimal?> cutValues) in cut)
        {
            if (!include(name) || !whole.TryGetValue(name, out IReadOnlyList<decimal?>? wholeValues))
            {
                continue;
            }

            for (int i = 0; i < Math.Min(cutCount, Math.Min(cutValues.Count, wholeValues.Count)); i++)
            {
                if (cutValues[i] != wholeValues[i])
                {
                    differing.Add($"{name}@{i}");
                    break;
                }
            }
        }

        return differing;
    }

    public static IEnumerable<object[]> Frames() => new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }.Select(frame => new object[] { frame });

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task EveryCausalIndicatorHasTheSameValuesAtAndBeforeTheDateWhetherOrNotLaterBarsExist(TimeFrame frame)
    {
        IReadOnlyList<CandleData> cut = await _truth.Gateway.GetCandlesUpToAsync(Symbol, frame, CleanAsOf, 1000);
        IReadOnlyList<CandleData> wholeTruth = await _truth.Gateway.GetCandlesAsync(Symbol, frame, 1000);
        IReadOnlyList<CandleData> wholeAltered = await _alteredAfterClean.Gateway.GetCandlesAsync(Symbol, frame, 1000);
        Assert.True(wholeTruth.Count > cut.Count && wholeAltered.Count > cut.Count, "later bars must exist for the comparison to mean something");

        var leaks = new List<string>();
        int compared = 0;
        int types = 0;
        foreach (IndicatorType type in RegisteredSingleTickerTypes().Where(type => !BacktestIndicatorEligibility.IsTypeBlocked(type)))
        {
            types++;
            Dictionary<string, IReadOnlyList<decimal?>>? cutSeries = await SeriesAsync(_truth, type, cut);
            if (cutSeries is null)
            {
                continue; // not enough bars for this frame, or a degenerate input: nothing to compare (the control below keeps this honest)
            }

            foreach (IReadOnlyList<CandleData> whole in new[] { wholeTruth, wholeAltered })
            {
                Dictionary<string, IReadOnlyList<decimal?>>? wholeSeries = await SeriesAsync(_truth, type, whole);
                Assert.NotNull(wholeSeries);
                List<string> differing = Differences(wholeSeries, cutSeries, cut.Count, name => BacktestIndicatorEligibility.IsAllowed(type, name));
                leaks.AddRange(differing.Select(name => $"{type}.{name}"));
            }

            compared++;
        }

        Assert.Empty(leaks.Distinct());
        // The comparison is not mostly "could not calculate": D1 and W1 calculate most indicators on this data; a monthly series is short.
        Assert.True(frame == TimeFrame.MN1 ? compared > 0 : compared * 2 >= types, $"{frame}: only {compared} of {types} indicators calculated");
    }

    [Fact]
    public async Task TheCheckCanFail_AnIndicatorDeclaredNonCausalChangesWhenLaterBarsAreAdded()
    {
        IReadOnlyList<CandleData> cut = await _truth.Gateway.GetCandlesUpToAsync(Symbol, TimeFrame.D1, CleanAsOf, 1000);
        IReadOnlyList<CandleData> whole = await _truth.Gateway.GetCandlesAsync(Symbol, TimeFrame.D1, 1000);

        var changed = new List<IndicatorType>();
        foreach (IndicatorType type in RegisteredSingleTickerTypes().Where(BacktestIndicatorEligibility.NonCausalIndicatorTypes.Contains))
        {
            var cutSeries = await SeriesAsync(_truth, type, cut);
            var wholeSeries = await SeriesAsync(_truth, type, whole);
            if (cutSeries is not null && wholeSeries is not null && Differences(wholeSeries, cutSeries, cut.Count, _ => true).Count > 0)
            {
                changed.Add(type);
            }
        }

        Assert.NotEmpty(changed);
    }

    // ---- hosting -----------------------------------------------------------------------------------------------------------------------

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

        /// <summary>The structured answer of a tool call, as text.</summary>
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
