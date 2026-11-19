using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T3 (decision D-2e): <c>fields</c> of sa_get_ticker_metadata through the real composition. Omitted, the answer keeps its bytes; sent, the metadata object holds
/// only the named properties in the fixed property order, and symbol, found and timeSeriesLastUpdated are always written.
/// </summary>
public sealed class TickerMetadataFieldsTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

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
        await _fixture.SeedMetadataAsync("MSFT", TickerMetadata.Unknown with
        {
            ShortName = "Microsoft",
            Sector = "Technology",
            CurrentPrice = 123.45m,
            Tag = "core holding",
        });
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string fieldsJson = "", string symbols = """["MSFT"]""")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_get_ticker_metadata", $$$"""{"symbols":{{{symbols}}}{{{fieldsJson}}}}"""))!;

    private static JsonElement[] Results(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("results").EnumerateArray().ToArray();

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    // ---- omitted = unchanged ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutFieldsTheAnswerHoldsEveryStoredPropertyInTheFixedOrder()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher);

        JsonElement item = Results(response).Single();
        Assert.Equal(new[] { "symbol", "found", "timeSeriesLastUpdated", "metadata" }, Keys(item));
        string[] storedOrder = TickerMetadataJson.SelectableFieldNames.Where(name => item.GetProperty("metadata").TryGetProperty(name, out _)).ToArray();
        Assert.Equal(storedOrder, Keys(item.GetProperty("metadata")));
        Assert.Contains("shortName", storedOrder);
        Assert.Contains("sector", storedOrder);
    }

    [Fact]
    public async Task NamingEverySelectableFieldGivesTheSameBytesAsOmittingTheArgument()
    {
        var dispatcher = await StartAsync();
        string everything = JsonSerializer.Serialize(TickerMetadataJson.SelectableFieldNames);

        using var omitted = await CallAsync(dispatcher);
        using var all = await CallAsync(dispatcher, $$""","fields":{{everything}}""");

        Assert.Equal(McpTestSupport.Structured(omitted).GetRawText(), McpTestSupport.Structured(all).GetRawText());
    }

    // ---- selection --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheNamedPropertiesAreWritten_AndTheThreeItemKeysStay()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ""","fields":["sector","symbol"]""");

        JsonElement item = Results(response).Single();
        Assert.Equal(new[] { "symbol", "found", "timeSeriesLastUpdated", "metadata" }, Keys(item));
        Assert.Equal("MSFT", item.GetProperty("symbol").GetString());
        Assert.True(item.GetProperty("found").GetBoolean());
        Assert.Equal("2024-01-20", item.GetProperty("timeSeriesLastUpdated").GetString());
        Assert.Equal(new[] { "sector" }, Keys(item.GetProperty("metadata")));
        Assert.Equal("Technology", item.GetProperty("metadata").GetProperty("sector").GetString());
    }

    [Fact]
    public async Task TheOrderOfTheOutputIsTheFixedOrderNotTheOrderOfFields()
    {
        var dispatcher = await StartAsync();

        using var one = await CallAsync(dispatcher, ""","fields":["sector","currentPrice","shortName"]""");
        using var other = await CallAsync(dispatcher, ""","fields":["shortName","sector","currentPrice"]""");
        using var all = await CallAsync(dispatcher);

        string[] fixedOrder = Keys(Results(all).Single().GetProperty("metadata")).Where(k => k is "sector" or "currentPrice" or "shortName").ToArray();
        Assert.Equal(fixedOrder, Keys(Results(one).Single().GetProperty("metadata")));
        Assert.Equal(McpTestSupport.Structured(one).GetRawText(), McpTestSupport.Structured(other).GetRawText());
    }

    [Fact]
    public async Task DuplicatesAreIgnored()
    {
        var dispatcher = await StartAsync();

        using var once = await CallAsync(dispatcher, ""","fields":["sector"]""");
        using var twice = await CallAsync(dispatcher, ""","fields":["sector","sector"]""");

        Assert.Equal(McpTestSupport.Structured(once).GetRawText(), McpTestSupport.Structured(twice).GetRawText());
    }

    [Fact]
    public async Task NamingOnlyTheAlwaysWrittenKeysIsAcceptedAndLeavesAnEmptyMetadataObject()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ""","fields":["symbol","found","timeSeriesLastUpdated"]""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        JsonElement item = Results(response).Single();
        Assert.Equal(new[] { "symbol", "found", "timeSeriesLastUpdated", "metadata" }, Keys(item));
        Assert.Empty(Keys(item.GetProperty("metadata")));
    }

    [Fact]
    public async Task ASymbolWithNothingStoredStaysNotFoundWithNullMetadata()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ""","fields":["sector"]""", symbols: """["7203.T"]""");

        JsonElement item = Results(response).Single();
        Assert.False(item.GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("metadata").ValueKind);
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyListIsRefused()
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, ""","fields":[]""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("fields", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(""","fields":["nosuchfield"]""", "fields[0]")]
    [InlineData(""","fields":["sector","Sector"]""", "fields[1]")]
    [InlineData(""","fields":["sector","shortName","TickerMetadata"]""", "fields[2]")]
    public async Task AnUnknownNameIsRefusedAtItsIndexAndTheHintListsTheValidNames(string fieldsJson, string param)
    {
        var dispatcher = await StartAsync();

        using var response = await CallAsync(dispatcher, fieldsJson);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(param, error.GetProperty("param").GetString());
        string hint = error.GetProperty("hint").GetString()!;
        Assert.Contains("sector", hint);
        Assert.Contains("symbol", hint);
        Assert.Contains("timeSeriesLastUpdated", hint);
        Assert.All(TickerMetadataJson.SelectableFieldNames, name => Assert.Contains(name, hint));
    }

    // ---- schema -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSchemaAdvertisesAnOptionalArrayOfStringsWithAtLeastOneItem()
    {
        var dispatcher = await StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement schema = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_get_ticker_metadata").GetProperty("inputSchema");
        JsonElement fields = schema.GetProperty("properties").GetProperty("fields");

        Assert.Equal("array", fields.GetProperty("type").GetString());
        Assert.Equal(1, fields.GetProperty("minItems").GetInt32());
        Assert.Equal("string", fields.GetProperty("items").GetProperty("type").GetString());
        Assert.DoesNotContain("fields", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
