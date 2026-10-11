using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public enum EnsembleValidationFailure
{
    MissingGeneration,
    IncompatibleContract,
    InvalidAccuracyEvidence,
}

internal static class EnsembleValidationErrors
{
    private static readonly object ReasonKey = new();

    internal static EnsembleValidationFailure? GetReason(Exception exception) =>
        exception.Data[ReasonKey] is EnsembleValidationFailure reason ? reason : null;

    internal static InvalidOperationException Create(string message, EnsembleValidationFailure reason)
    {
        var exception = new InvalidOperationException(message);
        exception.Data[ReasonKey] = reason;
        return exception;
    }
}

/// <summary>
/// What decides whether classification generations can be members of one ensemble: the time frame, the horizon, the class label SET and the two target
/// definition values stored in the ONNX metadata. Generations with an equal <see cref="Key"/> can be mixed; the window, the architecture and the
/// evaluation revision are not part of the rule (every member is preprocessed under its own contract).
/// </summary>
public sealed record EnsembleCompatibility(string Key, string Timeframe, int Horizon, string Labels, string NeutralThreshold, string PriceAdjustment);

public sealed record EnsembleMember(string ModelId, double Weight);

public sealed record EnsembleSpec(IReadOnlyList<EnsembleMember> Members)
{
    public const int MaximumMembers = TrainingResourceOverrides.MaximumEnsembleMembers;
    public const double SumTolerance = 1e-4;

    public void Validate()
    {
        if (Members is null || Members.Count is < 1 or > MaximumMembers)
            throw new InvalidOperationException($"An ensemble requires 1 to {MaximumMembers} members.");
        if (Members.Any(m => m is null || string.IsNullOrWhiteSpace(m.ModelId)
            || !double.IsFinite(m.Weight) || m.Weight < 0)
            || Members.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() != Members.Count)
            throw new InvalidOperationException("Ensemble IDs must be distinct and weights finite and nonnegative.");
        double sum = Members.Sum(m => m.Weight);
        if (!double.IsFinite(sum) || sum <= 0 || Math.Abs(sum - 1) > SumTolerance)
            throw new InvalidOperationException("Ensemble weights must sum to one within tolerance.");
    }

    /// <summary>Prefix of <see cref="Fingerprint"/>, so an ensemble identity can never be mistaken for a single generation id.</summary>
    public const string FingerprintPrefix = "ensemble:";

    /// <summary>
    /// The identity of this ensemble: SHA-256 over the ordered (generation id, weight) list. Two specs with the same members, weights and order have
    /// the same fingerprint; any change of a member or a weight changes it.
    /// </summary>
    public string Fingerprint()
    {
        var text = string.Join("|", Members.Select(m => m.ModelId + "=" + m.Weight.ToString("R", CultureInfo.InvariantCulture)));
        return FingerprintPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static EnsembleSpec Equal(IEnumerable<string> modelIds)
    {
        var ids = modelIds.ToArray();
        if (ids.Length is < 1 or > MaximumMembers || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("Select distinct model generations.");
        return new EnsembleSpec(Array.AsReadOnly(ids.Select(id => new EnsembleMember(id, 1.0 / ids.Length)).ToArray()));
    }

    public static EnsembleSpec AccuracyProportional(IEnumerable<ModelGenerationManifest> manifests,
        Func<ModelGenerationManifest, double> readFinalOuterAccuracy)
    {
        var models = manifests.ToArray();
        if (models.Length is < 1 or > MaximumMembers
            || models.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() != models.Length
            || models.Any(m => m.TargetType != PredictionModelMetadata.TargetTypeClassification
                || m.EvaluationRevision != models[0].EvaluationRevision))
            throw EnsembleValidationErrors.Create(
                "Accuracy preset requires distinct classification generations at the same evaluation revision.",
                EnsembleValidationFailure.IncompatibleContract);
        var accuracies = models.Select(readFinalOuterAccuracy).ToArray();
        if (accuracies.Any(x => !double.IsFinite(x) || x < 0))
            throw EnsembleValidationErrors.Create("Final-outer accuracy must be finite and nonnegative.",
                EnsembleValidationFailure.InvalidAccuracyEvidence);
        double total = accuracies.Sum();
        if (!double.IsFinite(total) || total <= 0)
            throw EnsembleValidationErrors.Create("Final-outer accuracies must have a positive total.",
                EnsembleValidationFailure.InvalidAccuracyEvidence);
        return new EnsembleSpec(Array.AsReadOnly(models.Select((m, i) =>
            new EnsembleMember(m.ModelId, accuracies[i] / total)).ToArray()));
    }
}

public interface IEnsembleSettingsManager
{
    EnsembleSpec? Current { get; }
    Task LoadAsync();
    Task SaveAsync(EnsembleSpec? spec);
}

/// <summary>Atomically persists the selected generation IDs and unit-fraction weights.</summary>
public sealed class EnsembleSettingsManager : IEnsembleSettingsManager
{
    public static EnsembleValidationFailure? GetValidationFailure(Exception exception) =>
        EnsembleValidationErrors.GetReason(exception);

    // A registered generation is immutable and content-addressed, so what its ONNX metadata says never changes: read it once per process (least recently used out).
    private static readonly BoundedLruCache<string, EnsembleCompatibility> CompatibilityCache = new(EnsembleLimits.MaxCachedCompatibilities, StringComparer.Ordinal);

    /// <summary>Entries currently held by the compatibility cache (for the test of its bound).</summary>
    internal static int CompatibilityCacheCount => CompatibilityCache.Count;

    /// <summary>
    /// The compatibility of <paramref name="manifest"/> for ensembles, or null when it can never be an ensemble member (not a classification model,
    /// invalid class labels) or cannot be read right now (the generation is unavailable). This is the single definition
    /// <see cref="ValidateGenerations"/> compares too, so "same group" and "can be saved together" cannot drift apart.
    /// </summary>
    public static EnsembleCompatibility? Describe(ModelGenerationManifest manifest, IModelGenerationRegistry registry)
    {
        if (manifest.TargetType != PredictionModelMetadata.TargetTypeClassification
            || !PredictionModelMetadata.HasValidClassOrder(manifest.ClassOrder)) return null;
        if (CompatibilityCache.TryGet(manifest.ModelId, out var cached)) return cached;
        try
        {
            var compatibility = ReadCompatibility(manifest, registry);
            CompatibilityCache.TryAdd(manifest.ModelId, compatibility);
            return compatibility;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or OnnxRuntimeException)
        {
            return null;
        }
    }

    private static EnsembleCompatibility ReadCompatibility(ModelGenerationManifest manifest, IModelGenerationRegistry registry)
    {
        using var lease = registry.Acquire(manifest.ModelId)
            ?? throw EnsembleValidationErrors.Create($"Generation '{manifest.ModelId}' is unavailable.",
                EnsembleValidationFailure.MissingGeneration);
        using var session = new InferenceSession(lease.ModelPath);
        return BuildCompatibility(manifest, session.ModelMetadata.CustomMetadataMap);
    }

    /// <summary>
    /// The pure part of <see cref="ReadCompatibility"/>. A generation whose metadata lacks <see cref="PredictionModelMetadata.NeutralThresholdKey"/> or
    /// <see cref="PredictionModelMetadata.PriceAdjustmentKey"/> cannot be shown to share the label rule and the price basis of the other members, so it is
    /// not an ensemble candidate (the Python trainers always write both).
    /// </summary>
    internal static EnsembleCompatibility BuildCompatibility(ModelGenerationManifest manifest, IReadOnlyDictionary<string, string> metadata)
    {
        if (manifest.TargetType != PredictionModelMetadata.TargetTypeClassification)
            throw EnsembleValidationErrors.Create("Only classification generations can be ensembled.",
                EnsembleValidationFailure.IncompatibleContract);
        if (!PredictionModelMetadata.HasValidClassOrder(manifest.ClassOrder))
            throw EnsembleValidationErrors.Create("Generation class labels are invalid.",
                EnsembleValidationFailure.IncompatibleContract);
        string neutral = RequiredMetadata(manifest, metadata, PredictionModelMetadata.NeutralThresholdKey);
        string price = RequiredMetadata(manifest, metadata, PredictionModelMetadata.PriceAdjustmentKey);
        string labels = string.Join(",", manifest.ClassOrder.Split(',').OrderBy(label => label, StringComparer.Ordinal));
        string key = string.Join("\n", manifest.Timeframe, manifest.Horizon.ToString(CultureInfo.InvariantCulture), labels, neutral, price);
        return new EnsembleCompatibility(key, manifest.Timeframe, manifest.Horizon, labels, neutral, price);
    }

    private static string RequiredMetadata(ModelGenerationManifest manifest, IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw EnsembleValidationErrors.Create($"Generation '{manifest.ModelId}' has no '{key}' metadata.", EnsembleValidationFailure.IncompatibleContract);

    private readonly string _path;
    private readonly IModelGenerationRegistry _registry;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    public EnsembleSpec? Current { get; private set; }

    public EnsembleSettingsManager(IModelGenerationRegistry registry)
        : this(registry, PathDiscovery.ResolveConfigPath("user_ensemble_settings.json")) { }

    public EnsembleSettingsManager(IModelGenerationRegistry registry, string path)
    {
        _registry = registry;
        _path = path;
    }

    public async Task LoadAsync()
    {
        await _mutation.WaitAsync();
        try
        {
            if (!File.Exists(_path)) { Current = null; return; }
            var spec = await AtomicJsonFile.LoadAsync<EnsembleSpec>(_path);
            if (spec is null) { Current = null; return; }
            var snapshot = Freeze(spec);
            await Task.Run(() => ValidateGenerations(snapshot, _registry));
            Current = snapshot;
        }
        finally { _mutation.Release(); }
    }

    public async Task SaveAsync(EnsembleSpec? spec)
    {
        var snapshot = spec is null ? null : Freeze(spec);
        await _mutation.WaitAsync();
        try
        {
            if (snapshot is not null) await Task.Run(() => ValidateGenerations(snapshot, _registry));
            await AtomicJsonFile.SaveAsync(_path, snapshot);
            Current = snapshot;
        }
        finally { _mutation.Release(); }
    }

    private static EnsembleSpec Freeze(EnsembleSpec spec) =>
        new(Array.AsReadOnly(spec.Members?.ToArray() ?? Array.Empty<EnsembleMember>()));

    public static void ValidateGenerations(EnsembleSpec spec, IModelGenerationRegistry registry)
    {
        spec.Validate();
        EnsembleCompatibility? first = null;
        foreach (var member in spec.Members)
        {
            var manifest = registry.GetManifest(member.ModelId)
                ?? throw EnsembleValidationErrors.Create($"Generation '{member.ModelId}' is unavailable.",
                    EnsembleValidationFailure.MissingGeneration);
            var compatibility = ReadCompatibility(manifest, registry);
            if (first is null) first = compatibility;
            else if (compatibility.Key != first.Key)
                throw EnsembleValidationErrors.Create("Ensemble generations have incompatible target, timeframe, horizon, or class labels.",
                    EnsembleValidationFailure.IncompatibleContract);
        }
    }

    public static double ReadFinalOuterAccuracy(ModelGenerationManifest manifest, IModelGenerationRegistry registry)
    {
        using var lease = registry.Acquire(manifest.ModelId)
            ?? throw EnsembleValidationErrors.Create("Generation is unavailable.", EnsembleValidationFailure.MissingGeneration);
        using var doc = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + ".metrics.json"));
        var report = doc.RootElement;
        if (report.GetProperty("evaluation_status").GetString() != "independent_outer"
            || report.GetProperty("evaluation_revision").GetString() != manifest.EvaluationRevision
            || report.GetProperty("target_type").GetString() != PredictionModelMetadata.TargetTypeClassification)
            throw new InvalidDataException("Independent final-outer evaluation is required.");
        var rows = report.GetProperty("outer_folds");
        var final = rows[rows.GetArrayLength() - 1];
        if (final.GetProperty("evaluation_revision").GetString() != manifest.EvaluationRevision
            || final.GetProperty("fold").GetDouble() != rows.GetArrayLength() - 1
            || final.GetProperty("fold_accuracy").GetDouble() != report.GetProperty("fold_accuracy").GetDouble())
            throw new InvalidDataException("Final-outer accuracy evidence is inconsistent.");
        return final.GetProperty("fold_accuracy").GetDouble();
    }
}
