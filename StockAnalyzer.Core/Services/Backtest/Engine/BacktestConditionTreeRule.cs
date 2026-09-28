using System;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Single home of the condition tree's size-limit invariants and of the wording/path helpers of its validation messages (depth: the root group is 1;
/// node count: the root is included and every occurrence of a node instance counts). The upper bounds are tunables owned by configuration
/// (<c>Backtest:MaxConditionTreeDepth</c>, <c>Backtest:MaxConditionTreeNodes</c>, see <c>BacktestSettings</c>); only the lower bound is
/// an invariant: a tree needs at least its root group, so a limit below 1 could not represent any tree.
/// </summary>
public static class BacktestConditionTreeRule
{
    /// <summary>Smallest meaningful value of either limit (depth counts the root group as 1; the node count includes the root).</summary>
    public const int MinBound = 1;

    /// <summary>
    /// The fixed message of a tree that is nested too deeply to be processed. The recursive tree routines call
    /// <c>RuntimeHelpers.EnsureSufficientExecutionStack</c> (no numeric bound: the runtime knows the real stack) and the public entry points convert its
    /// <see cref="System.InsufficientExecutionStackException"/> to an <see cref="System.ArgumentException"/> with this text, so an absurdly deep tree becomes
    /// a load/validation error instead of a process crash.
    /// </summary>
    public const string NestingTooDeepMessage = "The condition tree is nested too deeply to be processed.";

    /// <summary>The path of a child node: the parent's path followed by <c>.children[index]</c> (a root's path is its name, for example <c>EntryLong</c>).</summary>
    public static string ChildPath(string parentPath, int childIndex) => $"{parentPath}.children[{childIndex}]";

    /// <summary>
    /// The same exception type as <paramref name="ex"/> with the node <paramref name="path"/> in front of its message. The framework's own
    /// " (Parameter '...')" and "Actual value was ..." suffixes are cut from the old message first, so they are not doubled by the new exception.
    /// </summary>
    public static ArgumentException PrefixPath(string path, ArgumentException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string message = $"{path}: {WithoutFrameworkSuffix(ex.Message)}";
        return ex switch
        {
            ArgumentOutOfRangeException outOfRange => new ArgumentOutOfRangeException(outOfRange.ParamName, outOfRange.ActualValue, message),
            ArgumentNullException => new ArgumentNullException(ex.ParamName, message),
            _ => new ArgumentException(message, ex.ParamName, ex),
        };
    }

    private static string WithoutFrameworkSuffix(string message)
    {
        int cut = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(Environment.NewLine + "Actual value was", StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut];
    }

    /// <summary>
    /// Measures one root: depth (the root group is 1; only groups add depth, a leaf does not) and node count (the root and every occurrence of a
    /// node instance are counted, leaves included) - the same definitions the configured limits are checked against. Read-only and limit-free, so the
    /// editor can show "depth n exceeds the limit m" without owning any counting. An absurdly deep tree becomes an <see cref="ArgumentException"/>
    /// with <see cref="NestingTooDeepMessage"/> instead of a stack overflow.
    /// </summary>
    public static BacktestConditionTreeSize Measure(BacktestConditionGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            int nodeCount = 0;
            int depth = MeasureGroup(root, 1, ref nodeCount);
            return new BacktestConditionTreeSize(depth, nodeCount);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(NestingTooDeepMessage, nameof(root));
        }
    }

    private static int MeasureGroup(BacktestConditionGroup group, int depth, ref int nodeCount)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        nodeCount++;
        int deepest = depth;
        for (int i = 0; i < group.Children.Length; i++)
        {
            if (group.Children[i] is BacktestConditionGroup nested)
            {
                deepest = Math.Max(deepest, MeasureGroup(nested, depth + 1, ref nodeCount));
            }
            else
            {
                nodeCount++;
            }
        }
        return deepest;
    }

    /// <summary>The single wording of a group-name length violation (<paramref name="path"/> is the node path).</summary>
    public static string DescribeNameTooLong(string path, int length, int limit)
        => $"{path}: condition group name length {length} exceeds the configured Backtest:MaxConditionGroupNameLength ({limit}).";

    /// <summary>The single wording of a depth-limit violation, shared by the tree validator and the persisted-tree conversion (which must stop descending early). <paramref name="path"/> is the node path.</summary>
    public static string DescribeDepthExceeded(string path, int depth, int limit)
        => $"{path}: condition group depth {depth} exceeds the configured Backtest:MaxConditionTreeDepth ({limit}).";
}

/// <summary>Result of <see cref="BacktestConditionTreeRule.Measure"/>: group depth (root = 1) and node count (root and leaves included) of one root.</summary>
public readonly record struct BacktestConditionTreeSize(int Depth, int NodeCount);
