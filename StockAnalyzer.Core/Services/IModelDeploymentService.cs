using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Promotes a trained ONNX artifact from <c>&lt;DataRoot&gt;/TrainingArtifacts/TW/</c> to the canonical
/// <c>&lt;DataRoot&gt;/Models/</c> store so it becomes loadable by <see cref="IPredictionService"/>.
/// </summary>
public interface IModelDeploymentService
{
    /// <summary>
    /// Atomically copies <paramref name="onnxSourcePath"/> (and, when given, its
    /// <paramref name="metricsSourcePath"/> sidecar) into <c>&lt;DataRoot&gt;/Models/</c>,
    /// keeping the source filename unchanged (NAME-04: no rename on promotion). Returns the
    /// final <c>.onnx</c> path under <c>Models/</c>.
    /// </summary>
    Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default);

    /// <summary>Registers a verified immutable generation and activates it if it qualifies.</summary>
    Task<ModelGenerationManifest> RegisterGenerationAsync(
        string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default);

    /// <summary>
    /// Verifies a bagging / stacking <c>.ensemble.json</c> and registers every member as its own immutable generation
    /// <b>without activating any of them</b>, then returns the equal-weight (bagging) or fitted (stacking) mixture as an
    /// <see cref="EnsembleSpec"/>. The caller decides whether to make it the active ensemble.
    /// </summary>
    Task<EnsembleRegistration> RegisterEnsembleAsync(string ensembleManifestPath, CancellationToken ct = default) =>
        throw new NotSupportedException("This deployment service cannot register an ensemble.");
}

/// <summary>The generations registered for one ensemble run and the mixture that combines them.</summary>
public sealed record EnsembleRegistration(
    TrainingEnsembleMethod Method, EnsembleSpec Spec, IReadOnlyList<ModelGenerationManifest> Generations);
