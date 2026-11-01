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
);
