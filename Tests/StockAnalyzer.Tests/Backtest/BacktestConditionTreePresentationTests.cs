using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Results presentation of a condition-tree run (MP-2 Phase 4, spec B10 / addendum A7).</summary>
public class BacktestConditionTreePresentationTests
{
    /// <summary>Formats as "SMA &gt; {value}" (the leaf format itself is covered by the existing formatter tests).</summary>
    private static BacktestConditionLeaf Leaf(int value) => new(new BacktestConditionEntry
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    });

    private static BacktestConditionGroup Group(LogicalOperator op, params IBacktestConditionNode[] children)
        => new(op, children.ToImmutableArray());

    private static BacktestConditionTree Tree(
        BacktestConditionGroup? entryLong = null, BacktestConditionGroup? entryShort = null,
        BacktestConditionGroup? exitLong = null, BacktestConditionGroup? exitShort = null,
        BacktestConditionGroup? reverseLong = null, BacktestConditionGroup? reverseShort = null) => new(
        entryLong ?? BacktestConditionGroup.EmptyAnd, entryShort ?? BacktestConditionGroup.EmptyAnd,
        exitLong ?? BacktestConditionGroup.EmptyAnd, exitShort ?? BacktestConditionGroup.EmptyAnd,
        reverseLong ?? BacktestConditionGroup.EmptyAnd, reverseShort ?? BacktestConditionGroup.EmptyAnd);

    // ---- FormatTreeExpression ----

    [Fact]
    public void Expression_NestedAndOr_GoldenString()
    {
        // (A AND B) OR (C AND (D OR E))
        var root = Group(LogicalOperator.Or,
            Group(LogicalOperator.And, Leaf(1), Leaf(2)),
            Group(LogicalOperator.And, Leaf(3), Group(LogicalOperator.Or, Leaf(4), Leaf(5))));

        string expression = BacktestConditionFormatter.FormatTreeExpression(Tree(entryLong: root));

        Assert.Equal("EntryLong=(((SMA > 1) AND (SMA > 2)) OR ((SMA > 3) AND ((SMA > 4) OR (SMA > 5))))", expression);
    }

    [Fact]
    public void Expression_SingleLeaf()
    {
        Assert.Equal("ExitShort=((SMA > 7))", BacktestConditionFormatter.FormatTreeExpression(Tree(exitShort: Group(LogicalOperator.And, Leaf(7)))));
    }

    [Fact]
    public void Expression_EmptyRootsAreOmitted_AndAnEmptyTreeIsEmpty()
    {
        Assert.Equal(string.Empty, BacktestConditionFormatter.FormatTreeExpression(BacktestConditionTree.Empty));
        Assert.Equal("ReverseLong=((SMA > 1))", BacktestConditionFormatter.FormatTreeExpression(Tree(reverseLong: Group(LogicalOperator.And, Leaf(1)), entryShort: BacktestConditionGroup.EmptyAnd)));
    }

    [Fact]
    public void Expression_AllSixNames_InCanonicalOrder_JoinedBySemicolon()
    {
        BacktestConditionGroup G(int v) => Group(LogicalOperator.Or, Leaf(v));
        string expression = BacktestConditionFormatter.FormatTreeExpression(Tree(G(6), G(5), G(4), G(3), G(2), G(1)));

        Assert.Equal(
            "EntryLong=((SMA > 6)); EntryShort=((SMA > 5)); ExitLong=((SMA > 4)); ExitShort=((SMA > 3)); ReverseLong=((SMA > 2)); ReverseShort=((SMA > 1))",
            expression);
    }

    [Fact]
    public void TreeAudit_NamesTheLocationInsteadOfRoleAndConnector()
    {
        string audit = BacktestConditionFormatter.FormatAudit(Leaf(1).Comparison, 4, BacktestConditionSection.Exit, TradeSide.Short, "EXPR");

        Assert.StartsWith("Index=4; Section=Exit; Side=Short; Left=[", audit);
        Assert.EndsWith("ExecutionPaths=EXPR", audit);
        Assert.DoesNotContain("Role=", audit);
        Assert.DoesNotContain("ConnectorToNext", audit);
    }

    [Fact]
    public void LegacyExpression_IsUnchanged()
    {
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            RightNumericValue = 1m,
            Role = BacktestConditionRole.EntryOnly,
            Position = TradeSide.Long,
        };

        Assert.Equal("LongEntry=(SMA > 1)", BacktestConditionFormatter.FormatExecutionExpression(new[] { entry }));
    }

    // ---- results view-model ----

    private static BacktestResultsViewModel CreateResults() => new(
        NullLocalizationService.Instance, new FakeBacktestReportExporter(), new FakeDialogService());

    [Fact]
    public void TreeRun_ListsLeavesInPreOrder_BucketedBySection_WithReverseBucket()
    {
        BacktestConditionTree tree = Tree(
            entryLong: Group(LogicalOperator.And, Leaf(1), Group(LogicalOperator.Or, Leaf(2), Leaf(3))),
            exitShort: Group(LogicalOperator.And, Leaf(4)),
            reverseShort: Group(LogicalOperator.And, Leaf(5)));
        BacktestResultsViewModel vm = CreateResults();

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0,
            ImmutableArray<BacktestConditionEntry>.Empty, isNoOp: false, riskManagement: null, tree);

        Assert.Equal(new[] { (0, "SMA > 1"), (1, "SMA > 2"), (2, "SMA > 3") }, vm.EntryConditionEntries.Select(r => (r.SavedIndex, r.Text)));
        Assert.Equal(new[] { (3, "SMA > 4") }, vm.ExitConditionEntries.Select(r => (r.SavedIndex, r.Text)));
        Assert.Equal(new[] { (4, "SMA > 5") }, vm.ReverseConditionEntries.Select(r => (r.SavedIndex, r.Text)));
        Assert.Equal(BacktestConditionFormatter.FormatTreeExpression(tree), vm.Presentation!.ConditionExpression);
        Assert.Contains("Section=Reverse; Side=Short", vm.ReverseConditionEntries[0].Tooltip);
        Assert.Contains(vm.Presentation.ConditionExpression, vm.EntryConditionEntries[2].Tooltip);
    }

    [Fact]
    public void ListRun_WithoutATree_KeepsTheLegacyPresentation()
    {
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            RightNumericValue = 9m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Short,
        };
        BacktestResultsViewModel vm = CreateResults();

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0, ImmutableArray.Create(entry), isNoOp: false);

        Assert.Empty(vm.EntryConditionEntries);
        BacktestConditionPresentationRow row = Assert.Single(vm.ReverseConditionEntries);
        Assert.Equal("SMA > 9", row.Text);
        Assert.Contains("Role=Reversal", row.Tooltip);
        Assert.Equal(BacktestConditionFormatter.FormatExecutionExpression(new[] { entry }), vm.Presentation!.ConditionExpression);
    }

    [Fact]
    public void TryUpdate_WithATree_PublishesIt_AndAnInvalidReportKeepsThePreviousPresentation()
    {
        BacktestResultsViewModel vm = CreateResults();
        BacktestConditionTree tree = Tree(entryLong: Group(LogicalOperator.And, Leaf(1)));

        bool ok = vm.TryUpdate(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0,
            ImmutableArray<BacktestConditionEntry>.Empty, false, null, tree, out var failure);
        Assert.True(ok);
        Assert.Null(failure);
        BacktestResultPresentation published = vm.Presentation!;

        bool bad = vm.TryUpdate(BacktestTestFactory.CreateResult(), new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReport(), 0,
            ImmutableArray<BacktestConditionEntry>.Empty, false, null, tree, out failure);

        Assert.False(bad);
        Assert.Same(published, vm.Presentation);
    }
}
