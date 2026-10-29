using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_screener with an indicator that compares two symbols (Correlation) on weekly and monthly bars (follow-up of plan 02, owner decision W-2 = A: the date match stays exact).
/// The tree is ScreenerTestWorld.TrendWithGaps: one bar per calendar day from Monday 2024-01-01 to Monday 2025-02-03. UP closes 100 + day index and DOWN 600 - day index, so
/// any window of them correlates at +1 / -1 exactly; GAPW is UP without Monday 2025-01-06, GAPM is UP without 2024-09-01, STALE is UP stored only up to Wednesday 2025-01-22.
/// A weekly or monthly bar is stamped with the first STORED day of its period and the two symbols are matched by that exact date, so a period whose first stored day differs
/// has no value (no fill), and with a null in its window Correlation has no value either: the condition does not match and the answer carries no warning.
/// Complete weeks at the scan of 2025-02-03 end on the Sundays up to 2025-02-02 (the week of 2025-02-03 is partial and skipped); complete months end with January 2025.
/// </summary>
public sealed class ScreenerTwoSymbolTimeFrameTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private ScreenerTestWorld? _world;

    /// <summary>The one tree of the running test (every test starts with a fresh instance of this class).</summary>
    private ScreenerTestWorld World()
    {
        if (_world is null)
        {
            _world = ScreenerTestWorld.TrendWithGaps();
            _disposables.Add(_world);
        }

        return _world;
    }

    private static string Corr(int period, string comparisonSymbol, string comparison, string right)
        => $$$"""{"left":{"indicator":"Correlation","parameters":{"Period":{{{period}}},"ComparisonSymbol":"{{{comparisonSymbol}}}"}},"comparison":"{{{comparison}}}","right":{{{right}}}}""";

    private static string Call(string conditionJson, string extra) => $$$"""{"conditionTree":{{{conditionJson}}}{{{extra}}}}""";

    private static string[] Symbols(JsonElement root) => root.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static decimal Left(JsonElement root, string symbol) => root.GetProperty("matchedValues").GetProperty(symbol).GetProperty("L0").GetProperty("left").GetDecimal();

    private async Task<JsonDocument> RunAsync(string frame, string condition, string symbols, string extra = "")
        => JsonDocument.Parse(await World().CallAsync(
            "sa_run_screener", Call(condition, $",\"timeFrame\":\"{frame}\",\"symbols\":[{symbols}],\"includeMatchedValues\":true{extra}")));

    // ---- the comparison symbol's bars are weekly / monthly bars --------------------------------------------------------------------------------

    [Theory]
    [InlineData("W1", 20)]
    [InlineData("MN1", 5)]
    public async Task ATwoSymbolConditionOnWeeklyOrMonthlyBarsMatchesAndReadsThePeriodBars(string frame, int period)
    {
        using JsonDocument answer = await RunAsync(frame, Corr(period, "DOWN", "LessThan", "-0.99"), "\"UP\"");

        Assert.Equal(new[] { "UP" }, Symbols(answer.RootElement));
        Assert.InRange(Left(answer.RootElement, "UP"), -1.0001m, -0.99m);
    }

    [Fact]
    public async Task APeriodLongerThanTheWeeklyHistoryGivesNoValueWhileTheDailyScanHasOne()
    {
        // 57 complete weeks against 400 daily bars: Period 100 is the only difference between "no value" and "match", so the weekly scan really reads weekly bars.
        string condition = Corr(100, "DOWN", "LessThan", "-0.99");

        using JsonDocument weekly = await RunAsync("W1", condition, "\"UP\"");
        using JsonDocument daily = await RunAsync("D1", condition, "\"UP\"");

        Assert.Empty(Symbols(weekly.RootElement));
        Assert.Equal(new[] { "UP" }, Symbols(daily.RootElement));
    }

    // ---- exact date match: a period whose first stored day differs has no value --------------------------------------------------------------

    [Fact]
    public async Task AWeekWhoseFirstStoredDayDiffersBetweenTheSymbolsHasNoValueAndAWindowThatContainsItDoesNotMatch()
    {
        ScreenerTestWorld world = World();
        var selection = new ScreenerBarSelection(null, true);
        IReadOnlyList<DateTime> upStamps = (await world.Gateway.GetScreenerBarsAsync("UP", TimeFrame.W1, selection, 5)).Select(bar => bar.Timestamp.Date).ToList();
        IReadOnlyList<DateTime> gapStamps = (await world.Gateway.GetScreenerBarsAsync("GAPW", TimeFrame.W1, selection, 5)).Select(bar => bar.Timestamp.Date).ToList();
        // the cause: the week of 2025-01-06 is stamped Monday for UP and Tuesday for GAPW (its Monday is not stored)
        Assert.Equal(new[] { new DateTime(2024, 12, 30), new DateTime(2025, 1, 6), new DateTime(2025, 1, 13), new DateTime(2025, 1, 20), new DateTime(2025, 1, 27) }, upStamps);
        Assert.Equal(new[] { new DateTime(2024, 12, 30), new DateTime(2025, 1, 7), new DateTime(2025, 1, 13), new DateTime(2025, 1, 20), new DateTime(2025, 1, 27) }, gapStamps);

        using JsonDocument outside = await RunAsync("W1", Corr(3, "GAPW", "GreaterThan", "0.99"), "\"UP\""); // weeks 01-13, 01-20, 01-27
        using JsonDocument inside = await RunAsync("W1", Corr(4, "GAPW", "GreaterThan", "0.99"), "\"UP\""); // weeks 01-06 ... 01-27
        using JsonDocument control = await RunAsync("W1", Corr(4, "GAPM", "GreaterThan", "0.99"), "\"UP\""); // GAPM has every Monday: same window, same period

        Assert.Equal(new[] { "UP" }, Symbols(outside.RootElement));
        Assert.Empty(Symbols(inside.RootElement));
        Assert.Equal(new[] { "UP" }, Symbols(control.RootElement));
        Assert.False(inside.RootElement.TryGetProperty("warnings", out _)); // nothing tells the client why
    }

    [Fact]
    public async Task AMonthWhoseFirstStoredDayDiffersBetweenTheSymbolsHasNoValueAndAWindowThatContainsItDoesNotMatch()
    {
        using JsonDocument outside = await RunAsync("MN1", Corr(3, "GAPM", "GreaterThan", "0.99"), "\"UP\""); // Nov, Dec, Jan
        using JsonDocument inside = await RunAsync("MN1", Corr(5, "GAPM", "GreaterThan", "0.99"), "\"UP\""); // Sep ... Jan
        using JsonDocument control = await RunAsync("MN1", Corr(5, "GAPW", "GreaterThan", "0.99"), "\"UP\""); // GAPW has the first day of every month

        Assert.Equal(new[] { "UP" }, Symbols(outside.RootElement));
        Assert.Empty(Symbols(inside.RootElement)); // September 2024 is stamped 2024-09-01 for UP and 2024-09-02 for GAPM
        Assert.Equal(new[] { "UP" }, Symbols(control.RootElement));
    }

    [Fact]
    public async Task ASymbolWhoseDatesDoNotMatchGetsNoSortValueAndRanksLast()
    {
        using JsonDocument answer = await RunAsync(
            "W1", """{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}""", "\"GAPW\",\"DOWN\"",
            ",\"sortBy\":{\"indicator\":\"Correlation\",\"parameters\":{\"Period\":4,\"ComparisonSymbol\":\"UP\"}}");

        JsonElement ranking = answer.RootElement.GetProperty("ranking");
        Assert.Equal(new[] { "DOWN", "GAPW" }, ranking.EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!).ToArray());
        Assert.InRange(ranking[0].GetProperty("sortValue").GetDecimal(), -1.0001m, -0.99m);
        Assert.Equal(JsonValueKind.Null, ranking[1].GetProperty("sortValue").ValueKind); // GAPW's week of 2025-01-06 is stamped 01-07, UP's 01-06
    }

    // ---- the partial-period rule is applied to each symbol on its own ---------------------------------------------------------------------

    [Fact]
    public async Task AComparisonSymbolStoredOnlyToAMidWeekDayLosesItsLastWeekAndTheConditionDoesNotMatchUnlessTheDateEqualsThem()
    {
        // STALE's last stored day is Wednesday 2025-01-22, so its week of 01-20 is partial for it (reference = its own last stored day) and is left out, while UP's weeks
        // 01-20 and 01-27 are complete: UP has bars STALE has not, and a window that contains them has no value.
        using JsonDocument today = await RunAsync("W1", Corr(3, "STALE", "GreaterThan", "0.99"), "\"UP\"");
        // As of Tuesday 2025-01-21 both symbols are known up to the same day: both leave out the week of 01-20, and weeks 12-30, 01-06, 01-13 match.
        using JsonDocument sameDay = await RunAsync("W1", Corr(3, "STALE", "GreaterThan", "0.99"), "\"UP\"", ",\"asOfDate\":\"2025-01-21\"");

        Assert.Empty(Symbols(today.RootElement));
        Assert.Equal(new[] { "UP" }, Symbols(sameDay.RootElement));
    }

    // ---- nothing after the date can change the answer ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("W1")]
    [InlineData("MN1")]
    public async Task NothingStoredAfterTheDateInTheSymbolOrItsComparisonSymbolChangesAWeeklyOrMonthlyTwoSymbolAnswer(string frame)
    {
        const string asOf = "2025-01-15"; // inside a week and inside a month
        string condition = Corr(5, "DOWN", "LessThan", "-0.99");
        string arguments = Call(condition, $",\"timeFrame\":\"{frame}\",\"symbols\":[\"UP\"],\"asOfDate\":\"{asOf}\",\"includeMatchedValues\":true");
        string latest = Call(condition, $",\"timeFrame\":\"{frame}\",\"symbols\":[\"UP\"],\"includeMatchedValues\":true");
        ScreenerTestWorld truth = World();
        ScreenerTestWorld altered = ScreenerTestWorld.TrendWithGaps(alterAfter: DateOnly.Parse(asOf));
        _disposables.Add(altered);

        string truthAnswer = await truth.CallAsync("sa_run_screener", arguments);
        string alteredAnswer = await altered.CallAsync("sa_run_screener", arguments);

        Assert.Equal(truthAnswer, alteredAnswer);
        using JsonDocument document = JsonDocument.Parse(truthAnswer);
        Assert.Equal(new[] { "UP" }, Symbols(document.RootElement)); // not vacuous: UP matches at the date
        // the check can fail: without the date the altered tree (both closes jump tenfold together) answers differently
        Assert.NotEqual(await truth.CallAsync("sa_run_screener", latest), await altered.CallAsync("sa_run_screener", latest));
    }
}
