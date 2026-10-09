using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Short-history evaluation of the discrete T15 target. It owns its identifiers and never touches the all-block (R06)
/// contract or any numerical contract hash; the catalog rule is the R06 rule (one complete block per partition at minimum, every complete block kept).
/// </summary>
public static class RlShortHistoryEvaluationContract
{
    public const string SpecificationId = "SA-RL-SHORT-HISTORY-v1";
    public const string MetadataKey = "short_history_specification_id";
    public const string ContractKey = "short_history_contract";
    public const string ReportKey = "short_history_evaluation";
    public const int SchemaVersion = 1;
    public const string Statement = "Descriptive evidence only: short-history segments are not statistically independent evidence and no investment efficacy is established.";

    /// <summary>False for an absent id, true for the supported id; any other id is rejected like an unsupported all-block id.</summary>
    public static bool IsShortHistory(string? specification)
        => specification is null ? false : specification == SpecificationId ? true
            : throw new System.IO.InvalidDataException("Unsupported short-history RL evaluation specification.");

    public static string? Read(JsonElement element)
    {
        string? specification = element.TryGetProperty(MetadataKey, out var value) ? value.GetString() : null;
        if (element.TryGetProperty(MetadataKey, out _) && specification is null)
            throw new System.IO.InvalidDataException("An explicit short-history evaluation specification cannot be null.");
        _ = IsShortHistory(specification);
        return specification;
    }

    /// <summary>Named constants of this contract only; hashed so the report can prove which contract produced it.</summary>
    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlShortHistoryEvaluationContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral) values.Add(field.Name, field.GetValue(null)!);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}

public sealed record RlShortHistoryBlockCounts(int Train, int Validation, int Oos);

public sealed record RlShortHistoryEvaluationReport(string SpecificationId, int SchemaVersion, string ContractHash, string SourceRevision,
    RlShortHistoryBlockCounts BlockCount, RlReturnDistribution Validation, RlReturnDistribution? Oos,
    RlEvaluationResourceEstimate ResourceEstimate, string PricePit, string InvestmentEfficacy, string Statement);
