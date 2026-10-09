using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Constants of the transfer-learning program (SA-RL-PORTFOLIO-TRANSFER-v1). It is a class of its own: <see cref="RlPortfolioContract"/> reflects every
/// public constant into its pinned hash and must never receive these. Every public constant feeds <see cref="ContractHash"/> (the same pattern as
/// <see cref="RlPortfolioEvaluationContract"/>), which the lineage file carries, so a changed constant invalidates the evidence written under the old one.
/// </summary>
public static class RlPortfolioTransferContract
{
    public const string SpecificationId = "SA-RL-PORTFOLIO-TRANSFER-v1";
    public const int SchemaVersion = 1;

    /// <summary>Lineage file inside a run folder that used a stored model.</summary>
    public const string LineageFile = "transfer.json";
    /// <summary>The base weights copied into a fine-tune run folder (written by the fine-tune runner).</summary>
    public const string InitWeightsFile = "init_weights.pt";
    /// <summary>Message prefix of every transfer rejection.</summary>
    public const string ErrorPrefix = "PortfolioTransfer:";

    /// <summary>The run trained from the weights of a stored base model.</summary>
    public const string FineTuneKind = "fine_tune";
    /// <summary>The run only evaluated a stored base model on a new panel (no training, no model files).</summary>
    public const string ZeroShotKind = "zero_shot_evaluation";

    /// <summary>Fine-tune freezing: the shared encoder stays at the base weights, only the heads are trained.</summary>
    public const string EncoderFrozen = "encoder";
    /// <summary>Fine-tune freezing: nothing is frozen.</summary>
    public const string NoFreeze = "none";

    public const string Statement = "Descriptive evidence only: a transferred policy is not shown to beat training from scratch unless the paired comparison says so, segments are not independent evidence, and no investment efficacy is established.";

    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlPortfolioTransferContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral) values.Add(field.Name, field.GetValue(null)!);
        values.Add("NumericalContractHash", RlPortfolioContract.ContractHash);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}
