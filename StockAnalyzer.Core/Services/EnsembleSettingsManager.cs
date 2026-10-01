using System;
using System.Collections.Generic;
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
        ModelGenerationManifest? first = null;
        HashSet<string>? labels = null;
        string? targetDefinition = null;
        foreach (var member in spec.Members)
        {
            var manifest = registry.GetManifest(member.ModelId)
                ?? throw EnsembleValidationErrors.Create($"Generation '{member.ModelId}' is unavailable.",
                    EnsembleValidationFailure.MissingGeneration);
            using var lease = registry.Acquire(member.ModelId)
                ?? throw EnsembleValidationErrors.Create($"Generation '{member.ModelId}' is unavailable.",
                    EnsembleValidationFailure.MissingGeneration);
            using var session = new InferenceSession(lease.ModelPath);
            var metadata = session.ModelMetadata.CustomMetadataMap;
            string definition = (metadata.GetValueOrDefault("neutral_threshold") ?? "") + "\n"
                + (metadata.GetValueOrDefault("price_adjustment") ?? "");
            if (manifest.TargetType != PredictionModelMetadata.TargetTypeClassification)
                throw EnsembleValidationErrors.Create("Only classification generations can be ensembled.",
                    EnsembleValidationFailure.IncompatibleContract);
            var row = manifest.ClassOrder.Split(',');
            var set = new HashSet<string>(row, StringComparer.Ordinal);
            if (!PredictionModelMetadata.HasValidClassOrder(manifest.ClassOrder))
                throw EnsembleValidationErrors.Create("Generation class labels are invalid.",
                    EnsembleValidationFailure.IncompatibleContract);
            if (first is null) { first = manifest; labels = set; targetDefinition = definition; }
            else if (manifest.Timeframe != first.Timeframe || manifest.Horizon != first.Horizon
                || manifest.TargetType != first.TargetType || !labels!.SetEquals(set)
                || definition != targetDefinition)
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
