using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Constants of the portfolio-allocation RL program (SA-RL-PORTFOLIO-v1): artifact store, panel snapshot, universe rules, observation and
/// ONNX boundary. Every public constant, the scaler channel names and <see cref="RlContract.ContractHash"/> feed <see cref="ContractHash"/>,
/// so a changed constant invalidates every model that carries the old hash. It never touches RlContract.
/// </summary>
public static class RlPortfolioContract
{
    public const string SpecificationId = "SA-RL-PORTFOLIO-v1";
    public const int ArtifactSchemaVersion = 1;
    public const string ArtifactFolder = "RLPortfolio";
    public const string ArtifactManifest = "portfolio_manifest.json";

    /// <summary>2: the holdout split seed derives from the symbol list alone (version 1 derived it from the captured source hashes). Version 1 panels are rejected.</summary>
    public const int PanelSchemaVersion = 2;
    public const string PanelManifest = "panel.json";
    /// <summary>Folder below a panel snapshot that holds one immutable single-symbol source snapshot per symbol, named by the symbol.</summary>
    public const string PanelSymbolFolder = "symbols";
    /// <summary>Message prefix of every universe and panel rejection.</summary>
    public const string UniverseErrorPrefix = "PortfolioUniverse:";

    /// <summary>Fewest symbols of a portfolio (cash plus one asset is the existing single-symbol problem).</summary>
    public const int MinimumSymbols = 2;
    /// <summary>Fewest symbols when a symbol holdout is used: two to train on and two to hold out.</summary>
    public const int MinimumSymbolsWithHoldout = MinimumTrainSymbols + MinimumHoldoutSymbols;
    public const int MinimumTrainSymbols = 2;
    public const int MinimumHoldoutSymbols = 2;
    /// <summary>A block is usable only when at least this many symbols have a bar on every index of it.</summary>
    public const int MinimumBlockSymbols = 2;
    /// <summary>Per-asset observation channels beyond the market window: current weight and availability mask.</summary>
    public const int AssetAuxiliaryChannelCount = 2;
    /// <summary>Global observation values: cash weight, drawdown, remaining ratio, previous turnover.</summary>
    public const int GlobalFeatureCount = 4;

    /// <summary>Decimal places of a quantized target weight (D15): weights are floored to 1e-4 before any money arithmetic.</summary>
    public const int WeightDecimals = 4;

    public const string TrainPartition = "train";
    public const string ValidationPartition = "validation";
    public const string OosPartition = "oos";

    /// <summary>ONNX tensor names: one float32 observation row in, one float32 logit row (cash first, then one per asset slot) out.</summary>
    public const string OnnxInputName = "observation";
    public const string OnnxOutputName = "logits";
    public const string TargetType = "rl_portfolio";
    public const string MetadataContractHash = "rl_portfolio_contract_hash";
    public const string MetadataTargetType = "target_type";
    public const string MetadataMaxSymbols = "n_max";
    public const string MetadataChannels = "channels";
    public const string MetadataUniverseSha256 = "universe_sha256";
    public const string MetadataCalendarSha256 = "calendar_sha256";
    public const string MetadataEvaluationSpecification = "evaluation_specification_id";
    /// <summary>Feature mode of the portfolio scaler sidecar (scale-free log ratios and log volume, not the composed price features of T15/T16).</summary>
    public const string ScalerFeatureMode = "rl_portfolio_log_ratio";

    /// <summary>
    /// Per-asset market channels in observation order: log of open, high, low and close over the window-end close, then log of one plus volume.
    /// </summary>
    public static ImmutableArray<string> ScalerChannelNames { get; } = ImmutableArray.Create(
        "ln_open_over_close", "ln_high_over_close", "ln_low_over_close", "ln_close_over_close", "ln_one_plus_volume");

    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlPortfolioContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral) values.Add(field.Name, field.GetValue(null)!);
        values.Add(nameof(ScalerChannelNames), ScalerChannelNames.ToArray());
        values.Add("NumericalContractHash", RlContract.ContractHash);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}
