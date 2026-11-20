using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Factories;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T6 (decision D-6): <c>includeClosedPositions</c>, <c>includeTransactions</c> and <c>limit</c> of sa_get_portfolio_summary through the real composition, with a
/// portfolio built directly (so the order tie-breakers, the truncation and the legacy rows are exact). No allocation is offered (D-6d = C).
/// </summary>
public sealed class PortfolioHistoryToolTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2024, 1, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] SummaryKeys =
    {
        "portfolioFound", "baseCurrency", "metrics", "cashBalances", "positions", "closedPositionCount", "realizedPLByCurrency", "unconvertedCurrencies", "exchangeRates",
    };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(10, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private sealed class FixedPortfolioSource : IReadOnlyPortfolioSource
    {
        private readonly PortfolioReadResult _result;

        public FixedPortfolioSource(Portfolio portfolio, bool found = true) => _result = new PortfolioReadResult(found, portfolio);

        public int Reads { get; private set; }

        public Task<PortfolioReadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(_result);
        }
    }

    private sealed class UnavailableSource : IReadOnlyPortfolioSource
    {
        public Task<PortfolioReadResult> LoadAsync(CancellationToken cancellationToken = default) => throw new DataUnavailableException("The portfolio is being saved.");
    }

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyPortfolioSource source, params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()), portfolioSource: source);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> SummaryAsync(McpMessageDispatcher dispatcher, string arguments = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_get_portfolio_summary", arguments))!;

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        return McpTestSupport.Structured(response);
    }

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static Portfolio PortfolioOf(IReadOnlyList<Transaction>? history = null, IReadOnlyList<ClosedPosition>? closed = null) => new(0m, history: history, closedPositions: closed);

    private static ClosedPosition Closed(Guid id, TransactionType type, DateTimeOffset entry, DateTimeOffset exit, string ticker = "MSFT")
        => new(id, ticker, type, 10m, 100m, 110m, entry, exit, 100m, 2m);

    private static Transaction Trade(int minute, Guid? id = null, TransactionType type = TransactionType.Long)
        => new(Day.AddMinutes(minute), type, "MSFT", 1m, 100m, -100m, id: id);

    // ---- omitted = unchanged ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheArgumentsTheAnswerHasTheSameKeysAsBefore_AndFalseGivesTheSameBytes_AndNothingIsListed()
    {
        var source = new FixedPortfolioSource(PortfolioOf(new[] { Trade(1) }, new[] { Closed(Guid.NewGuid(), TransactionType.Long, Day, Day.AddDays(1)) }));
        var dispatcher = await StartAsync(source);

        using var omitted = await SummaryAsync(dispatcher);
        using var explicitFalse = await SummaryAsync(dispatcher, """{"includeClosedPositions":false,"includeTransactions":false}""");

        JsonElement result = Ok(omitted);
        Assert.Equal(SummaryKeys, Keys(result));
        Assert.Equal(1, result.GetProperty("closedPositionCount").GetInt32());
        Assert.Equal(result.GetRawText(), Ok(explicitFalse).GetRawText());
    }

    [Fact]
    public async Task AskingForTheListsAddsOnlyTheTwoNewKeysAtTheEnd_AndEveryOldKeyIsUnchanged()
    {
        var source = new FixedPortfolioSource(PortfolioOf(new[] { Trade(1) }, new[] { Closed(Guid.NewGuid(), TransactionType.Long, Day, Day.AddDays(1)) }));
        var dispatcher = await StartAsync(source);

        using var plain = await SummaryAsync(dispatcher);
        using var both = await SummaryAsync(dispatcher, """{"includeClosedPositions":true,"includeTransactions":true}""");

        JsonElement before = Ok(plain);
        JsonElement after = Ok(both);
        Assert.Equal(SummaryKeys.Concat(new[] { "closedPositions", "transactions" }), Keys(after));
        foreach (string key in SummaryKeys)
        {
            Assert.Equal(before.GetProperty(key).GetRawText(), after.GetProperty(key).GetRawText());
        }
    }

    // ---- closed positions -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClosedPositionsAreNewestExitFirst_ThenNewestEntry_ThenTheIdTextAscending()
    {
        DateTimeOffset exit = Day.AddDays(10);
        var oldestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newestId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var sameEntryA = Closed(newestId, TransactionType.Long, Day, exit);
        var sameEntryB = Closed(oldestId, TransactionType.Short, Day, exit, "7203-T");
        var laterEntry = Closed(Guid.Parse("88888888-0000-0000-0000-000000000001"), TransactionType.Long, Day.AddDays(5), exit);
        var laterExit = Closed(Guid.Parse("44444444-0000-0000-0000-000000000001"), TransactionType.Long, Day, exit.AddDays(1));
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(closed: new[] { sameEntryA, sameEntryB, laterEntry, laterExit })));

        using var response = await SummaryAsync(dispatcher, """{"includeClosedPositions":true}""");

        JsonElement list = Ok(response).GetProperty("closedPositions");
        Assert.Equal(new[] { laterExit.Id, laterEntry.Id, oldestId, newestId }, list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(4, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, list.GetProperty("returnedCount").GetInt32());
        Assert.False(list.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AClosedPositionItemHasTheDocumentedFieldsInOrder_AndNoAllocationNorCurrency()
    {
        var closed = new ClosedPosition(Guid.Parse("11111111-2222-3333-4444-555555555555"), "7203-T", TransactionType.Short, 3m, 2100.5m, 1900m, Day, Day.AddDays(3), 601.5m, 12m);
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(closed: new[] { closed })));

        using var response = await SummaryAsync(dispatcher, """{"includeClosedPositions":true}""");

        JsonElement item = Ok(response).GetProperty("closedPositions").GetProperty("items")[0];
        Assert.Equal(new[] { "id", "ticker", "side", "quantity", "entryPrice", "exitPrice", "entryTime", "exitTime", "realizedPnL", "totalFees" }, Keys(item));
        Assert.Equal("11111111-2222-3333-4444-555555555555", item.GetProperty("id").GetString());
        Assert.Equal("Short", item.GetProperty("side").GetString());
        Assert.Equal(3m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(2100.5m, item.GetProperty("entryPrice").GetDecimal());
        Assert.Equal(1900m, item.GetProperty("exitPrice").GetDecimal());
        Assert.Equal(ToolJson.FormatTimestampUtc(Day), item.GetProperty("entryTime").GetString());
        Assert.Equal(ToolJson.FormatTimestampUtc(Day.AddDays(3)), item.GetProperty("exitTime").GetString());
        Assert.Equal(601.5m, item.GetProperty("realizedPnL").GetDecimal());
        Assert.Equal(12m, item.GetProperty("totalFees").GetDecimal());
        Assert.DoesNotContain("allocation", Ok(response).GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- transactions -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoHundredAndFiveTransactionsAreTruncatedAtTwoHundred_NewestFirst_WithTheTotal()
    {
        Transaction[] history = Enumerable.Range(0, 205).Select(i => Trade(i)).ToArray();
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(history)));

        using var response = await SummaryAsync(dispatcher, """{"includeTransactions":true}""");

        JsonElement list = Ok(response).GetProperty("transactions");
        Assert.Equal(205, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(200, list.GetProperty("returnedCount").GetInt32());
        Assert.True(list.GetProperty("truncated").GetBoolean());
        JsonElement[] items = list.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(200, items.Length);
        Assert.Equal(history[204].Id, items[0].GetProperty("id").GetGuid());
        Assert.Equal(history[5].Id, items[^1].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TransactionsWithTheSameTimeAreOrderedByTheIdTextAscending()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000009");
        var middle = Guid.Parse("7fffffff-0000-0000-0000-000000000009");
        var high = Guid.Parse("ffffffff-0000-0000-0000-000000000009");
        var newest = Guid.Parse("11111111-0000-0000-0000-000000000009");
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(new[] { Trade(5, high), Trade(5, low), Trade(5, middle), Trade(6, newest) })));

        using var response = await SummaryAsync(dispatcher, """{"includeTransactions":true}""");

        Guid[] ids = Ok(response).GetProperty("transactions").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(new[] { newest, low, middle, high }, ids);
    }

    [Fact]
    public async Task ATransactionItemHasTheDocumentedFields_TheAppliedRateOnlyWhenOne_AndNeverTheUsersAnnotations()
    {
        var rate = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, new DateTime(2024, 1, 2));
        var deposit = new Transaction(Day, TransactionType.Deposit, null, 0m, 0m, 10_000m, price: new Money(0m, CurrencyCode.USD), commission: new Money(0m, CurrencyCode.USD), appliedRate: rate);
        var trade = new Transaction(Day.AddMinutes(1), TransactionType.ExitLong, "MSFT", 4m, 130m, 520m, 1.5m, notes: "private note text", targetPrice: 140m, stopLoss: 90m);
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(new[] { deposit, trade })));

        using var response = await SummaryAsync(dispatcher, """{"includeTransactions":true}""");

        JsonElement[] items = Ok(response).GetProperty("transactions").GetProperty("items").EnumerateArray().ToArray();
        JsonElement tradeItem = items[0];
        Assert.Equal(new[] { "id", "executedAt", "type", "ticker", "quantity", "pricePerUnit", "priceCurrency", "cashAmount", "fee", "commissionCurrency" }, Keys(tradeItem));
        Assert.Equal("ExitLong", tradeItem.GetProperty("type").GetString());
        Assert.Equal("JPY", tradeItem.GetProperty("priceCurrency").GetString());
        Assert.Equal(1.5m, tradeItem.GetProperty("fee").GetDecimal());
        JsonElement depositItem = items[1];
        Assert.Equal(new[] { "id", "executedAt", "type", "quantity", "pricePerUnit", "priceCurrency", "cashAmount", "fee", "commissionCurrency", "appliedRate" }, Keys(depositItem));
        Assert.Equal("Deposit", depositItem.GetProperty("type").GetString());
        Assert.Equal("USD", depositItem.GetProperty("priceCurrency").GetString());
        Assert.Equal(new[] { "baseCurrency", "quoteCurrency", "rate", "asOf" }, Keys(depositItem.GetProperty("appliedRate")));
        Assert.Equal(150m, depositItem.GetProperty("appliedRate").GetProperty("rate").GetDecimal());
        string text = Ok(response).GetRawText();
        Assert.DoesNotContain("private note text", text);
        Assert.DoesNotContain("notes", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetPrice", text);
        Assert.DoesNotContain("stopLoss", text);
    }

    [Fact]
    public async Task ALegacyRowWithoutACurrencyLeavesTheCurrencyKeysOut()
    {
        var legacy = new Transaction(Day, TransactionType.Long, "MSFT", 1m, 100m, -100m, price: default(Money), commission: default(Money));
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(new[] { legacy })));

        using var response = await SummaryAsync(dispatcher, """{"includeTransactions":true}""");

        JsonElement item = Ok(response).GetProperty("transactions").GetProperty("items")[0];
        Assert.Equal(new[] { "id", "executedAt", "type", "ticker", "quantity", "pricePerUnit", "cashAmount", "fee" }, Keys(item));
    }

    [Fact]
    public void EveryTransactionTypeHasOneStableName_AndTheAliasesShareIt()
    {
        Assert.Equal("Long", PortfolioHistoryJson.TransactionTypeName(TransactionType.Long));
        Assert.Equal("ExitLong", PortfolioHistoryJson.TransactionTypeName(TransactionType.ExitLong));
        Assert.Equal("Deposit", PortfolioHistoryJson.TransactionTypeName(TransactionType.Deposit));
        Assert.Equal("Withdrawal", PortfolioHistoryJson.TransactionTypeName(TransactionType.Withdrawal));
        Assert.Equal("Short", PortfolioHistoryJson.TransactionTypeName(TransactionType.Short));
        Assert.Equal("ExitShort", PortfolioHistoryJson.TransactionTypeName(TransactionType.ExitShort));
        Assert.Equal("Long", PortfolioHistoryJson.TransactionTypeName(TransactionType.Buy));
        Assert.Equal("ExitLong", PortfolioHistoryJson.TransactionTypeName(TransactionType.Sell));
        Assert.Equal("ExitShort", PortfolioHistoryJson.TransactionTypeName(TransactionType.Cover));
        Assert.Equal("9", PortfolioHistoryJson.TransactionTypeName((TransactionType)9));
        // Every member (aliases included) maps to a name, never to its number.
        Assert.All(Enum.GetValues<TransactionType>(), member => Assert.False(int.TryParse(PortfolioHistoryJson.TransactionTypeName(member), out _), member.ToString()));
    }

    // ---- limit ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LimitCapsEachRequestedList()
    {
        Transaction[] history = Enumerable.Range(0, 20).Select(i => Trade(i)).ToArray();
        ClosedPosition[] closed = Enumerable.Range(0, 20).Select(i => Closed(Guid.NewGuid(), TransactionType.Long, Day, Day.AddDays(i + 1))).ToArray();
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(history, closed)));

        using var response = await SummaryAsync(dispatcher, """{"includeClosedPositions":true,"includeTransactions":true,"limit":7}""");

        JsonElement result = Ok(response);
        foreach (string name in new[] { "closedPositions", "transactions" })
        {
            Assert.Equal(20, result.GetProperty(name).GetProperty("totalCount").GetInt32());
            Assert.Equal(7, result.GetProperty(name).GetProperty("returnedCount").GetInt32());
            Assert.True(result.GetProperty(name).GetProperty("truncated").GetBoolean());
            Assert.Equal(7, result.GetProperty(name).GetProperty("items").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("""{"includeTransactions":true,"limit":0}""", "INVALID_ARGUMENT")]
    [InlineData("""{"includeTransactions":true,"limit":-5}""", "INVALID_ARGUMENT")]
    [InlineData("""{"includeTransactions":true,"limit":201}""", "LIMIT_EXCEEDED")]
    [InlineData("""{"includeClosedPositions":true,"limit":201}""", "LIMIT_EXCEEDED")]
    [InlineData("""{"limit":5}""", "INVALID_ARGUMENT")]
    [InlineData("""{"includeClosedPositions":false,"limit":5}""", "INVALID_ARGUMENT")]
    public async Task ARefusedLimitNamesTheArgument_AndIsRefusedBeforeThePortfolioIsRead(string arguments, string code)
    {
        var source = new FixedPortfolioSource(PortfolioOf());
        var dispatcher = await StartAsync(source);

        using var response = await SummaryAsync(dispatcher, arguments);

        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("limit", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task TheListLimitsAreConfigurable_AndTheSmallerOneBoundsLimitWhenBothListsAreAsked()
    {
        Transaction[] history = Enumerable.Range(0, 9).Select(i => Trade(i)).ToArray();
        ClosedPosition[] closed = Enumerable.Range(0, 9).Select(i => Closed(Guid.NewGuid(), TransactionType.Long, Day, Day.AddDays(i + 1))).ToArray();
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf(history, closed)), ("MaxReturnedClosedPositions", "2"), ("MaxReturnedTransactions", "5"));

        using var defaults = await SummaryAsync(dispatcher, """{"includeClosedPositions":true,"includeTransactions":true}""");
        using var tooMuch = await SummaryAsync(dispatcher, """{"includeClosedPositions":true,"includeTransactions":true,"limit":3}""");
        using var onlyTransactions = await SummaryAsync(dispatcher, """{"includeTransactions":true,"limit":5}""");

        JsonElement result = Ok(defaults);
        Assert.Equal(2, result.GetProperty("closedPositions").GetProperty("returnedCount").GetInt32());
        Assert.Equal(5, result.GetProperty("transactions").GetProperty("returnedCount").GetInt32());
        Assert.True(McpTestSupport.IsError(tooMuch));
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(tooMuch));
        Assert.Equal(5, Ok(onlyTransactions).GetProperty("transactions").GetProperty("returnedCount").GetInt32());
    }

    // ---- no portfolio / unreadable portfolio ------------------------------------------------------------------------------------

    [Fact]
    public async Task NoPortfolioGivesEmptyListsWithTotalCountZero()
    {
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioFactory.Empty, found: false));

        using var response = await SummaryAsync(dispatcher, """{"includeClosedPositions":true,"includeTransactions":true}""");

        JsonElement result = Ok(response);
        Assert.False(result.GetProperty("portfolioFound").GetBoolean());
        foreach (string name in new[] { "closedPositions", "transactions" })
        {
            Assert.Equal(0, result.GetProperty(name).GetProperty("totalCount").GetInt32());
            Assert.Equal(0, result.GetProperty(name).GetProperty("returnedCount").GetInt32());
            Assert.False(result.GetProperty(name).GetProperty("truncated").GetBoolean());
            Assert.Empty(result.GetProperty(name).GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task AnUnreadablePortfolioIsDataUnavailableWithoutAnyPathInTheMessage()
    {
        var dispatcher = await StartAsync(new UnavailableSource());

        using var response = await SummaryAsync(dispatcher, """{"includeTransactions":true}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.ErrorCodeOf(response));
        string text = McpTestSupport.Structured(response).GetRawText();
        Assert.DoesNotContain(":\\", text);
        Assert.DoesNotContain(Path.GetTempPath(), text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- schema -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSchemaAdvertisesTheThreeArguments_AndTheDescriptionSaysNothingIsAllocated()
    {
        var dispatcher = await StartAsync(new FixedPortfolioSource(PortfolioOf()));

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_get_portfolio_summary");
        JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");

        Assert.Equal("boolean", properties.GetProperty("includeClosedPositions").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("includeTransactions").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("limit").GetProperty("type").GetString());
        Assert.Equal(1, properties.GetProperty("limit").GetProperty("minimum").GetInt32());
        Assert.Equal(200, properties.GetProperty("limit").GetProperty("maximum").GetInt32());
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("stockanalyzer://docs/tools/sa_get_portfolio_summary", description, StringComparison.Ordinal);
        string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_get_portfolio_summary.md"));
        Assert.Contains("never returned", doc);
        Assert.Contains("no allocation", doc, StringComparison.OrdinalIgnoreCase);
    }
}
