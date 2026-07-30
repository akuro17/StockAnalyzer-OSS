using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Represents the result of an indicator calculation.
/// Supports both single-series and multi-series indicators.
/// Can optionally provide technical signals for confluence orchestration.
/// </summary>
public interface IIndicatorResult : IReadOnlyList<decimal?>
{
    /// <summary>
    /// Indicates whether the calculation was successful.
    /// </summary>
    bool IsSuccessful { get; }

    /// <summary>
    /// Error message if the calculation failed, null otherwise.
    /// </summary>
    string? ErrorMessage { get; }

    /// <summary>
    /// The primary/main series of values (for backward compatibility with single-series indicators).
    /// </summary>
    IReadOnlyList<decimal?> MainValues { get; }

    /// <summary>
    /// Checks if a named series exists in the result.
    /// </summary>
    bool HasSeries(string name);

    /// <summary>
    /// Gets a specific named series. Returns empty list if not found.
    /// </summary>
    IReadOnlyList<decimal?> GetSeries(string name);

    /// <summary>
    /// Gets all available series names.
    /// </summary>
    IEnumerable<string> SeriesNames { get; }

    /// <summary>
    /// Gets all available series names as an indexed list to allow allocation-free iteration.
    /// </summary>
    IReadOnlyList<string> SeriesNamesList { get; }

    /// <summary>
    /// Pre-allocated display labels for each series (e.g., "Main" -> "SMA(20)").
    /// Generated via ApplyMetadata to maintain ZeroAllocation in UI.
    /// </summary>
    IReadOnlyDictionary<string, string> SeriesLabels { get; }

    /// <summary>
    /// Custom data payload for specialized indicators (e.g., Volume Buckets, Heatmaps).
    /// </summary>
    object? CustomData { get; }

    /// <summary>
    /// Optional signal provider associated with this result.
    /// Used for confluence orchestration.
    /// </summary>
    IConfluenceSignalProvider? SignalProvider { get; }
}
