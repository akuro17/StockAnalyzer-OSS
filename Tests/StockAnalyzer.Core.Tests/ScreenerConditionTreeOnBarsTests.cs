using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="ScreenerConditionTreeEvaluator.EvaluateOnBars"/> folds a Filters tree exactly like <see cref="ScreenerConditionTreeEvaluator.Evaluate(IScreenerConditionNode, IReadOnlyList{CandleData}, StockAnalyzer.Core.Models.Portfolio.TickerMetadata)"/>:
/// the two folds are written side by side, so these tests are what keeps them the same.
/// </summary>
public sealed class ScreenerConditionTreeOnBarsTests
{
    private static readonly ScreenerIndicatorEntry True = Versus(Close(), ComparisonOperator.GreaterThan, 0m);
    private static readonly ScreenerIndicatorEntry False = Versus(Close(), ComparisonOperator.LessThan, 0m);

    private static IEnumerable<(string Name, IScreenerConditionNode Tree)> Trees()
    {
        ScreenerIndicatorEntry Disabled(ScreenerIndicatorEntry entry) => Versus(entry.LeftHand, entry.Operator, entry.RightNumericValue, enabled: false);

        yield return ("leaf true", Leaf(True));
        yield return ("leaf false", Leaf(False));
        yield return ("disabled root leaf", Leaf(Disabled(True)));
        yield return ("empty and", Group(LogicalOperator.And));
        yield return ("empty or", Group(LogicalOperator.Or));
        yield return ("and true true", Group(LogicalOperator.And, Leaf(True), Leaf(True)));
        yield return ("and true false", Group(LogicalOperator.And, Leaf(True), Leaf(False)));
        yield return ("or false true", Group(LogicalOperator.Or, Leaf(False), Leaf(True)));
        yield return ("or false false", Group(LogicalOperator.Or, Leaf(False), Leaf(False)));
        yield return ("and with a disabled false (absent, not false)", Group(LogicalOperator.And, Leaf(True), Leaf(Disabled(False))));
        yield return ("or with a disabled true (absent, not true)", Group(LogicalOperator.Or, Leaf(False), Leaf(Disabled(True))));
        yield return ("all children disabled", Group(LogicalOperator.And, Leaf(Disabled(True)), Leaf(Disabled(True))));
        yield return ("nested", Group(LogicalOperator.And, Leaf(True), Group(LogicalOperator.Or, Leaf(False), Group(LogicalOperator.And, Leaf(True), Leaf(True)))));
        yield return ("nested all absent", Group(LogicalOperator.Or, Group(LogicalOperator.And, Leaf(Disabled(True))), Leaf(True)));
        yield return ("an empty child group beside a false leaf", Group(LogicalOperator.Or, Group(LogicalOperator.And), Leaf(False)));
    }

    [Fact]
    public void TheFoldOnBarsEqualsTheOriginalFoldForEveryTreeOfDailySides()
    {
        List<CandleData> daily = Daily(30);
        ScreenerBarSet set = BarSet();

        foreach ((string name, IScreenerConditionNode tree) in Trees())
        {
            Assert.True(
                ScreenerConditionTreeEvaluator.Evaluate(tree, daily, default) == ScreenerConditionTreeEvaluator.EvaluateOnBars(tree, set, default),
                $"The two folds disagree for the tree '{name}'.");
        }
    }

    [Fact]
    public void ASideOnAnotherFrameChangesTheAnswerOfTheTreeAsItChangesTheLeaf()
    {
        // The weekly close is 127 and the daily close 129: the same comparison is true on one frame and false on the other.
        var weekly = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m);
        var daily = Versus(Close(), ComparisonOperator.Equal, 127m);

        Assert.True(ScreenerConditionTreeEvaluator.EvaluateOnBars(Group(LogicalOperator.And, Leaf(weekly)), BarSet(), default));
        Assert.False(ScreenerConditionTreeEvaluator.EvaluateOnBars(Group(LogicalOperator.And, Leaf(daily)), BarSet(), default));
        Assert.True(ScreenerConditionTreeEvaluator.EvaluateOnBars(Group(LogicalOperator.Or, Leaf(daily), Leaf(weekly)), BarSet(), default));
        Assert.False(ScreenerConditionTreeEvaluator.EvaluateOnBars(Group(LogicalOperator.And, Leaf(daily), Leaf(weekly)), BarSet(), default));
    }

    [Fact]
    public async Task TheTreeConditionAnswersThroughTheAsyncMember()
    {
        var tree = new ScreenerConditionTreeCondition(Group(LogicalOperator.And, Leaf(Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m))));

        Assert.True(await ((IScreeningCondition)tree).IsMetOnBarsAsync(BarSet(), default));
        Assert.True(await tree.IsMetOnBarsAsync(BarSet(), default));
    }

    [Fact]
    public void TheTreeWithoutAnActiveLeafIsFalse()
    {
        IScreenerConditionNode root = Group(LogicalOperator.And, Leaf(Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m, enabled: false)));

        Assert.False(ScreenerConditionTreeEvaluator.HasActiveLeaf(root));
        Assert.False(ScreenerConditionTreeEvaluator.EvaluateOnBars(root, BarSet(), default));
    }
}
