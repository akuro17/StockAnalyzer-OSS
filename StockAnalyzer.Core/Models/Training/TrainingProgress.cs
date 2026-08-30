using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// One progress update streamed from a running training job. The orchestrator parses the
/// trainer's stdout protocol lines — <c>STAGE:&lt;name&gt;</c>, <c>PROGRESS:&lt;0-100&gt;</c>,
/// <c>METRIC:&lt;json&gt;</c> — and reports the latest known state as this record.
/// </summary>
public sealed record TrainingProgress
{
    /// <summary>Most recent pipeline stage (for example <c>load</c>, <c>build_dataset</c>, <c>fold 2/5</c>, <c>export</c>, <c>verify</c>).</summary>
    public string? Stage { get; init; }

    /// <summary>Overall completion percentage, 0–100.</summary>
    public int Percent { get; init; }

    /// <summary>
    /// Latest <c>METRIC:</c> payload (for example a fold's accuracy / baseline / macro-F1),
    /// or <see langword="null"/> when the last line carried no metric.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Metric { get; init; }

    /// <summary>Optional human-readable line (raw log text) accompanying this update.</summary>
    public string? Message { get; init; }
}
