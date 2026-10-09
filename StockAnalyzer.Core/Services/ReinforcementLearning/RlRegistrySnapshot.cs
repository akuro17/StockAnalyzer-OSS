using System.Collections.Immutable;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Explicit optional operations; absence of data does not imply lack of support.</summary>
public sealed record RlRegistryCapabilities(bool Storage, bool Recovery, bool Checkpoints)
{
    public static RlRegistryCapabilities None { get; } = new(false, false, false);
}

/// <summary>Owned generation/active-state snapshot captured within one registry transaction.</summary>
public sealed record RlRegistrySnapshot(ImmutableArray<RlGeneration> Generations,
    string? DirectionActiveId, string? ContinuousActiveId, bool DirectionUnavailable, bool ContinuousUnavailable)
{
    public RlRegistryCapabilities Capabilities { get; init; } = RlRegistryCapabilities.None;
}
