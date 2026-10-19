using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Result containing evaluated daily return ratios and reference date for the heatmap.
/// </summary>
public sealed class HeatmapReturnResult
{
    public IReadOnlyList<UniformHeatmapItem> Items { get; }
    public DateOnly? TargetDate { get; }
    public bool IsLiveMarketData { get; }
    public string ProviderId { get; }
    public string StatusMessage { get; }

    public static readonly HeatmapReturnResult Empty = new(
        Array.Empty<UniformHeatmapItem>(),
        targetDate: null,
        isLiveMarketData: false,
        providerId: "Empty",
        statusMessage: "No data"
    );

    public HeatmapReturnResult(
        IReadOnlyList<UniformHeatmapItem>? items,
        DateOnly? targetDate,
        bool isLiveMarketData,
        string? providerId,
        string? statusMessage)
    {
        Items = items ?? Array.Empty<UniformHeatmapItem>();
        TargetDate = targetDate;
        IsLiveMarketData = isLiveMarketData;
        ProviderId = providerId ?? string.Empty;
        StatusMessage = statusMessage ?? string.Empty;
    }
}
