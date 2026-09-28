using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;

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

    public ModelDeploymentService(ILogger<ModelDeploymentService>? logger = null)
    {
        _logger = logger ?? NullLogger<ModelDeploymentService>.Instance;
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
