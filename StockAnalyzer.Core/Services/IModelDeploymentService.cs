using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Promotes a trained ONNX artifact from <c>training/artifacts/</c> to the canonical
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
}
