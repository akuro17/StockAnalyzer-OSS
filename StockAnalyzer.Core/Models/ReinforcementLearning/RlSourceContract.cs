using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.IO;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Adopted local stored-price ingress and evidence policy; no acquisition is performed.</summary>
public static class RlSourceContract
{
    public const int SchemaVersion = 2;
    public const string Provider = "local_parquet";
    public const string ProviderVersion = "2";
    public const string EvidencePolicy = "stored_price_unverified";
    public const string Adjustment = "stored_values_unverified";
    public const string LocalProvenance = "local_file_bytes_verified";
    public const string Unverified = "unverified";
    public const string FinalityPolicy = "exclude_current_exchange_date";
    /// <summary>Single owner of the insufficient-history rejection text prefix, so producers and the UI classify it identically.</summary>
    public const string InsufficientDataMarker = "InsufficientData";
    public static string InsufficientData(string detail) => $"{InsufficientDataMarker}: {detail}";
    public static bool IsInsufficientData(System.Exception error)
        => error is InvalidDataException && error.Message.StartsWith(InsufficientDataMarker, System.StringComparison.Ordinal);
    public const string DateLabelSemantics = "UTC-kind midnight is an exchange-local date label, not an execution event or PIT evidence.";

    // The existing public name is retained for compatibility; these describe local ingress, not network acquisition.
    public static IReadOnlyDictionary<string, object> Acquisition { get; } =
        new Dictionary<string, object>
        {
            ["timeframe"] = nameof(TimeFrame.D1), ["source_folder"] = MarketDataSettings.DefaultDailyDataPath,
            ["network"] = false, ["preserve_stored_order"] = true, ["repair"] = false,
            ["impute"] = false, ["adjust_prices"] = false, ["evidence_policy"] = EvidencePolicy,
        }.ToImmutableDictionary();

    public static void Validate(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values is null || values.Count != Acquisition.Count)
            throw new InvalidDataException("Explicit local ingress parameters are required.");
        foreach (var expected in Acquisition)
        {
            if (!values.TryGetValue(expected.Key, out var actual)
                || actual.GetRawText() != JsonSerializer.Serialize(expected.Value))
                throw new InvalidDataException($"Local ingress parameter {expected.Key} mismatches the RL contract.");
        }
    }
}
