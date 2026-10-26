using System;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Pure validator for a Screener Filters condition tree (<see cref="IScreenerConditionNode"/>).
/// Enforces system and configured bounds: execution stack depth, tree depth (root = 1) &lt;= maxDepth,
/// total node count &lt;= maxNodes, offset &lt;= maxOffset, and valid group/leaf node contracts.
/// </summary>
public static class ScreenerConditionTreeValidator
{
    public const string NestingTooDeepMessage = "The condition tree is nested too deep.";
    private const string RootParamName = "root";

    /// <summary>
    /// Validates the structure and limits of <paramref name="root"/>.
    /// Throws <see cref="ArgumentNullException"/> if <paramref name="root"/> is null.
    /// Throws <see cref="ArgumentOutOfRangeException"/> if limit parameters are outside their system bands or if limits are exceeded.
    /// Throws <see cref="ArgumentException"/> on structural invalidity or execution stack exhaustion.
    /// </summary>
    public static void Validate(
        IScreenerConditionNode root,
        int? maxOffset = null,
        int? maxDepth = null,
        int? maxNodes = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        int depthCeiling = (int)ScreenerFilterTreeLimits.MaxFilterTreeDepth.HardMax;
        if (maxDepth is < 1 || maxDepth > depthCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, $"maxDepth must be within [1, {depthCeiling}].");
        }

        int nodesCeiling = (int)ScreenerFilterTreeLimits.MaxFilterTreeNodes.HardMax;
        if (maxNodes is < 1 || maxNodes > nodesCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), maxNodes, $"maxNodes must be within [1, {nodesCeiling}].");
        }

        int offsetCeiling = (int)ScreenerConditionOffsetLimits.MaxConditionOffset.HardMax;
        if (maxOffset is < 0 || maxOffset > offsetCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOffset), maxOffset, $"maxOffset must be within [0, {offsetCeiling}].");
        }

        if (maxDepth is { } d && maxNodes is { } n && d > n)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), d,
                $"maxDepth ({d}) must not exceed maxNodes ({n}): a tree cannot be deeper than it has nodes.");
        }

        int effectiveMaxDepth = maxDepth ?? ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth;
        int effectiveMaxNodes = maxNodes ?? ScreenerFilterTreeLimits.DefaultMaxFilterTreeNodes;
        int effectiveMaxOffset = maxOffset ?? ScreenerConditionOffsetLimits.DefaultMaxConditionOffset;

        int nodeCount = 0;
        try
        {
            ValidateNode(root, depth: 1, isRoot: true, effectiveMaxOffset, effectiveMaxDepth, effectiveMaxNodes, ref nodeCount);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(NestingTooDeepMessage, nameof(root));
        }
    }

    private static void ValidateNode(
        IScreenerConditionNode node,
        int depth,
        bool isRoot,
        int effectiveMaxOffset,
        int effectiveMaxDepth,
        int effectiveMaxNodes,
        ref int nodeCount)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        nodeCount++;
        if (nodeCount > effectiveMaxNodes)
        {
            throw new ArgumentOutOfRangeException(
                RootParamName,
                nodeCount,
                $"The condition tree has more than {effectiveMaxNodes} nodes, exceeding the configured Screener:MaxFilterTreeNodes ({effectiveMaxNodes}).");
        }

        if (depth > effectiveMaxDepth)
        {
            throw new ArgumentOutOfRangeException(
                RootParamName,
                depth,
                $"The condition tree depth {depth} exceeds the configured Screener:MaxFilterTreeDepth of {effectiveMaxDepth} levels.");
        }

        switch (node)
        {
            case ScreenerConditionGroup group:
                if (!Enum.IsDefined(typeof(LogicalOperator), group.Operator))
                {
                    throw new ArgumentException("Condition group operator is undefined.", RootParamName);
                }

                if (group.Children.IsDefault)
                {
                    throw new ArgumentException("Condition group children collection cannot be uninitialized.", RootParamName);
                }

                if (!isRoot && group.Children.Length == 0)
                {
                    throw new ArgumentException("A nested condition group must contain at least one condition.", RootParamName);
                }

                for (int i = 0; i < group.Children.Length; i++)
                {
                    IScreenerConditionNode child = group.Children[i]
                        ?? throw new ArgumentException($"Children[{i}] must not be null.", RootParamName);
                    ValidateNode(child, depth + 1, isRoot: false, effectiveMaxOffset, effectiveMaxDepth, effectiveMaxNodes, ref nodeCount);
                }
                break;

            case ScreenerConditionLeaf leaf:
                if (leaf.Entry is null)
                {
                    throw new ArgumentException("Condition leaf Entry must not be null.", RootParamName);
                }

                if (!Enum.IsDefined(typeof(ComparisonOperator), leaf.Entry.Operator))
                {
                    throw new ArgumentException($"Undefined comparison operator {leaf.Entry.Operator}.", RootParamName);
                }

                if (!Enum.IsDefined(typeof(RightHandTargetMode), leaf.Entry.TargetMode))
                {
                    throw new ArgumentException($"Undefined right hand target mode {leaf.Entry.TargetMode}.", RootParamName);
                }

                if (leaf.Entry.LeftHand is null)
                {
                    throw new ArgumentException("Condition leaf LeftHand must not be null.", RootParamName);
                }

                ValidateOffset(leaf.Entry.LeftHand.Offset, "LeftHand", effectiveMaxOffset);

                if (leaf.Entry.TargetMode == RightHandTargetMode.Indicator)
                {
                    if (leaf.Entry.RightHand is null)
                    {
                        throw new ArgumentException("Condition leaf RightHand is required when TargetMode is Indicator.", RootParamName);
                    }
                    ValidateOffset(leaf.Entry.RightHand.Offset, "RightHand", effectiveMaxOffset);
                }
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported condition node type {(node is null ? "null" : node.GetType().Name)}.", RootParamName);
        }
    }

    private static void ValidateOffset(int offset, string sideName, int effectiveMaxOffset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"Condition leaf {sideName} Offset must be >= 0 (negative offsets imply lookahead).");
        }

        if (offset > effectiveMaxOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"The condition uses an Offset of {offset} bars on {sideName}, above the configured Screener:MaxConditionOffset of {effectiveMaxOffset}.");
        }
    }
}
