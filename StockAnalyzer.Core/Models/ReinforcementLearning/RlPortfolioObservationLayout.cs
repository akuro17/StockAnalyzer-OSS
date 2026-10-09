using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// The observation variant of a portfolio model: which market channels one window bar carries, and the contract hash and scaler feature mode that
/// identify it. This is the single owner of the per-slot size, so the resource arithmetic, the scaler sidecar and the ONNX boundary read it from here
/// instead of from the five-channel <see cref="RlContract"/> (which stays the contract of the v1 variant and of the single-symbol targets).
/// </summary>
public sealed record RlPortfolioObservationLayout
{
    public string SpecificationId { get; }
    public string ContractHash { get; }
    public string ScalerFeatureMode { get; }
    public ImmutableArray<string> ChannelNames { get; }

    private RlPortfolioObservationLayout(string specificationId, string contractHash, string scalerFeatureMode, ImmutableArray<string> channelNames)
        => (SpecificationId, ContractHash, ScalerFeatureMode, ChannelNames) = (specificationId, contractHash, scalerFeatureMode, channelNames);

    /// <summary>Market channels per window bar.</summary>
    public int MarketChannelCount => ChannelNames.Length;

    /// <summary>Values of one asset slot: the market window and the two auxiliary channels.</summary>
    public int SlotLength => checked(RlContract.WindowLength * MarketChannelCount + RlPortfolioContract.AssetAuxiliaryChannelCount);

    /// <summary>Observation length for <paramref name="symbols"/> asset slots: the slots, then the global values.</summary>
    public long ObservationLength(int symbols)
    {
        if (symbols < 1) throw new ArgumentOutOfRangeException(nameof(symbols));
        return checked(symbols * (long)SlotLength + RlPortfolioContract.GlobalFeatureCount);
    }

    /// <summary>The variant of every model made so far (SA-RL-PORTFOLIO-v1), built from the pinned contracts.</summary>
    public static RlPortfolioObservationLayout V1 { get; } = new(RlPortfolioContract.SpecificationId, RlPortfolioContract.ContractHash,
        RlPortfolioContract.ScalerFeatureMode, RlPortfolioContract.ScalerChannelNames);

    /// <summary>The v1 channels plus the two causal cross-sectional channels.</summary>
    public static RlPortfolioObservationLayout V2A { get; } = new(RlPortfolioV2Contract.SpecificationId, RlPortfolioV2Contract.ContractHash,
        RlPortfolioV2Contract.ScalerFeatureMode, RlPortfolioV2Contract.ScalerChannelNames);

    public static IReadOnlyList<RlPortfolioObservationLayout> All { get; } = new[] { V1, V2A };

    /// <summary>The variant a model or scaler names by its contract hash; false for a missing or unknown hash.</summary>
    public static bool TryFromContractHash(string? contractHash, [NotNullWhen(true)] out RlPortfolioObservationLayout? layout)
    {
        layout = contractHash is null ? null : All.FirstOrDefault(l => string.Equals(l.ContractHash, contractHash, StringComparison.Ordinal));
        return layout is not null;
    }
}
