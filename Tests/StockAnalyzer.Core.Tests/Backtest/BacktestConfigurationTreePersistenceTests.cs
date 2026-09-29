using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using System.Collections.Immutable;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Condition-tree persistence, schema version 2 and legacy migration (MP-1 Phase 4). See Y:\Temp\sa_analysis_BacktestConditionTree.md B6.</summary>
public class BacktestConfigurationTreePersistenceTests : IDisposable
{
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"backtest_tree_configuration_test_{Guid.NewGuid():N}.json");

    private static JsonSerializerOptions MakeOptions() => new()
    {
        WriteIndented = true,
        TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Dispose()
    {
        if (File.Exists(_tempFilePath)) File.Delete(_tempFilePath);
    }

    // ---- builders ---------------------------------------------------------------------------------------------------------

    private static BacktestConditionSideDto SideDto(IndicatorType type = IndicatorType.SMA, int offset = 0) => new() { IndicatorType = type, Offset = offset };

    private static BacktestConditionEntryDto EntryDto(decimal value = 1m, int offset = 0) => new()
    {
        Left = SideDto(offset: offset),
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    };

    private static BacktestConditionNodeDto LeafNode(decimal value = 1m, int offset = 0) => new() { Kind = BacktestConditionNodeKind.Leaf, Comparison = EntryDto(value, offset) };

    private static BacktestConditionNodeDto GroupNode(LogicalOperator op, params BacktestConditionNodeDto[] children)
        => new() { Kind = BacktestConditionNodeKind.Group, Operator = op, Children = children.ToList() };

    private static BacktestConfigurationDto Config(
        int schemaVersion,
        IEnumerable<BacktestConditionEntryDto>? entries = null,
        BacktestConditionTreeDto? tree = null) => new()
    {
        SchemaVersion = schemaVersion,
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
        ConditionEntries = entries?.ToList() ?? new List<BacktestConditionEntryDto>(),
        ConditionTree = tree,
    };

    private static BacktestConfigurationDto V2(BacktestConditionTreeDto tree)
        => Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, tree: tree);

    /// <summary>(A AND B) OR (C AND (D OR E)) in EntryLong, one leaf in ExitShort.</summary>
    private static BacktestConditionTreeDto SampleTree() => new()
    {
        EntryLong = GroupNode(LogicalOperator.Or,
            GroupNode(LogicalOperator.And, LeafNode(1m), LeafNode(2m)),
            GroupNode(LogicalOperator.And, LeafNode(3m), GroupNode(LogicalOperator.Or, LeafNode(4m), LeafNode(5m)))),
        ExitShort = GroupNode(LogicalOperator.And, LeafNode(6m)),
    };

    /// <summary>Structure + values of a persisted tree as one comparable string.</summary>
    private static string Dump(BacktestConditionNodeDto node) => node.Kind == BacktestConditionNodeKind.Leaf
        ? FormattableString.Invariant($"L({node.Comparison!.Left.IndicatorType}|{node.Comparison.Operator}|{node.Comparison.TargetMode}|{node.Comparison.RightNumericValue}|{node.Comparison.Left.Offset})")
        : $"{node.Operator}[{string.Join(",", node.Children.Select(Dump))}]";

    private static string Dump(BacktestConditionTreeDto tree)
        => string.Join(";", new[] { tree.EntryLong, tree.EntryShort, tree.ExitLong, tree.ExitShort, tree.ReverseLong, tree.ReverseShort }.Select(Dump));

    private static BacktestConfigurationLoadResult Validate(BacktestConfigurationDto dto, int? depth = null, int? nodes = null)
        => BacktestConfigurationValidation.Validate(dto, maxConditionTreeDepth: depth, maxConditionTreeNodes: nodes);

    // ---- round trip -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task V2_RoundTripsThroughARealFile_WithExactStructure()
    {
        BacktestConfigurationDto original = V2(SampleTree());

        await AtomicJsonFile.SaveAsync(_tempFilePath, original, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(restored);
        Assert.Equal(BacktestConfigurationDto.ConditionTreeSchemaVersion, restored!.SchemaVersion);
        Assert.Equal(Dump(SampleTree()), Dump(restored.ConditionTree!));
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, Validate(restored).Status);
    }

    [Fact]
    public void DomainTree_ToDtoToTree_IsAnIdentityOnStructure()
    {
        BacktestConditionTree tree = BacktestConditionTreeDtoMapper.ToTree(SampleTree());

        BacktestConditionTreeDto dto = BacktestConditionTreeDtoMapper.ToDto(tree);

        Assert.Equal(Dump(SampleTree()), Dump(dto));
        Assert.Equal(Dump(dto), Dump(BacktestConditionTreeDtoMapper.ToDto(BacktestConditionTreeDtoMapper.ToTree(dto))));
    }

    [Fact]
    public void EntryConversion_RoundTripsEveryField_AndOwnsItsParameters()
    {
        var dto = new BacktestConditionEntryDto
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 7 }, Offset = 2, Frame = TimeFrame.H1 },
            Operator = ComparisonOperator.LessThanOrEqual,
            TargetMode = RightHandTargetMode.Indicator,
            RightNumericValue = 3m,
            Right = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.High },
            LogicalOperator = LogicalOperator.Or,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Short,
        };

        BacktestConditionEntry entry = BacktestConditionTreeDtoMapper.ToEntry(dto);
        BacktestConditionEntryDto back = BacktestConditionTreeDtoMapper.ToEntryDto(entry);

        Assert.Equal(dto.Operator, entry.Operator);
        Assert.Equal(dto.TargetMode, entry.TargetMode);
        Assert.Equal(dto.RightNumericValue, entry.RightNumericValue);
        Assert.Equal(dto.LogicalOperator, entry.LogicalOperator);
        Assert.Equal(dto.Role, entry.Role);
        Assert.Equal(dto.Position, entry.Position);
        Assert.Equal(TimeFrame.H1, entry.Left.Frame);
        Assert.Equal(2, entry.Left.Offset);
        Assert.Equal(PriceType.High, entry.Right!.PriceSource);
        Assert.Equal(dto.Role, back.Role);
        Assert.Equal(dto.Position, back.Position);
        Assert.Equal(7, ((CoreSmaParameter)back.Left.Parameters!).Period);
        Assert.NotSame(dto.Left.Parameters, entry.Left.Parameters);
        Assert.NotSame(entry.Left.Parameters, back.Left.Parameters);
    }

    [Fact]
    public void EntryConversion_NormalizesADisplayAliasToTheCanonicalSeriesName()
    {
        var dto = new BacktestConditionEntryDto { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.BB, OutputName = "Upper" }, RightNumericValue = 1m };

        Assert.Equal("Main", BacktestConditionTreeDtoMapper.ToEntry(dto).Left.OutputName);
        Assert.Equal("Main", BacktestConditionTreeDtoMapper.ToEntryDto(new BacktestConditionEntry { Left = new BacktestConditionSide { IndicatorType = IndicatorType.BB, OutputName = "Upper" } }).Left.OutputName);
    }

    [Fact]
    public void Snapshot_DeepCopiesTheTree()
    {
        BacktestConfigurationDto source = V2(SampleTree());

        BacktestConfigurationDto copy = BacktestConfigurationSnapshot.Create(source);

        Assert.NotSame(source.ConditionTree, copy.ConditionTree);
        Assert.NotSame(source.ConditionTree!.EntryLong, copy.ConditionTree!.EntryLong);
        Assert.NotSame(source.ConditionTree.EntryLong.Children[0], copy.ConditionTree.EntryLong.Children[0]);
        Assert.Equal(Dump(source.ConditionTree), Dump(copy.ConditionTree));
        Assert.Null(BacktestConfigurationSnapshot.Create(Config(BacktestConfigurationDto.CurrentSchemaVersion)).ConditionTree);
    }

    // ---- version 1 (legacy) -----------------------------------------------------------------------------------------------

    [Fact]
    public void V1_LegacyList_ResolvesToTheMigratorTree()
    {
        var entries = new[]
        {
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 1m, Role = BacktestConditionRole.EntryOnly, LogicalOperator = LogicalOperator.Or },
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 2m, Role = BacktestConditionRole.EntryOnly },
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 3m, Role = BacktestConditionRole.ExitOnly },
        };
        BacktestConfigurationDto dto = Config(BacktestConfigurationDto.CurrentSchemaVersion, entries);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, Validate(dto).Status);
        BacktestConditionTree resolved = BacktestConditionTreeDtoMapper.ResolveTree(dto, 500, 8, 64);
        BacktestConditionTree expected = BacktestConditionTreeMigrator.Migrate(BacktestConditionValidator.SnapshotDtos(entries, 500));

        Assert.Equal(Dump(BacktestConditionTreeDtoMapper.ToDto(expected)), Dump(BacktestConditionTreeDtoMapper.ToDto(resolved)));
        Assert.True(resolved.ExitLong.HasLeaf && resolved.ExitShort.HasLeaf);
    }

    [Fact]
    public void V1_AllAndList_MigratesToOneFlatGroup()
    {
        BacktestConditionEntryDto[] entries = Enumerable.Range(1, 10)
            .Select(i => new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = i, Role = BacktestConditionRole.EntryOnly })
            .ToArray();

        BacktestConditionTree tree = BacktestConditionTreeDtoMapper.ResolveTree(Config(BacktestConfigurationDto.CurrentSchemaVersion, entries), 500, 8, 64);

        Assert.Equal(LogicalOperator.And, tree.EntryLong.Operator);
        Assert.Equal(10, tree.EntryLong.Children.Length);
        Assert.All(tree.EntryLong.Children, child => Assert.IsType<BacktestConditionLeaf>(child));
    }

    [Fact]
    public void V1_EntryCopiedIntoSeveralRoots_SharesNoMutableSideStateBetweenRoots()
    {
        var entry = new BacktestConditionEntryDto
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 5 } },
            RightNumericValue = 1m,
            Role = BacktestConditionRole.Both,
        };

        BacktestConditionTree tree = BacktestConditionTreeDtoMapper.ResolveTree(Config(BacktestConfigurationDto.CurrentSchemaVersion, new[] { entry }), 500, 8, 64);

        var copies = new[] { tree.EntryLong, tree.ExitLong, tree.ExitShort }
            .Select(root => ((BacktestConditionLeaf)root.Children.Single()).Comparison.Left)
            .ToArray();
        Assert.Equal(copies.Length, copies.Select(side => side.Parameters).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(copies.Length, copies.Distinct(ReferenceEqualityComparer.Instance).Count());

        ((CoreSmaParameter)copies[0].Parameters!).Period = 99;
        Assert.All(copies.Skip(1), side => Assert.Equal(5, ((CoreSmaParameter)side.Parameters!).Period));
    }

    /// <summary>The decisions a tree takes over 40 bars in every position state, so two trees can be compared by behavior.</summary>
    private static string Behavior(BacktestConditionTree tree)
    {
        ConditionBasedBacktestStrategy strategy = ConditionBasedBacktestStrategy.FromTree(tree);
        var series = new Dictionary<string, System.Collections.Immutable.ImmutableArray<decimal?>>();
        int k = 0;
        foreach (StrategyIndicatorRequest request in strategy.GetRequiredIndicators())
        {
            int shift = k++;
            series[request.Key] = Enumerable.Range(0, 40).Select(bar => (decimal?)((bar + shift) % 5)).ToImmutableArray();
        }
        var set = new IndicatorSeriesSet(series);
        var log = new System.Text.StringBuilder();
        for (int bar = 0; bar < 40; bar++)
        {
            foreach (TradeSide? position in new TradeSide?[] { null, TradeSide.Long, TradeSide.Short })
            {
                var context = new StrategyContext { BarIndex = bar, Bar = SyntheticBars.Bar(bar, 100m, 100m, 100m, 100m), PositionSide = position, Indicators = set };
                log.Append(strategy.Evaluate(context)?.SignalType.ToString() ?? "-").Append('/').Append(strategy.EvaluateExit(context)?.SignalType.ToString() ?? "-").Append(';');
            }
        }
        return log.ToString();
    }

    [Fact]
    public async Task V1_List_ToTree_ToV2Dto_RealFile_ToTree_KeepsStructureAndBehavior()
    {
        var entries = new[]
        {
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 1m, Role = BacktestConditionRole.EntryOnly, LogicalOperator = LogicalOperator.Or },
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 2m, Role = BacktestConditionRole.EntryOnly },
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 3m, Role = BacktestConditionRole.ExitOnly },
            new BacktestConditionEntryDto { Left = SideDto(), RightNumericValue = 0m, Role = BacktestConditionRole.Reversal, Position = TradeSide.Short },
        };
        BacktestConditionTree fromV1 = BacktestConditionTreeDtoMapper.ResolveTree(Config(BacktestConfigurationDto.CurrentSchemaVersion, entries), 500, 8, 64);

        BacktestConfigurationDto v2 = V2(BacktestConditionTreeDtoMapper.ToDto(fromV1));
        await AtomicJsonFile.SaveAsync(_tempFilePath, v2, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());
        BacktestConditionTree fromV2 = BacktestConditionTreeDtoMapper.ResolveTree(restored!, 500, 8, 64);

        Assert.Equal(Dump(BacktestConditionTreeDtoMapper.ToDto(fromV1)), Dump(BacktestConditionTreeDtoMapper.ToDto(fromV2)));
        string behavior = Behavior(fromV1);
        Assert.Equal(behavior, Behavior(fromV2));
        Assert.Contains("LongEntry", behavior);
    }

    [Fact]
    public void V1_LegacyFile_IsNeverRejectedByTheTreeLimits()
    {
        // Alternating connectors nest one level per change: deeper than any sensible limit, but a v1 file predates the limits.
        BacktestConditionEntryDto[] entries = Enumerable.Range(0, 20)
            .Select(i => new BacktestConditionEntryDto
            {
                Left = SideDto(),
                RightNumericValue = i,
                Role = BacktestConditionRole.EntryOnly,
                LogicalOperator = i % 2 == 0 ? LogicalOperator.And : LogicalOperator.Or,
            })
            .ToArray();

        BacktestConfigurationLoadResult result = Validate(Config(BacktestConfigurationDto.CurrentSchemaVersion, entries), depth: 2, nodes: 2);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void V1_WithAConditionTree_IsAnError()
    {
        BacktestConfigurationLoadResult result = Validate(Config(BacktestConfigurationDto.CurrentSchemaVersion, tree: SampleTree()));

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionTree is not allowed", result.ErrorMessage);
    }

    // ---- version 2 rules --------------------------------------------------------------------------------------------------

    [Fact]
    public void V2_WithoutATree_IsAnError()
    {
        BacktestConfigurationLoadResult result = Validate(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionTree is required", result.ErrorMessage);
    }

    [Fact]
    public void V2_WithConditionEntries_IsAnError_SoThereIsOneSourceOfTruth()
    {
        BacktestConfigurationDto dto = Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, new[] { EntryDto() }, SampleTree());

        BacktestConfigurationLoadResult result = Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionEntries must be empty", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnknownSchemaVersion_IsAnErrorNamingBothSupportedVersions(int version)
    {
        BacktestConfigurationLoadResult result = Validate(Config(version));

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("expected 1 or 2", result.ErrorMessage);
    }

    [Fact]
    public void V2_EmptyTree_IsValid_AndResolvesToInactiveRoots()
    {
        BacktestConfigurationDto dto = V2(new BacktestConditionTreeDto());

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, Validate(dto).Status);
        BacktestConditionTree tree = BacktestConditionTreeDtoMapper.ResolveTree(dto, 500, 8, 64);
        Assert.False(tree.EntryLong.HasLeaf);
    }

    // ---- bounds (never clamped) -------------------------------------------------------------------------------------------

    [Fact]
    public void V2_DepthAtTheLimitLoads_OneAboveIsAnError()
    {
        static BacktestConditionNodeDto Chain(int depth)
        {
            BacktestConditionNodeDto node = GroupNode(LogicalOperator.And, LeafNode());
            for (int level = 1; level < depth; level++) node = GroupNode(LogicalOperator.Or, node);
            return node;
        }

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, Validate(V2(new BacktestConditionTreeDto { EntryLong = Chain(4) }), depth: 4).Status);

        BacktestConfigurationLoadResult above = Validate(V2(new BacktestConditionTreeDto { EntryLong = Chain(5) }), depth: 4);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, above.Status);
        Assert.Contains("Backtest:MaxConditionTreeDepth", above.ErrorMessage);
    }

    [Fact]
    public void V2_NodeCountAtTheLimitLoads_OneAboveIsAnError()
    {
        BacktestConditionNodeDto Fan(int leaves) => GroupNode(LogicalOperator.Or, Enumerable.Range(0, leaves).Select(_ => LeafNode()).ToArray());

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, Validate(V2(new BacktestConditionTreeDto { EntryLong = Fan(9) }), nodes: 10).Status);

        BacktestConfigurationLoadResult above = Validate(V2(new BacktestConditionTreeDto { EntryLong = Fan(10) }), nodes: 10);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, above.Status);
        Assert.Contains("Backtest:MaxConditionTreeNodes", above.ErrorMessage);
    }

    // ---- malformed shapes -------------------------------------------------------------------------------------------------

    [Fact]
    public void V2_MalformedNodes_AreLoadErrors()
    {
        var cases = new (string Name, BacktestConditionTreeDto Tree, string Expected)[]
        {
            ("leaf with children", new BacktestConditionTreeDto { EntryLong = GroupNode(LogicalOperator.And, new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Leaf, Comparison = EntryDto(), Children = new() { LeafNode() } }) }, "must not have Children"),
            ("leaf without comparison", new BacktestConditionTreeDto { EntryLong = GroupNode(LogicalOperator.And, new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Leaf }) }, "requires a Comparison"),
            ("group with comparison", new BacktestConditionTreeDto { EntryLong = new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Group, Comparison = EntryDto() } }, "must not carry a Comparison"),
            ("root is a leaf", new BacktestConditionTreeDto { EntryLong = LeafNode() }, "root must be a Group"),
            ("undefined kind", new BacktestConditionTreeDto { EntryLong = GroupNode(LogicalOperator.And, new BacktestConditionNodeDto { Kind = (BacktestConditionNodeKind)9 }) }, "Kind"),
            ("undefined operator", new BacktestConditionTreeDto { EntryLong = new BacktestConditionNodeDto { Operator = (LogicalOperator)9, Children = new() { LeafNode() } } }, "operator is undefined"),
            ("empty nested group", new BacktestConditionTreeDto { EntryLong = GroupNode(LogicalOperator.And, LeafNode(), GroupNode(LogicalOperator.Or)) }, "at least one condition"),
            ("leaf carrying a role", new BacktestConditionTreeDto { EntryLong = GroupNode(LogicalOperator.And, new BacktestConditionNodeDto { Kind = BacktestConditionNodeKind.Leaf, Comparison = new BacktestConditionEntryDto { Left = SideDto(), Role = BacktestConditionRole.Reversal } }) }, "must not carry Role"),
            ("negative offset", new BacktestConditionTreeDto { ExitLong = GroupNode(LogicalOperator.And, LeafNode(offset: -1)) }, "Offset"),
            ("offset above the limit", new BacktestConditionTreeDto { ExitLong = GroupNode(LogicalOperator.And, LeafNode(offset: BacktestConfigurationValidation.DefaultMaxConditionOffset + 1)) }, "Offset"),
        };

        foreach (var (name, tree, expected) in cases)
        {
            BacktestConfigurationLoadResult result = Validate(V2(tree));
            Assert.True(result.Status == BacktestConfigurationLoadStatus.Error, $"{name}: expected an error");
            Assert.True(result.ErrorMessage!.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{name}: message was '{result.ErrorMessage}'");
        }
    }

    [Fact]
    public void V2_NullRootOrNullChild_IsALoadError_NotACrash()
    {
        var nullRoot = new BacktestConditionTreeDto { EntryLong = null! };
        var nullChild = new BacktestConditionTreeDto { EntryLong = new BacktestConditionNodeDto { Children = new() { null! } } };
        var nullChildren = new BacktestConditionTreeDto { EntryLong = new BacktestConditionNodeDto { Children = null! } };

        foreach (BacktestConditionTreeDto tree in new[] { nullRoot, nullChild, nullChildren })
        {
            Assert.Equal(BacktestConfigurationLoadStatus.Error, Validate(V2(tree)).Status);
        }
    }

    [Fact]
    public async Task V2_JsonNestedBeyondTheReaderLimit_FailsToLoadInsteadOfOverflowing()
    {
        BacktestConditionNodeDto node = GroupNode(LogicalOperator.And, LeafNode());
        for (int level = 0; level < 80; level++) node = GroupNode(LogicalOperator.Or, node);
        await AtomicJsonFile.SaveAsync(_tempFilePath, V2(new BacktestConditionTreeDto { EntryLong = node }), new JsonSerializerOptions(MakeOptions()) { MaxDepth = 1000 });

        await Assert.ThrowsAsync<JsonException>(async () => await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions()));
    }

    [Fact]
    public void ExistingV1Validation_MessagesAreUnchanged()
    {
        var negativeOffset = new[] { new BacktestConditionEntryDto { Left = SideDto(offset: -1) } };

        BacktestConfigurationLoadResult result = Validate(Config(BacktestConfigurationDto.CurrentSchemaVersion, negativeOffset));

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionEntries[0].Left.Offset", result.ErrorMessage);
    }

    // ---- migrator: same-operator runs are one group -----------------------------------------------------------------------

    [Fact]
    public void Migrator_ChangeOfOperatorNestsTheRunningResult_AsTheLegacyLeftFoldDoes()
    {
        // A AND B OR C AND D  ==  (((A AND B) OR C) AND D)
        BacktestConditionEntry E(decimal v, LogicalOperator connector) => new()
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            RightNumericValue = v,
            Role = BacktestConditionRole.EntryOnly,
            LogicalOperator = connector,
        };

        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(new[]
        {
            E(1, LogicalOperator.And), E(2, LogicalOperator.Or), E(3, LogicalOperator.And), E(4, LogicalOperator.And),
        });

        Assert.Equal("And[Or[And[L1,L2],L3],L4]", Shape(tree.EntryLong));
    }

    private static string Shape(IBacktestConditionNode node) => node switch
    {
        BacktestConditionLeaf leaf => FormattableString.Invariant($"L{leaf.Comparison.RightNumericValue}"),
        BacktestConditionGroup group => $"{group.Operator}[{string.Join(",", group.Children.Select(Shape))}]",
        _ => "?",
    };
}
