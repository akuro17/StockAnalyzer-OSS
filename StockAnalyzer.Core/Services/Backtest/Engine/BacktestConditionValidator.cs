using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>Single ownership and validation boundary for condition-based backtest input.</summary>
public static class BacktestConditionValidator
{
    public static ImmutableArray<BacktestConditionEntry> SnapshotDtos(
        IReadOnlyList<BacktestConditionEntryDto> entries,
        int maxOffset)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var domainEntries = new List<BacktestConditionEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntryDto dto = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            domainEntries.Add(ToDomainEntry(dto, i, nameof(entries)));
        }

        return Snapshot(domainEntries, maxOffset);
    }

    /// <summary>Converts one persisted comparison to its domain entry (shared by the list and the tree conversion; messages name <paramref name="index"/> and report <paramref name="paramName"/>).</summary>
    internal static BacktestConditionEntry ToDomainEntry(BacktestConditionEntryDto dto, int index, string paramName)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new BacktestConditionEntry
        {
            Left = ToSide(dto.Left, index, "Left", paramName),
            Operator = dto.Operator,
            TargetMode = dto.TargetMode,
            RightNumericValue = dto.RightNumericValue,
            Right = dto.Right is null ? null : ToSide(dto.Right, index, "Right", paramName),
            LogicalOperator = dto.LogicalOperator,
            Role = dto.Role,
            Position = dto.Position,
        };
    }

    private static BacktestConditionSide ToSide(BacktestConditionSideDto? dto, int index, string name, string paramName)
    {
        if (dto is null)
        {
            throw new ArgumentException($"ConditionEntries[{index}].{name} must not be null.", paramName);
        }
        return new BacktestConditionSide
        {
            IndicatorType = dto.IndicatorType,
            Parameters = dto.Parameters?.Clone(),
            OutputName = dto.OutputName,
            Offset = dto.Offset,
            Frame = dto.Frame,
            PriceSource = dto.PriceSource,
        };
    }

    public static ImmutableArray<BacktestConditionEntry> Snapshot(
        IReadOnlyList<BacktestConditionEntry> entries,
        int? maxOffset = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = ImmutableArray.CreateBuilder<BacktestConditionEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntry entry = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            ValidateEntry(entry, i, maxOffset);
            builder.Add(CloneEntry(entry));
        }

        return builder.MoveToImmutable();
    }

    public static IEnumerable<BacktestConditionSide> ActiveSides(IReadOnlyList<BacktestConditionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntry entry = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            yield return entry.Left
                ?? throw new ArgumentException($"ConditionEntries[{i}].Left must not be null.", nameof(entries));
            if (entry.TargetMode == RightHandTargetMode.Indicator)
            {
                yield return entry.Right
                    ?? throw new ArgumentException($"ConditionEntries[{i}].Right is required in Indicator mode.", nameof(entries));
            }
        }
    }

    /// <summary>
    /// Validates a condition tree and returns an independently owned deep copy (the tree counterpart of <see cref="Snapshot"/>; that method is unchanged).
    /// Rules, checked in canonical root order and depth-first pre-order: every leaf comparison passes the same per-entry checks as a list entry and keeps
    /// Role/Position/LogicalOperator at their defaults; a non-root group must have at least one child; group depth (root = 1) must be &lt;= <paramref name="maxDepth"/>;
    /// the node count of one root (root included) must be &lt;= <paramref name="maxNodes"/>. A null limit means "no configured bound" (like <paramref name="maxOffset"/>); a caller that receives a tree from an untrusted source (file, UI) must pass the configured limits. A violation throws - nothing is clamped. In messages the
    /// "ConditionEntries[n]" index is the leaf's pre-order ordinal across the whole tree (the same ordinal the Results tab shows).
    /// </summary>
    public static BacktestConditionTree SnapshotTree(BacktestConditionTree tree, int? maxOffset, int? maxDepth, int? maxNodes, int? maxNameLength = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (maxDepth is < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, $"maxDepth must be >= {BacktestConditionTreeRule.MinBound}.");
        }
        if (maxNodes is < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), maxNodes, $"maxNodes must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        if (maxNameLength is < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNameLength), maxNameLength, $"maxNameLength must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        int leafOrdinal = 0;
        try
        {
            return BacktestConditionTree.Create((section, side) =>
            {
                int nodeCount = 0;
                return SnapshotGroup(tree.Root(section, side), BacktestConditionTree.RootName(section, side), depth: 1, isRoot: true, maxOffset, maxDepth, maxNodes, maxNameLength, ref nodeCount, ref leafOrdinal);
            });
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(BacktestConditionTreeRule.NestingTooDeepMessage, nameof(tree));
        }
    }

    /// <summary>
    /// Every condition side a tree reads, in canonical root order and pre-order (Left then Right when the target is an indicator) - the tree
    /// counterpart of <see cref="ActiveSides(IReadOnlyList{BacktestConditionEntry})"/>. Fixes the order in which indicator requests are registered.
    /// </summary>
    public static IReadOnlyList<BacktestConditionSide> ActiveSides(BacktestConditionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var sides = new List<BacktestConditionSide>();
        try
        {
            foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
            {
                CollectSides(tree.Root(section, side), sides);
            }
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(BacktestConditionTreeRule.NestingTooDeepMessage, nameof(tree));
        }
        return sides;
    }

    private static void CollectSides(IBacktestConditionNode node, List<BacktestConditionSide> sides)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is BacktestConditionGroup group)
        {
            for (int i = 0; i < group.Children.Length; i++) CollectSides(group.Children[i], sides);
            return;
        }

        BacktestConditionEntry entry = ((BacktestConditionLeaf)node).Comparison;
        sides.Add(entry.Left ?? throw new ArgumentException("A condition leaf's Left must not be null.", nameof(node)));
        if (entry.TargetMode == RightHandTargetMode.Indicator)
        {
            sides.Add(entry.Right ?? throw new ArgumentException("A condition leaf's Right is required in Indicator mode.", nameof(node)));
        }
    }

    /// <summary><paramref name="path"/> is the node path used in every message (root: its name, child: <see cref="BacktestConditionTreeRule.ChildPath"/>).</summary>
    private static BacktestConditionGroup SnapshotGroup(
        BacktestConditionGroup group, string path, int depth, bool isRoot,
        int? maxOffset, int? maxDepth, int? maxNodes, int? maxNameLength, ref int nodeCount, ref int leafOrdinal)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (maxDepth is { } depthLimit && depth > depthLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(group), depth,
                BacktestConditionTreeRule.DescribeDepthExceeded(path, depth, depthLimit));
        }
        CountNode(path, maxNodes, ref nodeCount);
        if (!isRoot && group.Children.Length == 0)
        {
            throw new ArgumentException($"{path}: a nested condition group must contain at least one condition.", nameof(group));
        }
        if (!Enum.IsDefined(typeof(LogicalOperator), group.Operator))
        {
            throw new ArgumentException($"{path}: condition group operator is undefined.", nameof(group));
        }
        if (group.Name is not null)
        {
            if (isRoot)
            {
                throw new ArgumentException($"{path}: a condition tree root is fixed and must not have a name.", nameof(group));
            }
            if (maxNameLength is { } nameLimit && group.Name.Length > nameLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(group), group.Name.Length,
                    BacktestConditionTreeRule.DescribeNameTooLong(path, group.Name.Length, nameLimit));
            }
        }

        var children = ImmutableArray.CreateBuilder<IBacktestConditionNode>(group.Children.Length);
        for (int i = 0; i < group.Children.Length; i++)
        {
            string childPath = BacktestConditionTreeRule.ChildPath(path, i);
            switch (group.Children[i])
            {
                case BacktestConditionGroup nested:
                    children.Add(SnapshotGroup(nested, childPath, depth + 1, isRoot: false, maxOffset, maxDepth, maxNodes, maxNameLength, ref nodeCount, ref leafOrdinal));
                    break;
                case BacktestConditionLeaf leaf:
                    CountNode(childPath, maxNodes, ref nodeCount);
                    children.Add(new BacktestConditionLeaf(SnapshotLeafComparison(leaf.Comparison, childPath, leafOrdinal++, maxOffset)));
                    break;
                default:
                    throw new ArgumentException($"{childPath}: unsupported condition node type.", nameof(group));
            }
        }
        return new BacktestConditionGroup(group.Operator, children.MoveToImmutable(), group.Name);
    }

    private static void CountNode(string path, int? maxNodes, ref int nodeCount)
    {
        nodeCount++;
        if (maxNodes is { } nodeLimit && nodeCount > nodeLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), nodeCount,
                $"{path}: condition tree has more than the configured Backtest:MaxConditionTreeNodes ({nodeLimit}) nodes.");
        }
    }

    private static BacktestConditionEntry SnapshotLeafComparison(BacktestConditionEntry entry, string path, int leafOrdinal, int? maxOffset)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            ValidateEntry(entry, leafOrdinal, maxOffset);
        }
        catch (ArgumentException ex)
        {
            throw BacktestConditionTreeRule.PrefixPath(path, ex);
        }

        var defaults = new BacktestConditionEntry();
        if (entry.Role != defaults.Role || entry.Position != defaults.Position || entry.LogicalOperator != defaults.LogicalOperator)
        {
            throw new ArgumentException(
                $"{path}: ConditionEntries[{leafOrdinal}]: a condition-tree leaf must not carry Role/Position/LogicalOperator (its location and parent group define them).",
                nameof(entry));
        }
        return CloneEntry(entry);
    }

    public static void ValidateRiskManagement(BacktestRiskManagementSettings? settings)
    {
        if (settings is null) return;
        ValidateRiskRatio(settings.StopLossPercent, nameof(settings.StopLossPercent));
        ValidateRiskRatio(settings.TakeProfitPercent, nameof(settings.TakeProfitPercent));
    }

    public static string NormalizeOutputName(BacktestConditionSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        return IndicatorOutputSeriesResolver.Normalize(side.IndicatorType, side.OutputName, side.PriceSource);
    }

    private static void ValidateEntry(BacktestConditionEntry entry, int index, int? maxOffset)
    {
        if (!IsSupportedOperator(entry.Operator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Operator '{entry.Operator}' is not supported.", nameof(entry));
        }
        if (entry.TargetMode is not (RightHandTargetMode.NumericValue or RightHandTargetMode.Indicator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].TargetMode '{entry.TargetMode}' is not supported.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(LogicalOperator), entry.LogicalOperator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].LogicalOperator is undefined.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(BacktestConditionRole), entry.Role))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Role is undefined.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(TradeSide), entry.Position))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Position is undefined.", nameof(entry));
        }

        ValidateSide(entry.Left, index, "Left", active: true, maxOffset);
        if (entry.TargetMode == RightHandTargetMode.Indicator && entry.Right is null)
        {
            throw new ArgumentException($"ConditionEntries[{index}].Right is required in Indicator mode.", nameof(entry));
        }
        if (entry.Right is not null)
        {
            ValidateSide(entry.Right, index, "Right", entry.TargetMode == RightHandTargetMode.Indicator, maxOffset);
        }
    }

    private static void ValidateSide(BacktestConditionSide? side, int index, string sideName, bool active, int? maxOffset)
    {
        if (side is null)
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName} must not be null.", nameof(side));
        }
        if (!Enum.IsDefined(typeof(IndicatorType), side.IndicatorType))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.IndicatorType is undefined.", nameof(side));
        }
        if (side.Frame is { } frame && !Enum.IsDefined(typeof(TimeFrame), frame))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.Frame is undefined.", nameof(side));
        }
        if (side.PriceSource is { } priceSource && !Enum.IsDefined(typeof(PriceType), priceSource))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.PriceSource is undefined.", nameof(side));
        }
        if (!active) return;

        if (!BacktestConditionOffsetRule.IsValid(side.Offset))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side.Offset, BacktestConditionOffsetRule.Describe(index, sideName, side.Offset));
        }
        if (maxOffset is { } limit && !BacktestConditionOffsetRule.IsWithinLimit(side.Offset, limit))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side.Offset, BacktestConditionOffsetRule.DescribeAboveLimit(index, sideName, side.Offset, limit));
        }

        string outputName = NormalizeOutputName(side);
        BacktestIndicatorViolationReason violation = BacktestIndicatorEligibility.Check(side.IndicatorType, outputName);
        if (violation != BacktestIndicatorViolationReason.None)
        {
            throw new ArgumentException(
                $"ConditionEntries[{index}].{sideName}: {BacktestIndicatorEligibility.Describe(side.IndicatorType, outputName, violation)}",
                nameof(side));
        }
    }

    internal static BacktestConditionEntry CloneEntry(BacktestConditionEntry entry) => new()
    {
        Left = CloneSide(entry.Left),
        Operator = entry.Operator,
        TargetMode = entry.TargetMode,
        RightNumericValue = entry.RightNumericValue,
        Right = entry.Right is null ? null : CloneSide(entry.Right),
        LogicalOperator = entry.LogicalOperator,
        Role = entry.Role,
        Position = entry.Position,
    };

    private static BacktestConditionSide CloneSide(BacktestConditionSide side) => new()
    {
        IndicatorType = side.IndicatorType,
        Parameters = side.Parameters?.Clone(),
        OutputName = NormalizeOutputName(side),
        Offset = side.Offset,
        Frame = side.Frame,
        PriceSource = side.PriceSource,
    };

    private static bool IsSupportedOperator(ComparisonOperator value) => value is
        ComparisonOperator.GreaterThan or
        ComparisonOperator.GreaterThanOrEqual or
        ComparisonOperator.LessThan or
        ComparisonOperator.LessThanOrEqual or
        ComparisonOperator.Equal or
        ComparisonOperator.NotEqual;

    private static void ValidateRiskRatio(decimal? value, string name)
    {
        if (value is < 0m or >= 1m)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be null or satisfy 0 <= ratio < 1.");
        }
    }
}
