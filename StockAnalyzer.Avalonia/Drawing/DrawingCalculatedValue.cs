using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Represents a calculated analytical or geometric value produced by a chart drawing tool.
/// </summary>
public readonly record struct DrawingCalculatedValue(
    string Key,
    string Label,
    decimal? NumericValue,
    string FormattedText,
    IndicatorColor Color
);
