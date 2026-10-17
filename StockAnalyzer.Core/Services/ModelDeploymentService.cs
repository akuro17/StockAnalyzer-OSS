using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Default <see cref="IModelDeploymentService"/>. Resolves the target path via
/// <see cref="PathDiscovery.ResolvePredictionModelPath"/> -- the same resolver
/// <see cref="IPredictionService"/> uses to load a model -- and writes each file through a
/// temp-then-<see cref="File.Move(string, string, bool)"/> sequence so a reader never observes a
/// partially-written artifact (NAME-03/NAME-04: the artifacts filename is kept, and re-deploying
/// the same filename overwrites cleanly rather than corrupting it mid-write).
/// </summary>
public sealed class ModelDeploymentService : IModelDeploymentService
{
    private readonly ILogger<ModelDeploymentService> _logger;
    private readonly IModelGenerationRegistry? _registry;
    private readonly IStockAnalyzerSettings? _settings;

    /// <param name="settings">Gives the chart's confidence threshold for <see cref="ModelAutoActivationGate"/>; without it only the log-loss condition of the gate applies.</param>
    public ModelDeploymentService(ILogger<ModelDeploymentService>? logger = null,
        IModelGenerationRegistry? registry = null, IStockAnalyzerSettings? settings = null)
    {
        _logger = logger ?? NullLogger<ModelDeploymentService>.Instance;
        _registry = registry;
        _settings = settings;
    }

    public async Task<ModelGenerationManifest> RegisterGenerationAsync(
        string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
    {
        if (_registry is null) throw new InvalidOperationException("Model generation registry is unavailable.");
        var manifest = await _registry.RegisterAsync(onnxSourcePath, metricsSourcePath, ct).ConfigureAwait(false);
        // The registry read the same metrics file (default: beside the model) when it staged the generation.
        var decision = ModelAutoActivationGate.Evaluate(
            metricsSourcePath ?? onnxSourcePath + ".metrics.json", manifest.ClassOrder, _settings?.PredictionConfidenceThreshold);
        if (decision.Stopped)
        {
            _logger.LogInformation("Generation {ModelId} was registered without automatic activation (reach is zero: {ReachIsZero}, not better than a uniform guess: {NotBetter}).",
                manifest.ModelId, decision.ReachIsZero, decision.NotBetterThanUniform);
            return manifest;
        }
        try { await _registry.ActivateAsync(manifest.ModelId, ct: ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(ex, "Generation {ModelId} was registered without automatic activation.", manifest.ModelId);
        }
        return manifest;
    }

    public async Task<EnsembleRegistration> RegisterEnsembleAsync(string ensembleManifestPath, CancellationToken ct = default)
    {
        if (_registry is null) throw new InvalidOperationException("Model generation registry is unavailable.");
        var manifest = TrainingEnsembleManifest.Load(ensembleManifestPath);
        // Every member passes the registration checks in staging before the first one is registered, so a bad member leaves
        // the registry untouched (it has no delete operation to undo a partial registration). Registration is content-addressed
        // and idempotent, so what can still fail afterwards is an I/O fault.
        foreach (var member in manifest.Members)
            await _registry.VerifyCandidateAsync(member.OnnxPath, member.MetricsPath, ct).ConfigureAwait(false);
        var generations = new List<ModelGenerationManifest>();
        foreach (var member in manifest.Members)
            generations.Add(await _registry.RegisterAsync(member.OnnxPath, member.MetricsPath, ct).ConfigureAwait(false));
        var spec = new EnsembleSpec(Array.AsReadOnly(generations
            .Select((generation, index) => new EnsembleMember(generation.ModelId, manifest.Members[index].Weight)).ToArray()));
        // Distinct ids, weights and the shared target/timeframe/horizon/class-order contract are the same rules the ensemble
        // settings enforce, so a registration that cannot be used as an ensemble fails here rather than at prediction time.
        await Task.Run(() => EnsembleSettingsManager.ValidateGenerations(spec, _registry), ct).ConfigureAwait(false);
        _logger.LogInformation("Registered {Method} ensemble of {Count} generations (run {RunId}).",
            manifest.Method, generations.Count, manifest.RunId);
        return new EnsembleRegistration(manifest.Method, spec, Array.AsReadOnly(generations.ToArray()));
    }

    public async Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(onnxSourcePath))
        {
            throw new ArgumentException("onnxSourcePath must be provided.", nameof(onnxSourcePath));
        }
        if (!File.Exists(onnxSourcePath))
        {
            throw new FileNotFoundException("Trained ONNX artifact not found.", onnxSourcePath);
        }

        var fileName = Path.GetFileName(onnxSourcePath);
        var finalOnnxPath = PathDiscovery.ResolvePredictionModelPath(fileName);

        await AtomicCopyAsync(onnxSourcePath, finalOnnxPath, ct).ConfigureAwait(false);
        _logger.LogInformation("ModelDeploymentService: deployed {Source} -> {Destination}.", onnxSourcePath, finalOnnxPath);

        if (!string.IsNullOrWhiteSpace(metricsSourcePath) && File.Exists(metricsSourcePath))
        {
            var finalMetricsPath = finalOnnxPath + ".metrics.json";
            await AtomicCopyAsync(metricsSourcePath, finalMetricsPath, ct).ConfigureAwait(false);
            _logger.LogInformation("ModelDeploymentService: deployed metrics sidecar -> {Destination}.", finalMetricsPath);
        }

        return finalOnnxPath;
    }

    private static async Task AtomicCopyAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = File.Create(tempPath))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            // Reached only when the copy above threw or was cancelled before the Move; a
            // completed Move already removed the temp file, so this is then a no-op.
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { /* best-effort cleanup */ }
                catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
            }
        }
    }
}
