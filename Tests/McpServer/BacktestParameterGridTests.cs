using System.Text.Json.Nodes;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>Plan 04 / T2: the pure grid and placeholder functions behind sa_optimize_backtest.</summary>
public sealed class BacktestParameterGridTests
{
    private const int Limit = 500;

    private static BacktestParameterGrid Grid(int limit, params (string Name, decimal Start, decimal End, decimal Step)[] ranges)
        => BacktestParameterGrid.Create(ranges.Select(r => new ParameterRange(r.Name, r.Start, r.End, r.Step)).ToList(), limit);

    private static BacktestParameterGrid Grid(params (string Name, decimal Start, decimal End, decimal Step)[] ranges) => Grid(Limit, ranges);

    private static ToolException Refused(Action action) => Assert.Throws<ToolException>(action);

    // ---- values ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ValuesStartAtStartStepUpAndStopAtTheLargestValueNotAboveEnd()
    {
        BacktestParameterGrid grid = Grid(("p", 5, 21, 2));

        Assert.Equal(9, grid.CombinationCount);
        Assert.Equal(new decimal[] { 5, 7, 9, 11, 13, 15, 17, 19, 21 }, Enumerable.Range(0, 9).Select(i => grid.ValueAt(0, i)));
    }

    [Fact]
    public void AnEndThatIsNotOnTheStepIsNotReached()
    {
        BacktestParameterGrid grid = Grid(("p", 1, 10, 4));

        Assert.Equal(3, grid.ValueCount(0));
        Assert.Equal(9m, grid.ValueAt(0, 2));
    }

    [Fact]
    public void FractionalStepsAreExactDecimalsNotAccumulatedSums()
    {
        BacktestParameterGrid grid = Grid(("level", 0.1m, 0.3m, 0.1m));

        Assert.Equal(new[] { 0.1m, 0.2m, 0.3m }, Enumerable.Range(0, 3).Select(i => grid.ValueAt(0, i)));

        BacktestParameterGrid tenths = Grid(("x", 0m, 1m, 0.1m));
        Assert.Equal(11, tenths.CombinationCount);
        Assert.Equal(0.7m, tenths.ValueAt(0, 7));
    }

    [Fact]
    public void AWholeValueHasNoTrailingZeros()
    {
        BacktestParameterGrid grid = Grid(("p", 10.0m, 12.0m, 1.0m));

        Assert.Equal("10", grid.ValueAt(0, 0).ToString());
        Assert.Equal("12", grid.ValueAt(0, 2).ToString());
        Assert.Equal("14", BacktestParameterGrid.Normalize(14.000m).ToString());
    }

    [Fact]
    public void AStartEqualToEndGivesOneValue()
    {
        BacktestParameterGrid grid = Grid(("p", 14, 14, 3));

        Assert.Equal(1, grid.CombinationCount);
        Assert.Equal(14m, grid.ValueAt(0, 0));
    }

    [Fact]
    public void ANegativeRangeIsAllowed()
    {
        BacktestParameterGrid grid = Grid(("shift", -2, 2, 2));

        Assert.Equal(new decimal[] { -2, 0, 2 }, Enumerable.Range(0, 3).Select(i => grid.ValueAt(0, i)));
    }

    // ---- numbering ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CombinationsAreNumberedWithTheFirstRangeChangingSlowest()
    {
        BacktestParameterGrid grid = Grid(("a", 1, 2, 1), ("b", 10, 30, 10));

        Assert.Equal(6, grid.CombinationCount);
        string[] expected = { "a=1,b=10", "a=1,b=20", "a=1,b=30", "a=2,b=10", "a=2,b=20", "a=2,b=30" };
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(i => string.Join(",", grid.ValuesAt(i).Select(p => $"{p.Key}={p.Value}"))));
    }

    [Fact]
    public void ThreeRangesKeepTheOrderAndEveryCombinationOnce()
    {
        BacktestParameterGrid grid = Grid(("a", 1, 3, 1), ("b", 1, 2, 1), ("c", 1, 4, 1));

        Assert.Equal(24, grid.CombinationCount);
        var seen = Enumerable.Range(0, 24).Select(i => string.Join(",", grid.ValuesAt(i).Select(p => p.Value))).ToList();
        Assert.Equal(24, seen.Distinct().Count());
        Assert.Equal("1,1,1", seen[0]);
        Assert.Equal("1,1,2", seen[1]);
        Assert.Equal("1,2,1", seen[4]);
        Assert.Equal("2,1,1", seen[8]);
        Assert.Equal("3,2,4", seen[23]);
    }

    [Fact]
    public void NeighboursDifferByOneStepInOneParameterAndStayInsideTheRanges()
    {
        BacktestParameterGrid grid = Grid(("a", 1, 3, 1), ("b", 10, 30, 10));

        // combination 4 = a=2, b=20 (the middle): four neighbours, a down, a up, b down, b up in that order
        IReadOnlyList<int> middle = grid.Neighbors(4);
        Assert.Equal(new[] { "a=1,b=20", "a=3,b=20", "a=2,b=10", "a=2,b=30" }, middle.Select(i => string.Join(",", grid.ValuesAt(i).Select(p => $"{p.Key}={p.Value}"))));

        // a corner has two neighbours
        Assert.Equal(new[] { "a=2,b=10", "a=1,b=20" }, grid.Neighbors(0).Select(i => string.Join(",", grid.ValuesAt(i).Select(p => $"{p.Key}={p.Value}"))));
    }

    [Fact]
    public void ASingleCombinationHasNoNeighbours()
    {
        Assert.Empty(Grid(("p", 5, 5, 1)).Neighbors(0));
    }

    [Fact]
    public void ASingleParameterHasOneNeighbourAtTheEdgesAndTwoInside()
    {
        BacktestParameterGrid grid = Grid(("p", 1, 5, 1));

        Assert.Equal(new[] { 1 }, grid.Neighbors(0));
        Assert.Equal(new[] { 1, 3 }, grid.Neighbors(2));
        Assert.Equal(new[] { 3 }, grid.Neighbors(4));
    }

    [Fact]
    public void AnIndexOutsideTheGridIsRefused()
    {
        BacktestParameterGrid grid = Grid(("p", 1, 3, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Positions(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Positions(-1));
    }

    // ---- limits and refusals -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLimitItselfIsAcceptedAndOneMoreIsLimitExceededWithoutThinning()
    {
        Assert.Equal(Limit, Grid(("p", 1, Limit, 1)).CombinationCount);

        ToolException error = Refused(() => Grid(("p", 1, Limit + 1, 1)));
        Assert.Equal(ToolErrorCodes.LimitExceeded, error.Code);
        Assert.StartsWith(BacktestParameterGrid.RangesArgument, error.Parameter);
    }

    [Fact]
    public void TheProductOfTheRangesIsCheckedAgainstTheLimit()
    {
        Assert.Equal(500, Grid(("a", 1, 20, 1), ("b", 1, 25, 1)).CombinationCount);

        ToolException error = Refused(() => Grid(("a", 1, 20, 1), ("b", 1, 26, 1)));
        Assert.Equal(ToolErrorCodes.LimitExceeded, error.Code);
        Assert.Equal(BacktestParameterGrid.RangesArgument, error.Parameter);
        Assert.Contains("520", error.Message);
    }

    [Fact]
    public void AProductThatOverflowsALongIsLimitExceededNotAnOverflowException()
    {
        // eight ranges of 400 values: 400^8 is about 6.5e20, above long.MaxValue
        (string, decimal, decimal, decimal)[] ranges = Enumerable.Range(0, 8).Select(i => ($"p{i}", 1m, 400m, 1m)).ToArray();

        ToolException error = Refused(() => Grid(20000, ranges));
        Assert.Equal(ToolErrorCodes.LimitExceeded, error.Code);
    }

    [Fact]
    public void AnAstronomicalRangeIsLimitExceededWithoutBuildingItsValues()
    {
        ToolException tiny = Refused(() => Grid(("p", 0, 1_000_000_000_000_000m, 0.000000001m)));
        ToolException huge = Refused(() => Grid(("p", decimal.MinValue, decimal.MaxValue, 1)));

        Assert.Equal(ToolErrorCodes.LimitExceeded, tiny.Code);
        Assert.Equal(ToolErrorCodes.LimitExceeded, huge.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AStepOfZeroOrLessIsInvalid(int step)
    {
        ToolException error = Refused(() => Grid(("p", 1, 5, step)));

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("parameterRanges[0].step", error.Parameter);
    }

    [Fact]
    public void AStartAboveTheEndIsInvalid()
    {
        ToolException error = Refused(() => Grid(("a", 1, 5, 1), ("b", 9, 3, 1)));

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("parameterRanges[1].start", error.Parameter);
    }

    [Fact]
    public void ANameThatIsEmptyOrUsedTwiceIsInvalid()
    {
        Assert.Equal("parameterRanges[0].name", Refused(() => Grid((" ", 1, 5, 1))).Parameter);

        ToolException twice = Refused(() => Grid(("p", 1, 5, 1), ("p", 1, 5, 1)));
        Assert.Equal(ToolErrorCodes.InvalidArgument, twice.Code);
        Assert.Equal("parameterRanges[1].name", twice.Parameter);
    }

    [Fact]
    public void NoRangeAtAllIsInvalid()
    {
        Assert.Equal(ToolErrorCodes.InvalidArgument, Refused(() => BacktestParameterGrid.Create(Array.Empty<ParameterRange>(), Limit)).Code);
    }

    // ---- placeholders ------------------------------------------------------------------------------------------------------------------

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void PlaceholdersAreFoundInDocumentOrderWithTheirPlace()
    {
        JsonNode conditions = Node("""
            {"entryLong":{"left":{"indicator":"RSI","parameters":{"Period":{"$param":"period"}}},"comparison":"LessThan","right":{"$param":"level"}},
             "exitLong":{"operator":"And","children":[{"left":{"price":"Close","offset":{"$param":"shift"}},"comparison":"GreaterThan","right":{"$param":"level"}}]}}
            """);

        IReadOnlyList<(string Name, string Path)> found = BacktestParameterGrid.FindPlaceholders(conditions, "conditions");

        Assert.Equal(new[] { "period", "level", "shift", "level" }, found.Select(f => f.Name));
        Assert.Equal("conditions.entryLong.left.parameters.Period", found[0].Path);
        Assert.Equal("conditions.exitLong.children[0].left.offset", found[2].Path);
    }

    [Theory]
    [InlineData("""{"x":{"$param":"a","other":1}}""")]
    [InlineData("""{"x":{"$param":5}}""")]
    [InlineData("""{"x":{"$param":""}}""")]
    [InlineData("""{"x":{"$param":null}}""")]
    public void AMalformedPlaceholderIsInvalidAndNamesItsPlace(string json)
    {
        ToolException error = Refused(() => BacktestParameterGrid.FindPlaceholders(Node(json), "conditions"));

        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("conditions.x", error.Parameter);
    }

    [Fact]
    public void SubstitutionReplacesEveryPlaceholderWithAWholeOrFractionalNumberAndLeavesTheRest()
    {
        JsonNode conditions = Node("""{"a":{"parameters":{"Period":{"$param":"p"}}},"right":{"$param":"l"},"list":[1,{"$param":"p"},"text",null,true]}""");
        var values = new Dictionary<string, decimal> { ["p"] = BacktestParameterGrid.Normalize(14.0m), ["l"] = 0.25m };

        JsonNode result = BacktestParameterGrid.Substitute(conditions, values)!;

        Assert.Equal("""{"a":{"parameters":{"Period":14}},"right":0.25,"list":[1,14,"text",null,true]}""", result.ToJsonString());
        Assert.Empty(BacktestParameterGrid.FindPlaceholders(result, "conditions"));
    }

    [Fact]
    public void SubstitutionDoesNotChangeTheTemplate()
    {
        JsonNode conditions = Node("""{"right":{"$param":"l"}}""");
        string before = conditions.ToJsonString();

        _ = BacktestParameterGrid.Substitute(conditions, new Dictionary<string, decimal> { ["l"] = 3m });

        Assert.Equal(before, conditions.ToJsonString());
    }

    [Fact]
    public void ASubstitutedValueReadsBackAsAnIntegerWhenItIsWhole()
    {
        BacktestParameterGrid grid = Grid(("p", 10.0m, 12.0m, 1.0m));
        var values = grid.ValuesAt(1).ToDictionary(pair => pair.Key, pair => pair.Value);

        JsonNode result = BacktestParameterGrid.Substitute(Node("""{"Period":{"$param":"p"}}"""), values)!;

        Assert.True(result["Period"]!.GetValue<decimal>() == 11m);
        Assert.Equal("""{"Period":11}""", result.ToJsonString());
    }
}
