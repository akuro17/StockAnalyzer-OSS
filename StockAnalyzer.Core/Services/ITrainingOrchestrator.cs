using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Launches a GUI-triggered model-training run: serializes a <see cref="TrainingJobConfig"/> to
/// the wire JSON, starts <c>StockAnalyzer.Python/training/run_training.py --config</c>, and
/// streams its <c>STAGE:</c> / <c>PROGRESS:</c> / <c>METRIC:</c> / <c>ARTIFACT:</c> stdout
/// protocol as <see cref="TrainingProgress"/> updates.
/// </summary>
public interface ITrainingOrchestrator
{
    /// <summary>
    /// Runs one training job to completion (or failure). A successful <see cref="TrainingRunResult"/>
    /// is returned whether the trainer itself succeeded or failed with a non-zero exit code --
    /// only cancellation is exceptional: cancelling <paramref name="ct"/> kills the trainer
    /// process (and its child tree) and the awaited task ends with
    /// <see cref="OperationCanceledException"/> instead of returning a result.
    /// </summary>
    Task<TrainingRunResult> StartTrainingAsync(
        TrainingJobConfig config,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken ct = default);
}
