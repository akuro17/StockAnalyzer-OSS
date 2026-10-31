using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener with a <c>timeFrame</c> on a side (plan "Screener per-side time frame", T6). The data tree is ScreenerTestWorld.Trend: one bar per calendar day from Monday
/// 2024-01-01 to Monday 2025-02-03; UP closes 100 + day index, DOWN 600 - day index, LATE a constant 250 from 2024-12-01. The numbers are worked out from that arithmetic:
/// the last day (index 399, 2025-02-03) is the only day of a partial week, the last complete week ends Sunday 2025-02-02 (index 398, UP 498), the week before it ends on
/// index 391 (UP 491), and so on in steps of seven. DOWN's weekly closes are 600 - index (202, 209, ...). A comparison of the daily close with a weekly value separates the
/// frames: the daily close of UP is 499 and its last complete weekly close 498, so "daily Close greater than weekly Close" is true only when the two sides read different frames.
/// </summary>
public sealed class ScreenerPerSideTimeFrameTests : IDisposable
{
    private const string DailyAboveWeeklyClose = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"W1"}}""";

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private ScreenerTestWorld Trend(DateOnly? alterAfter = null)
    {
        ScreenerTestWorld world = ScreenerTestWorld.Trend(alterAfter);
        _disposables.Add(world);
        return world;
    }

    private static string Call(string conditionJson, string extra) => $$$"""{"conditionTree":{{{conditionJson}}}{{{extra}}}}""";

    private static string[] Symbols(JsonElement root) => root.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static (decimal Left, decimal Right) Values(JsonElement root, string symbol)
    {
        JsonElement pair = root.GetProperty("matchedValues").GetProperty(symbol).GetProperty("L0");
        return (pair.GetProperty("left").GetDecimal(), pair.GetProperty("right").GetDecimal());
    }

    private static JsonElement FrameReport(JsonElement root, string frame)
        => root.GetProperty("sideFrames").EnumerateArray().Single(report => report.GetProperty("timeFrame").GetString() == frame);

    private static void AssertNoSingleFramePartialKeys(JsonElement root)
    {
        Assert.False(root.TryGetProperty("partialLastBar", out _));
        Assert.False(root.TryGetProperty("partialPeriodStarts", out _));
        Assert.False(root.TryGetProperty("skippedPartialBarCount", out _));
    }

    private static async Task<(string Code, string Param)> ErrorOfAsync(ScreenerTestWorld world, string arguments)
    {
        using JsonDocument response = await world.CallRawAsync("sa_run_screener", arguments);
        Assert.True(McpTestSupport.IsError(response), "the call was expected to fail");
        return (McpTestSupport.ErrorCodeOf(response), McpTestSupport.Structured(response).GetProperty("param").GetString()!);
    }

    private static ScreenerIndicatorEntry PriceEntry(TimeFrame left, TimeFrame right) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = left },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        RightHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = right },
        IsEnabled = true,
    };

    // ---- a condition that mixes frames ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADailySideAgainstAWeeklySideReadsEachSideOnItsOwnFrame()
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(DailyAboveWeeklyClose, ",\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP" }, Symbols(root)); // 499 (daily) > 498 (last complete week); DOWN: 201 is not above 202
        Assert.Equal((499m, 498m), Values(root, "UP"));
        Assert.Equal("Close > Close [W1]", root.GetProperty("comparisons")[0].GetProperty("text").GetString());
        Assert.Equal("D1", root.GetProperty("timeFrame").GetString());
        Assert.True(root.GetProperty("completeBarsOnly").GetBoolean());
        JsonElement weekly = FrameReport(root, "W1");
        Assert.False(weekly.GetProperty("partialLastBar").GetBoolean());
        Assert.Equal(2, weekly.GetProperty("skippedPartialBarCount").GetInt32()); // the week of 2025-02-03 holds one day in both symbols
        Assert.Equal(0, weekly.GetProperty("symbolsWithoutBarsCount").GetInt32());
        Assert.Single(root.GetProperty("sideFrames").EnumerateArray());
        AssertNoSingleFramePartialKeys(root);
    }

    [Fact]
    public async Task AnExpandedNameKeepsTheTimeFrameOfItsSideOnEveryComparisonItBecomes()
    {
        // Plan 05 / T2 with the per-side time frame: Rising 2 on a weekly close is two weekly comparisons (UP's last complete weekly closes are 498, 491, 484).
        const string risingWeeks = """{"left":{"price":"Close","timeFrame":"W1"},"comparison":"Rising","bars":2}""";
        const string writtenOut = """{"operator":"And","children":[{"left":{"price":"Close","timeFrame":"W1"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"W1","offset":1}},{"left":{"price":"Close","timeFrame":"W1","offset":1},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"W1","offset":2}}]}""";

        using JsonDocument sugared = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(risingWeeks, ",\"symbols\":[\"UP\",\"DOWN\"]")));
        using JsonDocument written = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(writtenOut, ",\"symbols\":[\"UP\",\"DOWN\"]")));

        Assert.Equal(new[] { "UP" }, Symbols(sugared.RootElement));
        Assert.Equal(Symbols(written.RootElement), Symbols(sugared.RootElement));
        Assert.Equal(FrameReport(written.RootElement, "W1").GetRawText(), FrameReport(sugared.RootElement, "W1").GetRawText());
        Assert.Single(sugared.RootElement.GetProperty("sideFrames").EnumerateArray());
    }

    [Fact]
    public async Task WithCompleteBarsOnlyFalseTheWeeklySideReadsThePartialWeekAndNamesIt()
    {
        const string atLeastTheWeeklyClose = """{"left":{"price":"Close"},"comparison":"GreaterThanOrEqual","right":{"price":"Close","timeFrame":"W1"}}""";

        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(atLeastTheWeeklyClose, ",\"completeBarsOnly\":false,\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP" }, Symbols(root));
        Assert.Equal((499m, 499m), Values(root, "UP")); // the partial week closes at 499, the same day as the daily bar (the complete week closed at 498)
        Assert.False(root.GetProperty("completeBarsOnly").GetBoolean());
        JsonElement weekly = FrameReport(root, "W1");
        Assert.True(weekly.GetProperty("partialLastBar").GetBoolean());
        Assert.Equal(new[] { "2025-02-03" }, weekly.GetProperty("partialPeriodStarts").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(weekly.TryGetProperty("skippedPartialBarCount", out _));
    }

    [Fact]
    public async Task ASideWithoutAFrameReadsTheToolLevelFrameWhenAnotherSideNamesOne()
    {
        string condition = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","timeFrame":"D1"}}""";

        using JsonDocument mixed = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(condition, ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true")));
        using JsonDocument scanFrame = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call("""{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close"}}""", ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\",\"DOWN\"]")));

        // The left side is weekly (498), the right side daily (499): true for UP. Without any side frame the whole scan is weekly: 498 < 498 is false.
        Assert.Equal(new[] { "UP" }, Symbols(mixed.RootElement));
        Assert.Equal((498m, 499m), Values(mixed.RootElement, "UP"));
        Assert.Equal("W1", mixed.RootElement.GetProperty("timeFrame").GetString());
        Assert.Empty(Symbols(scanFrame.RootElement));
        Assert.False(scanFrame.RootElement.TryGetProperty("sideFrames", out _));
    }

    [Fact]
    public async Task AnOffsetCountsBarsOfTheSidesOwnFrame()
    {
        const string condition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"W1","offset":1}}""";

        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(condition, ",\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true")));

        // One weekly bar back is the week ending Sunday 2025-01-26 (index 391, close 491); one daily bar back would be 498.
        Assert.Equal(new[] { "UP" }, Symbols(answer.RootElement));
        Assert.Equal((499m, 491m), Values(answer.RootElement, "UP"));
    }

    [Fact]
    public async Task ASideWithAFrameOfItsOwnGivesTheAnswerOfTheSameConditionOnTheToolLevelFrame()
    {
        const string onTheSide = """{"left":{"price":"Close","timeFrame":"W1"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":5},"timeFrame":"W1"}}""";
        const string onTheTool = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":5}}}""";

        foreach (bool completeBarsOnly in new[] { true, false })
        {
            string extra = $",\"completeBarsOnly\":{(completeBarsOnly ? "true" : "false")},\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true";
            using JsonDocument side = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(onTheSide, extra)));
            using JsonDocument tool = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(onTheTool, ",\"timeFrame\":\"W1\"" + extra)));

            Assert.Equal(Symbols(tool.RootElement), Symbols(side.RootElement));
            Assert.Equal(Values(tool.RootElement, "UP"), Values(side.RootElement, "UP"));
            Assert.Equal(completeBarsOnly ? (498m, 484m) : (499m, 489.8m), Values(side.RootElement, "UP"));
            JsonElement report = FrameReport(side.RootElement, "W1");
            Assert.Equal(tool.RootElement.GetProperty("partialLastBar").GetBoolean(), report.GetProperty("partialLastBar").GetBoolean());
            if (completeBarsOnly)
            {
                Assert.Equal(tool.RootElement.GetProperty("skippedPartialBarCount").GetInt32(), report.GetProperty("skippedPartialBarCount").GetInt32());
            }
        }
    }

    [Fact]
    public async Task AMonthlySideIsReadFromMonthlyBars()
    {
        const string condition = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"MN1"}}""";

        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(condition, ",\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        // January 2025 ended on index 396 (close 496); February 2025 is partial and left out.
        Assert.Equal((499m, 496m), Values(answer.RootElement, "UP"));
        Assert.Equal(1, FrameReport(answer.RootElement, "MN1").GetProperty("skippedPartialBarCount").GetInt32());
        Assert.DoesNotContain(answer.RootElement.GetProperty("sideFrames").EnumerateArray(), report => report.GetProperty("timeFrame").GetString() == "W1");
    }

    [Fact]
    public async Task ASymbolWithNoMonthlyBarIsNamedInTheFrameReportAndDoesNotMatch()
    {
        // LATE starts 2024-12-01: as of 2024-12-15 its only month is partial, so with completeBarsOnly it has no monthly bar; UP's last complete month is November (index 334, close 434).
        const string dailyAboveMonthlyClose = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"MN1"}}""";
        string extra = ",\"asOfDate\":\"2024-12-15\",\"symbols\":[\"LATE\",\"UP\"],\"includeMatchedValues\":true";
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(dailyAboveMonthlyClose, extra)));

        JsonElement root = answer.RootElement;
        JsonElement monthly = FrameReport(root, "MN1");
        Assert.Equal(new[] { "UP" }, Symbols(root));
        Assert.Equal((449m, 434m), Values(root, "UP"));
        Assert.Equal(1, monthly.GetProperty("symbolsWithoutBarsCount").GetInt32());
        Assert.Equal(new[] { "LATE" }, monthly.GetProperty("symbolsWithoutBars").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(2, monthly.GetProperty("skippedPartialBarCount").GetInt32()); // December is partial in both
    }

    // ---- a date, a ranking, a saved template ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAsOfDateCutsTheDailyBarsBeforeTheWeeksAreBuilt()
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(DailyAboveWeeklyClose, ",\"asOfDate\":\"2024-12-18\",\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        // Wednesday 2024-12-18 is index 352 (close 452); the last complete week ended Sunday 2024-12-15 (index 349, close 449).
        Assert.Equal("2024-12-18", answer.RootElement.GetProperty("asOfDate").GetString());
        Assert.Equal((452m, 449m), Values(answer.RootElement, "UP"));
    }

    [Fact]
    public async Task NothingStoredAfterTheDateChangesAMixedFrameAnswerOrItsValuesOrItsRanking()
    {
        const string asOf = "2024-12-18";
        string arguments = Call(
            DailyAboveWeeklyClose,
            $",\"asOfDate\":\"{asOf}\",\"includeMatchedValues\":true,\"sortBy\":{{\"price\":\"Close\",\"timeFrame\":\"W1\"}}");
        ScreenerTestWorld truth = Trend();
        ScreenerTestWorld altered = Trend(alterAfter: DateOnly.Parse(asOf));

        string truthAnswer = await truth.CallAsync("sa_run_screener", arguments);
        string alteredAnswer = await altered.CallAsync("sa_run_screener", arguments);

        Assert.Equal(truthAnswer, alteredAnswer);
        using JsonDocument document = JsonDocument.Parse(truthAnswer);
        Assert.Contains("UP", Symbols(document.RootElement)); // not vacuous
        Assert.Equal(449m, document.RootElement.GetProperty("ranking")[0].GetProperty("sortValue").GetDecimal());
    }

    [Fact]
    public async Task TheCheckCanFail_WithoutTheDateTheAlteredTreeGivesAnotherMixedFrameAnswer()
    {
        string arguments = Call(DailyAboveWeeklyClose, ",\"includeMatchedValues\":true");

        string truth = await Trend().CallAsync("sa_run_screener", arguments);
        string altered = await Trend(alterAfter: DateOnly.Parse("2024-12-18")).CallAsync("sa_run_screener", arguments);

        Assert.NotEqual(truth, altered);
    }

    [Fact]
    public async Task SortByReadsItsValueFromItsOwnFrame()
    {
        const string matchAll = """{"left":{"price":"Close","timeFrame":"D1"},"comparison":"GreaterThan","right":0}""";

        using JsonDocument weekly = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(matchAll, ",\"symbols\":[\"UP\",\"DOWN\",\"LATE\"],\"sortBy\":{\"price\":\"Close\",\"timeFrame\":\"W1\"}")));
        using JsonDocument daily = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(matchAll, ",\"symbols\":[\"UP\",\"DOWN\",\"LATE\"],\"sortBy\":{\"price\":\"Close\"}")));

        decimal[] Ranked(JsonDocument answer) => answer.RootElement.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("sortValue").GetDecimal()).ToArray();
        Assert.Equal(new[] { "UP", "LATE", "DOWN" }, Symbols(weekly.RootElement));
        Assert.Equal(new[] { 498m, 250m, 202m }, Ranked(weekly)); // the last complete weekly closes
        Assert.Equal(new[] { 499m, 250m, 201m }, Ranked(daily)); // the daily closes
    }

    [Fact]
    public async Task ASortByThatNamesAFrameMovesTheWholeScanToSidesOfTheirOwnFrames()
    {
        // The condition names no frame; the sortBy does, so the condition's sides read the literal tool-level frame (W1 here) and the sortBy its own (D1).
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener",
            Call("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""", ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\"],\"includeMatchedValues\":true,\"sortBy\":{\"price\":\"Close\",\"timeFrame\":\"D1\"}")));

        JsonElement root = answer.RootElement;
        Assert.Equal(498m, root.GetProperty("matchedValues").GetProperty("UP").GetProperty("L0").GetProperty("left").GetDecimal());
        Assert.Equal(499m, root.GetProperty("ranking")[0].GetProperty("sortValue").GetDecimal());
        Assert.Equal(1, FrameReport(root, "W1").GetProperty("skippedPartialBarCount").GetInt32()); // the condition's weekly side skipped the partial week of the one symbol
    }

    [Fact]
    public async Task ASavedTemplateWithAWeeklySideReadsItsStoredFrames()
    {
        ScreenerTestWorld world = Trend();
        Guid id = world.SaveTemplate(PriceEntry(TimeFrame.D1, TimeFrame.W1));

        using JsonDocument answer = JsonDocument.Parse(await world.CallAsync(
            "sa_run_screener", $$$"""{"templateId":"{{{id}}}","symbols":["UP","DOWN"],"includeMatchedValues":true}"""));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP" }, Symbols(root));
        Assert.Equal((499m, 498m), Values(root, "UP"));
        Assert.Equal(2, FrameReport(root, "W1").GetProperty("skippedPartialBarCount").GetInt32());
        AssertNoSingleFramePartialKeys(root);
        Assert.False(root.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task ASavedTemplateWithAWeeklySideRefusesAToolLevelFrameThatHasNoSideToApplyTo()
    {
        ScreenerTestWorld world = Trend();
        Guid id = world.SaveTemplate(PriceEntry(TimeFrame.D1, TimeFrame.W1));

        (string code, string param) = await ErrorOfAsync(world, $$$"""{"templateId":"{{{id}}}","timeFrame":"W1"}""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("timeFrame", param);
    }

    [Fact]
    public async Task ASavedTemplateOfDailySidesStillTakesTheToolLevelFrameAsTheScanFrame()
    {
        ScreenerTestWorld world = Trend();
        Guid id = world.SaveTemplate(PriceEntry(TimeFrame.D1, TimeFrame.D1));

        // Daily Close > daily Close is never true; on the weekly scan frame it is also never true, but the answer carries the single-frame partial keys, as before.
        using JsonDocument answer = JsonDocument.Parse(await world.CallAsync("sa_run_screener", $$$"""{"templateId":"{{{id}}}","timeFrame":"W1","symbols":["UP"]}"""));

        Assert.True(answer.RootElement.TryGetProperty("skippedPartialBarCount", out _));
        Assert.False(answer.RootElement.TryGetProperty("sideFrames", out _));
    }

    // ---- no side names a frame: the answer is the one it always was ----------------------------------------------------------------------

    [Fact]
    public async Task WithoutASideFrameTheAnswerHasNoFrameReport()
    {
        using JsonDocument daily = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""", ",\"symbols\":[\"UP\"]")));
        using JsonDocument weekly = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call("""{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""", ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\"]")));

        Assert.False(daily.RootElement.TryGetProperty("sideFrames", out _));
        Assert.False(weekly.RootElement.TryGetProperty("sideFrames", out _));
        Assert.True(weekly.RootElement.GetProperty("skippedPartialBarCount").GetInt32() >= 0);
    }

    // ---- what is refused ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"column":"Sector","timeFrame":"D1"}""", "Equal", "\"Tech\"")]            // a text column
    [InlineData("""{"column":"Trailing P/E","timeFrame":"W1"}""", "GreaterThan", "10")]      // a numeric column read from the stored metadata
    public async Task AFrameOnAColumnReadFromTheStoredMetadataIsInvalidArgument(string left, string comparison, string right)
    {
        (string code, string param) = await ErrorOfAsync(Trend(), $$$"""{"conditionTree":{"left":{{{left}}},"comparison":"{{{comparison}}}","right":{{{right}}}}}""");

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.left.timeFrame", param);
    }

    [Theory]
    [InlineData("H1")]
    [InlineData("banana")]
    [InlineData("W")]
    public async Task AFrameThatTheDataLayerCannotLoadOrIsNotAFrameIsInvalidArgumentNamingTheSide(string frame)
    {
        (string code, string param) = await ErrorOfAsync(Trend(), Call($$$"""{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"{{{frame}}}"}}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.right.timeFrame", param);
    }

    [Fact]
    public async Task AFrameThatIsNotAStringIsInvalidArgument()
    {
        (string code, string param) = await ErrorOfAsync(Trend(), Call("""{"left":{"price":"Close","timeFrame":7},"comparison":"GreaterThan","right":0}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.left.timeFrame", param);
    }

    [Fact]
    public async Task AFrameOtherThanDailyOnATwoSymbolIndicatorIsInvalidArgumentNamingTheFrame()
    {
        const string correlation = """{"indicator":"Correlation","parameters":{"Period":20,"ComparisonSymbol":"DOWN"},"timeFrame":"W1"}""";

        (string code, string param) = await ErrorOfAsync(Trend(), Call($$$"""{"left":{{{correlation}}},"comparison":"GreaterThan","right":0}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.left.timeFrame", param);
    }

    [Fact]
    public async Task ATwoSymbolIndicatorBesideASideThatNamesAFrameIsInvalidArgumentBecauseThatScanReadsNoComparisonSymbol()
    {
        const string correlation = """{"indicator":"Correlation","parameters":{"Period":20,"ComparisonSymbol":"DOWN"}}""";

        // The correlation names no frame (it would read the tool-level D1), the right side names W1: the scan is the one in which each side reads its own frame.
        (string code, string param) = await ErrorOfAsync(Trend(), Call($$$"""{"left":{{{correlation}}},"comparison":"GreaterThan","right":{"price":"Close","timeFrame":"W1"}}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.left.indicator", param);
    }

    [Fact]
    public async Task ATwoSymbolIndicatorWithNoSideFrameAnywhereStillWorksOnTheToolLevelFrame()
    {
        const string correlation = """{"indicator":"Correlation","parameters":{"Period":20,"ComparisonSymbol":"DOWN"}}""";

        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call($$$"""{"left":{{{correlation}}},"comparison":"LessThan","right":0}""", ",\"symbols\":[\"UP\"]")));

        Assert.Equal(new[] { "UP" }, Symbols(answer.RootElement)); // UP and DOWN move in opposite directions: the correlation is negative
    }

    [Fact]
    public async Task AnchoredVwapOnAWeeklySideIsInvalidArgument()
    {
        (string code, string param) = await ErrorOfAsync(Trend(), Call("""{"left":{"indicator":"AnchoredVWAP","timeFrame":"W1"},"comparison":"GreaterThan","right":0}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Equal("conditionTree.left.timeFrame", param);
    }

    [Fact]
    public async Task AnOffsetBeyondTheLimitIsStillRefusedWithAFrame()
    {
        (string code, string param) = await ErrorOfAsync(Trend(), Call("""{"left":{"price":"Close","timeFrame":"W1","offset":100000},"comparison":"GreaterThan","right":0}""", string.Empty));

        Assert.Equal(ToolErrorCodes.ConditionLimitExceeded, code);
        Assert.Equal("conditionTree.left.offset", param);
    }

    [Fact]
    public async Task ASideKeyThatIsNotKnownIsStillRefusedAndNamesTheKey()
    {
        (string code, string param) = await ErrorOfAsync(Trend(), Call("""{"left":{"price":"Close","timeframe":"W1"},"comparison":"GreaterThan","right":0}""", string.Empty));

        Assert.Equal(ToolErrorCodes.InvalidArgument, code);
        Assert.Contains("timeframe", param, StringComparison.Ordinal);
    }

    // ---- the gateway behind the tool ------------------------------------------------------------------------------------------------------

    private static ScreenerConditionTreeCondition Tree(ScreenerIndicatorEntry entry)
        => new(new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(entry))));

    [Fact]
    public async Task TheGatewayScansSidesOnTheirFramesAndSaysWhatEachFrameDid()
    {
        ScreenerTestWorld world = Trend();
        var selection = new ScreenerBarSelection(null, CompleteBarsOnly: true);

        ScreenerSideFramesResult result = await world.Gateway.RunScreenerWithSideFramesAsync(new[] { "up", "DOWN" }, Tree(PriceEntry(TimeFrame.D1, TimeFrame.W1)), selection);

        Assert.Equal(new[] { "UP" }, result.Matched); // symbols are canonicalised
        ScreenerFrameReport weekly = Assert.Single(result.Frames);
        Assert.Equal((TimeFrame.W1, 2), (weekly.Frame, weekly.SkippedPartialBarCount));
    }

    [Fact]
    public async Task TheGatewayRunsATemplateOnTheFramesItStoresAndRefusesAFrameItCannotLoad()
    {
        ScreenerTestWorld world = Trend();
        var selection = new ScreenerBarSelection(null, CompleteBarsOnly: true);
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "t" };
        template.SetEntries(new[] { PriceEntry(TimeFrame.D1, TimeFrame.W1) });

        ScreenerSideFramesResult result = await world.Gateway.RunScreenerTemplateWithSideFramesAsync(new[] { "UP", "DOWN" }, template, selection);

        Assert.Equal(new[] { "UP" }, result.Matched);
        Assert.Equal(TimeFrame.W1, Assert.Single(result.Frames).Frame);
        await Assert.ThrowsAsync<NotSupportedException>(() => world.Gateway.RunScreenerWithSideFramesAsync(new[] { "UP" }, Tree(PriceEntry(TimeFrame.D1, TimeFrame.H1)), selection));
        template.SetEntries(new[] { PriceEntry(TimeFrame.H1, TimeFrame.D1) });
        await Assert.ThrowsAsync<NotSupportedException>(() => world.Gateway.RunScreenerTemplateWithSideFramesAsync(new[] { "UP" }, template, selection));
    }

    // ---- the catalogue --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryCatalogueColumnTakesAFrameExactlyWhenTheScreenerReadsItFromBars()
    {
        ScreenerTestWorld world = Trend();
        using JsonDocument catalogue = JsonDocument.Parse(await world.CallAsync("sa_get_capabilities", """{"includeScreenerCatalog":true,"indicatorTypeFilter":["SMA"]}"""));
        JsonElement columns = catalogue.RootElement.GetProperty("screener").GetProperty("columns");
        Assert.True(columns.GetArrayLength() > 10);

        int readFromBars = 0;
        foreach (JsonElement column in columns.EnumerateArray())
        {
            string name = column.GetProperty("name").GetString()!;
            bool isText = column.TryGetProperty("isText", out JsonElement text) && text.GetBoolean();
            bool readsBars = ScreenerSideFrames.ReadsBars(new ScreenerIndicatorSideConfig { OutputName = name, CategoryType = ScreenerItemCategoryType.Column });
            string side = $$$"""{"column":"{{{name}}}","timeFrame":"D1"}""";
            string arguments = isText
                ? Call($$$"""{"left":{{{side}}},"comparison":"Equal","right":"x"}""", ",\"symbols\":[\"UP\"]")
                : Call($$$"""{"left":{{{side}}},"comparison":"GreaterThan","right":0}""", ",\"symbols\":[\"UP\"]");

            using JsonDocument response = await world.CallRawAsync("sa_run_screener", arguments);

            Assert.Equal(!readsBars, McpTestSupport.IsError(response));
            if (readsBars)
            {
                readFromBars++;
            }
            else
            {
                Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
                Assert.Equal("conditionTree.left.timeFrame", McpTestSupport.Structured(response).GetProperty("param").GetString());
            }
        }

        Assert.True(readFromBars >= 5, "Open, High, Low, Close and Volume are read from the bars");
    }

    [Fact]
    public async Task TheCapabilitiesNameTheFramesASideMayTake()
    {
        using JsonDocument catalogue = JsonDocument.Parse(await Trend().CallAsync("sa_get_capabilities", """{"includeScreenerCatalog":true,"indicatorTypeFilter":["SMA"]}"""));

        string[] frames = catalogue.RootElement.GetProperty("screener").GetProperty("sideTimeFrames").EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(new[] { "D1", "W1", "MN1" }, frames);
        Assert.Equal("D1", catalogue.RootElement.GetProperty("screener").GetProperty("timeFrame").GetString());
    }

    [Fact]
    public async Task TheDescriptionAndTheExampleTeachTheSideFrame()
    {
        ScreenerTestWorld world = Trend();

        using JsonDocument listing = await world.RequestAsync("tools/list");

        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_run_screener");
        string description = tool.GetProperty("description").GetString()!;
        foreach (string word in new[] { "sideFrames", "own timeFrame", "tool-level timeFrame", "INVALID_ARGUMENT", "AnchoredVWAP", "symbolsWithoutBars" })
        {
            Assert.Contains(word, description, StringComparison.Ordinal);
        }
    }
}
