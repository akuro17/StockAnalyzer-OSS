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
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_get_portfolio_summary and sa_analyze_portfolio_risk (owner decisions U-06, U-07) through the real composition: the saved portfolio files are read as the application reads them,
/// valued at the stored prices, and the risk figures are those of <see cref="PortfolioRiskCalculator"/> on the same bars.
/// </summary>
public sealed class PortfolioToolsTests : IDisposable
{
    private const int Days = 120;
    private static readonly DateTimeOffset Day1 = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly decimal[] MsftCloses = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();
    private static readonly decimal[] ToyotaCloses = Enumerable.Range(0, Days).Select(i => Math.Round(2000m + (decimal)(150 * Math.Cos(i / 8.0)), 1)).ToArray();

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "MSFT", "7203.T");
    private readonly string _portfolioRoot = Path.Combine(Path.GetTempPath(), "sa_portfolio_tools_" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _database = new();
    private readonly List<McpServerRuntime> _runtimes = new();

    public PortfolioToolsTests()
    {
        Directory.CreateDirectory(_portfolioRoot);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", MsftCloses);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "7203-T", ToyotaCloses);
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

    private sealed class UnavailablePortfolioSource : IReadOnlyPortfolioSource
    {
        public Task<PortfolioReadResult> LoadAsync(CancellationToken cancellationToken = default)
            => throw new DataUnavailableException("The portfolio is being saved.");
    }

    private async Task SaveAsync(params Transaction[] transactions)
    {
        Portfolio portfolio = new PortfolioManager().RebuildPortfolio(0m, transactions);
        using var repository = new UserPortfolioRepository(_portfolioRoot);
        await repository.SavePortfolioAsync(portfolio);
    }

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyPortfolioSource? source = null)
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
            portfolioSource: source ?? new FileReadOnlyPortfolioSource(Path.Combine(_portfolioRoot, "Portfolios"), _database));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    private static Transaction Deposit(decimal amount, CurrencyCode? currency = null, ExchangeRate? rate = null)
        => new(Day1, TransactionType.Deposit, null, 0m, 0m, amount, price: currency is { } c ? new Money(0m, c) : null, appliedRate: rate);

    private static Transaction Trade(TransactionType type, string ticker, decimal quantity, decimal price, int day, CurrencyCode? currency = null)
        => new(Day1.AddDays(day), type, ticker, quantity, price, 0m, price: currency is { } c ? new Money(price, c) : null, commission: currency is { } c2 ? new Money(0m, c2) : null);

    private static JsonElement Position(JsonDocument response, string ticker, bool isShort = false)
        => McpTestSupport.Structured(response).GetProperty("positions").EnumerateArray()
            .Single(p => p.GetProperty("ticker").GetString() == ticker && p.GetProperty("isShort").GetBoolean() == isShort);

    // ---- sa_get_portfolio_summary -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Summary_ValuesTheOpenPositionsAtTheLatestStoredCloseByTheApplicationsOwnValuation()
    {
        await SaveAsync(Deposit(1_000_000m), Trade(TransactionType.Long, "MSFT", 10m, 100m, 1), Trade(TransactionType.Short, "7203-T", 3m, 2100m, 2));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_portfolio_summary");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        decimal msft = MsftCloses[^1];
        decimal toyota = ToyotaCloses[^1];
        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("portfolioFound").GetBoolean());
        Assert.Equal("JPY", result.GetProperty("baseCurrency").GetString());
        JsonElement longPosition = Position(response, "MSFT");
        Assert.Equal(msft, longPosition.GetProperty("latestPrice").GetDecimal());
        Assert.Equal(10m * msft, longPosition.GetProperty("marketValueInBase").GetDecimal());
        Assert.Equal(10m * (msft - 100m), longPosition.GetProperty("unrealizedPLInBase").GetDecimal());
        JsonElement shortPosition = Position(response, "7203-T", isShort: true);
        Assert.Equal(-3m * toyota, shortPosition.GetProperty("marketValueInBase").GetDecimal());
        Assert.Equal(3m * (2100m - toyota), shortPosition.GetProperty("unrealizedPLInBase").GetDecimal());

        JsonElement metrics = result.GetProperty("metrics");
        decimal cash = 1_000_000m - (10m * 100m) + (3m * 2100m);
        Assert.Equal(cash, metrics.GetProperty("cashBalance").GetDecimal());
        Assert.Equal(cash + (10m * msft) - (3m * toyota), metrics.GetProperty("totalValue").GetDecimal());
        Assert.Equal((10m * (msft - 100m)) + (3m * (2100m - toyota)), metrics.GetProperty("totalUnrealizedPL").GetDecimal());
        Assert.Equal(0, result.GetProperty("unconvertedCurrencies").GetArrayLength());
    }

    [Fact]
    public async Task Summary_ARealizedTradeIsInTheRebuiltClosedPositionsAndRealizedProfit()
    {
        await SaveAsync(Deposit(1_000_000m), Trade(TransactionType.Long, "MSFT", 10m, 100m, 1), Trade(TransactionType.ExitLong, "MSFT", 4m, 130m, 3));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_portfolio_summary");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(1, result.GetProperty("closedPositionCount").GetInt32());
        Assert.Equal(120m, result.GetProperty("metrics").GetProperty("totalRealizedPL").GetDecimal());
        Assert.Equal(6m, Position(response, "MSFT").GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task Summary_AHoldingInAnotherCurrencyIsConvertedByTheRateTheUsersTransactionsApplied_AndOneWithoutARateIsListed()
    {
        var usdToJpy = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, new DateTime(2024, 1, 2));
        var eur = new CurrencyCode("EUR");
        await SaveAsync(
            Deposit(1_000_000m),
            Deposit(10_000m, CurrencyCode.USD, usdToJpy),
            Deposit(5_000m, eur),
            Trade(TransactionType.Long, "MSFT", 10m, 100m, 1, CurrencyCode.USD),
            Trade(TransactionType.Long, "7203-T", 2m, 90m, 2, eur));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument inJpy = await CallAsync(dispatcher, "sa_get_portfolio_summary", """{"baseCurrency":"jpy"}""");
        using JsonDocument inUsd = await CallAsync(dispatcher, "sa_get_portfolio_summary", """{"baseCurrency":"USD"}""");

        decimal msft = MsftCloses[^1];
        JsonElement jpy = McpTestSupport.Structured(inJpy);
        Assert.Equal(new[] { "EUR" }, jpy.GetProperty("unconvertedCurrencies").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.True(Position(inJpy, "MSFT").GetProperty("convertedToBase").GetBoolean());
        Assert.Equal(10m * msft * 150m, Position(inJpy, "MSFT").GetProperty("marketValueInBase").GetDecimal());
        Assert.False(Position(inJpy, "7203-T").GetProperty("convertedToBase").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Position(inJpy, "7203-T").GetProperty("marketValueInBase").ValueKind);
        Assert.Contains(jpy.GetProperty("exchangeRates").EnumerateArray(), r => r.GetProperty("baseCurrency").GetString() == "USD" && r.GetProperty("rate").GetDecimal() == 150m);

        JsonElement usd = McpTestSupport.Structured(inUsd);
        Assert.Equal(10m * msft, Position(inUsd, "MSFT").GetProperty("marketValueInBase").GetDecimal());
        Assert.Equal(new[] { "EUR" }, usd.GetProperty("unconvertedCurrencies").EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    [Fact]
    public async Task Summary_NoSavedPortfolioIsAnEmptyAnswerAndNothingIsCreated()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_portfolio_summary");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(result.GetProperty("portfolioFound").GetBoolean());
        Assert.Equal(0, result.GetProperty("positions").GetArrayLength());
        Assert.Equal(0m, result.GetProperty("metrics").GetProperty("totalValue").GetDecimal());
        Assert.False(Directory.Exists(Path.Combine(_portfolioRoot, "Portfolios")));
    }

    [Fact]
    public async Task Summary_APositionWithoutAStoredPriceIsValuedAtItsCostAndSaysSo()
    {
        await SaveAsync(Deposit(1_000_000m), Trade(TransactionType.Long, "NOPRICE", 5m, 200m, 1));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_portfolio_summary");

        JsonElement position = Position(response, "NOPRICE");
        Assert.False(position.GetProperty("priceAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, position.GetProperty("latestPrice").ValueKind);
        Assert.Equal(1000m, position.GetProperty("marketValueInBase").GetDecimal());
        Assert.Equal(0m, position.GetProperty("unrealizedPLInBase").GetDecimal());
    }

    [Theory]
    [InlineData("""{"baseCurrency":"YEN1"}""")]
    [InlineData("""{"baseCurrency":"J1Y"}""")]
    [InlineData("""{"baseCurrency":""}""")]
    public async Task Summary_ABaseCurrencyThatIsNotAThreeLetterCodeIsInvalid(string arguments)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_portfolio_summary", arguments);

        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("baseCurrency", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("sa_get_portfolio_summary")]
    [InlineData("sa_analyze_portfolio_risk")]
    public async Task APortfolioBeingSavedIsDataUnavailableNotAnEmptyAnswer(string tool)
    {
        McpMessageDispatcher dispatcher = await StartAsync(new UnavailablePortfolioSource());

        using JsonDocument response = await CallAsync(dispatcher, tool);

        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.ErrorCodeOf(response));
    }

    // ---- sa_analyze_portfolio_risk ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<CandleData> Bars(decimal[] closes, int count)
        => closes.Select((close, i) => new CandleData(new DateTime(2024, 1, 1).AddDays(i), close, close, close, close, 1000)).TakeLast(count).ToList();

    [Fact]
    public async Task Risk_IsThePortfolioRiskCalculatorOnTheSameBarsAndTheApplicationsMarketValues()
    {
        await SaveAsync(Deposit(1_000_000m), Trade(TransactionType.Long, "MSFT", 10m, 100m, 1), Trade(TransactionType.Short, "7203-T", 3m, 2100m, 2));
        McpMessageDispatcher dispatcher = await StartAsync();
        const int Lookback = 90;

        using JsonDocument response = await CallAsync(dispatcher, "sa_analyze_portfolio_risk", $$"""{"lookbackBars":{{Lookback}},"confidenceLevel":0.9}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        var candles = new Dictionary<string, IReadOnlyList<CandleData>> { ["MSFT"] = Bars(MsftCloses, Lookback), ["7203-T"] = Bars(ToyotaCloses, Lookback) };
        PortfolioRiskResult expected = PortfolioRiskCalculator.Calculate(
            new[] { new PortfolioExposure("7203-T", -3m * ToyotaCloses[^1]), new PortfolioExposure("MSFT", 10m * MsftCloses[^1]) }, candles, 0.9); // the tool lists the positions by key: 7203-T_Short, MSFT
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(expected.GrossExposure, result.GetProperty("grossExposure").GetDecimal());
        Assert.Equal(expected.NetExposure, result.GetProperty("netExposure").GetDecimal());
        Assert.Equal(expected.CommonDateCount, result.GetProperty("commonDateCount").GetInt32());
        Assert.Equal(expected.ValueAtRisk, result.GetProperty("valueAtRisk").GetDecimal());
        Assert.Equal(expected.ConditionalValueAtRisk, result.GetProperty("conditionalValueAtRisk").GetDecimal());
        Assert.Equal(expected.PortfolioVolatility, result.GetProperty("portfolioVolatility").GetDecimal());
        JsonElement[] contributions = result.GetProperty("contributions").EnumerateArray().ToArray();
        Assert.Equal(expected.Contributions.Select(c => c.Symbol), contributions.Select(c => c.GetProperty("symbol").GetString()));
        Assert.Equal(expected.Contributions.Select(c => c.Weight), contributions.Select(c => c.GetProperty("weight").GetDecimal()));
        Assert.True(contributions.Single(c => c.GetProperty("symbol").GetString() == "7203-T").GetProperty("weight").GetDecimal() < 0m);
        Assert.Equal(5, result.GetProperty("definitions").GetArrayLength());
    }

    [Fact]
    public async Task Risk_AHoldingWithoutAPriceOrARateIsExcludedWithItsReasonAndTheRestIsStillCalculated()
    {
        await SaveAsync(
            Deposit(1_000_000m), Deposit(5_000m, new CurrencyCode("EUR")),
            Trade(TransactionType.Long, "MSFT", 10m, 100m, 1),
            Trade(TransactionType.Long, "NOPRICE", 5m, 200m, 2),
            Trade(TransactionType.Long, "7203-T", 2m, 90m, 3, new CurrencyCode("EUR")));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_analyze_portfolio_risk");

        JsonElement result = McpTestSupport.Structured(response);
        var reasons = result.GetProperty("excludedSymbols").EnumerateArray().ToDictionary(e => e.GetProperty("symbol").GetString()!, e => e.GetProperty("reason").GetString());
        Assert.Equal("No stored price", reasons["NOPRICE"]);
        Assert.Equal("No exchange rate to base currency", reasons["7203-T"]);
        Assert.Equal("MSFT", Assert.Single(result.GetProperty("contributions").EnumerateArray()).GetProperty("symbol").GetString());
        Assert.Equal(1m, result.GetProperty("contributions")[0].GetProperty("weight").GetDecimal());
        Assert.Equal(new[] { "EUR" }, result.GetProperty("unconvertedCurrencies").EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    [Fact]
    public async Task Risk_ABenchmarkAddsTheBeta_AndABadConfidenceLevelOrBenchmarkIsRefused()
    {
        await SaveAsync(Deposit(1_000_000m), Trade(TransactionType.Long, "MSFT", 10m, 100m, 1), Trade(TransactionType.Long, "7203-T", 3m, 2100m, 2));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument withBenchmark = await CallAsync(dispatcher, "sa_analyze_portfolio_risk", """{"benchmark":"msft"}""");
        using JsonDocument without = await CallAsync(dispatcher, "sa_analyze_portfolio_risk");
        using JsonDocument badLevel = await CallAsync(dispatcher, "sa_analyze_portfolio_risk", """{"confidenceLevel":0.2}""");
        using JsonDocument badBenchmark = await CallAsync(dispatcher, "sa_analyze_portfolio_risk", """{"benchmark":"NOSUCH"}""");

        Assert.Equal(JsonValueKind.Number, McpTestSupport.Structured(withBenchmark).GetProperty("beta").ValueKind);
        Assert.False(McpTestSupport.Structured(without).TryGetProperty("beta", out _));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(badLevel));
        Assert.Equal("confidenceLevel", McpTestSupport.Structured(badLevel).GetProperty("param").GetString());
        Assert.Equal("SYMBOL_NOT_FOUND", McpTestSupport.ErrorCodeOf(badBenchmark));
    }

    [Fact]
    public async Task Risk_NoSavedPortfolioHasNoFiguresAndAnExplanation()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_analyze_portfolio_risk");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(result.GetProperty("portfolioFound").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("valueAtRisk").ValueKind);
        Assert.NotEqual(0, result.GetProperty("notes").GetArrayLength());
    }
}
