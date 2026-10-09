using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Constants of the observation variant "v2-a" of the portfolio program (FX0/FX1): the five v1 market channels per window bar plus two causal
/// cross-sectional channels. It is a separate contract, so <see cref="RlPortfolioContract"/> and its pinned hash stay untouched and a model of
/// either variant is told apart by its own hash. Every public constant, the channel names and <see cref="RlPortfolioContract.ContractHash"/>
/// feed <see cref="ContractHash"/>, so a changed convention or a changed v1 constant invalidates every v2 model.
/// </summary>
public static class RlPortfolioV2Contract
{
    public const string SpecificationId = "SA-RL-PORTFOLIO-v2-a";

    /// <summary>Feature mode of the v2-a scaler sidecar.</summary>
    public const string ScalerFeatureMode = "rl_portfolio_log_ratio_relative";

    /// <summary>Market channels per window bar: the five of v1 plus the relative one-bar return and the cross-sectional rank.</summary>
    public const int MarketChannelCount = 7;

    /// <summary>
    /// Conventions of the two extra channels (decision D-FX2). The relative return is the one-bar log return minus the mean of the same return over the
    /// slots that are unmasked in the current phase; the rank is (rank - 0.5) / count over those slots, rank 1 = lowest window return, ties share the average
    /// rank; a bar whose previous bar lies before the block has return 0. Only the identifier is hashed: a changed convention must change this text.
    /// </summary>
    public const string FeatureConventions =
        "relative_return=return_minus_mean_over_unmasked;rank=(average_rank-0.5)/count_over_unmasked_lowest_first;first_bar_return_outside_block=0";

    /// <summary>The two channels beyond the five of v1, in observation order.</summary>
    public static ImmutableArray<string> ExtraChannelNames { get; } = ImmutableArray.Create("relative_one_bar_return", "cross_sectional_rank");

    /// <summary>All market channels in observation order: the v1 channels first, so the first five channels keep their meaning.</summary>
    public static ImmutableArray<string> ScalerChannelNames { get; } = RlPortfolioContract.ScalerChannelNames.AddRange(ExtraChannelNames);

    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlPortfolioV2Contract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral) values.Add(field.Name, field.GetValue(null)!);
        values.Add(nameof(ScalerChannelNames), ScalerChannelNames.ToArray());
        values.Add("PortfolioContractHash", RlPortfolioContract.ContractHash);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}
