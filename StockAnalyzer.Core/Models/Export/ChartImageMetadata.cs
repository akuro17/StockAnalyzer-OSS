using System;

namespace StockAnalyzer.Core.Models.Export;

/// <summary>
/// Encapsulates metadata associated with an exported chart image.
/// </summary>
public record ChartImageMetadata
{
    /// <summary>
    /// Stock symbol or ticker (e.g. "7203", "AAPL").
    /// </summary>
    public string Symbol { get; init; } = string.Empty;

    /// <summary>
    /// Company or asset name (e.g. "Toyota Motor Corp", "Apple Inc.").
    /// </summary>
    public string CompanyName { get; init; } = string.Empty;

    /// <summary>
    /// Timeframe description (e.g. "Daily", "Weekly", "Monthly").
    /// </summary>
    public string Timeframe { get; init; } = string.Empty;

    /// <summary>
    /// Start date of the visible chart slice.
    /// </summary>
    public DateTime? StartDate { get; init; }

    /// <summary>
    /// End date of the visible chart slice.
    /// </summary>
    public DateTime? EndDate { get; init; }

    /// <summary>
    /// Comma-separated summary of active indicators (e.g. "SMA(20), RSI(14), MACD(12,26,9)").
    /// </summary>
    public string IndicatorsSummary { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the export was generated (UTC or Local).
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Software / Application name.
    /// </summary>
    public string ApplicationName { get; init; } = "StockAnalyzer";

    /// <summary>
    /// Application version.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Optional serialized JSON string containing detailed indicator parameters and settings.
    /// </summary>
    public string? DetailedJson { get; init; }
}
