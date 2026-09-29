using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// Objective verification of <see cref="ScreenerConditionTreeEvaluator"/> (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md
/// Task 3, decision A: the previous per-entry sequential AND/OR fold in ScreenerViewModel.ScanAsync is replaced by this recursive tree
/// evaluation). Each leaf uses SMA(period=1) against a single candle, which is mathematically the candle's own Close - a deterministic,
/// real (non-mocked) IScreeningCondition result, per CLAUDE.md's "Mocking ScreenerIndicatorEntry PROHIBITED" invariant.
/// </summary>
public class ScreenerConditionTreeEvaluatorTests
{
    private static readonly IReadOnlyList<CandleData> Candles = new[]
    {
        new CandleData(DateTime.UtcNow, 100m, 100m, 100m, 100m, 1000)
    };

    /// <summary>An entry whose IsMet(Candles, default) is <paramref name="expectedResult"/> by construction (Close=100 vs a threshold on either side).</summary>
    private static ScreenerConditionLeaf Leaf(bool expectedResult, bool isEnabled = true) => new(new ScreenerIndicatorEntry
    {
        LeftHand = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            Parameters = new Dictionary<string, object> { { "Period", 1 } },
            TimeFrame = TimeFrame.D1,
            Offset = 0
        },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = expectedResult ? 50m : 150m, // Close(100) > 50 => true; Close(100) > 150 => false
        IsEnabled = isEnabled
    });

    private static bool Evaluate(IScreenerConditionNode node) => ScreenerConditionTreeEvaluator.Evaluate(node, Candles, default(TickerMetadata));

    [Fact]
    public void EmptyRoot_IsFalse()
    {
        Assert.False(Evaluate(ScreenerConditionGroup.EmptyAnd));
    }

    [Fact]
    public void SingleTrueLeaf_UnderAndRoot_IsTrue()
    {
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true)));
        Assert.True(Evaluate(root));
    }

    [Fact]
    public void SingleFalseLeaf_UnderAndRoot_IsFalse()
    {
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(false)));
        Assert.False(Evaluate(root));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void AndGroup_RequiresEveryChildTrue(bool a, bool b, bool expected)
    {
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(a), Leaf(b)));
        Assert.Equal(expected, Evaluate(root));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void OrGroup_RequiresAtLeastOneChildTrue(bool a, bool b, bool expected)
    {
        var root = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(Leaf(a), Leaf(b)));
        Assert.Equal(expected, Evaluate(root));
    }

    [Fact]
    public void NestedGroup_EvaluatesItsOwnOperatorIndependently()
    {
        // Root AND [ (B OR C) ], with A/D as leaves: A(true) AND (B(false) OR C(true)) => true AND true => true
        var nested = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(Leaf(false), Leaf(true)));
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true), nested));
        Assert.True(Evaluate(root));
    }

    [Fact]
    public void NestedGroup_FalseNestedOrMakesAndRootFalse()
    {
        // A(true) AND (B(false) OR C(false)) => true AND false => false
        var nested = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(Leaf(false), Leaf(false)));
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true), nested));
        Assert.False(Evaluate(root));
    }

    [Fact]
    public void DisabledLeaf_IsExcludedFromAndGroup_NotTreatedAsFalse()
    {
        // A(true) AND B(disabled, would-be-false) => B is absent, so the group is just A(true) => true.
        // (If disabled were treated as false instead of absent, this would wrongly evaluate to false.)
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true), Leaf(false, isEnabled: false)));
        Assert.True(Evaluate(root));
    }

    [Fact]
    public void DisabledLeaf_IsExcludedFromOrGroup_NotTreatedAsTrue()
    {
        // A(false) OR B(disabled, would-be-true) => B is absent, so the group is just A(false) => false.
        var root = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(Leaf(false), Leaf(true, isEnabled: false)));
        Assert.False(Evaluate(root));
    }

    [Fact]
    public void GroupWithEveryLeafDisabled_IsFalse_LikeAnEmptyGroup()
    {
        var root = new ScreenerConditionGroup(LogicalOperator.Or, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true, isEnabled: false), Leaf(true, isEnabled: false)));
        Assert.False(Evaluate(root));
        Assert.False(ScreenerConditionTreeEvaluator.HasActiveLeaf(root));
    }

    [Fact]
    public void HasActiveLeaf_TrueWhenAtLeastOneEnabledLeafExistsAnywhereInTheTree()
    {
        var nested = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(Leaf(true, isEnabled: false)));
        var root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(nested, Leaf(true, isEnabled: true)));
        Assert.True(ScreenerConditionTreeEvaluator.HasActiveLeaf(root));
    }

    [Fact]
    public void Evaluate_ThrowsForUnsupportedNodeType()
    {
        Assert.Throws<ArgumentException>(() => ScreenerConditionTreeEvaluator.Evaluate(new UnsupportedNode(), Candles, default));
    }

    private sealed class UnsupportedNode : IScreenerConditionNode
    {
    }
}
