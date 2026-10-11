using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Constants of the approved D11 explanation (11-1=A, 11-3=B, 11-4=B, 11-5=A/A/100/A, 11-6=A, 11-8=A). Python mirrors them
/// (<c>explain.py</c>); <c>Tests/ModelExplanationParity.json</c> keeps both sides identical.
/// </summary>
public static class ModelExplanationContract
{
    public const string SidecarSuffix = ".explain.json";
    public const int SchemaVersion = 1;
    public const string OutputSpace = "probability";
    public const string Method = "shapley_permutation_sampling";
    public const string AvailableStatus = "available";
    public const string RegressionNotAdoptedStatus = "regression_not_adopted";
    public const string NoBackgroundStatus = "no_background";
    public const string NoValidationStatus = "no_validation";
    public const string CallBudgetExceededStatus = "call_budget_exceeded";
    public const string BackgroundPopulation = "train";
    public const string ExplainedPopulation = "inner_validation";
    public const string PdpSemantics = "input_space_constant_offset_not_ohlc_consistent";
    public const string ExplanationType = "grouped_interventional_permutation_shapley";
    public const string RngPolicy = "per_case_seed_sequence";

    /// <summary>Why a case was explained (mirror of <c>explain.REASON_LATEST</c> / <c>REASON_TOP_PREFIX</c>): a newest anchor, or <c>top_&lt;class&gt;</c>.</summary>
    public const string ReasonLatest = "latest";
    public const string ReasonTopPrefix = "top_";

    /// <summary>
    /// The <c>status</c> of the run metrics' <c>explanation</c> block when the optional explanation raised during training (mirror of
    /// <c>run_training.EXPLANATION_FAILED_STATUS</c>); no sidecar exists then.
    /// </summary>
    public const string GenerationFailedStatus = "failed";
    public const string MetricsKey = "explanation";
    public const int Seed = ModelAnalysisContract.Seed;
    public const int BackgroundSize = 100;
    public const int RepresentativePerClass = 2;
    public const int RepresentativeLatest = 2;
    public const int PdpGridPoints = 11;
    public const int TopCount = 5;

    /// <summary>f(x) = base + Σφ holds exactly in the generator; this absorbs only JSON/float round-trip.</summary>
    internal const double IdentityTolerance = 1e-6;
}

/// <summary>Safety bounds of the explanation feature (code contract, never configuration).</summary>
public static class ModelExplanationLimits
{
    /// <summary>Upper bound of the offline Shapley / partial-dependence work of one explanation run.</summary>
    public const int MaxModelCalls = 2_000_000;

    /// <summary>
    /// Generations whose explanation (and, separately, calibration and model analysis) the services keep loaded at once; the least
    /// recently used one is evicted beyond it. It only bounds memory while browsing many generations and is not derived from a
    /// measurement (approved value).
    /// </summary>
    public const int MaxCachedGenerations = 32;
}

public sealed record ExplanationSample(string Symbol, DateTime AnchorDate, int AnchorRow);

/// <summary><see cref="StandardError"/> is the standard error of the permutation mean (absent in older sidecars or with a single permutation).</summary>
public sealed record ExplanationContribution(string Channel, double Value, double? StandardError = null);

/// <summary>One class' decomposition <c>Prediction = BaseValue + Σ Contributions</c> in probability space.</summary>
public sealed record ExplanationOutput(string OutputId, double BaseValue, double Prediction,
    ImmutableArray<ExplanationContribution> Contributions)
{
    /// <summary>
    /// The <paramref name="count"/> largest contributions by absolute value, ties by channel index ascending, sign kept.
    /// Fewer channels than <paramref name="count"/> returns all of them.
    /// </summary>
    public ImmutableArray<ExplanationContribution> Top(int count = ModelExplanationContract.TopCount)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return Contributions.Select((value, index) => (value, index))
            .OrderByDescending(item => Math.Abs(item.value.Value)).ThenBy(item => item.index)
            .Take(count).Select(item => item.value).ToImmutableArray();
    }
}

/// <summary><see cref="StabilityHalfDelta"/>: the largest change of any contribution of the case when only the first half of the permutations is used.</summary>
public sealed record ExplanationRecord(ExplanationSample Sample, ImmutableArray<string> Reasons,
    ImmutableArray<ExplanationOutput> Outputs, double? StabilityHalfDelta = null);

/// <summary>
/// <see cref="OutOfSupportRate"/> (one share per grid point): the part of the shifted cells outside the training population's
/// [min, max] of the channel; absent in older sidecars.
/// </summary>
public sealed record PartialDependenceChannel(string Channel, ImmutableArray<double> Offsets,
    ImmutableDictionary<string, ImmutableArray<double>> Values, ImmutableArray<double> OutOfSupportRate = default);

public sealed record PartialDependence(int GridPoints, ImmutableArray<double> QuantileLevels, string Semantics,
    ImmutableArray<PartialDependenceChannel> Channels);

public sealed record ExplanationPopulation(string Background, string Explained, int ValidationSampleCount);

/// <summary>Sampling reliability over every case, output and channel of one explanation; informative only, never a pass/fail gate.</summary>
public sealed record ExplanationStability(int PermutationsUsed, double MaxStandardError, double MaxHalfDelta);

/// <summary>
/// Immutable offline explanation bound to exactly one ONNX. A status other than <c>available</c> carries no records: the
/// explanation is optional and its absence never affects prediction.
/// </summary>
public sealed record ModelExplanation(int SchemaVersion, string ModelSha256, string DataRevision,
    string FeatureContractHash, string TargetContractHash, string Timeframe, int WindowSize,
    ImmutableArray<string> BaseChannels, ImmutableArray<string> OrderedChannels, string Status,
    string? OutputSpace = null, string? Method = null, int? Seed = null, int? Permutations = null,
    int? BackgroundSize = null, int? ModelCalls = null, ImmutableArray<string> Classes = default,
    ExplanationPopulation? Population = null, ImmutableArray<ExplanationRecord> Records = default,
    PartialDependence? Pdp = null, string? ExplanationType = null, string? RngPolicy = null,
    ExplanationStability? Stability = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] TargetContractKeys =
    {
        PredictionModelMetadata.ClassOrderKey, PredictionModelMetadata.TargetFormulaKey, PredictionModelMetadata.NeutralThresholdKey,
        PredictionModelMetadata.HorizonKey, PredictionModelMetadata.TargetTypeKey,
    };

    public static string SidecarPath(string modelPath) => modelPath + ModelExplanationContract.SidecarSuffix;

    public bool IsAvailable => Status == ModelExplanationContract.AvailableStatus;

    /// <summary>SHA-256 of the canonical JSON of the target-defining metadata (mirror of <c>explain.target_contract_hash</c>).</summary>
    public static string ComputeTargetContractHash(IReadOnlyDictionary<string, string> metadata)
    {
        var builder = new StringBuilder("{");
        for (int i = 0; i < TargetContractKeys.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(JsonSerializer.Serialize(TargetContractKeys[i])).Append(':')
                .Append(JsonSerializer.Serialize(metadata.GetValueOrDefault(TargetContractKeys[i]) ?? ""));
        }
        builder.Append('}');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>Returns <see langword="null"/> when no explanation was generated; throws when one exists but is invalid.</summary>
    public static ModelExplanation? Load(string modelPath, IReadOnlyDictionary<string, string> metadata, string expectedModelHash)
    {
        var path = SidecarPath(modelPath);
        if (!File.Exists(path)) return null;
        ModelExplanation explanation;
        try
        {
            explanation = JsonSerializer.Deserialize<ModelExplanation>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Model explanation is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Model explanation is not valid JSON.", ex); }
        explanation.Validate(metadata, expectedModelHash);
        return explanation;
    }

    public void Validate(IReadOnlyDictionary<string, string> metadata, string modelHash)
    {
        static bool Hash(string? text) => ContentHash.IsLowerHex64(text);
        static void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw new InvalidDataException("Model explanation contract is invalid."); }
        static bool Unit(double v) => double.IsFinite(v) && v is >= 0 and <= 1;
        static bool NonNegative(double v) => double.IsFinite(v) && v >= 0;
        Require(SchemaVersion == ModelExplanationContract.SchemaVersion && Hash(ModelSha256) && ModelSha256 == modelHash
            && Hash(DataRevision) && FeatureContractHash == FixedScaler.ComputeContractHash(metadata)
            && TargetContractHash == ComputeTargetContractHash(metadata)
            && Timeframe == metadata.GetValueOrDefault(PredictionModelMetadata.OutputTimeframeKey)
            && WindowSize > 0 && !BaseChannels.IsDefaultOrEmpty && !OrderedChannels.IsDefaultOrEmpty
            && BaseChannels.All(s => !string.IsNullOrWhiteSpace(s)) && BaseChannels.Distinct(StringComparer.Ordinal).Count() == BaseChannels.Length);
        if (Status != ModelExplanationContract.AvailableStatus)
        {
            Require(Status is ModelExplanationContract.RegressionNotAdoptedStatus or ModelExplanationContract.NoBackgroundStatus
                    or ModelExplanationContract.NoValidationStatus or ModelExplanationContract.CallBudgetExceededStatus
                && Records.IsDefaultOrEmpty && Pdp is null);
            return;
        }
        Require(OutputSpace == ModelExplanationContract.OutputSpace && Method == ModelExplanationContract.Method
            && Seed == ModelExplanationContract.Seed && Permutations is > 0 && Permutations <= ModelExplanationContract.BackgroundSize
            && BackgroundSize == Permutations && ModelCalls is > 0 && ModelCalls <= ModelExplanationLimits.MaxModelCalls
            && !Classes.IsDefaultOrEmpty && Classes.All(s => !string.IsNullOrWhiteSpace(s))
            && string.Join(",", Classes) == metadata.GetValueOrDefault(PredictionModelMetadata.ClassOrderKey)
            && Population is { Background: ModelExplanationContract.BackgroundPopulation, Explained: ModelExplanationContract.ExplainedPopulation }
            && Population.ValidationSampleCount > 0 && !Records.IsDefaultOrEmpty && Pdp is not null);
        Require(Records.Length <= Classes.Length * ModelExplanationContract.RepresentativePerClass + ModelExplanationContract.RepresentativeLatest);
        Require((ExplanationType is null || ExplanationType == ModelExplanationContract.ExplanationType)
            && (RngPolicy is null || RngPolicy == ModelExplanationContract.RngPolicy)
            && (Stability is null || (Stability.PermutationsUsed > 0 && Stability.PermutationsUsed <= Permutations
                && NonNegative(Stability.MaxStandardError) && NonNegative(Stability.MaxHalfDelta))));
        foreach (var record in Records)
        {
            Require(record is { Sample: not null } && !string.IsNullOrWhiteSpace(record.Sample.Symbol) && record.Sample.AnchorRow >= 0
                && (record.StabilityHalfDelta is not { } halfDelta || NonNegative(halfDelta))
                && !record.Reasons.IsDefaultOrEmpty && !record.Outputs.IsDefaultOrEmpty
                && record.Outputs.Select(o => o.OutputId).SequenceEqual(Classes, StringComparer.Ordinal));
            foreach (var output in record.Outputs)
            {
                Require(Unit(output.BaseValue) && Unit(output.Prediction) && !output.Contributions.IsDefaultOrEmpty
                    && output.Contributions.Select(c => c.Channel).SequenceEqual(BaseChannels, StringComparer.Ordinal)
                    && output.Contributions.All(c => double.IsFinite(c.Value) && (c.StandardError is not { } error || NonNegative(error))));
                Require(Math.Abs(output.BaseValue + output.Contributions.Sum(c => c.Value) - output.Prediction)
                    <= ModelExplanationContract.IdentityTolerance);
            }
        }
        Require(Pdp.GridPoints == ModelExplanationContract.PdpGridPoints && Pdp.Semantics == ModelExplanationContract.PdpSemantics
            && Pdp.QuantileLevels.Length == Pdp.GridPoints && Pdp.QuantileLevels.All(double.IsFinite)
            && !Pdp.Channels.IsDefaultOrEmpty
            && Pdp.Channels.Select(c => c.Channel).SequenceEqual(BaseChannels, StringComparer.Ordinal));
        foreach (var channel in Pdp.Channels)
        {
            Require(channel.Offsets.Length == Pdp.GridPoints && channel.Offsets.All(double.IsFinite)
                && channel.Values is not null && channel.Values.Keys.OrderBy(k => k, StringComparer.Ordinal)
                    .SequenceEqual(Classes.OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal)
                && channel.Values.Values.All(v => v.Length == Pdp.GridPoints && v.All(Unit))
                && (channel.OutOfSupportRate.IsDefault || (channel.OutOfSupportRate.Length == Pdp.GridPoints && channel.OutOfSupportRate.All(Unit))));
        }
    }
}
