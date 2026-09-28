#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Condition-tree strategy integration + fingerprint (MP-1 Phase 3). See Y:\Temp\sa_analysis_BacktestConditionTree.md B4, B5, B7.</summary>
public class BacktestConditionTreeStrategyTests
{
    /// <summary>Fingerprint of <see cref="ListBuiltStrategy_FingerprintIsByteIdenticalToTheGoldenCapturedBeforeThisChange"/>'s run, captured from the list-built strategy BEFORE any Phase 3 production edit.</summary>
    private const string LegacyGoldenFingerprint = "8D9D2AFC6C5F5FDDB29049C517E3CF7DB7BA3B3553016A000358DCC235AC3198";

    private static BacktestConditionEntry[] LegacyEntries() => new[]
    {
        CloseVsSma(ComparisonOperator.GreaterThan, 3, BacktestConditionRole.EntryOnly, TradeSide.Long),
        CloseVsSma(ComparisonOperator.LessThan, 3, BacktestConditionRole.ExitOnly, TradeSide.Long),
    };

    [Fact]
    public void ListBuiltStrategy_FingerprintIsByteIdenticalToTheGoldenCapturedBeforeThisChange()
    {
        var strategy = new ConditionBasedBacktestStrategy(LegacyEntries(), new BacktestRiskManagementSettings { StopLossPercent = 0.05m });

        Assert.Equal(LegacyGoldenFingerprint, FingerprintHex(strategy, WavyBars()));
        Assert.Null(strategy.Tree);
    }

    public static IEnumerable<object?[]> LegacyScenarios()
    {
        yield return new object?[] { "entry+exit", LegacyEntries(), null };
        yield return new object?[] { "risk management", LegacyEntries(), new BacktestRiskManagementSettings { StopLossPercent = 0.02m, TakeProfitPercent = 0.03m } };
        yield return new object?[]
        {
            "both role",
            new[] { CloseVsSma(ComparisonOperator.GreaterThan, 3, BacktestConditionRole.Both, TradeSide.Long) },
            null,
        };
        yield return new object?[]
        {
            "reversal long/short",
            new[]
            {
                CloseVsSma(ComparisonOperator.GreaterThan, 3, BacktestConditionRole.Reversal, TradeSide.Long),
                CloseVsSma(ComparisonOperator.LessThan, 3, BacktestConditionRole.Reversal, TradeSide.Short),
            },
            null,
        };
        yield return new object?[]
        {
            "or-connected entries, short entry, mixed exit",
            new[]
            {
                CloseVsSma(ComparisonOperator.GreaterThan, 5, BacktestConditionRole.EntryOnly, TradeSide.Long, LogicalOperator.Or),
                CloseVsSma(ComparisonOperator.GreaterThan, 3, BacktestConditionRole.EntryOnly, TradeSide.Long),
                CloseVsSma(ComparisonOperator.LessThan, 5, BacktestConditionRole.EntryOnly, TradeSide.Short),
                CloseVsSma(ComparisonOperator.LessThan, 3, BacktestConditionRole.ExitOnly, TradeSide.Long, LogicalOperator.Or),
                CloseVsSma(ComparisonOperator.GreaterThan, 5, BacktestConditionRole.ExitOnly, TradeSide.Long),
            },
            new BacktestRiskManagementSettings { StopLossPercent = 0.04m },
        };
    }

    [Theory]
    [MemberData(nameof(LegacyScenarios))]
    public void MigratedTreeStrategy_ReproducesTheListStrategyRunExactly(string scenario, BacktestConditionEntry[] entries, BacktestRiskManagementSettings? risk)
    {
        var listStrategy = new ConditionBasedBacktestStrategy(entries, risk);
        ConditionBasedBacktestStrategy treeStrategy = ConditionBasedBacktestStrategy.FromTree(BacktestConditionTreeMigrator.Migrate(entries), risk);

        BacktestResult expected = Run(listStrategy);
        BacktestResult actual = Run(treeStrategy);

        Assert.True(expected.Signals.Length > 0, $"scenario {scenario} must produce signals to be meaningful");
        Assert.Equal(expected.StrategyName, actual.StrategyName);
        Assert.Equal(expected.ReproducibilityHash, actual.ReproducibilityHash);
        Assert.True(expected.Orders.SequenceEqual(actual.Orders), $"{scenario}: orders");
        Assert.True(expected.Fills.SequenceEqual(actual.Fills), $"{scenario}: fills");
        Assert.True(expected.Trades.SequenceEqual(actual.Trades), $"{scenario}: trades");
        Assert.True(expected.Signals.SequenceEqual(actual.Signals), $"{scenario}: signals (including reasons)");
        Assert.True(expected.EquityPoints.SequenceEqual(actual.EquityPoints), $"{scenario}: equity");
        Assert.Equal(
            listStrategy.GetRequiredIndicators().Select(r => $"{r.Type}/{r.OutputName}/{r.PriceSource}").Distinct().OrderBy(t => t),
            treeStrategy.GetRequiredIndicators().Select(r => $"{r.Type}/{r.OutputName}/{r.PriceSource}").Distinct().OrderBy(t => t));
    }

    [Fact]
    public void TreeBuiltStrategy_ExposesTreeAndNoLegacyEntries_AndFactoryValidates()
    {
        BacktestConditionTree tree = Tree(entryLong: Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 0m)));

        ConditionBasedBacktestStrategy strategy = ConditionBasedBacktestStrategy.FromTree(tree);

        Assert.Equal("ConditionBased", strategy.Name);
        Assert.NotNull(strategy.Tree);
        Assert.NotSame(tree, strategy.Tree);
        Assert.Empty(strategy.Entries);
        Assert.Throws<ArgumentNullException>(() => ConditionBasedBacktestStrategy.FromTree(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConditionBasedBacktestStrategy.FromTree(tree, new BacktestRiskManagementSettings { StopLossPercent = 1m }));
    }

    [Fact]
    public void OrGroup_EntersWhenOnlyTheAlternativeBranchHolds_AndNestedAndNeedsBothLegs()
    {
        // (A AND B) OR C with A = Close > 1,000,000 (never), B = Close > 0 (always), C toggled.
        BacktestConditionGroup EntryLong(decimal cThreshold) => Group(LogicalOperator.Or,
            Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 1_000_000m), CloseLeaf(ComparisonOperator.GreaterThan, 0m)),
            CloseLeaf(ComparisonOperator.GreaterThan, cThreshold));

        BacktestResult onlyAB = Run(ConditionBasedBacktestStrategy.FromTree(Tree(entryLong: EntryLong(1_000_000m))));
        BacktestResult viaC = Run(ConditionBasedBacktestStrategy.FromTree(Tree(entryLong: EntryLong(0m))));

        Assert.DoesNotContain(onlyAB.Signals, s => s.Type == SignalType.LongEntry);
        Assert.Contains(viaC.Signals, s => s.Type == SignalType.LongEntry && s.Reason == "Condition entry chain matched");
    }

    [Fact]
    public void Exit_IsSideSpecific_ExitLongNeverClosesAHeldShort()
    {
        BacktestConditionGroup always = Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 0m));
        var bars = WavyBars(20);

        BacktestResult exitLongOnly = Run(ConditionBasedBacktestStrategy.FromTree(Tree(entryShort: always, exitLong: always)), bars);
        BacktestResult exitShort = Run(ConditionBasedBacktestStrategy.FromTree(Tree(entryShort: always, exitShort: always)), bars);

        Assert.Contains(exitLongOnly.Signals, s => s.Type == SignalType.ShortEntry);
        Assert.DoesNotContain(exitLongOnly.Signals, s => s.Type is SignalType.LongExit or SignalType.ShortExit);
        Assert.Contains(exitShort.Signals, s => s.Type == SignalType.ShortExit && s.Reason == "Condition exit chain matched");
    }

    [Fact]
    public void Reverse_OpensTheOppositeSideAndClosesTheHeldOneInTheSameBar_WithLegacyReasons()
    {
        BacktestConditionGroup always = Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 0m));

        // Enter Long, then ReverseShort is always true: holding Long => ShortEntry + LongExit in the same bar.
        BacktestResult result = Run(ConditionBasedBacktestStrategy.FromTree(Tree(entryLong: always, reverseShort: always)), WavyBars(20));

        Assert.Contains(result.Signals, s => s.Type == SignalType.ShortEntry && s.Reason == "Reversal: opening Short");
        Assert.Contains(result.Signals, s => s.Type == SignalType.LongExit && s.Reason == "Reversal: closing Long before opening Short");
    }

    [Fact]
    public void TreeFingerprint_IsAvailableStableAndSensitiveToStructure_AndDiffersFromTheListLayout()
    {
        var bars = WavyBars();
        BacktestConditionTree Make(LogicalOperator op, bool swap)
        {
            IBacktestConditionNode a = CloseLeaf(ComparisonOperator.GreaterThan, 90m), b = CloseLeaf(ComparisonOperator.LessThan, 200m);
            return Tree(entryLong: swap ? Group(op, b, a) : Group(op, a, b));
        }

        string baseline = FingerprintHex(ConditionBasedBacktestStrategy.FromTree(Make(LogicalOperator.And, false)), bars);

        Assert.Equal(baseline, FingerprintHex(ConditionBasedBacktestStrategy.FromTree(Make(LogicalOperator.And, false)), bars));
        Assert.NotEqual(baseline, FingerprintHex(ConditionBasedBacktestStrategy.FromTree(Make(LogicalOperator.Or, false)), bars));
        Assert.NotEqual(baseline, FingerprintHex(ConditionBasedBacktestStrategy.FromTree(Make(LogicalOperator.And, true)), bars));

        // D9: a migrated legacy configuration keeps its run hash but gets the tree manifest, hence a new fingerprint.
        var legacy = new ConditionBasedBacktestStrategy(LegacyEntries());
        ConditionBasedBacktestStrategy migrated = ConditionBasedBacktestStrategy.FromTree(BacktestConditionTreeMigrator.Migrate(LegacyEntries()));
        Assert.NotEqual(FingerprintHex(legacy, bars), FingerprintHex(migrated, bars));
        Assert.Equal(Run(legacy, bars).ReproducibilityHash, Run(migrated, bars).ReproducibilityHash);
    }

    [Fact]
    public void TreeFingerprint_CoversRiskManagement()
    {
        var bars = WavyBars();
        BacktestConditionTree tree = Tree(entryLong: Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 90m)));

        string none = FingerprintHex(ConditionBasedBacktestStrategy.FromTree(tree), bars);
        string withRisk = FingerprintHex(ConditionBasedBacktestStrategy.FromTree(tree, new BacktestRiskManagementSettings { StopLossPercent = 0.05m }), bars);

        Assert.NotEqual(none, withRisk);
    }

    [Fact]
    public void Specification_ConditionTree_BuildsATreeStrategyAndKeepsExistingKindValues()
    {
        BacktestConditionTree tree = Tree(entryLong: Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 90m)));

        var spec = BacktestStrategySpecification.ConditionTree(tree);

        Assert.Equal(0, (int)BacktestStrategyKind.NoOp);
        Assert.Equal(1, (int)BacktestStrategyKind.ConditionBased);
        Assert.Equal(2, (int)BacktestStrategyKind.ConditionTree);
        Assert.Equal(BacktestStrategyKind.ConditionTree, spec.Kind);
        Assert.NotNull(spec.Tree);
        Assert.NotSame(spec.Tree, spec.Tree);
        Assert.Empty(spec.Conditions);
        Assert.Single(spec.GetRequiredIndicators());
        Assert.Null(BacktestStrategySpecification.ConditionBased(LegacyEntries()).Tree);
        Assert.Throws<ArgumentNullException>(() => BacktestStrategySpecification.ConditionTree(null!));
    }
}
