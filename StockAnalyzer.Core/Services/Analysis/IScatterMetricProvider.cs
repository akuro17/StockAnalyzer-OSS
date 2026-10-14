using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Immutable record carrying computed metric values for a single ticker in scatter/heatmap evaluation.
/// </summary>
public readonly record struct ScatterMetricItem(
    string Ticker,
    double XValue,
    double YValue,
    double ReturnPercentage
);

/// <summary>
/// Evaluation result produced by an <see cref="IScatterMetricProvider"/>.
/// Contains evaluated items, heatmap return percentages, and data source status metadata.
/// </summary>
public sealed class ScatterMetricResult
{
    public static readonly ScatterMetricResult Empty = new(
        Array.Empty<ScatterMetricItem>(),
        isLiveMarketData: false,
        providerId: "Empty",
        statusMessage: "No data"
    );

    public IReadOnlyList<ScatterMetricItem> Items { get; }
    public bool IsLiveMarketData { get; }
    public string ProviderId { get; }
    public string StatusMessage { get; }
    public DateTimeOffset TimestampUtc { get; }

    public ScatterMetricResult(
        IReadOnlyList<ScatterMetricItem> items,
        bool isLiveMarketData,
        string providerId,
        string statusMessage,
        DateTimeOffset? timestampUtc = null)
    {
        Items = items ?? Array.Empty<ScatterMetricItem>();
        IsLiveMarketData = isLiveMarketData;
        ProviderId = providerId ?? string.Empty;
        StatusMessage = statusMessage ?? string.Empty;
        TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Abstraction for asynchronous batch metric calculation for scatter plots and heatmaps.
/// Decouples data retrieval (DuckDB, synthetic demo, cached) from UI view models and statistical analysis engines.
/// </summary>
public interface IScatterMetricProvider
{
    string ProviderId { get; }
    bool IsLiveMarketData { get; }

    Task<ScatterMetricResult> ComputeMetricsAsync(
        IReadOnlyList<string> tickers,
        string xMetric,
        string yMetric,
        CancellationToken cancellationToken = default);
}
