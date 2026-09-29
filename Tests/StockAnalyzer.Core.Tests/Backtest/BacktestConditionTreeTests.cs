using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Condition-tree model, evaluator and legacy migration (MP-1 Phase 1). See Y:\Temp\sa_analysis_BacktestConditionTree.md B1-B3.</summary>
public class BacktestConditionTreeTests
{
    [Theory]
    [InlineData(LogicalOperator.And)]
    [InlineData(LogicalOperator.Or)]
    public void Group_TruthTable_OneToThreeLeaves(LogicalOperator op)
    {
        for (int leafCount = 1; leafCount <= 3; leafCount++)
        {
            BacktestConditionSide[] sides = Enumerable.Range(0, leafCount).Select(_ => Side()).ToArray();
            var (series, map) = TruthTable(sides);
            var group = new BacktestConditionGroup(op, sides.Select(s => (IBacktestConditionNode)Leaf(s)).ToImmutableArray());

            for (int bar = 0; bar < (1 << leafCount); bar++)
            {
                bool[] bits = Enumerable.Range(0, leafCount).Select(k => ((bar >> k) & 1) == 1).ToArray();
                bool expected = op == LogicalOperator.And ? bits.All(b => b) : bits.Any(b => b);
                Assert.Equal(expected, BacktestConditionTreeEvaluator.Evaluate(group, map, series, bar));
            }
        }
    }

    [Fact]
    public void NestedTree_AndOrCombination_MatchesBooleanExpressionOverAllInputs()
    {
        // (A AND B) OR (C AND (D OR E))
        BacktestConditionSide[] s = Enumerable.Range(0, 5).Select(_ => Side()).ToArray();
        var (series, map) = TruthTable(s);
        BacktestConditionGroup tree = Or(And(Leaf(s[0]), Leaf(s[1])), And(Leaf(s[2]), Or(Leaf(s[3]), Leaf(s[4]))));

        for (int bar = 0; bar < 32; bar++)
        {
            bool a = (bar & 1) != 0, b = (bar & 2) != 0, c = (bar & 4) != 0, d = (bar & 8) != 0, e = (bar & 16) != 0;
            bool expected = (a && b) || (c && (d || e));
            Assert.Equal(expected, BacktestConditionTreeEvaluator.Evaluate(tree, map, series, bar));
        }
    }

    [Fact]
    public void SingleChildGroup_EvaluatesToItsChild()
    {
        var side = Side();
        var (series, map) = TruthTable(side);
        Assert.False(BacktestConditionTreeEvaluator.Evaluate(And(Leaf(side)), map, series, 0));
        Assert.True(BacktestConditionTreeEvaluator.Evaluate(Or(Leaf(side)), map, series, 1));
    }

    [Fact]
    public void EmptyGroup_IsFalse_AndHasNoLeaf()
    {
        var (series, map) = TruthTable(Side());
        Assert.False(BacktestConditionTreeEvaluator.Evaluate(BacktestConditionGroup.EmptyAnd, map, series, 0));
        Assert.False(BacktestConditionTreeEvaluator.Evaluate(Or(), map, series, 1));
        Assert.False(BacktestConditionGroup.EmptyAnd.HasLeaf);
        Assert.False(And(And(), Or()).HasLeaf);
        Assert.True(And(And(), Or(Leaf(Side()))).HasLeaf);
    }

    [Fact]
    public void UnavailableValue_IsFalse_InAndAndInOr()
    {
        BacktestConditionSide a = Side(), b = Side();
        var series = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>>
        {
            ["a"] = new decimal?[] { null }.ToImmutableArray(),
            ["b"] = new decimal?[] { 1m }.ToImmutableArray(),
        });
        var map = new Dictionary<BacktestConditionSide, string> { [a] = "a", [b] = "b" };

        Assert.False(BacktestConditionTreeEvaluator.Evaluate(And(Leaf(a), Leaf(b)), map, series, 0));
        Assert.True(BacktestConditionTreeEvaluator.Evaluate(Or(Leaf(a), Leaf(b)), map, series, 0));
        Assert.False(BacktestConditionTreeEvaluator.Evaluate(Or(Leaf(a)), map, series, 0));
    }

    [Fact]
    public void Evaluation_DoesNotShortCircuit_MisconfiguredLaterLeafStillThrows()
    {
        BacktestConditionSide trueSide = Side(), unmapped = Side();
        var (series, map) = TruthTable(trueSide);

        // First child is true (Or is already decided) and the second is not mapped: legacy list evaluation would throw, so must the tree.
        Assert.Throws<KeyNotFoundException>(() =>
            BacktestConditionTreeEvaluator.Evaluate(Or(Leaf(trueSide), Leaf(unmapped)), map, series, 1));
        // First child false (And already decided) - same.
        Assert.Throws<KeyNotFoundException>(() =>
            BacktestConditionTreeEvaluator.Evaluate(And(Leaf(trueSide), Leaf(unmapped)), map, series, 0));
    }

    [Fact]
    public void GroupConstruction_RejectsUndefinedOperatorDefaultArrayAndNullChild()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConditionGroup((LogicalOperator)99, ImmutableArray<IBacktestConditionNode>.Empty));
        Assert.Throws<ArgumentException>(() => new BacktestConditionGroup(LogicalOperator.And, default));
        Assert.Throws<ArgumentException>(() => new BacktestConditionGroup(LogicalOperator.And, new IBacktestConditionNode[] { null! }.ToImmutableArray()));
        Assert.Throws<ArgumentNullException>(() => new BacktestConditionLeaf(null!));
    }

    [Fact]
    public void Tree_RootAccessor_ReturnsTheMatchingRoot()
    {
        BacktestConditionGroup[] roots = Enumerable.Range(0, 6).Select(_ => And(Leaf(Side()))).ToArray();
        var tree = new BacktestConditionTree(roots[0], roots[1], roots[2], roots[3], roots[4], roots[5]);

        Assert.Same(roots[0], tree.Root(BacktestConditionSection.Entry, TradeSide.Long));
        Assert.Same(roots[1], tree.Root(BacktestConditionSection.Entry, TradeSide.Short));
        Assert.Same(roots[2], tree.Root(BacktestConditionSection.Exit, TradeSide.Long));
        Assert.Same(roots[3], tree.Root(BacktestConditionSection.Exit, TradeSide.Short));
        Assert.Same(roots[4], tree.Root(BacktestConditionSection.Reverse, TradeSide.Long));
        Assert.Same(roots[5], tree.Root(BacktestConditionSection.Reverse, TradeSide.Short));
        Assert.False(BacktestConditionTree.Empty.EntryLong.HasLeaf);
    }

    // ---- Legacy migration -------------------------------------------------------------------------------------------------

    [Fact]
    public void Migration_LeftFold_PreservesLegacyNoPrecedenceSemantics()
    {
        // Legacy: A OR B AND C == (A OR B) AND C (no precedence). Standard precedence would give A OR (B AND C).
        BacktestConditionSide[] s = Enumerable.Range(0, 3).Select(_ => Side()).ToArray();
        var (series, map) = TruthTable(s);
        BacktestConditionEntry[] entries =
        {
            new() { Left = s[0], LogicalOperator = LogicalOperator.Or, Role = BacktestConditionRole.EntryOnly },
            new() { Left = s[1], LogicalOperator = LogicalOperator.And, Role = BacktestConditionRole.EntryOnly },
            new() { Left = s[2], Role = BacktestConditionRole.EntryOnly },
        };

        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(entries);

        for (int bar = 0; bar < 8; bar++)
        {
            bool a = (bar & 1) != 0, b = (bar & 2) != 0, c = (bar & 4) != 0;
            Assert.Equal((a || b) && c, BacktestConditionTreeEvaluator.Evaluate(tree.EntryLong, map, series, bar));
        }
    }

    [Fact]
    public void Migration_EquivalentToLegacyEvaluation_ForEveryRoleSideAndConnectorCombination()
    {
        BacktestConditionRole[] roles = Enum.GetValues<BacktestConditionRole>();
        TradeSide[] positions = Enum.GetValues<TradeSide>();
        LogicalOperator[] connectors = Enum.GetValues<LogicalOperator>();
        var shapes = (from role in roles from position in positions from connector in connectors select (role, position, connector)).ToArray();

        int checkedLists = 0;
        for (int length = 1; length <= 4; length++)
        {
            BacktestConditionSide[] sides = Enumerable.Range(0, length).Select(_ => Side()).ToArray();
            var (series, map) = TruthTable(sides);

            // The last entry's connector is never read, so it is fixed to And to avoid redundant duplicates.
            foreach (int[] pick in Enumerate(shapes.Length, length))
            {
                var entries = new BacktestConditionEntry[length];
                for (int i = 0; i < length; i++)
                {
                    var (role, position, connector) = shapes[pick[i]];
                    if (i == length - 1 && connector != LogicalOperator.And) goto nextList;
                    entries[i] = new BacktestConditionEntry
                    {
                        Left = sides[i],
                        Role = role,
                        Position = position,
                        LogicalOperator = connector,
                    };
                }

                AssertMigrationMatchesLegacy(entries, map, series, 1 << length);
                checkedLists++;
            nextList:;
            }
        }

        Assert.True(checkedLists > 30000, $"Enumeration unexpectedly small: {checkedLists}");
    }

    private static IEnumerable<int[]> Enumerate(int radix, int length)
    {
        var digits = new int[length];
        while (true)
        {
            yield return (int[])digits.Clone();
            int position = length - 1;
            while (position >= 0 && ++digits[position] == radix)
            {
                digits[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }

    private static void AssertMigrationMatchesLegacy(
        BacktestConditionEntry[] entries,
        Dictionary<BacktestConditionSide, string> map,
        IndicatorSeriesSet series,
        int bars)
    {
        BacktestConditionExecutionPaths paths = BacktestConditionExecutionPaths.Create(entries);
        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(entries);

        for (int bar = 0; bar < bars; bar++)
        {
            Assert.Equal(BacktestConditionEvaluator.Evaluate(paths.LongEntry, map, series, bar), BacktestConditionTreeEvaluator.Evaluate(tree.EntryLong, map, series, bar));
            Assert.Equal(BacktestConditionEvaluator.Evaluate(paths.ShortEntry, map, series, bar), BacktestConditionTreeEvaluator.Evaluate(tree.EntryShort, map, series, bar));
            bool legacyExit = BacktestConditionEvaluator.Evaluate(paths.Exit, map, series, bar);
            Assert.Equal(legacyExit, BacktestConditionTreeEvaluator.Evaluate(tree.ExitLong, map, series, bar));
            Assert.Equal(legacyExit, BacktestConditionTreeEvaluator.Evaluate(tree.ExitShort, map, series, bar));
            Assert.Equal(BacktestConditionEvaluator.Evaluate(paths.ReverseLong, map, series, bar), BacktestConditionTreeEvaluator.Evaluate(tree.ReverseLong, map, series, bar));
            Assert.Equal(BacktestConditionEvaluator.Evaluate(paths.ReverseShort, map, series, bar), BacktestConditionTreeEvaluator.Evaluate(tree.ReverseShort, map, series, bar));
        }

        // A path is active in the tree exactly when the legacy path is non-empty (legacy "Count > 0" guard).
        Assert.Equal(paths.LongEntry.Length > 0, tree.EntryLong.HasLeaf);
        Assert.Equal(paths.ShortEntry.Length > 0, tree.EntryShort.HasLeaf);
        Assert.Equal(paths.Exit.Length > 0, tree.ExitLong.HasLeaf);
        Assert.Equal(paths.Exit.Length > 0, tree.ExitShort.HasLeaf);
        Assert.Equal(paths.ReverseLong.Length > 0, tree.ReverseLong.HasLeaf);
        Assert.Equal(paths.ReverseShort.Length > 0, tree.ReverseShort.HasLeaf);
    }

    [Fact]
    public void Migration_CopiesEntriesIntoEveryPathTheyBelongTo_AndDoesNotMutateInput()
    {
        BacktestConditionSide both = Side(), reversalLong = Side(), entryShort = Side(), exitOnly = Side();
        BacktestConditionEntry[] entries =
        {
            new() { Left = both, Role = BacktestConditionRole.Both, Position = TradeSide.Long, LogicalOperator = LogicalOperator.Or },
            new() { Left = reversalLong, Role = BacktestConditionRole.Reversal, Position = TradeSide.Long },
            new() { Left = entryShort, Role = BacktestConditionRole.EntryOnly, Position = TradeSide.Short },
            new() { Left = exitOnly, Role = BacktestConditionRole.ExitOnly, Position = TradeSide.Short },
        };

        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(entries);

        Assert.Equal(new[] { both, reversalLong }, LeafSides(tree.EntryLong));
        Assert.Equal(new[] { entryShort }, LeafSides(tree.EntryShort));
        Assert.Equal(new[] { both, exitOnly }, LeafSides(tree.ExitLong));
        Assert.Equal(new[] { both, exitOnly }, LeafSides(tree.ExitShort));
        Assert.Equal(new[] { reversalLong }, LeafSides(tree.ReverseLong));
        Assert.Empty(LeafSides(tree.ReverseShort));

        // Leaf comparisons are normalized; the input keeps its own Role/Position/LogicalOperator.
        var expectedDefaults = new BacktestConditionEntry();
        foreach (BacktestConditionLeaf leaf in Leaves(tree.EntryLong))
        {
            Assert.Equal(expectedDefaults.Role, leaf.Comparison.Role);
            Assert.Equal(expectedDefaults.Position, leaf.Comparison.Position);
            Assert.Equal(expectedDefaults.LogicalOperator, leaf.Comparison.LogicalOperator);
        }
        Assert.Equal(BacktestConditionRole.Both, entries[0].Role);
        Assert.Equal(LogicalOperator.Or, entries[0].LogicalOperator);
        Assert.Equal(TradeSide.Short, entries[3].Position);
    }

    [Fact]
    public void Migration_EmptyList_ProducesInactiveTree_AndNullThrows()
    {
        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(Array.Empty<BacktestConditionEntry>());

        foreach (BacktestConditionSection section in Enum.GetValues<BacktestConditionSection>())
        {
            foreach (TradeSide side in Enum.GetValues<TradeSide>())
            {
                Assert.False(tree.Root(section, side).HasLeaf);
            }
        }
        Assert.Throws<ArgumentNullException>(() => BacktestConditionTreeMigrator.Migrate(null!));
    }

    private static IEnumerable<BacktestConditionLeaf> Leaves(IBacktestConditionNode node)
    {
        if (node is BacktestConditionLeaf leaf)
        {
            yield return leaf;
            yield break;
        }
        foreach (IBacktestConditionNode child in ((BacktestConditionGroup)node).Children)
        {
            foreach (BacktestConditionLeaf nested in Leaves(child)) yield return nested;
        }
    }

    private static BacktestConditionSide[] LeafSides(IBacktestConditionNode node) => Leaves(node).Select(l => l.Comparison.Left).ToArray();

    // ---- Allocation -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluation_OfDepthFourTree_AllocatesNothing()
    {
        BacktestConditionSide[] s = Enumerable.Range(0, 5).Select(_ => Side()).ToArray();
        var (series, map) = TruthTable(s);
        BacktestConditionGroup tree = Or(And(Leaf(s[0]), Or(Leaf(s[1]), And(Leaf(s[2]), Or(Leaf(s[3]), Leaf(s[4]))))));

        for (int warm = 0; warm < 100; warm++) BacktestConditionTreeEvaluator.Evaluate(tree, map, series, warm % 32);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool sink = false;
        for (int i = 0; i < 10_000; i++) sink ^= BacktestConditionTreeEvaluator.Evaluate(tree, map, series, i % 32);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"Evaluation allocated {allocated} bytes (sink={sink}).");
    }
}
