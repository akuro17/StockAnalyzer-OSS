using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener with <c>sortBy</c> / <c>sortDirection</c> (plan 02, T3, owner decision D-02e). The data tree is ScreenerTestWorld.Trend: on the latest bars UP closes 499,
/// DOWN 201 and LATE 250 (LATE has only 65 bars). The order rules (section 6.2): the larger value first for Descending, the smaller for Ascending, a symbol without a
/// value last in both directions, equal values by ascending symbol, <c>maxReturnedMatches</c> keeps the first ranks, and without sortBy nothing changes.
/// </summary>
public sealed class ScreenerRankingTests : IDisposable
{
    private const string MatchAll = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""";

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private ScreenerTestWorld Trend()
    {
        ScreenerTestWorld world = ScreenerTestWorld.Trend();
        _disposables.Add(world);
        return world;
    }

    private static string Call(string conditionJson, string extra = "") => $$$"""{"conditionTree":{{{conditionJson}}}{{{extra}}}}""";

    private static string[] Symbols(JsonElement root) => root.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Ranked(JsonElement root) => root.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!).ToArray();

    private static decimal?[] SortValues(JsonElement root)
        => root.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("sortValue").ValueKind == JsonValueKind.Null ? (decimal?)null : e.GetProperty("sortValue").GetDecimal()).ToArray();

    private async Task<JsonDocument> RunAsync(string extra, string condition = MatchAll)
        => JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(condition, extra)));

    // ---- order ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DescendingIsTheDefaultAndPutsTheLargestValueFirst()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"}""");

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP", "LATE", "DOWN" }, Ranked(root));
        Assert.Equal(new decimal?[] { 499m, 250m, 201m }, SortValues(root));
        Assert.Equal(Ranked(root), Symbols(root)); // matchedSymbols follow the ranking
        Assert.Equal(new[] { 1, 2, 3 }, root.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("rank").GetInt32()).ToArray());
    }

    [Fact]
    public async Task AscendingPutsTheSmallestValueFirst()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"},"sortDirection":"Ascending" """.Trim());

        Assert.Equal(new[] { "DOWN", "LATE", "UP" }, Ranked(answer.RootElement));
        Assert.Equal(new decimal?[] { 201m, 250m, 499m }, SortValues(answer.RootElement));
    }

    [Fact]
    public async Task WithoutSortByTheListStaysAscendingBySymbolAndHasNoRankingKey()
    {
        using JsonDocument answer = await RunAsync(string.Empty);

        Assert.Equal(new[] { "DOWN", "LATE", "UP" }, Symbols(answer.RootElement));
        Assert.False(answer.RootElement.TryGetProperty("ranking", out _));
    }

    [Theory]
    [InlineData("Descending")]
    [InlineData("Ascending")]
    public async Task EqualValuesAreInAscendingSymbolOrderWhicheverTheDirection(string direction)
    {
        // Volume is 1000 on every bar of every symbol.
        using JsonDocument answer = await RunAsync($$$""","sortBy":{"column":"Volume"},"sortDirection":"{{{direction}}}" """.Trim());

        Assert.Equal(new[] { "DOWN", "LATE", "UP" }, Ranked(answer.RootElement));
        Assert.All(SortValues(answer.RootElement), value => Assert.Equal(1000m, value));
    }

    [Theory]
    [InlineData("Descending", new[] { "UP", "DOWN", "LATE" })]
    [InlineData("Ascending", new[] { "DOWN", "UP", "LATE" })]
    public async Task ASymbolWithoutAValueIsLastInBothDirections(string direction, string[] expected)
    {
        // SMA(100) needs 100 bars: LATE has 65, so its value is null.
        using JsonDocument answer = await RunAsync($$$""","sortBy":{"indicator":"SMA","parameters":{"Period":100}},"sortDirection":"{{{direction}}}" """.Trim());

        JsonElement root = answer.RootElement;
        Assert.Equal(expected, Ranked(root));
        decimal?[] values = SortValues(root);
        Assert.Null(values[^1]);
        Assert.All(values[..^1], value => Assert.NotNull(value));
        Assert.Equal(3, root.GetProperty("matchedSymbolCount").GetInt32());
    }

    [Fact]
    public async Task ASingleMatchWithoutAValueIsRankOneWithANullSortValue()
    {
        using JsonDocument answer = await RunAsync(""","symbols":["LATE"],"sortBy":{"price":"Close","offset":70}""");

        JsonElement ranking = answer.RootElement.GetProperty("ranking");
        Assert.Equal(1, ranking.GetArrayLength());
        Assert.Equal(1, ranking[0].GetProperty("rank").GetInt32());
        Assert.Equal("LATE", ranking[0].GetProperty("symbol").GetString());
        Assert.Equal(JsonValueKind.Null, ranking[0].GetProperty("sortValue").ValueKind);
    }

    [Fact]
    public async Task TheOffsetOfTheSortSideIsApplied()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close","offset":1}""");

        Assert.Equal(new[] { "UP", "LATE", "DOWN" }, Ranked(answer.RootElement));
        Assert.Equal(new decimal?[] { 498m, 250m, 202m }, SortValues(answer.RootElement)); // the bar before the latest: UP 498, LATE 250 (constant), DOWN 202
    }

    // ---- the list -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MaxReturnedMatchesKeepsTheFirstRanksAndTheCountStaysTrue()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"},"maxReturnedMatches":2""");

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP", "LATE" }, Symbols(root));
        Assert.Equal(new[] { "UP", "LATE" }, Ranked(root));
        Assert.Equal(3, root.GetProperty("matchedSymbolCount").GetInt32());
        Assert.Equal(2, root.GetProperty("returnedMatchCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task OnlyMatchedSymbolsAreRanked()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"}""", condition: """{"left":{"price":"Close"},"comparison":"LessThan","right":300}""");

        Assert.Equal(new[] { "LATE", "DOWN" }, Ranked(answer.RootElement)); // UP closes 499 and does not match
    }

    [Fact]
    public async Task NoMatchGivesAnEmptyRanking()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"}""", condition: """{"left":{"price":"Close"},"comparison":"LessThan","right":0}""");

        Assert.Equal(0, answer.RootElement.GetProperty("ranking").GetArrayLength());
        Assert.Empty(Symbols(answer.RootElement));
    }

    [Fact]
    public async Task RankingAndMatchedValuesCanBeAskedTogether()
    {
        using JsonDocument answer = await RunAsync(""","sortBy":{"price":"Close"},"includeMatchedValues":true,"maxReturnedMatches":2""");

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP", "LATE" }, Ranked(root));
        Assert.Equal(new[] { "UP", "LATE" }, root.GetProperty("matchedValues").EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---- the other paths -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASortSideThatNamesAComparisonSymbolIsRankedWithThatSymbolsBars()
    {
        using JsonDocument answer = await RunAsync(
            ""","symbols":["UP","DOWN"],"sortBy":{"indicator":"Correlation","parameters":{"Period":10,"ComparisonSymbol":"UP"}}""");

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP", "DOWN" }, Ranked(root)); // UP with itself +1, DOWN against UP -1
        decimal?[] values = SortValues(root);
        Assert.InRange(values[0]!.Value, 0.99m, 1.01m);
        Assert.InRange(values[1]!.Value, -1.01m, -0.99m);
    }

    [Fact]
    public async Task ASavedTemplateCanBeRanked()
    {
        ScreenerTestWorld world = Trend();
        Guid id = world.SaveTemplate(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = TimeFrame.D1 },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            IsEnabled = true,
        });

        using JsonDocument answer = JsonDocument.Parse(await world.CallAsync("sa_run_screener", $$"""{"templateId":"{{id}}","sortBy":{"price":"Close"},"sortDirection":"Ascending"}"""));

        Assert.Equal(new[] { "DOWN", "LATE", "UP" }, Ranked(answer.RootElement));
    }

    [Fact]
    public async Task WithAnAsOfDateTheRankingUsesTheValuesAtTheDate()
    {
        using JsonDocument answer = await RunAsync(""","asOfDate":"2024-03-01","sortBy":{"price":"Close"}""", condition: """{"left":{"price":"Close"},"comparison":"LessThan","right":1000}""");

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "DOWN", "UP" }, Ranked(root)); // day 60: DOWN 540, UP 160; LATE has no bar yet
        Assert.Equal(new decimal?[] { 540m, 160m }, SortValues(root));
    }

    [Fact]
    public async Task ASnapshotColumnAsTheSortSideIsRefusedWithADateButNotWithout()
    {
        ScreenerTestWorld world = Trend();

        using JsonDocument refused = JsonDocument.Parse(await Capture(world, Call(MatchAll, ""","asOfDate":"2024-03-01","sortBy":{"column":"Market Cap"}""")));
        using JsonDocument accepted = JsonDocument.Parse(await Capture(world, Call(MatchAll, ""","sortBy":{"column":"Market Cap"}""")));

        Assert.Equal(ToolErrorCodes.InvalidArgument, refused.RootElement.GetProperty("code").GetString());
        Assert.Equal("sortBy.column", refused.RootElement.GetProperty("param").GetString());
        Assert.False(accepted.RootElement.TryGetProperty("code", out _));
        Assert.Equal(3, accepted.RootElement.GetProperty("ranking").GetArrayLength()); // no stored metadata: every value is null, the order is by symbol
        Assert.All(SortValues(accepted.RootElement), value => Assert.Null(value));
    }

    private static async Task<string> Capture(ScreenerTestWorld world, string arguments)
    {
        using JsonDocument response = await world.CallRawAsync("sa_run_screener", arguments);
        return McpTestSupport.Structured(response).GetRawText();
    }

    // ---- refusals --------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(""","sortBy":{"criteria":"Gartley Pattern"}""", "sortBy.criteria")]
    [InlineData(""","sortBy":{"column":"Sector"}""", "sortBy.column")]
    [InlineData(""","sortBy":{"price":"Nope"}""", "sortBy.price")]
    [InlineData(""","sortBy":{"price":"Close","indicator":"SMA"}""", "sortBy")]
    [InlineData(""","sortBy":{"price":"Close","offset":-1}""", "sortBy.offset")]
    [InlineData(""","sortDirection":"Ascending" """, "sortDirection")]
    [InlineData(""","sortBy":{"price":"Close"},"sortDirection":"Up" """, "sortDirection")]
    public async Task ASortRequestThatCannotRankIsInvalidArgumentNamingThePart(string extra, string parameter)
    {
        using JsonDocument response = await Trend().CallRawAsync("sa_run_screener", Call(MatchAll, extra.Trim()));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    // ---- the order rule on its own -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void RankOrdersByValueThenSymbolWithMissingValuesLast()
    {
        var values = new Dictionary<string, decimal?>
        {
            ["B"] = 2m, ["A"] = 2m, ["C"] = -3m, ["D"] = null, ["E"] = null, ["F"] = 10m,
        };

        Assert.Equal(new[] { "F", "A", "B", "C", "D", "E" }, RunScreenerTool.Rank(values, descending: true));
        Assert.Equal(new[] { "C", "A", "B", "F", "D", "E" }, RunScreenerTool.Rank(values, descending: false));
    }

    [Fact]
    public void RankOfNoSymbolsIsEmptyAndOfOnlyMissingValuesIsBySymbol()
    {
        Assert.Empty(RunScreenerTool.Rank(new Dictionary<string, decimal?>(), descending: true));
        Assert.Equal(new[] { "A", "B" }, RunScreenerTool.Rank(new Dictionary<string, decimal?> { ["B"] = null, ["A"] = null }, descending: false));
    }
}
