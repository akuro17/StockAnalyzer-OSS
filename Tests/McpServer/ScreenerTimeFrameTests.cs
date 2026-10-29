using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener with <c>timeFrame</c> W1 / MN1 and <c>completeBarsOnly</c> (plan 02, T4, owner decision D-02c = C). The data tree is ScreenerTestWorld.Trend: one bar per
/// calendar day from Monday 2024-01-01 to Monday 2025-02-03; UP closes 100 + day index, DOWN 600 - day index, LATE a constant 250 from 2024-12-01. The expected numbers are
/// worked out from that arithmetic: day 398 is Sunday 2025-02-02, day 391 the Sunday before, and so on; the week of 2025-02-03 and the month of February 2025 are
/// partial (one and three days), every earlier period is complete.
/// </summary>
public sealed class ScreenerTimeFrameTests : IDisposable
{
    private const string CloseAboveItsAverage = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":5}}}""";
    private const string MatchAll = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""";

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

    private static string[] PartialStarts(JsonElement root)
        => root.TryGetProperty("partialPeriodStarts", out JsonElement starts) ? starts.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();

    // ---- the bars that are evaluated ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWeeklyScanEvaluatesTheWeeklyBarsBuiltFromTheDailyOnesAndLeavesOutThePartialLastWeek()
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(CloseAboveItsAverage, ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        Assert.Equal("W1", root.GetProperty("timeFrame").GetString());
        Assert.Equal(new[] { "UP" }, Symbols(root)); // UP rises every week, DOWN falls
        // The last complete week is the one ending Sunday 2025-02-02 (day 398: close 498); the weekly closes of the five weeks are 470, 477, 484, 491, 498 -> SMA(5) 484.
        Assert.Equal((498m, 484m), Values(root, "UP"));
        Assert.True(root.GetProperty("completeBarsOnly").GetBoolean());
        Assert.False(root.GetProperty("partialLastBar").GetBoolean());
        Assert.Equal(2, root.GetProperty("skippedPartialBarCount").GetInt32()); // the week of 2025-02-03 had one day in both symbols
        Assert.False(root.TryGetProperty("partialPeriodStarts", out _));
    }

    [Fact]
    public async Task WithCompleteBarsOnlyFalseThePartialWeekIsEvaluatedAndNamed()
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(CloseAboveItsAverage, ",\"timeFrame\":\"W1\",\"completeBarsOnly\":false,\"symbols\":[\"UP\",\"DOWN\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "UP" }, Symbols(root));
        // The partial week holds Monday 2025-02-03 only (day 399: close 499); the five weekly closes are 477, 484, 491, 498, 499 -> SMA(5) 489.8.
        Assert.Equal((499m, 489.8m), Values(root, "UP"));
        Assert.False(root.GetProperty("completeBarsOnly").GetBoolean());
        Assert.True(root.GetProperty("partialLastBar").GetBoolean());
        Assert.Equal(new[] { "2025-02-03" }, PartialStarts(root));
        Assert.False(root.TryGetProperty("skippedPartialBarCount", out _));
    }

    [Theory]
    [InlineData(true, 496, "2025-01-01")]
    [InlineData(false, 499, "2025-02-01")]
    public async Task AMonthlyScanEvaluatesTheLastCompleteMonthOrThePartialOneAsAsked(bool completeBarsOnly, int expectedClose, string partialStart)
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(MatchAll, $",\"timeFrame\":\"MN1\",\"completeBarsOnly\":{(completeBarsOnly ? "true" : "false")},\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        // January 2025 ends on day 396 (close 496); February 2025 holds days 397-399 (the last close is 499).
        Assert.Equal(expectedClose, Values(root, "UP").Left);
        Assert.Equal(!completeBarsOnly, root.GetProperty("partialLastBar").GetBoolean());
        Assert.Equal(completeBarsOnly ? Array.Empty<string>() : new[] { partialStart }, PartialStarts(root));
    }

    [Fact]
    public async Task ADailyScanIgnoresCompleteBarsOnlyAndAnswersLikeOneWithoutAFrame()
    {
        ScreenerTestWorld world = Trend();
        string plain = await world.CallAsync("sa_run_screener", Call(MatchAll, ",\"includeMatchedValues\":true"));

        string explicitDaily = await world.CallAsync("sa_run_screener", Call(MatchAll, ",\"timeFrame\":\"D1\",\"completeBarsOnly\":false,\"includeMatchedValues\":true"));

        Assert.Equal(plain, explicitDaily);
        Assert.DoesNotContain("completeBarsOnly", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("partialLastBar", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeeklyConditionReadsWeeklyBarsNotDailyOnes_TheSideNeedsNoFrameOfItsOwn()
    {
        // The comparison is true on both frames, so the SMA(5) value itself tells them apart: 484 on the five last complete weekly bars, 497 on the five last daily bars (495..499).
        // The side stays TimeFrame.D1 in the bound condition (the binder pins it); the weekly list is what the extractor is given, and it calculates on that list.
        using JsonDocument weekly = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(CloseAboveItsAverage, ",\"timeFrame\":\"W1\",\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));
        using JsonDocument daily = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(CloseAboveItsAverage, ",\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        Assert.Equal(484m, Values(weekly.RootElement, "UP").Right);
        Assert.Equal(497m, Values(daily.RootElement, "UP").Right);
    }

    // ---- with a date -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2024-12-18", true, 449, "")]            // Wednesday: the week of 2024-12-16 has three days, so the previous week (ending Sunday 12-15, day 349) is evaluated
    [InlineData("2024-12-18", false, 452, "2024-12-16")] // ... or the partial week up to Wednesday (day 352)
    [InlineData("2024-12-15", true, 449, "")]            // Sunday: the week ending that day is complete
    public async Task AWeeklyScanAsOfADateCutsTheDailyBarsBeforeTheWeeksAreBuiltAndAppliesThePartialRuleToTheDate(string asOf, bool completeBarsOnly, int expectedClose, string partialStart)
    {
        using JsonDocument answer = JsonDocument.Parse(await Trend().CallAsync(
            "sa_run_screener", Call(MatchAll, $",\"timeFrame\":\"W1\",\"asOfDate\":\"{asOf}\",\"completeBarsOnly\":{(completeBarsOnly ? "true" : "false")},\"symbols\":[\"UP\"],\"includeMatchedValues\":true")));

        JsonElement root = answer.RootElement;
        Assert.Equal(asOf, root.GetProperty("asOfDate").GetString());
        Assert.Equal(expectedClose, Values(root, "UP").Left);
        Assert.Equal(partialStart.Length == 0 ? Array.Empty<string>() : new[] { partialStart }, PartialStarts(root));
    }

    [Theory]
    [InlineData("W1", true)]
    [InlineData("W1", false)]
    [InlineData("MN1", true)]
    [InlineData("MN1", false)]
    public async Task NothingStoredAfterTheDateChangesAWeeklyOrMonthlyAnswerOrItsValues(string frame, bool completeBarsOnly)
    {
        const string asOf = "2024-12-18"; // inside a week and inside a month
        string arguments = Call(
            """{"operator":"And","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":3}}},{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}]}""",
            $",\"timeFrame\":\"{frame}\",\"asOfDate\":\"{asOf}\",\"completeBarsOnly\":{(completeBarsOnly ? "true" : "false")},\"includeMatchedValues\":true,\"sortBy\":{{\"price\":\"Close\"}}");
        ScreenerTestWorld truth = Trend();
        ScreenerTestWorld altered = Trend(alterAfter: DateOnly.Parse(asOf));

        string truthAnswer = await truth.CallAsync("sa_run_screener", arguments);
        string alteredAnswer = await altered.CallAsync("sa_run_screener", arguments);

        Assert.Equal(truthAnswer, alteredAnswer);
        using JsonDocument document = JsonDocument.Parse(truthAnswer);
        Assert.Contains("UP", Symbols(document.RootElement)); // not vacuous: UP matches and its values are in the answer
        Assert.True(document.RootElement.GetProperty("matchedValues").GetProperty("UP").GetProperty("L0").GetProperty("left").GetDecimal() < 460m);
    }

    [Fact]
    public async Task TheCheckCanFail_WithoutTheDateTheAlteredTreeGivesAnotherWeeklyAnswer()
    {
        string arguments = Call(MatchAll, ",\"timeFrame\":\"W1\",\"includeMatchedValues\":true");

        string truth = await Trend().CallAsync("sa_run_screener", arguments);
        string altered = await Trend(alterAfter: DateOnly.Parse("2024-12-18")).CallAsync("sa_run_screener", arguments);

        Assert.NotEqual(truth, altered);
    }

    [Fact]
    public async Task ASymbolWhoseOnlyPeriodIsPartialIsNotEvaluatedWithCompleteBarsOnlyAndIsWithout()
    {
        // LATE starts 2024-12-01: as of 2024-12-15 its only month (December) is partial (it ends on the 31st).
        string common = ",\"timeFrame\":\"MN1\",\"asOfDate\":\"2024-12-15\",\"symbols\":[\"LATE\",\"UP\"]";

        using JsonDocument complete = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(MatchAll, common)));
        using JsonDocument partial = JsonDocument.Parse(await Trend().CallAsync("sa_run_screener", Call(MatchAll, common + ",\"completeBarsOnly\":false")));

        Assert.Equal(new[] { "UP" }, Symbols(complete.RootElement));
        Assert.Equal(1, complete.RootElement.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Equal(1, complete.RootElement.GetProperty("evaluatedSymbolCount").GetInt32());
        Assert.Equal(2, complete.RootElement.GetProperty("skippedPartialBarCount").GetInt32());
        Assert.Equal(new[] { "LATE", "UP" }, Symbols(partial.RootElement));
        Assert.Equal(0, partial.RootElement.GetProperty("noDataAtAsOfCount").GetInt32());
        Assert.Equal(new[] { "2024-12-01" }, PartialStarts(partial.RootElement));
    }

    // ---- the other paths -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASavedTemplateAndARankingFollowTheFrame()
    {
        ScreenerTestWorld world = Trend();
        Guid id = world.SaveTemplate(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CategoryType = ScreenerItemCategoryType.Indicator, TimeFrame = TimeFrame.D1 },
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 300m,
            IsEnabled = true,
        });

        using JsonDocument answer = JsonDocument.Parse(await world.CallAsync(
            "sa_run_screener", $$$"""{"templateId":"{{{id}}}","timeFrame":"W1","sortBy":{"price":"Close"}}"""));

        JsonElement root = answer.RootElement;
        Assert.Equal(new[] { "LATE", "DOWN" }, Symbols(root)); // weekly closes: LATE 250, DOWN 202 (UP 498 does not match)
        Assert.Equal(new decimal[] { 250m, 202m }, root.GetProperty("ranking").EnumerateArray().Select(e => e.GetProperty("sortValue").GetDecimal()).ToArray());
        Assert.Equal(3, root.GetProperty("skippedPartialBarCount").GetInt32());
    }

    [Fact]
    public async Task AFrameTheDataLayerCannotLoadIsUnsupportedTimeFrameNamingTheArgument()
    {
        using JsonDocument response = await Trend().CallRawAsync("sa_run_screener", Call(MatchAll, ",\"timeFrame\":\"H1\""));

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.UnsupportedTimeFrame, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("timeFrame", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task TheDescriptionStatesTheFramesAndThePartialPeriodRule()
    {
        ScreenerTestWorld world = Trend();

        using JsonDocument listing = await world.RequestAsync("tools/list");

        string description = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "sa_run_screener").GetProperty("description").GetString()!;
        foreach (string word in new[] { "W1", "MN1", "completeBarsOnly", "partialLastBar", "skippedPartialBarCount", "sortBy", "ranking" })
        {
            Assert.Contains(word, description, StringComparison.Ordinal);
        }
    }

    // ---- the data service behind it ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBarsDataServiceNamesThePartialPeriodsItServedOrLeftOut()
    {
        ScreenerTestWorld world = Trend();
        var kept = new ScreenerBarsDataService(world.DataService, new ScreenerBarSelection(null, CompleteBarsOnly: false));
        var skipped = new ScreenerBarsDataService(world.DataService, new ScreenerBarSelection(null, CompleteBarsOnly: true));

        IReadOnlyList<CandleData> withPartial = await kept.LoadCandlesAsync("UP", TimeFrame.W1, 3);
        IReadOnlyList<CandleData> withoutPartial = await skipped.LoadCandlesAsync("UP", TimeFrame.W1, 3);

        Assert.Equal(3, withPartial.Count);
        Assert.Equal(3, withoutPartial.Count);
        Assert.Equal(withPartial.Take(2), withoutPartial.Skip(1)); // the same two older weeks; the partial one is the extra last bar
        Assert.Equal(new DateOnly(2025, 2, 3), kept.EvaluatedPartial["UP"]);
        Assert.Empty(kept.SkippedPartial);
        Assert.Equal(new DateOnly(2025, 2, 3), skipped.SkippedPartial["UP"]);
        Assert.Empty(skipped.EvaluatedPartial);
    }

    [Fact]
    public async Task TheGatewayBarsOfASelectionAreTheBarsAScanEvaluates()
    {
        ScreenerTestWorld world = Trend();
        var selection = new ScreenerBarSelection(DateOnly.Parse("2024-12-18"), CompleteBarsOnly: true);

        IReadOnlyList<CandleData> bars = await world.Gateway.GetScreenerBarsAsync("UP", TimeFrame.W1, selection, 4);

        Assert.Equal(4, bars.Count);
        Assert.Equal(449m, bars[^1].Close); // the week ending Sunday 2024-12-15
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.Gateway.GetScreenerBarsAsync("UP", TimeFrame.W1, selection, 0));
    }
}
