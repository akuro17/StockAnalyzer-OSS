namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Immutable item representing a single tile in the Uniform Grid Heatmap.
/// </summary>
/// <param name="Ticker">Ticker symbol.</param>
/// <param name="ReturnPercentage">Decimal return ratio (e.g. 0.05 for +5.0%) for color interpolation and percentage formatting.</param>
/// <param name="Subtitle">Optional descriptive subtitle or company name.</param>
public readonly record struct UniformHeatmapItem(
    string Ticker,
    double ReturnPercentage,
    string? Subtitle = null
);
