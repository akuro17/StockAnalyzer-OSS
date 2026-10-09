using System;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>What a fine-tune run keeps at the weights of its base model.</summary>
public enum RlPortfolioFreeze
{
    /// <summary>The shared encoder stays bit-for-bit at the base weights; the slot logit head and the critic train.</summary>
    EncoderFrozen = 0,
    /// <summary>Nothing is frozen: every actor weight starts at the base weights and trains.</summary>
    None = 1,
}

/// <summary>A fine-tune run: start the policy from the actor weights of the stored, verified run <paramref name="BaseRunId"/>.</summary>
public sealed record RlPortfolioTransferInit(Guid BaseRunId, RlPortfolioFreeze Freeze);

public static class RlPortfolioFreezes
{
    /// <summary>The name a lineage file and the worker job use for a freeze (the constants of <see cref="RlPortfolioTransferContract"/>).</summary>
    public static string Name(RlPortfolioFreeze freeze) => freeze switch
    {
        RlPortfolioFreeze.EncoderFrozen => RlPortfolioTransferContract.EncoderFrozen,
        RlPortfolioFreeze.None => RlPortfolioTransferContract.NoFreeze,
        _ => throw new ArgumentOutOfRangeException(nameof(freeze), freeze, "Unknown transfer freeze."),
    };
}
