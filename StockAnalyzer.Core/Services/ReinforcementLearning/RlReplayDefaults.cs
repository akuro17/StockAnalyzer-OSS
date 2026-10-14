using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Defaults of the replay inputs, so a beginner can replay without choosing anything.</summary>
public static class RlReplayDefaults
{
    /// <summary>Partition name of the final out-of-sample blocks (the literal of the episode catalog). It is NOT a member of <see cref="RlContract"/>: every public constant there is part of the pinned contract hash.</summary>
    public const string OosPartition = "oos";

    /// <summary>The first out-of-sample block, otherwise the first block, otherwise null for an empty array.</summary>
    public static RlEpisodeBlock? DefaultBlock(ImmutableArray<RlEpisodeBlock> blocks)
    {
        if (blocks.IsDefault) throw new ArgumentException("The block array is not initialized.", nameof(blocks));
        return blocks.FirstOrDefault(b => string.Equals(b.Partition, OosPartition, StringComparison.Ordinal))
            ?? blocks.FirstOrDefault();
    }
}
