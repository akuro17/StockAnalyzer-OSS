using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Service responsible for calculating magnet snap points on the chart.
/// </summary>
public interface IMagnetSnapService
{
    /// <summary>
    /// Calculates magnet snap to nearest OHLC price point.
    /// </summary>
    /// <param name="mouseScreenPoint">The mouse position in chart-relative coordinates.</param>
    /// <param name="candles">The visible candle data.</param>
    /// <param name="coordinateTransform">The coordinate transform for conversions.</param>
    /// <returns>Tuple of (snapped chart point, snapped screen point, whether snap occurred).</returns>
    (ChartPoint SnappedChartPoint, global::Avalonia.Point SnappedScreenPoint, bool IsSnapped) GetMagnetSnap(
        global::Avalonia.Point mouseScreenPoint,
        IReadOnlyList<CoreCandleData> candles,
        ICoordinateTransform coordinateTransform);
}
