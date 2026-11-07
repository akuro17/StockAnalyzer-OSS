using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Immutable catalog entry representing a registered model generation (supervised or reinforcement learning)
/// for MCP listing and inspection. Contains zero file paths.
/// </summary>
public sealed record ModelCatalogEntry(
    string ModelId,
    string Kind,
    IReadOnlyList<string> Symbols,
    string Timeframe,
    DateTimeOffset CreatedAt,
    bool IsActive,
    int WindowSize,
    int HorizonBars,
    string TargetType,
    int ClassCount,
    string? Architecture,
    double? Score,
    string? EvaluationRevision,
    decimal? ValidationReturn,
    decimal? OosReturn
)
{
    /// <summary>How the supervised model was trained (<c>fresh</c> or <c>fine_tune</c>); null when unknown (a generation without lineage) and for reinforcement learning.</summary>
    public string? TrainingMode { get; init; }

    /// <summary>Where <see cref="ModelCatalogEntry.CreatedAt"/> comes from: one of <see cref="CreatedAtSources"/>.</summary>
    public string CreatedAtSource { get; init; } = CreatedAtSources.Filesystem;
}

/// <summary>The origins of <see cref="ModelCatalogEntry.CreatedAt"/>. New values may be added; existing ones keep their meaning.</summary>
public static class CreatedAtSources
{
    /// <summary>The start time encoded in the training run identifier of the generation's lineage; unchanged by copying the folder.</summary>
    public const string RunId = "runId";

    /// <summary>The older of the creation and last-write time of the manifest file (or of the folder); a guess for generations without a run identifier.</summary>
    public const string Filesystem = "filesystem";

    /// <summary>A time the generation recorded about itself (reinforcement learning).</summary>
    public const string Recorded = "recorded";
}
