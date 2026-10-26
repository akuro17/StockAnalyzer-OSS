using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

public class ScreenerConditionTreeValidatorTests
{
    private static readonly int DefaultDepth = ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth; // 8
    private static readonly int DefaultNodes = ScreenerFilterTreeLimits.DefaultMaxFilterTreeNodes; // 64
    private const int DefaultOffset = 10;

    private static ScreenerIndicatorEntry MakeEntry(int leftOffset = 0, int rightOffset = 0, RightHandTargetMode mode = RightHandTargetMode.NumericValue) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            Parameters = new() { { "Period", 20 } },
            Offset = leftOffset
        },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = mode,
        RightNumericValue = 50m,
        RightHand = mode == RightHandTargetMode.Indicator
            ? new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.EMA,
                Parameters = new() { { "Period", 50 } },
                Offset = rightOffset
            }
            : new()
    };

    private static ScreenerConditionLeaf Leaf(int leftOffset = 0) => new(MakeEntry(leftOffset));

    private static ScreenerConditionGroup Group(LogicalOperator op, params IScreenerConditionNode[] children)
        => new(op, children.ToImmutableArray());

    /// <summary>Creates a tree whose deepest node sits at exactly <paramref name="depth"/> (root = 1) with 1 leaf.</summary>
    private static ScreenerConditionGroup ChainOfDepth(int depth)
    {
        ScreenerConditionGroup current = Group(LogicalOperator.And, Leaf());
        for (int level = depth - 1; level >= 2; level--)
        {
            current = Group(level % 2 == 0 ? LogicalOperator.And : LogicalOperator.Or, current);
        }
        return current;
    }

    [Fact]
    public void Validate_NullRoot_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ScreenerConditionTreeValidator.Validate(null!));
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(-1, 10, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, -1, 10)]
    [InlineData(10, 10, -1)]
    public void Validate_InvalidBounds_ThrowsArgumentOutOfRangeException(int depth, int nodes, int offset)
    {
        var root = Group(LogicalOperator.And, Leaf());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, maxOffset: offset, maxDepth: depth, maxNodes: nodes));
    }

    [Fact]
    public void Validate_EmptyRootGroup_Succeeds()
    {
        // An empty root is the default/inactive state of a tree before conditions are registered.
        ScreenerConditionTreeValidator.Validate(ScreenerConditionGroup.EmptyAnd, DefaultOffset, DefaultDepth, DefaultNodes);
    }

    [Fact]
    public void Validate_EmptyNestedGroup_ThrowsArgumentException()
    {
        var nestedEmpty = Group(LogicalOperator.Or);
        var root = Group(LogicalOperator.And, Leaf(), nestedEmpty);

        var ex = Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("nested condition group must contain at least one condition", ex.Message);
    }

    [Fact]
    public void Validate_DepthAtLimit_Passes_AndExceedingThrows()
    {
        var atLimit = ChainOfDepth(DefaultDepth);
        ScreenerConditionTreeValidator.Validate(atLimit, DefaultOffset, DefaultDepth, DefaultNodes);

        var aboveLimit = ChainOfDepth(DefaultDepth + 1);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(aboveLimit, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("Screener:MaxFilterTreeDepth", ex.Message);
    }

    [Fact]
    public void Validate_NodesAtLimit_Passes_AndExceedingThrows()
    {
        // Root + (DefaultNodes - 1) leaves = exactly DefaultNodes
        var atLimit = Group(LogicalOperator.Or, Enumerable.Range(0, DefaultNodes - 1).Select(_ => (IScreenerConditionNode)Leaf()).ToArray());
        ScreenerConditionTreeValidator.Validate(atLimit, DefaultOffset, DefaultDepth, DefaultNodes);

        // Root + DefaultNodes leaves = DefaultNodes + 1
        var aboveLimit = Group(LogicalOperator.Or, Enumerable.Range(0, DefaultNodes).Select(_ => (IScreenerConditionNode)Leaf()).ToArray());
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(aboveLimit, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("Screener:MaxFilterTreeNodes", ex.Message);
    }

    [Fact]
    public void Validate_NegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        var badLeaf = new ScreenerConditionLeaf(MakeEntry(leftOffset: -1));
        var root = Group(LogicalOperator.And, badLeaf);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("must be >= 0", ex.Message);
    }

    [Fact]
    public void Validate_OffsetExceedingMax_ThrowsArgumentOutOfRangeException()
    {
        var badLeaf = new ScreenerConditionLeaf(MakeEntry(leftOffset: DefaultOffset + 1));
        var root = Group(LogicalOperator.And, badLeaf);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("Screener:MaxConditionOffset", ex.Message);
    }

    [Fact]
    public void Validate_TargetModeIndicator_MissingRightHand_ThrowsArgumentException()
    {
        var entry = MakeEntry(mode: RightHandTargetMode.Indicator);
        entry.RightHand = null!;
        var badLeaf = new ScreenerConditionLeaf(entry);
        var root = Group(LogicalOperator.And, badLeaf);

        Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
    }

    [Fact]
    public void Validate_TargetModeIndicator_RightOffsetExceedingMax_Throws()
    {
        var entry = MakeEntry(leftOffset: 0, rightOffset: DefaultOffset + 1, mode: RightHandTargetMode.Indicator);
        var badLeaf = new ScreenerConditionLeaf(entry);
        var root = Group(LogicalOperator.And, badLeaf);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("RightHand", ex.Message);
    }

    [Fact]
    public void Validate_UnsupportedNodeType_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(new UnsupportedNode(), DefaultOffset, DefaultDepth, DefaultNodes));
    }

    [Fact]
    public void Validate_UninitializedImmutableArray_ThrowsArgumentException()
    {
        var uninitialized = (ScreenerConditionGroup)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ScreenerConditionGroup));
        var ex = Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(uninitialized, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("children collection cannot be uninitialized", ex.Message);
    }

    [Fact]
    public void Validate_UndefinedComparisonOperator_ThrowsArgumentException()
    {
        var entry = MakeEntry();
        entry.Operator = (ComparisonOperator)999;
        var root = Group(LogicalOperator.And, new ScreenerConditionLeaf(entry));

        var ex = Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("Undefined comparison operator", ex.Message);
    }

    [Fact]
    public void Validate_UndefinedTargetMode_ThrowsArgumentException()
    {
        var entry = MakeEntry();
        entry.TargetMode = (RightHandTargetMode)999;
        var root = Group(LogicalOperator.And, new ScreenerConditionLeaf(entry));

        var ex = Assert.Throws<ArgumentException>(() =>
            ScreenerConditionTreeValidator.Validate(root, DefaultOffset, DefaultDepth, DefaultNodes));
        Assert.Contains("Undefined right hand target mode", ex.Message);
    }

    [Fact]
    public void Validate_NullBounds_UsesDefaultsAndHardLimits()
    {
        var normalTree = Group(LogicalOperator.And, Leaf());
        // Default bounds should permit normal tree
        ScreenerConditionTreeValidator.Validate(normalTree);

        // When bounds are null, default max depth is DefaultMaxFilterTreeDepth (8). Depth 9 should throw.
        var deepTree = ChainOfDepth(ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth + 1);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(deepTree));
        Assert.Contains("Screener:MaxFilterTreeDepth", ex.Message);
        Assert.Equal("root", ex.ParamName);
    }

    [Theory]
    [InlineData(17, 10, 500)]
    [InlineData(10, 257, 500)]
    [InlineData(10, 50, 2501)]
    public void Validate_BoundsExceedingHardCeilings_ThrowsArgumentOutOfRangeException(int depth, int nodes, int offset)
    {
        var root = Group(LogicalOperator.And, Leaf());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, maxOffset: offset, maxDepth: depth, maxNodes: nodes));
    }

    [Fact]
    public void Validate_DepthExceedingNodes_ThrowsArgumentOutOfRangeException()
    {
        var root = Group(LogicalOperator.And, Leaf());
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenerConditionTreeValidator.Validate(root, maxOffset: 500, maxDepth: 10, maxNodes: 8));
        Assert.Equal("maxDepth", ex.ParamName);
        Assert.Contains("must not exceed maxNodes", ex.Message);
    }

    private sealed class UnsupportedNode : IScreenerConditionNode
    {
    }
}
