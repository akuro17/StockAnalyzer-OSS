#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlReplayPoliciesTests
{
    private static RlGeneration Generation(string target) => new("model", "AAA", "rev", "hash", "eval", 0m, 0m,
        new Dictionary<string, string>(), DateTimeOffset.UnixEpoch) { Target = target };

    [Fact]
    public void DiscreteGenerationOffersTargetAndFourBaselinesInOrder()
    {
        var policies = RlReplayPolicies.For(Generation(RlFiles.Target));
        Assert.Equal(new[] { RlFiles.Target, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId }, policies);
    }

    [Fact]
    public void ContinuousGenerationAddsThePairedBaselineLast()
    {
        var policies = RlReplayPolicies.For(Generation(RlContinuousContract.Target));
        Assert.Equal(RlContinuousContract.Target, policies[0]);
        Assert.Equal(RlContinuousContract.PairedBaselineId, policies[^1]);
        Assert.Equal(6, policies.Count);
        Assert.Equal(policies.Count, policies.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NullGenerationIsRejected() => Assert.Throws<ArgumentNullException>(() => RlReplayPolicies.For(null!));
}

public sealed class RlReplayDefaultsTests
{
    private static RlEpisodeBlock Block(string id, string partition) => new(id, partition, 0, 1);

    [Fact]
    public void FirstOutOfSampleBlockIsPreferred()
    {
        var blocks = ImmutableArray.Create(Block("v0", "validation"), Block("o0", RlReplayDefaults.OosPartition), Block("o1", RlReplayDefaults.OosPartition));
        Assert.Equal("o0", RlReplayDefaults.DefaultBlock(blocks)!.Id);
    }

    [Fact]
    public void WithoutOutOfSampleBlocksTheFirstBlockIsUsed()
    {
        var blocks = ImmutableArray.Create(Block("v0", "validation"), Block("v1", "validation"));
        Assert.Equal("v0", RlReplayDefaults.DefaultBlock(blocks)!.Id);
    }

    [Fact]
    public void EmptyBlocksGiveNull() => Assert.Null(RlReplayDefaults.DefaultBlock(ImmutableArray<RlEpisodeBlock>.Empty));

    [Fact]
    public void UninitializedArrayIsRejected() => Assert.Throws<ArgumentException>(() => RlReplayDefaults.DefaultBlock(default));

    [Fact]
    public void OosPartitionMatchesTheCatalogLiteral() => Assert.Equal("oos", RlReplayDefaults.OosPartition);
}
