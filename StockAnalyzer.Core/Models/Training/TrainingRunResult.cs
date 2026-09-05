using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Outcome of a completed (or failed) training run. Returned by the orchestrator and recorded
/// by the experiment log alongside the originating <see cref="TrainingJobConfig"/>.
/// </summary>
public sealed record TrainingRunResult
{
    /// <summary>
    /// Run identifier, <c>yyyyMMdd-HHmmssfff-{8-hex-char GUID suffix}</c>. Generated exactly
    /// once by <see cref="Services.TrainingOrchestrator"/> and passed to <c>run_training.py</c>
    /// via <see cref="TrainingJobConfig.RunId"/> so both sides share the same identifier; also
    /// the experiment-log folder name and the ONNX artifact filename stem.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary><see langword="true"/> when the trainer exited 0 and produced an ONNX artifact.</summary>
    public required bool Success { get; init; }

    /// <summary>Process exit code of the orchestrator script (<c>0</c> on success).</summary>
    public int ExitCode { get; init; }

    /// <summary>Absolute path of the produced <c>.onnx</c> file under <c>&lt;DataRoot&gt;/TrainingArtifacts/</c>, or <see langword="null"/> on failure.</summary>
    public string? OnnxArtifactPath { get; init; }

    /// <summary>Absolute path of the produced <c>.onnx.metrics.json</c> sidecar, or <see langword="null"/> when absent.</summary>
    public string? MetricsArtifactPath { get; init; }

    /// <summary>Final aggregated metrics reported by the run (accuracy, majority baseline, macro-F1, log-loss, …).</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; }
        = new Dictionary<string, double>();

    /// <summary>Failure reason or a short completion summary; may be <see langword="null"/>.</summary>
    public string? Message { get; init; }

    /// <summary>UTC instant the run started.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>UTC instant the run finished.</summary>
    public DateTimeOffset CompletedUtc { get; init; }
}
