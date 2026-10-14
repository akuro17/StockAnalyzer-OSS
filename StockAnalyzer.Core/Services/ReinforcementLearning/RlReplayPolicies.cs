using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Single owner of the policy ids a stored generation can be replayed with (the set <see cref="QValueService"/> accepts).</summary>
public static class RlReplayPolicies
{
    /// <summary>Order: the generation's own target, then Hold, BuyHold, Sma, Random, then the paired baseline for continuous generations only.</summary>
    public static IReadOnlyList<string> For(RlGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var policies = new List<string>
        {
            generation.Target, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId
        };
        if (generation.Target == RlContinuousContract.Target) policies.Add(RlContinuousContract.PairedBaselineId);
        return policies;
    }
}
