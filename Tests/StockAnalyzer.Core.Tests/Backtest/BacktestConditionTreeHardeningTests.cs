using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Hardening of the condition tree (MP-3 P1 and the canonical-order contract). Characterization only: every expectation here was derived from the
/// specification tables of Y:\Temp\sa_implementation_plan_BacktestConditionTree_Hardening.md section 3 and holds on the shipped code.
/// </summary>
public class BacktestConditionTreeHardeningTests
{
    private static readonly string[] CanonicalNames = { "EntryLong", "EntryShort", "ExitLong", "ExitShort", "ReverseLong", "ReverseShort" };

    // ---- canonical root order (single definition, audit M1) ------------------------------------------------------------------

    [Fact]
    public void CanonicalRoots_AreTheDocumentedOrder()
    {
        Assert.Equal(
            CanonicalNames,
            BacktestConditionTree.CanonicalRoots.Select(root => BacktestConditionTree.RootName(root.Section, root.Side)));
    }

    [Fact]
    public void PersistedDtoProperties_AreNamedAndDeclaredInCanonicalOrder()
    {
        string[] declared = typeof(BacktestConditionTreeDto).GetProperties()
            .Where(p => p.PropertyType == typeof(BacktestConditionNodeDto))
            .OrderBy(p => p.MetadataToken)
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(CanonicalNames, declared);
    }

    [Fact]
    public void TreeAccessors_AreTheSameRootsAsTheCanonicalLookup()
    {
        BacktestConditionTree tree = BacktestConditionTree.Create((_, _) => Group(LogicalOperator.And, Leaf(Side())));

        foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
        {
            PropertyInfo property = typeof(BacktestConditionTree).GetProperty(BacktestConditionTree.RootName(section, side))!;
            Assert.Same(property.GetValue(tree), tree.Root(section, side));
        }
        Assert.Equal(6, BacktestConditionTree.CanonicalRoots.Distinct().Count());
    }

    [Fact]
    public void Create_AsksForEachRootOnceInCanonicalOrder()
    {
        var asked = new List<string>();

        BacktestConditionTree.Create((section, side) =>
        {
            asked.Add(BacktestConditionTree.RootName(section, side));
            return BacktestConditionGroup.EmptyAnd;
        });

        Assert.Equal(CanonicalNames, asked);
    }

    [Fact]
    public void ActiveSides_FollowTheCanonicalRootOrder()
    {
        BacktestConditionTree tree = BacktestConditionTree.Create((section, side) =>
        {
            int index = BacktestConditionTree.CanonicalRoots.IndexOf((section, side));
            return Group(LogicalOperator.And, Leaf(new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Offset = index }));
        });

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, BacktestConditionValidator.ActiveSides(tree).Select(side => side.Offset));
    }

    [Fact]
    public void LeafOrdinals_InMessagesFollowTheCanonicalRootOrder()
    {
        for (int bad = 0; bad < CanonicalNames.Length; bad++)
        {
            BacktestConditionNodeDto[] nodes = Enumerable.Range(0, 6)
                .Select(k => DtoGroup(DtoLeaf(offset: k == bad ? -1 : 0)))
                .ToArray();
            var dto = new BacktestConfigurationDto
            {
                SchemaVersion = BacktestConfigurationDto.ConditionTreeSchemaVersion,
                ConditionTree = new BacktestConditionTreeDto
                {
                    EntryLong = nodes[0], EntryShort = nodes[1], ExitLong = nodes[2], ExitShort = nodes[3], ReverseLong = nodes[4], ReverseShort = nodes[5],
                },
            };

            ArgumentException ex = Assert.ThrowsAny<ArgumentException>(
                () => BacktestConditionTreeDtoMapper.ResolveTree(dto, 500, DefaultDepth, DefaultNodes));

            Assert.Contains($"ConditionEntries[{bad}]", ex.Message);
        }
    }

    private static readonly int DefaultDepth = new BacktestSettings().MaxConditionTreeDepth;
    private static readonly int DefaultNodes = new BacktestSettings().MaxConditionTreeNodes;

    private static BacktestConditionNodeDto DtoLeaf(int offset = 0) => new()
    {
        Kind = BacktestConditionNodeKind.Leaf,
        Comparison = new BacktestConditionEntryDto
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA, Offset = offset },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1m,
        },
    };

    private static BacktestConditionNodeDto DtoGroup(params BacktestConditionNodeDto[] children)
        => new() { Kind = BacktestConditionNodeKind.Group, Operator = LogicalOperator.And, Children = children.ToList() };

    // ---- P1-1: state-transition table ---------------------------------------------------------------------------------------

    private const string EntryReason = "Condition entry chain matched";
    private const string ExitReason = "Condition exit chain matched";
    private const string OpenShortReason = "Reversal: opening Short";
    private const string OpenLongReason = "Reversal: opening Long";
    private const string CloseLongReason = "Reversal: closing Long before opening Short";
    private const string CloseShortReason = "Reversal: closing Short before opening Long";
    private const string StopReason = "Stop-Loss breach";
    private const string TakeProfitReason = "Take-Profit breach";

    private static BacktestConditionGroup TableRoot(char code, LogicalOperator op = LogicalOperator.And) => code switch
    {
        'T' => Group(op, CloseLeaf(ComparisonOperator.GreaterThan, 0m)),
        'F' => Group(op, CloseLeaf(ComparisonOperator.GreaterThan, 1_000_000m)),
        '-' => BacktestConditionGroup.EmptyAnd,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private static (ConditionBasedBacktestStrategy Strategy, StrategyContext Context) TableCase(
        BacktestConditionTree tree, TradeSide? position, BacktestRiskManagementSettings? risk, decimal? entryPrice, decimal low, decimal high)
    {
        ConditionBasedBacktestStrategy strategy = ConditionBasedBacktestStrategy.FromTree(tree, risk);
        // A tree without any leaf registers no indicator (all six roots inactive); otherwise all leaves share the single Price/Close request.
        var series = new Dictionary<string, ImmutableArray<decimal?>>();
        foreach (StrategyIndicatorRequest request in strategy.GetRequiredIndicators())
        {
            series[request.Key] = ImmutableArray.Create<decimal?>(100m);
        }
        var indicators = new IndicatorSeriesSet(series);
        var context = new StrategyContext
        {
            BarIndex = 0,
            Bar = SyntheticBars.Bar(0, 100m, high, low, 100m),
            PositionSide = position,
            Snapshot = default,
            Indicators = indicators,
            EntryPrice = entryPrice,
        };
        return (strategy, context);
    }

    /// <summary>roots = six characters in canonical order (EntryLong, EntryShort, ExitLong, ExitShort, ReverseLong, ReverseShort): T true, F false, - empty.</summary>
    private static BacktestConditionTree TableTree(string roots)
    {
        int index = 0;
        return BacktestConditionTree.Create((_, _) => TableRoot(roots[index++]));
    }

    public static IEnumerable<object?[]> StateTable()
    {
        // id, roots, position, stop-loss, take-profit, entry price, low, high, evaluate (signal, order, limit, stop, reason), evaluateExit (signal, reason)
        yield return new object?[] { "S01", "TT----", null, null, null, null, 100m, 100m, SignalType.LongEntry, OrderType.Market, null, null, EntryReason, null, null };
        yield return new object?[] { "S02", "FT----", null, null, null, null, 100m, 100m, SignalType.ShortEntry, OrderType.Market, null, null, EntryReason, null, null };
        yield return new object?[] { "S03", "FFTTTT", null, null, null, null, 100m, 100m, null, null, null, null, null, null, null };
        yield return new object?[] { "S04", "------", null, null, null, null, 100m, 100m, null, null, null, null, null, null, null };
        yield return new object?[] { "S05", "--T--T", TradeSide.Long, null, null, null, 100m, 100m, SignalType.ShortEntry, OrderType.Market, null, null, OpenShortReason, SignalType.LongExit, CloseLongReason };
        yield return new object?[] { "S06", "--T--F", TradeSide.Long, null, null, null, 100m, 100m, SignalType.LongExit, OrderType.Market, null, null, ExitReason, null, null };
        yield return new object?[] { "S07", "TTFTTF", TradeSide.Long, null, null, null, 100m, 100m, null, null, null, null, null, null, null };
        yield return new object?[] { "S08", "---TT-", TradeSide.Short, null, null, null, 100m, 100m, SignalType.LongEntry, OrderType.Market, null, null, OpenLongReason, SignalType.ShortExit, CloseShortReason };
        yield return new object?[] { "S09", "---T--", TradeSide.Short, null, null, null, 100m, 100m, SignalType.ShortExit, OrderType.Market, null, null, ExitReason, null, null };
        yield return new object?[] { "S10", "--T---", TradeSide.Short, null, null, null, 100m, 100m, null, null, null, null, null, null, null };
        yield return new object?[] { "S11", "------", TradeSide.Long, 0.05m, null, 100m, 94m, 100m, SignalType.LongExit, OrderType.Stop, null, 95m, StopReason, null, null };
        yield return new object?[] { "S12", "------", TradeSide.Long, 0.05m, 0.10m, 100m, 94m, 111m, SignalType.LongExit, OrderType.Stop, null, 95m, StopReason, null, null };
        yield return new object?[] { "S13", "------", TradeSide.Long, null, 0.10m, 100m, 99m, 111m, SignalType.LongExit, OrderType.Limit, 110m, null, TakeProfitReason, null, null };
        yield return new object?[] { "S14", "-----T", TradeSide.Long, 0.05m, null, 100m, 94m, 100m, SignalType.LongExit, OrderType.Stop, null, 95m, StopReason, SignalType.LongExit, CloseLongReason };
        yield return new object?[] { "S15", "-----T", TradeSide.Long, 0.05m, null, null, 94m, 100m, SignalType.ShortEntry, OrderType.Market, null, null, OpenShortReason, SignalType.LongExit, CloseLongReason };
        yield return new object?[] { "S16", "------", TradeSide.Short, 0.05m, null, 100m, 100m, 106m, SignalType.ShortExit, OrderType.Stop, null, 105m, StopReason, null, null };
    }

    [Theory]
    [MemberData(nameof(StateTable))]
    public void StateTable_MatchesTheSpecification(
        string id, string roots, TradeSide? position, decimal? stopLoss, decimal? takeProfit, decimal? entryPrice, decimal low, decimal high,
        SignalType? evalSignal, OrderType? evalOrder, decimal? evalLimit, decimal? evalStop, string? evalReason, SignalType? exitSignal, string? exitReason)
    {
        var risk = stopLoss is null && takeProfit is null
            ? null
            : new BacktestRiskManagementSettings { StopLossPercent = stopLoss, TakeProfitPercent = takeProfit };
        var (strategy, context) = TableCase(TableTree(roots), position, risk, entryPrice, low, high);

        StrategyOrderRequest? evaluated = strategy.Evaluate(context);
        StrategyOrderRequest? exit = strategy.EvaluateExit(context);

        Assert.True(evalSignal is null == evaluated is null, $"{id}: Evaluate presence");
        if (evalSignal is not null)
        {
            Assert.Equal(evalSignal, evaluated!.Value.SignalType);
            Assert.Equal(evalOrder, evaluated.Value.OrderType);
            Assert.Equal(evalLimit, evaluated.Value.LimitPrice);
            Assert.Equal(evalStop, evaluated.Value.StopPrice);
            Assert.Equal(evalReason, evaluated.Value.Reason);
        }
        Assert.True(exitSignal is null == exit is null, $"{id}: EvaluateExit presence");
        if (exitSignal is not null)
        {
            Assert.Equal(exitSignal, exit!.Value.SignalType);
            Assert.Equal(exitReason, exit.Value.Reason);
        }
    }

    [Fact]
    public void StateTable_EntryRootOperatorsDecideTheEntry()
    {
        BacktestConditionTree Or(char first, char second) => BacktestConditionTree.Create((section, side) =>
            section == BacktestConditionSection.Entry && side == TradeSide.Long
                ? Group(LogicalOperator.Or, TableRoot(first).Children[0], TableRoot(second).Children[0])
                : BacktestConditionGroup.EmptyAnd);
        BacktestConditionTree And(char first, char second) => BacktestConditionTree.Create((section, side) =>
            section == BacktestConditionSection.Entry && side == TradeSide.Long
                ? Group(LogicalOperator.And, TableRoot(first).Children[0], TableRoot(second).Children[0])
                : BacktestConditionGroup.EmptyAnd);

        var (orStrategy, orContext) = TableCase(Or('F', 'T'), null, null, null, 100m, 100m);
        var (andStrategy, andContext) = TableCase(And('F', 'T'), null, null, null, 100m, 100m);

        Assert.Equal(SignalType.LongEntry, orStrategy.Evaluate(orContext)!.Value.SignalType);
        Assert.Null(andStrategy.Evaluate(andContext));
    }

    [Fact]
    public void StateTable_AnOrRootWithOnlyAFalseLeafIsFalse_NotAnEmptyAnd()
    {
        BacktestConditionTree tree = BacktestConditionTree.Create((section, side) =>
            section == BacktestConditionSection.Entry && side == TradeSide.Long
                ? Group(LogicalOperator.Or, TableRoot('F').Children[0])
                : BacktestConditionGroup.EmptyAnd);
        var (strategy, context) = TableCase(tree, null, null, null, 100m, 100m);

        Assert.Null(strategy.Evaluate(context));
    }

    // ---- P1-2: evaluation contract --------------------------------------------------------------------------------------------

    private static readonly ComparisonOperator[] SupportedOperators =
    {
        ComparisonOperator.GreaterThan, ComparisonOperator.GreaterThanOrEqual, ComparisonOperator.LessThan,
        ComparisonOperator.LessThanOrEqual, ComparisonOperator.Equal, ComparisonOperator.NotEqual,
    };

    private static bool EvaluateSingle(BacktestConditionEntry entry, IReadOnlyDictionary<BacktestConditionSide, string> map, IndicatorSeriesSet series, int bar)
        => BacktestConditionTreeEvaluator.Evaluate(new BacktestConditionLeaf(entry), map, series, bar);

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual)]
    [InlineData(ComparisonOperator.LessThan)]
    [InlineData(ComparisonOperator.LessThanOrEqual)]
    [InlineData(ComparisonOperator.Equal)]
    [InlineData(ComparisonOperator.NotEqual)]
    public void UnavailableValue_IsFalseForEveryOperator_IncludingNotEqual(ComparisonOperator op)
    {
        var left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA };
        var right = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 9 } };
        var series = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>>
        {
            ["l"] = ImmutableArray.Create<decimal?>(null, 5m),
            ["r"] = ImmutableArray.Create<decimal?>(5m, null),
        });
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "l", [right] = "r" };

        var leftNull = new BacktestConditionEntry { Left = left, Operator = op, TargetMode = RightHandTargetMode.NumericValue, RightNumericValue = 1m };
        var rightNull = new BacktestConditionEntry { Left = left, Operator = op, TargetMode = RightHandTargetMode.Indicator, Right = right };

        Assert.False(EvaluateSingle(leftNull, map, series, 0));
        Assert.False(EvaluateSingle(rightNull, map, series, 1));
    }

    [Fact]
    public void OffsetBeforeTheFirstBar_IsFalseEvenForNotEqual()
    {
        var side = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Offset = 2 };
        var series = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>> { ["k"] = ImmutableArray.Create<decimal?>(1m, 1m, 1m) });
        var map = new Dictionary<BacktestConditionSide, string> { [side] = "k" };
        var entry = new BacktestConditionEntry { Left = side, Operator = ComparisonOperator.NotEqual, TargetMode = RightHandTargetMode.NumericValue, RightNumericValue = 0m };

        Assert.False(EvaluateSingle(entry, map, series, 1));
        Assert.True(EvaluateSingle(entry, map, series, 2));
    }

    [Fact]
    public void Comparison_IsExactDecimal()
    {
        var side = new BacktestConditionSide { IndicatorType = IndicatorType.SMA };
        var series = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>> { ["k"] = ImmutableArray.Create<decimal?>(0.1m + 0.2m) });
        var map = new Dictionary<BacktestConditionSide, string> { [side] = "k" };

        Assert.True(EvaluateSingle(new BacktestConditionEntry { Left = side, Operator = ComparisonOperator.Equal, RightNumericValue = 0.3m }, map, series, 0));
        Assert.False(EvaluateSingle(new BacktestConditionEntry { Left = side, Operator = ComparisonOperator.Equal, RightNumericValue = 0.3000000000000000000000000001m }, map, series, 0));
    }

    [Fact]
    public void DirectCallWithAnUnmappedSide_ThrowsKeyNotFound()
    {
        var side = new BacktestConditionSide { IndicatorType = IndicatorType.SMA };
        var series = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>> { ["k"] = ImmutableArray.Create<decimal?>(1m) });
        var entry = new BacktestConditionEntry { Left = side, Operator = ComparisonOperator.GreaterThan, RightNumericValue = 0m };

        Assert.Throws<KeyNotFoundException>(() => EvaluateSingle(entry, new Dictionary<BacktestConditionSide, string>(), series, 0));
    }

    // ---- shared random generators ---------------------------------------------------------------------------------------------

    private static BacktestConditionSide TemplateSide(int template, int offset) => template switch
    {
        0 => new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, Offset = offset },
        1 => new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.High, Offset = offset },
        2 => new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 3 }, Offset = offset },
        _ => new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 5 }, Offset = offset },
    };

    private const int TemplateCount = 4;

    private static BacktestConditionEntry RandomComparison(Random random, BacktestConditionSide left, BacktestConditionSide? right)
        => new()
        {
            Left = left,
            Operator = SupportedOperators[random.Next(SupportedOperators.Length)],
            TargetMode = right is null ? RightHandTargetMode.NumericValue : RightHandTargetMode.Indicator,
            RightNumericValue = random.Next(50, 151),
            Right = right,
        };

    private static ImmutableArray<decimal?> RandomSeries(Random random, int bars)
        => Enumerable.Range(0, bars).Select(_ => random.NextDouble() < 0.2 ? (decimal?)null : random.Next(50, 151)).ToImmutableArray();

    private sealed class MutableGroup
    {
        public MutableGroup(LogicalOperator op, int depth) { Operator = op; Depth = depth; }
        public LogicalOperator Operator { get; }
        public int Depth { get; }
        public List<object> Children { get; } = new();
    }

    private static BacktestConditionGroup RandomRoot(Random random, int maxDepth, int maxNodes)
    {
        int target = random.Next(0, maxNodes + 1);
        if (target == 0) return BacktestConditionGroup.EmptyAnd;

        var root = new MutableGroup(random.Next(2) == 0 ? LogicalOperator.And : LogicalOperator.Or, 1);
        var groups = new List<MutableGroup> { root };
        int count = 1;
        for (int attempt = 0; attempt < target * 4 && count < target; attempt++)
        {
            MutableGroup parent = groups[random.Next(groups.Count)];
            bool addGroup = random.NextDouble() < 0.3 && parent.Depth < maxDepth && count + 2 <= target;
            if (addGroup)
            {
                var child = new MutableGroup(random.Next(2) == 0 ? LogicalOperator.And : LogicalOperator.Or, parent.Depth + 1);
                child.Children.Add(RandomLeaf(random));
                parent.Children.Add(child);
                groups.Add(child);
                count += 2;
            }
            else
            {
                parent.Children.Add(RandomLeaf(random));
                count++;
            }
        }
        return Freeze(root);
    }

    private static BacktestConditionLeaf RandomLeaf(Random random)
    {
        bool indicatorMode = random.Next(2) == 0;
        return new BacktestConditionLeaf(RandomComparison(
            random,
            TemplateSide(random.Next(TemplateCount), random.Next(0, 6)),
            indicatorMode ? TemplateSide(random.Next(TemplateCount), random.Next(0, 6)) : null));
    }

    private static BacktestConditionGroup Freeze(MutableGroup group)
        => new(group.Operator, group.Children.Select(child => child is MutableGroup nested ? Freeze(nested) : (IBacktestConditionNode)child).ToImmutableArray());

    // ---- P1-3: validated trees never throw ------------------------------------------------------------------------------------

    [Fact]
    public void ValidatedTrees_NeverThrowWhileEvaluating()
    {
        var settings = new BacktestSettings();
        var random = new Random(20260926);
        const int treeCount = 2000;
        const int bars = 40;
        TradeSide?[] positions = { null, TradeSide.Long, TradeSide.Short };

        for (int t = 0; t < treeCount; t++)
        {
            BacktestConditionTree generated = BacktestConditionTree.Create((_, _) => RandomRoot(random, settings.MaxConditionTreeDepth, settings.MaxConditionTreeNodes));
            BacktestConditionTree tree = BacktestConditionValidator.SnapshotTree(generated, settings.MaxConditionOffset, settings.MaxConditionTreeDepth, settings.MaxConditionTreeNodes);
            ConditionBasedBacktestStrategy strategy = ConditionBasedBacktestStrategy.FromTree(tree);
            var series = strategy.GetRequiredIndicators().ToDictionary(request => request.Key, _ => RandomSeries(random, bars));
            var set = new IndicatorSeriesSet(series);

            for (int bar = 0; bar < bars; bar++)
            {
                foreach (TradeSide? position in positions)
                {
                    var context = new StrategyContext { BarIndex = bar, Bar = SyntheticBars.Bar(bar, 100m, 100m, 100m, 100m), PositionSide = position, Indicators = set };
                    _ = strategy.Evaluate(context);
                    _ = strategy.EvaluateExit(context);
                }
            }
        }
    }

    // ---- P1-4: randomized differential against the legacy list evaluation ------------------------------------------------------

    private static BacktestConditionEntry RandomListEntry(Random random, Dictionary<BacktestConditionSide, string> map, int index, bool alternating)
    {
        int leftTemplate = random.Next(TemplateCount);
        BacktestConditionSide left = TemplateSide(leftTemplate, random.Next(0, 6));
        map[left] = "k" + leftTemplate;
        BacktestConditionSide? right = null;
        if (random.Next(2) == 0)
        {
            int rightTemplate = random.Next(TemplateCount);
            right = TemplateSide(rightTemplate, random.Next(0, 6));
            map[right] = "k" + rightTemplate;
        }

        BacktestConditionEntry comparison = RandomComparison(random, left, right);
        BacktestConditionRole[] roles = { BacktestConditionRole.EntryOnly, BacktestConditionRole.ExitOnly, BacktestConditionRole.Both, BacktestConditionRole.Reversal };
        return new BacktestConditionEntry
        {
            Left = comparison.Left,
            Operator = comparison.Operator,
            TargetMode = comparison.TargetMode,
            RightNumericValue = comparison.RightNumericValue,
            Right = comparison.Right,
            Role = alternating ? roles[index % 4] : roles[random.Next(4)],
            Position = alternating ? (index % 2 == 0 ? TradeSide.Long : TradeSide.Short) : (random.Next(2) == 0 ? TradeSide.Long : TradeSide.Short),
            LogicalOperator = alternating ? (index % 2 == 0 ? LogicalOperator.And : LogicalOperator.Or) : (random.Next(2) == 0 ? LogicalOperator.And : LogicalOperator.Or),
        };
    }

    private static void AssertMigrationEquivalent(BacktestConditionEntry[] entries, Dictionary<BacktestConditionSide, string> map, IndicatorSeriesSet series, int bars, string label)
    {
        BacktestConditionExecutionPaths paths = BacktestConditionExecutionPaths.Create(entries);
        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(entries);

        for (int bar = 0; bar < bars; bar++)
        {
            void Same(ImmutableArray<BacktestConditionEntry> path, BacktestConditionGroup root, string name)
            {
                bool legacy = BacktestConditionEvaluator.Evaluate(path, map, series, bar);
                bool migrated = BacktestConditionTreeEvaluator.Evaluate(root, map, series, bar);
                Assert.True(legacy == migrated, $"{label} {name} bar {bar}: legacy={legacy} tree={migrated}");
            }
            Same(paths.LongEntry, tree.EntryLong, "LongEntry");
            Same(paths.ShortEntry, tree.EntryShort, "ShortEntry");
            Same(paths.Exit, tree.ExitLong, "ExitLong");
            Same(paths.Exit, tree.ExitShort, "ExitShort");
            Same(paths.ReverseLong, tree.ReverseLong, "ReverseLong");
            Same(paths.ReverseShort, tree.ReverseShort, "ReverseShort");
        }
    }

    [Fact]
    public void Migration_MatchesTheLegacyEvaluation_OnRandomAndAlternatingLists()
    {
        const int bars = 40;
        var random = new Random(20260927);
        var series = new IndicatorSeriesSet(Enumerable.Range(0, TemplateCount).ToDictionary(k => "k" + k, _ => RandomSeries(random, bars)));

        foreach (int length in new[] { 1, 2, 3, 5, 10, 50, 100, 1000 })
        {
            for (int list = 0; list < 25; list++)
            {
                var map = new Dictionary<BacktestConditionSide, string>();
                BacktestConditionEntry[] entries = Enumerable.Range(0, length).Select(i => RandomListEntry(random, map, i, alternating: false)).ToArray();
                AssertMigrationEquivalent(entries, map, series, bars, $"random L={length} #{list}");
            }
        }

        foreach (int length in new[] { 100, 1000 })
        {
            var map = new Dictionary<BacktestConditionSide, string>();
            BacktestConditionEntry[] entries = Enumerable.Range(0, length).Select(i => RandomListEntry(random, map, i, alternating: true)).ToArray();
            AssertMigrationEquivalent(entries, map, series, bars, $"alternating L={length}");
        }
    }

    // ---- P1-6: structure counts ---------------------------------------------------------------------------------------------

    private static BacktestConditionTree TreeWithEntryLong(BacktestConditionGroup entryLong)
        => BacktestConditionTree.Create((section, side) => section == BacktestConditionSection.Entry && side == TradeSide.Long ? entryLong : BacktestConditionGroup.EmptyAnd);

    private static BacktestConditionLeaf NumericLeaf() => new(new BacktestConditionEntry { Left = Side(), RightNumericValue = 1m });

    [Fact]
    public void NodeCount_CountsTheRootAndEveryNode_AndTheLimitPasses()
    {
        int limit = new BacktestSettings().MaxConditionTreeNodes;
        BacktestConditionGroup Leaves(int n) => Group(LogicalOperator.And, Enumerable.Range(0, n).Select(_ => (IBacktestConditionNode)NumericLeaf()).ToArray());

        _ = BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(Leaves(limit - 1)), null, null, limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(Leaves(limit)), null, null, limit));
        _ = BacktestConditionValidator.SnapshotTree(BacktestConditionTree.Empty, null, 1, 1);
        _ = BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(Leaves(1)), null, 1, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(Leaves(1)), null, 1, 1));
    }

    [Fact]
    public void NodeCount_CountsANodeInstancePlacedTwiceTwice()
    {
        BacktestConditionLeaf shared = NumericLeaf();
        BacktestConditionGroup root = Group(LogicalOperator.And, shared, shared);

        _ = BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(root), null, null, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(root), null, null, 2));
    }

    [Fact]
    public void Depth_CountsTheRootAsOne()
    {
        BacktestConditionGroup nested = Group(LogicalOperator.And, Group(LogicalOperator.Or, NumericLeaf()));

        _ = BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(nested), null, 2, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(TreeWithEntryLong(nested), null, 1, null));
    }

    // ---- P1-7: fingerprint goldens --------------------------------------------------------------------------------------------
    // Captured from the code at MP-3 P1 (after the canonical-order consolidation): they guard future regressions of the byte layout; they do not
    // prove the pre-MP-1 layout (that proof is the golden of BacktestConditionTreeStrategyTests, captured before any production edit).

    private const string TreeLayoutGolden = "697273CD752B7F32C8BFF5EBB13772A3DC7FAD9AA9A3247AD495F89DAF55D4B3";

    private static ConditionBasedBacktestStrategy GoldenTreeStrategy()
    {
        BacktestConditionGroup entryLong = Group(
            LogicalOperator.Or,
            Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.GreaterThan, 90m), CloseLeaf(ComparisonOperator.LessThan, 200m)),
            CloseLeaf(ComparisonOperator.GreaterThan, 150m));
        BacktestConditionGroup exitShort = Group(LogicalOperator.And, CloseLeaf(ComparisonOperator.LessThan, 80m));
        BacktestConditionTree tree = BacktestConditionTree.Create((section, side) =>
            section == BacktestConditionSection.Entry && side == TradeSide.Long ? entryLong
            : section == BacktestConditionSection.Exit && side == TradeSide.Short ? exitShort
            : BacktestConditionGroup.EmptyAnd);
        return ConditionBasedBacktestStrategy.FromTree(tree, new BacktestRiskManagementSettings { StopLossPercent = 0.05m });
    }

    [Fact]
    public void TreeLayoutFingerprint_IsPinned()
    {
        Assert.Equal(TreeLayoutGolden, FingerprintHex(GoldenTreeStrategy(), WavyBars()));
    }

    [Theory]
    [InlineData("both role", "08FA04F7850989F2520DA48E72120839AFC0279E8B21DFCEA47CC3BFBE2ECB8D")]
    [InlineData("reversal long/short", "DDF75E27ACF906BA5EBB0DC670F618A2FAD4B415A1E37208C55D9C96704BCBAE")]
    [InlineData("or-connected entries, short entry, mixed exit", "B6EB0E78C061D036132924A428A4CB0BFBC95122C8A79A6364DC62DAFF94775A")]
    public void ListLayoutFingerprint_IsPinned(string scenario, string expected)
    {
        object?[] row = BacktestConditionTreeStrategyTests.LegacyScenarios().Single(r => (string)r[0]! == scenario);
        var strategy = new ConditionBasedBacktestStrategy((BacktestConditionEntry[])row[1]!, (BacktestRiskManagementSettings?)row[2]);

        Assert.Equal(expected, FingerprintHex(strategy, WavyBars()));
    }

    // ---- recursion guard (P2-4): a pathological depth is an error, never a crash -----------------------------------------------

    /// <summary>Alternating connectors nest one level per entry, so this legacy list migrates to a tree that is far deeper than any stack could recurse over.</summary>
    private static BacktestConditionEntryDto[] AbsurdlyDeepLegacyList(int length) => Enumerable.Range(0, length)
        .Select(i => new BacktestConditionEntryDto
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA },
            RightNumericValue = 1m,
            Role = BacktestConditionRole.EntryOnly,
            LogicalOperator = i % 2 == 0 ? LogicalOperator.And : LogicalOperator.Or,
        })
        .ToArray();

    private const int AbsurdDepth = 200_000;

    [Fact]
    public void AbsurdlyDeepLegacyList_ResolvesToAnErrorWithTheFixedMessage()
    {
        var dto = new BacktestConfigurationDto { SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion, ConditionEntries = AbsurdlyDeepLegacyList(AbsurdDepth).ToList() };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => BacktestConditionTreeDtoMapper.ResolveTree(dto, 500, DefaultDepth, DefaultNodes));

        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, ex.Message);
    }

    [Fact]
    public void AbsurdlyDeepLegacyList_IsALoadError_NotACrash()
    {
        BacktestConfigurationDto dto = new()
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            Symbol = "AAPL",
            Frame = TimeFrame.D1,
            EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            InitialCapital = 1_000_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            InitialMarginRatio = 0.30m,
            MaintenanceMarginRatio = 0.20m,
            LiquidationPenaltyRatio = 0.005m,
            ConditionEntries = AbsurdlyDeepLegacyList(AbsurdDepth).ToList(),
        };

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto, maxConditionTreeDepth: DefaultDepth, maxConditionTreeNodes: DefaultNodes);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, result.ErrorMessage);
    }

    [Fact]
    public void AbsurdlyDeepPersistedTree_WithoutADepthLimit_IsAnErrorWithTheFixedMessage()
    {
        BacktestConditionNodeDto deepest = DtoGroup(DtoLeaf());
        for (int i = 0; i < AbsurdDepth; i++) deepest = DtoGroup(deepest);
        var dto = new BacktestConditionTreeDto { EntryLong = deepest };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => BacktestConditionTreeDtoMapper.ToTree(dto, maxDepth: null));

        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, ex.Message);
    }

    [Fact]
    public void AbsurdlyDeepTree_IsRejectedByTheValidatorAndByActiveSides()
    {
        BacktestConditionGroup deepest = Group(LogicalOperator.And, NumericLeaf());
        for (int i = 0; i < AbsurdDepth; i++) deepest = Group(LogicalOperator.Or, deepest);
        BacktestConditionTree tree = TreeWithEntryLong(deepest);

        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, Assert.Throws<ArgumentException>(() => BacktestConditionValidator.SnapshotTree(tree, null, null, null)).Message);
        Assert.Contains(BacktestConditionTreeRule.NestingTooDeepMessage, Assert.Throws<ArgumentException>(() => BacktestConditionValidator.ActiveSides(tree)).Message);
    }

    // ---- node paths in messages (P2-2, P2-3) ----------------------------------------------------------------------------------

    private static string Path(params int[] childIndexes)
        => childIndexes.Aggregate("EntryLong", BacktestConditionTreeRule.ChildPath);

    [Fact]
    public void ChildPath_AppendsTheChildIndex()
    {
        Assert.Equal("EntryLong.children[1].children[0]", Path(1, 0));
    }

    [Fact]
    public void PrefixPath_KeepsTheExceptionTypeAndDoesNotDoubleTheFrameworkSuffixes()
    {
        ArgumentException outOfRange = BacktestConditionTreeRule.PrefixPath("A.children[0]", new ArgumentOutOfRangeException("offset", 5, "value is bad"));
        ArgumentException withParam = BacktestConditionTreeRule.PrefixPath("A", new ArgumentException("shape is bad", "node"));
        ArgumentException plain = BacktestConditionTreeRule.PrefixPath("A", new ArgumentException("plain"));

        Assert.IsType<ArgumentOutOfRangeException>(outOfRange);
        Assert.Equal(5, ((ArgumentOutOfRangeException)outOfRange).ActualValue);
        Assert.Equal("offset", outOfRange.ParamName);
        Assert.StartsWith("A.children[0]: value is bad", outOfRange.Message);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(outOfRange.Message, System.Text.RegularExpressions.Regex.Escape("(Parameter 'offset')")));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(outOfRange.Message, "Actual value was"));

        Assert.IsType<ArgumentException>(withParam);
        Assert.Equal("node", withParam.ParamName);
        Assert.StartsWith("A: shape is bad", withParam.Message);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(withParam.Message, System.Text.RegularExpressions.Regex.Escape("(Parameter 'node')")));

        Assert.IsType<ArgumentException>(plain);
        Assert.StartsWith("A: plain", plain.Message);
    }

    private static BacktestConditionTree PathTree(params IBacktestConditionNode[] rootChildren)
        => TreeWithEntryLong(Group(LogicalOperator.And, rootChildren));

    [Fact]
    public void ValidatorMessages_NameTheNodePath()
    {
        BacktestConditionLeaf ok = NumericLeaf();

        Assert.Contains(Path(0) + ": condition group depth 2",
            Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(PathTree(Group(LogicalOperator.And, ok)), null, 1, null)).Message);
        Assert.Contains(Path(2) + ": condition tree has more than",
            Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(PathTree(ok, ok, ok), null, null, 3)).Message);
        Assert.Contains(Path(1, 0) + ": a nested condition group must contain",
            Assert.Throws<ArgumentException>(() => BacktestConditionValidator.SnapshotTree(PathTree(ok, Group(LogicalOperator.And, Group(LogicalOperator.Or))), null, null, null)).Message);

        var withRole = new BacktestConditionLeaf(new BacktestConditionEntry { Left = Side(), RightNumericValue = 1m, Role = BacktestConditionRole.ExitOnly });
        Assert.Contains(Path(1) + ": ConditionEntries[1]: a condition-tree leaf must not carry",
            Assert.Throws<ArgumentException>(() => BacktestConditionValidator.SnapshotTree(PathTree(ok, withRole), null, null, null)).Message);
    }

    public static IEnumerable<object[]> BadLeaves()
    {
        yield return new object[] { "negative offset", new BacktestConditionEntry { Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Offset = -1 }, RightNumericValue = 1m } };
        yield return new object[] { "offset above the limit", new BacktestConditionEntry { Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Offset = 501 }, RightNumericValue = 1m } };
        yield return new object[] { "indicator mode without Right", new BacktestConditionEntry { Left = Side(), TargetMode = RightHandTargetMode.Indicator, Right = null } };
    }

    [Theory]
    [MemberData(nameof(BadLeaves))]
    public void LeafValidationErrors_CarryTheNodePath_AndKeepTheirExceptionType(string label, BacktestConditionEntry bad)
    {
        Exception listError = Assert.ThrowsAny<Exception>(() => BacktestConditionValidator.Snapshot(new[] { bad }, 500));
        BacktestConditionTree tree = PathTree(NumericLeaf(), Group(LogicalOperator.And, new BacktestConditionLeaf(bad)));

        Exception treeError = Assert.ThrowsAny<Exception>(() => BacktestConditionValidator.SnapshotTree(tree, 500, null, null));

        Assert.True(listError.GetType() == treeError.GetType(), $"{label}: {listError.GetType().Name} vs {treeError.GetType().Name}");
        Assert.Contains(Path(1, 0) + ": ", treeError.Message);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(treeError.Message, "Actual value was").Count <= 1, "the framework suffix must not be doubled");
    }

    [Fact]
    public void PersistedTreeMessages_NameTheNodePath()
    {
        BacktestConditionNodeDto Root(params BacktestConditionNodeDto[] children) => DtoGroup(children);
        var leafWithoutComparison = new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Leaf };
        var badLeaf = new BacktestConditionNodeDto
        {
            Kind = BacktestConditionNodeKind.Leaf,
            Comparison = new BacktestConditionEntryDto { Left = null! },
        };

        Assert.Contains("EntryLong: a condition tree root must be a Group",
            Assert.Throws<ArgumentException>(() => BacktestConditionTreeDtoMapper.ToTree(new BacktestConditionTreeDto { EntryLong = DtoLeaf() })).Message);
        Assert.Contains(Path(1) + ": a Leaf node requires a Comparison",
            Assert.Throws<ArgumentException>(() => BacktestConditionTreeDtoMapper.ToTree(new BacktestConditionTreeDto { EntryLong = Root(DtoLeaf(), leafWithoutComparison) })).Message);
        Assert.Contains(Path(0) + ": condition group depth 2",
            Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionTreeDtoMapper.ToTree(new BacktestConditionTreeDto { EntryLong = Root(DtoGroup(DtoLeaf())) }, maxDepth: 1)).Message);
        Assert.Contains(Path(1, 0) + ": ",
            Assert.ThrowsAny<ArgumentException>(() => BacktestConditionTreeDtoMapper.ToTree(new BacktestConditionTreeDto { EntryLong = Root(DtoLeaf(), DtoGroup(badLeaf)) })).Message);
    }

    // ---- P1-8: closed set of node types -------------------------------------------------------------------------------------

    [Fact]
    public void ConditionNode_IsAClosedSetOfTwoTypes()
    {
        string[] implementations = typeof(IBacktestConditionNode).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IBacktestConditionNode).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            implementations.SequenceEqual(new[] { nameof(BacktestConditionGroup), nameof(BacktestConditionLeaf) }),
            "A new condition node type was added: update BacktestConditionTreeEvaluator, BacktestConditionValidator (SnapshotGroup, CollectSides), " +
            "BacktestConditionTreeDtoMapper, BacktestConditionTreeMigrator, BacktestRunFingerprintBuilder.WriteConditionNode, BacktestConfigurationSnapshot, and this test.");
    }
}
