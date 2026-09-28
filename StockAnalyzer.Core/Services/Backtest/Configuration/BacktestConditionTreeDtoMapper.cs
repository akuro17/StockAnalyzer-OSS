using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>
/// Conversion between the persisted condition-tree DTOs and the immutable <see cref="BacktestConditionTree"/>, and the single rule that decides which
/// of the two persisted condition sources a configuration file uses (<see cref="ResolveTree"/>). Shape violations throw <see cref="ArgumentException"/>
/// (the configuration manager reports them as load errors); nothing is repaired or clamped.
/// </summary>
public static class BacktestConditionTreeDtoMapper
{
    /// <summary>
    /// The runtime condition tree of a configuration. Version 1: the flat <see cref="BacktestConfigurationDto.ConditionEntries"/> are validated exactly as before
    /// and migrated (<see cref="BacktestConditionTreeMigrator"/>); the configured tree size limits are NOT applied to such a migrated tree, so a legacy file never
    /// fails to load because of a limit that did not exist when it was written. Version 2: <see cref="BacktestConfigurationDto.ConditionTree"/> is required,
    /// <see cref="BacktestConfigurationDto.ConditionEntries"/> must be empty, and the tree is validated against <paramref name="maxOffset"/>,
    /// <paramref name="maxDepth"/> and <paramref name="maxNodes"/> (null = unbounded). Any other version is rejected.
    /// </summary>
    public static BacktestConditionTree ResolveTree(BacktestConfigurationDto configuration, int maxOffset, int? maxDepth, int? maxNodes, int? maxNameLength = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ConditionEntries is null)
        {
            throw new ArgumentException("ConditionEntries must not be null.", nameof(configuration));
        }

        switch (configuration.SchemaVersion)
        {
            case BacktestConfigurationDto.CurrentSchemaVersion:
                if (configuration.ConditionTree is not null)
                {
                    throw new ArgumentException($"ConditionTree is not allowed in a SchemaVersion {BacktestConfigurationDto.CurrentSchemaVersion} file.", nameof(configuration));
                }
                // The migrator shares one side object between every root an entry is copied into; the unbounded SnapshotTree pass gives each leaf its own copy.
                return BacktestConditionValidator.SnapshotTree(
                    BacktestConditionTreeMigrator.Migrate(BacktestConditionValidator.SnapshotDtos(configuration.ConditionEntries, maxOffset)),
                    maxOffset, maxDepth: null, maxNodes: null, maxNameLength: null);

            case BacktestConfigurationDto.ConditionTreeSchemaVersion:
                if (configuration.ConditionTree is null)
                {
                    throw new ArgumentException($"ConditionTree is required in a SchemaVersion {BacktestConfigurationDto.ConditionTreeSchemaVersion} file.", nameof(configuration));
                }
                if (configuration.ConditionEntries.Count != 0)
                {
                    throw new ArgumentException($"ConditionEntries must be empty in a SchemaVersion {BacktestConfigurationDto.ConditionTreeSchemaVersion} file (ConditionTree is the only source of conditions).", nameof(configuration));
                }
                return BacktestConditionValidator.SnapshotTree(ToTree(configuration.ConditionTree, maxDepth), maxOffset, maxDepth, maxNodes, maxNameLength);

            default:
                throw new ArgumentException(
                    $"Unrecognized SchemaVersion {configuration.SchemaVersion} (expected {BacktestConfigurationDto.CurrentSchemaVersion} or {BacktestConfigurationDto.ConditionTreeSchemaVersion}).",
                    nameof(configuration));
        }
    }

    /// <summary>
    /// Converts a persisted tree to the domain tree, checking the node shape (see <see cref="BacktestConditionNodeDto"/>). <paramref name="maxDepth"/> only
    /// stops the descent early on an over-deep file (same wording as the validator); the complete bound check is <see cref="BacktestConditionValidator.SnapshotTree"/>.
    /// </summary>
    public static BacktestConditionTree ToTree(BacktestConditionTreeDto dto, int? maxDepth = null)
    {
        ArgumentNullException.ThrowIfNull(dto);

        int leafOrdinal = 0;
        try
        {
            return BacktestConditionTree.Create((section, side) =>
                ToRoot(DtoRoot(dto, section, side), BacktestConditionTree.RootName(section, side), maxDepth, ref leafOrdinal));
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(BacktestConditionTreeRule.NestingTooDeepMessage, nameof(dto));
        }
    }

    /// <summary>
    /// The DTO property of a root. The DTO property names are the persisted contract and equal <see cref="BacktestConditionTree.RootName"/>
    /// (pinned by a reflection test); the order in which roots are read and written is <see cref="BacktestConditionTree.CanonicalRoots"/>.
    /// </summary>
    private static BacktestConditionNodeDto DtoRoot(BacktestConditionTreeDto dto, BacktestConditionSection section, TradeSide side) => (section, side) switch
    {
        (BacktestConditionSection.Entry, TradeSide.Long) => dto.EntryLong,
        (BacktestConditionSection.Entry, TradeSide.Short) => dto.EntryShort,
        (BacktestConditionSection.Exit, TradeSide.Long) => dto.ExitLong,
        (BacktestConditionSection.Exit, TradeSide.Short) => dto.ExitShort,
        (BacktestConditionSection.Reverse, TradeSide.Long) => dto.ReverseLong,
        (BacktestConditionSection.Reverse, TradeSide.Short) => dto.ReverseShort,
        _ => throw new ArgumentOutOfRangeException(nameof(section), $"Undefined section/side ({section}, {side})."),
    };

    /// <summary>The persisted form of a domain tree (independently owned: parameters are cloned).</summary>
    public static BacktestConditionTreeDto ToDto(BacktestConditionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new BacktestConditionTreeDto
        {
            EntryLong = ToDtoNode(tree.EntryLong),
            EntryShort = ToDtoNode(tree.EntryShort),
            ExitLong = ToDtoNode(tree.ExitLong),
            ExitShort = ToDtoNode(tree.ExitShort),
            ReverseLong = ToDtoNode(tree.ReverseLong),
            ReverseShort = ToDtoNode(tree.ReverseShort),
        };
    }

    private static BacktestConditionGroup ToRoot(BacktestConditionNodeDto? node, string rootName, int? maxDepth, ref int leafOrdinal)
    {
        if (node is null)
        {
            throw new ArgumentException($"{rootName} must not be null.", nameof(node));
        }
        if (node.Kind != BacktestConditionNodeKind.Group)
        {
            throw new ArgumentException($"{rootName}: a condition tree root must be a Group node.", nameof(node));
        }
        return (BacktestConditionGroup)ToNode(node, rootName, depth: 1, maxDepth, ref leafOrdinal);
    }

    /// <summary><paramref name="path"/> is the node path used in every message (root: its name, child: <see cref="BacktestConditionTreeRule.ChildPath"/>).</summary>
    private static IBacktestConditionNode ToNode(BacktestConditionNodeDto? node, string path, int depth, int? maxDepth, ref int leafOrdinal)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is null)
        {
            throw new ArgumentException($"{path}: a condition node must not be null.", nameof(node));
        }

        switch (node.Kind)
        {
            case BacktestConditionNodeKind.Leaf:
                if (node.Comparison is null)
                {
                    throw new ArgumentException($"{path}: a Leaf node requires a Comparison.", nameof(node));
                }
                if (node.Children is { Count: > 0 })
                {
                    throw new ArgumentException($"{path}: a Leaf node must not have Children.", nameof(node));
                }
                if (node.Name is not null)
                {
                    throw new ArgumentException($"{path}: a Leaf node must not have a Name.", nameof(node));
                }
                try
                {
                    return new BacktestConditionLeaf(BacktestConditionValidator.ToDomainEntry(node.Comparison, leafOrdinal++, nameof(node)));
                }
                catch (ArgumentException ex)
                {
                    throw BacktestConditionTreeRule.PrefixPath(path, ex);
                }

            case BacktestConditionNodeKind.Group:
                if (maxDepth is { } limit && depth > limit)
                {
                    throw new ArgumentOutOfRangeException(nameof(node), depth, BacktestConditionTreeRule.DescribeDepthExceeded(path, depth, limit));
                }
                if (node.Comparison is not null)
                {
                    throw new ArgumentException($"{path}: a Group node must not carry a Comparison.", nameof(node));
                }
                if (node.Children is null)
                {
                    throw new ArgumentException($"{path}: a Group node requires a Children list.", nameof(node));
                }

                var children = ImmutableArray.CreateBuilder<IBacktestConditionNode>(node.Children.Count);
                for (int i = 0; i < node.Children.Count; i++)
                {
                    children.Add(ToNode(node.Children[i], BacktestConditionTreeRule.ChildPath(path, i), depth + 1, maxDepth, ref leafOrdinal));
                }
                return new BacktestConditionGroup(node.Operator, children.MoveToImmutable(), node.Name);

            default:
                throw new ArgumentException($"{path}: condition node Kind {node.Kind} is undefined.", nameof(node));
        }
    }

    /// <summary>
    /// The domain comparison of one persisted comparison, without any validation (the caller validates when it needs to): an independently owned entry
    /// whose OutputName is normalized to the canonical series name, exactly like the copies <see cref="BacktestConditionValidator.Snapshot"/> makes.
    /// This and <see cref="ToEntryDto"/> are the single conversion between the persisted and the domain comparison (the UI holds no copy of it).
    /// </summary>
    public static BacktestConditionEntry ToEntry(BacktestConditionEntryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return BacktestConditionValidator.CloneEntry(BacktestConditionValidator.ToDomainEntry(dto, index: 0, nameof(dto)));
    }

    /// <summary>The persisted form of one domain comparison (independently owned: parameters are cloned; OutputName is normalized to the canonical series name).</summary>
    public static BacktestConditionEntryDto ToEntryDto(BacktestConditionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new BacktestConditionEntryDto
        {
            Left = ToDtoSide(entry.Left),
            Operator = entry.Operator,
            TargetMode = entry.TargetMode,
            RightNumericValue = entry.RightNumericValue,
            Right = entry.Right is null ? null : ToDtoSide(entry.Right),
            LogicalOperator = entry.LogicalOperator,
            Role = entry.Role,
            Position = entry.Position,
        };
    }

    private static BacktestConditionNodeDto ToDtoNode(IBacktestConditionNode node)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is BacktestConditionGroup group)
        {
            var children = new System.Collections.Generic.List<BacktestConditionNodeDto>(group.Children.Length);
            for (int i = 0; i < group.Children.Length; i++) children.Add(ToDtoNode(group.Children[i]));
            return new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Group, Operator = group.Operator, Children = children, Name = group.Name };
        }

        return new BacktestConditionNodeDto
        {
            Kind = BacktestConditionNodeKind.Leaf,
            Comparison = ToEntryDto(((BacktestConditionLeaf)node).Comparison),
        };
    }

    private static BacktestConditionSideDto ToDtoSide(BacktestConditionSide side) => new()
    {
        IndicatorType = side.IndicatorType,
        Parameters = side.Parameters?.Clone(),
        OutputName = BacktestConditionValidator.NormalizeOutputName(side),
        Offset = side.Offset,
        Frame = side.Frame,
        PriceSource = side.PriceSource,
    };
}
