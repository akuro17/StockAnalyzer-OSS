using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The inputs of one drawing-value calculation: the control points (count equals the descriptor's point count,
/// guaranteed by the caller), the evaluation time, the optional current price, the colour of the tool and its options.
/// </summary>
public readonly record struct DrawingValueRequest(
    IReadOnlyList<DrawingAnchor> Points,
    DateTime Timestamp,
    decimal? CurrentPrice,
    IndicatorColor Color,
    DrawingValueOptions Options);
