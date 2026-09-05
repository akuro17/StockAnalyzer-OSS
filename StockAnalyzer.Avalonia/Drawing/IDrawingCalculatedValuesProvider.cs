using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Interface for chart drawing objects that expose calculated geometric, financial,
/// or analytical values for inspection in the Data Window (Data tab), tooltips, or external consumers.
/// </summary>
public interface IDrawingCalculatedValuesProvider
{
    /// <summary>
    /// Computes and returns the list of calculated values at the given timestamp and price context.
    /// </summary>
    /// <param name="timestamp">The target time or cursor timestamp.</param>
    /// <param name="currentPrice">Optional current market price or hovered price.</param>
    /// <returns>Read-only list of calculated values.</returns>
    IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null);
}
