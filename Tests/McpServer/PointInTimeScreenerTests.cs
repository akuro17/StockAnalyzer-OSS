using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using World = StockAnalyzer.Tests.McpServer.ScreenerTestWorld;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener with an <c>asOfDate</c> (plan 02, T2). Without the argument the answer is byte-identical to the answers captured before it existed (golden).
/// With it every symbol is evaluated on its last bar dated on or before the date, nothing stored after the date can change the answer (two data trees, one with every
/// close after the date multiplied by ten), a symbol with no bar at the date is counted apart, a column that is today's stored snapshot is refused (owner decision D-02a),
/// and the saved-template, ticker-group, comparison-symbol and matched-value paths follow the same date.
/// </summary>
public sealed class PointInTimeScreenerTests : IDisposable
{
    // The trees are ScreenerTestWorld.ZigZag() and ScreenerTestWorld.Trend(): UP closes 100 + day index, DOWN 600 - day index, LATE 250 from 2024-12-01.
    private const string EarlyDate = "2024-03-01";   // day index 60: UP closes 160, DOWN 540, LATE has no bar yet
    private const string LateStartDate = "2024-12-01"; // the first bar of LATE (inclusive date)

    private const string CloseUnder300 = """{"left":{"price":"Close"},"comparison":"LessThan","right":300}""";

    /// <summary>The sa_run_screener answers of this data tree, captured from the build before <c>asOfDate</c> existed (three calls).</summary>
    private static readonly string[] ZigZagCalls =
    {
        """{"conditionTree":{"left":{"indicator":"RSI","parameters":{"Period":5}},"comparison":"LessThan","right":80},"maxReturnedMatches":10}""",
        """{"conditionTree":{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":150},{"left":{"column":"Volume"},"comparison":"GreaterThanOrEqual","right":1000}]},"symbols":["MSFT","AAPL"],"includeMatchedValues":true}""",
        """{"conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":20}}},"symbols":["MSFT","AAPL"],"includeMatchedValues":true,"maxReturnedMatches":1}""",
    };

    private static readonly string[] ZigZagGolden =
    {
        """{"timeFrame":"D1","universe":"AllAvailable","groupId":null,"evaluatedSymbolCount":3,"matchedSymbolCount":3,"returnedMatchCount":3,"truncated":false,"treeNodeCount":2,"treeDepth":1,"matchedSymbols":["7203-T","AAPL","MSFT"]}""",
        """{"timeFrame":"D1","universe":"Custom","groupId":null,"evaluatedSymbolCount":2,"matchedSymbolCount":2,"returnedMatchCount":2,"truncated":false,"treeNodeCount":3,"treeDepth":1,"matchedSymbols":["AAPL","MSFT"],"comparisons":[{"id":"L0","path":"conditionTree.children[0]","text":"Close > 150"},{"id":"L1","path":"conditionTree.children[1]","text":"Volume >= 1000"}],"matchedValues":{"AAPL":{"L0":{"left":200,"right":150},"L1":{"left":1000,"right":1000}},"MSFT":{"L0":{"left":200,"right":150},"L1":{"left":1000,"right":1000}}}}""",
        """{"timeFrame":"D1","universe":"Custom","groupId":null,"evaluatedSymbolCount":2,"matchedSymbolCount":2,"returnedMatchCount":1,"truncated":true,"treeNodeCount":2,"treeDepth":1,"matchedSymbols":["AAPL"],"comparisons":[{"id":"L0","path":"conditionTree","text":"Close > SMA(20)"}],"matchedValues":{"AAPL":{"L0":{"left":200,"right":198.0}}}}""",
    };

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private World ZigZagWorld() => Track(ScreenerTestWorld.ZigZag());

    private World TrendWorld(DateOnly? alterAfter = null, WorkspaceSettings? workspace = null) => Track(ScreenerTestWorld.Trend(alterAfter, workspace));

    private World Track(World world)
    {
        _disposables.Add(world);
        return world;
    }

    private static string Tree(string conditionJson, string extra = "") => $$$"""{"conditionTree":{{{conditionJson}}}{{{extra}}}}""";

    private static string[] Matched(JsonElement root) => root.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ---- nothing changes without the argument ------------------------------------------------------------------------------------------

    private static string GoldenText(int index) => ZigZagGolden[index].Replace(">", "\\u003E", StringComparison.Ordinal);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WithoutAnAsOfDateTheAnswerIsByteIdenticalToTheOneCapturedBeforeTheArgumentExisted(int index)
    {
        string answer = await ZigZagWorld().CallAsync("sa_run_screener", ZigZagCalls[index]);

        Assert.Equal(GoldenText(index), answer);
        Assert.DoesNotContain("asOfDate", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("noDataAtAsOfCount", answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnAsOfDateAfterTheLastStoredBarGivesTheAnswerWithoutOneApartFromTheTwoNewKeys(int index)
    {
        string call = ZigZagCalls[index][..^1] + ",\"asOfDate\":\"2030-01-01\"}";

        string answer = await ZigZagWorld().CallAsync("sa_run_screener", call);

        Assert.Contains("\"timeFrame\":\"D1\",\"asOfDate\":\"2030-01-01\",\"universe\"", answer, StringComparison.Ordinal);
        Assert.Contains("\"noDataAtAsOfCount\":0,", answer, StringComparison.Ordinal);
        Assert.Equal(
            GoldenText(index),
            answer.Replace("\"asOfDate\":\"2030-01-01\",", string.Empty, StringComparison.Ordinal).Replace("\"noDataAtAsOfCount\":0,", string.Empty, StringComparison.Ordinal));
    }

    // ---- the meaning of the date -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachSymbolIsEvaluatedOnItsLastBarOnOrBeforeTheDateAndASymbolWithoutOneIsCountedApart()
    {
        World world = TrendWorld();

        using JsonDocument early = JsonDocument.Parse(await world.CallAsync("sa_run_screener", Tree(CloseUnder300, $",\"asOfDate\":\"{EarlyDate}\"")));
        using JsonDocument latest = JsonDocument.Parse(await world.CallAsync("sa_run_screener", Tree(CloseUnder300)));

        JsonElement root = early.RootElement;
        Assert.Equal(EarlyDate, root.GetProperty("asOfDate").GetString());
        Assert.Equal(new[] { "UP" }, Matched(root)); // UP closed 160 that day; DOWN 540
        Assert.Equal(2, root.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(1, root.GetProperty("noDataAtAsOfCount").GetInt32()); // LATE has no bar on or before the date
        Assert.Equal(new[] { "DOWN", "LATE" }, Matched(latest.RootElement)); // the latest bars: UP 499, DOWN 201, LATE 250
        Assert.False(latest.RootElement.TryGetProperty("asOfDate", out _));
        Assert.Equal(3, latest.RootElement.GetProperty("evaluatedSymbolCount").GetInt32());
    }

    [Fact]
    public async Task TheDateIsInclusive_ASymbolWhoseFirstBarIsThatDayIsEvaluated()
    {
        using JsonDocument answer = JsonDocument.Parse(await TrendWorld().CallAsync("sa_run_screener", Tree(CloseUnder300, $",\"asOfDate\":\"{LateStartDate}\"")));

        JsonElement root = answer.RootElement;
        Assert.Equal(0, root.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Equal(3, root.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(new[] { "DOWN", "LATE" }, Matched(root)); // UP 435 on that day, DOWN 265, LATE 250
    }

    [Fact]
    public async Task ADateBeforeEveryFirstBarEvaluatesNothingAndCountsEverySymbol()
    {
        using JsonDocument answer = JsonDocument.Parse(await TrendWorld().CallAsync("sa_run_screener", Tree(CloseUnder300, ",\"asOfDate\":\"2023-12-31\"")));

        JsonElement root = answer.RootElement;
        Assert.Equal(0, root.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(3, root.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Empty(Matched(root));
    }

    [Fact]
    public async Task MatchedValuesAreTheValuesOfTheBarsAtTheDate()
    {
        string answer = await TrendWorld().CallAsync("sa_run_screener", Tree(CloseUnder300, $",\"asOfDate\":\"{EarlyDate}\",\"includeMatchedValues\":true"));

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement values = document.RootElement.GetProperty("matchedValues").GetProperty("UP").GetProperty("L0");
        Assert.Equal(160m, values.GetProperty("left").GetDecimal()); // the close of day 60, not the latest 499
        Assert.Equal(300m, values.GetProperty("right").GetDecimal());
    }

    // ---- nothing after the date can change the answer ----------------------------------------------------------------------------------

    private const string RichCondition =
        """{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"LessThan","right":300},{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":20}}},{"left":{"indicator":"Correlation","parameters":{"Period":10,"ComparisonSymbol":"DOWN"}},"comparison":"LessThan","right":0}]}""";

    [Fact]
    public async Task NothingStoredAfterTheDateInTheSymbolOrInAComparisonSymbolChangesTheAnswerOrItsValues()
    {
        DateOnly asOf = DateOnly.Parse(EarlyDate);
        World truth = TrendWorld();
        World altered = TrendWorld(alterAfter: asOf);
        string arguments = Tree(RichCondition, $",\"asOfDate\":\"{EarlyDate}\",\"includeMatchedValues\":true");

        // the trees must be equal up to the date for the comparison to mean something, and different after it
        Assert.Equal(await truth.Gateway.GetCandlesUpToAsync("UP", TimeFrame.D1, asOf, 10000), await altered.Gateway.GetCandlesUpToAsync("UP", TimeFrame.D1, asOf, 10000));
        Assert.NotEqual(await truth.Gateway.GetCandlesAsync("UP", TimeFrame.D1, 10000), await altered.Gateway.GetCandlesAsync("UP", TimeFrame.D1, 10000));

        string truthAnswer = await truth.CallAsync("sa_run_screener", arguments);
        string alteredAnswer = await altered.CallAsync("sa_run_screener", arguments);

        Assert.Equal(truthAnswer, alteredAnswer);
        using JsonDocument document = JsonDocument.Parse(truthAnswer);
        Assert.Equal(new[] { "UP" }, Matched(document.RootElement)); // the comparison is not vacuous: UP meets all three conditions at the date
        Assert.Equal(3, document.RootElement.GetProperty("comparisons").GetArrayLength());
    }

    [Fact]
    public async Task TheCheckCanFail_WithoutTheDateTheAlteredTreeGivesAnotherAnswer()
    {
        string arguments = Tree(CloseUnder300, ",\"includeMatchedValues\":true"); // the latest bars: DOWN and LATE meet it in the real tree, none does after the alteration

        string truth = await TrendWorld().CallAsync("sa_run_screener", arguments);
        string altered = await TrendWorld(alterAfter: DateOnly.Parse(EarlyDate)).CallAsync("sa_run_screener", arguments);

        Assert.NotEqual(truth, altered);
    }

    // ---- the saved-template and the ticker-group paths ---------------------------------------------------------------------------------

    private static ScreenerIndicatorEntry CloseUnder300Entry() => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = TimeFrame.D1 },
        Operator = ComparisonOperator.LessThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 300m,
        IsEnabled = true,
    };

    private static ScreenerIndicatorEntry ColumnEntry(string column) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { OutputName = column, CategoryType = ScreenerItemCategoryType.Column, TimeFrame = TimeFrame.D1 },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 0m,
        IsEnabled = true,
    };

    [Fact]
    public async Task ASavedTemplateFollowsTheDateLikeTheSameConditionGivenDirectlyAndNothingAfterTheDateChangesIt()
    {
        World truth = TrendWorld();
        World altered = TrendWorld(alterAfter: DateOnly.Parse(EarlyDate));
        Guid id = truth.SaveTemplate(CloseUnder300Entry());
        Assert.Equal(id, altered.SaveTemplate(id, CloseUnder300Entry()));

        string viaTemplate = await truth.CallAsync("sa_run_screener", $$"""{"templateId":"{{id}}","asOfDate":"{{EarlyDate}}"}""");
        string viaTemplateAltered = await altered.CallAsync("sa_run_screener", $$"""{"templateId":"{{id}}","asOfDate":"{{EarlyDate}}"}""");
        string direct = await truth.CallAsync("sa_run_screener", Tree(CloseUnder300, $",\"asOfDate\":\"{EarlyDate}\""));

        Assert.Equal(viaTemplate, viaTemplateAltered);
        using JsonDocument template = JsonDocument.Parse(viaTemplate);
        using JsonDocument tree = JsonDocument.Parse(direct);
        Assert.Equal(new[] { "UP" }, Matched(template.RootElement));
        Assert.Equal(Matched(tree.RootElement), Matched(template.RootElement));
        Assert.Equal(1, template.RootElement.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Equal(EarlyDate, template.RootElement.GetProperty("asOfDate").GetString());
    }

    [Fact]
    public async Task ATickerGroupIsScreenedAsOfTheDateAndTheAnswerWarnsThatItsMembersAreTodays()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Trend", IndicatorColor.FromRgb(0, 0, 255), false, new[]
                {
                    new WatchlistItem("UP", DateTimeOffset.UtcNow),
                    new WatchlistItem("LATE", DateTimeOffset.UtcNow),
                }),
            },
        };
        World world = TrendWorld(workspace: workspace);

        using JsonDocument answer = JsonDocument.Parse(await world.CallAsync("sa_run_screener", Tree(CloseUnder300, $",\"groupId\":\"{watchlistId}\",\"asOfDate\":\"{EarlyDate}\"")));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP" }, Matched(root));
        Assert.Equal(1, root.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(1, root.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Contains(root.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("members of the group are those of today", StringComparison.Ordinal));
    }

    // ---- D-02a: a snapshot column is refused with a date -------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"left":{"column":"Market Cap"},"comparison":"GreaterThan","right":0}""", "conditionTree.left.column")]
    [InlineData("""{"left":{"column":"Sector"},"comparison":"Equal","right":"Technology"}""", "conditionTree.left.column")]
    [InlineData("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"column":"Book Value"}}""", "conditionTree.right.column")]
    [InlineData("""{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":0},{"left":{"column":"P/B Ratio"},"comparison":"LessThan","right":1}]}""", "conditionTree.children[1].left.column")]
    public async Task ASnapshotColumnWithADateIsInvalidArgumentNamingTheSideAndListingTheColumnsThatRemain(string conditionJson, string parameter)
    {
        using JsonDocument response = await TrendWorld().CallRawAsync("sa_run_screener", Tree(conditionJson, $",\"asOfDate\":\"{EarlyDate}\""));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(parameter, error.GetProperty("param").GetString());
        string hint = error.GetProperty("hint").GetString()!;
        foreach (string name in new[] { "Open", "High", "Low", "Close", "Volume", "Change", "Change %" })
        {
            Assert.Contains(name, hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSameSnapshotColumnWithoutADateIsStillAccepted()
    {
        using JsonDocument response = await TrendWorld().CallRawAsync("sa_run_screener", Tree("""{"left":{"column":"Market Cap"},"comparison":"GreaterThan","right":0}"""));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
    }

    [Theory]
    [InlineData("Close")]
    [InlineData("Volume")]
    [InlineData("Change %")]
    public async Task AColumnReadFromTheBarsStaysAvailableWithADate(string column)
    {
        using JsonDocument response = await TrendWorld().CallRawAsync("sa_run_screener", Tree($$$"""{"left":{"column":"{{{column}}}"},"comparison":"GreaterThan","right":-1000}""", $",\"asOfDate\":\"{EarlyDate}\""));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
    }

    [Fact]
    public void TheColumnsReadFromTheBarsAreExactlyOpenHighLowCloseVolumeChangeAndChangePercent()
    {
        var catalog = new ScreenerCatalog(new ScreenerCatalogProvider(), new IndicatorFactory());

        IReadOnlyList<string> allowed = new ScreenerAsOfColumnGuard(catalog).AllowedColumns;

        // A column added to the registry is a snapshot until someone decides otherwise: this list must then be changed on purpose.
        Assert.Equal(new[] { "Change", "Change %", "Close", "High", "Low", "Open", "Volume" }, allowed.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(catalog.Columns.Count > allowed.Count);
    }

    [Fact]
    public async Task ASavedTemplateWithASnapshotColumnIsRefusedWithADateNamingTheEntrySide()
    {
        World world = TrendWorld();
        Guid id = world.SaveTemplate(CloseUnder300Entry(), ColumnEntry("Market Cap"));

        using JsonDocument refused = await world.CallRawAsync("sa_run_screener", $$"""{"templateId":"{{id}}","asOfDate":"{{EarlyDate}}"}""");
        using JsonDocument accepted = await world.CallRawAsync("sa_run_screener", $$"""{"templateId":"{{id}}"}""");

        Assert.True(McpTestSupport.IsError(refused));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(refused));
        Assert.Equal("entries[1].leftHand", McpTestSupport.Structured(refused).GetProperty("param").GetString());
        Assert.False(McpTestSupport.IsError(accepted), McpTestSupport.Structured(accepted).ToString());
    }

    // ---- argument names and format -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EndDateIsRefusedWithAHintThatNamesAsOfDate()
    {
        using JsonDocument response = await TrendWorld().CallRawAsync("sa_run_screener", Tree(CloseUnder300, ",\"endDate\":\"2024-03-01\""));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("endDate", error.GetProperty("param").GetString());
        string hint = error.GetProperty("hint").GetString()!;
        Assert.Contains("asOfDate", hint, StringComparison.Ordinal);
        Assert.Contains("endDate", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2024/03/01")]
    [InlineData("01-03-2024")]
    [InlineData("tomorrow")]
    public async Task AnAsOfDateInAnotherFormIsInvalidArgumentNamingIt(string date)
    {
        using JsonDocument response = await TrendWorld().CallRawAsync("sa_run_screener", Tree(CloseUnder300, $",\"asOfDate\":\"{date}\""));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("asOfDate", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task TheDescriptionStatesTheDateTheSurvivorshipLimitAndTheColumnRule()
    {
        World world = TrendWorld();

        using JsonDocument listing = await world.RequestAsync("tools/list");

        string description = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "sa_run_screener").GetProperty("description").GetString()!;
        Assert.Contains("asOfDate", description, StringComparison.Ordinal);
        Assert.Contains("noDataAtAsOfCount", description, StringComparison.Ordinal);
        Assert.Contains("survivorship", description, StringComparison.Ordinal);
        Assert.Contains("stored snapshot", description, StringComparison.Ordinal);
    }

    // ---- the gateway and its as-of data service ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("D1")]
    [InlineData("W1")]
    [InlineData("MN1")]
    public async Task TheScreenerBarsDataServiceServesTheSameBarsAsTheGatewayReadsUpToTheDate(string frame)
    {
        World world = TrendWorld();
        TimeFrame timeFrame = Enum.Parse<TimeFrame>(frame);
        DateOnly date = DateOnly.Parse("2024-12-18"); // a Wednesday inside a week and a month: the straddling bars hold only the days up to it
        var service = new ScreenerBarsDataService(world.DataService, new ScreenerBarSelection(date, false));

        IReadOnlyList<CandleData> served = await service.LoadCandlesAsync("UP", timeFrame, 100);

        Assert.Equal(await world.Gateway.GetCandlesUpToAsync("UP", timeFrame, date, 100), served);
        Assert.All(served, bar => Assert.True(DateOnly.FromDateTime(bar.Timestamp) <= date));
        Assert.Empty(service.SymbolsWithoutBars);
    }

    [Fact]
    public async Task TheScreenerBarsDataServiceHonoursTheCountAndRemembersTheSymbolsWithoutABar()
    {
        World world = TrendWorld();
        var service = new ScreenerBarsDataService(world.DataService, new ScreenerBarSelection(DateOnly.Parse(EarlyDate), false));

        IReadOnlyList<CandleData> few = await service.LoadCandlesAsync("UP", TimeFrame.D1, 5);
        IReadOnlyList<CandleData> all = await service.LoadCandlesAsync("DOWN", TimeFrame.D1, 0);
        IReadOnlyList<CandleData> none = await service.LoadCandlesAsync("LATE", TimeFrame.D1, 5);

        Assert.Equal(5, few.Count);
        Assert.Equal(DateOnly.Parse(EarlyDate), DateOnly.FromDateTime(few[^1].Timestamp));
        Assert.Equal(61, all.Count); // 2024-01-01 .. 2024-03-01
        Assert.Empty(none);
        Assert.Equal(new[] { "LATE" }, service.SymbolsWithoutBars);
    }

    // ---- hosting -----------------------------------------------------------------------------------------------------------------------

}
