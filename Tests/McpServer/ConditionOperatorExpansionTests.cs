using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>Shared builders for the JSON of a condition (plain strings: the JSON ends in runs of closing braces that raw interpolated strings cannot hold).</summary>
internal static class ConditionJsonBuilder
{
    public static string Close(int offset = 0) => offset == 0 ? "{\"price\":\"Close\"}" : "{\"price\":\"Close\",\"offset\":" + offset + "}";

    public static string Sma(int period, int offset = 0)
        => "{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + period + "}" + (offset == 0 ? string.Empty : ",\"offset\":" + offset) + "}";

    public static string Cmp(string left, string comparison, string right) => "{\"left\":" + left + ",\"comparison\":\"" + comparison + "\",\"right\":" + right + "}";

    /// <summary>A comparison that looks back over <paramref name="bars"/> bars of its left side: <paramref name="name"/> is Rising (the default) or Falling.</summary>
    public static string Streak(string left, int bars, string name = "Rising") => "{\"left\":" + left + ",\"comparison\":\"" + name + "\",\"bars\":" + bars + "}";

    public static string Group(string op, params string[] children) => "{\"operator\":\"" + op + "\",\"children\":[" + string.Join(",", children) + "]}";
}

/// <summary>
/// Plan 05 / T2, the pure part: <see cref="ConditionOperatorExpander"/> returns exactly the comparisons of the plan's section 6.2, as JSON, and refuses what does not belong
/// to the name. Offsets are added to the offsets the sides already have; a constant right side stays the constant.
/// </summary>
public sealed class ConditionOperatorExpanderTests
{
    private const string Path = "conditions.entryLong";
    private const string Setting = "Backtest:MaxConditionOffset";
    private const int MaxOffset = 5;

    private static JsonElement Leaf(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<JsonElement> Expand(string leafJson, int maxOffset = MaxOffset)
    {
        JsonElement leaf = Leaf(leafJson);
        Assert.True(ConditionOperatorExpander.TryGetName(leaf, out string name));
        return ConditionOperatorExpander.Expand(leaf, name, Path, maxOffset, Setting);
    }

    private static void AssertSame(string expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())), $"expected {expected} but was {actual.GetRawText()}");

    private static ToolException Refused(string leafJson, int maxOffset = MaxOffset)
        => Assert.Throws<ToolException>(() => Expand(leafJson, maxOffset));

    [Fact]
    public void CrossesAboveIsGreaterThanNowAndLessThanOrEqualOneBarEarlierWithBothOffsetsRaisedByOne()
    {
        IReadOnlyList<JsonElement> result = Expand(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "CrossesAbove", ConditionJsonBuilder.Sma(20, 2)));

        Assert.Equal(2, result.Count);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "GreaterThan", ConditionJsonBuilder.Sma(20, 2)), result[0]);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(1), "LessThanOrEqual", ConditionJsonBuilder.Sma(20, 3)), result[1]);
    }

    [Fact]
    public void CrossesBelowWithANumberKeepsTheNumberAndAddsOneToTheLeftOffset()
    {
        IReadOnlyList<JsonElement> result = Expand(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(2), "CrossesBelow", "70"));

        Assert.Equal(2, result.Count);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(2), "LessThan", "70"), result[0]);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(3), "GreaterThanOrEqual", "70"), result[1]);
    }

    [Fact]
    public void RisingThreeIsThreeConsecutiveBarToBarComparisons()
    {
        IReadOnlyList<JsonElement> result = Expand(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 3));

        Assert.Equal(3, result.Count);
        for (int index = 0; index < 3; index++)
        {
            AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(index), "GreaterThan", ConditionJsonBuilder.Close(index + 1)), result[index]);
        }
    }

    [Fact]
    public void FallingOneIsOneLessThanComparison()
    {
        IReadOnlyList<JsonElement> result = Expand(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 1, "Falling"));

        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", ConditionJsonBuilder.Close(1)), Assert.Single(result));
    }

    [Fact]
    public void FallingAddsBarsToTheOffsetTheSideAlreadyHas()
    {
        IReadOnlyList<JsonElement> result = Expand(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Sma(10, 3), 2, "Falling"));

        Assert.Equal(2, result.Count);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Sma(10, 3), "LessThan", ConditionJsonBuilder.Sma(10, 4)), result[0]);
        AssertSame(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Sma(10, 4), "LessThan", ConditionJsonBuilder.Sma(10, 5)), result[1]);
    }

    [Theory]
    [InlineData("crossesabove", true)]
    [InlineData(" CrossesBelow ", true)]
    [InlineData("RISING", true)]
    [InlineData("GreaterThan", false)]
    [InlineData("", false)]
    public void TheNameIsMatchedLikeAnOperatorNameTrimmedAndInAnyCase(string comparison, bool expected)
        => Assert.Equal(expected, ConditionOperatorExpander.TryGetName(Leaf("{\"left\":{\"price\":\"Close\"},\"comparison\":\"" + comparison + "\",\"right\":1}"), out _));

    [Fact]
    public void ALeafWithoutAStringComparisonIsNotAnExpandedName()
    {
        Assert.False(ConditionOperatorExpander.TryGetName(Leaf("{\"left\":{\"price\":\"Close\"}}"), out _));
        Assert.False(ConditionOperatorExpander.TryGetName(Leaf("{\"comparison\":3}"), out _));
        Assert.False(ConditionOperatorExpander.TryGetName(Leaf("[1]"), out _));
    }

    [Fact]
    public void BarsOnACrossIsRefusedAtTheBarsKey()
    {
        ToolException error = Refused("{\"left\":{\"price\":\"Close\"},\"comparison\":\"CrossesAbove\",\"right\":1,\"bars\":2}");

        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Equal(Path + ".bars", error.Parameter);
    }

    [Fact]
    public void ARightSideOnRisingIsRefusedAtTheRightKey()
    {
        ToolException error = Refused("{\"left\":{\"price\":\"Close\"},\"comparison\":\"Rising\",\"bars\":2,\"right\":1}");

        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Equal(Path + ".right", error.Parameter);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"bars\":0")]
    [InlineData(",\"bars\":6")]
    [InlineData(",\"bars\":1.5")]
    [InlineData(",\"bars\":\"3\"")]
    [InlineData(",\"bars\":-1")]
    public void RisingNeedsBarsFromOneToTheLargestOffset(string bars)
    {
        ToolException error = Refused("{\"left\":{\"price\":\"Close\"},\"comparison\":\"Rising\"" + bars + "}");

        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Equal(Path + ".bars", error.Parameter);
    }

    [Fact]
    public void RisingWithTheLargestOffsetAsBarsIsAccepted()
        => Assert.Equal(MaxOffset, Expand(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), MaxOffset)).Count);

    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void CrossesAbovePastTheLargestOffsetNamesTheNodeAndTheSetting(int leftOffset)
    {
        // The earlier bar reads offset + 1: from 5 that is 6 > 5; the same sides one smaller are fine.
        string leaf = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(leftOffset), "CrossesAbove", "1");
        if (leftOffset < MaxOffset)
        {
            Assert.Equal(2, Expand(leaf).Count);
            return;
        }

        ToolException error = Refused(leaf);
        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Equal(Path, error.Parameter);
        Assert.Contains(Setting, error.Hint);
        Assert.Contains(MaxOffset.ToString(), error.Hint);
    }

    [Fact]
    public void ARightSideThatTheExpansionPushesPastTheLimitIsRefused()
    {
        ToolException error = Refused(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "CrossesBelow", ConditionJsonBuilder.Sma(5, 5)));

        Assert.Equal(Path, error.Parameter);
    }

    [Fact]
    public void RisingPastTheLargestOffsetThroughTheSidesOwnOffsetIsRefused()
    {
        Assert.Equal(2, Expand(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(3), 2)).Count);
        Assert.Equal(Path, Refused(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(3), 3)).Parameter);
    }

    [Fact]
    public void AnOffsetThatIsAlreadyOverTheLimitOrInvalidIsLeftToTheOrdinarySideBinding()
    {
        Assert.Equal(2, Expand(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(9), "CrossesAbove", "1")).Count);
        Assert.Equal(2, Expand("{\"left\":{\"price\":\"Close\",\"offset\":-2},\"comparison\":\"CrossesAbove\",\"right\":1}").Count);
        Assert.Equal(2, Expand("{\"left\":{\"price\":\"Close\",\"offset\":\"x\"},\"comparison\":\"CrossesAbove\",\"right\":1}").Count);
    }

    [Fact]
    public void TheNamesAreNotRealComparisonOperatorsSoTheParityGuardsStayValid()
    {
        Assert.Equal(new[] { "CrossesAbove", "CrossesBelow", "Rising", "Falling" }, ConditionOperatorExpander.ExpandedComparisonNames);
        Assert.All(ConditionOperatorExpander.ExpandedComparisonNames, name => Assert.False(Enum.TryParse<StockAnalyzer.Core.Models.Screener.ComparisonOperator>(name, out _)));
        Assert.Empty(ConditionOperatorExpander.ExpandedComparisonNames.Intersect(BacktestConditionBinder.ComparisonNames));
        Assert.Empty(ConditionOperatorExpander.ExpandedComparisonNames.Intersect(ScreenerConditionBinder.ComparisonNames));
    }
}

/// <summary>
/// Plan 05 / T2 through sa_run_backtest on the sine-wave MSFT series (about 38 days a period, so every kind of cross and run exists). Proof (c): a run written with an expanded
/// name equals the run of the comparisons written out by hand - the same fingerprint, hash, trades and echoed conditions. Proof (d) of the plan lives in McpParityGuardTests,
/// which this task leaves untouched.
/// </summary>
public sealed class ConditionOperatorExpansionBacktestTests : IDisposable
{
    private const int Days = 400;
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";

    private static readonly decimal[] Closes = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public ConditionOperatorExpansionBacktestTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", Closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditions)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", "{\"symbol\":\"MSFT\",\"conditions\":" + conditions + Period + "}"))!;

    private static string Entry(string entryLong, string exitLong) => "{\"entryLong\":" + entryLong + ",\"exitLong\":" + exitLong + "}";

    private static JsonElement Ok(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string Code(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static string Fingerprint(JsonElement result) => result.GetProperty("runFingerprint").GetProperty("sha256").GetString()!;

    private static string Sma5 => ConditionJsonBuilder.Sma(5);

    /// <summary>(name, the run with an expanded name, the same run with the comparisons written out).</summary>
    public static IEnumerable<object[]> EquivalentPairs()
    {
        string c0 = ConditionJsonBuilder.Close();
        string c1 = ConditionJsonBuilder.Close(1);
        string c2 = ConditionJsonBuilder.Close(2);
        string c3 = ConditionJsonBuilder.Close(3);
        string exitBelowSma = ConditionJsonBuilder.Cmp(c0, "LessThan", Sma5);

        yield return new object[]
        {
            "CrossesAbove as the whole root against an SMA",
            Entry(ConditionJsonBuilder.Cmp(c0, "CrossesAbove", Sma5), exitBelowSma),
            Entry(ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "GreaterThan", Sma5), ConditionJsonBuilder.Cmp(c1, "LessThanOrEqual", ConditionJsonBuilder.Sma(5, 1))), exitBelowSma),
        };
        yield return new object[]
        {
            "CrossesBelow as the exit against an SMA",
            Entry(ConditionJsonBuilder.Cmp(c0, "GreaterThan", "100"), ConditionJsonBuilder.Cmp(c0, "CrossesBelow", Sma5)),
            Entry(ConditionJsonBuilder.Cmp(c0, "GreaterThan", "100"), ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "LessThan", Sma5), ConditionJsonBuilder.Cmp(c1, "GreaterThanOrEqual", ConditionJsonBuilder.Sma(5, 1)))),
        };
        yield return new object[]
        {
            "CrossesAbove a number",
            Entry(ConditionJsonBuilder.Cmp(c0, "CrossesAbove", "100"), ConditionJsonBuilder.Cmp(c0, "CrossesBelow", "100")),
            Entry(
                ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "GreaterThan", "100"), ConditionJsonBuilder.Cmp(c1, "LessThanOrEqual", "100")),
                ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "LessThan", "100"), ConditionJsonBuilder.Cmp(c1, "GreaterThanOrEqual", "100"))),
        };
        yield return new object[]
        {
            "Rising 3 inside an Or group",
            Entry(ConditionJsonBuilder.Group("Or", ConditionJsonBuilder.Streak(c0, 3), ConditionJsonBuilder.Cmp(c0, "GreaterThan", "109")), ConditionJsonBuilder.Cmp(c0, "LessThan", c1)),
            Entry(
                ConditionJsonBuilder.Group(
                    "Or",
                    ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "GreaterThan", c1), ConditionJsonBuilder.Cmp(c1, "GreaterThan", c2), ConditionJsonBuilder.Cmp(c2, "GreaterThan", c3)),
                    ConditionJsonBuilder.Cmp(c0, "GreaterThan", "109")),
                ConditionJsonBuilder.Cmp(c0, "LessThan", c1)),
        };
        yield return new object[]
        {
            "Falling 1 as a single comparison root",
            Entry(ConditionJsonBuilder.Cmp(c0, "GreaterThan", c1), ConditionJsonBuilder.Streak(c0, 1, "Falling")),
            Entry(ConditionJsonBuilder.Cmp(c0, "GreaterThan", c1), ConditionJsonBuilder.Cmp(c0, "LessThan", c1)),
        };
        yield return new object[]
        {
            "offsets of both sides are added to",
            Entry(ConditionJsonBuilder.Cmp(c1, "CrossesAbove", ConditionJsonBuilder.Sma(5, 2)), exitBelowSma),
            Entry(ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c1, "GreaterThan", ConditionJsonBuilder.Sma(5, 2)), ConditionJsonBuilder.Cmp(c2, "LessThanOrEqual", ConditionJsonBuilder.Sma(5, 3))), exitBelowSma),
        };
        yield return new object[]
        {
            "Falling 2 under an And root",
            Entry(ConditionJsonBuilder.Cmp(c0, "GreaterThan", c1), ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Streak(c0, 2, "Falling"), ConditionJsonBuilder.Cmp(c0, "LessThan", "104"))),
            Entry(
                ConditionJsonBuilder.Cmp(c0, "GreaterThan", c1),
                ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(c0, "LessThan", c1), ConditionJsonBuilder.Cmp(c1, "LessThan", c2)), ConditionJsonBuilder.Cmp(c0, "LessThan", "104"))),
        };
    }

    [Theory]
    [MemberData(nameof(EquivalentPairs))]
    public async Task AnExpandedNameRunsExactlyAsTheComparisonsWrittenOut(string name, string sugar, string handWritten)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument sugared = await RunAsync(dispatcher, sugar);
        using JsonDocument written = await RunAsync(dispatcher, handWritten);

        JsonElement a = Ok(sugared);
        JsonElement b = Ok(written);
        Assert.True(a.GetProperty("report").GetProperty("totalTrades").GetInt32() > 0, $"{name}: the scenario must trade or the comparison proves nothing");
        Assert.Equal(Fingerprint(b), Fingerprint(a));
        Assert.Equal(b.GetProperty("reproducibilityHash").GetString(), a.GetProperty("reproducibilityHash").GetString());
        Assert.Equal(b.GetProperty("trades").GetRawText(), a.GetProperty("trades").GetRawText());
        Assert.Equal(b.GetProperty("conditionRoots").GetRawText(), a.GetProperty("conditionRoots").GetRawText());
    }

    [Fact]
    public async Task CrossesAboveAndBelowTradeTheBarAfterTheCrossOfThePriceAndTheLevel()
    {
        // Independent of the expansion: the crossing days are read off the stored closes (close above 100 now and not above it the bar before).
        var upCrosses = new HashSet<DateOnly>();
        var downCrosses = new HashSet<DateOnly>();
        var first = new DateOnly(2024, 1, 1);
        for (int index = 1; index < Days; index++)
        {
            if (Closes[index] > 100m && Closes[index - 1] <= 100m)
            {
                upCrosses.Add(first.AddDays(index));
            }

            if (Closes[index] < 100m && Closes[index - 1] >= 100m)
            {
                downCrosses.Add(first.AddDays(index));
            }
        }

        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument response = await RunAsync(
            dispatcher,
            Entry(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "CrossesAbove", "100"), ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "CrossesBelow", "100")));

        JsonElement[] trades = Ok(response).GetProperty("trades").GetProperty("items").EnumerateArray().ToArray();
        Assert.True(trades.Length >= 5, $"expected several round trips, got {trades.Length}");
        foreach (JsonElement trade in trades)
        {
            Assert.Contains(DateOnly.Parse(trade.GetProperty("entryDate").GetString()!).AddDays(-1), upCrosses);
            Assert.Contains(DateOnly.Parse(trade.GetProperty("exitDate").GetString()!).AddDays(-1), downCrosses);
        }
    }

    [Fact]
    public async Task TheEchoShowsTheExpansionAndCountsTheExpandedComparisons()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 3), ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1")));

        JsonElement entry = Ok(response).GetProperty("conditionRoots")[0];
        Assert.Equal(3, entry.GetProperty("comparisonCount").GetInt32());
        Assert.Equal("Close > Close [1 bars back] And Close [1 bars back] > Close [2 bars back] And Close [2 bars back] > Close [3 bars back]", entry.GetProperty("text").GetString());
    }

    // ---- the tree limits count the expanded nodes; errors name the node the client wrote ---------------------------------------

    [Fact]
    public async Task TheExpandedComparisonsCountAgainstTheNodeLimitOfTheRoot()
    {
        // A root that expands to a group holds that group plus its comparisons: 4 for Rising 3, 3 for Rising 2.
        McpMessageDispatcher dispatcher = await StartAsync(("Backtest:MaxConditionTreeNodes", "3"), ("Backtest:MaxConditionTreeDepth", "3"));
        string exit = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1");

        using JsonDocument fits = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 2), exit));
        using JsonDocument tooMany = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 3), exit));

        Ok(fits);
        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(tooMany));
        Assert.Equal("conditions.entryLong", Param(tooMany));
        Assert.Contains("Backtest:MaxConditionTreeNodes", McpTestSupport.Structured(tooMany).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task TheExpansionGroupCountsAgainstTheDepthLimit()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("Backtest:MaxConditionTreeDepth", "1"));
        string exit = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1");

        using JsonDocument asRoot = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 2), exit));
        using JsonDocument nested = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Group("Or", ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 2)), exit));

        Ok(asRoot);
        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(nested));
        Assert.Equal("conditions.entryLong.children[0]", Param(nested));
        Assert.Contains("Backtest:MaxConditionTreeDepth", McpTestSupport.Structured(nested).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AnExpansionPastTheOffsetLimitIsInvalidAtTheNodeAndNamesTheSetting()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("Backtest:MaxConditionOffset", "10"));
        string exit = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1");

        using JsonDocument crossed = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(10), "CrossesAbove", "1"), exit));
        using JsonDocument bars = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 11), exit));
        using JsonDocument edge = await RunAsync(dispatcher, Entry(ConditionJsonBuilder.Streak(ConditionJsonBuilder.Close(), 10), exit));

        Assert.Equal("INVALID_ARGUMENT", Code(crossed));
        Assert.Equal("conditions.entryLong", Param(crossed));
        Assert.Contains("Backtest:MaxConditionOffset", McpTestSupport.Structured(crossed).GetProperty("hint").GetString());
        Assert.Equal("INVALID_ARGUMENT", Code(bars));
        Assert.Equal("conditions.entryLong.bars", Param(bars));
        Ok(edge);
    }

    [Fact]
    public async Task AnErrorInASideOfAnExpandedComparisonNamesTheSideTheClientWrote()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(
            dispatcher,
            Entry("{\"left\":{\"price\":\"Typo\"},\"comparison\":\"CrossesAbove\",\"right\":1}", ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1")));

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal("conditions.entryLong.left.price", Param(response));
    }

    [Fact]
    public async Task AnUnknownKeyOnAnExpandedLeafIsRefusedAndBarsOnAnOrdinaryLeafStaysUnknown()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string exit = ConditionJsonBuilder.Cmp(ConditionJsonBuilder.Close(), "LessThan", "1");

        using JsonDocument extra = await RunAsync(dispatcher, Entry("{\"left\":{\"price\":\"Close\"},\"comparison\":\"Rising\",\"bars\":2,\"extra\":1}", exit));
        using JsonDocument ordinary = await RunAsync(dispatcher, Entry("{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":1,\"bars\":2}", exit));

        Assert.Equal("conditions.entryLong.extra", Param(extra));
        Assert.Equal("conditions.entryLong.bars", Param(ordinary));
        Assert.Equal("INVALID_ARGUMENT", Code(ordinary));
    }

    // ---- the names are published ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sa_run_backtest")]
    [InlineData("sa_run_screener")]
    public async Task EveryExpandedNameIsInTheToolDescription(string tool)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/list\"}", CancellationToken.None))!);

        string description = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == tool).GetProperty("description").GetString()!;

        foreach (string name in ConditionOperatorExpander.ExpandedComparisonNames)
        {
            Assert.Contains(name, description, StringComparison.Ordinal);
        }

        Assert.Contains("bars", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilitiesListsTheExpandedNamesNextToTheComparisons()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", "{\"indicatorTypeFilter\":[\"RSI\"],\"includeScreenerCatalog\":true}"))!;

        JsonElement screener = McpTestSupport.Structured(response).GetProperty("screener");

        Assert.Equal(ConditionOperatorExpander.ExpandedComparisonNames, screener.GetProperty("expandedComparisons").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.DoesNotContain(screener.GetProperty("comparisons").EnumerateArray().Select(c => c.GetString()), name => ConditionOperatorExpander.ExpandedComparisonNames.Contains(name!));
    }
}

/// <summary>
/// Plan 05 / T2 through sa_run_screener on four 60-bar series whose latest bars tell the cases apart: UP rises every bar, DOWN falls every bar, CROSS is flat at 100 and closes at 110,
/// DROP is flat at 100 and closes at 90. Each expanded name matches exactly the symbols its definition says, and the same set as the comparisons written out.
/// </summary>
public sealed class ConditionOperatorExpansionScreenerTests : IDisposable
{
    private const int Bars = 60;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Bars, "up", "down", "cross", "drop");
    private readonly List<McpServerRuntime> _runtimes = new();

    public ConditionOperatorExpansionScreenerTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "UP", Enumerable.Range(0, Bars).Select(i => 100m + i).ToArray());
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "DOWN", Enumerable.Range(0, Bars).Select(i => 200m - i).ToArray());
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "CROSS", Enumerable.Range(0, Bars).Select(i => i == Bars - 1 ? 110m : 100m).ToArray());
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "DROP", Enumerable.Range(0, Bars).Select(i => i == Bars - 1 ? 90m : 100m).ToArray());
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditionTree)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", "{\"conditionTree\":" + conditionTree + "}"))!;

    private static string[] Matched(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetProperty("matchedSymbols").EnumerateArray().Select(s => s.GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    private static string Code(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static string C(int offset = 0) => ConditionJsonBuilder.Close(offset);

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[]
        {
            ConditionJsonBuilder.Streak(C(), 3),
            ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(C(), "GreaterThan", C(1)), ConditionJsonBuilder.Cmp(C(1), "GreaterThan", C(2)), ConditionJsonBuilder.Cmp(C(2), "GreaterThan", C(3))),
            new[] { "UP" },
        };
        yield return new object[] { ConditionJsonBuilder.Streak(C(), 1), ConditionJsonBuilder.Cmp(C(), "GreaterThan", C(1)), new[] { "CROSS", "UP" } };
        yield return new object[]
        {
            ConditionJsonBuilder.Streak(C(), 3, "Falling"),
            ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(C(), "LessThan", C(1)), ConditionJsonBuilder.Cmp(C(1), "LessThan", C(2)), ConditionJsonBuilder.Cmp(C(2), "LessThan", C(3))),
            new[] { "DOWN" },
        };
        yield return new object[] { ConditionJsonBuilder.Streak(C(), 1, "Falling"), ConditionJsonBuilder.Cmp(C(), "LessThan", C(1)), new[] { "DOWN", "DROP" } };
        yield return new object[]
        {
            ConditionJsonBuilder.Cmp(C(), "CrossesAbove", "105"),
            ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(C(), "GreaterThan", "105"), ConditionJsonBuilder.Cmp(C(1), "LessThanOrEqual", "105")),
            new[] { "CROSS" },
        };
        yield return new object[]
        {
            ConditionJsonBuilder.Cmp(C(), "CrossesBelow", "95"),
            ConditionJsonBuilder.Group("And", ConditionJsonBuilder.Cmp(C(), "LessThan", "95"), ConditionJsonBuilder.Cmp(C(1), "GreaterThanOrEqual", "95")),
            new[] { "DROP" },
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AnExpandedNameMatchesTheSymbolsItsDefinitionSaysAndTheSameAsTheComparisonsWrittenOut(string sugar, string handWritten, string[] expected)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument sugared = await RunAsync(dispatcher, sugar);
        using JsonDocument written = await RunAsync(dispatcher, handWritten);

        Assert.Equal(expected, Matched(sugared));
        Assert.Equal(Matched(written), Matched(sugared));
    }

    [Fact]
    public async Task AnExpandedNameWorksInsideAGroupNextToAnOrdinaryComparison()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, ConditionJsonBuilder.Group("Or", ConditionJsonBuilder.Cmp(C(), "CrossesAbove", "105"), ConditionJsonBuilder.Cmp(C(), "CrossesBelow", "95")));

        Assert.Equal(new[] { "CROSS", "DROP" }, Matched(response));
    }

    [Fact]
    public async Task TheExpandedComparisonsCountAgainstTheScreenerNodeLimit()
    {
        // Rising 3 is a group and 3 comparisons: 4 nodes fit a limit of 4, 5 do not.
        McpMessageDispatcher dispatcher = await StartAsync(("Screener:MaxFilterTreeNodes", "4"), ("Screener:MaxFilterTreeDepth", "2"));

        using JsonDocument fits = await RunAsync(dispatcher, ConditionJsonBuilder.Streak(C(), 3));
        using JsonDocument tooMany = await RunAsync(dispatcher, ConditionJsonBuilder.Streak(C(), 4));

        Assert.Equal(new[] { "UP" }, Matched(fits));
        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(tooMany));
        Assert.Equal("conditionTree", Param(tooMany));
        Assert.Contains("Screener:MaxFilterTreeNodes", McpTestSupport.Structured(tooMany).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AnExpansionPastTheScreenerOffsetLimitIsInvalidAtTheNode()
    {
        McpMessageDispatcher dispatcher = await StartAsync(("Screener:MaxConditionOffset", "10"));

        using JsonDocument bars = await RunAsync(dispatcher, ConditionJsonBuilder.Streak(C(), 11));
        using JsonDocument crossed = await RunAsync(dispatcher, ConditionJsonBuilder.Cmp(C(10), "CrossesAbove", "1"));

        Assert.Equal("INVALID_ARGUMENT", Code(bars));
        Assert.Equal("conditionTree.bars", Param(bars));
        Assert.Equal("INVALID_ARGUMENT", Code(crossed));
        Assert.Equal("conditionTree", Param(crossed));
        Assert.Contains("Screener:MaxConditionOffset", McpTestSupport.Structured(crossed).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task ARightSideOnRisingAndBarsOnACrossAreRefused()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument right = await RunAsync(dispatcher, "{\"left\":{\"price\":\"Close\"},\"comparison\":\"Rising\",\"bars\":2,\"right\":1}");
        using JsonDocument bars = await RunAsync(dispatcher, "{\"left\":{\"price\":\"Close\"},\"comparison\":\"CrossesAbove\",\"right\":1,\"bars\":2}");

        Assert.Equal("conditionTree.right", Param(right));
        Assert.Equal("conditionTree.bars", Param(bars));
    }
}
