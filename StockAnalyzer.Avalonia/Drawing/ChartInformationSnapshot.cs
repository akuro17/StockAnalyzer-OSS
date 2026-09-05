using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Immutable snapshot of analytical data inspected at a specific chart coordinate/timestamp for the Information tool.
/// Conforms to DataWindow presentation values, strictly excluding the ticker symbol.
/// </summary>
public sealed record ChartInformationSnapshot(
    DateTime Timestamp,
    CandleInformation? Candle,
    IReadOnlyList<IndicatorInformationItem> Indicators,
    IReadOnlyList<DrawingInformationItem> Drawings
);

/// <summary>
/// Price and volume information for the inspected candlestick.
/// Symbol is deliberately omitted per specification.
/// </summary>
public sealed record CandleInformation(
    string DateText,
    string OpenText,
    string HighText,
    string LowText,
    string CloseText,
    string VolumeText,
    string YesterdayChangeText,
    string YesterdayChangeRatioText,
    IndicatorColor YesterdayChangeColor
);

/// <summary>
/// Metric value for an active technical indicator at the inspected bar.
/// </summary>
public sealed record IndicatorInformationItem(
    string Name,
    string FormattedValue,
    IndicatorColor Color
);

/// <summary>
/// Metric value for a visible drawing tool at the inspected bar.
/// </summary>
public sealed record DrawingInformationItem(
    string DisplayName,
    string MetricLabel,
    string FormattedValue,
    IndicatorColor Color,
    string? FullLabel = null
)
{
    public string FullLabel { get; init; } = FullLabel ?? $"{DisplayName} - {MetricLabel}";
}
