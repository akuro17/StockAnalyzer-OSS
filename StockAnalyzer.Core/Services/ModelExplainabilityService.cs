using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public enum ExplanationAvailability
{
    /// <summary>No explanation was generated for the generation (the model itself is unaffected).</summary>
    NotGenerated,
    /// <summary>An explanation file exists but does not satisfy its contract for this generation.</summary>
    Invalid,
    /// <summary>The run that trained the generation recorded that its optional explanation raised, so no file exists.</summary>
    Failed,
    Available,
}

public sealed record ModelExplanationLoad(ExplanationAvailability Availability, ModelExplanation? Explanation);

public enum CalibrationAvailability
{
    /// <summary>The model's probabilities are the raw model output; no calibration was requested.</summary>
    None,
    /// <summary>Calibration was requested at training time but not produced (for example too few samples).</summary>
    Unavailable,
    /// <summary>The generation declares a verified calibration that every prediction applies.</summary>
    Active,
    /// <summary>The generation carries a calibration sidecar that is not applied (the temperature is at the boundary).</summary>
    Disabled,
    /// <summary>The calibration state could not be read (reported by the caller when the load throws; the service itself never returns it).</summary>
    LoadFailed,
}

public sealed record ModelCalibrationLoad(CalibrationAvailability Availability, ModelCalibration? Calibration = null,
    string? Reason = null);

public interface IModelExplainabilityService
{
    /// <summary>Loads the optional offline explanation of one generation. Never computes anything.</summary>
    Task<ModelExplanationLoad> LoadExplanationAsync(string modelId, CancellationToken ct = default);

    /// <summary>The generation's calibration state; <see cref="CalibrationAvailability.None"/> when never requested.</summary>
    Task<ModelCalibrationLoad> LoadCalibrationAsync(string modelId, CancellationToken ct = default);
}

/// <summary>
/// Reads the immutable, generation-bound explanation and calibration sidecars outside rendering and prediction. A missing
/// explanation is a state, not an error; a declared calibration is part of the model contract and is verified by the registry.
/// </summary>
/// <remarks>
/// Loaded explanations and calibrations are kept in least-recently-used caches bounded by
/// <see cref="ModelExplanationLimits.MaxCachedGenerations"/>. Concurrent requests for the same generation share one load, which
/// runs to completion even when a caller stops waiting (cancellation releases that caller only).
/// </remarks>
public sealed class ModelExplainabilityService : IModelExplainabilityService
{
    private readonly IModelGenerationRegistry _registry;
    private readonly BoundedLruCache<string, ModelExplanation> _explanations;
    private readonly BoundedLruCache<string, ModelCalibration> _calibrations;
    private readonly ConcurrentDictionary<string, Lazy<Task<ModelExplanationLoad>>> _explanationLoads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<ModelCalibrationLoad>>> _calibrationLoads = new(StringComparer.Ordinal);

    public ModelExplainabilityService(IModelGenerationRegistry registry)
        : this(registry, ModelExplanationLimits.MaxCachedGenerations)
    {
    }

    /// <summary>A smaller capacity than the contract bound, so tests can exercise eviction without registering many generations.</summary>
    internal ModelExplainabilityService(IModelGenerationRegistry registry, int cacheCapacity)
    {
        _registry = registry;
        _explanations = new BoundedLruCache<string, ModelExplanation>(cacheCapacity, StringComparer.Ordinal);
        _calibrations = new BoundedLruCache<string, ModelCalibration>(cacheCapacity, StringComparer.Ordinal);
    }

    public Task<ModelExplanationLoad> LoadExplanationAsync(string modelId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ModelExplanationLoad>(ct);
        if (_explanations.TryGet(modelId, out var cached))
            return Task.FromResult(new ModelExplanationLoad(ExplanationAvailability.Available, cached));
        return SharedLoad(_explanationLoads, modelId, ReadExplanation).WaitAsync(ct);
    }

    public Task<ModelCalibrationLoad> LoadCalibrationAsync(string modelId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ModelCalibrationLoad>(ct);
        if (_calibrations.TryGet(modelId, out var cached))
            return Task.FromResult(Loaded(cached));
        return SharedLoad(_calibrationLoads, modelId, ReadCalibration).WaitAsync(ct);
    }

    /// <summary>
    /// One in-flight load per generation: callers that arrive while it runs await the same task. The entry is removed when the load
    /// ends (after a successful result is cached), so a failed or empty result is retried by the next request as before.
    /// </summary>
    private static Task<TLoad> SharedLoad<TLoad>(ConcurrentDictionary<string, Lazy<Task<TLoad>>> inFlight, string modelId,
        Func<string, TLoad> read)
    {
        var load = inFlight.GetOrAdd(modelId, id => new Lazy<Task<TLoad>>(() => Task.Run(() =>
        {
            try { return read(id); }
            finally { inFlight.TryRemove(id, out _); }
        })));
        return load.Value;
    }

    private ModelExplanationLoad ReadExplanation(string modelId)
    {
        using var lease = _registry.Acquire(modelId);
        var manifest = _registry.GetManifest(modelId);
        if (lease is null || manifest is null) return new ModelExplanationLoad(ExplanationAvailability.NotGenerated, null);
        if (!File.Exists(ModelExplanation.SidecarPath(lease.ModelPath)))
            return new ModelExplanationLoad(GenerationFailed(lease.ModelPath)
                ? ExplanationAvailability.Failed : ExplanationAvailability.NotGenerated, null);
        try
        {
            using var session = new InferenceSession(lease.ModelPath);
            var explanation = ModelExplanation.Load(lease.ModelPath, session.ModelMetadata.CustomMetadataMap, manifest.ModelHash);
            if (explanation is null) return new ModelExplanationLoad(ExplanationAvailability.NotGenerated, null);
            _explanations.TryAdd(modelId, explanation);
            return new ModelExplanationLoad(ExplanationAvailability.Available, explanation);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or OnnxRuntimeException)
        {
            return new ModelExplanationLoad(ExplanationAvailability.Invalid, null);
        }
    }

    /// <summary>True when the generation's run metrics record that the optional explanation raised during training.</summary>
    private static bool GenerationFailed(string modelPath)
    {
        try
        {
            using var metrics = JsonDocument.Parse(File.ReadAllText(modelPath + ".metrics.json"));
            return metrics.RootElement.TryGetProperty(ModelExplanationContract.MetricsKey, out var block)
                && block.ValueKind == JsonValueKind.Object && block.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String && status.GetString() == ModelExplanationContract.GenerationFailedStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private ModelCalibrationLoad ReadCalibration(string modelId)
    {
        using var lease = _registry.Acquire(modelId);
        var manifest = _registry.GetManifest(modelId);
        if (lease is null || manifest is null) return new ModelCalibrationLoad(CalibrationAvailability.None);
        using var metrics = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + ".metrics.json"));
        if (!manifest.RequiredSidecars.Any(s => s.Kind == ModelCalibrationContract.SidecarKind))
        {
            // Requested at training time but not produced: the run records why, so the panel can say so.
            return metrics.RootElement.TryGetProperty("calibration", out var requested)
                && requested.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                && status.GetString() is { Length: > 0 } reason && reason != ModelCalibrationContract.AvailableStatus
                ? new ModelCalibrationLoad(CalibrationAvailability.Unavailable, null, reason)
                : new ModelCalibrationLoad(CalibrationAvailability.None);
        }
        using var session = new InferenceSession(lease.ModelPath);
        var calibration = ModelCalibration.Load(lease.ModelPath, session.ModelMetadata.CustomMetadataMap, manifest.ModelHash,
            metrics.RootElement.GetProperty("data_revision").GetString() ?? "");
        _calibrations.TryAdd(modelId, calibration);
        return Loaded(calibration);
    }

    private static ModelCalibrationLoad Loaded(ModelCalibration calibration) => calibration.IsEnabled
        ? new ModelCalibrationLoad(CalibrationAvailability.Active, calibration)
        : new ModelCalibrationLoad(CalibrationAvailability.Disabled, calibration, calibration.Status);
}
