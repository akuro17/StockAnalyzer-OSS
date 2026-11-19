using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T2b (decision D-2b): the optional <c>decimalPlaces</c> of sa_calculate_chart_series, sa_calculate_volume_profile, sa_analyze_portfolio_risk and
/// sa_project_price_path, and the optional <c>includeBins</c> of sa_calculate_volume_profile, through the real composition. Same rule as T2a: with the argument every written
/// non-integer number equals the number of the call without it rounded by the test; counts, volumes, dates and text are untouched; an echo of the caller's own argument is as sent.
/// </summary>
public sealed class DecimalPlacesChartAndRiskToolsTests : IDisposable
{
    private const int Days = 200;
    private const string ChartTool = "sa_calculate_chart_series";
    private const string ProfileTool = "sa_calculate_volume_profile";
    private const string RiskTool = "sa_analyze_portfolio_risk";
    private const string PathTool = "sa_project_price_path";
    private static readonly DateTimeOffset Day1 = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "7203.T");
    private readonly string _portfolioRoot = Path.Combine(Path.GetTempPath(), "sa_decimal_places_risk_" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _database = new();
    private readonly List<McpServerRuntime> _runtimes = new();

    public DecimalPlacesChartAndRiskToolsTests()
    {
        Directory.CreateDirectory(_portfolioRoot);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)) + (i * 0.037m), 4)).ToList());
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "7203-T", Enumerable.Range(0, Days).Select(i => Math.Round(2000m + (decimal)(150 * Math.Cos(i / 8.0)) - (i * 0.9m), 3)).ToList());
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _database.Dispose();
        _fixture.Dispose();
        Directory.Delete(_portfolioRoot, recursive: true);
    }

    private async Task SavePortfolioAsync()
    {
        Transaction deposit = new(Day1, TransactionType.Deposit, null, 0m, 0m, 5_000_000m);
        Transaction longMsft = new(Day1.AddDays(1), TransactionType.Long, "MSFT", 100m, 100m, 0m);
        Transaction shortToyota = new(Day1.AddDays(2), TransactionType.Short, "7203-T", 30m, 2100m, 0m);
        Portfolio portfolio = new PortfolioManager().RebuildPortfolio(0m, new[] { deposit, longMsft, shortToyota });
        using var repository = new UserPortfolioRepository(_portfolioRoot);
        await repository.SavePortfolioAsync(portfolio);
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
        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            portfolioSource: new FileReadOnlyPortfolioSource(Path.Combine(_portfolioRoot, "Portfolios"), _database));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonElement> OkAsync(McpMessageDispatcher dispatcher, string tool, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static string With(string openArguments, string extra) => openArguments + extra + "}";

    private static string Places(string places) => places.Length == 0 ? string.Empty : $",\"decimalPlaces\":{places}";

    // ---- sa_calculate_chart_series ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("HeikinAshi", "2", true)]
    [InlineData("HeikinAshi", "0", true)]
    [InlineData("Renko", "2", true)]
    [InlineData("Renko", "4", true)]
    [InlineData("Kagi", "2", false)]
    [InlineData("ThreeLineBreak", "2", false)]
    [InlineData("PointAndFigure", "2", false)]
    public async Task ChartSeries_DecimalPlacesIsTheCallWithoutItRoundedByTheTest(string chartType, string places, bool mustChange)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string open = $$"""{"symbol":"MSFT","timeFrame":"D1","chartType":"{{chartType}}","outputTailCount":80""";

        JsonElement calculated = await OkAsync(dispatcher, ChartTool, With(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, ChartTool, With(open, Places(places)));

        int changed = DecimalPlacesAnalysisToolsTests.AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: false);
        Assert.True(!mustChange || changed > 0, "the fixture must hold numbers that the rounding changes");
        Assert.NotEqual(0, calculated.GetProperty("elements").GetArrayLength());
    }

    [Fact]
    public async Task ChartSeries_TheVolumeIsNeverRounded()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string open = """{"symbol":"MSFT","timeFrame":"D1","chartType":"HeikinAshi","outputTailCount":40""";

        JsonElement calculated = await OkAsync(dispatcher, ChartTool, With(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, ChartTool, With(open, Places("0")));

        Assert.Equal(
            calculated.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("volume").GetRawText()),
            rounded.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("volume").GetRawText()));
    }

    // ---- sa_calculate_volume_profile --------------------------------------------------------------------------------------------

    private const string ProfileArguments = """{"symbol":"MSFT","timeFrame":"D1","binCount":12,"valueAreaPercent":0.683""";

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("4")]
    public async Task VolumeProfile_DecimalPlacesIsTheCallWithoutItRoundedByTheTest_AndTheValueAreaPercentIsEchoedAsSent(string places)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement calculated = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, Places(places)));

        int changed = DecimalPlacesAnalysisToolsTests.AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: false, echoedAsSent: new HashSet<string> { "$.valueArea.percent" });
        Assert.True(changed > 0);
        Assert.Equal("0.683", rounded.GetProperty("valueArea").GetProperty("percent").GetRawText());
    }

    [Fact]
    public async Task VolumeProfile_IncludeBinsFalseLeavesOutTheBinsArrayAndNothingElse()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement withBins = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, string.Empty));
        JsonElement explicitTrue = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, ",\"includeBins\":true"));
        JsonElement withoutBins = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, ",\"includeBins\":false"));

        Assert.Equal(withBins.GetRawText(), explicitTrue.GetRawText());
        string[] full = withBins.EnumerateObject().Select(p => p.Name).ToArray();
        string[] reduced = withoutBins.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(full.Where(name => name != "bins"), reduced);
        Assert.Contains("bins", full);
        foreach (string name in reduced)
        {
            Assert.Equal(withBins.GetProperty(name).GetRawText(), withoutBins.GetProperty(name).GetRawText());
        }

        Assert.True(withoutBins.GetProperty("binCount").GetInt32() > 0);
        Assert.True(withoutBins.TryGetProperty("poc", out _));
        Assert.True(withoutBins.TryGetProperty("valueArea", out _));
        Assert.Equal(withBins.GetProperty("totalBarVolume").GetRawText(), withoutBins.GetProperty("totalBarVolume").GetRawText());
    }

    [Fact]
    public async Task VolumeProfile_IncludeBinsFalseWithDecimalPlacesStillRoundsThePocAndTheValueArea()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        JsonElement calculated = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, ",\"includeBins\":false"));
        JsonElement rounded = await OkAsync(dispatcher, ProfileTool, With(ProfileArguments, ",\"includeBins\":false" + Places("1")));

        int changed = DecimalPlacesAnalysisToolsTests.AssertRoundedCopy(calculated, rounded, 1, asDouble: false, echoedAsSent: new HashSet<string> { "$.valueArea.percent" });
        Assert.True(changed >= 0);
        Assert.False(rounded.TryGetProperty("bins", out _));
    }

    [Theory]
    [InlineData("\"no\"")]
    [InlineData("0")]
    public async Task VolumeProfile_RefusesANonBooleanIncludeBins(string value)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, ProfileTool, With(ProfileArguments, $",\"includeBins\":{value}")))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("includeBins", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    // ---- sa_analyze_portfolio_risk ----------------------------------------------------------------------------------------------

    private const string RiskArguments = "{\"lookbackBars\":150,\"confidenceLevel\":0.9,\"benchmark\":\"MSFT\",\"baseCurrency\":\"JPY\"";

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("6")]
    public async Task PortfolioRisk_DecimalPlacesIsTheCallWithoutItRoundedByTheTest_AndTheConfidenceLevelIsEchoedAsSent(string places)
    {
        await SavePortfolioAsync();
        McpMessageDispatcher dispatcher = await StartAsync();
        const string open = RiskArguments;

        JsonElement calculated = await OkAsync(dispatcher, RiskTool, With(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, RiskTool, With(open, Places(places)));

        Assert.True(calculated.GetProperty("portfolioFound").GetBoolean());
        Assert.NotEqual(0, calculated.GetProperty("contributions").GetArrayLength());
        int changed = DecimalPlacesAnalysisToolsTests.AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: false, echoedAsSent: new HashSet<string> { "$.confidenceLevel" });
        Assert.True(changed > 0);
        Assert.Equal("0.9", rounded.GetProperty("confidenceLevel").GetRawText());
    }

    // ---- sa_project_price_path --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Arima", "0")]
    [InlineData("Arima", "2")]
    [InlineData("Fft", "3")]
    public async Task ProjectPricePath_DecimalPlacesIsTheCallWithoutItRoundedByTheTest(string method, string places)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string open = $$"""{"symbol":"MSFT","timeFrame":"D1","method":"{{method}}","horizonBars":15""";

        JsonElement calculated = await OkAsync(dispatcher, PathTool, With(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, PathTool, With(open, Places(places)));

        Assert.NotEqual(0, calculated.GetProperty("projectedPath").GetArrayLength());
        int changed = DecimalPlacesAnalysisToolsTests.AssertRoundedCopy(calculated, rounded, int.Parse(places), asDouble: true);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task ProjectPricePath_TheBandsAreRoundedFromTheUnroundedBandsNotFromTheRoundedValue()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string open = """{"symbol":"MSFT","timeFrame":"D1","method":"Arima","horizonBars":10,"confidenceMultiplier":2.0""";

        JsonElement calculated = await OkAsync(dispatcher, PathTool, With(open, string.Empty));
        JsonElement rounded = await OkAsync(dispatcher, PathTool, With(open, Places("1")));

        JsonElement[] before = calculated.GetProperty("projectedPath").EnumerateArray().ToArray();
        JsonElement[] after = rounded.GetProperty("projectedPath").EnumerateArray().ToArray();
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(Math.Round(before[i].GetProperty("lower").GetDouble(), 1, MidpointRounding.AwayFromZero), after[i].GetProperty("lower").GetDouble());
            Assert.Equal(Math.Round(before[i].GetProperty("upper").GetDouble(), 1, MidpointRounding.AwayFromZero), after[i].GetProperty("upper").GetDouble());
        }
    }

    // ---- the four tools: refusals and schema ------------------------------------------------------------------------------------

    private static string OpenOf(string tool) => tool switch
    {
        ChartTool => "{\"symbol\":\"MSFT\",\"timeFrame\":\"D1\",\"chartType\":\"HeikinAshi\"",
        ProfileTool => "{\"symbol\":\"MSFT\",\"timeFrame\":\"D1\"",
        RiskTool => "{\"lookbackBars\":100",
        _ => "{\"symbol\":\"MSFT\",\"timeFrame\":\"D1\",\"method\":\"Arima\"",
    };

    [Theory]
    [InlineData(ChartTool, "-1", "INVALID_ARGUMENT")]
    [InlineData(ChartTool, "11", "LIMIT_EXCEEDED")]
    [InlineData(ChartTool, "\"2\"", "INVALID_ARGUMENT")]
    [InlineData(ProfileTool, "-1", "INVALID_ARGUMENT")]
    [InlineData(ProfileTool, "11", "LIMIT_EXCEEDED")]
    [InlineData(ProfileTool, "\"2\"", "INVALID_ARGUMENT")]
    [InlineData(RiskTool, "-1", "INVALID_ARGUMENT")]
    [InlineData(RiskTool, "11", "LIMIT_EXCEEDED")]
    [InlineData(RiskTool, "\"2\"", "INVALID_ARGUMENT")]
    [InlineData(PathTool, "-1", "INVALID_ARGUMENT")]
    [InlineData(PathTool, "11", "LIMIT_EXCEEDED")]
    [InlineData(PathTool, "\"2\"", "INVALID_ARGUMENT")]
    public async Task ABadDecimalPlacesIsRefusedWithTheSharedRule(string tool, string places, string code)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, With(OpenOf(tool), $",\"decimalPlaces\":{places}")))!;

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("decimalPlaces", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(ChartTool, false)]
    [InlineData(ProfileTool, true)]
    [InlineData(RiskTool, false)]
    [InlineData(PathTool, false)]
    public async Task TheSchemaOffersDecimalPlacesAsAnOptionalBoundedInteger_AndOnlyTheProfileOffersIncludeBins(string tool, bool offersBins)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement entry = McpTestSupport.Result(list).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == tool);
        JsonElement schema = entry.GetProperty("inputSchema");
        JsonElement property = schema.GetProperty("properties").GetProperty("decimalPlaces");

        Assert.Equal("integer", property.GetProperty("type").GetString());
        Assert.Equal(0, property.GetProperty("minimum").GetInt32());
        Assert.Equal(SeriesOutputOptions.MaxDecimalPlaces, property.GetProperty("maximum").GetInt32());
        Assert.False(property.TryGetProperty("default", out _), "absence of decimalPlaces means no rounding: there is no number to state as the default");
        Assert.Contains("decimalPlaces", entry.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(offersBins, schema.GetProperty("properties").TryGetProperty("includeBins", out JsonElement bins));
        if (offersBins)
        {
            Assert.Equal("boolean", bins.GetProperty("type").GetString());
            Assert.Contains("includeBins", entry.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
    }
}
