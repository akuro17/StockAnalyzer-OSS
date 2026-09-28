using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.ConditionTreeTestKit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>User-entered group names (MP-4): model, validation limit, persistence, and the guarantee that a name never changes a run.</summary>
public class BacktestConditionGroupNameTests
{
    private static BacktestConditionGroup Named(string? name, params IBacktestConditionNode[] children)
        => new(LogicalOperator.Or, children.ToImmutableArray(), name);

    private static BacktestConditionTree TreeWith(BacktestConditionGroup nested)
        => Tree(entryLong: And(Leaf(Side()), nested));

    // ---- model ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Moving average  ", "Moving average")]
    public void Name_IsTrimmed_AndEmptyMeansNoName(string? input, string? expected)
    {
        Assert.Equal(expected, Named(input, Leaf(Side())).Name);
    }

    [Fact]
    public void EmptyAnd_HasNoName()
    {
        Assert.Null(BacktestConditionGroup.EmptyAnd.Name);
    }

    // ---- validation limit ----

    [Fact]
    public void SnapshotTree_KeepsTheName_InTheDeepCopy()
    {
        BacktestConditionTree tree = TreeWith(Named("MA", Leaf(Side())));

        BacktestConditionTree copy = BacktestConditionValidator.SnapshotTree(tree, null, null, null, maxNameLength: 10);

        Assert.Equal("MA", ((BacktestConditionGroup)copy.EntryLong.Children[1]).Name);
    }

    [Fact]
    public void SnapshotTree_NameAtExactlyTheLimitPasses_OneBeyondFailsWithThePath()
    {
        const string Ok = "12345";
        BacktestConditionValidator.SnapshotTree(TreeWith(Named(Ok, Leaf(Side()))), null, null, null, Ok.Length);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => BacktestConditionValidator.SnapshotTree(TreeWith(Named(Ok + "6", Leaf(Side()))), null, null, null, Ok.Length));

        Assert.Contains("EntryLong.children[1]", ex.Message);
        Assert.Contains("MaxConditionGroupNameLength", ex.Message);
    }

    [Fact]
    public void SnapshotTree_NoLimit_AcceptsAnyLength()
    {
        BacktestConditionValidator.SnapshotTree(TreeWith(Named(new string('x', 500), Leaf(Side()))), null, null, null);
    }

    [Fact]
    public void SnapshotTree_LimitBelowTheMinimum_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionValidator.SnapshotTree(BacktestConditionTree.Empty, null, null, null, 0));
    }

    [Fact]
    public void SnapshotTree_ARootWithAName_IsRejected()
    {
        var namedRoot = new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(Leaf(Side())), "root");

        ArgumentException ex = Assert.Throws<ArgumentException>(() => BacktestConditionValidator.SnapshotTree(Tree(entryLong: namedRoot), null, null, null));

        Assert.Contains("EntryLong", ex.Message);
    }

    // ---- persistence ----

    private static BacktestConfigurationDto ConfigWith(BacktestConditionTree tree) => new()
    {
        SchemaVersion = BacktestConfigurationDto.ConditionTreeSchemaVersion,
        ConditionTree = BacktestConditionTreeDtoMapper.ToDto(tree),
    };

    [Fact]
    public void Dto_RoundTripsTheName_ThroughToDtoAndToTree()
    {
        BacktestConditionTree tree = TreeWith(Named("MA", Leaf(Side())));

        BacktestConditionTreeDto dto = BacktestConditionTreeDtoMapper.ToDto(tree);
        BacktestConditionTree back = BacktestConditionTreeDtoMapper.ToTree(dto);

        Assert.Equal("MA", dto.EntryLong.Children[1].Name);
        Assert.Null(dto.EntryLong.Name);
        Assert.Null(dto.EntryLong.Children[0].Name);
        Assert.Equal("MA", ((BacktestConditionGroup)back.EntryLong.Children[1]).Name);
    }

    [Fact]
    public void ConfigurationSnapshot_CopiesTheName()
    {
        BacktestConfigurationDto config = ConfigWith(TreeWith(Named("MA", Leaf(Side()))));

        BacktestConfigurationDto copy = BacktestConfigurationSnapshot.Create(config);

        Assert.Equal("MA", copy.ConditionTree!.EntryLong.Children[1].Name);
        Assert.NotSame(config.ConditionTree!.EntryLong.Children[1], copy.ConditionTree.EntryLong.Children[1]);
    }

    [Fact]
    public void ResolveTree_AppliesTheConfiguredNameLimit()
    {
        BacktestConfigurationDto config = ConfigWith(TreeWith(Named("abcdef", Leaf(Side()))));

        Assert.Equal("abcdef", ((BacktestConditionGroup)BacktestConditionTreeDtoMapper.ResolveTree(config, 500, 8, 64, 6).EntryLong.Children[1]).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestConditionTreeDtoMapper.ResolveTree(config, 500, 8, 64, 5));
    }

    [Fact]
    public void Dto_ALeafWithAName_IsALoadError()
    {
        var dto = new BacktestConditionTreeDto
        {
            EntryLong = new BacktestConditionNodeDto
            {
                Kind = BacktestConditionNodeKind.Group,
                Children = new System.Collections.Generic.List<BacktestConditionNodeDto>
                {
                    new()
                    {
                        Kind = BacktestConditionNodeKind.Leaf,
                        Name = "not allowed",
                        Comparison = BacktestConditionTreeDtoMapper.ToEntryDto(Comparison(Side())),
                    },
                },
            },
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => BacktestConditionTreeDtoMapper.ToTree(dto));

        Assert.Contains("Leaf node must not have a Name", ex.Message);
    }

    [Fact]
    public void Dto_AFileWithoutNames_LoadsUnchanged()
    {
        BacktestConditionTree tree = TreeWith(Named(null, Leaf(Side())));

        BacktestConditionTree back = BacktestConditionTreeDtoMapper.ResolveTree(ConfigWith(tree), 500, 8, 64, 32);

        Assert.Null(((BacktestConditionGroup)back.EntryLong.Children[1]).Name);
    }

    // ---- a name never changes a run ----

    [Fact]
    public void Names_DoNotChangeTheRunFingerprintOrTheResult()
    {
        ImmutableArray<StockAnalyzer.Core.Models.CandleData> bars = WavyBars();
        BacktestConditionGroup Build(string? name) => Named(name, CloseLeaf(ComparisonOperator.GreaterThan, 103m), CloseLeaf(ComparisonOperator.LessThan, 108m));
        BacktestConditionTree WithName(string? name) => Tree(
            entryLong: And(CloseLeaf(ComparisonOperator.GreaterThan, 100m), Build(name)),
            exitLong: And(CloseLeaf(ComparisonOperator.LessThan, 99m)));

        string unnamed = FingerprintHex(ConditionBasedBacktestStrategy.FromTree(WithName(null)), bars);
        string named = FingerprintHex(ConditionBasedBacktestStrategy.FromTree(WithName("Range")), bars);

        Assert.Equal(unnamed, named);
    }

    // ---- setting ----

    [Fact]
    public void Setting_HasADefault_ValidatesItsLowerBound_AndTheInterfaceDefaultsFromThePoco()
    {
        var settings = new BacktestSettings();
        Assert.True(settings.MaxConditionGroupNameLength >= BacktestConditionTreeRule.MinBound);
        settings.Validate();

        settings.MaxConditionGroupNameLength = BacktestConditionTreeRule.MinBound - 1;
        Assert.Throws<InvalidOperationException>(settings.Validate);
    }
}
