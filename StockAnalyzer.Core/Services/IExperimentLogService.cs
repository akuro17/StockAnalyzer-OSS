using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Records a minimal, self-contained log of one training run under
/// <c>&lt;DataRoot&gt;/Experiments/&lt;run-id&gt;/</c> (S-1: lightweight experiment tracking).
/// </summary>
public interface IExperimentLogService
{
    /// <summary>
    /// Writes <c>config.json</c> (the originating <see cref="TrainingJobConfig"/>) and
    /// <c>metrics.json</c> (the full <see cref="TrainingRunResult"/>, including artifact paths)
    /// into <c>&lt;DataRoot&gt;/Experiments/&lt;result.RunId&gt;/</c>.
    /// </summary>
    Task RecordAsync(TrainingJobConfig config, TrainingRunResult result, CancellationToken ct = default);
}
