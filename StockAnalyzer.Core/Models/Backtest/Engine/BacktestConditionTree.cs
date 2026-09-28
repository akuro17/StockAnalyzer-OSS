using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// The complete condition set of a backtest: one root <see cref="BacktestConditionGroup"/> per (section, side).
/// Canonical order, used wherever the roots are enumerated (indicator registration, fingerprint, result rows):
/// EntryLong, EntryShort, ExitLong, ExitShort, ReverseLong, ReverseShort. <see cref="CanonicalRoots"/> is the single definition of that order; it is a
/// contract (it fixes the fingerprint byte layout and the indicator registration order), independent of the numeric values of the enums.
/// ReverseLong = while holding Short, flip to Long; ReverseShort = while holding Long, flip to Short; a native Reverse root never acts while flat.
/// </summary>
public sealed class BacktestConditionTree
{
    public BacktestConditionTree(
        BacktestConditionGroup entryLong,
        BacktestConditionGroup entryShort,
        BacktestConditionGroup exitLong,
        BacktestConditionGroup exitShort,
        BacktestConditionGroup reverseLong,
        BacktestConditionGroup reverseShort)
    {
        EntryLong = entryLong ?? throw new ArgumentNullException(nameof(entryLong));
        EntryShort = entryShort ?? throw new ArgumentNullException(nameof(entryShort));
        ExitLong = exitLong ?? throw new ArgumentNullException(nameof(exitLong));
        ExitShort = exitShort ?? throw new ArgumentNullException(nameof(exitShort));
        ReverseLong = reverseLong ?? throw new ArgumentNullException(nameof(reverseLong));
        ReverseShort = reverseShort ?? throw new ArgumentNullException(nameof(reverseShort));
    }

    /// <summary>The six (section, side) pairs in canonical order. Every enumeration of the roots goes through this list.</summary>
    public static ImmutableArray<(BacktestConditionSection Section, TradeSide Side)> CanonicalRoots { get; } = ImmutableArray.Create(
        (BacktestConditionSection.Entry, TradeSide.Long),
        (BacktestConditionSection.Entry, TradeSide.Short),
        (BacktestConditionSection.Exit, TradeSide.Long),
        (BacktestConditionSection.Exit, TradeSide.Short),
        (BacktestConditionSection.Reverse, TradeSide.Long),
        (BacktestConditionSection.Reverse, TradeSide.Short));

    /// <summary>The root's name: section followed by side ("EntryLong"). It is also the property name of the persisted tree DTO and the name used in messages.</summary>
    public static string RootName(BacktestConditionSection section, TradeSide side) => $"{section}{side}";

    /// <summary>
    /// Builds a tree by asking <paramref name="rootFactory"/> for each root exactly once, in canonical order (so a caller that numbers leaves
    /// while it builds gets the canonical pre-order numbering).
    /// </summary>
    public static BacktestConditionTree Create(Func<BacktestConditionSection, TradeSide, BacktestConditionGroup> rootFactory)
    {
        ArgumentNullException.ThrowIfNull(rootFactory);
        var roots = new BacktestConditionGroup[CanonicalRoots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = rootFactory(CanonicalRoots[i].Section, CanonicalRoots[i].Side);
        }
        return new BacktestConditionTree(roots[0], roots[1], roots[2], roots[3], roots[4], roots[5]);
    }

    public BacktestConditionGroup EntryLong { get; }
    public BacktestConditionGroup EntryShort { get; }
    public BacktestConditionGroup ExitLong { get; }
    public BacktestConditionGroup ExitShort { get; }
    public BacktestConditionGroup ReverseLong { get; }
    public BacktestConditionGroup ReverseShort { get; }

    /// <summary>A tree whose six roots are all empty (no active path).</summary>
    public static BacktestConditionTree Empty { get; } = new(
        BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd,
        BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);

    /// <summary>True iff at least one of the six roots contains a leaf. A tree without any leaf has no active path and behaves like an empty condition list.</summary>
    public bool HasAnyLeaf => EntryLong.HasLeaf || EntryShort.HasLeaf || ExitLong.HasLeaf || ExitShort.HasLeaf || ReverseLong.HasLeaf || ReverseShort.HasLeaf;

    public BacktestConditionGroup Root(BacktestConditionSection section, TradeSide side) => (section, side) switch
    {
        (BacktestConditionSection.Entry, TradeSide.Long) => EntryLong,
        (BacktestConditionSection.Entry, TradeSide.Short) => EntryShort,
        (BacktestConditionSection.Exit, TradeSide.Long) => ExitLong,
        (BacktestConditionSection.Exit, TradeSide.Short) => ExitShort,
        (BacktestConditionSection.Reverse, TradeSide.Long) => ReverseLong,
        (BacktestConditionSection.Reverse, TradeSide.Short) => ReverseShort,
        _ => throw new ArgumentOutOfRangeException(nameof(section), $"Undefined section/side ({section}, {side}).")
    };
}
