using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// Objective verification of <see cref="ScreenerConditionTreeEvaluator"/> (docs/sa_implementation_plan_ScreenerFilterConditionTree
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

    private static ScreenerConditionGroup And(params IScreenerConditionNode[] children)
        => new(LogicalOperator.And, children.ToImmutableArray());

    private static ScreenerConditionGroup Or(params IScreenerConditionNode[] children)
        => new(LogicalOperator.Or, children.ToImmutableArray());

    [Fact]
    public void MultiLeafAndGroup_ManySiblings_EvaluatesCorrectly()
    {
        // 16 leaves: all true => true
        var allTrue = And(Enumerable.Range(0, 16).Select(_ => (IScreenerConditionNode)Leaf(true)).ToArray());
        Assert.True(Evaluate(allTrue));

        // 15 true, 1 false => false
        var oneFalse = And(Enumerable.Range(0, 15).Select(_ => (IScreenerConditionNode)Leaf(true))
            .Concat(new[] { (IScreenerConditionNode)Leaf(false) }).ToArray());
        Assert.False(Evaluate(oneFalse));

        // 15 true, 1 disabled false => true (disabled is absent)
        var oneDisabledFalse = And(Enumerable.Range(0, 15).Select(_ => (IScreenerConditionNode)Leaf(true))
            .Concat(new[] { (IScreenerConditionNode)Leaf(false, isEnabled: false) }).ToArray());
        Assert.True(Evaluate(oneDisabledFalse));
    }

    [Fact]
    public void MultiLeafOrGroup_ManySiblings_EvaluatesCorrectly()
    {
        // 16 leaves: all false => false
        var allFalse = Or(Enumerable.Range(0, 16).Select(_ => (IScreenerConditionNode)Leaf(false)).ToArray());
        Assert.False(Evaluate(allFalse));

        // 15 false, 1 true => true
        var oneTrue = Or(Enumerable.Range(0, 15).Select(_ => (IScreenerConditionNode)Leaf(false))
            .Concat(new[] { (IScreenerConditionNode)Leaf(true) }).ToArray());
        Assert.True(Evaluate(oneTrue));

        // 15 false, 1 disabled true => false (disabled is absent)
        var oneDisabledTrue = Or(Enumerable.Range(0, 15).Select(_ => (IScreenerConditionNode)Leaf(false))
            .Concat(new[] { (IScreenerConditionNode)Leaf(true, isEnabled: false) }).ToArray());
        Assert.False(Evaluate(oneDisabledTrue));
    }

    [Theory]
    [InlineData(true, false, true, false, true, true)]    // A=T, B=F, C=T, D=F, E=T => E makes (D or E)=T, C and T=T, B or T=T, A and T=T
    [InlineData(true, false, true, false, false, false)]  // A=T, B=F, C=T, D=F, E=F => (D or E)=F, C and F=F, B or F=F, A and F=F
    [InlineData(false, false, true, false, true, false)]  // A=F => Root AND is false immediately
    [InlineData(true, true, false, false, false, true)]   // A=T, B=T => Level 2 OR is true via B, Root AND is true
    public void FourLevelNestedTree_AlternatingAndOr_EvaluatesCorrectly(bool a, bool b, bool c, bool d, bool e, bool expected)
    {
        // Level 1: Root AND (Leaf(a), Level 2: Group OR (Leaf(b), Level 3: Group AND (Leaf(c), Level 4: Group OR (Leaf(d), Leaf(e)))))
        var level4 = Or(Leaf(d), Leaf(e));
        var level3 = And(Leaf(c), level4);
        var level2 = Or(Leaf(b), level3);
        var level1 = And(Leaf(a), level2);

        Assert.Equal(expected, Evaluate(level1));
    }

    [Fact]
    public void DeepNestedTree_DisabledSubtree_BecomesAbsentWithoutCorruptingParentLogic()
    {
        // Level 4 has all disabled leaves => Level 4 is absent (null)
        // Level 3 AND has Leaf(C=true) and absent Level 4 => Level 3 evaluates to true!
        // Level 2 OR has Leaf(B=false) and Level 3(true) => Level 2 evaluates to true!
        // Level 1 AND has Leaf(A=true) and Level 2(true) => Level 1 evaluates to true!
        var level4Disabled = Or(Leaf(false, isEnabled: false), Leaf(false, isEnabled: false));
        var level3 = And(Leaf(true), level4Disabled);
        var level2 = Or(Leaf(false), level3);
        var level1 = And(Leaf(true), level2);

        Assert.True(Evaluate(level1));

        // But if Level 3's Leaf C is also disabled, entire Level 3 is absent.
        // Level 2 OR only has Leaf B (false) => Level 2 evaluates to false.
        // Level 1 AND has Leaf A (true) and Level 2 (false) => Level 1 evaluates to false.
        var level3AllDisabled = And(Leaf(true, isEnabled: false), level4Disabled);
        var level2WithDisabledSubtree = Or(Leaf(false), level3AllDisabled);
        var level1WithDisabledSubtree = And(Leaf(true), level2WithDisabledSubtree);

        Assert.False(Evaluate(level1WithDisabledSubtree));
    }

    [Fact]
    public void FullTruthTable_FiveVariableNestedTree_MatchesBooleanAlgebraExactly()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            bool a = (mask & 1) != 0;
            bool b = (mask & 2) != 0;
            bool c = (mask & 4) != 0;
            bool d = (mask & 8) != 0;
            bool e = (mask & 16) != 0;

            bool expected = a && (b || (c && (d || e)));

            var level4 = Or(Leaf(d), Leaf(e));
            var level3 = And(Leaf(c), level4);
            var level2 = Or(Leaf(b), level3);
            var level1 = And(Leaf(a), level2);

            bool actual = Evaluate(level1);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void PropertyBased_PruningDisabledBranches_YieldsIdenticalEvaluation()
    {
        // For each of the 32 enablement subsets of 5 leaves in A AND (B OR (C AND (D OR E))),
        // evaluating the original tree (with disabled leaves) must match evaluating the pruned tree (where disabled leaves are removed).
        for (int enableMask = 0; enableMask < 32; enableMask++)
        {
            bool enA = (enableMask & 1) != 0;
            bool enB = (enableMask & 2) != 0;
            bool enC = (enableMask & 4) != 0;
            bool enD = (enableMask & 8) != 0;
            bool enE = (enableMask & 16) != 0;

            // Give true/false values to the leaves
            var leafA = Leaf(true, isEnabled: enA);
            var leafB = Leaf(false, isEnabled: enB);
            var leafC = Leaf(true, isEnabled: enC);
            var leafD = Leaf(false, isEnabled: enD);
            var leafE = Leaf(true, isEnabled: enE);

            var level4 = Or(leafD, leafE);
            var level3 = And(leafC, level4);
            var level2 = Or(leafB, level3);
            var unprunedTree = And(leafA, level2);

            bool originalResult = Evaluate(unprunedTree);

            IScreenerConditionNode? prunedTree = PruneDisabled(unprunedTree);
            bool prunedResult = prunedTree is not null && Evaluate(prunedTree);

            Assert.Equal(originalResult, prunedResult);
        }
    }

    private static IScreenerConditionNode? PruneDisabled(IScreenerConditionNode node)
    {
        if (node is ScreenerConditionLeaf leaf)
        {
            return leaf.Entry.IsEnabled ? leaf : null;
        }
        if (node is ScreenerConditionGroup group)
        {
            var activeChildren = group.Children
                .Select(PruneDisabled)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToImmutableArray();

            if (activeChildren.Length == 0) return null;
            return new ScreenerConditionGroup(group.Operator, activeChildren, group.Name);
        }
        return node;
    }

    [Fact]
    public void DeepNestedTree_EntireTreeDisabled_ReturnsFalse()
    {
        var level4 = Or(Leaf(true, isEnabled: false), Leaf(true, isEnabled: false));
        var level3 = And(Leaf(true, isEnabled: false), level4);
        var level2 = Or(Leaf(true, isEnabled: false), level3);
        var level1 = And(Leaf(true, isEnabled: false), level2);

        Assert.False(Evaluate(level1));
        Assert.False(ScreenerConditionTreeEvaluator.HasActiveLeaf(level1));
    }

    private static ScreenerConditionLeaf PriceLeaf(bool expectedResult, bool isEnabled = true) => new(new ScreenerIndicatorEntry
    {
        LeftHand = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Price,
            OutputName = "Close",
            Offset = 0
        },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = expectedResult ? 50m : 150m,
        IsEnabled = isEnabled
    });

    [Fact]
    public void Evaluate_ZeroAllocation_OnEvaluationPath()
    {
        var level4 = Or(PriceLeaf(false), PriceLeaf(true));
        var level3 = And(PriceLeaf(true), level4);
        var level2 = Or(PriceLeaf(false), level3);
        var tree = And(PriceLeaf(true), level2);

        // Warm up loop to allow Tier-1 JIT and PGO to complete
        for (int i = 0; i < 2000; i++)
        {
            _ = Evaluate(tree);
        }

        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            _ = Evaluate(tree);
        }

        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();
        long diff = afterAllocated - beforeAllocated;

        Assert.Equal(0, diff);
    }

    private sealed class UnsupportedNode : IScreenerConditionNode
    {
    }
}
