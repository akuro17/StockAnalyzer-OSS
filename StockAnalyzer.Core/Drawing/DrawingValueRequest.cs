using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The inputs of one drawing-value calculation: the control points (count equals the descriptor's point count,
/// guaranteed by the caller), the evaluation time, the optional current price, the colour of the tool and its options.
/// </summary>
/// <param name="Candles">
/// The bars, oldest first, for the tools that are calculated from bars (RegressionTrend, AnchoredVWAP, ClassicPivotPoints, DatePriceRange); null for every other tool.
/// A candle tool given null returns no values, except DatePriceRange, which then still gives the figures that need no bars.
/// </param>
public readonly record struct DrawingValueRequest(
    IReadOnlyList<DrawingAnchor> Points,
    DateTime Timestamp,
    decimal? CurrentPrice,
    IndicatorColor Color,
    DrawingValueOptions Options,
    IReadOnlyList<CoreCandleData>? Candles = null);
